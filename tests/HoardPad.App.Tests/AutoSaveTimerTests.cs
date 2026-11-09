// AutoSaveTimerTests.cs — F-FS1c: the periodic auto-save timer.
//
// Proves three things:
//   1. WorkspaceService.SnapshotAllDirtyAsync — the timer's tick target — writes a recovery snapshot for
//      EVERY dirty tab (not just the active one) and skips clean tabs, reusing the exact
//      SessionService.SnapshotAsync write path SnapshotActiveAsync and crash-recovery already rely on.
//   2. A snapshot written by SnapshotAllDirtyAsync ROUND-TRIPS through the real startup restore
//      (WorkspaceService.RestoreAsync -> SessionReconciler, the same pipeline crash-recovery uses) on a
//      fresh WorkspaceService instance pointed at the same on-disk session/recovery directory.
//   3. StartAutoSaveTimer/StopAutoSaveTimer actually wire up (and tear down) a DispatcherTimer at the
//      requested interval — the "timer is wired to the real method" seam, since firing a real 30s+ tick in
//      a test is impractical.
//
// Plus the FIX-CYCLE additions below (2 Criticals + 3 Warnings from two review gates):
//   4. Fix 1 — StartAutoSaveTimer clamps a zero/negative/out-of-range requested interval into
//      [MinAutoSaveSeconds, MaxAutoSaveSeconds] and never throws (a raw negative TimeSpan would otherwise
//      crash Avalonia's DispatcherTimer.Interval setter at every startup until settings.json is hand-fixed).
//   5. Fix 2 — repeated auto-save ticks for the SAME dirty tab leave at most ~1 snapshot file for that tab
//      (not one per tick), and the ONE that survives is the LATEST edit, round-tripping correctly through
//      the real startup restore.
//   6. Fix 3 — two overlapping auto-save batches never run concurrently: the second is a no-op while the
//      first is still in flight (driven directly via the re-entrancy gate test seam).
//   7. Fix 4 — StopAutoSaveTimerAsync DRAINS an in-flight batch before returning (does not merely stop
//      future ticks), closing the race with a shutdown-time PersistAsync.
//   8. Fix 5 — App.ApplyAutoSaveIntervalChange (the SettingsChanged live-push wiring) re-wires an
//      ALREADY-RUNNING timer to a settings-changed interval, clamped exactly like Fix 1.

using System;
using System.IO;
using System.Linq;
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

public sealed class AutoSaveTimerTests
{
    private sealed class FixtureFile : IDisposable
    {
        public string Path { get; }
        public string Directory { get; }

        public FixtureFile(string name)
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hoardpad-autosave-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, name);
            File.WriteAllText(Path, "on-disk baseline");
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

