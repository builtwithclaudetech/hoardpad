// FindReplaceFocusTests.cs — QA4-8: focus lands in the Find box the moment the panel opens.
//
// Drives the real MainWindow + FindReplaceView headless (same pattern as
// FindReplacePanelViewVisibilityTests) and asserts the Find TextBox is the FOCUSED element after
// ShowFindCommand fires — not just that the panel Control became visible. A user who opens Find/Replace
// and types without clicking expects those keystrokes to land in the Find box.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FindReplaceFocusTests
{
    [AvaloniaFact]
    public void OpeningFindReplaceFocusesTheFindBox()
    {
        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        shell.AttachFindReplace(frVm);
        window.UpdateLayout();

        shell.ShowFindCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(); // FocusFind() defers via Dispatcher.Post (see its doc comment).

        var panel = window.FindControl<FindReplaceView>("FindReplacePanel")!;
        Assert.True(panel.IsVisible);

        var findBox = panel.FindControl<TextBox>("FindBox")!;
        Assert.True(findBox.IsFocused); // QA4-8: caret must be in the Find box on open, no click needed.
    }
}
