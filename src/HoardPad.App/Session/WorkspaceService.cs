// WorkspaceService.cs — the M6.3 integration coordinator (FR-19/FR-20, carry-forwards A + C).
//
// Ties the session/recovery engine to the live workspace:
//   - RestoreAsync : startup — drive SessionService.LoadAndReconcileAsync, materialize each restored tab
//                    into a DocumentTab (carry-forward A), surface the FR-19 restore-conflict dialog for a
//                    changed-on-disk file, treat a deleted-on-disk file as unsaved-to-a-missing-path, and
//                    bind the AI panel per MA5 (carry-forward C — re-prompt before arming on a stale root).
//   - OpenFolderAsync : the live File ▸ Open Folder path — set the AI sandbox root + bind/arm the panel.
//   - SnapshotActiveAsync / SnapshotAllDirtyAsync / PersistAsync : auto-save (FR-20) + the session index
//                    (the rebuildable hint). SnapshotAllDirtyAsync is the F-FS1c periodic-timer target —
//                    it reuses the exact SessionService.SnapshotAsync write path SnapshotActiveAsync and
//                    crash-recovery already rely on, extended to every dirty tab (not just the active one).
//   - StartAutoSaveTimer / StopAutoSaveTimer : F-FS1c — the periodic DispatcherTimer wrapping
//                    SnapshotAllDirtyAsync, wired from App.axaml.cs's startup/shutdown lifecycle.
//   - ClearRecoveryOnCleanSaveAsync : FR-21 — a clean save+close clears the buffer's recovery data.
//
// The conflict-dialog seam is injected (IRestoreConflictPrompt) so the restore is unit-testable headless
// with a scripted answer; production shows the real ShowDialog(owner) modal. Materializing tabs touches
// Avalonia VMs (EditorViewModel / ChecklistViewModel), so RestoreAsync runs on the UI thread but its I/O
// (probe / read / snapshot) is awaited off-UI inside the services it calls.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HoardPad.App.Editor;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Recovery;

namespace HoardPad.App.Session;

/// <summary>The restore-conflict prompt seam (FR-19) — production shows the modal; tests script the answer.</summary>
public interface IRestoreConflictPrompt
{
    /// <summary>Ask the user keep-recovered / keep-disk / show-diff for a changed-on-disk file (design §9.6).</summary>
    Task<Views.RestoreConflictChoice> AskAsync(string fileName, CancellationToken ct);
}

/// <summary>
/// The M6.3 workspace coordinator (FR-19/FR-20, carry-forwards A + C). Owns the open-tab set on the shell
/// VM, the session/recovery lifecycle, and the AI-session binding. Constructed per app run by the
/// composition root with the live shell VM + AI panel.
/// </summary>
internal sealed class WorkspaceService : IDisposable
{
    private readonly MainWindowViewModel _shell;
    private readonly AiPanelViewModel _aiPanel;
    private readonly SessionService _session;
    private readonly SessionReconciler _reconciler;
    private readonly IAiSessionBinder _aiBinder;
    private readonly IRestoreConflictPrompt _conflictPrompt;
    private readonly EditorViewModelFactory _editorFactory;
    private readonly ChecklistViewModelFactory _checklistFactory;
    private readonly SettingsTabFactory _settingsTabFactory;
    private readonly IOpenFolderPrompt _rootPrompt;
    // FS-1: resolves the "My Documents" tier-(iii) fallback, invoked lazily (only when tier iii actually
    // fires). Production wires MyDocumentsResolver.ResolveLive; tests inject a hermetic stand-in so a
    // startup restore never touches the real environment/filesystem unless the test genuinely means to.
    private readonly Func<string> _myDocumentsResolver;
    // F-FS1a-anc: the host realpath resolver, used ONLY for the FindTabByPath/CanonicalizePath dedupe
    // compare — resolving a symlinked ANCESTOR directory (not just an immediate symlinked file) to its
    // real target so a file reached through either path dedupes to the same tab. Not the sandbox gate;
    // AiSessionBinder owns that leg with its own IPathResolver reference.
    private readonly IPathResolver _pathResolver;

    // The current open-folder root: raw (as the user chose it / as FS-1 derived it) and canonical
    // (realpath-normalised — what actually gates the sandbox). FS-1 (Decision A, FINAL): recomputed every
    // launch, NEVER persisted separately — see RestoreAsync's 3-tier derivation and PersistAsync's note.
    private string? _openFolder;         // raw path (or null).
    private string? _canonicalOpenFolder; // canonical form (or null).
    private readonly string _conversationId = Guid.NewGuid().ToString("N");
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// F-FS1c: the periodic auto-save interval used when <see cref="StartAutoSaveTimer"/> is called with no
    /// explicit interval. Production (App.axaml.cs) always passes the user's FR-20
    /// <c>HoardPadSettings.AutoSaveSeconds</c> (Settings ▸ "Auto-save interval (s)", already clamped 1..30);
    /// this constant is only the no-argument fallback for callers (tests, or a future non-settings caller)
    /// that don't care about that setting.
    /// </summary>
    internal const int DefaultAutoSaveIntervalSeconds = 30;

    // F-FS1c: the periodic snapshot timer. Null until StartAutoSaveTimer runs; StopAutoSaveTimer tears it
    // down so no DispatcherTimer is ever leaked across a stop/restart or app shutdown.
    private DispatcherTimer? _autoSaveTimer;

