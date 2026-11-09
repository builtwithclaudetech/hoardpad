// DeleteParentDialog.axaml.cs — the C-3 modal delete-non-empty-parent prompt code-behind.
//
// Framework wiring only: build the prompt text from the item + child count, focus Cancel by default
// (the non-destructive choice — design §5.2), and close with the chosen DeleteParentChoice as the dialog
// result. Shown via ShowDialog(owner) so it blocks all other checklist mutation until answered (C-3).

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Views;

/// <summary>The modal subtree-vs-promote-vs-cancel prompt for deleting a non-empty parent (C-3).</summary>
internal sealed partial class DeleteParentDialog : Window
{
    /// <summary>Design-time / XAML-loader ctor.</summary>
    public DeleteParentDialog()
    {
        InitializeComponent();
    }

    private DeleteParentDialog(string itemText, int childCount) : this()
    {
        var prompt = this.FindControl<TextBlock>("PromptText")!;
        prompt.Text = $"Delete \"{itemText}\"? It has {childCount} item{(childCount == 1 ? "" : "s")} under it.";

        // Cancel takes initial focus so an accidental Enter never deletes a subtree (design §5.2).
        Opened += (_, _) => this.FindControl<Button>("CancelButton")!.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDeleteSubtree(object? sender, RoutedEventArgs e) => Close(DeleteParentChoice.DeleteSubtree);

    private void OnPromote(object? sender, RoutedEventArgs e) => Close(DeleteParentChoice.PromoteChildren);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(DeleteParentChoice.Cancel);

    /// <summary>
    /// Show the prompt MODALLY over <paramref name="owner"/> (C-3) and return the user's choice. A closed
    /// window with no button press (e.g. window-X) resolves to <see cref="DeleteParentChoice.Cancel"/>.
    /// </summary>
    public static async System.Threading.Tasks.Task<DeleteParentChoice> ShowAsync(
        Window owner, string itemText, int childCount)
    {
        var dialog = new DeleteParentDialog(itemText, childCount);
        return await dialog.ShowDialog<DeleteParentChoice>(owner).ConfigureAwait(true);
    }
}