    private static (WorkspaceService Ws, MainWindowViewModel Shell) NewWorkspace(FakeRecoveryFileSystem fs, TempAppDataPaths paths)
    {
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var shell = new MainWindowViewModel();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var conflictPrompt = new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk);
        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler,
            new EchoingAiSessionBinder(),
            conflictPrompt, MakeEditorFactory(), MakeChecklistFactory(), MakeSettingsTabFactory(),
            new ScriptedOpenFolderPrompt(null), StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        return (ws, shell);
    }

    [Fact]
    public async Task SnapshotAllDirtyAsyncSnapshotsEveryDirtyTabAndSkipsCleanTabs()
    {
        using var paths = new TempAppDataPaths();
        using var fixtureA = new FixtureFile("a.txt"); // stays clean.
        using var fixtureB = new FixtureFile("b.txt"); // becomes dirty.
        using var fixtureC = new FixtureFile("c.txt"); // becomes dirty, not the active tab.

        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, MainWindowViewModel shell) = NewWorkspace(fs, paths);

        await ws.OpenFolderTreeFileAsync(fixtureA.Path, CancellationToken.None);
        await ws.OpenFolderTreeFileAsync(fixtureB.Path, CancellationToken.None);
        await ws.OpenFolderTreeFileAsync(fixtureC.Path, CancellationToken.None); // becomes the active tab.

        DocumentTab tabB = Assert.Single(shell.Tabs, t => t.Editor?.Document.Path == fixtureB.Path);
        tabB.Editor!.Document.Text = "unsaved edit B";
        tabB.Editor.IsDirty = true;

        DocumentTab tabC = Assert.Single(shell.Tabs, t => t.Editor?.Document.Path == fixtureC.Path);
        tabC.Editor!.Document.Text = "unsaved edit C";
        tabC.Editor.IsDirty = true;

        // This is the SAME call the periodic timer's tick fires — invoked directly here so the test does
        // not depend on real wall-clock elapsed time.
        int snapshotted = await ws.SnapshotAllDirtyAsync(CancellationToken.None);

        Assert.Equal(2, snapshotted); // B and C, not clean A.

        string tabAId = Assert.Single(shell.Tabs, t => t.Editor?.Document.Path == fixtureA.Path).TabId;
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var snapshots = await store.ReadSnapshotsAsync(CancellationToken.None);
        Assert.DoesNotContain(snapshots, s => s.TabId == tabAId);
        Assert.Contains(snapshots, s => s.TabId == tabB.TabId);
        Assert.Contains(snapshots, s => s.TabId == tabC.TabId);
    }

    [Fact]
    public async Task PeriodicSnapshotFiresBetweenExplicitSavesAndRoundTripsThroughRealStartupRestore()
    {
        // Reproduces the reported gap: a dirty tab that is NEVER explicitly saved must still survive an
        // unexpected termination — because the periodic tick (simulated here by calling the timer's exact
        // tick target directly) wrote a snapshot BETWEEN saves.
        using var paths = new TempAppDataPaths();
        using var fixture = new FixtureFile("dirty.txt");

        var fs1 = new FakeRecoveryFileSystem();
        (WorkspaceService ws1, MainWindowViewModel shell1) = NewWorkspace(fs1, paths);
        await ws1.OpenFolderTreeFileAsync(fixture.Path, CancellationToken.None);

        DocumentTab tab = Assert.Single(shell1.Tabs, t => t.Editor?.Document.Path == fixture.Path);
        tab.Editor!.Document.Text = "in-progress edit, never explicitly saved";
        tab.Editor.IsDirty = true;

        // Simulate the periodic timer firing between explicit saves — no Save was ever called.
        int snapshotted = await ws1.SnapshotAllDirtyAsync(CancellationToken.None);
        Assert.Equal(1, snapshotted);

        await ws1.PersistAsync(CancellationToken.None); // the session.json index (the rebuildable hint).

        // Fresh process/instance restore (the real crash-recovery pipeline), pointed at the SAME on-disk
        // session/recovery directory.
        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(fixture.Path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        (WorkspaceService ws2, MainWindowViewModel shell2) = NewWorkspace(fs2, paths);

        await ws2.RestoreAsync(CancellationToken.None);

        DocumentTab restored = Assert.Single(shell2.Tabs, t => t.Editor?.Document.Path == fixture.Path);
        Assert.Equal("in-progress edit, never explicitly saved", restored.Editor!.Document.Text);
        Assert.True(restored.Editor.IsDirty); // a recovered buffer is unsaved-edits until the user saves.
    }

    [Fact]
    public void StartAutoSaveTimerWiresARunningTimerAtTheGivenIntervalAndStopReleasesIt()
    {
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        Assert.False(ws.IsAutoSaveTimerRunning);
        Assert.Null(ws.AutoSaveTimerInterval);

        ws.StartAutoSaveTimer(TimeSpan.FromSeconds(7));

        Assert.True(ws.IsAutoSaveTimerRunning);
        Assert.Equal(TimeSpan.FromSeconds(7), ws.AutoSaveTimerInterval);

        ws.StopAutoSaveTimer();

        Assert.False(ws.IsAutoSaveTimerRunning);
        Assert.Null(ws.AutoSaveTimerInterval);

        // Stopping twice is a no-op (no leaked/duplicate timer state).
        ws.StopAutoSaveTimer();
        Assert.False(ws.IsAutoSaveTimerRunning);
    }

    [Fact]
    public void StartAutoSaveTimerWithNoIntervalDefaultsToDefaultAutoSaveIntervalSeconds()
    {
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        ws.StartAutoSaveTimer();

        Assert.Equal(TimeSpan.FromSeconds(WorkspaceService.DefaultAutoSaveIntervalSeconds), ws.AutoSaveTimerInterval);

        ws.StopAutoSaveTimer();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(9_999)]
    public void StartAutoSaveTimerClampsAnOutOfRangeIntervalAndNeverThrows(int requestedSeconds)
    {
        // Fix 1 (CRITICAL): AutoSaveSeconds == 0 (a missing/partial settings.json's CLR default) must never
        // become TimeSpan.Zero (a hot-spinning DispatcherTimer), and a negative value must never reach
        // Avalonia's DispatcherTimer.Interval setter raw (it throws ArgumentOutOfRangeException, which would
        // crash the app at every startup until the file is hand-fixed).
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        Exception? thrown = Record.Exception(() => ws.StartAutoSaveTimer(TimeSpan.FromSeconds(requestedSeconds)));

        Assert.Null(thrown); // never throws, not even for a raw-negative interval.
        Assert.NotNull(ws.AutoSaveTimerInterval);
        Assert.InRange(
            ws.AutoSaveTimerInterval!.Value.TotalSeconds,
            HoardPadSettings.MinAutoSaveSeconds, HoardPadSettings.MaxAutoSaveSeconds);

        ws.StopAutoSaveTimer();
    }

    [Fact]
    public async Task RepeatedAutoSaveTicksForTheSameDirtyTabLeaveAtMostOneSnapshotFileAndRoundTripTheLatestEdit()
    {
        // Fix 2 (CRITICAL): a dirty tab left open across many periodic ticks must not accumulate one
        // snapshot file per tick — at most ~1 (the latest) survives per tab, and that ONE survivor must still
        // be the LATEST edit (not an arbitrary/older one — the bug this guards against is pruning the wrong
        // file), round-tripping correctly through the real startup restore.
        using var paths = new TempAppDataPaths();
        using var fixture = new FixtureFile("many-ticks.txt");

        var fs1 = new FakeRecoveryFileSystem();
        (WorkspaceService ws1, MainWindowViewModel shell1) = NewWorkspace(fs1, paths);
        await ws1.OpenFolderTreeFileAsync(fixture.Path, CancellationToken.None);

        DocumentTab tab = Assert.Single(shell1.Tabs, t => t.Editor?.Document.Path == fixture.Path);
        tab.Editor!.IsDirty = true;

        const int tickCount = 5;
        for (int i = 0; i < tickCount; i++)
        {
            tab.Editor.Document.Text = $"in-progress edit #{i}";
            int snapshotted = await ws1.SnapshotAllDirtyAsync(CancellationToken.None); // the same call the timer's tick target fires.
            Assert.Equal(1, snapshotted);
        }

        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var snapshotsForTab = (await store.ReadSnapshotsAsync(CancellationToken.None))
            .Where(s => s.TabId == tab.TabId)
            .ToList();

        RecoverySnapshot survivor = Assert.Single(snapshotsForTab); // at most ~1 file, not one per tick.
        Assert.Equal($"in-progress edit #{tickCount - 1}", SessionStore.DecodeContent(survivor)); // the LATEST edit.

        await ws1.PersistAsync(CancellationToken.None);

        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(fixture.Path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        (WorkspaceService ws2, MainWindowViewModel shell2) = NewWorkspace(fs2, paths);
        await ws2.RestoreAsync(CancellationToken.None);

        DocumentTab restored = Assert.Single(shell2.Tabs, t => t.Editor?.Document.Path == fixture.Path);
        Assert.Equal($"in-progress edit #{tickCount - 1}", restored.Editor!.Document.Text);
        Assert.True(restored.Editor.IsDirty);
    }

    [Fact]
    public async Task OverlappingAutoSaveBatchesTheSecondIsSkippedWhileTheFirstIsInFlight()
    {
        // Fix 3 (WARNING): a tick that fires while the prior batch is still awaiting must be a no-op — never
        // overlapping write batches. Driven directly through the re-entrancy gate test seam (holding the gate
        // simulates a batch genuinely in flight, without racing a real snapshot write).
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        Assert.True(ws.TryAcquireAutoSaveGateForTest()); // simulate tick #1 already in flight.

        bool ranWhileBusy = await ws.RunAutoSaveBatchIfIdleAsync(CancellationToken.None);
        Assert.False(ranWhileBusy); // tick #2 is skipped — the gate was already held.

        ws.ReleaseAutoSaveGateForTest(); // tick #1 finishes.

        bool ranAfterRelease = await ws.RunAutoSaveBatchIfIdleAsync(CancellationToken.None);
        Assert.True(ranAfterRelease); // now free to run.
    }

    [Fact]
    public async Task StopAutoSaveTimerAsyncDrainsAnInFlightBatchBeforeReturning()
    {
        // Fix 4 (WARNING): StopAutoSaveTimer alone only stops FUTURE ticks; StopAutoSaveTimerAsync must
        // additionally DRAIN a batch already in flight before it completes, so a caller (e.g. the shutdown
        // handler's own PersistAsync) can never race an in-flight auto-save write. Proven non-vacuously by
        // holding the gate (simulating an in-flight batch), observing the drain task does NOT complete while
        // held, then releasing and observing it completes immediately after.
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        ws.StartAutoSaveTimer(TimeSpan.FromSeconds(5));
        Assert.True(ws.TryAcquireAutoSaveGateForTest()); // simulate a tick already in flight.

        Task stopTask = ws.StopAutoSaveTimerAsync();
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted); // must not complete while the in-flight batch still holds the gate.

        ws.ReleaseAutoSaveGateForTest(); // the in-flight batch finishes.
        await stopTask; // the drain now completes.

        Assert.False(ws.IsAutoSaveTimerRunning);
    }

    [Fact]
    public async Task SaveClearWinsAgainstARacingTickWriteForTheSameTabNoResurrection()
    {
        // F-FS1c DURABILITY FIX (frozen oracle): forces the EXACT problematic interleave deterministically —
        // a periodic tick's snapshot WRITE for tab X captures its intent to write (and probes the on-disk
        // baseline) BEFORE the manual-Save CLEAR for the SAME tab runs to completion, but the WRITE's actual
        // file write would otherwise land AFTER the CLEAR. SessionService.WriteSyncPointForTest pauses the
        // WRITE at exactly that point — never a timing-based race — so the CLEAR is guaranteed to commit
        // first every time this test runs.
        //
        // Post-condition (the invariant this guards): after a Save-CLEAR races a tick-WRITE for the same
        // tab, NO snapshot for that tab survives — the clear wins; the stale in-flight write is dropped
        // rather than resurrecting what the clear just removed.
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var session = new SessionService(store, reconciler, fs, new ManualTimeProvider());

        const string tabId = "tab-under-race";

        // Prime an existing (older) snapshot for this tab, so the racing CLEAR has real durable state to
        // remove — mirrors production: a tab that has already been ticked once, then explicitly saved.
        await session.SnapshotAsync(
            tabId, TabKind.File, null, "Plain Text", null, "older content", CancellationToken.None);

        var writeGate = new TaskCompletionSource();
        session.WriteSyncPointForTest = () => writeGate.Task;

        // Start the racing tick-WRITE: it captures its write intent, then blocks at the sync point —
        // BEFORE it has touched any snapshot file.
        Task<string?> raceWriteTask = session.SnapshotAsync(
            tabId, TabKind.File, null, "Plain Text", null, "stale in-flight tick content", CancellationToken.None);

        // The Save-CLEAR runs to completion WHILE the tick-WRITE is still paused mid-flight.
        await session.ClearRecoveryAsync(tabId, CancellationToken.None);

        // Release the paused tick-WRITE now that the clear has already committed.
        writeGate.SetResult();
        await raceWriteTask;

        var survivors = (await store.ReadSnapshotsAsync(CancellationToken.None))
            .Where(s => s.TabId == tabId)
            .ToList();
        Assert.Empty(survivors); // the clear must win — no resurrected stale snapshot for this tab.
    }

    [Fact]
    public async Task ConcurrentTickWritesForTheSameTabSerializeUnderThePerTabLockLeavingExactlyOneSurvivor()
    {
        // F-FS1c per-tab lock coverage (defense-in-depth beyond the App-layer re-entrancy gate that
        // normally keeps one auto-save batch from overlapping another): TWO overlapping SnapshotAsync calls
        // for the SAME tab, with NO clear racing them, must never corrupt recovery state. Both calls are
        // paused at the sync-point seam BEFORE either has acquired the per-tab lock, then released together
        // — forcing their write-then-prune pairs to contend for the same SemaphoreSlim(1,1). Because
        // PruneSupersededSnapshots deletes every OTHER file for the tab and keeps only the file its own
        // write just created, the invariant under test is: exactly ONE snapshot survives regardless of
        // which of the two wins the lock race first — never zero (a prune racing ahead of the other's
        // still-durable write) and never two (a prune that ran before the other write arrived).
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var session = new SessionService(store, reconciler, fs, new ManualTimeProvider());

        const string tabId = "tab-concurrent-writes";

        // Prime an existing (older) snapshot so a losing prune has real durable state to remove.
        await session.SnapshotAsync(
            tabId, TabKind.File, null, "Plain Text", null, "older content", CancellationToken.None);

        var gate = new TaskCompletionSource();
        session.WriteSyncPointForTest = () => gate.Task;

        // Neither call is awaited yet: FakeRecoveryFileSystem.ProbeAsync completes synchronously, so both
        // calls run synchronously up to the (genuinely suspending) sync-point await and are guaranteed
        // paused there — never yet holding the per-tab lock — before the gate is released below.
        Task<string?> writeA = session.SnapshotAsync(
            tabId, TabKind.File, null, "Plain Text", null, "concurrent write A", CancellationToken.None);
        Task<string?> writeB = session.SnapshotAsync(
            tabId, TabKind.File, null, "Plain Text", null, "concurrent write B", CancellationToken.None);

        gate.SetResult();
        await Task.WhenAll(writeA, writeB);

        // Neither is dropped as stale — no ClearRecoveryAsync raced them, so both captured the same
        // (unchanged) generation and both commit.
        Assert.NotNull(writeA.Result);
        Assert.NotNull(writeB.Result);

        var survivors = (await store.ReadSnapshotsAsync(CancellationToken.None))
            .Where(s => s.TabId == tabId)
            .ToList();

        RecoverySnapshot survivor = Assert.Single(survivors); // never zero, never two.
        Assert.True(
            SessionStore.DecodeContent(survivor) is "concurrent write A" or "concurrent write B",
            "the survivor must be one of the two writes' real content, not the primed older snapshot and not corrupted.");
    }

    [Fact]
    public async Task ClearRecoveryAsyncForATabWithNoPriorSnapshotIsAHarmlessNoOp()
    {
        // F-FS1c idempotency (SessionService.ClearRecoveryAsync's own doc comment: "Idempotent"): clearing a
        // tab that was never snapshotted — e.g. opened and saved before the first auto-save tick ever fired
        // — must not throw and must not conjure a stray snapshot into existence.
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var session = new SessionService(store, reconciler, fs, new ManualTimeProvider());

        const string tabId = "tab-never-snapshotted";

        Exception? thrown = await Record.ExceptionAsync(
            () => session.ClearRecoveryAsync(tabId, CancellationToken.None));

        Assert.Null(thrown);

        var survivors = (await store.ReadSnapshotsAsync(CancellationToken.None))
            .Where(s => s.TabId == tabId)
            .ToList();
        Assert.Empty(survivors);
    }

    [Fact]
    public async Task StopAutoSaveTimerAsyncDrainCanBeBoundedWithATimeoutAndTheTimerHasAlreadyStoppedRegardless()
    {
        // F-FS1c shutdown-drain bound: App.axaml.cs's ShutdownRequested handler wraps
        // StopAutoSaveTimerAsync() in Task.WaitAsync(ShutdownAutoSaveDrainTimeout) so a batch stuck draining
        // (e.g. a very large document on slow disk) cannot hang the OS close forever. Proves the mechanism
        // that bound relies on: StopAutoSaveTimerCore() (future ticks stopped + the DispatcherTimer
        // released) already ran SYNCHRONOUSLY as the first line of StopAutoSaveTimerAsync, so a timed-out
        // drain still leaves the timer genuinely stopped — only "wait for an in-flight batch" is bounded.
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        ws.StartAutoSaveTimer(TimeSpan.FromSeconds(5));
        Assert.True(ws.TryAcquireAutoSaveGateForTest()); // simulate a batch stuck in flight (never released).

        await Assert.ThrowsAsync<TimeoutException>(
            () => ws.StopAutoSaveTimerAsync().WaitAsync(TimeSpan.FromMilliseconds(50)));

        Assert.False(ws.IsAutoSaveTimerRunning); // stopped synchronously regardless of the drain timing out.
        Assert.Null(ws.AutoSaveTimerInterval);

        ws.ReleaseAutoSaveGateForTest(); // let the (still-running, orphaned-in-this-test) drain finish.
    }

    [Fact]
    public async Task DisposeWhileATimedOutDrainIsStillPendingDoesNotThrowAndTheAbandonedDrainCompletesCleanly()
    {
        // The F-FS1c shutdown Critical (Gate-1 hostile review): App.axaml.cs's ShutdownRequested handler
        // bounds StopAutoSaveTimerAsync() with Task.WaitAsync(timeout) — a timeout ABANDONS the drain task,
        // it does not cancel it (Task.WaitAsync semantics). The abandoned drain (still awaiting
        // _autoSaveGate) can therefore still be pending when provider.Dispose() -> WorkspaceService.Dispose()
        // runs on the SECOND ShutdownRequested call. Proves that interleave is safe: Dispose() must not
        // dispose _autoSaveGate while the abandoned drain (or the "stuck" batch's own release) could still
        // reference it — else either side throws an unobserved ObjectDisposedException.
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        ws.StartAutoSaveTimer(TimeSpan.FromSeconds(5));
        Assert.True(ws.TryAcquireAutoSaveGateForTest()); // simulate a batch stuck in flight past the shutdown bound.

        // Mirror App.axaml.cs's timed-out drain: the outer WaitAsync(timeout) abandons the inner task.
        Task drainTask = ws.StopAutoSaveTimerAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => drainTask.WaitAsync(TimeSpan.FromMilliseconds(50)));
        Assert.False(drainTask.IsCompleted); // still pending — the abandoned drain genuinely still awaits the gate.

        // Dispose runs on the second ShutdownRequested call, exactly as production does, WHILE the abandoned
        // drain above is still pending.
        Exception? disposeThrew = Record.Exception(ws.Dispose);
        Assert.Null(disposeThrew);

        // Let the "stuck" batch finish (mirrors its own SnapshotAllDirtyAsync eventually completing) — this
        // is what wakes the abandoned drain's `await _autoSaveGate.WaitAsync()` / trailing
        // `_autoSaveGate.Release()`.
        ws.ReleaseAutoSaveGateForTest();

        // Bounded completion (no hang) AND no exception from either the batch's release or the abandoned
        // drain's own release racing the Dispose() call above.
        Task raced = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(drainTask, raced);
        Exception? drainThrew = await Record.ExceptionAsync(() => drainTask);
        Assert.Null(drainThrew);
    }

    [Fact]
    public void ApplyAutoSaveIntervalChangeReWiresTheRunningTimerToTheNewClampedInterval()
    {
        // Fix 5 (WARNING): App.ApplyAutoSaveIntervalChange is the SettingsChanged live-push wiring — an
        // in-session AutoSaveSeconds change must re-wire the ALREADY-RUNNING timer, not merely persist and
        // wait for a relaunch. Also proves the Fix 1 clamp travels through this seam.
        using var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        (WorkspaceService ws, _) = NewWorkspace(fs, paths);

        ws.StartAutoSaveTimer(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), ws.AutoSaveTimerInterval);

        HoardPadSettings updated = HoardPadSettings.Default with { AutoSaveSeconds = 20 };
        App.ApplyAutoSaveIntervalChange(ws, updated);

        Assert.True(ws.IsAutoSaveTimerRunning);
        Assert.Equal(TimeSpan.FromSeconds(20), ws.AutoSaveTimerInterval);

        HoardPadSettings outOfRange = updated with { AutoSaveSeconds = 0 };
        App.ApplyAutoSaveIntervalChange(ws, outOfRange);
        Assert.InRange(
            ws.AutoSaveTimerInterval!.Value.TotalSeconds,
            HoardPadSettings.MinAutoSaveSeconds, HoardPadSettings.MaxAutoSaveSeconds);

        ws.StopAutoSaveTimer();
    }
}