    // F-FS1c fix 3: the re-entrancy guard for the periodic auto-save batch. A tick that fires while the
    // prior batch is still awaiting is a no-op (never overlapping write batches); fix 4 also drains through
    // this gate on shutdown so no in-flight batch can race the shutdown-time PersistAsync.
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);

    // QA5-B1 W1: the re-entrancy guard for OpenFolderTreeFileAsync — canonical paths CURRENTLY in flight
    // (past the FindTabByPath check, before the new tab lands in _shell.Tabs). Closes the race
    // FindTabByPath alone cannot: MainWindow.axaml.cs's OnFolderTreeNodeDoubleTapped invokes
    // OpenFolderTreeItemCommand.Execute directly (bypassing CanExecute, a pre-existing codebase
    // convention — mirrors Close/Save), so AsyncRelayCommand's no-concurrent-execution guard never
    // engages; a fast double double-click on a NOT-YET-OPEN file can start a second call while the first
    // is still awaiting its file read, and both would see an empty dedupe set. Only ever touched from the
    // UI thread (every await in that method is ConfigureAwait(true)), so no lock is needed.
    private readonly HashSet<string> _openingFolderTreePaths = new(StringComparer.Ordinal);

    public WorkspaceService(
        MainWindowViewModel shell,
        AiPanelViewModel aiPanel,
        SessionService session,
        SessionReconciler reconciler,
        IAiSessionBinder aiBinder,
        IRestoreConflictPrompt conflictPrompt,
        EditorViewModelFactory editorFactory,
        ChecklistViewModelFactory checklistFactory,
        SettingsTabFactory settingsTabFactory,
        IOpenFolderPrompt rootPrompt,
        Func<string> myDocumentsResolver,
        IPathResolver pathResolver)
    {
        _shell = shell;
        _aiPanel = aiPanel;
        _session = session;
        _reconciler = reconciler;
        _aiBinder = aiBinder;
        _conflictPrompt = conflictPrompt;
        _editorFactory = editorFactory;
        _checklistFactory = checklistFactory;
        _settingsTabFactory = settingsTabFactory;
        _rootPrompt = rootPrompt;
        _myDocumentsResolver = myDocumentsResolver ?? throw new ArgumentNullException(nameof(myDocumentsResolver));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    /// <summary>The restored open folder (raw, as the user chose it), or null. Exposed for tests/assertions.</summary>
    public string? OpenFolder => _openFolder;

    /// <summary>The canonical (realpath-normalised) open folder — the MA5 baseline persisted next shutdown.</summary>
    public string? CanonicalOpenFolder => _canonicalOpenFolder;

    /// <summary>Startup restore with no passed files (the common relaunch case). See the overload below.</summary>
    public Task<SessionReconcileResult> RestoreAsync(CancellationToken ct) =>
        RestoreAsync(Array.Empty<string>(), ct);

    /// <summary>
    /// Startup restore (FR-19): drive the Core §9.2 tab reconciliation, materialize the surviving tabs
    /// (carry-forward A), surface the restore-conflict dialog for a changed-on-disk file, open any FS-1
    /// tier-(i) <paramref name="passedFiles"/> (double-click / "Open With") as additional tabs, then
    /// recompute the single Folder + AI-write root for THIS launch (FS-1, Decision A, FINAL — no separately
    /// persisted open-folder) and bind the AI panel to it. Returns the reconcile result (for tests / status
    /// surfacing).
    /// </summary>
    /// <param name="passedFiles">
    /// Absolute file path(s) passed on the command line (double-click / "Open With"), in argv order. Empty
    /// for a plain relaunch.
    /// </param>
    public async Task<SessionReconcileResult> RestoreAsync(IReadOnlyList<string> passedFiles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(passedFiles);
        List<string> normalizedPassedFiles = NormalizePassedFiles(passedFiles);

        SessionReconcileResult result = await _session.LoadAndReconcileAsync(ct).ConfigureAwait(true);

        foreach (RestoredTab restored in result.Tabs)
        {
            ct.ThrowIfCancellationRequested();
            DocumentTab? tab = await MaterializeAsync(restored, ct).ConfigureAwait(true);
            if (tab is not null)
                _shell.Tabs.Add(tab);
        }

        // FS-1 tier (i): open the passed file(s) as ADDITIONAL tabs — the prior session's tabs restored
        // above stay exactly as they were. A passed file already among the restored tabs is DEDUPED (the
        // existing tab is reused/focused, never duplicated). The first passed file becomes the active tab
        // below, overriding the prior-active restore (tier (i) "first passed active").
        DocumentTab? firstPassedTab = null;
        foreach (string passedFile in normalizedPassedFiles)
        {
            ct.ThrowIfCancellationRequested();
            DocumentTab tab = FindTabByPath(passedFile) ?? await OpenPassedFileAsync(passedFile, ct).ConfigureAwait(true);
            firstPassedTab ??= tab;
        }

        // QA3-23: nothing survived reconciliation AND no passed file opened either (a genuine first
        // launch) — the shell's ctor-injected scratch EditorViewModel is already the live, visible
        // buffer, but it had no tab identity, so ActiveTab stayed null and anything that resolves off
        // ActiveTab (Find & Replace's "Current file" scope, per-tab undo, …) was inert until the user
        // clicked New/Open. Wrap that SAME editor instance as a real scratch tab — ActivateTab below
        // re-points Editor at it via a ReferenceEquals no-op, so this is not a rebind.
        if (_shell.Tabs.Count == 0 && _shell.Editor is { } startupEditor)
            _shell.Tabs.Add(DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.Scratch, startupEditor));

        // Focus: a passed file wins over the prior active tab (tier (i)); else the prior active tab, or
        // the first that survived.
        DocumentTab? active = firstPassedTab
            ?? _shell.Tabs.FirstOrDefault(t => t.TabId == result.ActiveTabId)
            ?? _shell.Tabs.FirstOrDefault();
        _shell.ActivateTab(active);

        // FS-1 (Decision A, FINAL): recompute the single Folder + AI-write root for THIS launch — tiers
        // (i) passed file, (ii) first restored tab with a saved path (walked in the CURRENT tab order,
        // i.e. _shell.Tabs as materialized/opened above — FS-2 owns guaranteeing that order equals save
        // order), (iii) My Documents. No separately-persisted open-folder (PersistAsync no longer writes
        // one) — an explicitly-opened folder with no open tabs is consequently not restored next launch.
        var tabFacts = _shell.Tabs.Select(t => new TabPathFact(t.FilePath)).ToArray();
        FolderRootResolution rootResolution =
            FolderRootResolver.Resolve(normalizedPassedFiles, tabFacts, _myDocumentsResolver);

        // AI write agency still does NOT arm at startup — it arms on the first write-mode AI action
        // (preserves the QA2-18 posture); read/chat is available immediately. The scope line shows the
        // derived root regardless of tier (AiSessionBinder.BindDerivedRootAsync).
        BindResult bindResult = await _aiBinder.BindDerivedRootAsync(
            _aiPanel, rootResolution.Root, _conversationId, _sessionId, ct).ConfigureAwait(true);
        _openFolder = bindResult.RawRoot;
        _canonicalOpenFolder = bindResult.CanonicalRoot;

        // QA3-2: populate the folder-tree pane for the resolved root — EXCEPT tier (iii)'s My-Documents
        // fallback, which stays collapsed until the user summons it via [Folder] (FS-1).
        if (!rootResolution.KeepFolderPaneCollapsed)
            await PopulateFolderTreeAsync(ct).ConfigureAwait(true);

        return result;
    }

    /// <summary>
    /// FS-1 tier (i): find an already-open tab at <paramref name="fullPath"/> (dedupe), or null. Compares
    /// CANONICAL forms (F-FS1a, extended by F-FS1a-anc to a symlinked ANCESTOR directory, not just an
    /// immediate symlinked file) so a symlink anywhere in the path to an already-open file resolves to the
    /// same tab instead of spawning a duplicate. A tab with a null/empty <see cref="DocumentTab.FilePath"/>
    /// (an unsaved scratch buffer) never matches. Internal (not private) solely so tests can drive it
    /// directly with a malformed <paramref name="fullPath"/> —
    /// <see cref="RestoreAsync(IReadOnlyList{string},CancellationToken)"/>'s own argument normalization runs
    /// BEFORE this method is reached, so exercising this method's "never throw" contract for malformed input
    /// needs a seam that bypasses that earlier normalization.
    /// </summary>
    internal DocumentTab? FindTabByPath(string fullPath)
    {
        string canonical = CanonicalizePath(fullPath);
        return _shell.Tabs.FirstOrDefault(t =>
            !string.IsNullOrEmpty(t.FilePath)
            && string.Equals(CanonicalizePath(t.FilePath), canonical, StringComparison.Ordinal));
    }

    /// <summary>
    /// F-FS1a-anc: canonicalize <paramref name="path"/> for the <see cref="FindTabByPath"/> dedupe compare via
    /// <see cref="IPathResolver.ResolveCanonical"/> (realpath) — this resolves EVERY symlink in the path,
    /// including a symlinked ANCESTOR DIRECTORY earlier in the path, not just an immediate symlinked file
    /// (F-FS1a's original, narrower scope). <paramref name="path"/> is passed as both the raw path and its
    /// own resolution root; that root is only consulted when the raw path is relative, and every real caller
    /// here (tab <c>FilePath</c>s, and <see cref="RestoreAsync(IReadOnlyList{string},CancellationToken)"/>'s
    /// already-<see cref="Path.GetFullPath(string)"/>-normalized passed files) is already absolute. Falls
    /// back to <see cref="CanonicalizeImmediateSymlinkOnly"/> — the original immediate-symlink-only
    /// resolution — when the host resolver throws for any reason (a not-yet-saved or since-deleted path, a
    /// malformed path, a degenerate/permission failure): canonicalization must NEVER throw, this method is
    /// total. Ordinal (case-sensitive) throughout, matching this method's existing compare posture on Linux.
    /// </summary>
    private string CanonicalizePath(string path)
    {
        try
        {
            return _pathResolver.ResolveCanonical(path, path).Value;
        }
        catch (ArgumentException)
        {
            return CanonicalizeImmediateSymlinkOnly(path);
        }
        catch (System.IO.IOException)
        {
            return CanonicalizeImmediateSymlinkOnly(path);
        }
        catch (UnauthorizedAccessException)
        {
            return CanonicalizeImmediateSymlinkOnly(path);
        }
        catch (NotSupportedException)
        {
            return CanonicalizeImmediateSymlinkOnly(path);
        }
    }

    /// <summary>
    /// F-FS1a's original canonicalization: resolves a symlink AT THE FINAL PATH COMPONENT (i.e.
    /// <paramref name="path"/> itself, if it is directly a symlink) to its final target, but does NOT resolve
    /// a symlinked ANCESTOR DIRECTORY earlier in the path. Retained as <see cref="CanonicalizePath"/>'s
    /// fallback for the (rare) case where the host <see cref="IPathResolver"/> throws — still strictly better
    /// than no resolution at all. Falls back to <see cref="Path.GetFullPath(string)"/> of the ORIGINAL path
    /// when <paramref name="path"/> doesn't exist, isn't a link, or resolution fails for any reason; if even
    /// that fallback fails (e.g. a malformed path with embedded null characters), returns
    /// <paramref name="path"/> UNCHANGED as a last resort — this method is also total.
    /// </summary>
    private static string CanonicalizeImmediateSymlinkOnly(string path)
    {
        try
        {
            System.IO.FileSystemInfo? target = System.IO.File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target?.FullName ?? SafeGetFullPath(path);
        }
        catch (System.IO.IOException)
        {
            return SafeGetFullPath(path);
        }
        catch (UnauthorizedAccessException)
        {
            return SafeGetFullPath(path);
        }
        catch (ArgumentException)
        {
            return SafeGetFullPath(path);
        }
        catch (NotSupportedException)
        {
            return SafeGetFullPath(path);
        }
    }

    /// <summary>
    /// F-FS1a-anc: normalize the <see cref="RestoreAsync(IReadOnlyList{string},CancellationToken)"/> argv
    /// (double-click / "Open With") to full paths, PER-ARG try/skip — one malformed arg (e.g. embedded null
    /// characters, an invalid Win32 device path) must degrade to "that one file didn't open," never crash the
    /// entire restore and lose every other tab. A well-formed arg among a malformed one still opens.
    /// </summary>
    private static List<string> NormalizePassedFiles(IReadOnlyList<string> passedFiles)
    {
        var normalized = new List<string>(passedFiles.Count);
        foreach (string passedFile in passedFiles)
        {
            try
            {
                normalized.Add(System.IO.Path.GetFullPath(passedFile));
            }
            catch (ArgumentException)
            {
                // malformed arg — skip it, the rest of the restore must still proceed.
            }
            catch (System.IO.PathTooLongException)
            {
                // same.
            }
            catch (NotSupportedException)
            {
                // same.
            }
        }

        return normalized;
    }

    /// <summary>
    /// Last-resort wrapper around <see cref="Path.GetFullPath(string)"/> for
    /// <see cref="CanonicalizeImmediateSymlinkOnly"/>'s fallback path — the same malformed input that made
    /// <see cref="System.IO.File.ResolveLinkTarget"/> throw can also make <see cref="Path.GetFullPath(string)"/>
    /// throw (<see cref="ArgumentException"/> / <see cref="NotSupportedException"/>), so this returns
    /// <paramref name="path"/> unchanged rather than propagating.
    /// </summary>
    private static string SafeGetFullPath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>
    /// FS-1 tier (i): open one passed file (double-click / "Open With") as a new tab — the SAME routing
    /// <see cref="OpenFolderTreeFileAsync"/>/<see cref="OpenDocumentAsync"/> use (BUG-10: <c>.hoard</c> to
    /// ChecklistView, everything else to EditorView). Adds the tab to the shell but does NOT activate it —
    /// the caller (<see cref="RestoreAsync(IReadOnlyList{string},CancellationToken)"/>) activates the FIRST
    /// passed file once, after the whole batch is open.
    /// </summary>
    private async Task<DocumentTab> OpenPassedFileAsync(string path, CancellationToken ct)
    {
        string extension = System.IO.Path.GetExtension(path);
        if (string.Equals(extension, ".hoard", StringComparison.OrdinalIgnoreCase))
        {
            ChecklistViewModel checklist = await _checklistFactory.OpenAsync(path, ct).ConfigureAwait(true);
            var tab = DocumentTab.ForChecklist(Guid.NewGuid().ToString("N"), checklist);
            _shell.Tabs.Add(tab);
            return tab;
        }

        EditorViewModel editor = _editorFactory.Create();
        await editor.OpenAsync(path, ct).ConfigureAwait(true);
        var textTab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.File, editor);
        _shell.Tabs.Add(textTab);
        return textTab;
    }

    /// <summary>
    /// Materialize one reconciled tab into a hosted DocumentTab (carry-forward A). Surfaces the FR-19
    /// restore-conflict dialog for a changed-on-disk file BEFORE the buffer is armed for a clobbering Save;
    /// a keep-disk choice reopens from disk (discarding the recovered buffer). A dropped tab returns null.
    /// </summary>
    private async Task<DocumentTab?> MaterializeAsync(RestoredTab restored, CancellationToken ct)
    {
        if (restored.Action is ReconciliationAction.Nothing or ReconciliationAction.DropTab)
            return null;

        bool keepRecovered = restored.RecoveredText is not null;

        // FR-19: a changed-on-disk file surfaces the restore-conflict dialog BEFORE we restore the stale
        // buffer + arm a clobbering Save. Keep-disk discards the recovered buffer (reopen from disk).
        if (restored.SurfaceConflict && restored.Path is not null)
        {
            string fileName = System.IO.Path.GetFileName(restored.Path);
            Views.RestoreConflictChoice choice = await _conflictPrompt.AskAsync(fileName, ct).ConfigureAwait(true);
            // REVISE-10 fix: ShowDiff is NON-DESTRUCTIVE — behave as KeepRecovered so the user's recovered
            // work is not silently discarded. The real diff view is a TODO for a later milestone (the diff
            // renderer is out of scope for M6.3). Only KeepDisk discards the recovered buffer.
            keepRecovered = choice != Views.RestoreConflictChoice.KeepDisk;
        }

        return restored.Kind == TabKind.Hoard
            ? await MaterializeHoardAsync(restored, keepRecovered, ct).ConfigureAwait(true)
            : await MaterializeTextAsync(restored, keepRecovered, ct).ConfigureAwait(true);
    }

    private async Task<DocumentTab> MaterializeTextAsync(RestoredTab restored, bool keepRecovered, CancellationToken ct)
    {
        EditorViewModel editor = _editorFactory.Create();

        if (restored.Path is not null && !restored.OnDiskFileDeleted)
        {
            // Open the on-disk file first (gets encoding/EOL/grammar), then overlay the recovered buffer if
            // the user kept it (the recovered text is the unsaved-edit superset over the on-disk baseline).
            await editor.OpenAsync(restored.Path, ct).ConfigureAwait(true);
        }

        if (keepRecovered && restored.RecoveredText is not null)
        {
            editor.Document.Text = restored.RecoveredText;
            editor.IsDirty = true; // a recovered buffer is unsaved-edits — dirty until the user saves.
            editor.SyncSegmentsFromDocument();
        }

        return DocumentTab.ForEditor(restored.TabId, restored.Kind, editor,
            hasRestoreConflict: restored.SurfaceConflict && keepRecovered, // a kept-recovered conflict stays flagged.
            onDiskFileDeleted: restored.OnDiskFileDeleted);
    }

    private async Task<DocumentTab> MaterializeHoardAsync(RestoredTab restored, bool keepRecovered, CancellationToken ct)
    {
        // A .hoard restores from disk (its terminal mutations are already durable, DP-3); a kept-recovered
        // buffer overlays the recovered JSON. The factory loads from disk or wraps the recovered text.
        // Thread the probe's fileExistsOnDisk through so the factory avoids a second File.Exists on the
        // UI thread (REVISE-7: the reconcile already probed this; use that result).
        bool fileExistsOnDisk = !restored.OnDiskFileDeleted && restored.Path is not null;
        ChecklistViewModel checklist = keepRecovered && restored.RecoveredText is not null
            ? _checklistFactory.FromRecoveredText(restored.Path ?? string.Empty, restored.RecoveredText, fileExistsOnDisk)
            : await _checklistFactory.OpenAsync(restored.Path ?? string.Empty, ct).ConfigureAwait(true);

        return DocumentTab.ForChecklist(restored.TabId, checklist,
            onDiskFileDeleted: restored.OnDiskFileDeleted);
    }

    /// <summary>
    /// Open a folder live (File ▸ Open Folder): set the AI sandbox root + bind/arm the panel (carry-forward
    /// C). No MA5 staleness here — the root is the user's fresh choice.
    /// </summary>
    public async Task OpenFolderAsync(string root, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        BindResult bindResult = await _aiBinder.BindLiveFolderAsync(
            _aiPanel, root, _conversationId, _sessionId, ct).ConfigureAwait(true);
        _openFolder = bindResult.RawRoot;
        _canonicalOpenFolder = bindResult.CanonicalRoot;

        // QA3-2: populate the folder-tree pane for the newly-opened folder (the live-open leg).
        await PopulateFolderTreeAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// QA3-2: populate the folder-tree pane's root row (design §9.5) for the current open-folder root
    /// (<see cref="_canonicalOpenFolder"/> preferred, falling back to <see cref="_openFolder"/>). The
    /// immediate children (files + subdirectories) are enumerated OFF the UI thread (NFR-1 — a sync
    /// Directory.Enumerate* tree walk must never run on the dispatcher thread, mirrors
    /// FindReplaceViewModel.ReviewInFolderAsync / ShellFindReplaceWorkspace.EnumerateFolderFiles); deeper
    /// nesting is lazy-loaded per row by <see cref="FolderTreeNode"/> itself on expand. Clears the tree
    /// when no folder is open, and shows the pane once populated (design §9.5 "Open (armed) -> tree with
    /// (root) labeled" — an armed open folder always shows its populated tree, not just the toggle).
    /// </summary>
    /// <remarks>
    /// <paramref name="ct"/> only gates the <c>Task.Run</c> from STARTING (a cancellation requested before
    /// the enumeration begins is honored); it cannot interrupt the enumeration once it is running —
    /// <c>Directory.Enumerate*</c> takes no token. A cancelled root populate simply completes its scan on
    /// the thread-pool thread with the result then discarded.
    /// </remarks>
    private async Task PopulateFolderTreeAsync(CancellationToken ct)
    {
        string? root = _canonicalOpenFolder ?? _openFolder;
        if (root is null)
        {
            _shell.SetFolderTree(null);
            return;
        }

        IReadOnlyList<FolderTreeNode> children =
            await Task.Run(() => FolderTreeNode.EnumerateImmediateChildren(root), ct).ConfigureAwait(true);
        var rootNode = new FolderTreeNode(RootFolderDisplayName(root), root, isDirectory: true, eagerChildren: children)
        {
            // The root row starts expanded: its children are already loaded (eagerChildren above), so this
            // is a pure display flag (no re-fetch) — and it is the only way "immediate children shown" is
            // actually true the moment the folder opens, rather than behind an extra manual expand.
            IsExpanded = true,
        };
        _shell.SetFolderTree(rootNode);
        _shell.IsFolderTreeVisible = true;
    }

    /// <summary>
    /// QA4-6: the folder-tree root row's label — the leaf name of <paramref name="root"/> (e.g.
    /// <c>"/home/you/notes"</c> -> <c>"notes"</c>) rather than the placeholder literal <c>"(root)"</c>, so
    /// the user can see which folder is actually open at a glance. Trims a trailing separator first
    /// (<see cref="Path.GetFileName(string)"/> returns empty for a path ending in one); falls back to the
    /// full root string for the degenerate case where trimming leaves nothing (a filesystem root like
    /// <c>"/"</c> on Linux or <c>"C:\"</c> on Windows has no leaf segment).
    /// </summary>
    private static string RootFolderDisplayName(string root)
    {
        string trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(leaf) ? root : leaf;
    }

    /// <summary>
    /// QA3-2: open a single file from the folder tree into a new tab (click-to-open — the recommended
    /// minimum-viable interaction for a populated tree, so a listed file is actually actionable). Routes
    /// <c>.hoard</c> files to ChecklistView, everything else to EditorView — the same routing
    /// <see cref="OpenDocumentAsync"/> uses for the picker path. QA5-B1: DEDUPES against an already-open tab
    /// via <see cref="FindTabByPath"/> first — activating the existing tab instead of opening a duplicate.
    /// QA5-B1 W1: also guards the IN-FLIGHT case (the target isn't open YET, but an earlier still-awaiting
    /// call for the SAME canonical path already claimed it — see <see cref="_openingFolderTreePaths"/>). A
    /// second concurrent call for the same in-flight path is a NO-OP — it neither adds a tab nor activates
    /// one itself. Deliberate, not merely "simplest": the FIRST call's own <c>ActivateTab</c> at the end of
    /// this method already lands the tab in the active/focused state once it completes, so a second caller
    /// has nothing useful left to do — there is no tab yet for it to activate, and waiting for the first
    /// call to finish just to re-activate the same tab it already activated would be redundant.
    /// </summary>
    public async Task OpenFolderTreeFileAsync(string path, CancellationToken ct)
    {
        if (FindTabByPath(path) is { } existing)
        {
            _shell.ActivateTab(existing);
            return;
        }

        string canonical = CanonicalizePath(path);
        if (!_openingFolderTreePaths.Add(canonical))
            return; // an open for this exact not-yet-open path is already in flight — see doc comment above.

        try
        {
            string extension = System.IO.Path.GetExtension(path);
            if (string.Equals(extension, ".hoard", StringComparison.OrdinalIgnoreCase))
            {
                ChecklistViewModel checklist = await _checklistFactory.OpenAsync(path, ct).ConfigureAwait(true);
                var tab = DocumentTab.ForChecklist(Guid.NewGuid().ToString("N"), checklist);
                _shell.Tabs.Add(tab);
                _shell.ActivateTab(tab);
            }
            else
            {
                EditorViewModel editor = _editorFactory.Create();
                await editor.OpenAsync(path, ct).ConfigureAwait(true);
                var tab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.File, editor);
                _shell.Tabs.Add(tab);
                _shell.ActivateTab(tab);
            }
        }
        finally
        {
            _openingFolderTreePaths.Remove(canonical);
        }
    }

    /// <summary>
    /// STF-5: the toolbar/menu File ▸ Open Folder… action. Shows the OS folder picker (the same
    /// <see cref="IOpenFolderPrompt"/> seam the MA5 re-prompt and QA2-18 lazy picker use) and, on a
    /// confirmed pick, delegates to <see cref="OpenFolderAsync"/> to set the AI sandbox root and bind/arm
    /// the panel. No-op when the user cancels the picker.
    /// </summary>
    public async Task OpenFolderPickerAsync(CancellationToken ct)
    {
        string? root = await _rootPrompt.RePromptForRootAsync(_openFolder, ct).ConfigureAwait(true);
        if (string.IsNullOrEmpty(root))
            return; // user cancelled the picker.

        await OpenFolderAsync(root, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// QA2-18 LAZY folder request: the handler the AI panel invokes on the FIRST write-mode AI action when
    /// no folder is bound yet (never at startup), and (FEAT-10) the AI ▸ Change Sandbox Folder… command,
    /// which invokes the SAME path against an already-bound session to re-scope it to a new root. Delegates
    /// to the binder — which shows the approved explainer, opens the OS picker, and binds + arms a confirmed
    /// root (a fresh self-test on every call, so re-binding an already-armed session re-runs it) — then
    /// records the resulting open folder. Returns <c>true</c> when a folder was bound (write agency can now
    /// arm), <c>false</c> when the user declined. The prior open folder (if any) is offered to the picker as
    /// context. A decline leaves <see cref="OpenFolder"/>/<see cref="CanonicalOpenFolder"/> UNCHANGED (FEAT-10
    /// fix): the binder does not touch an already-bound panel session on decline, so a cancelled Change
    /// Sandbox Folder must not null out the still-live MA5 baseline for an unrelated already-open root —
    /// that would desync session.json from the panel's actual (unchanged) armed state.
    /// </summary>
    public async Task<bool> RequestWriteFolderAsync(CancellationToken ct)
    {
        BindResult bindResult = await _aiBinder.RequestWriteFolderAsync(
            _aiPanel, _openFolder, _conversationId, _sessionId, ct).ConfigureAwait(true);
        if (bindResult.RawRoot is not null)
        {
            _openFolder = bindResult.RawRoot;
            _canonicalOpenFolder = bindResult.CanonicalRoot;
        }
        return bindResult.RawRoot is not null;
    }

    /// <summary>
    /// Open the settings tab (ODQ-12, Settings ▸ Preferences…). Single-instance: if a settings tab is
    /// already open it is re-activated rather than duplicated; otherwise a fresh one is loaded and added.
    /// </summary>
    public async Task OpenSettingsAsync(CancellationToken ct)
    {
        DocumentTab? existing = _shell.Tabs.FirstOrDefault(t => t.IsSettings);
        if (existing is not null)
        {
            _shell.ActivateTab(existing);
            return;
        }

        DocumentTab tab = await _settingsTabFactory.CreateAsync(ct).ConfigureAwait(true);
        _shell.Tabs.Add(tab);
        _shell.ActivateTab(tab);
    }

    /// <summary>
    /// Create a new scratch buffer tab (toolbar New). A fresh <see cref="EditorViewModel"/> is minted and
    /// added to the shell's tab set as a <see cref="TabKind.Scratch"/> tab.
    /// </summary>
    public void NewDocument()
    {
        EditorViewModel editor = _editorFactory.Create();
        var tab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.Scratch, editor);
        _shell.Tabs.Add(tab);
        _shell.ActivateTab(tab);
    }

    /// <summary>
    /// Create a new <c>.hoard</c> checklist tab (BUG-10: File ▸ New Checklist). A fresh
    /// <see cref="ChecklistViewModel"/> is minted via the factory and added to the shell's tab set
    /// as a <see cref="TabKind.Hoard"/> tab.
    /// </summary>
    public void NewChecklist()
    {
        ChecklistViewModel checklist = _checklistFactory.CreateNew(_openFolder);
        var tab = DocumentTab.ForChecklist(Guid.NewGuid().ToString("N"), checklist);
        _shell.Tabs.Add(tab);
        _shell.ActivateTab(tab);
    }

    /// <summary>
    /// Show the platform file-open dialog and open selected files as new text tabs (toolbar Open). Each
    /// selected file is opened into a fresh <see cref="EditorViewModel"/> via <c>OpenAsync</c> and added
    /// to the shell's tab set. No-op when the user cancels or no owner window is available.
    /// </summary>
    public async Task OpenDocumentAsync(CancellationToken ct)
    {
        Window? owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return;

        var options = new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Open File",
        };

        IReadOnlyList<IStorageFile> picked = await owner.StorageProvider
            .OpenFilePickerAsync(options).ConfigureAwait(true);
        if (picked.Count == 0)
            return;

        foreach (IStorageFile file in picked)
        {
            ct.ThrowIfCancellationRequested();
            string? localPath = file.TryGetLocalPath();
            if (localPath is null)
                continue;

            // BUG-10: route .hoard files to ChecklistView, others to EditorView.
            string extension = System.IO.Path.GetExtension(localPath);
            if (string.Equals(extension, ".hoard", StringComparison.OrdinalIgnoreCase))
            {
                ChecklistViewModel checklist = await _checklistFactory.OpenAsync(localPath, ct).ConfigureAwait(true);
                var checklistTab = DocumentTab.ForChecklist(Guid.NewGuid().ToString("N"), checklist);
                _shell.Tabs.Add(checklistTab);
                _shell.ActivateTab(checklistTab);
            }
            else
            {
                EditorViewModel editor = _editorFactory.Create();
                await editor.OpenAsync(localPath, ct).ConfigureAwait(true);
                var textTab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.File, editor);
                _shell.Tabs.Add(textTab);
                _shell.ActivateTab(textTab);
            }
        }
    }

    /// <summary>
    /// Save the active document (toolbar Save). If the active tab has an <see cref="EditorViewModel"/> with
    /// a known path, saves directly through the atomic store and clears recovery data. For a scratch buffer
    /// (no path), shows a Save As dialog to choose the path before saving. QA3-7: a checklist tab is no
    /// longer a silent no-op here — a <c>.hoard</c> already auto-saves every mutation (zero-debounce), so
    /// Save forces an immediate durable flush (<see cref="ChecklistViewModel.FlushAsync"/>) instead, which
    /// is meaningful when a coalesced item-text edit is still inside its ≤150 ms window, or the checklist
    /// has never been physically written yet (a fresh checklist before its first terminal mutation).
    /// </summary>
    public async Task SaveDocumentAsync(CancellationToken ct)
    {
        if (_shell.ActiveTab?.Checklist is { } checklist)
        {
            await checklist.FlushAsync(ct).ConfigureAwait(true);
            return;
        }

        if (_shell.ActiveTab?.Editor is not { } editor)
            return;

        if (editor.Document.Path is not null)
        {
            await editor.SaveAsync(ct).ConfigureAwait(true);
            await ClearRecoveryOnCleanSaveAsync(_shell.ActiveTab.TabId, ct).ConfigureAwait(true);
        }
        else
        {
            Window? owner = (Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is null)
                return;

            var options = new FilePickerSaveOptions
            {
                Title = "Save As…",
                DefaultExtension = DefaultExtensionForEditor(editor),
                FileTypeChoices = BuildFileTypeChoices(editor.LanguageMode),
                ShowOverwritePrompt = true,
            };

            IStorageFile? picked = await owner.StorageProvider
                .SaveFilePickerAsync(options).ConfigureAwait(true);
            if (picked is null)
                return;

            string? localPath = picked.TryGetLocalPath();
            if (localPath is null)
                return;

            editor.Document.Path = localPath;
            editor.SyncSegmentsFromDocument(); // propagate the new display name.
            await editor.SaveAsync(ct).ConfigureAwait(true);
            await ClearRecoveryOnCleanSaveAsync(_shell.ActiveTab.TabId, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// QA4-4: the single <c>.hoard</c> <see cref="FilePickerFileType"/> for the checklist Save-As dialog's
    /// file-type dropdown (the missing piece the maintainer's live test caught — <see cref="FilePickerSaveOptions.DefaultExtension"/>
    /// alone forces the extension but does not render a visible selector on every platform's native picker).
    /// </summary>
    private static readonly FilePickerFileType[] HoardChecklistFileTypeChoices =
        [new FilePickerFileType("HoardPad Checklist (*.hoard)") { Patterns = ["*.hoard"] }];

    /// <summary>
    /// QA3-7 (Option B — auto-save preserved + additive Save-As): File ▸ Save As… / <c>_Checklist ▸ Save
    /// As…</c> for the ACTIVE <c>.hoard</c> checklist tab. Shows the OS Save dialog (forcing the
    /// <c>.hoard</c> extension both via <see cref="FilePickerSaveOptions.DefaultExtension"/> and — as a
    /// backstop, since a picker does not strictly enforce it — <see cref="ChecklistViewModel.RelocateAsync"/>'s
    /// own force-extension guard), showing a <c>*.hoard</c> file-type selector (QA4-4:
    /// <see cref="HoardChecklistFileTypeChoices"/>), then completes the safe relocate order:
    ///   1–3. write-quiesce → write-new-first(forced ext) → flip <c>_path</c>/<c>_hasPersisted</c> — done
    ///        inside <see cref="ChecklistViewModel.RelocateAsync"/>.
    ///   4. immediate session.json index update — <see cref="PersistAsync"/>, run BEFORE the old file is
    ///      touched, so a crash right after still lets session-restore find the tab at its NEW path.
    ///   5. delete-old-LAST — only now that the new file is durable and the index already points at it.
    ///      Best-effort: a failed delete leaves a harmless orphan rather than risking data (orphan-not-loss
    ///      bias — the whole point of doing this step last).
    /// No-op when the active tab is not a checklist, no window owner is available, or the user cancels the
    /// picker.
    /// </summary>
    public async Task SaveAsActiveChecklistAsync(CancellationToken ct)
    {
        if (_shell.ActiveTab?.Checklist is not { } checklist)
            return;

        Window? owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return;

        var options = new FilePickerSaveOptions
        {
            Title = "Save Checklist As…",
            DefaultExtension = "hoard",
            FileTypeChoices = HoardChecklistFileTypeChoices,
            ShowOverwritePrompt = true,
        };

        IStorageFile? picked = await owner.StorageProvider.SaveFilePickerAsync(options).ConfigureAwait(true);
        string? requestedPath = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(requestedPath))
            return; // user cancelled the picker.

        await RelocateChecklistAsync(checklist, requestedPath, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The picker-independent core of <see cref="SaveAsActiveChecklistAsync"/> (steps 1–5 of the QA3-7 safe
    /// relocate, given an already-CONFIRMED target path). Split out so steps 4–5 — the session-index-before-
    /// delete-old ordering that lives at this layer, not inside <see cref="ChecklistViewModel"/> — are unit-
    /// testable without the OS Save dialog (this codebase's <c>IStorageProvider</c> pickers have no headless
    /// test seam, see <c>BootstrapTests.OpenDocumentRendersAnEditableEditorSurfaceEndToEnd</c>'s note).
    /// </summary>
    internal async Task RelocateChecklistAsync(ChecklistViewModel checklist, string requestedPath, CancellationToken ct)
    {
        string? priorPath = await checklist.RelocateAsync(requestedPath, ct).ConfigureAwait(true);

        // Step 4: the session index must point at the new path BEFORE the old file is ever touched.
        await PersistAsync(ct).ConfigureAwait(true);

        // Step 5: delete-old-LAST.
        if (priorPath is not null)
            TryDeleteOrphanedFile(priorPath);
    }

    /// <summary>
    /// Best-effort delete of the OLD file after a QA3-7 Save-As relocate (step 5, run only once the new
    /// file is durable and session.json already points at it). Swallows I/O failures deliberately: leaving
    /// a harmless orphan on disk is the correct fail-safe outcome, never a data-loss risk.
    /// </summary>
    private static void TryDeleteOrphanedFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch (System.IO.IOException)
        {
            // orphan-not-loss: leave the old file rather than risk anything.
        }
        catch (UnauthorizedAccessException)
        {
            // same.
        }
    }

    /// <summary>
    /// FEAT-2: File ▸ Save As… for the ACTIVE document tab, routed by kind — a <c>.hoard</c> checklist tab
    /// goes through the QA3-7 relocate (<see cref="SaveAsActiveChecklistAsync"/>); everything else goes
    /// through the new text-editor relocate (<see cref="SaveAsActiveEditorAsync"/>).
    /// </summary>
    public Task SaveAsActiveDocumentAsync(CancellationToken ct) =>
        _shell.ActiveTab?.Checklist is not null
            ? SaveAsActiveChecklistAsync(ct)
            : SaveAsActiveEditorAsync(ct);

    /// <summary>
    /// FEAT-2/FEAT-8: File ▸ Save As… for the ACTIVE text (editor) tab. Shows the OS Save dialog, defaulting
    /// the extension to the document's CURRENT on-disk extension when it already has one (relocating an
    /// already-saved file), or the <see cref="Editor.LanguageCatalog.DefaultExtensionFor"/> extension for
    /// its active status-bar language when it is a never-saved scratch buffer — never a hardcoded "txt". The
    /// dialog also exposes a <see cref="BuildFileTypeChoices"/> File Type dropdown, defaulted to the active
    /// language (FEAT-8); picking a different type in the OS dialog changes the extension the picker returns.
    /// Unlike the QA3-7 checklist relocate, a text Save-As never deletes the prior file: standard editor
    /// semantics (Notepad++ / VS Code) leave the old file on disk exactly as last saved, and only the
    /// buffer's target path moves. No-op when the active tab has no editor, no window owner is available, or
    /// the user cancels the picker.
    /// </summary>
    public async Task SaveAsActiveEditorAsync(CancellationToken ct)
    {
        if (_shell.ActiveTab is not { Editor: { } editor } tab)
            return;

        Window? owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return;

        var options = new FilePickerSaveOptions
        {
            Title = "Save As…",
            DefaultExtension = DefaultExtensionForEditor(editor),
            FileTypeChoices = BuildFileTypeChoices(editor.LanguageMode),
            ShowOverwritePrompt = true,
        };

        IStorageFile? picked = await owner.StorageProvider.SaveFilePickerAsync(options).ConfigureAwait(true);
        string? requestedPath = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(requestedPath))
            return; // user cancelled the picker.

        await RelocateEditorAsync(editor, tab.TabId, requestedPath, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// FEAT-2: the Save-As default extension for <paramref name="editor"/>'s picker. Prefers the document's
    /// CURRENT on-disk extension when it already has a path (relocating an already-saved file keeps its own
    /// extension by default); otherwise falls back to the LanguageCatalog default for its active status-bar
    /// language (a never-saved scratch buffer whose language may have been set manually via the Language
    /// menu, QA2-13).
    /// </summary>
    internal static string DefaultExtensionForEditor(EditorViewModel editor)
    {
        if (editor.Document.Path is { Length: > 0 } path)
        {
            string extension = System.IO.Path.GetExtension(path);
            if (extension.Length > 1)
                return extension[1..]; // trim the leading dot.
        }

        return Editor.LanguageCatalog.DefaultExtensionFor(editor.LanguageMode);
    }

    /// <summary>
    /// FEAT-8: builds the Save / Save-As dialog's File Type dropdown from
    /// <see cref="Editor.LanguageCatalog.AvailableLanguages"/>, with the entry for
    /// <paramref name="activeLabel"/> (the document's current status-bar language) ordered FIRST. Native
    /// save pickers (GTK/Win32/macOS) select the first <c>FileTypeChoices</c> entry by default when none is
    /// otherwise pre-selected, so ordering is how the dropdown opens already showing the active language.
    /// <c>OrderBy</c> is a stable sort, so every other language keeps its catalog order behind it. Falls
    /// back to the catalog's own order (Plain Text first) when <paramref name="activeLabel"/> is not one of
    /// the 20 menu languages (e.g. a TextMate-recognized extension outside the manually-selectable set).
    /// </summary>
    internal static IReadOnlyList<FilePickerFileType> BuildFileTypeChoices(string activeLabel) =>
        Editor.LanguageCatalog.AvailableLanguages
            .OrderBy(language => string.Equals(language.Label, activeLabel, StringComparison.Ordinal) ? 0 : 1)
            .Select(language => FileTypeFor(language.Label))
            .ToArray();

    /// <summary>One <see cref="FilePickerFileType"/> for a <see cref="Editor.LanguageCatalog.AvailableLanguages"/>
    /// label, e.g. "Python" -> "Python (*.py)" / <c>*.py</c>. Dockerfile has no conventional extension
    /// (<see cref="Editor.LanguageCatalog.DefaultExtensionFor"/> returns ""), so its pattern is the
    /// unrestricted <c>*</c> rather than the malformed <c>*.</c>.</summary>
    private static FilePickerFileType FileTypeFor(string label)
    {
        string extension = Editor.LanguageCatalog.DefaultExtensionFor(label);
        string pattern = extension.Length > 0 ? $"*.{extension}" : "*";
        return new FilePickerFileType($"{label} ({pattern})") { Patterns = [pattern] };
    }

    /// <summary>
    /// The picker-independent core of <see cref="SaveAsActiveEditorAsync"/> (given an already-confirmed
    /// target path) — split out so it is unit-testable without the OS Save dialog (this codebase's
    /// <c>IStorageProvider</c> pickers have no headless test seam, mirroring
    /// <see cref="RelocateChecklistAsync"/>'s same note). Re-points the buffer to the new path, writes it
    /// durably (create-or-replace through the atomic store via <see cref="EditorViewModel.SaveAsync"/>),
    /// clears FR-21 recovery data for the tab, re-titles the tab (QA3-12's DocumentName-forwarding
    /// mechanism, reused as-is), and immediately re-persists the session index so session-restore finds
    /// the tab at its new path without waiting for a clean shutdown.
    /// </summary>
    internal async Task RelocateEditorAsync(EditorViewModel editor, string tabId, string requestedPath, CancellationToken ct)
    {
        editor.Document.Path = requestedPath;
        editor.SyncSegmentsFromDocument(); // QA3-12 reuse: raises DocumentName -> tab header + window title retitle.
        await editor.SaveAsync(ct).ConfigureAwait(true);
        await ClearRecoveryOnCleanSaveAsync(tabId, ct).ConfigureAwait(true);
        await PersistAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// PANEL-3 (feature-ai-multidoc-panel FR-MD-6(d) / OQ-MD-3): Save-As a specific SCRATCH editor tab so the
    /// AI multi-doc panel can force a never-saved buffer to resolve to a real path when the user checks it as a
    /// write target. Shows the Save As dialog, and on a confirmed path writes the buffer through the durable
    /// save path (identical to <see cref="SaveDocumentAsync"/>'s scratch branch) and returns the new on-disk
    /// path. Returns <c>null</c> when the tab is not a scratch editor, or the user cancelled the dialog, or no
    /// window owner is available — the panel then reverts the check (a path-less buffer cannot be granted, NG-5).
    /// </summary>
    /// <param name="tabId">The durable tab id of the scratch buffer to Save-As (from the panel's row identity).</param>
    public async Task<string?> SaveAsScratchAsync(string tabId, CancellationToken ct)
    {
        DocumentTab? tab = _shell.Tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab?.Editor is not { } editor || editor.Document.Path is not null)
            return null; // not a scratch editor tab (already saved, closed, or a non-editor tab).

        Window? owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return null;

        var options = new FilePickerSaveOptions
        {
            Title = "Save As…",
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
        };

        IStorageFile? picked = await owner.StorageProvider
            .SaveFilePickerAsync(options).ConfigureAwait(true);
        string? localPath = picked?.TryGetLocalPath();
        if (localPath is null)
            return null; // cancelled or a non-local target.

        editor.Document.Path = localPath;
        editor.SyncSegmentsFromDocument(); // propagate the new display name to the tab.
        await editor.SaveAsync(ct).ConfigureAwait(true);
        await ClearRecoveryOnCleanSaveAsync(tab.TabId, ct).ConfigureAwait(true);
        return localPath;
    }

    /// <summary>
    /// PANEL-4 (feature-ai-multidoc-panel FR-MD-7): auto-flush ONE checked document to its on-disk path before
    /// an AI turn. Finds the editor tab whose document path matches <paramref name="path"/> (the same string
    /// the panel's open-document snapshot emitted) and saves it through the durable save path, sealing an
    /// editor undo point (FR-MD-7). Returns <c>true</c> on a durable flush; <c>false</c> when no matching
    /// editor tab is found, the tab is a scratch buffer with no path, or the save fails — the panel then
    /// fails safe and does NOT launch the turn against stale bytes (FR-MD-7 durability posture). The caller
    /// only invokes this for a checked, dirty, non-scratch row, so a non-editor (.hoard) tab or a path-less
    /// tab is a no-target case handled by returning false.
    /// </summary>
    public async Task<bool> FlushDocumentByPathAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        DocumentTab? tab = _shell.Tabs.FirstOrDefault(
            t => t.Editor is { } e && string.Equals(e.Document.Path, path, StringComparison.Ordinal));
        if (tab?.Editor is not { } editor || editor.Document.Path is null)
            return false;

        await editor.FlushForAiTurnAsync(ct).ConfigureAwait(true);
        await ClearRecoveryOnCleanSaveAsync(tab.TabId, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// QA3-20 fix: bounded attempts <see cref="RefreshOpenDocumentByPathAsync"/> makes before giving up on
    /// seeing the AI's write reach disk. See that method's doc comment for why more than one attempt is
    /// needed.
    /// </summary>
    private const int RefreshFromDiskMaxAttempts = 10;

    /// <summary>QA3-20 fix: delay between refresh attempts (see <see cref="RefreshFromDiskMaxAttempts"/>).</summary>
    private static readonly TimeSpan RefreshFromDiskRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// QA3-9: refresh an OPEN editor tab's buffer after the AI applied a write to its on-disk path (the M11
    /// live-write / FR-39 replay path). Finds the editor tab whose document path matches <paramref name="path"/>
    /// and re-reads it from disk through the undo-preserving <see cref="EditorViewModel.RefreshFromDiskAsync"/>
    /// (Ctrl+Z reverts it, QA3-20), so a later manual Save can never silently clobber the AI's edit with a
    /// stale in-memory buffer. No-op when no open editor tab's path matches (the AI wrote to a file that
    /// isn't open, or the file the AI touched is a <c>.hoard</c> checklist — out of scope here, checklists
    /// auto-save their own terminal mutations and are not a text-editor buffer).
    /// </summary>
    /// <remarks>
    /// QA3-20 live verification (Qa320LiveUndoVerifyTests) caught a TOCTOU race here: the caller (
    /// <c>AiPanelViewModel.HandleTurnEventAsync</c>) invokes this the instant a <c>FileOp</c> event reports
    /// <c>Outcome == Applied</c> — which reflects only the <c>deny_by_default.py</c> PreToolUse hook's ALLOW
    /// decision (<see cref="HoardPad.Core.Ai.Provider.LiveOpProjection.ToChip"/>: <c>WasApplied = ev.HookDecision
    /// == HookDecision.Allow</c>), not confirmation that the CLI's own Write/Edit tool call has actually
    /// finished writing the file. There is no PostToolUse write-completion signal in this architecture (the
    /// hook is PreToolUse-only, and it is FROZEN spine — not something this fix touches). A single immediate
    /// read can therefore race ahead of the real write, see no difference from the buffer, and — because
    /// <see cref="EditorViewModel.RefreshFromDiskAsync"/> is a one-shot read with no retry of its own —
    /// permanently miss the AI's edit (live-reproduced: the buffer stayed stale even after 15s of polling).
    /// The fix retries the read with a short bounded backoff until the on-disk content actually differs (or
    /// attempts are exhausted): the common, non-racing case (write already went through) still succeeds on
    /// the FIRST attempt with no added latency; a genuine no-op refresh (disk truly unchanged) exhausts the
    /// retries and correctly no-ops, exactly as before.
    /// </remarks>
    public async Task RefreshOpenDocumentByPathAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
            return;

        DocumentTab? tab = _shell.Tabs.FirstOrDefault(
            t => t.Editor is { } e && string.Equals(e.Document.Path, path, StringComparison.Ordinal));
        if (tab?.Editor is not { } editor)
            return;

        for (int attempt = 0; attempt < RefreshFromDiskMaxAttempts; attempt++)
        {
            string beforeText = editor.Document.Text;
            await editor.RefreshFromDiskAsync(ct).ConfigureAwait(true);
            if (!string.Equals(editor.Document.Text, beforeText, StringComparison.Ordinal))
                return; // the refresh applied a real change (or this was already a genuine no-op — either way, done).

            bool isLastAttempt = attempt == RefreshFromDiskMaxAttempts - 1;
            if (!isLastAttempt)
                await Task.Delay(RefreshFromDiskRetryDelay, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Auto-save (FR-20): write a recovery snapshot for the active text tab's buffer. A .hoard tab needs no
    /// snapshot here (its terminal mutations persist immediately, FR-31). Returns the snapshot ref.
    /// </summary>
    public async Task<string?> SnapshotActiveAsync(CancellationToken ct)
    {
        if (_shell.ActiveTab is not { Editor: { } editor } tab)
            return null;

        string language = editor.Document.LanguageLabel;
        CursorPosition? cursor = ParseCursor(editor.CursorLabel);
        return await _session.SnapshotAsync(
            tab.TabId, tab.Kind, editor.Document.Path, language, cursor, editor.Document.Text, ct)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// F-FS1c periodic auto-save: write a recovery snapshot for EVERY dirty text tab, not just the active
    /// one — a background (unfocused) tab's unsaved edits are exposed to an unexpected termination exactly
    /// like the active tab's are. Reuses the SAME write path <see cref="SnapshotActiveAsync"/> already uses
    /// for the active tab and crash-recovery already reads back (<see cref="SessionService.SnapshotAsync"/>)
    /// — no new persistence format, no new read path. A clean tab, or a <c>.hoard</c> tab (its terminal
    /// mutations persist immediately, FR-31), is skipped. Snapshots a defensive copy of the tab set so a
    /// concurrent tab close mid-loop cannot fault the enumeration. Returns the number of tabs snapshotted.
    /// </summary>
    public async Task<int> SnapshotAllDirtyAsync(CancellationToken ct)
    {
        int snapshotted = 0;
        foreach (DocumentTab tab in _shell.Tabs.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (tab.Editor is not { IsDirty: true } editor)
                continue;

            string language = editor.Document.LanguageLabel;
            CursorPosition? cursor = ParseCursor(editor.CursorLabel);
            await _session.SnapshotAsync(
                tab.TabId, tab.Kind, editor.Document.Path, language, cursor, editor.Document.Text, ct)
                .ConfigureAwait(true);
            snapshotted++;
        }

        return snapshotted;
    }

    /// <summary>
    /// F-FS1c: start the periodic auto-save timer — on each tick, off the crash-only path, it snapshots
    /// every dirty tab via <see cref="SnapshotAllDirtyAsync"/>. Idempotent: a second call stops any
    /// already-running timer first, so re-calling with a new interval (e.g. after a settings change) never
    /// leaks the prior <see cref="DispatcherTimer"/>. <paramref name="interval"/> defaults to
    /// <see cref="DefaultAutoSaveIntervalSeconds"/> when omitted; production always passes the user's FR-20
    /// <c>AutoSaveSeconds</c> setting (App.axaml.cs).
    /// </summary>
    /// <remarks>
    /// F-FS1c fix 1: <paramref name="interval"/> is CLAMPED to
    /// [<see cref="HoardPadSettings.MinAutoSaveSeconds"/>, <see cref="HoardPadSettings.MaxAutoSaveSeconds"/>]
    /// here — the one place every caller (production startup, the Settings-change re-wire, tests) funnels
    /// through — so a zero/negative/out-of-range value (e.g. a hand-edited or partially-deserialized
    /// <c>settings.json</c> whose missing <c>AutoSaveSeconds</c> field CLR-defaults to 0) can never become
    /// <see cref="TimeSpan.Zero"/> (a hot-spinning timer) or a negative interval (Avalonia's
    /// <see cref="DispatcherTimer.Interval"/> setter throws <see cref="ArgumentOutOfRangeException"/> on a
    /// negative value, which would crash the app at startup on every launch until the file is hand-fixed).
    /// The Settings-UI write site (<see cref="SettingsChangeApplier"/>) already clamps before persisting —
    /// this clamp is the defense-in-depth backstop for every OTHER path a value reaches this timer.
    /// </remarks>
    public void StartAutoSaveTimer(TimeSpan? interval = null)
    {
        StopAutoSaveTimerCore();

        TimeSpan requested = interval ?? TimeSpan.FromSeconds(DefaultAutoSaveIntervalSeconds);
        var timer = new DispatcherTimer
        {
            Interval = ClampInterval(requested),
        };
        timer.Tick += OnAutoSaveTick;
        timer.Start();
        _autoSaveTimer = timer;
    }

    /// <summary>F-FS1c fix 1: clamp a requested auto-save interval to the documented FR-20 bounds.</summary>
    private static TimeSpan ClampInterval(TimeSpan requested)
    {
        double clampedSeconds = Math.Clamp(
            requested.TotalSeconds, HoardPadSettings.MinAutoSaveSeconds, HoardPadSettings.MaxAutoSaveSeconds);
        return TimeSpan.FromSeconds(clampedSeconds);
    }

    /// <summary>F-FS1c: stop and release the periodic auto-save timer. Idempotent; safe to call when never
    /// started. This ONLY stops FUTURE ticks — it does not wait for a tick already in flight to finish; use
    /// <see cref="StopAutoSaveTimerAsync"/> when the caller needs that guarantee (fix 4, e.g. shutdown).</summary>
    public void StopAutoSaveTimer() => StopAutoSaveTimerCore();

    /// <summary>
    /// F-FS1c fix 4: stop the periodic auto-save timer AND drain any in-flight snapshot batch before
    /// returning. Unlike <see cref="StopAutoSaveTimer"/> (future ticks only), this guarantees that once it
    /// completes, no <see cref="OnAutoSaveTick"/> continuation can still be running — closing the race where
    /// an in-flight batch's write could otherwise land after (or interleave with) the caller's own shutdown-
    /// time persist. Drains through the same <see cref="_autoSaveGate"/> fix 3 uses for re-entrancy: a
    /// no-op-fast-path when nothing is in flight (the gate is free), a real await when a batch is running.
    /// </summary>
    public async Task StopAutoSaveTimerAsync()
    {
        StopAutoSaveTimerCore();
        await _autoSaveGate.WaitAsync().ConfigureAwait(true);
        _autoSaveGate.Release();
    }

    private void StopAutoSaveTimerCore()
    {
        if (_autoSaveTimer is null)
            return;

        _autoSaveTimer.Stop();
        _autoSaveTimer.Tick -= OnAutoSaveTick;
        _autoSaveTimer = null;
    }

    /// <summary>
    /// The DispatcherTimer tick callback — <c>async void</c> is the documented UI-event-handler exception
    /// (csharp-style.md § Async). A failed snapshot is logged and swallowed here (a UI-event-handler
    /// boundary, csharp-style.md § Error handling) rather than left to crash the dispatcher loop or cancel
    /// future ticks; the next tick simply retries.
    /// </summary>
    private async void OnAutoSaveTick(object? sender, EventArgs e)
    {
        try
        {
            await RunAutoSaveBatchIfIdleAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Avalonia.Logging.Logger.TryGet(Avalonia.Logging.LogEventLevel.Warning, "WorkspaceService")
                ?.Log(this, "Periodic auto-save snapshot failed: {0}", ex.Message);
        }
    }

    /// <summary>
    /// F-FS1c fix 3: the re-entrancy-guarded auto-save batch <see cref="OnAutoSaveTick"/> drives — extracted
    /// (rather than inlined in the <c>async void</c> tick handler) so the guard is directly testable.
    /// Acquires <see cref="_autoSaveGate"/> without blocking (<see cref="SemaphoreSlim.Wait(int)"/> with a
    /// zero timeout); if a prior batch is still awaiting <see cref="SnapshotAllDirtyAsync"/>, this call is a
    /// no-op and returns <c>false</c> immediately — a tick firing at the 1s floor while a batch of several
    /// large dirty tabs is still writing can never overlap write batches. Returns <c>true</c> when this call
    /// actually ran the batch.
    /// </summary>
    internal async Task<bool> RunAutoSaveBatchIfIdleAsync(CancellationToken ct)
    {
        if (!_autoSaveGate.Wait(0, ct))
            return false; // a batch is already in flight — this tick is a no-op; the next tick retries.

        try
        {
            await SnapshotAllDirtyAsync(ct).ConfigureAwait(true);
            return true;
        }
        finally
        {
            _autoSaveGate.Release();
        }
    }

    /// <summary>Test seam (F-FS1c fix 3/4): synchronously acquire the auto-save re-entrancy gate, simulating
    /// a batch already in flight without racing a real snapshot write. Mirrors <see cref="SemaphoreSlim.Wait(int)"/>
    /// with a zero timeout — returns <c>false</c> if the gate is already held.</summary>
    internal bool TryAcquireAutoSaveGateForTest() => _autoSaveGate.Wait(0);

    /// <summary>Test seam (F-FS1c fix 3/4): release the auto-save re-entrancy gate acquired via
    /// <see cref="TryAcquireAutoSaveGateForTest"/>, simulating the in-flight batch finishing.</summary>
    internal void ReleaseAutoSaveGateForTest() => _autoSaveGate.Release();

    /// <summary>Test seam (F-FS1c): whether the periodic auto-save timer is currently running.</summary>
    internal bool IsAutoSaveTimerRunning => _autoSaveTimer?.IsEnabled ?? false;

    /// <summary>Test seam (F-FS1c): the periodic auto-save timer's configured interval, or null when not running.</summary>
    internal TimeSpan? AutoSaveTimerInterval => _autoSaveTimer?.Interval;

    /// <summary>Persist the session.json index (the rebuildable hint, §9.2) from the current tab set.</summary>
    public async Task PersistAsync(CancellationToken ct)
    {
        long seq = _session.NextSeq();
        // The settings tab (ODQ-12) is NON-PERSISTED: it carries no document buffer and has no §9.2
        // snapshot identity, so it is excluded from the session index (M6.8).
        var tabs = _shell.Tabs.Where(t => !t.IsSettings).Select(t => ToSessionTab(t, seq)).ToList();
        // FS-1 (Decision A, FINAL): the open folder is NEVER persisted separately — it is recomputed every
        // launch from the passed file(s) / restored tabs / My Documents (RestoreAsync). Writing null here
        // is the retirement of the old MA5 openFolder/canonicalOpenFolder baseline (prd.md §9.2): an
        // explicitly-opened folder (File ▸ Open Folder) with no open tabs is consequently not restored.
        await _session.PersistSessionAsync(
            openFolder: null, canonicalOpenFolder: null, _shell.ActiveTab?.TabId, tabs, ct).ConfigureAwait(true);
    }

    /// <summary>FR-21: clear a buffer's recovery data on a clean save+close (no spurious recovery prompt next launch).</summary>
    public Task ClearRecoveryOnCleanSaveAsync(string tabId, CancellationToken ct) =>
        _session.ClearRecoveryAsync(tabId, ct);

    /// <summary>
    /// F-FS1c fix 3/4: stop the periodic auto-save timer this coordinator owns. The DI container disposes
    /// this singleton on <c>provider.Dispose()</c> (App.axaml.cs's <c>ShutdownRequested</c>), which by then
    /// has already awaited <see cref="StopAutoSaveTimerAsync"/> — bounded by
    /// <c>App.ShutdownAutoSaveDrainTimeout</c>. That bound means the drain can time out and be ABANDONED
    /// (<see cref="Task.WaitAsync(TimeSpan)"/> does not cancel the underlying task, only stops awaiting it),
    /// so an in-flight batch's <c>finally { _autoSaveGate.Release(); }</c> and the abandoned drain's own
    /// trailing <c>_autoSaveGate.Release()</c> can both still run AFTER this method returns — this is NOT
    /// guaranteed release-only cleanup with nothing racing it.
    /// <para>
    /// Deliberately does NOT dispose <see cref="_autoSaveGate"/>: doing so while either of those releases is
    /// still pending would throw an unobserved <see cref="ObjectDisposedException"/>. <see cref="_autoSaveGate"/>'s
    /// <see cref="SemaphoreSlim.AvailableWaitHandle"/> is never accessed anywhere in this codebase, so it
    /// never allocates the lazy OS wait handle that <c>Dispose</c> exists to release — leaving it undisposed
    /// leaks nothing beyond the managed object itself, reclaimed by the GC moments later as the process exits.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        StopAutoSaveTimerCore();
    }

    private static SessionTab ToSessionTab(DocumentTab tab, long seq)
    {
        if (tab.Editor is { } editor)
        {
            // FEAT-2 fix (mirrors the .hoard Critical-2 fix below): a scratch tab that has since acquired a
            // real on-disk path (File ▸ Save on an untitled buffer, or a FEAT-2 Save-As relocate) must be
            // written as TabKind.File, not the tab's original construction-time TabKind.Scratch. §9.2's
            // reconciliation table unconditionally drops a scratch-kind session entry with no still-live
            // recovery snapshot (FR-21 clears the snapshot on every clean save) — writing the STALE Scratch
            // kind here would make a freshly-saved-then-untouched document vanish from the very next restart
            // even though it has a perfectly durable file on disk.
            TabKind kind = tab.Kind == TabKind.Scratch && editor.Document.Path is not null
                ? TabKind.File
                : tab.Kind;
            return new SessionTab(
                tab.TabId, kind, editor.Document.Path, editor.Document.LanguageLabel,
                editor.IsDirty, ParseCursor(editor.CursorLabel),
                RecoveryRef: editor.IsDirty ? $"{tab.TabId}" : null, Seq: seq);
        }

        // A .hoard tab: no cursor, never "dirty" (zero-debounce persisted); the path is its identity.
        // Write the REAL file path (Critical-2 fix: null here means a clean-save+close finds no session
        // entry path and the tab reconciles to DropTab on the next restart). The checklist's FilePath
        // is its authoritative location; a .hoard without a path is a never-saved buffer (uncommon).
        return new SessionTab(
            tab.TabId, tab.Kind, Path: tab.Checklist?.FilePath, Language: "hoard", IsDirty: false,
            Cursor: null, RecoveryRef: null, Seq: seq);
    }

    private static CursorPosition? ParseCursor(string cursorLabel)
    {
        // "Ln 12, Col 4" → (12, 4). A non-matching label yields null (the restore default-cursors to 1,1).
        var match = System.Text.RegularExpressions.Regex.Match(cursorLabel, @"Ln (\d+), Col (\d+)");
        return match.Success
            ? new CursorPosition(
                int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }
}
