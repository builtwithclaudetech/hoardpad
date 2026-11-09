using System.Collections.Immutable;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.Provider;

/// <summary>
/// What the Archetype-A orchestration directs the host to do at a point in a turn. <see cref="Continue"/> is the
/// healthy path; <see cref="DegradeFailClosed"/> is the MA14 / self-test fail-closed response (architecture §4.2
/// step 5/6, TD §4.4/§4.7, FR-10(c)).
/// </summary>
public enum TurnAction
{
    /// <summary>The turn proceeds — the sandbox is positively armed and every observed write was gated.</summary>
    Continue,

    /// <summary>
    /// FAIL CLOSED. Either write agency was never armed (self-test absent/failed, TD §4.4) or a write tool-use
    /// went unmatched by a hook callback (MA14 degrade, TD §4.7). The host must stop write agency / abort the
    /// turn and apply FR-10(c) (block/confirm remaining or halt); ops already applied are logged.
    /// </summary>
    DegradeFailClosed,
}

/// <summary>
/// Why the turn is in a <see cref="TurnAction.DegradeFailClosed"/> state, for the activity log
/// (<see cref="DowngradeReason"/>) and the UI. <see cref="None"/> while the turn is healthy.
/// </summary>
public enum DegradeCause
{
    /// <summary>No degrade — the turn is healthy.</summary>
    None,

    /// <summary>Write agency was never armed: the FR-24 §3 self-test did not pass (TD §4.4).</summary>
    SelfTestNotPassed,

    /// <summary>A write tool-use had no matching hook callback — MA14 liveness degrade (TD §4.7).</summary>
    UnmatchedWriteToolUse,
}

/// <summary>
/// The immutable Archetype-A turn state: the resolved write-agency posture, the accumulating MA14 liveness
/// correlation across the streamed events, and the current <see cref="TurnAction"/>. Pure value — the host owns
/// the process, the stream, and the kill; this carries the safety decision.
/// </summary>
/// <param name="Agency">The resolved agency posture for the turn (self-test-gated, TD §4.3/§4.4).</param>
/// <param name="Action">The current directive to the host (<see cref="TurnAction.Continue"/> until a degrade).</param>
/// <param name="Cause">Why the turn degraded (<see cref="DegradeCause.None"/> while healthy).</param>
/// <param name="PendingWriteToolUses">
/// Write tool-use events observed so far whose <c>tool_use_id</c> has NOT yet been matched by a hook callback. A
/// write moves OUT of this set the instant a matching callback is observed; a write that stays here at a liveness
/// checkpoint is the MA14 degrade signal.
/// </param>
/// <param name="GatedToolUseIds">
/// The <c>tool_use_id</c>s the hook was positively observed to gate but whose write tool-use event has not yet
/// been seen. This makes the match ORDER-INDEPENDENT across the two channels (the stdout stream and the
/// hook-observation channel): a callback that arrives before its write event is remembered here and clears that
/// write the moment the write event is folded in — so a benign cross-channel reordering never false-degrades.
/// </param>
public sealed record ArchetypeATurnState(
    AgencyResolution Agency,
    TurnAction Action,
    DegradeCause Cause,
    ImmutableHashSet<WriteToolUseEvent> PendingWriteToolUses,
    ImmutableHashSet<string> GatedToolUseIds)
{
    /// <summary><c>true</c> once the turn has failed closed (write agency stopped / turn aborting).</summary>
    public bool IsDegraded => Action == TurnAction.DegradeFailClosed;
}

