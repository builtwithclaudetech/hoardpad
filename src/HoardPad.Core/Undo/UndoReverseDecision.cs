using HoardPad.Core.Records;

namespace HoardPad.Core.Undo;

/// <summary>The reverse action the §9.4 reversal logic prescribes for one <see cref="UndoJournalEntry"/>.</summary>
public enum ReverseActionKind
{
    /// <summary>
    /// Undo is unavailable — the entry was over the snapshot size cap (<c>snapshotSkipped: true</c>), so it
    /// has no before-image / <c>preOpContentHash</c>. The restore path is never entered (M14).
    /// </summary>
    Unavailable,

    /// <summary>
    /// Refuse + reprompt — the target or clobbered destination was externally modified since the op (current
    /// content hash ≠ the recorded hash, FR-28(ii)). Do NOT clobber; warn/refuse (M14). Hash is
    /// authoritative; <c>mtime</c> is only a re-hash-skip hint, never the sole basis.
    /// </summary>
    RefuseReprompt,

    /// <summary>
    /// Reverse a <c>create</c>: delete the created file (host re-verifies it is unchanged-since-creation
    /// immediately before the delete — M14). <c>priorContent</c> is <c>null</c> (nothing existed before).
    /// </summary>
    DeleteCreated,

    /// <summary>
    /// Reverse an <c>edit</c>/overwrite or a <c>delete</c>: restore the target's recorded before-image
    /// (<c>priorContent</c>) at <c>paths.target</c>.
    /// </summary>
    RestoreTarget,

    /// <summary>
    /// Reverse a <c>move</c>: move <c>dest</c> back to <c>source</c>. If the move clobbered a pre-existing
    /// <c>dest</c> (<see cref="ReverseAction.RestoreClobberedDestination"/> is set), also restore that
    /// destination's prior bytes (<c>destPriorContent</c>) so undo restores the clobbered file's content,
    /// not just the move mapping (§9.4 Decision 1 / M10).
    /// </summary>
    MoveBack,

    /// <summary>
    /// Reverse a <c>copy</c>: delete the copy at <c>dest</c>. If the copy clobbered a pre-existing
    /// <c>dest</c> (<see cref="ReverseAction.RestoreClobberedDestination"/> is set), restore that
    /// destination's prior bytes (<c>destPriorContent</c>) instead of leaving it deleted (§9.4).
    /// </summary>
    UndoCopy,
}

/// <summary>
/// The §9.4 reverse decision for one entry: the <see cref="Kind"/> plus the clobbered-destination flag
/// (set for a clobbering <c>move</c>/<c>copy</c> so the host also restores <c>destPriorContent</c>).
/// </summary>
/// <param name="Kind">The prescribed reverse action.</param>
/// <param name="RestoreClobberedDestination">
/// <c>true</c> when this was a clobbering <c>move</c>/<c>copy</c> (the entry carries <c>destPriorContent</c>),
/// so undo must restore the clobbered destination's prior bytes in addition to the move-back / copy-delete.
/// </param>
public sealed record ReverseAction(ReverseActionKind Kind, bool RestoreClobberedDestination = false);

/// <summary>
/// The host's current read of a file at reversal time, supplied to <see cref="UndoReverseDecision"/>.
/// Used for both the source/target axis (<c>paths.target</c>) and — for a clobbering move/copy — the
/// destination axis (<c>paths.dest</c>). The host does the byte read + hash (and re-verifies again
/// immediately before the restore write — the M14 TOCTOU close); the domain only decides.
/// </summary>
/// <remarks>
/// <b>Host contract for a <c>delete</c> undo:</b> the host PRE-STAGES (resurrects) the target before
/// calling <see cref="UndoReverseDecision.Decide"/>, so <see cref="Exists"/> reflects the staged/restored
/// state. Without pre-staging, <c>!Exists</c> would trip the <see cref="UndoReverseDecision.ExternallyModified"/>
/// path and make every delete undo refuse — defeating the entire undo path. The M6.3 host must honour this
/// ordering; the <c>DeleteReversesToRestoreTarget</c> test pins it with <c>Exists: true</c>.
/// </remarks>
/// <param name="Exists">Whether the file exists at the checked path right now (for delete undo: post-pre-stage).</param>
/// <param name="ContentHash">
/// The current content hash, or <c>null</c> when the host skipped the re-hash on the mtime hint (only
/// sound when <see cref="Mtime"/> equals the recorded pre-op mtime for the target axis — see the decision).
/// For the destination axis there is no recorded baseline mtime in §9.4, so any hash-absent state refuses.
/// </param>
/// <param name="Mtime">The current mtime, or <c>null</c> when unavailable.</param>
public readonly record struct CurrentTargetState(bool Exists, string? ContentHash, DateTimeOffset? Mtime);

