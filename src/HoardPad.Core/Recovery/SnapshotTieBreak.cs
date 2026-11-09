namespace HoardPad.Core.Recovery;

/// <summary>
/// The minimal ordering facts of one recovery snapshot for the §9.2 same-machine tie-break: its
/// monotonic per-machine <c>seq</c> (primary key) and its <c>savedAt</c> wall-clock (secondary).
/// The recovery directory is machine-local and not synced (R4-MA1), so two snapshots for one
/// <c>tabId</c> can only be the same machine's counter — the tie-break never compares two machines.
/// </summary>
/// <param name="Seq">The monotonic per-machine sequence counter, or <c>null</c> for a legacy snapshot lacking it.</param>
/// <param name="SavedAt">The wall-clock save instant (a secondary tie-break only), or <c>null</c> if absent.</param>
public readonly record struct SnapshotOrder(long? Seq, DateTimeOffset? SavedAt);

/// <summary>The §9.2 outcome of ordering two duplicate-<c>tabId</c> snapshots.</summary>
public enum TieBreakOutcome
{
    /// <summary>Keep snapshot A (higher <c>seq</c>, or — falling back — the later <c>savedAt</c>); the loser is discarded + logged.</summary>
    KeepA,

    /// <summary>Keep snapshot B (higher <c>seq</c>, or — falling back — the later <c>savedAt</c>); the loser is discarded + logged.</summary>
    KeepB,

    /// <summary>
    /// Neither <c>seq</c> nor <c>savedAt</c> distinguishes the two — surface a conflict (keep-A / keep-B /
    /// diff, per FR-19) rather than silently guessing. A duplicate <c>tabId</c> that cannot be ordered is
    /// a genuine ambiguity, not a tie to resolve arbitrarily.
    /// </summary>
    SurfaceConflict,
}

/// <summary>
/// The §9.2 duplicate-<c>tabId</c> same-machine crash-mid-write tie-break (R3-MA1 (MA6+MA10)): keep the
/// snapshot with the higher per-machine <c>seq</c>; fall back to the later <c>savedAt</c> when <c>seq</c>
/// does not distinguish them; surface a conflict when neither does. Pure ordering logic.
/// </summary>
public static class SnapshotTieBreak
{
    /// <summary>Order two snapshots of the same <c>tabId</c>.</summary>
    public static TieBreakOutcome Resolve(SnapshotOrder a, SnapshotOrder b)
    {
        // Primary: monotonic per-machine seq. Only usable when BOTH carry it (a legacy snapshot lacking
        // the counter forces the savedAt fallback — §9.2 "seq-missing-or-tied case").
        if (a.Seq is { } sa && b.Seq is { } sb && sa != sb)
            return sa > sb ? TieBreakOutcome.KeepA : TieBreakOutcome.KeepB;

        // Secondary: savedAt wall-clock, only when both present and distinct.
        if (a.SavedAt is { } ta && b.SavedAt is { } tb && ta != tb)
            return ta > tb ? TieBreakOutcome.KeepA : TieBreakOutcome.KeepB;

        // Neither orders them → genuine ambiguity → surface a conflict (never guess).
        return TieBreakOutcome.SurfaceConflict;
    }
}