/// <summary>
/// The Archetype-A (<c>claude -p</c>) turn-lifecycle ORCHESTRATION — platform-free (architecture §4.2, TD §4).
/// HoardPad does not author the agent's tool loop here; it GATES it. Every OS leg (process launch, stream read,
/// process-tree kill, hook materialization, self-test run) is an <see cref="IAgentBackend"/> /
/// <see cref="IToolPermissionHookHost"/> call the host owns (M5.2); this type owns the SAFETY BRAIN:
/// <list type="number">
/// <item>compose the FR-24 §4.1 launch flag contract (<see cref="ClaudeLaunchContract"/>), never bypassing
///   permissions and never passing <c>--cwd</c>;</item>
/// <item>resolve the agency→permission mapping under the self-test gate (<see cref="AgencyMapping"/>) — write
///   agency is armed ONLY after the fail-closed self-test passes (TD §4.4);</item>
/// <item>run the MA14 liveness match across the streamed write tool-uses vs the observed hook callbacks
///   (<see cref="LivenessMatch"/>), degrading fail-closed on the first unmatched write (TD §4.7).</item>
/// </list>
/// </summary>
/// <remarks>
/// Pure + platform-free: no process, no clock, no P/Invoke. The host drives this by (a) calling
/// <see cref="Begin"/> after running the self-test to get the starting state + the launch args to hand to
/// <see cref="IAgentBackend.StartTurnAsync"/>, then (b) folding each parsed <see cref="WriteToolUseEvent"/> and
/// each observed <see cref="HookCallbackObservation"/> into the state as the stream advances
/// (<see cref="OnWriteToolUse"/> / <see cref="OnHookCallback"/>). The instant the state's
/// <see cref="ArchetypeATurnState.Action"/> becomes <see cref="TurnAction.DegradeFailClosed"/>, the host kills
/// the process tree (the SOLE fail-closed enforcer, TD §4.4) and applies FR-10(c).
/// </remarks>
public static class ArchetypeAOrchestration
{
    /// <summary>
    /// Begin a turn: resolve the self-test-gated agency posture and compose the FR-24 §4.1 launch args. When the
    /// self-test did not pass for a write-capable mode the returned state is ALREADY degraded
    /// (<see cref="DegradeCause.SelfTestNotPassed"/>) — write agency is never armed without a passing self-test.
    /// </summary>
    /// <param name="configuredMode">The user-configured agency mode (FR-27).</param>
    /// <param name="selfTest">
    /// The host's fail-closed self-test result (FR-24 §3), or <c>null</c> if not run — both non-passing cases
    /// degrade a write-capable mode to read-only (TD §4.4).
    /// </param>
    /// <param name="maxBudgetUsd">The FR-24 <c>--max-budget-usd</c> ceiling (must be positive).</param>
    /// <param name="settingsJsonPath">The absolute path to the host-built <c>--settings</c> hook JSON.</param>
    /// <param name="modelId">BUG-5: optional model override threaded from TurnRequest (null = backend default).</param>
    /// <param name="effortLevel">BUG-5: optional effort level threaded from TurnRequest (null = backend default).</param>
    /// <returns>The starting turn state and the composed launch args to hand to the host.</returns>
    public static (ArchetypeATurnState State, ImmutableArray<string> LaunchArgs) Begin(
        AgencyMode configuredMode,
        SelfTestResult? selfTest,
        decimal maxBudgetUsd,
        string settingsJsonPath,
        string? modelId = null,
        string? effortLevel = null)
    {
        var agency = AgencyMapping.Resolve(configuredMode, selfTest);
        var launchArgs = ClaudeLaunchContract.Compose(maxBudgetUsd, settingsJsonPath, modelId, effortLevel);

        // The self-test gate (TD §4.4): a write-capable mode that did not pass the self-test starts degraded —
        // write agency is never armed without a passing self-test. A read-only mode never needs the gate, and an
        // armed write mode starts healthy.
        var startDegraded = agency.GateDenied;
        var state = new ArchetypeATurnState(
            Agency: agency,
            Action: startDegraded ? TurnAction.DegradeFailClosed : TurnAction.Continue,
            Cause: startDegraded ? DegradeCause.SelfTestNotPassed : DegradeCause.None,
            PendingWriteToolUses: ImmutableHashSet<WriteToolUseEvent>.Empty,
            GatedToolUseIds: ImmutableHashSet.Create<string>(StringComparer.Ordinal));

        return (state, launchArgs);
    }

