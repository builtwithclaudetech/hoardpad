// ConfirmCloseDialog.axaml.cs — the BUG-3 unsaved-changes confirmation dialog code-behind.
//
// Framework wiring only: build the prompt from the filename, focus Cancel by default
// (the non-destructive choice), and close with true (close anyway) / false (cancel).
// Shown via ShowDialog(owner) so it blocks the close until answered.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace HoardPad.App.Views;

/// <summary>
/// The modal unsaved-changes prompt for closing a dirty tab (BUG-3). Returns <c>true</c> to
/// proceed with the close, <c>false</c> to cancel.
/// </summary>
internal sealed partial class ConfirmCloseDialog : Window
{
    /// <summary>Design-time / XAML-loader ctor.</summary>
    public ConfirmCloseDialog()
    {
        InitializeComponent();
    }

    private ConfirmCloseDialog(string documentName) : this()
    {
        var prompt = this.FindControl<TextBlock>("PromptText")!;
        prompt.Text = $"\"{documentName}\" has unsaved changes. Close anyway?";

        // Cancel takes initial focus so an accidental Enter never discards changes.
        Opened += (_, _) => this.FindControl<Button>("CancelButton")!.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>
    /// Show the prompt MODALLY over <paramref name="owner"/> and return the user's choice. A closed
    /// window with no button press (e.g. window-X) resolves to <c>false</c> (cancel).
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> ShowAsync(
        Window owner, string documentName)
    {
        var dialog = new ConfirmCloseDialog(documentName);
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }
}
