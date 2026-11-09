// SessionService.cs — the App-facing session/recovery service (FR-19/FR-20/FR-21, PRD §9.2).
//
// Composes SessionStore (durable I/O) + SessionReconciler (drives the Core §9.2 table) into the three
// operations the workspace needs:
//   - LoadAndReconcileAsync : startup — read session.json + scan the recovery dir, drive the Core
//                             reconciliation, return the tabs to materialize.
//   - SnapshotAsync         : auto-save (FR-20) — write ONE self-describing recovery snapshot atomically
//                             into the machine-local recovery dir, minting the next monotonic per-machine
//                             seq, capturing the on-disk baseline hash+mtime (B7) for a file/hoard tab.
//   - ClearRecoveryAsync    : clean save+close (FR-21) — delete the buffer's recovery snapshots so no
//                             spurious recovery prompt appears next launch.
//   - PersistSessionAsync   : write the session.json index (the rebuildable hint).
//
// The monotonic per-machine seq is owned here and persisted in session.json (NextSeq). Every timestamp
// (savedAt) comes from the INJECTED TimeProvider — never DateTime.UtcNow (architecture §3, C-9). All
// operations run off the UI thread.
//
// F-FS1c DURABILITY FIX — the Save-vs-tick snapshot race: the periodic auto-save tick's SnapshotAsync
// (write-then-prune, called for every dirty tab) and the manual-Save path's ClearRecoveryAsync run on
// independent tasks with no shared coordination. Without the fix below, a tick's write could START before
// a Save's clear (the user just persisted the real file, so its recovery snapshot should vanish) but its
// file WRITE could land AFTER the clear — resurrecting a stale snapshot that next launch's restore could
// then offer/load over the user's freshly-saved content. Closed with:
//   1. A per-tabId SemaphoreSlim (_tabLocks) so a write+prune and a clear for the SAME tab can never
//      physically interleave their file operations.
//   2. A per-tabId generation counter (_tabGeneration), bumped by every clear. A write captures the
//      generation it observed when it BEGAN, and re-checks it (under the per-tab lock, immediately before
//      committing) right before touching disk — if a clear committed in between, the generation no longer
//      matches and the write is dropped as stale rather than resurrecting what the clear just removed. A
//      lock alone cannot express this: a lock only prevents two operations from running AT THE SAME TIME,
//      not "this write's premise (the tab was still dirty pre-clear) is now stale" — the generation check
//      is what lets a write that started before a clear notice the clear happened and self-abort, instead
//      of blindly writing once the lock frees up.

using System.Collections.Concurrent;
using HoardPad.Core.Recovery;

namespace HoardPad.App.Session;

/// <summary>
/// The session/recovery service (FR-19/FR-20/FR-21). Owns the monotonic per-machine <c>seq</c>, drives
/// the pure Core §9.2 reconciliation through <see cref="SessionReconciler"/>, and writes durable
/// snapshots + the session index atomically through <see cref="SessionStore"/>.
/// </summary>
internal sealed class SessionService
{
    private readonly SessionStore _store;
    private readonly SessionReconciler _reconciler;
    private readonly IRecoveryFileSystem _fs;
    private readonly TimeProvider _time;
    private readonly object _seqGate = new();

    // The next per-machine seq to mint. Seeded from session.json on load; advanced on every snapshot write.
    private long _nextSeq = 1;

    // F-FS1c: the per-tabId write/clear mutual-exclusion + staleness generation (see the file-header note
    // above). Both dictionaries are keyed by tabId and grow for the lifetime of this (singleton) service,
    // with NO eviction on tab close: MainWindowViewModel.CloseTabCoreAsync removes the tab from the shell's
    // Tabs collection directly and never calls back into WorkspaceService/SessionService, so there is no
    // clean close hook at this layer to evict an entry from (and none is invented here — evicting from a
    // dictionary another operation may still be mid-flight against would just trade this debt for a new
    // race). Accepted v1 debt: bounded by session open/close churn over one process's run (~100 bytes per
    // tabId ever seen — a SemaphoreSlim + a long + dictionary overhead), acceptable for this app's
    // personal-use target. Revisit if/when a tab-close hook lands at this layer.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tabLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _tabGeneration = new(StringComparer.Ordinal);

