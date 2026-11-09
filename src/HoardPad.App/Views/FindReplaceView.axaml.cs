// FindReplaceView.axaml.cs — the M6.7 docked Find/Replace panel wiring (FR-17 / FR-18, design §3.5).
//
// Framework wiring only (the testable orchestration lives in FindReplaceViewModel): focus the find box on
// summon, recompute the live count when the panel re-binds to a new active buffer, route F3 / Shift+F3 to
// next/previous + Esc to close, and expose FocusFind() so Ctrl+F / Ctrl+H can place the caret in the box.

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Views;

/// <summary>The docked find/replace panel (FR-17 / FR-18). Custom — supersedes the stock SearchPanel (design §3.5).</summary>
internal sealed partial class FindReplaceView : UserControl
{
    private TextBox _findBox = null!;

    public FindReplaceView()
    {
        InitializeComponent();
        _findBox = this.FindControl<TextBox>("FindBox")!;
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private FindReplaceViewModel? ViewModel => DataContext as FindReplaceViewModel;

    /// <summary>
    /// Recompute the live count + focus the find box (Ctrl+F / Ctrl+H summon path — QA4-8).
    /// <see cref="FocusFind"/> is called from inside the VM's own IsVisible <c>PropertyChanged</c> cascade
    /// (see <c>MainWindow.OnFindReplacePropertyChanged</c>), i.e. WHILE the panel's own IsVisible binding is
    /// still propagating. Calling <c>TextBox.Focus()</c> synchronously at that point silently no-ops — Avalonia
    /// reads the raw <c>IsVisible</c> property as already true, but the focus system's effective-visibility
    /// bookkeeping has not settled yet, so the request is dropped (confirmed empirically: an inline call never
    /// took focus even though <c>panel.IsVisible</c> already read <c>true</c> at that point; deferring one
    /// dispatcher tick fixed it). Posting at <see cref="DispatcherPriority.Loaded"/> — same priority
    /// <c>ChecklistView.axaml.cs</c> uses for its analogous "focus a control once the visual tree has caught
    /// up" deferrals (row-textbox focus after Swap/RebuildVisibleRows) — defers the actual focus call until
    /// after layout/render have settled but still ahead of input processing, so the request lands reliably
    /// and, in practice, on the very next frame (imperceptible to the user).
    /// </summary>
    public void FocusFind()
    {
        ViewModel?.RecomputeMatches();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ViewModel is not { IsVisible: true })
                    return; // closed again before the deferred focus ran — don't steal focus back.
                _findBox.Focus();
                _findBox.SelectAll();
            },
            DispatcherPriority.Loaded);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.IsVisible = false;          // Esc closes the panel (design §3.5).
                e.Handled = true;
                break;
            case Key.F3 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                vm.FindPreviousCommand.Execute(null); // Shift+F3 → previous match (FR-18).
                e.Handled = true;
                break;
            case Key.F3:
                vm.FindNextCommand.Execute(null);     // F3 → next match (FR-18).
                e.Handled = true;
                break;
            case Key.Enter:
                vm.FindNextCommand.Execute(null);     // Enter in the find box also advances (Notepad++ idiom).
                e.Handled = true;
                break;
            default:
                break;
        }
    }
}
