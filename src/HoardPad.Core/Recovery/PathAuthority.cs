namespace HoardPad.Core.Recovery;

/// <summary>Which of {snapshot, session-entry} owns the authoritative <c>path</c> for a <c>tabId</c> (§9.2).</summary>
public enum PathSource
{
    /// <summary>The snapshot's embedded <c>path</c> is authoritative.</summary>
    Snapshot,

    /// <summary>The session entry's <c>path</c> is authoritative (it was persisted more recently — e.g. a Save-As).</summary>
    SessionEntry,
}

/// <summary>
/// The §9.2 "Save-As cannot strand a stale snapshot path" authority rule (R3-MA1 (MA6+MA10)).
/// </summary>
/// <remarks>
/// The recovery directory is the source of truth for buffer <i>bytes</i>, but <c>path</c> authority
/// follows whichever of {snapshot, session-entry} was persisted more recently — ordered by the same
/// monotonic per-machine <c>seq</c>. So a Save-As performed after the last snapshot (which updates
/// <c>session.json</c>'s <c>path</c> while the snapshot still holds the old <c>path</c>) does NOT cause
/// restore to clobber at the stale location. Absent a more-recent session entry, the snapshot's embedded
/// <c>path</c> remains authoritative (it was written atomically with its own metadata, whereas
/// <c>session.json</c> is a rebuildable hint that may lag). A stranded stale path is inherently a
/// single-machine event, so the per-machine <c>seq</c> orders it correctly.
/// </remarks>
public static class PathAuthority
{
    /// <summary>
    /// Decide which <c>path</c> wins, given the snapshot's <c>seq</c> and the session entry's last-persist
    /// <c>seq</c>. The session entry wins only when it is strictly more recent (a higher <c>seq</c>);
    /// otherwise — equal, older, or a missing session <c>seq</c> — the snapshot's embedded path holds.
    /// </summary>
    /// <param name="snapshotSeq">The snapshot's monotonic per-machine <c>seq</c>.</param>
    /// <param name="sessionEntrySeq">
    /// The session entry's <c>seq</c> at its last persist (§9.2: session entries carry the same <c>seq</c>),
    /// or <c>null</c> when the session entry carries no <c>seq</c> (then the snapshot stays authoritative).
    /// </param>
    public static PathSource Resolve(long snapshotSeq, long? sessionEntrySeq) =>
        sessionEntrySeq is { } session && session > snapshotSeq
            ? PathSource.SessionEntry
            : PathSource.Snapshot;
}
