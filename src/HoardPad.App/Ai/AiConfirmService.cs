// AiConfirmService.cs — the production safety-confirm service (M6.6, design §6.5/§6.6.1).
//
// Hosts the AiConfirmView as an IN-PANEL overlay (NOT a separate OS window — FR-22): it adds the
// confirm control to a host Panel (the AI panel's overlay container), awaits the user's decision (or a
// caller cancellation = the no-reply fail-closed deadline), then removes it. The control sets the
// safety-critical default focus (Deny / Stop) from the request; this service only hosts + awaits.

using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using HoardPad.App.Views;

namespace HoardPad.App.Ai;

/// <summary>
/// Production <see cref="IAiConfirmService"/>. Shows the inline <see cref="AiConfirmView"/> over a host
/// <see cref="Panel"/> (the AI panel overlay) — never a separate window — and resolves the user's
/// decision. A caller cancellation (the no-reply timeout) removes the confirm and resolves it as the
/// conservative answer (Deny / Stop) so the turn fails closed.
/// </summary>
public sealed class AiConfirmService : IAiConfirmService
{
    private readonly Panel _overlayHost;

    /// <summary>Construct the service over the panel's overlay host (where the confirm control is shown).</summary>
    public AiConfirmService(Panel overlayHost)
    {
        ArgumentNullException.ThrowIfNull(overlayHost);
        _overlayHost = overlayHost;
    }

    /// <inheritdoc />
    public Task<bool> ConfirmApproveEachAsync(ApproveEachRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = new AiConfirmView();
        // SAFETY: attach to the visual tree BEFORE calling ShowApproveEach so FocusDefault runs on an
        // attached control. Focus on an unattached control is a silent no-op; after attach Avalonia would
        // focus the first declared button (AffirmativeButton = Approve), inverting the Deny-focused invariant.
        return ShowAndAwaitAsync(view, show: v => v.ShowApproveEach(request), conservativeAnswerOnCancel: false, ct);
    }

    /// <inheritdoc />
    public Task<bool> ConfirmCapReachedAsync(CapReachedRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = new AiConfirmView();
        // SAFETY: same attach-before-show ordering — the Stop-focused invariant depends on the control
        // being in the visual tree when FocusDefault fires.
        return ShowAndAwaitAsync(view, show: v => v.ShowCapReached(request), conservativeAnswerOnCancel: false, ct);
    }

    /// <inheritdoc />
    public Task<bool> ConfirmSettingsChangeAsync(SettingsChangeConfirmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = new AiConfirmView();
        // SAFETY: same attach-before-show ordering — the Deny-focused invariant depends on the control
        // being in the visual tree when FocusDefault fires.
        return ShowAndAwaitAsync(view, show: v => v.ShowSettingsChange(request), conservativeAnswerOnCancel: false, ct);
    }

    private Task<bool> ShowAndAwaitAsync(AiConfirmView view, Action<AiConfirmView> show, bool conservativeAnswerOnCancel, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Decided(object? _, bool answer)
        {
            view.Decided -= Decided;
            _overlayHost.Children.Remove(view);
            tcs.TrySetResult(answer);
        }

        view.Decided += Decided;
        // Attach FIRST so the control is in the visual tree, THEN call show(view) which configures the
        // control and calls Focus() on the conservative button. Attaching before show is the key ordering:
        // Focus() on an attached control in an active focus scope works immediately; Focus() on an
        // unattached control is a silent no-op (Avalonia ignores it if there is no visual-tree scope).
        _overlayHost.Children.Add(view);
        show(view);

        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                view.Decided -= Decided;
                _overlayHost.Children.Remove(view);
                tcs.TrySetResult(conservativeAnswerOnCancel);
            });
        }

        return tcs.Task;
    }
}

/// <summary>
/// The fail-closed default <see cref="IAiConfirmService"/> the panel view-model holds at composition time,
/// before the panel view binds the production <see cref="AiConfirmService"/> over its overlay host. It
/// always returns the CONSERVATIVE answer (Deny / Stop) so an unconfigured confirm can never authorize an
/// AI file op — the safe default. The view swaps in the real service on attach (it can show the dialog).
/// </summary>
public sealed class FailClosedConfirmService : IAiConfirmService
{
    /// <inheritdoc />
    public Task<bool> ConfirmApproveEachAsync(ApproveEachRequest request, CancellationToken ct) =>
        Task.FromResult(false); // Deny.

    /// <inheritdoc />
    public Task<bool> ConfirmCapReachedAsync(CapReachedRequest request, CancellationToken ct) =>
        Task.FromResult(false); // Stop.

    /// <inheritdoc />
    public Task<bool> ConfirmSettingsChangeAsync(SettingsChangeConfirmRequest request, CancellationToken ct) =>
        Task.FromResult(false); // Deny.
}