    /// <summary>
    /// Fold a write tool-use the agent emitted into the turn state: it enters the pending set (awaiting its hook
    /// callback). Once already degraded the state is returned unchanged (the turn is failing closed).
    /// </summary>
    /// <remarks>
    /// A write tool-use event observed while <see cref="AgencyResolution.WriteAgencyArmed"/> is <c>false</c>
    /// (read-only turn, or a gate-denied turn) is itself a degrade signal — the agent emitted a write despite
    /// the tool roster being restricted, which is the strongest possible disarm signal (MA14, TD §4.7). Transition
    /// immediately to <see cref="TurnAction.DegradeFailClosed"/> with <see cref="DegradeCause.UnmatchedWriteToolUse"/>.
    /// </remarks>
    public static ArchetypeATurnState OnWriteToolUse(ArchetypeATurnState state, WriteToolUseEvent writeEvent)
    {
        if (state.IsDegraded)
            return state;

        // If write agency was never armed (read-only turn, gate-denied turn), a write tool-use event is impossible
        // under a healthy hook — treat it as a degrade signal immediately (the tool was not offered; its presence
        // means the sandbox was bypassed or the hook silently failed, TD §4.7).
        if (!state.Agency.WriteAgencyArmed)
        {
            return state with
            {
                Action = TurnAction.DegradeFailClosed,
                Cause = DegradeCause.UnmatchedWriteToolUse,
            };
        }

        // Order-independent: if the hook callback for this id was already observed, the write is born matched —
        // consume the pre-recorded gated id instead of adding to the pending set.
        if (state.GatedToolUseIds.Contains(writeEvent.ToolUseId))
            return state with { GatedToolUseIds = state.GatedToolUseIds.Remove(writeEvent.ToolUseId) };

        return state with { PendingWriteToolUses = state.PendingWriteToolUses.Add(writeEvent) };
    }

    /// <summary>
    /// Fold an observed hook callback into the turn state: the write whose <c>tool_use_id</c> matches leaves the
    /// pending set (it was positively gated — MA14 healthy for that op, TD §4.7). A callback for an id not in the
    /// pending set is harmless (a Read gate, or a callback observed before its write event) and is ignored.
    /// </summary>
    public static ArchetypeATurnState OnHookCallback(ArchetypeATurnState state, HookCallbackObservation callback)
    {
        if (state.IsDegraded || string.IsNullOrEmpty(callback.ToolUseId))
            return state;

        var matched = state.PendingWriteToolUses
            .Where(w => string.Equals(w.ToolUseId, callback.ToolUseId, StringComparison.Ordinal))
            .ToImmutableArray();

        if (matched.Length == 0)
        {
            // The write event for this gated id has not streamed yet — remember the callback so OnWriteToolUse
            // can match it later (order-independent across the two channels). A spare callback for a Read gate or
            // an id that never becomes a write is harmless (it just sits in the gated set, unused).
            return state with { GatedToolUseIds = state.GatedToolUseIds.Add(callback.ToolUseId) };
        }

        var remaining = state.PendingWriteToolUses;
        foreach (var w in matched)
            remaining = remaining.Remove(w);

        return state with { PendingWriteToolUses = remaining };
    }

    /// <summary>
    /// Resolve the MA14 verdict at a checkpoint (turn end, or any point the host samples liveness): every write
    /// still pending a hook callback is unmatched → degrade fail-closed (TD §4.7, FR-10(c)). If the turn already
    /// degraded (e.g. self-test gate), that state stands. Healthy turns with no pending writes stay
    /// <see cref="TurnAction.Continue"/>.
    /// </summary>
    /// <remarks>
    /// The host calls this when the stream ends (no more callbacks can arrive for a pending write) or when it
    /// otherwise samples liveness mid-turn. The returned <see cref="LivenessVerdict"/> is the audit trail of which
    /// writes went unmatched (logged per FR-10(c)).
    /// </remarks>
    public static (ArchetypeATurnState State, LivenessVerdict Verdict) ResolveLiveness(ArchetypeATurnState state)
    {
        if (state.IsDegraded)
        {
            // Already failing closed — surface the still-pending writes as the audit list without re-deciding.
            var pending = state.PendingWriteToolUses.ToImmutableArray();
            return (state, new LivenessVerdict(ShouldDegrade: true, UnmatchedWriteEvents: pending));
        }

        var verdict = new LivenessVerdict(
            ShouldDegrade: state.PendingWriteToolUses.Count > 0,
            UnmatchedWriteEvents: state.PendingWriteToolUses.ToImmutableArray());

        if (!verdict.ShouldDegrade)
            return (state, verdict);

        var degraded = state with
        {
            Action = TurnAction.DegradeFailClosed,
            Cause = DegradeCause.UnmatchedWriteToolUse,
        };
        return (degraded, verdict);
    }
}
