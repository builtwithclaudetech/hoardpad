namespace HoardPad.Core.Ai.Caps;

/// <summary>
/// The reachable Archetype-A write tools the cap classifier sees (PRD FR-38 / TD §4.5). The live
/// <c>claude</c> tool protocol exposes only whole-file <c>Write</c> and string-replace <c>Edit</c>; neither
/// maps to a destructive/non-destructive split on its own (R3-BL1), so the classifier is a predicate over
/// <c>(tool, pre-op bytes, proposed bytes)</c>, not over the tool name alone.
/// </summary>
public enum WriteTool
{
    /// <summary>The whole-file <c>Write</c> tool (<c>{file_path, content}</c>).</summary>
    Write,

    /// <summary>The string-replace <c>Edit</c> tool (<c>{old_string, new_string, replace_all}</c>).</summary>
    Edit,
}

/// <summary>Which per-turn cap an applied op is routed to by the create-vs-overwrite split (PRD FR-38 / §9.3 mi3).</summary>
public enum OpKind
{
    /// <summary>
    /// A create — a <c>Write</c> or <c>Edit</c> whose target does NOT exist (incl. the <c>Edit</c>-empty-
    /// <c>old_string</c> create idiom, R4-BL1). Routes to the FR-11 create cap and increments the FR-40 set.
    /// </summary>
    Create,

    /// <summary>
    /// A destructive overwrite — a write onto an EXISTING target that meets the FR-38 magnitude predicate
    /// (a whole-file <c>Write</c>, OR an op replacing ≥ X% of the prior bytes). Routes to the FR-38
    /// destructive cap and increments the FR-40 set.
    /// </summary>
    DestructiveOverwrite,

    /// <summary>
    /// A sub-threshold edit — a write onto an EXISTING target that does NOT meet the FR-38 magnitude
    /// predicate (a partial <c>Edit</c> &lt; X% of prior bytes). Counts toward NO per-op cap (FR-38 excludes
    /// it, R4-mi5), but STILL increments the FR-40 distinct-files set — the aggregate backstop for the
    /// sub-threshold mass-overwrite vector (R4-BL2).
    /// </summary>
    SubThresholdEdit,
}

/// <summary>
/// One applied write op, as the PreToolUse hook sees it: the tool, whether the target already existed at
/// hook time (the <c>stat</c>/existence check, on BOTH <c>Write</c> and <c>Edit</c> targets — R4-BL1), the
/// target's pre-op size, and the proposed post-op size. Sizes are in bytes.
/// </summary>
/// <remarks>
/// <paramref name="TargetExists"/> is the create-vs-overwrite router: absent → create, present → overwrite/edit.
/// The host supplies the existence flag and the byte sizes (it does the <c>stat</c>); the classifier is pure.
/// </remarks>
/// <param name="Tool">The write tool used.</param>
/// <param name="TargetExists">Whether the target path existed at hook time (the host's <c>stat</c> result).</param>
/// <param name="TargetPreOpBytes">Pre-op size of the target in bytes (0 when the target does not exist).</param>
/// <param name="ProposedBytes">Proposed post-op size in bytes.</param>
public readonly record struct OpClassificationInput(
    WriteTool Tool,
    bool TargetExists,
    long TargetPreOpBytes,
    long ProposedBytes);

