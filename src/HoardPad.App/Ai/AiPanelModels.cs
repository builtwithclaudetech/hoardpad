// AiPanelModels.cs — the M6.6 AI-panel view-model display models (design §6).
//
// The small, observable display shapes the FR-22 AI panel renders: a transcript line (a
// chat message, an inline file-op chip, or a safety-event chip), a cap meter, a sandbox
// indicator state, and a pending-proposal row. These are App-layer view models — they do
// NOT duplicate any Core.Ai safety logic; they are the rendered projection of it.

using CommunityToolkit.Mvvm.ComponentModel;
using HoardPad.Core.Records;

namespace HoardPad.App.Ai;

/// <summary>What a transcript line renders as (design §6.1/§6.4) — drives the chip styling.</summary>
public enum TranscriptKind
{
    /// <summary>A "You:" user prompt line.</summary>
    UserMessage,

    /// <summary>An "AI:" streamed assistant message line.</summary>
    AssistantMessage,

    /// <summary>An inline file-op chip (▣ edit / ＋ create / ⌦ delete / ⇄ move) with an outcome badge (§6.4).</summary>
    FileOpChip,

    /// <summary>A safety-event chip (sandbox-block, degrade, cap, stopped) in a danger/warning token (§6.7).</summary>
    SafetyEventChip,

    /// <summary>A neutral system/status line (e.g. "— stopped —", read-only refusal — §6.4).</summary>
    SystemNote,
}

/// <summary>The outcome badge vocabulary shown on a file-op chip + activity-log row (design §6.4, llm F8).</summary>
public enum OpOutcomeBadge
{
    /// <summary>An auto-apply op that applied without a confirm.</summary>
    Applied,

    /// <summary>A user-confirmed approve-each op.</summary>
    Approved,

    /// <summary>An op the sandbox / hook refused (out of sandbox, denied).</summary>
    Blocked,

    /// <summary>An op the user reversed.</summary>
    Undone,

    /// <summary>An op parked in the Pending surface after a fail-closed no-reply timeout (FR-39).</summary>
    Queued,
}

/// <summary>
/// One transcript line in the AI conversation (design §6.4). A chat message carries
/// <see cref="Text"/>; a file-op chip carries <see cref="Op"/> + <see cref="TargetPath"/> +
/// <see cref="Badge"/>; a safety-event chip carries <see cref="Text"/> in the danger/warning token.
/// </summary>
public sealed partial class TranscriptLine : ObservableObject
{
    /// <summary>What this line renders as.</summary>
    public TranscriptKind Kind { get; init; }

    /// <summary>The message / note / chip-detail text (mutable so a streaming assistant line appends).</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>The op type for a <see cref="TranscriptKind.FileOpChip"/> line (§6.4 chip glyph).</summary>
    public ActivityOp Op { get; init; }

    /// <summary>The op's resolved target path for a file-op chip.</summary>
    public string? TargetPath { get; init; }

    /// <summary>The outcome badge for a file-op chip (design §6.4).</summary>
    [ObservableProperty]
    private OpOutcomeBadge _badge;

    /// <summary><c>true</c> for the danger-token safety chips (sandbox block, degrade) — drives styling.</summary>
    public bool IsDanger { get; init; }
}

/// <summary>
/// One of the three live per-turn cap meters (design §6.5): created N/25, destructive N/10,
/// touched N/20. The meter binds to the single conversation-keyed cap-state — NOT a per-backend
/// counter — so a backend switch never resets it (§6.3/§6.5).
/// </summary>
public sealed partial class CapMeterViewModel : ObservableObject
{
    /// <summary>The meter label ("created" / "destructive" / "touched").</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The current count this conversation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    [NotifyPropertyChangedFor(nameof(Emphasis))]
    private int _count;

    /// <summary>The cap value (FR-11=25 / FR-38=10 / FR-40=20).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    [NotifyPropertyChangedFor(nameof(Emphasis))]
    private int _cap;

    /// <summary>The "N/CAP" text — the non-color cue (§10.3).</summary>
    public string Display => $"{Count}/{Cap}";

    /// <summary>
    /// The emphasis tier (design §6.5, llm F10): normal &lt; 60%, warning at 60%, danger at 80% of cap,
    /// so a building injection run is visible BEFORE it trips. Bound to a brand token by the view.
    /// </summary>
    public CapEmphasis Emphasis
    {
        get
        {
            if (Cap <= 0)
                return CapEmphasis.Normal;
            // Integer thresholds: warning at ≥ 60%, danger at ≥ 80% of the cap.
            if (Count * 100 >= Cap * 80)
                return CapEmphasis.Danger;
            if (Count * 100 >= Cap * 60)
                return CapEmphasis.Warning;
            return CapEmphasis.Normal;
        }
    }
}

