using System.Collections.Immutable;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// One parked Archetype-A proposal (architecture §4.6, PRD FR-39): an <c>approve-each</c> op that the
/// no-reply deadline failed closed. It holds the captured <c>tool_input</c> (the full PreToolUse event
/// JSON — it fully describes the proposed create/overwrite/edit) plus the correlation ids, so a later
/// user-initiated apply can rebuild the <see cref="FileOpRequest"/> and run it through the SAME shared
/// file-ops service as an Archetype-B op (NOT by re-launching <c>claude</c> or re-invoking the hook).
/// </summary>
/// <remarks>
/// Replay is safe because a PreToolUse-denied/killed op left NO partial on-disk state (SPIKE-3 C3) — there
/// is nothing half-written to reconcile. The replayed apply still runs the full sandbox + before-image +
/// log/journal path; it is simply governed by FR-10(b) per-op confirm and is NOT counted against the
/// per-turn caps (architecture §4.6 — set via <see cref="FileOpContext.CountsAgainstPerTurnCaps"/>).
/// </remarks>
/// <param name="ProposalId">A stable id for this parked proposal (the apply/discard actions key on it).</param>
/// <param name="RequestId">The <c>claude -p</c> invocation (§9.3 requestId) the op belonged to.</param>
/// <param name="OriginConversationId">
/// The conversation the proposal was parked under — recorded for audit only. A later apply is typically a
/// NEW user-initiated conversation (possibly after the origin conversation's cap-state was reset, §4.4),
/// which is precisely why the replay is not counted against the (possibly-gone) origin per-turn caps.
/// </param>
/// <param name="CapturedToolInputJson">The captured PreToolUse <c>tool_input</c> JSON describing the proposed op.</param>
public sealed record PendingProposal(
    string ProposalId,
    string RequestId,
    string OriginConversationId,
    string CapturedToolInputJson);

/// <summary>
/// The immutable FR-39 pending-proposals queue (architecture §4.6). Park (on a fail-closed deny), then
/// apply or discard. Pure value — the durable persistence of the surface is a host concern; this is the
/// domain shape + the park/dequeue/discard transitions.
/// </summary>
public sealed record PendingProposals(ImmutableList<PendingProposal> Proposals)
{
    /// <summary>The empty queue.</summary>
    public static PendingProposals Empty { get; } = new(ImmutableList<PendingProposal>.Empty);

    /// <summary>Park a fail-closed proposal at the tail of the queue (FR-39).</summary>
    public PendingProposals Park(PendingProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return this with { Proposals = Proposals.Add(proposal) };
    }

    /// <summary>Find a parked proposal by id, or <c>null</c> when it is not (or no longer) queued.</summary>
    public PendingProposal? Find(string proposalId) =>
        Proposals.FirstOrDefault(p => string.Equals(p.ProposalId, proposalId, StringComparison.Ordinal));

    /// <summary>Remove a proposal (after a successful apply OR an explicit user discard).</summary>
    public PendingProposals Remove(string proposalId) =>
        this with { Proposals = Proposals.RemoveAll(p => string.Equals(p.ProposalId, proposalId, StringComparison.Ordinal)) };
}