/// <summary>
/// The pure FR-38 counted-op classifier + create-vs-overwrite router (PRD FR-38 §"Counted-op classifier",
/// TD §4.5). Decides, for one op over <c>(tool, target_pre_op_bytes, proposed_bytes)</c>, which per-turn cap
/// it is routed to.
/// </summary>
/// <remarks>
/// The routing (PRD FR-38, TD §4.5 line 462, §9.3 mi3):
/// <list type="bullet">
/// <item><b>Target absent → <see cref="OpKind.Create"/></b> (FR-11). A <c>Write</c> OR <c>Edit</c> to a new
///   path is a create — including the <c>Edit</c>-empty-<c>old_string</c> create idiom (R4-BL1). The split
///   covers <c>Edit</c> and not <c>Write</c> alone because <c>Edit</c> creates files; a <c>Write</c>-keyed
///   split would leave the <c>Edit</c>-create bypass open.</item>
/// <item><b>Target present + whole-file <c>Write</c> → <see cref="OpKind.DestructiveOverwrite"/></b> (FR-38).
///   A whole-file <c>Write</c> onto an existing file is destructive regardless of magnitude.</item>
/// <item><b>Target present + an op replacing ≥ X% of prior bytes → <see cref="OpKind.DestructiveOverwrite"/></b>
///   (FR-38). The magnitude predicate catches a single full-file <c>Edit</c> hunk (a whole-file overwrite that
///   presents as <c>Edit</c>) — the Write-keyed counter is NOT the sole trigger (R3-BL1).</item>
/// <item><b>Target present + a sub-X% <c>Edit</c> → <see cref="OpKind.SubThresholdEdit"/></b>. Excluded from
///   the FR-38 per-op cap (a legitimate partial edit is never nagged) — but FR-40 still counts the file.</item>
/// </list>
/// <b>FR-40 misroute-invariance</b> is a property of the accounting (<see cref="CapAccounting"/>), not of this
/// routing: every one of the three <see cref="OpKind"/>s increments the FR-40 distinct-files set, so a
/// <c>stat</c>-race misroute between create and overwrite (R4-MA4) cannot escape FR-40 — the file is counted
/// once whichever bucket the race selected.
/// </remarks>
public static class CapClassifier
{
    /// <summary>Classify one applied op into its <see cref="OpKind"/> using the given X% magnitude threshold.</summary>
    /// <param name="op">The op as the hook sees it.</param>
    /// <param name="destructiveMagnitudePercent">
    /// The FR-38 X% threshold, as a whole percent (the pinned value is <see cref="CapConfig.PinnedDestructiveMagnitudePercent"/> = 80).
    /// </param>
    public static OpKind Classify(OpClassificationInput op, int destructiveMagnitudePercent)
    {
        // Create-vs-overwrite split: a non-existent target is always a create (FR-11), routed regardless of
        // tool — this is the R4-BL1 Edit-create close (Edit-to-a-new-path is the agent's file-create idiom).
        if (!op.TargetExists)
            return OpKind.Create;

        // The target exists → overwrite/edit. A whole-file Write is destructive regardless of magnitude.
        if (op.Tool == WriteTool.Write)
            return OpKind.DestructiveOverwrite;

        // An Edit onto an existing file: destructive only if it shrinks or replaces ≥ X% of the prior bytes
        // (a single full-file Edit hunk). The "replaced fraction" is approximated from the size delta the hook
        // sees — the same (prior bytes, proposed bytes) magnitude predicate FR-38 names (R3-BL1).
        return MeetsMagnitudePredicate(op.TargetPreOpBytes, op.ProposedBytes, destructiveMagnitudePercent)
            ? OpKind.DestructiveOverwrite
            : OpKind.SubThresholdEdit;
    }

    /// <summary>
    /// The FR-38 ≥ X% magnitude predicate over <c>(prior bytes, proposed bytes)</c>: an op counts as a
    /// destructive overwrite when it shrinks or replaces at least X% of the prior bytes. "Shrinks ... or
    /// replaces ≥ X% of the prior bytes" (PRD FR-38) — measured as the magnitude of the byte delta relative
    /// to the prior size: <c>|prior − proposed| / prior ≥ X%</c>. A near-empty rewrite of a large file (a
    /// truncate-to-0 reaching Archetype A as a Write of empty content) is ≥ 80% and counts; a 20-line edit in
    /// a 200-line file (~10%) is below 80% and is the excluded partial edit.
    /// </summary>
    /// <remarks>
    /// A prior size of 0 (an existing but empty file) cannot have ≥ X% of its prior bytes replaced by a delta
    /// measure, so an Edit onto an empty existing file is a sub-threshold edit, not a destructive overwrite —
    /// the whole-file-<c>Write</c> path above already catches a destructive empty-file overwrite via the
    /// tool-name branch. Integer math avoids floating point: <c>delta·100 ≥ percent·prior</c>.
    /// </remarks>
    internal static bool MeetsMagnitudePredicate(long priorBytes, long proposedBytes, int percent)
    {
        if (priorBytes <= 0)
            return false;

        var delta = priorBytes > proposedBytes ? priorBytes - proposedBytes : proposedBytes - priorBytes;
        return delta * 100L >= (long)percent * priorBytes;
    }
}