/// <summary>The cap-meter emphasis tier (design §6.5 / §10.3) — mapped to a brand token by the view.</summary>
public enum CapEmphasis
{
    /// <summary>Below 60% of the cap — on-surface token.</summary>
    Normal,

    /// <summary>At/above 60% of the cap — warning token (the build-up is visible).</summary>
    Warning,

    /// <summary>At/above 80% of the cap — danger token (close to / at the backstop).</summary>
    Danger,
}

/// <summary>
/// The always-visible sandbox-scope line state (design §6.7). The resolved open-folder root the
/// AI is scoped to, plus the arming state. With no folder open, write agency is disabled (§6.8).
/// </summary>
public enum SandboxState
{
    /// <summary>
    /// No folder open → AI file-write agency disabled; read/chat allowed (§6.8, FR-26). QA2-18 (the maintainer's lazy
    /// decision): this is the panel's RESTING state at startup — the OS folder picker is NEVER fired here.
    /// It fires only on the first write-mode AI action (AiPanelViewModel.RequestWriteFolderAsync).
    /// </summary>
    NoFolder,

    /// <summary>
    /// FS-1 (Decision A, FINAL): a folder IS bound (the root FolderRootResolver derived for this launch)
    /// but write agency has not been armed yet — arming defers to the first write-mode AI action (no OS
    /// picker; the root is already known). Read/chat is available; the sandbox-scope line shows the
    /// resolved root so the safety boundary stays legible even before arming (design DP-2).
    /// </summary>
    NotYetArmed,

    /// <summary>Folder open, self-test passed → write agency armed (§6.7 Armed).</summary>
    Armed,

    /// <summary>Self-test failed → write agency stays disabled; read/chat allowed (§6.7 Self-test failed).</summary>
    SelfTestFailed,

    /// <summary>Mid-turn degrade detected → forced approve-each for the session (§6.7 Degraded / FR-10(c)).</summary>
    Degraded,
}

/// <summary>
/// One parked FR-39 pending proposal as the Pending tab renders it (design §6.6.4). It wraps the
/// Core.Ai <see cref="HoardPad.Core.Ai.FileOps.PendingProposal"/> with the display fields the row needs.
/// </summary>
public sealed partial class PendingProposalViewModel : ObservableObject
{
    /// <summary>The stable proposal id the apply/discard actions key on.</summary>
    public string ProposalId { get; init; } = string.Empty;

    /// <summary>The op type glyph/label (create / edit / …).</summary>
    public ActivityOp Op { get; init; }

    /// <summary>The proposed target path.</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>The "(proposed HH:mm, timed out)" subtitle.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// The raw JSON string from the stream-json <c>tool_use.input</c> block, captured at park time
    /// (FR-39 replay-rebuild — MF1.4). Used by <c>ApplyPendingAsync</c> to reconstruct the target path +
    /// proposed content bytes without touching the deny_by_default hook or the hook channel.
    /// <c>null</c> when the proposal was parked before MF1.4 went in or when no stream-json input was
    /// available at park time (the apply path handles the null case by declining the replay).
    /// </summary>
    public string? CapturedToolInputJson { get; init; }

    /// <summary>
    /// OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): the single RESOLVED per-file grant path that
    /// authorized this op's out-of-folder write, persisted at park time from the turn's armed grant set. When
    /// non-null, <c>ApplyPendingAsync</c> reads the before-image through the UNION reader with this single-
    /// element grant and replays through a per-op union-gated service — so a checked out-of-folder write is
    /// replayable (AC-MD-2). <c>null</c> for an in-root op (replayed through the unchanged root-only path) OR
    /// for an out-of-folder op that was never granted (the union reader then Refuses → Blocked chip).
    /// </summary>
    public string? GrantedResolvedPath { get; init; }
}

/// <summary>
/// Backend connection state for the BUG-4 diagnostic status line.
/// Maps to <c>Brand.Success.Brush</c> / <c>Brand.Warning.Brush</c> / <c>Brand.Danger.Brush</c> via
/// <see cref="StatusBrushConverter"/>.
/// </summary>
public enum BackendConnectionState
{
    /// <summary>Initial state — no check performed yet. Renders in <c>Brand.Muted.Brush</c>.</summary>
    Disconnected,

    /// <summary>Last turn or self-test succeeded. Renders in <c>Brand.Success.Brush</c>.</summary>
    Connected,

    /// <summary>A turn or self-test is in progress. Renders in <c>Brand.Warning.Brush</c>.</summary>
    Connecting,

    /// <summary>Last turn or self-test failed. Renders in <c>Brand.Danger.Brush</c>.</summary>
    Error,
}
