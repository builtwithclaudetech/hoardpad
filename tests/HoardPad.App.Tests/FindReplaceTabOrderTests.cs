// FindReplaceTabOrderTests.cs — QA4-9: Tab from Find goes to Replace, then the remaining controls in a
// sensible order (see the TabIndex doc comment on FindReplaceView.axaml Row 1 for the full 1-14 sequence).
//
// Drives the real MainWindow + FindReplaceView headless (same pattern as FindReplacePanelViewVisibilityTests
// / FindReplaceFocusTests) and walks Tab presses, asserting the FOCUSED element identity at each step.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FindReplaceTabOrderTests
{
    [AvaloniaFact]
    public void TabOrderStartsFindThenReplaceThenTheRestInSequence()
    {
        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        // A folder must be open so the In-folder scope radio + Review button are enabled — otherwise Tab
        // skips them (disabled controls are never focusable) and the sequence below can't observe them.
        var ws = new FakeFindReplaceWorkspace { Root = "/tmp/fake-folder" };
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        shell.AttachFindReplace(frVm);
        window.UpdateLayout();

        shell.ShowFindCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(); // QA4-8's deferred FocusFind() puts focus in FindBox.

        var panel = window.FindControl<FindReplaceView>("FindReplacePanel")!;
        var findBox = panel.FindControl<TextBox>("FindBox")!;
        var replaceBox = panel.FindControl<TextBox>("ReplaceBox")!;

        Assert.True(findBox.IsFocused); // starting point: QA4-8's fix already put focus here.

        // QA4-9's required sequence: Find -> Replace -> Replace/ReplaceAll -> the toggles -> nav -> close ->
        // scope radios -> Review. Matches the TabIndex="1".."14" values set on FindReplaceView.axaml.
        Control[] expectedOrder =
        [
            replaceBox,
            panel.FindControl<Button>("ReplaceButton")!,
            panel.FindControl<Button>("ReplaceAllButton")!,
            panel.FindControl<ToggleButton>("RegexToggle")!,
            panel.FindControl<ToggleButton>("CaseToggle")!,
            panel.FindControl<ToggleButton>("WholeWordToggle")!,
            panel.FindControl<Button>("PrevButton")!,
            panel.FindControl<Button>("NextButton")!,
            panel.FindControl<Button>("CloseButton")!,
            panel.FindControl<RadioButton>("ScopeCurrentFileRadio")!,
            panel.FindControl<RadioButton>("ScopeAllOpenRadio")!,
            panel.FindControl<RadioButton>("ScopeInFolderRadio")!,
            panel.FindControl<Button>("ReviewButton")!,
        ];

        foreach (Control expected in expectedOrder)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.UpdateLayout();
            Assert.True(expected.IsFocused, $"expected '{expected.Name}' to have focus next");
        }
    }
}
