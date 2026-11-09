// SessionRecoveryTests.cs — the M6.3 session/recovery gates (FR-19/FR-20/FR-21, PRD §9.2).
//
// Drive the REAL session/recovery engine (SessionStore over a temp dir + the pure-Core-driven
// SessionReconciler) with a fake on-disk probe so the §9.2 reconciliation table is exercised end-to-end:
//   - a simulated crash+restart restores the tab + cursor (reconciliation-driven);
//   - a changed-on-disk file (hash mismatch) surfaces the restore-conflict (SurfaceConflict);
//   - the §9.2 named edge cases (seq tie-break, Save-As stale path, deleted-on-disk) resolve correctly;
//   - FR-21: a clean save+close clears the buffer's recovery snapshots (no spurious recovery prompt).
//
// Class names contain "Session"/"Recovery" so the verify filter "Session|Recovery" resolves them. Uses a
// FakeTimeProvider-equivalent (ManualTimeProvider) so savedAt is deterministic (no DateTime.UtcNow).

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Session;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class SessionRecoveryReconcileTests
{
    private static (SessionService service, SessionStore store, FakeRecoveryFileSystem fs, TempAppDataPaths paths)
        NewEngine()
    {
        var paths = new TempAppDataPaths();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var fs = new FakeRecoveryFileSystem();
        var reconciler = new SessionReconciler(fs);
        var service = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        return (service, store, fs, paths);
    }

    // ---- (1) The primary gate: a simulated crash+restart restores the tab + cursor. ----

    [Fact]
    public async Task CrashThenRestartRestoresTheTabBufferAndCursor()
    {
        var (service, _, fs, paths) = NewEngine();
        using var _paths = paths;

        // Pre-crash: a dirty scratch buffer is auto-saved (FR-20). A scratch buffer has no on-disk file, so
        // dimension 3 is N/A — the restore is snapshot-only.
        const string tabId = "tab-scratch-1";
        var cursor = new CursorPosition(Line: 12, Column: 4);
        await service.SnapshotAsync(tabId, TabKind.Scratch, path: null, language: "plaintext",
            cursor, bufferText: "unsaved notes that must survive a crash", CancellationToken.None);

        // Persist the session index so the active tab id is recorded (the rebuildable hint).
        await service.PersistSessionAsync(openFolder: null, canonicalOpenFolder: null, activeTabId: tabId,
            tabs: [new SessionTab(tabId, TabKind.Scratch, null, "plaintext", IsDirty: true, cursor, RecoveryRef: tabId, Seq: 1)],
            CancellationToken.None);

        // ---- simulate a RESTART: a fresh engine over the SAME temp dir reconciles from disk. ----
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var fs2 = new FakeRecoveryFileSystem();
        var service2 = new SessionService(store2, new SessionReconciler(fs2), fs2, new ManualTimeProvider());

        SessionReconcileResult result = await service2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab tab = Assert.Single(result.Tabs);
        Assert.Equal(tabId, tab.TabId);
        Assert.Equal(tabId, result.ActiveTabId);
        Assert.Equal(TabKind.Scratch, tab.Kind);
        Assert.Equal("unsaved notes that must survive a crash", tab.RecoveredText);  // buffer survived.
        Assert.Equal(12, tab.Cursor!.Line);                                          // cursor survived.
        Assert.Equal(4, tab.Cursor.Column);
        Assert.False(tab.SurfaceConflict);                                           // a scratch buffer never conflicts.
    }

    [Fact]
    public async Task RestartRestoresAFileTabWithUnchangedDiskWithNoConflict()
    {
        var (service, _, fs, paths) = NewEngine();
        using var _paths = paths;

        // A dirty on-disk file: snapshot captures the baseline (the fake reports the on-disk file present
        // with hash "H1" at snapshot time, so the snapshot's baselineContentHash == H1).
        const string tabId = "tab-file-1";
        const string filePath = "/proj/notes.md";
        fs.SetProbe(filePath, new CurrentDiskFile(Exists: true, ContentHash: "H1", Mtime: System.DateTimeOffset.UnixEpoch));
        await service.SnapshotAsync(tabId, TabKind.File, filePath, "markdown",
            new CursorPosition(1, 1), "dirty edits over the on-disk file", CancellationToken.None);

        // Restart: disk is STILL H1 (unchanged) → dimension 3 = present-unchanged → no conflict (§9.2).
        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(filePath, new CurrentDiskFile(Exists: true, ContentHash: "H1", Mtime: System.DateTimeOffset.UnixEpoch));
        var service2 = new SessionService(new SessionStore(new RealAtomicFileStore(), paths),
            new SessionReconciler(fs2), fs2, new ManualTimeProvider());

        SessionReconcileResult result = await service2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab tab = Assert.Single(result.Tabs);
        Assert.Equal(ReconciliationAction.RecoverOrphan, tab.Action);   // no session entry persisted → orphan.
        Assert.False(tab.SurfaceConflict);                              // disk matches the snapshot baseline.
        Assert.Equal("dirty edits over the on-disk file", tab.RecoveredText);
    }

    // ---- (2) The restore-conflict gate: a changed-on-disk file surfaces a conflict (hash mismatch). ----

    [Fact]
    public async Task ChangedOnDiskFileSurfacesARestoreConflict()
    {
        var (service, _, fs, paths) = NewEngine();
        using var _paths = paths;

        const string tabId = "tab-file-conflict";
        const string filePath = "/proj/changed.md";
        // Baseline at snapshot time: H1.
        fs.SetProbe(filePath, new CurrentDiskFile(Exists: true, ContentHash: "H1", Mtime: System.DateTimeOffset.UnixEpoch));
        await service.SnapshotAsync(tabId, TabKind.File, filePath, "markdown",
            new CursorPosition(3, 1), "recovered buffer", CancellationToken.None);

        // Restart: disk now H2 (a sync daemon / the other machine changed it) → present-changed-externally
        // → SurfaceConflict (FR-19, B7) — never silently restore + arm a clobbering Save.
        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(filePath, new CurrentDiskFile(Exists: true, ContentHash: "H2-CHANGED", Mtime: System.DateTimeOffset.UnixEpoch.AddMinutes(5)));
        var service2 = new SessionService(new SessionStore(new RealAtomicFileStore(), paths),
            new SessionReconciler(fs2), fs2, new ManualTimeProvider());

        SessionReconcileResult result = await service2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab tab = Assert.Single(result.Tabs);
        Assert.True(tab.SurfaceConflict, "a hash mismatch on restore must surface the FR-19 restore-conflict.");
    }

    // ---- (3) §9.2 named edge cases. ----

    [Fact]
    public async Task DuplicateTabIdSnapshotsKeepTheHigherSeqAndDoNotConflictWhenOrderable()
    {
        // §9.2 same-machine crash-mid-write: two snapshots of one tabId. The higher seq wins; orderable →
        // no forced conflict. We write seq=1 then seq=2 for the SAME tabId (both files persist on disk).
        var (service, store, _, paths) = NewEngine();
        using var _paths = paths;

        const string tabId = "tab-dup";
        // seq is minted internally; write twice so two snapshot files for tab-dup co-exist.
        await service.SnapshotAsync(tabId, TabKind.Scratch, null, "plaintext", new CursorPosition(1, 1), "v1", CancellationToken.None);
        await service.SnapshotAsync(tabId, TabKind.Scratch, null, "plaintext", new CursorPosition(2, 2), "v2-newer", CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        var service2 = new SessionService(new SessionStore(new RealAtomicFileStore(), paths),
            new SessionReconciler(fs2), fs2, new ManualTimeProvider());
        SessionReconcileResult result = await service2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab tab = Assert.Single(result.Tabs);
        Assert.Equal("v2-newer", tab.RecoveredText);   // higher-seq snapshot won the tie-break.
        Assert.False(tab.SurfaceConflict);             // orderable by seq → not a forced conflict.
    }

    [Fact]
    public async Task SaveAsAfterSnapshotRestoresAtTheNewerSessionPathNotTheStaleSnapshotPath()
    {
        // §9.2 Save-As stale-path rule (R3-MA1): a Save-As updates session.json's path (with a HIGHER seq)
        // while the snapshot still holds the OLD path. PathAuthority follows the more-recently-persisted
        // entry, so restore uses the NEW path — never clobbers at the stale location.
        var (service, _, fs, paths) = NewEngine();
        using var _paths = paths;

        const string tabId = "tab-saveas";
        const string oldPath = "/proj/old-name.md";
        const string newPath = "/proj/new-name.md";

        // Snapshot at seq=1 with the OLD path.
        fs.SetProbe(oldPath, new CurrentDiskFile(Exists: true, ContentHash: "HX", Mtime: System.DateTimeOffset.UnixEpoch));
        fs.SetProbe(newPath, new CurrentDiskFile(Exists: true, ContentHash: "HY", Mtime: System.DateTimeOffset.UnixEpoch));
        await service.SnapshotAsync(tabId, TabKind.File, oldPath, "markdown", new CursorPosition(1, 1), "buf", CancellationToken.None);

        // Save-As: session entry at a HIGHER seq with the NEW path (snapshot seq was 1; session seq = 5).
        await service.PersistSessionAsync(openFolder: null, canonicalOpenFolder: null, activeTabId: tabId,
            tabs: [new SessionTab(tabId, TabKind.File, newPath, "markdown", IsDirty: true, new CursorPosition(1, 1), RecoveryRef: tabId, Seq: 5)],
            CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(newPath, new CurrentDiskFile(Exists: true, ContentHash: "HY", Mtime: System.DateTimeOffset.UnixEpoch));
        var service2 = new SessionService(new SessionStore(new RealAtomicFileStore(), paths),
            new SessionReconciler(fs2), fs2, new ManualTimeProvider());
        SessionReconcileResult result = await service2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab tab = Assert.Single(result.Tabs);
        Assert.Equal(newPath, tab.Path);   // PathAuthority followed the newer session entry — no stale clobber.
    }

    [Fact]
    public async Task DeletedOnDiskFileIsMarkedDeletedNotSilentlyResurrected()
    {
        // §9.2 deleted row: the on-disk file is gone at restore. The tab restores with the recovered buffer
        // but marked OnDiskFileDeleted (next Save is a prompted re-create, not a silent resurrection).
        var (service, _, fs, paths) = NewEngine();
        using var _paths = paths;

        const string tabId = "tab-deleted";
        const string filePath = "/proj/gone.md";
        fs.SetProbe(filePath, new CurrentDiskFile(Exists: true, ContentHash: "HZ", Mtime: System.DateTimeOffset.UnixEpoch));
        await service.SnapshotAsync(tabId, TabKind.File, filePath, "markdown", new CursorPosition(1, 1), "buf", CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem(); // no probe set → Exists:false → Deleted.
        var service2 = new SessionService(new SessionStore(new RealAtomicFileStore(), paths),
            new SessionReconciler(fs2), fs2, new ManualTimeProvider());
        SessionReconcileResult result = await service2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab tab = Assert.Single(result.Tabs);
        Assert.True(tab.OnDiskFileDeleted, "a deleted-on-disk file must be marked, not silently resurrected.");
        Assert.False(tab.SurfaceConflict);
    }

    // ---- (4) FR-21: a clean save+close clears the buffer's recovery snapshots. ----

    [Fact]
    public async Task CleanSaveCloseClearsRecoverySnapshotsSoNoSpuriousPromptNextLaunch()
    {
        var (service, _, _, paths) = NewEngine();
        using var _paths = paths;

        const string tabId = "tab-clean";
        await service.SnapshotAsync(tabId, TabKind.Scratch, null, "plaintext", new CursorPosition(1, 1), "buf", CancellationToken.None);

        // FR-21: clear on clean save+close.
        await service.ClearRecoveryAsync(tabId, CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        var service2 = new SessionService(new SessionStore(new RealAtomicFileStore(), paths),
            new SessionReconciler(fs2), fs2, new ManualTimeProvider());
        SessionReconcileResult result = await service2.LoadAndReconcileAsync(CancellationToken.None);

        Assert.Empty(result.Tabs);   // no recovery artifact survived a clean save+close.
    }
}