/// <summary>
/// The pure §9.4 undo-journal reverse decision (architecture §5.4): given an <see cref="UndoJournalEntry"/>
/// and the host's <b>injected current</b> hash/mtime of the target (and, for a clobbering move/copy, the
/// destination), decide the reverse action.
/// </summary>
/// <remarks>
/// Order of guards is durability-critical:
/// <list type="number">
/// <item><b>Over-cap guard.</b> An entry with <c>snapshotSkipped: true</c> has no before-image /
///   <c>preOpContentHash</c>, so undo is reported <see cref="ReverseActionKind.Unavailable"/> and the
///   restore path is never entered (M14). This fires before either re-verify.</item>
/// <item><b>M14 TOCTOU re-verify — source/target axis.</b> For an op that records a
///   <c>preOpContentHash</c> (edit / overwrite / delete / clobbering move/copy), the decision compares the
///   recorded hash against <paramref name="current"/> before applying undo. If they differ, the target was
///   modified externally (FR-28(ii)) → <see cref="ReverseActionKind.RefuseReprompt"/>; never clobber.
///   Hash is authoritative; <c>mtime</c> is only a re-hash-skip hint — never the sole basis.</item>
/// <item><b>M14 TOCTOU re-verify — destination axis (FR-28(ii), §9.4 <c>destPriorContentHash</c>).</b>
///   For a <b>clobbering</b> move/copy (where <c>destPriorContent</c> / <c>DestPriorContentHash</c> is
///   present), the decision additionally re-verifies the destination's current state
///   (<paramref name="destCurrent"/>) against <c>entry.DestPriorContentHash</c>. If the destination was
///   modified externally since the AI op, restoring stale <c>destPriorContent</c> onto it would silently
///   destroy newer content — so the result is <see cref="ReverseActionKind.RefuseReprompt"/>. The
///   <c>DestPriorContentHash</c> field in §9.4 exists specifically for this check (prd.md:799). There is
///   no baseline mtime for the destination axis (§9.4 records none), so any hash-absent destination state
///   (host skipped the re-hash but has no mtime proof) is conservatively refused.</item>
/// <item><b>Per-op reversal.</b> Once all guards pass, the §9.4 op semantics select the reverse action.</item>
/// </list>
/// The host additionally holds a handle / re-verifies the hash immediately before each restore write
/// (these decision-time checks are the decision half; the host write-time checks are the close).
/// </remarks>
public static class UndoReverseDecision
{
    /// <summary>
    /// Decide the reverse action for one journal entry.
    /// </summary>
    /// <param name="entry">The §9.4 journal entry being reversed.</param>
    /// <param name="current">
    /// The host's current read of the file at <c>paths.target</c>. For a <c>delete</c> undo the host
    /// pre-stages (resurrects) the target so <see cref="CurrentTargetState.Exists"/> is <c>true</c> here
    /// — see the <see cref="CurrentTargetState"/> XML doc for the required host ordering.
    /// </param>
    /// <param name="destCurrent">
    /// The host's current read of the file at <c>paths.dest</c> — required when the entry is a clobbering
    /// move/copy (<c>destPriorContent</c> present). For non-clobbering ops this parameter is ignored; the
    /// default <c>default</c> value (<c>Exists: false</c>, both hashes null) is safe because the
    /// destination re-verify is only entered when <c>entry.DestPriorContentHash</c> is non-null.
    /// </param>
    public static ReverseAction Decide(
        UndoJournalEntry entry,
        CurrentTargetState current,
        CurrentTargetState destCurrent = default)
    {
        // (1) Over-cap entries never restore — no before-image exists (M14). Fires before either re-verify.
        if (entry.SnapshotSkipped)
            return new ReverseAction(ReverseActionKind.Unavailable);

        var clobbered = entry.DestPriorContent is not null;

        // (2) M14 TOCTOU re-verify — source/target axis. A create has no pre-op hash (nothing existed
        //     before); its "unchanged-since-creation" safety is the host's write-time re-verify.
        if (entry.Op != FileOp.Create
            && ExternallyModified(entry.PreOpContentHash, entry.PreOpMtime, current))
        {
            return new ReverseAction(ReverseActionKind.RefuseReprompt);
        }

        // (3) M14 TOCTOU re-verify — destination axis (FR-28(ii) / §9.4 DestPriorContentHash).
        //     For a clobbering move/copy, restoring destPriorContent onto dest would silently destroy
        //     newer dest content if a third party modified dest between the AI op and this undo.
        //     The §9.4 field DestPriorContentHash exists precisely to detect this.
        //     No baseline dest mtime is recorded in §9.4, so the mtime-skip path is unavailable here:
        //     any hash-absent dest state is conservatively refused (fail safe toward RefuseReprompt).
        if (clobbered && DestExternallyModified(entry.DestPriorContentHash, destCurrent))
            return new ReverseAction(ReverseActionKind.RefuseReprompt);

        // (4) Per-op §9.4 reversal semantics.
        return entry.Op switch
        {
            FileOp.Create => new ReverseAction(ReverseActionKind.DeleteCreated),
            FileOp.Edit => new ReverseAction(ReverseActionKind.RestoreTarget),
            FileOp.Delete => new ReverseAction(ReverseActionKind.RestoreTarget),
            FileOp.Move => new ReverseAction(ReverseActionKind.MoveBack, RestoreClobberedDestination: clobbered),
            FileOp.Copy => new ReverseAction(ReverseActionKind.UndoCopy, RestoreClobberedDestination: clobbered),
            _ => new ReverseAction(ReverseActionKind.RefuseReprompt),
        };
    }

