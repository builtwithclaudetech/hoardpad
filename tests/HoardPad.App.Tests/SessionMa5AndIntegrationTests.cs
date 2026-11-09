// SessionMa5AndIntegrationTests.cs — QA2-18 lazy-folder AI-session binding + carry-forward A/B integration.
//
// F-FS1b: the startup MA5 stale-root re-prompt tests (AiSessionBinder.BindAsync + the reconciler's
// OpenFolderRestore verdict) were removed — that machinery was unwired dead code once FS-1 stopped
// persisting the open folder (docs/tasklist.md). What remains here:
//   - The QA2-18 lazy first-write folder request: no OS folder picker fires until the first write-mode
//     AI action; a declined pick leaves the panel read/chat only (no silent stale sandbox root).
//   - Carry-forward A: a .hoard document hosts the ChecklistView + activates ActiveChecklist; a text doc
//     hosts the EditorView; opening a file re-mirrors the editor buffer + grammar in place.
//   - WorkspaceService integration: .hoard tab round-trips persist→restart→reopen, restore-conflict dialog
//     fires (scripted choice), deleted-on-disk path, show-diff non-destructive, editor swap no-leak.
//
// These tests drive the REAL AiSessionBinder over the existing host fakes (it never launches a turn — it
// only resolves the launcher + runs the self-test inside the panel's ArmAgencyAsync). Class names contain
// "Session"/"Recovery" so the verify filter "Session|Recovery" resolves them.

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class SessionMa5StaleRootTests
{
    private static AiSessionBinder NewBinder(IOpenFolderPrompt prompt, out TempAppDataPaths paths)
    {
        paths = new TempAppDataPaths();
        return new AiSessionBinder(
            new HoardPad.Host.PathResolver(),   // the real canonicalizing resolver (NormcasedPath producer).
            new RealAtomicFileStore(),
            new NullCapStateStore(),
            paths,
            new FakeAgentBackend(),
            new ScriptedHookHost(selfTestPasses: true),
            prompt,
            new ManualTimeProvider(),
            TestObservationChannelFactory.Create(),
            new HoardPad.Host.FileGrantsWriter(paths, new RealAtomicFileStore()));
    }

    private static AiPanelViewModel NewPanel() =>
        AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);

    [Fact]
    public async Task LazyRequestWriteFolderShowsExplainerThenArmsTheConfirmedRoot()
    {
        // QA2-18 LAZY path: the first write-mode AI action requests a folder. The binder shows the approved
        // first-write explainer BEFORE the picker, then arms the confirmed root.
        using var confirmedDir = new TempDir();
        var prompt = new ScriptedOpenFolderPrompt(confirmedDir.Path);
        AiSessionBinder binder = NewBinder(prompt, out var paths);
        using var _paths = paths;
        var panel = NewPanel();
        panel.ConfiguredMode = HoardPad.Core.Records.AgencyMode.AutoApply;

        BindResult bindResult = await binder.RequestWriteFolderAsync(
            panel, priorRoot: "/old/moved/root", conversationId: "c1", sessionId: "s1", CancellationToken.None);

        Assert.Equal(1, prompt.CallCount);                                   // picker fired ON DEMAND (not at startup).
        Assert.Equal("/old/moved/root", prompt.LastPriorRoot);               // prior root offered as context.
        Assert.NotNull(bindResult.RawRoot);                                  // confirmed root bound.
        Assert.NotNull(bindResult.CanonicalRoot);
        Assert.Equal(SandboxState.Armed, panel.Sandbox);                     // armed against the CONFIRMED root.
        Assert.Contains(confirmedDir.Path, panel.SandboxRoot);
        // The approved first-write explainer appeared as a transcript note before the picker.
        Assert.Contains(panel.Transcript, l => l.Text == AiPanelViewModel.FirstWriteFolderExplainer);
    }

    [Fact]
    public async Task LazyRequestDeclinedLeavesWriteAgencyDisabledNoSilentStaleRoot()
    {
        // MA5 + QA2-18: the user DECLINES the lazy picker (returns null). Write agency must NOT arm; the
        // panel stays read/chat only (the no-folder resting state).
        var prompt = new ScriptedOpenFolderPrompt(root: null);
        AiSessionBinder binder = NewBinder(prompt, out var paths);
        using var _paths = paths;
        var panel = NewPanel();
        panel.ConfiguredMode = HoardPad.Core.Records.AgencyMode.AutoApply;

        BindResult bindResult = await binder.RequestWriteFolderAsync(
            panel, priorRoot: "/old/deleted/root", conversationId: "c1", sessionId: "s1", CancellationToken.None);

        Assert.Equal(1, prompt.CallCount);
        Assert.Null(bindResult.RawRoot);                            // nothing armed.
        Assert.Null(bindResult.CanonicalRoot);
        Assert.Equal(SandboxState.NoFolder, panel.Sandbox);        // read/chat only — no silent stale root.
        Assert.False(panel.IsFolderOpen);
    }

    [Fact]
    public async Task BoundTurnDeliversTheSandboxAndCapEnvSourcedFromTheCapStoreAuthority()
    {
        // The M11 launch-env seam, App-assembly half (docs/escalation-m11-launch-env-seam.md Option A):
        // the binder assembles the spawn env (HOARDPAD_SANDBOX_ROOT = the canonical root; HOARDPAD_CAP_STATE_FILE
        // = ICapStateStore.ResolveCapStateFilePath(conversationId) — the ONE authority, never re-derived) and
        // threads it to the production AiTurnRunner. Bind a present-unchanged root with a CAPTURING backend +
        // a REAL CapStateStore, drive one real turn through the bound runner, and assert the env that reached
        // the launch seam carries the canonical root and the EXACT cap-state path the store resolves.
        using var dir = new TempDir();
        var capStore = new HoardPad.Host.CapStateStore(System.IO.Path.Combine(dir.Path, "caps"), new RealAtomicFileStore());
        var backend = new CapturingAgentBackend();

        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var binder = new AiSessionBinder(
            new HoardPad.Host.PathResolver(),
            new RealAtomicFileStore(),
            capStore,                                   // the REAL O_EXCL store (the cap-path authority).
            paths,
            backend,                                    // captures the env passed to StartTurnAsync.
            new ScriptedHookHost(selfTestPasses: true),
            new ScriptedOpenFolderPrompt(root: null),
            new ManualTimeProvider(),
            TestObservationChannelFactory.Create(),
            new HoardPad.Host.FileGrantsWriter(paths, new RealAtomicFileStore()));

        var panel = NewPanel();                          // a scripted-runner panel — REPOINTED to the production runner by Bind.
        panel.ConfiguredMode = HoardPad.Core.Records.AgencyMode.AutoApply;

        const string conv = "conv-env-1";
        BindResult bind = await binder.BindLiveFolderAsync(panel, dir.Path, conv, sessionId: "s-env-1", CancellationToken.None);
        Assert.NotNull(bind.CanonicalRoot);
        Assert.Equal(SandboxState.Armed, panel.Sandbox);

        // Drive one real turn through the bound production AiTurnRunner; the capturing backend records the env.
        panel.PromptInput = "hello";
        await panel.SendCommand.ExecuteAsync(null);

        Assert.NotNull(backend.CapturedEnv);
        Assert.Equal(bind.CanonicalRoot, backend.CapturedEnv!["HOARDPAD_SANDBOX_ROOT"]);
        // The cap-state path is the store's OWN resolved path (invariant 4 — the hook reads the file the spine writes).
        Assert.Equal(capStore.ResolveCapStateFilePath(conv), backend.CapturedEnv!["HOARDPAD_CAP_STATE_FILE"]);
    }

    /// <summary>A throwaway real directory (the binder requires Directory.Exists(root)).</summary>
    private sealed class TempDir : System.IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hoardpad-ma5-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}

