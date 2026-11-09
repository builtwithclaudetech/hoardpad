// ChecklistSaveAsIntegrationTests.cs — QA3-7 at the WorkspaceService layer (steps 4–5 of the safe
// relocate order: immediate session.json index update BEFORE the old file is touched, delete-old-LAST).
//
// ChecklistSaveAsTests.cs already proves steps 1–3 (write-quiesce -> write-new-first(forced ext) -> flip
// _path/_hasPersisted) at the ChecklistViewModel layer in isolation. This file proves the layer above:
// WorkspaceService.RelocateChecklistAsync (the picker-independent core of SaveAsActiveChecklistAsync —
// see its doc comment: this codebase's IStorageProvider pickers have no headless test seam) drives
// PersistAsync BEFORE deleting the old file, so a crash right after a relocate still lets session-restore
// find the tab at its NEW path, and a simulated crash mid-relocate (the new-path write itself fails) never
// touches session.json or the old file at all. Also proves File ▸ Save is no longer a silent no-op on a
// checklist tab (SaveDocumentAsync routes to ChecklistViewModel.FlushAsync).
//
// The verify filter matches the class name "ChecklistSaveAs".

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Checklist;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ChecklistSaveAsIntegrationTests
{
    private static (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths, SessionService session)
        NewWorkspace(IAtomicFileStore? checklistStore = null)
    {
        var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var shell = new MainWindowViewModel();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);

        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var editorFileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorFactory = new EditorViewModelFactory(editorFileService, registry, new LanguageCatalog(registry));
        var checklistFactory = new ChecklistViewModelFactory(
            new ChecklistFileService(checklistStore ?? new RealAtomicFileStore()), new ManualTimeProvider());
        var settingsFactory = new HoardPad.App.Settings.SettingsTabFactory(
            new HoardPad.App.Settings.SettingsStore(new RealAtomicFileStore(), paths));

        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler, new NullAiSessionBinder(),
            new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk),
            editorFactory, checklistFactory, settingsFactory, new ScriptedOpenFolderPrompt(null),
            StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        return (ws, shell, paths, sessionService);
    }

    private static DocumentTab AddHoardTab(MainWindowViewModel shell, ChecklistViewModel checklist, string tabId = "hoard-tab-1")
    {
        var tab = DocumentTab.ForChecklist(tabId, checklist);
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);
        return tab;
    }

    // ----- Step 4 before step 5: session.json points at the NEW path once RelocateChecklistAsync returns. -----

    [Fact]
    public async Task RelocateChecklistUpdatesTheSessionIndexToTheNewPathBeforeDeletingTheOldFile()
    {
        var (ws, shell, paths, _) = NewWorkspace();
        using var _paths = paths;

        string oldPath = Path.Combine(paths.AppDataRoot, "Original.hoard");
        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            new ChecklistFileService(new RealAtomicFileStore()), new ManualTimeProvider(),
            oldPath, new HoardDocument { Title = "Groceries" });
        checklist.AddItem(null, "Milk"); // a TERMINAL mutation -> zero-debounce write to oldPath (unlike
                                          // SetTitle's ≤150ms coalesce, which a ManualTimeProvider never
                                          // fires without an explicit Advance()).
        for (int i = 0; i < 200 && checklist.CompletedWrites < 1; i++)
            await Task.Delay(5);
        Assert.True(File.Exists(oldPath));

        AddHoardTab(shell, checklist);

        string newPath = Path.Combine(paths.AppDataRoot, "Relocated.hoard");
        await ws.RelocateChecklistAsync(checklist, newPath, CancellationToken.None);

        Assert.Equal(newPath, checklist.FilePath);
        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(oldPath), "step 5 (delete-old-LAST) must have removed the orphaned old file");

        // ---- Restart over the SAME session.json: session-restore must find the tab at the NEW path. ----
        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(newPath, new CurrentDiskFile(Exists: true, ContentHash: "H", Mtime: DateTimeOffset.UnixEpoch));
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());

        SessionReconcileResult result = await session2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab restoredTab = Assert.Single(result.Tabs);
        Assert.Equal(newPath, restoredTab.Path);
        Assert.Equal(TabKind.Hoard, restoredTab.Kind);

        await checklist.DisposeAsync();
    }

    // ----- Simulated crash mid-relocate: the new-path write itself fails -> session.json / old file untouched. -----

    [Fact]
    public async Task RelocateChecklistNeverTouchesSessionJsonOrDeletesTheOldFileWhenTheNewWriteFails()
    {
        string oldPath = Path.Combine(Path.GetTempPath(), "hoardpad-qa3-7-crash-" + Guid.NewGuid().ToString("N"), "Original.hoard");
        string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "WillFail.hoard");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        var faulting = new FaultingAtomicFileStoreForWorkspaceTests(new RealAtomicFileStore(), newPath);

        var (ws, shell, paths, _) = NewWorkspace(faulting);
        using var _paths = paths;

        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            new ChecklistFileService(faulting), new ManualTimeProvider(),
            oldPath, new HoardDocument { Title = "Groceries" });
        checklist.AddItem(null, "Milk"); // TERMINAL -> zero-debounce write to oldPath.
        for (int i = 0; i < 200 && checklist.CompletedWrites < 1; i++)
            await Task.Delay(5);
        Assert.True(File.Exists(oldPath));

        AddHoardTab(shell, checklist);

        // Persist a BASELINE session.json pointing at the OLD path before the aborted relocate.
        await ws.PersistAsync(CancellationToken.None);
        string sessionJsonPath = Path.Combine(paths.AppDataRoot, "session.json");
        Assert.True(File.Exists(sessionJsonPath));
        string baselineSessionJson = await File.ReadAllTextAsync(sessionJsonPath);

        await Assert.ThrowsAsync<IOException>(
            () => ws.RelocateChecklistAsync(checklist, newPath, CancellationToken.None));

        Assert.Equal(oldPath, checklist.FilePath); // never flipped.
        Assert.True(File.Exists(oldPath), "the old file must survive an aborted relocate");
        Assert.False(File.Exists(newPath), "a failed write must never leave a partial new file behind");
        // session.json must be exactly as it was before the aborted relocate — PersistAsync (step 4) never ran.
        Assert.Equal(baselineSessionJson, await File.ReadAllTextAsync(sessionJsonPath));

        await checklist.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(oldPath)!, recursive: true); } catch { /* best-effort */ }
    }

    // ----- File ▸ Save / toolbar Save is no longer a silent no-op on a checklist tab. -----

    [Fact]
    public async Task SaveDocumentOnAChecklistTabFlushesAPendingCoalescedEditInsteadOfDoingNothing()
    {
        var (ws, shell, paths, _) = NewWorkspace();
        using var _paths = paths;

        string path = Path.Combine(paths.AppDataRoot, "Flush.hoard");
        var document = new HoardDocument
        {
            Title = "List",
            Items = [new HoardItem { Id = "i1", Text = "first", Order = 0 }],
        };
        var clock = new ManualTimeProvider();
        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            new ChecklistFileService(new RealAtomicFileStore()), clock, path, document, fileAlreadyExists: false);
        AddHoardTab(shell, checklist);

        checklist.EditText("i1", "still coalescing"); // inside the <=150ms window — no write yet.
        Assert.Equal(0, checklist.CompletedWrites);

        // Before the fix, SaveDocumentAsync returned immediately because ActiveTab.Editor is null for a
        // checklist tab — this call must now actually route to the checklist and flush the pending edit.
        await ws.SaveDocumentAsync(CancellationToken.None);

        Assert.Equal(1, checklist.CompletedWrites);
        Assert.True(File.Exists(path));

        await checklist.DisposeAsync();
    }

    /// <summary>Delegates to a real store for every path except one poisoned target (WorkspaceService-layer variant).</summary>
    private sealed class FaultingAtomicFileStoreForWorkspaceTests : IAtomicFileStore
    {
        private readonly IAtomicFileStore _inner;
        private readonly string _poisonedPath;

        public FaultingAtomicFileStoreForWorkspaceTests(IAtomicFileStore inner, string poisonedPath)
        {
            _inner = inner;
            _poisonedPath = poisonedPath;
        }

        public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            string.Equals(destPath, _poisonedPath, StringComparison.Ordinal)
                ? throw new IOException("simulated crash mid-relocate")
                : _inner.CreateWhenAbsentAsync(destPath, content, ct);

        public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            string.Equals(destPath, _poisonedPath, StringComparison.Ordinal)
                ? throw new IOException("simulated crash mid-relocate")
                : _inner.ReplaceWhenPresentAsync(destPath, content, ct);

        public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            _inner.AppendRecordAsync(logPath, payload, ct);

        public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct) =>
            _inner.OpenAndRepairAsync(logPath, mode, ct);

        public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct) =>
            _inner.ProbeAtomicCapabilityAsync(destPath, ct);
    }
}