    /// <summary>
    /// Test-only synchronization seam (F-FS1c interleave test): when set, <see cref="SnapshotAsync"/>
    /// awaits it immediately after preparing a snapshot write — AFTER capturing the write's generation but
    /// BEFORE acquiring the per-tab lock — letting a test deterministically pause a tick-WRITE at exactly
    /// the point a racing Save-CLEAR must be able to run to completion. Null (the production default) is a
    /// no-op: the hot path never awaits anything here.
    /// </summary>
    internal Func<Task>? WriteSyncPointForTest { get; set; }

    private SemaphoreSlim GetTabLock(string tabId) => _tabLocks.GetOrAdd(tabId, static _ => new SemaphoreSlim(1, 1));

    public SessionService(
        SessionStore store,
        SessionReconciler reconciler,
        IRecoveryFileSystem fs,
        TimeProvider time)
    {
        _store = store;
        _reconciler = reconciler;
        _fs = fs;
        _time = time;
    }

    /// <summary>
    /// Startup reconcile (FR-19): read the session index + scan the recovery directory, then drive the pure
    /// Core §9.2 table. Seeds the per-machine seq counter from the persisted <c>NextSeq</c>. Returns the
    /// tabs to materialize.
    /// </summary>
    public async Task<SessionReconcileResult> LoadAndReconcileAsync(CancellationToken ct)
    {
        SessionState session = await _store.ReadSessionAsync(ct).ConfigureAwait(false);
        IReadOnlyList<RecoverySnapshot> snapshots = await _store.ReadSnapshotsAsync(ct).ConfigureAwait(false);

        lock (_seqGate)
            _nextSeq = Math.Max(_nextSeq, session.NextSeq);

        return await _reconciler.ReconcileAsync(session, snapshots, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write ONE recovery snapshot for a dirty/scratch buffer (FR-20). Mints the next per-machine seq,
    /// captures the on-disk baseline hash+mtime (B7) for a file/hoard tab so the next restore can detect an
    /// external change (FR-19), and writes atomically into the machine-local recovery dir. Returns the
    /// snapshot ref (the session index's recoveryRef hint), or <c>null</c> when the write was dropped as
    /// STALE — see the F-FS1c file-header note: a <see cref="ClearRecoveryAsync"/> for the same tab
    /// committed after this write began, so writing now would resurrect exactly what that clear removed.
    /// </summary>
    public async Task<string?> SnapshotAsync(
        string tabId,
        TabKind kind,
        string? path,
        string language,
        CursorPosition? cursor,
        string bufferText,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);

        // F-FS1c: the generation this write observed when it began — re-checked below, immediately before
        // committing, under the per-tab lock.
        long capturedGeneration = _tabGeneration.GetOrAdd(tabId, 0);

        long seq = NextSeq();

        // B7: capture the on-disk baseline (hash+mtime) at snapshot time, for a file/hoard tab. A scratch
        // buffer has no on-disk baseline. If the on-disk file is absent now (never-saved-yet path), the
        // baseline stays null — dimension 3 then treats restore as "no baseline to compare".
        string? baselineHash = null;
        DateTimeOffset? baselineMtime = null;
        if (kind != TabKind.Scratch && path is not null)
        {
            CurrentDiskFile current = await _fs.ProbeAsync(path, kind, ct).ConfigureAwait(false);
            if (current.Exists)
            {
                baselineHash = current.ContentHash;
                baselineMtime = current.Mtime;
            }
        }

        var snapshot = new RecoverySnapshot(
            TabId: tabId,
            Kind: kind,
            Path: path,
            Language: language,
            Cursor: cursor,
            SavedAt: _time.GetUtcNow(),
            Seq: seq,
            BaselineContentHash: baselineHash,
            BaselineMtime: baselineMtime,
            ContentBase64: SessionStore.EncodeContent(bufferText));

        if (WriteSyncPointForTest is not null)
            await WriteSyncPointForTest().ConfigureAwait(false);

        SemaphoreSlim tabLock = GetTabLock(tabId);
        await tabLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // F-FS1c: a clear committed for this tab since this write began — drop it rather than
            // resurrect a snapshot the clear just removed (see the file-header note).
            if (_tabGeneration.GetOrAdd(tabId, 0) != capturedGeneration)
                return null;

            string snapshotPath = await _store.WriteSnapshotAsync(snapshot, ct).ConfigureAwait(false);

            // F-FS1c fix 2: prune this tab's now-superseded older snapshots so a long dirty session at the
            // periodic auto-save cadence never accumulates more than ~1 file per tab. Write-then-prune-
            // older: the new snapshot above is already durable, so there is no window with zero recovery
            // data for this tab. Stays under the same per-tab lock as the write above (F-FS1c: PRUNE is one
            // of the three paths the lock must cover).
            await Task.Run(() => _store.PruneSupersededSnapshots(tabId, snapshotPath), ct).ConfigureAwait(false);

            return snapshotPath;
        }
        finally
        {
            tabLock.Release();
        }
    }

