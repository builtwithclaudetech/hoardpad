// AiConfirmView.axaml.cs — the inline safety-confirm control code-behind (M6.6, design §6.5/§6.6.1).
//
// Renders one of the three FR-10 confirms and SETS THE DEFAULT FOCUS from the request's DefaultFocus
// (the safety-critical invariant: Deny for approve-each, Stop for cap-reached). The control raises
// Decided when a button is pressed; the host AiConfirmService awaits it. Wiring + focus only — no
// policy lives here (the policy is the user's choice + the spine's cap/sandbox check).

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HoardPad.App.Ai;

namespace HoardPad.App.Views;

/// <summary>The in-panel safety-confirm control (NOT a separate OS window — FR-22 in-window rule).</summary>
public sealed partial class AiConfirmView : UserControl
{
    /// <summary>Raised when the user presses a button — <c>true</c> = affirmative (Approve/Allow), <c>false</c> = conservative (Deny/Stop).</summary>
    public event EventHandler<bool>? Decided;

    public AiConfirmView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Configure the control as the approve-each (or over-cap irreversible) confirm (design §6.6.1).
    /// The affirmative button is Approve / "Approve (irreversible)"; the conservative button is Deny and
    /// receives DEFAULT FOCUS (the request's <see cref="ApproveEachRequest.DefaultFocus"/> is Deny — web F4).
    /// </summary>
    public void ShowApproveEach(ApproveEachRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        HeadingText.Text = $"AI wants to {request.OpVerb}  {request.TargetPath}";

        bool hasDiff = request.DiffPreview.Length > 0;
        DiffBorder.IsVisible = hasDiff;
        DiffText.Text = request.DiffPreview;

        SandboxText.Text = request.IsIrreversible
            ? $"in the document folder {request.SandboxRoot}"
            : $"in the document folder {request.SandboxRoot} · undoable";

        WarningText.IsVisible = request.IsIrreversible;
        WarningText.Text = request.IsIrreversible
            ? "⚠ undo unavailable — this file exceeds the snapshot size cap. This op cannot be reversed once applied."
            : string.Empty;

        AffirmativeButton.Content = request.ApproveLabel;   // "Approve" or "Approve (irreversible)"
        ConservativeButton.Content = "Deny";

        // DEFAULT FOCUS — the conservative action (Deny) for both variants (web F4).
        FocusDefault(ApproveEachRequest.DefaultFocus);
    }

    /// <summary>
    /// Configure the control as the cap-reached confirm (design §6.5). The affirmative button is
    /// "Allow next file/op (will ask again)" (B1 — no re-arm); the conservative button is Stop and receives
    /// DEFAULT FOCUS (<see cref="CapReachedRequest.DefaultFocus"/> is Stop — web F4).
    /// </summary>
    public void ShowCapReached(CapReachedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        HeadingText.Text = request.Heading;
        DiffBorder.IsVisible = false;

        SandboxText.Text = "This turn has reached the per-turn limit.";
        WarningText.IsVisible = true;
        WarningText.Text = "This can be a sign of a runaway or injected instruction.";

        AffirmativeButton.Content = request.AllowLabel;     // "Allow next file/op (will ask again)"
        ConservativeButton.Content = "Stop";

        // DEFAULT FOCUS — Stop, the conservative action for this safety gate (web F4).
        FocusDefault(CapReachedRequest.DefaultFocus);
    }

    /// <summary>
    /// FEAT-14: configure the control as the AI-requested-settings-change confirm. The affirmative
    /// button is "Approve"; the conservative button is Deny and receives DEFAULT FOCUS (the request's
    /// <see cref="SettingsChangeConfirmRequest.DefaultFocus"/> is Deny — same web F4 invariant as
    /// <see cref="ShowApproveEach"/>). Reuses the same Heading/Diff/Sandbox-line layout with wording
    /// accurate to a settings change rather than a file op (no "in the document folder …" line, no
    /// irreversible warning — a settings change has no sandbox root and IS revertible via
    /// AiPanelViewModel's revert-last-change).
    /// </summary>
    public void ShowSettingsChange(SettingsChangeConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        HeadingText.Text = $"AI wants to change a setting: {request.FieldLabel}";

        DiffBorder.IsVisible = true;
        DiffText.Text = $"{request.OldValue}  ->  {request.NewValue}";

        SandboxText.Text = "Applies to HoardPad settings.";

        WarningText.IsVisible = false;
        WarningText.Text = string.Empty;

        AffirmativeButton.Content = "Approve";
        ConservativeButton.Content = "Deny";

        // DEFAULT FOCUS — the conservative action (Deny), same invariant as every other FR-10 confirm.
        FocusDefault(SettingsChangeConfirmRequest.DefaultFocus);
    }

    // TODO(M6.3): SetCountdown is wired for the no-reply countdown display (§6.6.1 "⏱ Ns"). It is not
    // called in M6.6 (the countdown tick-down is a M6.3/M6.6.x concern that needs the panel's timer
    // channel). The signature is public so M6.3 can connect it without a code change here.
    /// <summary>Show the no-reply countdown (approve-each only) — makes the fail-closed deadline visible (§6.6.1).</summary>
    public void SetCountdown(int seconds)
    {
        CountdownText.IsVisible = true;
        CountdownText.Text = $"⏱ {seconds}s";
    }

    private void FocusDefault(ConfirmButton button)
    {
        Button target = button == ConfirmButton.Approve ? AffirmativeButton : ConservativeButton;
        // Call Focus() directly — AiConfirmService attaches this control to the visual tree BEFORE calling
        // Show*, so the control is already in a focus scope. Avalonia's FocusManager does not require layout
        // to be committed before Focus() takes; what it requires is that the control be in a visual tree
        // with a focus scope (the parent Panel in the Window provides this). Deferring via Post was the
        // original approach but introduced a race in headless test runs; direct call is correct here.
        target.Focus();
    }

    private void OnAffirmative(object? sender, RoutedEventArgs e) => Decided?.Invoke(this, true);

    private void OnConservative(object? sender, RoutedEventArgs e) => Decided?.Invoke(this, false);
}
