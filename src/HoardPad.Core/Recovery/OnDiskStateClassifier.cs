namespace HoardPad.Core.Recovery;

/// <summary>
/// The host's read of the current on-disk file at <c>tab.path</c>, supplied to
/// <see cref="OnDiskStateClassifier"/>. The host does the directory scan + byte read (C-9); the domain
/// only decides. The current <see cref="ContentHash"/> is computed by the host (re-hash skipped iff the
/// mtime hint says it is safe to skip — see <see cref="OnDiskStateClassifier"/>).
/// </summary>
/// <param name="Exists">Whether a file exists at <c>tab.path</c> right now.</param>
/// <param name="ContentHash">
/// The current content hash, or <c>null</c> when the host skipped the re-hash on the mtime hint
/// (only valid to skip when <see cref="Mtime"/> equals the baseline mtime — see the classifier).
/// </param>
/// <param name="Mtime">The current on-disk mtime, or <c>null</c> when unavailable.</param>
public readonly record struct CurrentDiskFile(bool Exists, string? ContentHash, DateTimeOffset? Mtime);

/// <summary>
/// Classifies dimension 3 of the §9.2 table — present-unchanged vs present-changed-externally vs
/// deleted — for a <c>kind: file</c>/<c>hoard</c> tab, against the snapshot's recorded baseline.
/// </summary>
/// <remarks>
/// <b>Hash is authoritative; mtime is only a re-hash-skip optimization (§9.2, FR-28(ii)/FR-19).</b>
/// The decision is:
/// <list type="bullet">
/// <item>If the file is gone → <see cref="OnDiskFileState.Deleted"/>.</item>
/// <item>If the current content hash is known, compare hashes: equal → unchanged, differ → changed.
///   The hash verdict is final — an equal/unequal mtime never overrides it.</item>
/// <item>If the host elected to skip the re-hash (current hash unknown), that skip is honoured
///   <i>only</i> when the current mtime is present AND equals the baseline mtime — the sole condition
///   under which "no re-hash needed" is sound. In that case the verdict is unchanged.
///   If the host supplied no hash and the mtime does NOT prove an unchanged file (mtime missing, or
///   mtime differs), the domain refuses to guess unchanged: it returns
///   <see cref="OnDiskFileState.PresentChangedExternally"/> (fail safe toward surfacing a conflict,
///   never toward a silent clobbering Save). A sync-daemon mtime touch or coarse-resolution mtime
///   therefore cannot produce a false-unchanged verdict.</item>
/// </list>
/// </remarks>
public static class OnDiskStateClassifier
{
    /// <summary>
    /// Classify the current on-disk state against the snapshot baseline. For a <c>kind: scratch</c> tab
    /// (no on-disk file), callers do not invoke this — they use <see cref="OnDiskFileState.NotApplicable"/>.
    /// </summary>
    /// <param name="baselineContentHash">The snapshot's recorded <c>baselineContentHash</c> (B7).</param>
    /// <param name="baselineMtime">The snapshot's recorded <c>baselineMtime</c> (the re-hash-skip hint only).</param>
    /// <param name="current">The host's current read of the file at <c>tab.path</c>.</param>
    public static OnDiskFileState Classify(
        string baselineContentHash,
        DateTimeOffset? baselineMtime,
        CurrentDiskFile current)
    {
        if (!current.Exists)
            return OnDiskFileState.Deleted;

        // Hash is authoritative whenever the host computed it — the mtime never overrides a hash verdict.
        if (current.ContentHash is not null)
        {
            return string.Equals(current.ContentHash, baselineContentHash, StringComparison.Ordinal)
                ? OnDiskFileState.PresentUnchanged
                : OnDiskFileState.PresentChangedExternally;
        }

        // No current hash: the host skipped the re-hash on the mtime hint. That skip is sound ONLY when
        // the current mtime is present and equals the baseline mtime. Otherwise we must not assume
        // unchanged (a sync-daemon mtime touch or coarse mtime could mask a real edit) — fail toward a
        // surfaced conflict, never toward a silent unchanged verdict (§9.2, FR-28(ii)).
        var mtimeProvesUnchanged =
            current.Mtime is { } now && baselineMtime is { } baseline && now == baseline;

        return mtimeProvesUnchanged
            ? OnDiskFileState.PresentUnchanged
            : OnDiskFileState.PresentChangedExternally;
    }
}
