using System.Collections.Immutable;

namespace HoardPad.Core.Ai.Caps;

/// <summary>
/// The immutable per-conversation cap-state (architecture §4.4, TD §4.5): the FR-11/FR-38 counted-op counters,
/// the FR-40 distinct-files set, and the no-re-arm tripped flags. Keyed (by the host) to the user-initiated
/// conversation / logical-turn id — NOT the per-invocation <c>requestId</c>/<c>session_id</c> (R5-BL1). Two
/// conversations each own a distinct <see cref="CapState"/>, so they accrue independently; a HoardPad-initiated
/// continuation turn RESUMES the same conversation's state (only a new user-initiated conversation starts from
/// <see cref="Empty"/>).
/// </summary>
/// <remarks>
/// Pure value — the <c>O_EXCL</c>-locked persistence under the conversation id is a <c>HoardPad.Host</c> concern
/// (architecture §4.4); this record is what gets serialized into that locked file and fed back through
/// <see cref="CapAccounting.Account"/>. The tripped flags are sticky (no-re-arm, MA12): once a cap trips it
/// STAYS tripped for the conversation's lifetime — a confirm does not reset the counter (PRD FR-11/FR-38/FR-40,
/// TD §4.5 line 454). The FR-40 set is a <em>set</em> of canonical paths: a file touched twice counts once.
/// </remarks>
/// <param name="CreateCount">FR-11 counted creates so far this conversation (applied ops only, §9.3 mi3).</param>
/// <param name="DestructiveCount">FR-38 counted destructive overwrites so far this conversation (applied ops only).</param>
/// <param name="FilesTouched">FR-40 distinct canonical paths touched so far this conversation (create OR overwrite).</param>
/// <param name="CreateCapTripped">FR-11 no-re-arm tripped flag — once true, stays true (MA12).</param>
/// <param name="DestructiveCapTripped">FR-38 no-re-arm tripped flag — once true, stays true (MA12).</param>
/// <param name="FilesTouchedCapTripped">FR-40 no-re-arm tripped flag — once true, stays true (MA12).</param>
public sealed record CapState(
    int CreateCount,
    int DestructiveCount,
    ImmutableHashSet<string> FilesTouched,
    bool CreateCapTripped,
    bool DestructiveCapTripped,
    bool FilesTouchedCapTripped)
{
    /// <summary>
    /// A fresh cap-state for a new user-initiated conversation — every counter at 0, the FR-40 set empty
    /// (ordinal path comparison; the host supplies already-canonicalized paths), no cap tripped.
    /// </summary>
    public static CapState Empty { get; } = new(
        CreateCount: 0,
        DestructiveCount: 0,
        FilesTouched: ImmutableHashSet.Create<string>(StringComparer.Ordinal),
        CreateCapTripped: false,
        DestructiveCapTripped: false,
        FilesTouchedCapTripped: false);

    /// <summary>
    /// The FR-40 distinct-files count (the cardinality of <see cref="FilesTouched"/>).
    /// </summary>
    /// <remarks>
    /// <b>[JsonIgnore]</b> — this is a derived convenience property. The M5.1 Python deny-by-default hook reads
    /// the cap-state file as PascalCase JSON and must see EXACTLY the six contract fields
    /// (<c>CreateCount</c>, <c>DestructiveCount</c>, <c>FilesTouched</c>, <c>CreateCapTripped</c>,
    /// <c>DestructiveCapTripped</c>, <c>FilesTouchedCapTripped</c>). Serializing this computed property would
    /// emit a seventh field (<c>"FilesTouchedCount"</c>) and break the cross-language contract (Gate-2 W1).
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public int FilesTouchedCount => FilesTouched.Count;
}
