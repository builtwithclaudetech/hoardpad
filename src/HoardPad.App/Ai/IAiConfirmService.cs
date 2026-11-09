// IAiConfirmService.cs — the M6.6 safety-confirm seam (design §6.5/§6.6.1, FR-10/FR-27).
//
// The three safety-critical confirms (approve-each diff, over-cap irreversible, cap-reached) are
// requested by the panel view-model through this seam so the view-model stays UI-thread- and
// dialog-framework-agnostic and fully unit-testable. The production implementation shows the inline
// transcript confirm with the EXACT default focus + wording design §6 pins; a test fakes the seam to
// assert focus + wording + the answer flow without a real dialog.
//
// The default-focus + button-wording invariants are SAFETY-CRITICAL (design §6 — "Deny focused",
// "Stop focused", "(will ask again)"). They are encoded in the request shapes below (the view binds
// to them; the test asserts them), so a build cannot silently drop them.

namespace HoardPad.App.Ai;

/// <summary>
/// Shows the FR-10 safety confirms and returns the user's decision. The view-model awaits a decision
/// off the live turn path; the production confirm runs on the UI thread (it is a dialog). A no-reply
/// timeout is the view-model's concern (TimeProvider-driven, §6.6.1) — it cancels the pending confirm.
/// </summary>
public interface IAiConfirmService
{
    /// <summary>
    /// Show the approve-each per-write confirm (design §6.6.1). <b>[Deny] has initial focus</b> (web F4 —
    /// the conservative action) so an accidental Enter never applies an AI write. For an over-cap op the
    /// request carries <see cref="ApproveEachRequest.IsIrreversible"/> = <c>true</c> (the §6.6.1
    /// undo-unavailable variant: the button reads "Approve (irreversible)", still Deny-focused).
    /// </summary>
    /// <returns><c>true</c> = Approve (apply the op); <c>false</c> = Deny (reject it).</returns>
    Task<bool> ConfirmApproveEachAsync(ApproveEachRequest request, CancellationToken ct);

    /// <summary>
    /// Show the cap-reached confirm (design §6.5). <b>[Stop] has initial focus</b> (web F4) and the action
    /// button reads "Allow next file/op (will ask again)" — the cap does NOT re-arm, so the label MUST
    /// convey ongoing-confirm semantics (B1). <c>true</c> = allow one more op (and every subsequent op of
    /// that class confirms again); <c>false</c> = Stop the turn.
    /// </summary>
    /// <returns><c>true</c> = Allow next (one more op, will ask again); <c>false</c> = Stop the turn.</returns>
    Task<bool> ConfirmCapReachedAsync(CapReachedRequest request, CancellationToken ct);

    /// <summary>
    /// FEAT-14: show the AI-requested-settings-change confirm (approve-each mode only — auto-apply skips
    /// straight to applying, same as a file write). Reuses the SAME Deny-default-focus safety invariant as
    /// <see cref="ConfirmApproveEachAsync"/> — an accidental Enter must never apply an AI-proposed change
    /// to HoardPad's own settings.
    /// </summary>
    /// <returns><c>true</c> = Approve (apply); <c>false</c> = Deny (decline).</returns>
    Task<bool> ConfirmSettingsChangeAsync(SettingsChangeConfirmRequest request, CancellationToken ct);
}