public sealed class SessionDocumentHostingTests
{
    // Carry-forward A: a text tab repoints Editor; a .hoard tab populates ActiveChecklist + flips the
    // content-row visibility flags. These are pure VM-state assertions (no headless window needed).

    [Fact]
    public void ActivatingATextTabHostsTheEditorAndDisablesTheChecklistMenu()
    {
        var shell = new MainWindowViewModel();
        var editor = NewEditorVm();
        var tab = DocumentTab.ForEditor("t1", TabKind.File, editor);

        shell.ActivateTab(tab);

        Assert.True(shell.IsEditorActive);
        Assert.False(shell.IsChecklistActive);
        Assert.Same(editor, shell.Editor);
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null)); // no active checklist → menu disabled.
    }

    [Fact]
    public async Task ActivatingAHoardTabActivatesTheChecklistAndEnablesTheChecklistMenu()
    {
        var shell = new MainWindowViewModel();
        ChecklistViewModel checklist = NewChecklistVm();
        var tab = DocumentTab.ForChecklist("t2", checklist);

        shell.ActivateTab(tab);

        Assert.True(shell.IsChecklistActive);
        Assert.False(shell.IsEditorActive);
        Assert.Same(checklist, shell.ActiveChecklist);
        // QA4-3 fix 2: activation alone is NOT enough — the menu only enables once a row is ALSO focused
        // (FocusedChecklistItemId != null), so it greys out honestly instead of looking enabled and
        // silently no-opping until the user clicks a row.
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));
        shell.FocusedChecklistItemId = "row-id"; // simulates OnRowGotFocus publishing the clicked row.
        Assert.True(shell.ChecklistIndentCommand.CanExecute(null));

        await checklist.DisposeAsync();
    }

    [Fact]
    public async Task SwitchingFromHoardToTextTabClearsTheActiveChecklist()
    {
        var shell = new MainWindowViewModel();
        ChecklistViewModel checklist = NewChecklistVm();
        shell.ActivateTab(DocumentTab.ForChecklist("t2", checklist));
        Assert.NotNull(shell.ActiveChecklist);

        // Switch to a text tab — ActiveChecklist clears (the menu disables, no stale active checklist).
        shell.ActivateTab(DocumentTab.ForEditor("t1", TabKind.Scratch, NewEditorVm()));

        Assert.Null(shell.ActiveChecklist);
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));

        await checklist.DisposeAsync();
    }

    private static EditorViewModel NewEditorVm()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(new CountingAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }

    private static ChecklistViewModel NewChecklistVm()
    {
        var doc = new HoardPad.Core.Checklist.HoardDocument
        {
            Title = "List",
            Settings = new HoardPad.Core.Checklist.HoardSettings { CheckedItemMode = "strikethrough-in-place" },
            Items = [new HoardPad.Core.Checklist.HoardItem { Id = "a", Text = "a", ParentId = null, Order = 0 }],
        };
        return ChecklistViewModel.ForDocument(
            new HoardPad.App.Checklist.ChecklistFileService(new CountingAtomicFileStore()),
            new ManualTimeProvider(), "/tmp/hoardpad-test/host.hoard", doc);
    }
}

