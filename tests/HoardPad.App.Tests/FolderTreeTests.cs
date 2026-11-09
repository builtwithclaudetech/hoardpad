// FolderTreeTests.cs — QA3-2: the "Folder" side panel was never wired past a static placeholder
// (no ItemsSource, no backing collection, no enumeration). These are interaction-layer tests over the
// real WorkspaceService (the same wiring composition-root uses), asserting the tree actually populates
// with BOTH a directory node AND a file node — not merely "non-empty" (a single dummy file would falsely
// satisfy a weaker assertion while subdirectories go unhandled).

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FolderTreeTests
{
    private static (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) NewWorkspace()
    {
        var paths = new TempAppDataPaths();
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

        return (ws, shell, paths);
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
        new(new HoardPad.App.Checklist.ChecklistFileService(new RealAtomicFileStore()), new ManualTimeProvider());

    private static SettingsTabFactory MakeSettingsTabFactory() =>
        new(new SettingsStore(new RealAtomicFileStore(), new TempAppDataPaths()));

    /// <summary>A throwaway fixture folder with one file AND one subdirectory (with its own file inside).</summary>
    private sealed class FixtureFolder : System.IDisposable
    {
        public string Root { get; }
        public string FilePath { get; }
        public string SubdirPath { get; }
        public string HoardFilePath { get; }

        public FixtureFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "hoardpad-foldertree-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            FilePath = Path.Combine(Root, "notes.txt");
            File.WriteAllText(FilePath, "hello");

            SubdirPath = Path.Combine(Root, "sub");
            Directory.CreateDirectory(SubdirPath);
            File.WriteAllText(Path.Combine(SubdirPath, "nested.txt"), "nested");

            HoardFilePath = Path.Combine(Root, "list.hoard");
            File.WriteAllText(HoardFilePath, """
                {"schemaVersion":1,"title":"Test","settings":{"checkedItemMode":"strikethrough-in-place"},"items":[]}
                """);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ----- (1) Live open path: WorkspaceService.OpenFolderAsync -----

    [Fact]
    public async Task OpenFolderAsyncPopulatesTheTreeWithADirectoryNodeAndAFileNode()
    {
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        await ws.OpenFolderAsync(fixture.Root, CancellationToken.None);

        FolderTreeNode root = Assert.Single(shell.FolderTreeRoots);
        Assert.Equal(Path.GetFileName(fixture.Root), root.Name); // QA4-6: leaf folder name, not "(root)".
        Assert.True(root.IsDirectory);
        Assert.Contains(root.Children, n => n.IsDirectory && n.Name == "sub");
        Assert.Contains(root.Children, n => !n.IsDirectory && n.Name == "notes.txt");
    }

    [Fact]
    public async Task OpenFolderAsyncShowsTheFolderTreePane()
    {
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        Assert.False(shell.IsFolderTreeVisible); // hidden by default (design §9.5 "No folder").

        await ws.OpenFolderAsync(fixture.Root, CancellationToken.None);

        Assert.True(shell.IsFolderTreeVisible); // design §9.5 "Open (armed)" — the populated tree is shown.
    }

    [Fact]
    public async Task OpenFolderTreeFileAsyncOpensAFileIntoANewTab()
    {
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        await ws.OpenFolderAsync(fixture.Root, CancellationToken.None);
        int before = shell.Tabs.Count;

        await ws.OpenFolderTreeFileAsync(fixture.FilePath, CancellationToken.None);

        Assert.Equal(before + 1, shell.Tabs.Count);
        DocumentTab tab = shell.Tabs[^1];
        Assert.Equal(fixture.FilePath, tab.Editor?.Document.Path);
        Assert.Same(tab, shell.ActiveTab);
    }

    // ----- QA5-B1: double-clicking an already-open file in the Folder panel must ACTIVATE the existing
    // tab, not open a duplicate. FindTabByPath already existed for this exact purpose (used by RestoreAsync's
    // passed-file dedupe) but OpenFolderTreeFileAsync never consulted it. -----

    [Fact]
    public async Task OpenFolderTreeFileAsyncActivatesTheExistingTabInsteadOfDuplicatingOnASecondOpenOfTheSameTextFile()
    {
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        await ws.OpenFolderTreeFileAsync(fixture.FilePath, CancellationToken.None);
        DocumentTab firstOpen = Assert.Single(shell.Tabs);

        await ws.OpenFolderTreeFileAsync(fixture.FilePath, CancellationToken.None);

        Assert.Same(firstOpen, Assert.Single(shell.Tabs)); // no duplicate — the ORIGINAL tab instance.
        Assert.Same(firstOpen, shell.ActiveTab); // focus moved to the existing tab.
    }

    [Fact]
    public async Task OpenFolderTreeFileAsyncActivatesTheExistingTabInsteadOfDuplicatingOnASecondOpenOfTheSameHoardFile()
    {
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        await ws.OpenFolderTreeFileAsync(fixture.HoardFilePath, CancellationToken.None);
        DocumentTab firstOpen = Assert.Single(shell.Tabs);

        await ws.OpenFolderTreeFileAsync(fixture.HoardFilePath, CancellationToken.None);

        Assert.Same(firstOpen, Assert.Single(shell.Tabs)); // no duplicate tab spawned.
        Assert.Same(firstOpen, shell.ActiveTab);
    }

    /// <summary>
    /// F-FS1a-anc proof: the second activation arrives via a path through a SYMLINKED ANCESTOR DIRECTORY that
    /// resolves to the same real file — this only dedupes correctly if <see cref="WorkspaceService.FindTabByPath"/>
    /// (and its <c>CanonicalizePath</c>, F-FS1a-anc realpath resolution) is reused rather than a plain string
    /// compare. Skips cleanly on a test environment where directory symlinks are unsupported/unpermitted.
    /// </summary>
    [Fact]
    public async Task OpenFolderTreeFileAsyncDedupesASecondOpenReachedThroughASymlinkedAncestorDirectory()
    {
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        string ancestorSymlinkDir = Path.Combine(paths.AppDataRoot, "link-to-fixture-root");
        try
        {
            Directory.CreateSymbolicLink(ancestorSymlinkDir, fixture.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Directory symlinks unsupported/unpermitted in this environment — skip rather than fail on an
            // environment limitation unrelated to the fix under test (mirrors SessionMa5AndIntegrationTests).
            return;
        }

        string viaAncestorSymlink = Path.Combine(ancestorSymlinkDir, "notes.txt");

        await ws.OpenFolderTreeFileAsync(fixture.FilePath, CancellationToken.None);
        DocumentTab firstOpen = Assert.Single(shell.Tabs);

        await ws.OpenFolderTreeFileAsync(viaAncestorSymlink, CancellationToken.None);

        // Deduped to the SAME canonical file, not a string-compare miss — proves CanonicalizePath was reused.
        Assert.Same(firstOpen, Assert.Single(shell.Tabs));
        Assert.Same(firstOpen, shell.ActiveTab);
    }

    /// <summary>
    /// QA5-B1 W1 (hostile review): <see cref="FindTabByPath"/> alone cannot close the race where a SECOND
    /// call for the same NOT-YET-OPEN path starts while the first is still awaiting its file read —
    /// <c>MainWindow.axaml.cs</c>'s <c>OnFolderTreeNodeDoubleTapped</c> invokes the command's
    /// <c>Execute</c> directly, bypassing <c>CanExecute</c>, so <c>AsyncRelayCommand</c>'s own
    /// no-concurrent-execution guard never engages. Fires two overlapping calls (does NOT await the first
    /// before starting the second) for a file that starts out closed, so both calls race past the
    /// <see cref="FindTabByPath"/> check before either has added a tab. Non-vacuous: run against the
    /// pre-fix code (no in-flight guard), this spawns 2 tabs instead of 1 (captured in the report below).
    /// </summary>
    [Fact]
    public async Task OpenFolderTreeFileAsyncDoesNotDuplicateWhenTwoOverlappingCallsRaceForTheSameNotYetOpenFile()
    {
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        Task first = ws.OpenFolderTreeFileAsync(fixture.FilePath, CancellationToken.None);
        Task second = ws.OpenFolderTreeFileAsync(fixture.FilePath, CancellationToken.None); // NOT awaited first.

        await Task.WhenAll(first, second);

        DocumentTab tab = Assert.Single(shell.Tabs); // only one call actually opened the file.
        Assert.Equal(fixture.FilePath, tab.Editor?.Document.Path);
        Assert.Same(tab, shell.ActiveTab);
    }

    // ----- (2) Session-restore path: WorkspaceService.RestoreAsync -----

    [Fact]
    public async Task RestoreAsyncDerivesTheFolderFromTheFirstRestoredTabWithAPath()
    {
        // FS-1 tier (ii), Decision A (FINAL): openFolder is no longer persisted/reconciled (MA5 retired) —
        // the root is derived every launch from the FIRST restored tab that has a saved on-disk path. An
        // explicitly-opened folder with NO open tabs (the OLD MA5 scenario this test used to cover) is
        // consequently NOT restored next launch; see RestoreAsyncLeavesTheTreeAtMyDocumentsWhenNoTabHasAPath.
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;
        using var fixture = new FixtureFolder();

        // Simulate a prior run: open a file INSIDE the fixture folder as a tab (no File ▸ Open Folder —
        // FS-1 tier (ii) never needs an explicit folder open), then persist the session (shutdown).
        await ws.OpenFolderTreeFileAsync(fixture.FilePath, CancellationToken.None);
        await ws.PersistAsync(CancellationToken.None);

        // ---- Restart: a fresh WorkspaceService over the SAME session store ----
        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(fixture.FilePath, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());
        var shell2 = new MainWindowViewModel();
        var aiPanel2 = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        // EchoingAiSessionBinder mirrors the real AiSessionBinder's observable contract: it echoes the
        // derived root back as both RawRoot/CanonicalRoot, so WorkspaceService's post-bind
        // _canonicalOpenFolder ?? _openFolder resolves to the SAME root FS-1 derived (NullAiSessionBinder
        // always returns null roots regardless, which would leave the tree unpopulated and falsely pass
        // this test for the wrong reason).
        var ws2 = new WorkspaceService(
            shell2, aiPanel2, session2, reconciler2, new EchoingAiSessionBinder(),
            new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk),
            MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        await ws2.RestoreAsync(CancellationToken.None);

        FolderTreeNode root = Assert.Single(shell2.FolderTreeRoots);
        Assert.Equal(Path.GetFileName(fixture.Root), root.Name); // QA4-6: leaf folder name, not "(root)".
        Assert.Contains(root.Children, n => n.IsDirectory && n.Name == "sub");
        Assert.Contains(root.Children, n => !n.IsDirectory && n.Name == "notes.txt");
    }

    [Fact]
    public async Task RestoreAsyncLeavesTheTreeEmptyWhenNoFolderWasOpen()
    {
        // FS-1 tier (iii): no passed files, no restored tab has a path — the root falls back to My
        // Documents, but the folder-tree pane stays COLLAPSED until the user summons it (Decision A).
        (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) = NewWorkspace();
        using var _paths = paths;

        await ws.RestoreAsync(CancellationToken.None);

        Assert.Empty(shell.FolderTreeRoots);
        Assert.False(shell.IsFolderTreeVisible);
    }

    // ----- (3) Lazy-load path (QA3-2 REVISE — the fix-1/fix-2/fix-3 gaps this had zero coverage for) -----

    /// <summary>
    /// Poll until <paramref name="node"/>'s children no longer contain the transient "Loading…" row. Checks
    /// the LoadingLabel specifically (not the general <see cref="FolderTreeNode.IsPlaceholder"/> flag) — a
    /// truncated directory's PERMANENT "…N more" notice row is also IsPlaceholder, so that flag alone would
    /// never clear and this helper would spin until timeout.
    /// </summary>
    private static async Task WaitUntilLoadedAsync(FolderTreeNode node, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (node.Children.Any(c => c.Name == FolderTreeNode.LoadingLabel))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("FolderTreeNode lazy load did not complete in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ExpandingALazyDirectoryReplacesThePlaceholderWithRealChildren()
    {
        using var fixture = new FixtureFolder();
        var node = new FolderTreeNode("sub", fixture.SubdirPath, isDirectory: true);

        // Before expand: exactly one placeholder child (the expander arrow, no real I/O yet).
        FolderTreeNode placeholderBefore = Assert.Single(node.Children);
        Assert.True(placeholderBefore.IsPlaceholder);

        node.IsExpanded = true;
        await WaitUntilLoadedAsync(node);

        Assert.DoesNotContain(node.Children, c => c.IsPlaceholder);
        Assert.Contains(node.Children, c => !c.IsDirectory && c.Name == "nested.txt");
    }

    [Fact]
    public async Task ASecondRapidExpandDoesNotReRunTheLazyLoad()
    {
        using var fixture = new FixtureFolder();
        var node = new FolderTreeNode("sub", fixture.SubdirPath, isDirectory: true);

        node.IsExpanded = true;
        await WaitUntilLoadedAsync(node);
        Assert.Contains(node.Children, c => c.Name == "nested.txt");

        // Change the directory's real contents AFTER the first load, then collapse + re-expand. If the
        // _childrenLoaded guard did NOT hold, this second expand would re-enumerate and pick up the
        // deletion; observing the STALE (pre-deletion) listing survive is the proof the guard held.
        File.Delete(Path.Combine(fixture.SubdirPath, "nested.txt"));
        node.IsExpanded = false;
        node.IsExpanded = true;

        await Task.Delay(200); // give an (incorrect) reload every chance to run before asserting.
        Assert.Contains(node.Children, c => c.Name == "nested.txt");
    }

    [Fact]
    public void EnumerateImmediateChildrenReturnsEmptyForADeletedDirectory()
    {
        string missing = Path.Combine(Path.GetTempPath(), "hoardpad-foldertree-missing-" + Guid.NewGuid().ToString("N"));

        IReadOnlyList<FolderTreeNode> children = FolderTreeNode.EnumerateImmediateChildren(missing);

        Assert.Empty(children);
    }

    /// <summary>
    /// QA3-2 CRITICAL fix (hostile live review): a directory bigger than MaxChildrenPerDirectory must NOT
    /// hand an unbounded row count to the UI-bound collection (live-measured at ~1ms UI-thread layout per
    /// realized row — an unbounded directory would still block the dispatcher for seconds even with the
    /// Reset/bulk-add fix). Exercises the real cap end-to-end with real files on disk (not a mocked count).
    /// </summary>
    [Fact]
    public void EnumerateImmediateChildrenCapsALargeDirectoryAndAppendsATruncationNotice()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hoardpad-foldertree-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            int realCount = FolderTreeNode.MaxChildrenPerDirectory + 7;
            for (int i = 0; i < realCount; i++)
                File.WriteAllText(Path.Combine(dir, $"f{i:D5}.txt"), string.Empty);

            IReadOnlyList<FolderTreeNode> children = FolderTreeNode.EnumerateImmediateChildren(dir);

            // MaxChildrenPerDirectory REAL rows + exactly one synthetic notice row — never the full realCount.
            Assert.Equal(FolderTreeNode.MaxChildrenPerDirectory + 1, children.Count);
            FolderTreeNode notice = children[^1];
            Assert.True(notice.IsPlaceholder);
            Assert.False(notice.IsDirectory);
            Assert.Equal(string.Empty, notice.FullPath);
            Assert.Contains("7 more", notice.Name); // realCount - MaxChildrenPerDirectory omitted entries.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void OpenFolderTreeItemCommandIsANoOpForTheTruncationNoticeRow()
    {
        var shell = new MainWindowViewModel();
        bool handlerCalled = false;
        shell.OpenFolderTreeFileHandler = _ => { handlerCalled = true; return Task.CompletedTask; };

        // Build a real truncation-notice row the same way EnumerateImmediateChildren does (a synthetic
        // IsPlaceholder row with an empty FullPath) and confirm the SAME guard that protects the transient
        // "Loading…" row also protects this permanent one.
        string dir = Path.Combine(Path.GetTempPath(), "hoardpad-foldertree-cap-noop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < FolderTreeNode.MaxChildrenPerDirectory + 1; i++)
                File.WriteAllText(Path.Combine(dir, $"f{i:D5}.txt"), string.Empty);

            FolderTreeNode notice = FolderTreeNode.EnumerateImmediateChildren(dir)[^1];
            Assert.True(notice.IsPlaceholder);

            shell.OpenFolderTreeItemCommand.Execute(notice);

            Assert.False(handlerCalled); // the guard rejected the notice row — no resolve-to-cwd, no throw.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void OpenFolderTreeItemCommandIsANoOpForThePlaceholderRow()
    {
        var shell = new MainWindowViewModel();
        bool handlerCalled = false;
        shell.OpenFolderTreeFileHandler = _ => { handlerCalled = true; return Task.CompletedTask; };

        // A plain directory row (no eagerChildren) carries exactly the "Loading…" placeholder until expanded.
        var directoryRow = new FolderTreeNode("sub", "/some/fixture/sub", isDirectory: true);
        FolderTreeNode placeholder = Assert.Single(directoryRow.Children);
        Assert.True(placeholder.IsPlaceholder);

        shell.OpenFolderTreeItemCommand.Execute(placeholder);

        Assert.False(handlerCalled); // the guard rejected the placeholder — no resolve-to-cwd, no throw.
    }
}

/// <summary>
/// An <see cref="IAiSessionBinder"/> that echoes the restored/live open folder back as both RawRoot and
/// CanonicalRoot (unlike <see cref="NullAiSessionBinder"/>, which always returns null roots). Used where a
/// test needs WorkspaceService's post-bind open-folder fields to reflect the real restored/opened root.
/// </summary>
internal sealed class EchoingAiSessionBinder : IAiSessionBinder
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

    /// <summary>FS-1: echoes the derived root back, same posture as the other Bind* methods on this fake.</summary>
    public Task<BindResult> BindDerivedRootAsync(
        AiPanelViewModel panel, string root, string conversationId, string sessionId, CancellationToken ct)
    {
        panel.ClearSession();
        return Task.FromResult(new BindResult(RawRoot: root, CanonicalRoot: root));
    }
}