    /// <summary>
    /// Clear a buffer's recovery snapshots on clean save+close (FR-21). Idempotent. F-FS1c: bumps the
    /// tab's generation counter (under the per-tab lock, so it can never interleave with a racing
    /// <see cref="SnapshotAsync"/> write+prune for the same tab) BEFORE deleting — a write that captured
    /// the OLD generation and is still in flight will see the mismatch and drop its write instead of
    /// resurrecting a snapshot this clear is about to remove.
    /// </summary>
    public async Task ClearRecoveryAsync(string tabId, CancellationToken ct)
    {
        SemaphoreSlim tabLock = GetTabLock(tabId);
        await tabLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _tabGeneration.AddOrUpdate(tabId, addValue: 1, updateValueFactory: static (_, generation) => generation + 1);

            // Offload the synchronous File.Delete enumeration off the calling thread (REVISE-5 fix: the
            // caller is the UI thread; the delete loop must not block the dispatcher even if the recovery
            // dir is large).
            await Task.Run(() => _store.ClearSnapshots(tabId), ct).ConfigureAwait(false);
        }
        finally
        {
            tabLock.Release();
        }
    }

    /// <summary>
    /// Persist the session.json index (the rebuildable hint, §9.2). Stamps the current per-machine seq into
    /// each tab entry's <c>Seq</c> (so PathAuthority can follow the more-recently-persisted of {snapshot,
    /// session-entry} — the Save-As stale-path rule) and records the next-seq counter for the next launch.
    /// </summary>
    /// <param name="openFolder">
    /// The raw open-folder path. Always passed null since FS-1 (docs/tasklist.md) — the open folder is
    /// derived fresh every launch rather than persisted; the parameter is retained for schema compatibility.
    /// </param>
    /// <param name="canonicalOpenFolder">The canonicalised form of <paramref name="openFolder"/>. Always null for the same reason.</param>
    /// <param name="activeTabId">The tab id to focus on restore.</param>
    /// <param name="tabs">The open-tab entries.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PersistSessionAsync(
        string? openFolder,
        string? canonicalOpenFolder,
        string? activeTabId,
        IReadOnlyList<SessionTab> tabs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        long next;
        lock (_seqGate)
            next = _nextSeq;

        var state = new SessionState(
            SessionState.CurrentSchemaVersion, openFolder, canonicalOpenFolder, activeTabId, tabs, next);
        await _store.WriteSessionAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>Mint the next monotonic per-machine seq (the §9.2 same-machine tie-break / path-authority key).</summary>
    public long NextSeq()
    {
        lock (_seqGate)
            return _nextSeq++;
    }
}