/// <summary>
/// Integration tests for <see cref="WorkspaceService"/> — the glue layer between the session/recovery
/// engine and the live workspace. Exercises the paths that were uncovered by the review (Criticals 1 &amp; 2
/// and the REVISE items). All tests use a scripted <see cref="IRestoreConflictPrompt"/> + a NullAi binder
/// so no real AI panel or UI is involved.
/// </summary>
public sealed class SessionWorkspaceIntegrationTests
{
    // ----- Helpers -----

    private static (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths, string hoardPath)
        NewWorkspaceWithHoard()
    {
        var paths = new TempAppDataPaths();
        string hoardPath = Path.Combine(paths.AppDataRoot, "test.hoard");
        // Write a minimal valid .hoard file to disk so the restore-path can open it.
        File.WriteAllText(hoardPath, """
            {"schemaVersion":1,"title":"Test","settings":{"checkedItemMode":"strikethrough-in-place"},"items":[]}
            """);

        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var shell = new MainWindowViewModel();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var conflictPrompt = new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk);
        var editorFactory = MakeEditorFactory();
        var checklistFactory = MakeChecklistFactory();
        var aiBinder = new NullAiSessionBinder();

        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler,
            aiBinder, conflictPrompt, editorFactory, checklistFactory, MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        return (ws, shell, paths, hoardPath);
    }

    private static (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths, FakeRecoveryFileSystem fs, SessionService sessionService)
        NewWorkspaceRaw(
            IRestoreConflictPrompt? conflictPrompt = null,
            FakeRecoveryFileSystem? fs = null)
    {
        var paths = new TempAppDataPaths();
        fs ??= new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var shell = new MainWindowViewModel();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        conflictPrompt ??= new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk);
        var editorFactory = MakeEditorFactory();
        var checklistFactory = MakeChecklistFactory();
        var aiBinder = new NullAiSessionBinder();

        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler,
            aiBinder, conflictPrompt, editorFactory, checklistFactory, MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        return (ws, shell, paths, fs, sessionService);
    }

    private static EditorViewModelFactory MakeEditorFactory()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModelFactory(fileService, registry, new LanguageCatalog(registry));
    }

    private static ChecklistViewModelFactory MakeChecklistFactory() =>
        new(new ChecklistFileService(new RealAtomicFileStore()), new ManualTimeProvider());

    private static HoardPad.App.Settings.SettingsTabFactory MakeSettingsTabFactory() =>
        new(new HoardPad.App.Settings.SettingsStore(new RealAtomicFileStore(), new TempAppDataPaths()));

    // ----- QA3-23: the first-launch untitled buffer must be a real, active tab -----

    [Fact]
    public async Task RestoreAsyncWrapsTheStartupScratchEditorAsARealTabWhenNothingIsRestored()
    {
        // Mirrors the real composition root: MainWindowViewModel is constructed with the ctor-injected
        // scratch EditorViewModel BEFORE RestoreAsync runs, and there is no prior session (fresh install).
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var editorFactory = MakeEditorFactory();
        EditorViewModel startupEditor = editorFactory.Create();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var shell = new MainWindowViewModel(startupEditor, aiPanel);
        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler, new NullAiSessionBinder(),
            new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk),
            editorFactory, MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        await ws.RestoreAsync(CancellationToken.None);

        DocumentTab tab = Assert.Single(shell.Tabs);
        Assert.Same(startupEditor, tab.Editor);
        Assert.Same(tab, shell.ActiveTab); // active immediately — no New/Open click needed.
        Assert.Same(startupEditor, shell.Editor);

        // The regression this closes: Find & Replace's "Current file" scope resolves off ActiveTab, not
        // the top-level Editor mirror — before the fix, ActiveTab stayed null and this was always null.
        var findReplaceWorkspace = new ShellFindReplaceWorkspace(shell, ws, new RealAtomicFileStore());
        Assert.NotNull(findReplaceWorkspace.CurrentBuffer);
    }

    // ----- (1) Critical-2: .hoard tab round-trips persist → restart → reopens from disk -----

    [Fact]
    public async Task HoardTabRoundTripsPersistThenRestartAndReopensFromDisk()
    {
        // A .hoard tab whose path is null in session.json reconciles to DropTab (Critical-2). After the fix,
        // the real path persists in session.json and the tab reopens from disk on the next restart.
        var (ws, shell, paths, hoardPath) = NewWorkspaceWithHoard();
        using var _paths = paths;

        // Simulate a .hoard tab that is open.
        var checklistVm = await ChecklistViewModel.OpenAsync(
            new ChecklistFileService(new RealAtomicFileStore()), new ManualTimeProvider(), hoardPath, CancellationToken.None);
        var tab = DocumentTab.ForChecklist("hoard-tab-1", checklistVm);
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        // Persist the session (simulates shutdown) — ToSessionTab must write the real path.
        await ws.PersistAsync(CancellationToken.None);

        await checklistVm.DisposeAsync();

        // ---- Restart: build a fresh WorkspaceService over the SAME paths ----
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var fs2 = new FakeRecoveryFileSystem();
        // The .hoard file still exists on disk → the session-entry probe must see it as present so the
        // reconciler does not drop a non-dirty deleted entry (it would drop a gone file with no snapshot).
        fs2.SetProbe(hoardPath, new CurrentDiskFile(Exists: true, ContentHash: "H-hoard", Mtime: DateTimeOffset.UnixEpoch));
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());
        var shell2 = new MainWindowViewModel();
        var aiPanel2 = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var ws2 = new WorkspaceService(
            shell2, aiPanel2, session2, reconciler2, new NullAiSessionBinder(),
            new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk),
            MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        SessionReconcileResult result = await ws2.RestoreAsync(CancellationToken.None);

        // After the fix: the .hoard tab survives the restart and reopens from disk.
        RestoredTab restoredTab = Assert.Single(result.Tabs);
        Assert.Equal(hoardPath, restoredTab.Path);       // real path persisted → PathAuthority resolved it.
        Assert.Equal(TabKind.Hoard, restoredTab.Kind);
        Assert.True(shell2.Tabs.Count == 1, "the .hoard tab must be materialized after restart.");
    }

    // ----- (2) Restore-conflict dialog fires for an externally-changed file -----

    [Fact]
    public async Task RestoreConflictDialogFiresForExternallyChangedFile()
    {
        const string tabId = "tab-conflict";
        var recordingPrompt = new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepRecovered);
        var fs = new FakeRecoveryFileSystem();
        var (_, _, paths, _, sessionService) = NewWorkspaceRaw(conflictPrompt: recordingPrompt, fs: fs);
        using var _paths = paths;

        // Write a real file so MaterializeTextAsync can open it (keep-recovered overlays the recovered
        // buffer on top, but OpenAsync runs first to get encoding / language detection).
        string realPath = Path.Combine(paths.AppDataRoot, "conflict.md");
        await File.WriteAllTextAsync(realPath, "disk content at restart");

        // Snapshot at baseline H1.
        fs.SetProbe(realPath, new CurrentDiskFile(Exists: true, ContentHash: "H1", Mtime: DateTimeOffset.UnixEpoch));
        await sessionService.SnapshotAsync(tabId, TabKind.File, realPath, "markdown",
            new CursorPosition(1, 1), "recovered buffer text", CancellationToken.None);

        // Restart: disk changed to H2 → conflict surfaces.
        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(realPath, new CurrentDiskFile(Exists: true, ContentHash: "H2-CHANGED", Mtime: DateTimeOffset.UnixEpoch.AddMinutes(1)));
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());
        var shell2 = new MainWindowViewModel();
        var aiPanel2 = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var ws2 = new WorkspaceService(
            shell2, aiPanel2, session2, reconciler2, new NullAiSessionBinder(),
            recordingPrompt, MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        await ws2.RestoreAsync(CancellationToken.None);

        // The conflict prompt must have fired for the changed-on-disk file.
        Assert.Contains(Path.GetFileName(realPath), recordingPrompt.AskedFiles);
        // keep-recovered: the recovered buffer is loaded as dirty (overlaid on the opened disk file).
        DocumentTab? materializedTab = shell2.Tabs.FirstOrDefault();
        Assert.NotNull(materializedTab);
        Assert.Equal("recovered buffer text", materializedTab.Editor?.Document.Text);
        Assert.True(materializedTab.Editor?.IsDirty, "a kept-recovered buffer is unsaved-edits → dirty.");
    }

    [Fact]
    public async Task KeepDiskChoiceLoadsFromDiskAndIsNotDirty()
    {
        const string tabId = "tab-keepdisk";
        var diskContent = "disk content";
        var keepDiskPrompt = new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk);
        var fs = new FakeRecoveryFileSystem();
        var (_, _, paths, _, sessionService) = NewWorkspaceRaw(conflictPrompt: keepDiskPrompt, fs: fs);
        using var _paths = paths;

        // Write the on-disk file so EditorViewModel.OpenAsync can read it.
        string realPath = Path.Combine(paths.AppDataRoot, "keepdisk.md");
        await File.WriteAllTextAsync(realPath, diskContent);

        fs.SetProbe(realPath, new CurrentDiskFile(Exists: true, ContentHash: "H1", Mtime: DateTimeOffset.UnixEpoch));
        await sessionService.SnapshotAsync(tabId, TabKind.File, realPath, "plaintext",
            new CursorPosition(1, 1), "recovered text that should be discarded", CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(realPath, new CurrentDiskFile(Exists: true, ContentHash: "H2-CHANGED", Mtime: DateTimeOffset.UnixEpoch.AddMinutes(1)));
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());
        var shell2 = new MainWindowViewModel();
        var aiPanel2 = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var ws2 = new WorkspaceService(
            shell2, aiPanel2, session2, reconciler2, new NullAiSessionBinder(),
            keepDiskPrompt, MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        await ws2.RestoreAsync(CancellationToken.None);

        DocumentTab? tab = shell2.Tabs.FirstOrDefault();
        Assert.NotNull(tab);
        // keep-disk: the editor loads the on-disk content (not the stale recovered buffer).
        Assert.Equal(diskContent, tab.Editor?.Document.Text);
        Assert.False(tab.Editor?.IsDirty, "keep-disk opened from disk — not dirty.");
    }

    [Fact]
    public async Task ShowDiffChoiceIsNonDestructivePreservesRecoveredBuffer()
    {
        // REVISE-10: ShowDiff must NOT silently discard the recovered work — it behaves like KeepRecovered.
        const string tabId = "tab-showdiff";
        var showDiffPrompt = new ScriptedRestoreConflictPrompt(RestoreConflictChoice.ShowDiff);
        var fs = new FakeRecoveryFileSystem();
        var (_, _, paths, _, sessionService) = NewWorkspaceRaw(conflictPrompt: showDiffPrompt, fs: fs);
        using var _paths = paths;

        string realPath = Path.Combine(paths.AppDataRoot, "showdiff.md");
        await File.WriteAllTextAsync(realPath, "disk content");

        fs.SetProbe(realPath, new CurrentDiskFile(Exists: true, ContentHash: "H1", Mtime: DateTimeOffset.UnixEpoch));
        await sessionService.SnapshotAsync(tabId, TabKind.File, realPath, "plaintext",
            new CursorPosition(1, 1), "user's recovered work", CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(realPath, new CurrentDiskFile(Exists: true, ContentHash: "H2-CHANGED", Mtime: DateTimeOffset.UnixEpoch.AddMinutes(1)));
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());
        var shell2 = new MainWindowViewModel();
        var aiPanel2 = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var ws2 = new WorkspaceService(
            shell2, aiPanel2, session2, reconciler2, new NullAiSessionBinder(),
            showDiffPrompt, MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        await ws2.RestoreAsync(CancellationToken.None);

        DocumentTab? tab = shell2.Tabs.FirstOrDefault();
        Assert.NotNull(tab);
        // ShowDiff is non-destructive: the recovered buffer is preserved (KeepRecovered semantics until
        // the real diff view is built in a future milestone).
        Assert.Equal("user's recovered work", tab.Editor?.Document.Text);
        Assert.True(tab.Editor?.IsDirty, "preserved recovered buffer is unsaved → dirty.");
    }

    // ----- (3) Deleted-on-disk path -----

    [Fact]
    public async Task DeletedOnDiskFileRestoresWithRecoveredBufferAndDeletedFlag()
    {
        const string tabId = "tab-deletedws";
        const string filePath = "/proj/gone.md";
        var fs = new FakeRecoveryFileSystem();
        var (_, _, paths, _, sessionService) = NewWorkspaceRaw(fs: fs);
        using var _paths = paths;

        fs.SetProbe(filePath, new CurrentDiskFile(Exists: true, ContentHash: "HX", Mtime: DateTimeOffset.UnixEpoch));
        await sessionService.SnapshotAsync(tabId, TabKind.File, filePath, "markdown",
            new CursorPosition(1, 1), "the user's unsaved edits", CancellationToken.None);

        // Restart: file is gone from disk (no probe set → Exists: false).
        var fs2 = new FakeRecoveryFileSystem();
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());
        var shell2 = new MainWindowViewModel();
        var aiPanel2 = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var ws2 = new WorkspaceService(
            shell2, aiPanel2, session2, reconciler2, new NullAiSessionBinder(),
            new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk),
            MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        await ws2.RestoreAsync(CancellationToken.None);

        // The tab should materialize with the recovered buffer + the deleted flag.
        DocumentTab? tab = shell2.Tabs.FirstOrDefault();
        Assert.NotNull(tab);
        Assert.True(tab.OnDiskFileDeleted, "deleted-on-disk must be flagged, not silently resurrected.");
        Assert.Equal("the user's unsaved edits", tab.Editor?.Document.Text);
    }

    // ----- BUG-10: New Checklist creates a .hoard tab with a ChecklistViewModel -----

    [Fact]
    public void NewChecklistCreatesHoardTabWithChecklistViewModel()
    {
        var (ws, shell, _, _) = NewWorkspaceWithHoard();

        ws.NewChecklist();

        Assert.Single(shell.Tabs);
        DocumentTab tab = shell.Tabs[0];
        Assert.True(tab.IsChecklist);
        Assert.NotNull(tab.Checklist);
        Assert.Same(tab, shell.ActiveTab);
        Assert.Equal("New Checklist", tab.DisplayName);
    }

    [Fact]
    public void NewChecklistAddsTabAndActivatesItAlongsideExistingTabs()
    {
        var (ws, shell, _, _) = NewWorkspaceWithHoard();

        // Create a text tab first.
        ws.NewDocument();
        Assert.Single(shell.Tabs);

        // Then create a checklist tab — now there should be two.
        ws.NewChecklist();
        Assert.Equal(2, shell.Tabs.Count);

        // The checklist tab should be the active one.
        DocumentTab checklistTab = shell.Tabs[1];
        Assert.True(checklistTab.IsChecklist);
        Assert.Same(checklistTab, shell.ActiveTab);
    }

    // ----- QA3-9: WorkspaceService routes an AI-applied write's path to the matching OPEN editor tab. -----

    [Fact]
    public async Task RefreshOpenDocumentByPathAsyncRefreshesTheMatchingOpenTabFromDisk()
    {
        var (ws, shell, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        string path = Path.Combine(paths.AppDataRoot, "open-doc.txt");
        await File.WriteAllTextAsync(path, "original content");

        EditorViewModelFactory editorFactory = MakeEditorFactory();
        EditorViewModel editor = editorFactory.Create();
        await editor.OpenAsync(path, CancellationToken.None);
        var tab = DocumentTab.ForEditor("t1", TabKind.File, editor);
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        // Simulate the AI writing to the file OUTSIDE the editor (the M11 live-write / FR-39 replay path).
        await File.WriteAllTextAsync(path, "AI-edited content");

        await ws.RefreshOpenDocumentByPathAsync(path, CancellationToken.None);

        Assert.Equal("AI-edited content", editor.Document.Text);
        Assert.False(editor.IsDirty); // matches disk exactly — a later Save cannot clobber the AI's edit.
    }

    // ----- QA3-20 fix: Qa320LiveUndoVerifyTests' live drive caught a TOCTOU race — the "Applied" FileOp
    // event that triggers a refresh reflects only the PreToolUse hook's ALLOW decision, not confirmation
    // that the CLI's own tool call finished writing the file, so a single immediate read can (and, live,
    // did) race ahead of the real write and permanently miss it. -----

    [Fact]
    public async Task RefreshOpenDocumentByPathAsyncRetriesUntilARaceDelayedWriteBecomesVisible()
    {
        var (ws, shell, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        string path = Path.Combine(paths.AppDataRoot, "race-doc.txt");
        await File.WriteAllTextAsync(path, "original content");

        EditorViewModelFactory editorFactory = MakeEditorFactory();
        EditorViewModel editor = editorFactory.Create();
        await editor.OpenAsync(path, CancellationToken.None);
        var tab = DocumentTab.ForEditor("t1", TabKind.File, editor);
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        // The "AI's write" becomes visible 250ms AFTER the refresh call starts — well past its first
        // attempt but well within its retry budget — reproducing the race the live drive caught.
        Task delayedWrite = Task.Run(async () =>
        {
            await Task.Delay(250);
            await File.WriteAllTextAsync(path, "AI-edited content (raced)");
        });

        await ws.RefreshOpenDocumentByPathAsync(path, CancellationToken.None);
        await delayedWrite;

        Assert.Equal("AI-edited content (raced)", editor.Document.Text); // the retry loop caught the delayed write.
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task RefreshOpenDocumentByPathAsyncIsANoOpWhenNoOpenTabMatchesThePath()
    {
        var (ws, shell, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        EditorViewModelFactory editorFactory = MakeEditorFactory();
        EditorViewModel scratchEditor = editorFactory.Create(); // a scratch buffer — no on-disk path.
        var tab = DocumentTab.ForEditor("t1", TabKind.Scratch, scratchEditor);
        shell.Tabs.Add(tab);

        // No open tab's path matches — must not throw, and must not touch the unrelated scratch tab.
        await ws.RefreshOpenDocumentByPathAsync("/some/unrelated/path.txt", CancellationToken.None);

        Assert.Equal(string.Empty, scratchEditor.Document.Text);
    }

    // ----- F-FS1a: FindTabByPath dedupe canonicalizes symlinks (passed-file open, RestoreAsync tier (i)) --

    [Fact]
    public async Task RestoreAsyncDedupesASymlinkToAnAlreadyOpenFileButOpensAGenuinelyDifferentFileAsANewTab()
    {
        var (ws, shell, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        string realFile = Path.Combine(paths.AppDataRoot, "real.txt");
        string otherFile = Path.Combine(paths.AppDataRoot, "other.txt");
        await File.WriteAllTextAsync(realFile, "real content");
        await File.WriteAllTextAsync(otherFile, "other content");

        string symlinkPath = Path.Combine(paths.AppDataRoot, "real-link.txt");
        File.CreateSymbolicLink(symlinkPath, realFile);

        // The real file, a symlink TO it, and a genuinely different file — all passed on one launch.
        await ws.RestoreAsync([realFile, symlinkPath, otherFile], CancellationToken.None);

        // The symlink dedupes onto the SAME tab as its real target (2 tabs, not 3) — no duplicate spawned.
        Assert.Equal(2, shell.Tabs.Count);
        Assert.Single(shell.Tabs, t => t.FilePath == realFile);
        Assert.Single(shell.Tabs, t => t.FilePath == otherFile);
    }

    // F-FS1a follow-up: CanonicalizePath's fallback must be total — a malformed path (embedded null char,
    // or empty) must never crash FindTabByPath, or it would take down RestoreAsync's whole per-tab startup
    // loop over one bad session.json entry. FindTabByPath is called directly (not via RestoreAsync) because
    // RestoreAsync's OWN argument normalization (Path.GetFullPath on the raw passedFiles) throws on this
    // same malformed input BEFORE ever reaching FindTabByPath — that earlier throw is a separate, out-of-
    // scope concern, not the one under test here.

    [Fact]
    public void FindTabByPathReturnsNullRatherThanThrowingForAPathWithAnEmbeddedNullCharacter()
    {
        var (ws, _, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        DocumentTab? match = ws.FindTabByPath("bad\0path");

        Assert.Null(match);
    }

    [Fact]
    public void FindTabByPathReturnsNullRatherThanThrowingForAnEmptyPath()
    {
        var (ws, _, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        DocumentTab? match = ws.FindTabByPath(string.Empty);

        Assert.Null(match);
    }

    // ----- F-FS1a-anc: FindTabByPath dedupe canonicalizes a symlinked ANCESTOR directory, not just an
    // immediate symlinked file. Pre-fix, CanonicalizePath only resolved a symlink AT THE FINAL PATH
    // COMPONENT (File.ResolveLinkTarget on the raw path itself) — a file reached through a symlinked
    // ancestor directory earlier in the path was never resolved, so it fell back to a plain GetFullPath
    // string that does NOT match the real file's canonical form, spawning a DUPLICATE tab. This test is
    // non-vacuous: run against the pre-fix CanonicalizePath (immediate-symlink-only), it fails with 2 tabs
    // instead of 1 (captured below in the reliability report).

    [Fact]
    public async Task RestoreAsyncDedupesAFileReachedThroughASymlinkedAncestorDirectory()
    {
        var (ws, shell, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        string realDir = Path.Combine(paths.AppDataRoot, "real-dir");
        Directory.CreateDirectory(realDir);
        string realFile = Path.Combine(realDir, "doc.txt");
        await File.WriteAllTextAsync(realFile, "ancestor-symlink dedupe content");

        // The symlink is on the ANCESTOR directory, not the leaf file — "doc.txt" itself is never a
        // symlink; only "link-to-real-dir" (real-dir's sibling) is. This is the case F-FS1a's original,
        // narrower (immediate-symlink-only) resolution never covered.
        string ancestorSymlinkDir = Path.Combine(paths.AppDataRoot, "link-to-real-dir");
        try
        {
            Directory.CreateSymbolicLink(ancestorSymlinkDir, realDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Directory symlinks are unsupported/unpermitted in this test environment — skip cleanly
            // rather than fail on an environment limitation unrelated to the fix under test. Not expected
            // to fire on this Linux dev box (a normal user can always symlink under their own temp dir).
            return;
        }

        string viaAncestorSymlink = Path.Combine(ancestorSymlinkDir, "doc.txt");

        // Open the SAME underlying file via both the real path and the ancestor-symlink path on one launch.
        await ws.RestoreAsync([realFile, viaAncestorSymlink], CancellationToken.None);

        // Deduped to ONE tab, not two — the ancestor symlink resolves to the same canonical file.
        Assert.Single(shell.Tabs);
        Assert.Equal(realFile, shell.Tabs[0].FilePath);
    }

    // ----- F-FS1a-anc: RestoreAsync's argv normalize must degrade per-arg, not crash the whole restore -----

    [Fact]
    public async Task RestoreAsyncSkipsAMalformedPassedArgAndStillOpensTheValidOne()
    {
        var (ws, shell, paths, _, _) = NewWorkspaceRaw();
        using var _paths = paths;

        string validFile = Path.Combine(paths.AppDataRoot, "valid.txt");
        await File.WriteAllTextAsync(validFile, "valid content");

        // "bad\0path" makes Path.GetFullPath throw ArgumentException (embedded null character) — pre-fix,
        // that exception propagated out of the passedFiles.Select(...).ToList() eagerly, crashing the
        // ENTIRE restore (every tab lost, not just the malformed arg). Post-fix, the malformed arg is
        // skipped and the valid file still opens.
        SessionReconcileResult result = await ws.RestoreAsync(["bad\0path", validFile], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(shell.Tabs);
        Assert.Equal(validFile, shell.Tabs[0].FilePath);
    }
}

/// <summary>
/// A stub <see cref="IAiSessionBinder"/> that never touches the real AI spine. Returns no-op results so
/// <see cref="WorkspaceService"/> integration tests run headless (no process launch, no hook, no panel).
/// </summary>
internal sealed class NullAiSessionBinder : IAiSessionBinder
{
    public Task<BindResult> BindLiveFolderAsync(
        AiPanelViewModel panel, string root, string conversationId, string sessionId, CancellationToken ct)
    {
        panel.ClearSession();
        return Task.FromResult(new BindResult(RawRoot: root, CanonicalRoot: root));
    }

    public Task<BindResult> RequestWriteFolderAsync(
        AiPanelViewModel panel, string? priorRoot, string conversationId, string sessionId, CancellationToken ct) =>
        Task.FromResult(new BindResult(RawRoot: null, CanonicalRoot: null));

    public Task<BindResult> BindDerivedRootAsync(
        AiPanelViewModel panel, string root, string conversationId, string sessionId, CancellationToken ct)
    {
        panel.ClearSession();
        return Task.FromResult(new BindResult(RawRoot: null, CanonicalRoot: null));
    }
}

public sealed class SessionEditorReMirrorTests
{
    // Carry-forward B: opening a file INTO an already-bound EditorView (an in-place open, not a DataContext
    // swap) must re-mirror Document.Text AND re-apply the grammar — otherwise the live editor shows the
    // stale buffer. This is a headless [AvaloniaFact] because it exercises the real EditorView control.

    [AvaloniaFact]
    public async Task OpeningAFileInPlaceReMirrorsTheEditorBufferAndGrammar()
    {
        // Bind a fresh editor VM to an EditorView in a headless window.
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var vm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        var view = new EditorView { DataContext = vm };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();

        var editorControl = view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single();
        Assert.Equal(string.Empty, editorControl.Text); // a fresh scratch buffer is empty.

        // Write a real .py file and open it INTO the already-bound VM (in-place open).
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hoardpad-remirror-" + Guid.NewGuid().ToString("N") + ".py");
        await System.IO.File.WriteAllTextAsync(path, "def main():\n    return 42\n");
        try
        {
            await vm.OpenAsync(path, CancellationToken.None);

            // Without the carry-forward B fix the editor would still show the stale empty buffer. Re-mirror
            // pushes the opened document's text + grammar into the live control.
            view.MirrorActiveDocument();

            Assert.Equal("def main():\n    return 42\n", editorControl.Text);   // buffer re-mirrored.
            Assert.Equal("source.python", vm.Document.GrammarScope);            // grammar re-resolved.
        }
        finally
        {
            window.Close();
            try { System.IO.File.Delete(path); } catch { }
        }
    }
}
