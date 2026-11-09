// IAiTurnRunner.cs — the M6.6 App-layer turn-runner seam (design §6.4, architecture §3.4/§4.2).
//
// The AI-panel view-model drives ONE in-flight turn through this seam. The production
// implementation (AiTurnRunner) composes the protected safety spine — ArchetypeAOrchestration
// (Core.Ai), IAgentBackend + IToolPermissionHookHost (Host) — and runs the stream pump + the
// process-tree kill OFF the UI thread (architecture §3.4). The view-model NEVER touches a process,
// the stream pump, or the kill directly; it consumes the parsed turn events this seam yields and
// calls StopAsync (which maps to IAgentTurn.KillTreeAsync) for the instant-stop (NFR-8).
//
// Factoring the off-UI process plumbing behind this seam keeps the panel view-model fully unit-
// testable with a scripted fake runner (deterministic stream-json sequence) WITHOUT re-implementing
// any safety logic — the production runner does the real ArchetypeAOrchestration.Begin / self-test /
// liveness fold; the fake drives the same TurnEvent contract.

using HoardPad.Core.Records;

namespace HoardPad.App.Ai;

/// <summary>
/// Runs one AI turn off the UI thread and yields parsed turn events the panel renders (design §6.4).
/// The implementation owns the process, the stream pump, and the process-tree kill (architecture §3.4);
/// the view-model owns only the UX projection + the user controls (Stop / confirm).
/// </summary>
public interface IAiTurnRunner
{
    /// <summary>
    /// Start a turn and stream its events. The implementation runs the launch + stream pump off the UI
    /// thread (architecture §3.4); the returned async sequence is consumed by the view-model, which
    /// marshals each event onto the dispatcher. The turn ends when the sequence completes (the stream
    /// ended) or <paramref name="ct"/> is cancelled (a Stop / no-reply kill).
    /// </summary>
    /// <param name="request">The prompt + the resolved agency posture for this turn.</param>
    /// <param name="ct">Cancellation = a Stop or a no-reply fail-closed kill (the runner kills the tree).</param>
    IAsyncEnumerable<TurnEvent> RunAsync(TurnRequest request, CancellationToken ct);

    /// <summary>
    /// Kill the in-flight turn's process tree immediately (NFR-8 — maps to
    /// <see cref="HoardPad.Core.Host.Abstractions.IAgentTurn.KillTreeAsync"/>). Safe to call when no turn
    /// is in flight (a no-op). Runs off the UI thread; the view-model reflects the stop without waiting.
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// The input to a turn: the prompt and the resolved write-agency posture (whether write tools are armed
/// for this turn, after the self-test gate — design §6.2/§6.9). The view-model resolves the posture via
/// <see cref="HoardPad.Core.Ai.Provider.AgencyMapping"/> before starting the turn; the runner composes
/// the FR-24 §4.1 launch contract via <see cref="HoardPad.Core.Ai.Provider.ArchetypeAOrchestration.Begin"/>.
/// </summary>
/// <param name="Prompt">The user's prompt text (FR-25).</param>
/// <param name="EffectiveMode">The effective agency mode that gates this turn (post self-test gate, MA13).</param>
/// <param name="WriteAgencyArmed"><c>true</c> only when a write-capable mode passed the self-test (FR-24 §3).</param>
/// <param name="ModelId">BUG-5: the AI model override (e.g. <c>claude-opus-4-8</c>); <c>null</c> = backend default.</param>
/// <param name="EffortLevel">BUG-5: the AI reasoning effort override (<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>); <c>null</c> = backend default.</param>
/// <param name="FileGrantPaths">
/// PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): the resolved, normcased out-of-folder grant paths for
/// this turn — the SAME set the panel built the turn's <c>UnionSandboxGate</c>/<c>FileGrantSet</c> from
/// (Archetype B), so Archetype A (claude -p) enforces the identical set. When non-empty AND
/// <see cref="WriteAgencyArmed"/>, the runner writes these to a host grants file and injects
/// <c>HOARDPAD_FILE_GRANTS</c> onto the spawned process so the hook grant leg admits exactly these paths.
/// <c>null</c>/empty ⇒ the env var is never set (root-only, FR-MD-10) — byte-identical to today.
/// </param>
public sealed record TurnRequest(
    string Prompt,
    AgencyMode EffectiveMode,
    bool WriteAgencyArmed,
    string? ModelId = null,
    string? EffortLevel = null,
    System.Collections.Generic.IReadOnlyList<string>? FileGrantPaths = null);

/// <summary>What a streamed <see cref="TurnEvent"/> represents (design §6.4).</summary>
public enum TurnEventKind
{
    /// <summary>A partial/complete streamed assistant text token (FR-24 --include-partial-messages).</summary>
    AssistantText,

    /// <summary>The agent applied (or attempted) a file op — render a chip + advance the caps (§6.4/§6.5).</summary>
    FileOp,

    /// <summary>A safety event the runner detected mid-turn (sandbox block / degrade / cap-reached, §6.7).</summary>
    SafetyEvent,

    /// <summary>
    /// A write tool-use input capture event (MF1.4 / FR-39 replay). Carries the stream-json
    /// <c>tool_use.input</c> JSON for a write tool-use block so the panel can cache it under
    /// <c>ToolUseId</c> before the hook ask arrives. The panel uses the cached input at park time
    /// to populate <see cref="PendingProposalViewModel.CapturedToolInputJson"/>. Renders nothing in the
    /// transcript — the panel consumes it silently and drops it (no chip, no text).
    /// </summary>
    WriteToolInput,
}

/// <summary>The safety-event sub-kind a <see cref="TurnEventKind.SafetyEvent"/> carries (design §6.5/§6.7).</summary>
public enum SafetyEventKind
{
    /// <summary>A write the sandbox refused — out of the open-folder root (§6.7 SC-5(d)).</summary>
    SandboxBlocked,

