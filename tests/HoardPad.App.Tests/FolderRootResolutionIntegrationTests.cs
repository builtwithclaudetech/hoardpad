// FolderRootResolutionIntegrationTests.cs — FS-1 H1 frozen-oracle (WorkspaceService level): the 3-tier
// resolver wired end-to-end through RestoreAsync — passed-file dedupe/activation (tier i), tab-order
// derivation (tier ii), the My-Documents pane-collapse fallback (tier iii), and PersistAsync no longer
// writing a separately-persisted openFolder (Decision A, FINAL). Class name contains "FolderRootResolution"
// so the verify filter "FolderRootResolution" resolves it.

using System;
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

public sealed class FolderRootResolutionIntegrationTests
{
    /// <summary>A throwaway real file at a fresh throwaway directory (its own, not shared with other fixtures).</summary>
    private sealed class FixtureFile : IDisposable
    {
        public string Path { get; }
        public string Directory { get; }

        public FixtureFile(string name)
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hoardpad-fs1-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, name);
            File.WriteAllText(Path, "hello");
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort. */ }
        }
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

    private static (WorkspaceService Ws, MainWindowViewModel Shell, SessionStore Store) NewWorkspace(
        FakeRecoveryFileSystem fs, TempAppDataPaths paths, string myDocumentsRoot)
    {
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var shell = new MainWindowViewModel();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var conflictPrompt = new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk);
        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler,
            // EchoingAiSessionBinder (FolderTreeTests.cs): echoes the derived root back as-is, so
            // WorkspaceService.OpenFolder reflects EXACTLY what FolderRootResolver derived — unlike
            // NullAiSessionBinder, which would always null it out and falsely pass every assertion below.
            new EchoingAiSessionBinder(),
            conflictPrompt, MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), () => myDocumentsRoot, new HoardPad.Host.PathResolver());

        return (ws, shell, store);
    }

    [Fact]
    public async Task TierOnePassedFileOpensAsAnAdditionalTabAndBecomesActiveRootIsItsFolder()
    {
        using var paths = new TempAppDataPaths();
        using var priorFixture = new FixtureFile("prior.txt");
        using var passedFixture = new FixtureFile("passed.txt");

        var fs1 = new FakeRecoveryFileSystem();
        (WorkspaceService ws1, _, _) = NewWorkspace(fs1, paths, "/unused-tier-i-never-reaches-tier-iii");
        await ws1.OpenFolderTreeFileAsync(priorFixture.Path, CancellationToken.None);
        await ws1.PersistAsync(CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(priorFixture.Path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        (WorkspaceService ws2, MainWindowViewModel shell2, _) = NewWorkspace(fs2, paths, "/unused");

        await ws2.RestoreAsync([passedFixture.Path], CancellationToken.None);

        Assert.Equal(2, shell2.Tabs.Count); // the prior tab restored PLUS the passed file, additively.
        Assert.Contains(shell2.Tabs, t => t.Editor?.Document.Path == priorFixture.Path);
        Assert.Contains(shell2.Tabs, t => t.Editor?.Document.Path == passedFixture.Path);
        Assert.Equal(passedFixture.Path, shell2.ActiveTab?.Editor?.Document.Path); // first passed file active.
        Assert.Equal(passedFixture.Directory, ws2.OpenFolder); // root = the PASSED file's folder, not the prior tab's.
    }

    [Fact]
    public async Task TierOnePassedFileAlreadyOpenDedupesInsteadOfDuplicating()
    {
        using var paths = new TempAppDataPaths();
        using var fixture = new FixtureFile("already-open.txt");

        var fs1 = new FakeRecoveryFileSystem();
        (WorkspaceService ws1, _, _) = NewWorkspace(fs1, paths, "/unused");
        await ws1.OpenFolderTreeFileAsync(fixture.Path, CancellationToken.None);
        await ws1.PersistAsync(CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(fixture.Path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        (WorkspaceService ws2, MainWindowViewModel shell2, _) = NewWorkspace(fs2, paths, "/unused");

        await ws2.RestoreAsync([fixture.Path], CancellationToken.None);

        HoardPad.App.Session.DocumentTab tab = Assert.Single(shell2.Tabs); // DEDUPED — no duplicate tab.
        Assert.Equal(fixture.Path, tab.Editor?.Document.Path);
        Assert.Same(tab, shell2.ActiveTab); // the existing (only) tab is focused.
    }

    [Fact]
    public async Task TierTwoNoPassedFileDerivesRootFromTheFirstRestoredPathBearingTabSkippingScratch()
    {
        using var paths = new TempAppDataPaths();
        using var fixture = new FixtureFile("tab-with-path.txt");

        var fs1 = new FakeRecoveryFileSystem();
        (WorkspaceService ws1, _, _) = NewWorkspace(fs1, paths, "/unused");
        ws1.NewDocument(); // a scratch tab (no path) precedes the path-bearing tab in restore order.
        await ws1.OpenFolderTreeFileAsync(fixture.Path, CancellationToken.None);
        await ws1.PersistAsync(CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(fixture.Path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        (WorkspaceService ws2, MainWindowViewModel shell2, _) = NewWorkspace(fs2, paths, "/unused");

        await ws2.RestoreAsync(CancellationToken.None);

        Assert.Equal(fixture.Directory, ws2.OpenFolder);
        Assert.True(shell2.IsFolderTreeVisible); // a real derived root populates + SHOWS the tree (tiers i/ii).
        Assert.NotEmpty(shell2.FolderTreeRoots);
    }

    [Fact]
    public async Task TierTwoWithACleanTabFirstAndADirtyTabSecondDerivesRootFromTheFirstSavedTabNotTheDirtyOne()
    {
        // FS-1 hostile-review CRITICAL (2026-07-11, live-reproduced): a DIRTY tab (which has a recovery
        // snapshot) must NOT jump ahead of an earlier-SAVED clean tab in restore order — this is FS-2's
        // core guarantee, end to end through the REAL SessionReconciler (not a fake). Root must derive
        // from tab A's folder (saved FIRST, clean), never tab B's folder (saved second, dirty).
        using var paths = new TempAppDataPaths();
        using var fixtureA = new FixtureFile("a.txt"); // saved FIRST, clean (no snapshot).
        using var fixtureB = new FixtureFile("b.txt"); // saved SECOND, DIRTY (has a recovery snapshot).

        var fs1 = new FakeRecoveryFileSystem();
        (WorkspaceService ws1, MainWindowViewModel shell1, _) = NewWorkspace(fs1, paths, "/unused");
        await ws1.OpenFolderTreeFileAsync(fixtureA.Path, CancellationToken.None); // tab A: opened first, clean.
        await ws1.OpenFolderTreeFileAsync(fixtureB.Path, CancellationToken.None); // tab B: opened second, active.

        // Make tab B DIRTY and snapshot it (FR-20) — the active tab is B, so SnapshotActiveAsync targets it.
        DocumentTab tabB = Assert.Single(shell1.Tabs, t => t.Editor?.Document.Path == fixtureB.Path);
        tabB.Editor!.Document.Text = "unsaved change";
        tabB.Editor.IsDirty = true;
        await ws1.SnapshotActiveAsync(CancellationToken.None);
        await ws1.PersistAsync(CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(fixtureA.Path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        fs2.SetProbe(fixtureB.Path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        (WorkspaceService ws2, MainWindowViewModel shell2, _) = NewWorkspace(fs2, paths, "/unused");

        await ws2.RestoreAsync(CancellationToken.None);

        // Restore order must equal save order: A (clean, saved first) THEN B (dirty, saved second).
        Assert.Equal(
            new string?[] { fixtureA.Path, fixtureB.Path },
            shell2.Tabs.Select(t => t.Editor?.Document.Path).ToArray());
        // The derived AI-write root is A's folder — NOT B's, even though B is the dirty one with a snapshot.
        Assert.Equal(fixtureA.Directory, ws2.OpenFolder);
    }

    [Fact]
    public async Task TierThreeFallsBackToMyDocumentsAndKeepsTheFolderPaneCollapsed()
    {
        using var paths = new TempAppDataPaths();
        string myDocs = Path.Combine(Path.GetTempPath(), "hoardpad-fs1-mydocs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(myDocs);
        try
        {
            var fs = new FakeRecoveryFileSystem();
            (WorkspaceService ws, MainWindowViewModel shell, _) = NewWorkspace(fs, paths, myDocs);

            await ws.RestoreAsync(CancellationToken.None);

            Assert.Equal(myDocs, ws.OpenFolder);        // the AI-panel scope line's root — still shown.
            Assert.Empty(shell.FolderTreeRoots);        // FS-1: collapsed — never populated.
            Assert.False(shell.IsFolderTreeVisible);    // FS-1: collapsed — never shown.
        }
        finally
        {
            try { Directory.Delete(myDocs, recursive: true); } catch { /* best-effort. */ }
        }
    }

    [Fact]
    public async Task PersistAsyncNoLongerWritesASeparatelyPersistedOpenFolder()
    {
        // FS-1 (Decision A, FINAL): an explicitly-opened folder with no open tabs is NOT restored next
        // launch — proven directly against the raw session.json, not just the derived-root behavior.
        using var paths = new TempAppDataPaths();
        string opened = Path.Combine(Path.GetTempPath(), "hoardpad-fs1-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(opened);
        try
        {
            var fs = new FakeRecoveryFileSystem();
            (WorkspaceService ws, _, SessionStore store) = NewWorkspace(fs, paths, "/unused");

            await ws.OpenFolderAsync(opened, CancellationToken.None);
            Assert.Equal(opened, ws.OpenFolder); // the CURRENT session sees the live-opened root...

            await ws.PersistAsync(CancellationToken.None);

            SessionState persisted = await store.ReadSessionAsync(CancellationToken.None);
            Assert.Null(persisted.OpenFolder);            // ...but it is NEVER written to session.json.
            Assert.Null(persisted.CanonicalOpenFolder);
        }
        finally
        {
            try { Directory.Delete(opened, recursive: true); } catch { /* best-effort. */ }
        }
    }
}
