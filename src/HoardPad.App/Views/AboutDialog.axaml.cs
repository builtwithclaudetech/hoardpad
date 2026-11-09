// AboutDialog.axaml.cs — the Help ▸ About HoardPad dialog code-behind (QA2-14).
//
// Framework wiring only, matching ConfirmCloseDialog: a static ShowAsync(owner) shows the dialog
// modally over the main window and returns once the user closes it.

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace HoardPad.App.Views;

/// <summary>The Help ▸ About HoardPad dialog (QA2-14): app info + keyboard-shortcut reference.</summary>
internal sealed partial class AboutDialog : Window
{
    /// <summary>Design-time / XAML-loader ctor.</summary>
    public AboutDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Show the dialog MODALLY over <paramref name="owner"/>.</summary>
    public static async Task ShowAsync(Window owner)
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }
}
