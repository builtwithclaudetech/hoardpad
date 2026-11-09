// RestoreConflictDialog.axaml.cs — the FR-19 restore-conflict MODAL (design §9.6).
//
// Shown on relaunch for a recovered buffer whose on-disk file CHANGED while the app was dead (§9.2
// present-changed-externally). The user picks: keep my recovered version / keep the version on disk /
// show diff. Shown via ShowDialog(owner) so it blocks before HoardPad arms a clobbering Save — never a
// silent stale-buffer restore (FR-19, B7). Keep-disk is the conservative default focus (an accidental
// Enter must not clobber the newer on-disk file with the stale recovered buffer).

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace HoardPad.App.Views;

/// <summary>The user's choice on a restore-conflict (FR-19 / design §9.6).</summary>
public enum RestoreConflictChoice
{
    /// <summary>Keep the version now on disk (the conservative default — discard the recovered buffer).</summary>
    KeepDisk,

    /// <summary>Keep my recovered version (it will overwrite the on-disk file on the next Save).</summary>
    KeepRecovered,

    /// <summary>Show a diff of the recovered buffer vs the on-disk file (then re-decide).</summary>
    ShowDiff,
}

/// <summary>The modal keep-recovered / keep-disk / show-diff prompt for a changed-on-disk file (FR-19).</summary>
internal sealed partial class RestoreConflictDialog : Window
{
    // REVISE-9: track the result explicitly rather than relying on the Closed event receiving the value
    // passed to Close(). If the window is closed with window-X / Esc (without a button press) the value
    // passed to ShowDialog<T> is T's default — for an enum that is member 0, which happens to be KeepDisk
    // only if the enum declaration order is never changed. An explicit field eliminates order-dependence.
    private RestoreConflictChoice _result = RestoreConflictChoice.KeepDisk;

    /// <summary>Design-time / XAML-loader ctor.</summary>
    public RestoreConflictDialog()
    {
        InitializeComponent();
        // When the user closes with window-X / Esc (no button pressed), _result stays KeepDisk — the
        // conservative outcome (never silently restore a stale buffer + arm a clobbering Save, FR-19 / B7).
        Closed += (_, _) => Close(_result);
    }

    private RestoreConflictDialog(string fileName) : this()
    {
        var heading = this.FindControl<TextBlock>("HeadingText")!;
        heading.Text = $"Recovery conflict — {fileName} changed on disk while HoardPad was closed";

        // Keep-disk takes initial focus: an accidental Enter must NOT clobber the newer on-disk file with
        // the stale recovered buffer (the conservative side of FR-19 / B7).
        Opened += (_, _) => this.FindControl<Button>("KeepDiskButton")!.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeepRecovered(object? sender, RoutedEventArgs e)
    {
        _result = RestoreConflictChoice.KeepRecovered;
        Close(_result);
    }

    private void OnKeepDisk(object? sender, RoutedEventArgs e)
    {
        _result = RestoreConflictChoice.KeepDisk;
        Close(_result);
    }

    private void OnShowDiff(object? sender, RoutedEventArgs e)
    {
        // REVISE-10 fix: "show diff" is non-destructive in v1 — behave as keep-recovered (load the
        // recovered content as a dirty buffer) rather than silently discarding the user's recovered work.
        // TODO(M6.4+): replace with a real side-by-side diff view when the Markdown/preview surface is built.
        _result = RestoreConflictChoice.ShowDiff;
        Close(_result);
    }

    /// <summary>
    /// Show the conflict prompt MODALLY over <paramref name="owner"/> (FR-19) and return the choice. A
    /// window closed with no button press (window-X / Esc) resolves to <see cref="RestoreConflictChoice.KeepDisk"/>
    /// — the conservative outcome (never silently restore the stale buffer + arm a clobbering Save).
    /// </summary>
    public static async System.Threading.Tasks.Task<RestoreConflictChoice> ShowAsync(Window owner, string fileName)
    {
        var dialog = new RestoreConflictDialog(fileName);
        return await dialog.ShowDialog<RestoreConflictChoice>(owner).ConfigureAwait(true);
    }
}