    /// <summary>A mid-turn liveness/hook degrade — forces approve-each for the session (§6.7 / FR-10(c)).</summary>
    Degraded,

    /// <summary>A per-turn cap was reached — the turn pauses for the cap-reached confirm (§6.5).</summary>
    CapReached,
}

/// <summary>
/// One parsed turn event (design §6.4). An assistant-text event carries <see cref="Text"/>; a file-op
/// event carries the op + path + outcome + the cap snapshot AFTER accounting; a safety event carries its
/// kind + a message + (for a cap-reached event) the tripped cap.
/// </summary>
/// <param name="Kind">The event kind.</param>
/// <param name="Text">Assistant text, or a safety-event message.</param>
/// <param name="Op">The op type for a file-op event.</param>
/// <param name="TargetPath">The resolved target path for a file-op event.</param>
/// <param name="Outcome">The file-op outcome badge.</param>
/// <param name="Caps">The cap snapshot after this op was accounted (so the meters advance from one shared state).</param>
/// <param name="SafetyKind">The safety-event sub-kind (for a <see cref="TurnEventKind.SafetyEvent"/>).</param>
/// <param name="TrippedCap">The cap that tripped (for a <see cref="SafetyEventKind.CapReached"/> event).</param>
/// <param name="ToolUseId">
/// The <c>tool_use_id</c> for a <see cref="TurnEventKind.WriteToolInput"/> event (MF1.4 — FR-39 replay
/// tool-input cache). Unused for all other event kinds.
/// </param>
/// <param name="RawToolInputJson">
/// The raw <c>tool_use.input</c> JSON string for a <see cref="TurnEventKind.WriteToolInput"/> event, or
/// <c>null</c> when the input block was absent/unparseable. Unused for all other event kinds.
/// </param>
public sealed record TurnEvent(
    TurnEventKind Kind,
    string? Text = null,
    ActivityOp Op = ActivityOp.Read,
    string? TargetPath = null,
    OpOutcomeBadge Outcome = OpOutcomeBadge.Applied,
    CapSnapshot? Caps = null,
    SafetyEventKind SafetyKind = SafetyEventKind.SandboxBlocked,
    TrippedCap TrippedCap = TrippedCap.None,
    string? ToolUseId = null,
    string? RawToolInputJson = null)
{
    /// <summary>An assistant streamed-text event.</summary>
    public static TurnEvent Assistant(string text) => new(TurnEventKind.AssistantText, Text: text);

    /// <summary>A file-op event carrying the post-accounting cap snapshot.</summary>
    public static TurnEvent FileOp(ActivityOp op, string path, OpOutcomeBadge outcome, CapSnapshot caps) =>
        new(TurnEventKind.FileOp, Op: op, TargetPath: path, Outcome: outcome, Caps: caps);

    /// <summary>A sandbox-blocked safety event.</summary>
    public static TurnEvent Blocked(string message) =>
        new(TurnEventKind.SafetyEvent, Text: message, SafetyKind: SafetyEventKind.SandboxBlocked);

    /// <summary>A mid-turn degrade safety event.</summary>
    public static TurnEvent Degrade(string message) =>
        new(TurnEventKind.SafetyEvent, Text: message, SafetyKind: SafetyEventKind.Degraded);

    /// <summary>A cap-reached safety event (the turn pauses for the cap-reached confirm).</summary>
    public static TurnEvent Cap(TrippedCap cap, string message, CapSnapshot caps) =>
        new(TurnEventKind.SafetyEvent, Text: message, SafetyKind: SafetyEventKind.CapReached, TrippedCap: cap, Caps: caps);

    /// <summary>
    /// A write tool-input capture event (MF1.4 / FR-39 replay). Carries the stream-json
    /// <c>tool_use.input</c> JSON for one write tool-use so the panel can pre-cache it before the hook
    /// ask arrives. The panel consumes this event silently — it renders nothing in the transcript.
    /// </summary>
    public static TurnEvent WriteInput(string toolUseId, string? rawInputJson) =>
        new(TurnEventKind.WriteToolInput, ToolUseId: toolUseId, RawToolInputJson: rawInputJson);
}

/// <summary>The three live cap counts after an op was accounted (design §6.5) — one shared snapshot.</summary>
/// <param name="Created">FR-11 files created this conversation.</param>
/// <param name="Destructive">FR-38 destructive ops this conversation.</param>
/// <param name="Touched">FR-40 distinct files touched this conversation.</param>
public readonly record struct CapSnapshot(int Created, int Destructive, int Touched);

/// <summary>Which per-turn cap a cap-reached confirm concerns (design §6.5 — drives the heading + button label).</summary>
public enum TrippedCap
{
    /// <summary>No cap tripped.</summary>
    None,

    /// <summary>FR-11 create cap (heading "files created: 25/25", button "Allow next file").</summary>
    Created,

    /// <summary>FR-38 destructive cap (heading "destructive ops: 10/10", button "Allow next op").</summary>
    Destructive,

    /// <summary>FR-40 distinct-files cap (heading "files touched: 20/20", button "Allow next file").</summary>
    Touched,
}
