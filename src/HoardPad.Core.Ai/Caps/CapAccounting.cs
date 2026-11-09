namespace HoardPad.Core.Ai.Caps;

/// <summary>Which cap (if any) gated an accounted op — drives the §9.3 <c>downgradeReason</c> the host logs.</summary>
public enum CapTrigger
{
    /// <summary>No cap gated this op — it proceeds silently (it was within every cap).</summary>
    None,

    /// <summary>The FR-11 create cap gated this op (<c>downgradeReason: create-cap</c>).</summary>
    CreateCap,

    /// <summary>The FR-38 destructive cap gated this op (<c>downgradeReason: destructive-cap</c>).</summary>
    DestructiveCap,

    /// <summary>The FR-40 distinct-files-touched cap gated this op (<c>downgradeReason: files-touched-cap</c>).</summary>
    FilesTouchedCap,
}

/// <summary>
/// The result of accounting one applied op: the new <see cref="State"/>, whether the op
/// <see cref="RequiresConfirm"/> (it reached or is past a cap), and the <see cref="Trigger"/> that gated it.
/// </summary>
/// <remarks>
/// For Archetype A "requires confirm" realizes as the hook denying past the cap and HoardPad initiating a
/// continuation turn after the user confirms (PRD mi2). Because the caps DO NOT re-arm, every op past a tripped
/// cap returns <c>RequiresConfirm == true</c> again — a confirm never buys a fresh allowance (MA12).
/// </remarks>
/// <param name="State">The post-op cap-state (to persist back under the conversation id).</param>
/// <param name="RequiresConfirm">True when this op reached or exceeded a cap and must pause for a confirm.</param>
/// <param name="Trigger">The cap that gated the op (the first to trip on this op), or <see cref="CapTrigger.None"/>.</param>
public sealed record CapDecision(CapState State, bool RequiresConfirm, CapTrigger Trigger);

/// <summary>
/// The pure conversation-keyed cap accounting (architecture §4.4, TD §4.5, PRD FR-11/FR-38/FR-40 + §9.3 mi3).
/// Given a conversation's current <see cref="CapState"/> and one APPLIED op (classified by
/// <see cref="CapClassifier"/>), returns the new state and whether the op must pause for a confirm.
/// </summary>
/// <remarks>
/// Counting rules encoded (§9.3 mi3, binding):
/// <list type="bullet">
/// <item><b>Applied ops only.</b> The host calls this for ops that proceed (silently or after confirm); a
///   denied/out-of-sandbox op is never accounted, so it cannot burn a cap.</item>
/// <item><b>One per-op cap, disjoint.</b> A <see cref="OpKind.Create"/> increments only the FR-11 counter; a
///   <see cref="OpKind.DestructiveOverwrite"/> increments only the FR-38 counter; a
///   <see cref="OpKind.SubThresholdEdit"/> increments NEITHER per-op counter.</item>
/// <item><b>FR-40 is an orthogonal aggregate.</b> EVERY op kind (create, overwrite, AND sub-threshold edit)
///   adds its target path to the FR-40 distinct-files set — a sub-X% overwrite that increments no per-op cap
///   still increments FR-40 (R4-BL2), and a file touched twice counts once (it is a set).</item>
/// <item><b>FR-40 misroute-invariance.</b> Because the FR-40 set is incremented on every kind by path, a
///   <c>stat</c>-race misroute between create and overwrite (R4-MA4) still counts the file exactly once —
///   the misroute cannot push the distinct-files count past FR-40.</item>
/// <item><b>No re-arm (MA12).</b> A cap trips when its counter first reaches the cap value and the tripped flag
///   is STICKY: once tripped, every further op of that class returns <c>RequiresConfirm</c> again and the
///   counter keeps incrementing — a confirm never resets it.</item>
/// </list>
/// </remarks>
public static class CapAccounting
{
    /// <summary>
    /// Account one applied op against a conversation's cap-state.
    /// </summary>
    /// <param name="state">The conversation's current cap-state (resumed across continuation turns).</param>
    /// <param name="kind">The op kind from <see cref="CapClassifier.Classify"/>.</param>
    /// <param name="canonicalTargetPath">
    /// The op's canonical target path (the host resolves it). Added to the FR-40 distinct-files set for every
    /// kind — create, overwrite, and sub-threshold edit.
    /// </param>
    /// <param name="config">The (clamp-validated) cap configuration in force for this conversation.</param>
    public static CapDecision Account(CapState state, OpKind kind, string canonicalTargetPath, CapConfig config)
    {
        // FR-40 aggregate: every applied op adds its target to the distinct-files set, regardless of per-op
        // kind. A set, so a repeat touch is a no-op on the count (counts once per turn — §9.3 mi3, R4-MA4).
        var filesTouched = state.FilesTouched.Add(canonicalTargetPath);
        var filesTouchedCount = filesTouched.Count;
        // No-re-arm: the FR-40 flag is sticky once the distinct-files count reaches the cap.
        var filesTouchedTripped = state.FilesTouchedCapTripped || filesTouchedCount >= config.FilesTouchedCap;

        var createCount = state.CreateCount;
        var destructiveCount = state.DestructiveCount;
        var createTripped = state.CreateCapTripped;
        var destructiveTripped = state.DestructiveCapTripped;

        // The per-op cap (FR-11 create OR FR-38 destructive, disjoint). A sub-threshold edit increments no
        // per-op counter — it is bounded only on the FR-40 aggregate axis (R4-mi5).
        switch (kind)
        {
            case OpKind.Create:
                createCount++;
                createTripped = createTripped || createCount > config.CreateCap;
                break;
            case OpKind.DestructiveOverwrite:
                destructiveCount++;
                destructiveTripped = destructiveTripped || destructiveCount > config.DestructiveCap;
                break;
            case OpKind.SubThresholdEdit:
                // No per-op counter; FR-40 above is its only bound.
                break;
        }

        var newState = new CapState(
            CreateCount: createCount,
            DestructiveCount: destructiveCount,
            FilesTouched: filesTouched,
            CreateCapTripped: createTripped,
            DestructiveCapTripped: destructiveTripped,
            FilesTouchedCapTripped: filesTouchedTripped);

        // Which cap, if any, gated THIS op. The per-op cap that this op's kind belongs to takes precedence in
        // the reported trigger (it is the cap the op would chain against); FR-40 is the fallback aggregate
        // gate. A tripped flag means RequiresConfirm whether or not this exact op pushed it over — no re-arm.
        var trigger = ResolveTrigger(kind, createTripped, destructiveTripped, filesTouchedTripped);
        return new CapDecision(newState, RequiresConfirm: trigger != CapTrigger.None, Trigger: trigger);
    }

    private static CapTrigger ResolveTrigger(
        OpKind kind,
        bool createTripped,
        bool destructiveTripped,
        bool filesTouchedTripped) => kind switch
        {
            // A create is gated by its own per-op cap first; else by the FR-40 aggregate.
            OpKind.Create when createTripped => CapTrigger.CreateCap,
            // A destructive overwrite is gated by its own per-op cap first; else by FR-40.
            OpKind.DestructiveOverwrite when destructiveTripped => CapTrigger.DestructiveCap,
            // A sub-threshold edit has no per-op cap — only FR-40 can gate it.
            _ when filesTouchedTripped => CapTrigger.FilesTouchedCap,
            _ => CapTrigger.None,
        };
}
