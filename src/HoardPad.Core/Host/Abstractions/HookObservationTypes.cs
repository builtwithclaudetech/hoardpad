// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

// The M11 AI live-write path's pure data shapes (docs/escalation-ai-live-write-path.md §4, §5). These are
// the per-op REPORT the production deny_by_default.py hook sends back to HoardPad over the per-turn
// IHookObservationChannel after it has computed its OWN autonomous deny-by-default + sandbox + cap decision,
// plus the approve-each REQUEST/RESPONSE the channel raises to the App and returns to the blocked hook.
//
// SAFETY POSTURE (spec §5 — baked into these shapes; re-gated in Step 4):
//   - The hook decides FIRST and INDEPENDENTLY (the autonomous path in deny_by_default.py is the FLOOR).
//     HookOpEvent.HookDecision carries that autonomous verdict; the channel can only NARROW it (invariant 1).
//   - These are PURE DATA only — no I/O, no clock, no decision logic. The narrowing AND-combine lives in the
//     hook (Step 2b) and the Core.Ai projection (Step 3 of the contract); the cap advance is a READ of the
//     already-incremented O_EXCL cap-state (invariant 4), never a field these shapes carry as a counter.

/// <summary>
/// The op-class the hook classified one observed write into (spec §4 step 1: <c>op_class</c>). Mirrors the
/// create-vs-overwrite-vs-edit split the hook already computes (the <c>CapClassifier</c> port in
/// <c>deny_by_default.py</c>), surfaced to the App for the file-op CHIP label. NOT a cap counter — the cap
/// advance is a read of the O_EXCL cap-state, never derived from this enum (invariant 4).
/// </summary>
public enum HookOpClass
{
    /// <summary>A create — a <c>Write</c>/<c>Edit</c> whose target did not exist at hook time (FR-11 class).</summary>
    Create,

    /// <summary>A destructive overwrite — a whole-file <c>Write</c> or a ≥X% <c>Edit</c> onto an existing target (FR-38 class).</summary>
    Overwrite,

    /// <summary>A sub-threshold edit — a partial <c>Edit</c> onto an existing target below the FR-38 magnitude (FR-40-only class).</summary>
    Edit,
}

/// <summary>
/// The hook's AUTONOMOUS allow/deny verdict for one op, computed BEFORE the channel report (spec §5
/// invariant 1). This is the FLOOR: a <see cref="Deny"/> stays a deny regardless of any user answer; only a
/// <see cref="Allow"/> is eligible for a narrowing approve-each confirm. The channel and the App can NEVER
/// turn a <see cref="Deny"/> into an allow.
/// </summary>
public enum HookDecision
{
    /// <summary>The hook's autonomous decision was ALLOW (sandbox-contained, under cap). Eligible for a narrowing confirm.</summary>
    Allow,

    /// <summary>The hook's autonomous decision was DENY (out of sandbox, over cap, malformed, or any fail-closed branch). Sticky.</summary>
    Deny,
}

/// <summary>
/// One per-op REPORT the hook sends to HoardPad after computing its autonomous decision (spec §4 step 1).
/// HoardPad uses it for the file-op CHIP, the live cap-meter READ (NOT a counter — the actual advance is a
/// read of the O_EXCL cap-state), and the MA14 observed-callback feed (correlated by <see cref="ToolUseId"/>).
/// Pure data: the hook already did the sandbox + cap + classification work; this only carries the facts.
/// </summary>
/// <param name="ToolUseId">
/// The CLI's <c>tool_use_id</c> the hook saw on stdin — the MA14 correlation key against the stream-json
/// write tool-use side (<c>WriteToolUseEvent</c>). Opaque token; ordinal comparison (not a path).
/// </param>
/// <param name="ToolName">The write tool the agent invoked, as emitted (<c>Write</c> / <c>Edit</c> / <c>Read</c>).</param>
/// <param name="TargetPath">
/// The op's RESOLVED target path (the hook canonicalizes before reporting) — shown on the chip. May be
/// empty when the hook denied before a path could be resolved (a malformed/no-path deny).
/// </param>
/// <param name="OpClass">The create/overwrite/edit class the hook classified the op into (for the chip label).</param>
/// <param name="Magnitude">
/// The proposed post-op byte size the hook saw (the FR-38 magnitude input) — surfaced for the chip's
/// magnitude hint. 0 when unknown (e.g. a denied op the hook never sized). NOT a cap counter.
/// </param>
/// <param name="HookDecision">
/// The hook's AUTONOMOUS allow/deny verdict (the FLOOR — spec §5 invariant 1). The App renders a deny as a
/// blocked chip with NO cap advance; only an allow is eligible for a narrowing approve-each confirm.
/// </param>
public sealed record HookOpEvent(
    string ToolUseId,
    string ToolName,
    string TargetPath,
    HookOpClass OpClass,
    long Magnitude,
    HookDecision HookDecision);

/// <summary>
/// The approve-each REQUEST the channel raises to the App while the hook BLOCKS synchronously (spec §4 step 2,
/// design §6.5 — the FR-10(a)/(b) per-op confirm). Raised ONLY for an op whose
/// <see cref="HookOpEvent.HookDecision"/> is <see cref="HookDecision.Allow"/> (a hook-deny is already final —
/// no confirm is offered, invariant 1). The App shows its in-panel confirm with <b>Deny focused</b> and
/// returns a <see cref="HookDecisionResponse"/>; a no-reply / channel-fault yields a DENY (invariant 2/6).
/// </summary>
/// <param name="ToolUseId">The <c>tool_use_id</c> tying this request to its <see cref="HookOpEvent"/> + the MA14 feed.</param>
/// <param name="ToolName">The write tool the confirm concerns (<c>Write</c> / <c>Edit</c>).</param>
/// <param name="TargetPath">The resolved target path shown in the confirm diff/header (design §6.5).</param>
/// <param name="OpClass">The op class (create/overwrite/edit) shown in the confirm.</param>
public sealed record HookDecisionRequest(
    string ToolUseId,
    string ToolName,
    string TargetPath,
    HookOpClass OpClass);

/// <summary>
/// The App's narrowing answer to a <see cref="HookDecisionRequest"/>, returned to the blocked hook through the
/// channel (spec §4 step 2). The hook AND-combines it with its own (already-<see cref="HookDecision.Allow"/>)
/// autonomous decision: <see cref="Allow"/> applies the write; <see cref="Deny"/> (or no reply, or a channel
/// fault) blocks it (invariants 1, 2, 6). The user can never WIDEN — this response is only consulted when the
/// hook already decided allow.
/// </summary>
/// <param name="ToolUseId">The <c>tool_use_id</c> the answer is for (the hook validates it matches the blocked request).</param>
/// <param name="Allow">
/// <c>true</c> iff the user explicitly allowed the op. The default for any non-answer (timeout, channel fault,
/// torn read) is <c>false</c> (deny) — fail-closed by construction (invariant 2).
/// </param>
public readonly record struct HookDecisionResponse(string ToolUseId, bool Allow)
{
    /// <summary>The fail-closed default answer for a <paramref name="toolUseId"/> — DENY (invariant 2).</summary>
    public static HookDecisionResponse Deny(string toolUseId) => new(toolUseId, Allow: false);

    /// <summary>The user-allow answer for a <paramref name="toolUseId"/> — applies the (already hook-allowed) write.</summary>
    public static HookDecisionResponse Allowed(string toolUseId) => new(toolUseId, Allow: true);
}