/// <summary>
/// The approve-each / over-cap confirm request (design §6.6.1). Carries the op detail the diff/preview
/// shows + the sandbox line + the irreversible flag (the over-cap undo-unavailable variant).
/// </summary>
/// <param name="OpVerb">The op verb shown in the heading ("edit" / "create" / "overwrite").</param>
/// <param name="TargetPath">The op's target path ("AI wants to edit &lt;path&gt;").</param>
/// <param name="DiffPreview">The +/- diff or content preview (design §6.6.1).</param>
/// <param name="SandboxRoot">The resolved sandbox root for the "in the document folder &lt;root&gt;" line.</param>
/// <param name="IsIrreversible">
/// <c>true</c> for the over-cap undo-unavailable variant (§6.6.1, llm F4): the op exceeds the snapshot
/// size cap so undo is unavailable — the button reads "Approve (irreversible)" and the warning is shown.
/// </param>
public sealed record ApproveEachRequest(
    string OpVerb,
    string TargetPath,
    string DiffPreview,
    string SandboxRoot,
    bool IsIrreversible = false)
{
    /// <summary>The Approve-button label — "Approve (irreversible)" for the over-cap variant (§6.6.1).</summary>
    public string ApproveLabel => IsIrreversible ? "Approve (irreversible)" : "Approve";

    /// <summary>
    /// The default-focused button is ALWAYS Deny (web F4) — encoded so the view binds it and the test
    /// asserts it. The conservative action receives focus for both the normal and irreversible variants.
    /// </summary>
    public static ConfirmButton DefaultFocus => ConfirmButton.Deny;
}

/// <summary>
/// The cap-reached confirm request (design §6.5). Carries the tripped cap + count so the heading names
/// the specific cap (llm F3), and the "(will ask again)" action label (B1 — no re-arm).
/// </summary>
/// <param name="Cap">The cap that tripped (drives the heading + the Allow-button noun).</param>
/// <param name="Count">The cap value (e.g. 20) shown as "N/N" in the heading.</param>
public sealed record CapReachedRequest(TrippedCap Cap, int Count)
{
    /// <summary>The heading naming the specific cap + count (design §6.5 table, llm F3).</summary>
    public string Heading => Cap switch
    {
        TrippedCap.Created => $"⚠ Per-turn cap reached — files created: {Count}/{Count}",
        TrippedCap.Destructive => $"⚠ Per-turn cap reached — destructive ops: {Count}/{Count}",
        TrippedCap.Touched => $"⚠ Per-turn cap reached — files touched: {Count}/{Count}",
        _ => $"⚠ Per-turn cap reached: {Count}/{Count}",
    };

    /// <summary>
    /// The action-button label — MUST convey ongoing-confirm semantics (B1, binding): "(will ask again)"
    /// because the cap does NOT re-arm (§6.5). "Allow next op" for the destructive cap, "Allow next file"
    /// otherwise.
    /// </summary>
    public string AllowLabel => Cap == TrippedCap.Destructive
        ? "Allow next op (will ask again)"
        : "Allow next file (will ask again)";

    /// <summary>The default-focused button is ALWAYS Stop (web F4) for this safety gate — encoded for the view + test.</summary>
    public static ConfirmButton DefaultFocus => ConfirmButton.Stop;
}

/// <summary>
/// FEAT-14: the AI-requested-settings-change confirm request. Carries the field's human-readable label
/// (not the raw <c>HoardPadSettings</c> property name) and the old/new display strings the confirm dialog
/// and the audit transcript chip both show.
/// </summary>
/// <param name="FieldLabel">The human-readable setting name (e.g. "Editor font size").</param>
/// <param name="OldValue">The current value's display string.</param>
/// <param name="NewValue">The proposed value's display string.</param>
public sealed record SettingsChangeConfirmRequest(string FieldLabel, string OldValue, string NewValue)
{
    /// <summary>Deny is ALWAYS the default focus — the same conservative-default-focus invariant every FR-10 confirm uses.</summary>
    public static ConfirmButton DefaultFocus => ConfirmButton.Deny;
}

/// <summary>The buttons a safety confirm can default-focus (design §6 — the default-focus invariant).</summary>
public enum ConfirmButton
{
    /// <summary>Approve / Allow-next — the affirmative action (never the default focus on a safety gate).</summary>
    Approve,

    /// <summary>Deny — the conservative action; default focus for the approve-each confirms.</summary>
    Deny,

    /// <summary>Stop — ends the turn; default focus for the cap-reached confirm.</summary>
    Stop,
}