    /// <summary>
    /// The FR-28(ii) external-modification test for the <b>source/target</b> axis: current target identity
    /// vs the recorded pre-op identity. Hash is authoritative; mtime is only a re-hash-skip hint.
    /// </summary>
    internal static bool ExternallyModified(
        string? preOpContentHash,
        DateTimeOffset? preOpMtime,
        CurrentTargetState current)
    {
        // The target the undo must restore against is gone → removed/replaced externally → refuse.
        if (!current.Exists)
            return true;

        // Authoritative path: a current hash is available → compare hashes. The verdict is final;
        // mtime never overrides it.
        if (current.ContentHash is not null)
            return !string.Equals(current.ContentHash, preOpContentHash, StringComparison.Ordinal);

        // The host skipped the re-hash on the mtime hint. That skip is sound — i.e. "not externally
        // modified" — ONLY when the current mtime is present and equals the recorded pre-op mtime.
        // Otherwise fail toward RefuseReprompt (a same-tick edit under coarse mtime, or a missing
        // mtime, could mask a real change).
        var mtimeProvesUnmodified =
            current.Mtime is { } now && preOpMtime is { } pre && now == pre;

        return !mtimeProvesUnmodified;
    }

    /// <summary>
    /// The FR-28(ii) external-modification test for the <b>destination</b> axis of a clobbering
    /// move/copy: the clobbered destination's current state vs the recorded <c>destPriorContentHash</c>.
    /// </summary>
    /// <remarks>
    /// §9.4 records no baseline <c>destPriorMtime</c>, so the mtime-skip path is not available here —
    /// any hash-absent current state is conservatively refused. Hash is still authoritative when present.
    /// </remarks>
    private static bool DestExternallyModified(string? destPriorContentHash, CurrentTargetState dest)
    {
        // Dest is gone (or was never there, i.e. the default CurrentTargetState): cannot restore onto it.
        if (!dest.Exists)
            return true;

        // Hash available → authoritative verdict.
        if (dest.ContentHash is not null)
            return !string.Equals(dest.ContentHash, destPriorContentHash, StringComparison.Ordinal);

        // No hash and no mtime baseline for the dest axis → refuse (conservatively safe).
        return true;
    }
}
