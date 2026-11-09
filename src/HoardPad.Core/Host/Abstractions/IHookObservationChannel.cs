// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// HOST [host]. The M11 per-turn hook-observation channel (docs/escalation-ai-live-write-path.md §4): the
/// IPC endpoint HoardPad OWNS and the production <c>deny_by_default.py</c> hook CONNECTS to. The OS primitive
/// (a per-turn Unix domain socket on linux-x64 / a named pipe on win-x64, bound to the turn's working root +
/// conversation cap-state key) is a <c>HoardPad.Host</c> concern (Step 2a); this seam only DECLARES the
/// contract. Core.Ai owns the platform-free projection of an observed event into a chip / cap-read / MA14
/// match (Step 3); the App consumes <see cref="ObserveAsync"/> + answers the approve-each request (Step 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The channel can only NARROW, never WIDEN (spec §5 invariant 1).</b> The hook computes its autonomous
/// deny-by-default + sandbox + cap decision FIRST and independently and reports it as
/// <see cref="HookOpEvent.HookDecision"/>. For an approve-each turn the hook BLOCKS on
/// <see cref="RequestDecisionAsync"/>'s answer and AND-combines it with its own decision: a hook-deny stays a
/// deny regardless of the App; only a hook-allow is eligible for a user deny. Nothing on this seam can turn a
/// hook-deny into an allow.
/// </para>
/// <para>
/// <b>Fail-closed is mandatory (spec §5 invariant 2/6).</b> If the channel is unreachable, unwritable, or the
/// App does not answer within the hook's blocking window, the HOOK denies the write (the implementation never
/// surfaces an "allow on channel failure"). The cancellation token plumbed here is the fail-closed deadline:
/// when it trips, the channel returns the fail-closed answer (<see cref="HookDecisionResponse.Deny"/>) and the
/// blocked hook proceeds to deny. This is the single most important property and the first the extended SC-5
/// gate must prove (G-live-1).
/// </para>
/// <para>
/// <b>No re-arm (spec §5 invariant 4).</b> This seam carries observation + a narrowing confirm only. Caps
/// increment ONLY in the O_EXCL cap-state via <see cref="ICapStateStore"/>; the live meter the App shows is a
/// READ of that state (the Core.Ai projection), never a counter this channel accumulates.
/// </para>
/// </remarks>
public interface IHookObservationChannel
{
    /// <summary>
    /// Open the per-turn observation endpoint for one turn, bound to the turn's resolved
    /// <paramref name="workingRoot"/> and <paramref name="conversationKey"/> (the cap-state key). The returned
    /// <see cref="IHookObservationEndpoint"/> is the live, per-turn handle the App observes and answers
    /// through; it is closed (the OS primitive torn down) when disposed. Called by the host as it launches the
    /// turn, BEFORE the hook can connect — the endpoint path is injected into the hook via the host's existing
    /// <c>BuildHookSettings</c> env (spec §4 channel design).
    /// </summary>
    /// <param name="workingRoot">The turn's resolved open-folder root the hook sandboxes against.</param>
    /// <param name="conversationKey">The conversation-keyed cap-state key (NOT the per-invocation requestId — R5-BL1).</param>
    /// <param name="ct">Cancellation tears down the half-opened endpoint (a turn cancelled before the hook connects).</param>
    Task<IHookObservationEndpoint> OpenForTurnAsync(
        NormcasedPath workingRoot, string conversationKey, CancellationToken ct);
}

/// <summary>
/// One open per-turn observation endpoint (the live side of <see cref="IHookObservationChannel.OpenForTurnAsync"/>).
/// The App consumes <see cref="ObserveAsync"/> for chips + the cap-meter read + the MA14 feed, and answers each
/// pending approve-each confirm via <see cref="RequestDecisionAsync"/>. Disposing it tears down the OS primitive
/// and (per the fail-closed contract) causes any still-blocked hook to deny.
/// </summary>
public interface IHookObservationEndpoint : IAsyncDisposable
{
    /// <summary>
    /// The stream of per-op REPORTs the hook sends after each autonomous decision (spec §4 step 1). The App
    /// folds each event into: the file-op CHIP, the live cap-meter READ (a read of the already-incremented
    /// O_EXCL cap-state — invariant 4), and the MA14 observed-callback feed (correlated by
    /// <see cref="HookOpEvent.ToolUseId"/>). The sequence completes when the turn ends or the endpoint is
    /// disposed.
    /// </summary>
    /// <param name="ct">Cancellation = the turn ended / a Stop / a no-reply kill (ends the observe loop).</param>
    IAsyncEnumerable<HookOpEvent> ObserveAsync(CancellationToken ct);

    /// <summary>
    /// Block for the App's narrowing approve-each answer to one hook-ALLOWED op (spec §4 step 2). The host
    /// calls this while the hook is synchronously blocked; the App shows its in-panel confirm (<b>Deny
    /// focused</b>, design §6.5) and returns the answer, which the hook AND-combines with its own (already
    /// <see cref="HookDecision.Allow"/>) decision. A cancelled <paramref name="ct"/> (the no-reply deadline,
    /// a Stop, or a channel fault) MUST resolve to <see cref="HookDecisionResponse.Deny"/> — never throw a
    /// fault that a caller might (mis)read as an allow (invariants 2/6). The returned response's
    /// <see cref="HookDecisionResponse.ToolUseId"/> equals <paramref name="request"/>'s.
    /// </summary>
    /// <param name="request">The approve-each request raised for a hook-allowed op (carries the <c>tool_use_id</c>).</param>
    /// <param name="ct">The fail-closed deadline — when it trips, the answer is DENY (invariant 2).</param>
    Task<HookDecisionResponse> RequestDecisionAsync(HookDecisionRequest request, CancellationToken ct);
}
