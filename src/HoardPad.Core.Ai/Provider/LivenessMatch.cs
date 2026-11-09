using System.Collections.Immutable;

namespace HoardPad.Core.Ai.Provider;

/// <summary>
/// One observed PreToolUse hook callback, recorded by the host as the hook fires (architecture §4.2 step 6,
/// TD §4.7). The <see cref="ToolUseId"/> (read from the hook's stdin event JSON) is the MA14 correlation key
/// against the agent's <see cref="WriteToolUseEvent"/>. A callback proves the hook was honored for that op —
/// its presence is the ONLY positive liveness signal, because every hook-failure mode is a silent allow with no
/// negative CLI signal (TD §4.7, SPIKE-3 I).
/// </summary>
/// <param name="ToolUseId">The <c>tool_use_id</c> the hook saw on stdin — correlates 1:1 with a tool-use event.</param>
public sealed record HookCallbackObservation(string ToolUseId);

/// <summary>
/// The MA14 liveness verdict for a turn (or a turn slice): whether the sandbox is still positively armed per op,
/// and which write tool-use events were emitted WITHOUT a matching hook callback (the degrade signal — a write
/// the hook never gated, so the sandbox may be silently disarmed, TD §4.7).
/// </summary>
/// <param name="ShouldDegrade">
/// <c>true</c> when at least one write tool-use event has no matching hook callback — FR-10(c) applies (the
/// orchestration must fail closed: stop write agency / abort the turn). <c>false</c> when every write tool-use
/// was matched 1:1 (SPIKE-3 I confirmed hook fires match tool-use events 1:1 when armed).
/// </param>
/// <param name="UnmatchedWriteEvents">
/// The write tool-use events with no matching hook callback, in emission order (empty when not degraded).
/// </param>
public sealed record LivenessVerdict(bool ShouldDegrade, ImmutableArray<WriteToolUseEvent> UnmatchedWriteEvents);

/// <summary>
/// The MA14 positive-invocation liveness match (TD §4.7, PRD FR-26 "Per-op interception liveness", SPIKE-3 I).
/// Correlates each <c>stream-json</c> WRITE tool-use event the agent emitted against an observed per-op hook
/// callback, keyed by <c>tool_use_id</c>. A write tool-use with NO matching hook invocation is the degrade signal:
/// the hook armed at startup (proven by the FR-24 §3 self-test) but is no longer being honored per op, so the
/// sandbox may be silently disarmed — the orchestration must fail closed (FR-10(c)).
/// </summary>
/// <remarks>
/// <para>
/// Pure + platform-free: a set/correlation over two in-memory lists — no I/O, no clock, no process. The host
/// feeds it the parsed write tool-use events (<see cref="StreamJsonTaxonomy"/>) and the observed hook callbacks;
/// this logic decides degrade-or-not. This is deliberately a POSITIVE match (presence of an expected callback),
/// not a negative-error listen — TD §4.7 is explicit that no negative CLI signal exists for a silently-disarmed
/// hook, so absence-of-expected-invocation is the only detection channel.
/// </para>
/// <para>
/// Hook callbacks for tools that are not write tool-uses (e.g. a <c>Read</c> gate, or a callback whose
/// <c>tool_use_id</c> never appears as an emitted write) are irrelevant to the verdict — they simply do not
/// match any write event. The match is directional: EVERY write tool-use must have a callback; a spare callback
/// is harmless. A write event without a callback is the only thing that degrades.
/// </para>
/// </remarks>
public static class LivenessMatch
{
    /// <summary>
    /// Match the agent's WRITE tool-use events against the observed hook callbacks, keyed by <c>tool_use_id</c>,
    /// and return the MA14 verdict. Degrades when any write tool-use has no matching callback (TD §4.7).
    /// </summary>
    /// <param name="writeToolUses">
    /// The WRITE tool-use events the agent emitted this turn slice (from <see cref="StreamJsonTaxonomy"/>), in
    /// emission order. Read tool-uses are NOT passed here — they are not gated for liveness.
    /// </param>
    /// <param name="hookCallbacks">The per-op hook callbacks the host observed this turn slice.</param>
    public static LivenessVerdict Evaluate(
        IReadOnlyList<WriteToolUseEvent> writeToolUses,
        IReadOnlyList<HookCallbackObservation> hookCallbacks)
    {
        // The set of tool_use_ids the hook was positively observed to gate. A write whose id is absent here was
        // never gated → degrade. Ordinal because tool_use_id is an opaque CLI token, not a path (no normcase).
        var gatedIds = hookCallbacks
            .Select(c => c.ToolUseId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToImmutableHashSet(StringComparer.Ordinal);

        var unmatched = ImmutableArray.CreateBuilder<WriteToolUseEvent>();
        foreach (var write in writeToolUses)
        {
            if (!gatedIds.Contains(write.ToolUseId))
                unmatched.Add(write);
        }

        var unmatchedEvents = unmatched.ToImmutable();
        return new LivenessVerdict(ShouldDegrade: unmatchedEvents.Length > 0, UnmatchedWriteEvents: unmatchedEvents);
    }
}
