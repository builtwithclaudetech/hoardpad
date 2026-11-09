// FindReplaceReplaceAllMnemonicTests.cs — QA4-10: Alt+A triggers Replace All while the panel has focus.
//
// The gesture is a KeyBinding scoped to FindReplaceView itself (not an access-key mnemonic on the button),
// specifically to avoid colliding with the top-level "_AI" menu's own Alt+A mnemonic in MainWindow.axaml —
// see the doc comment on FindReplaceView.axaml's <UserControl.KeyBindings>. Drives the real MainWindow +
// FindReplaceView headless, same pattern as the sibling QA4-8/9 test files.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FindReplaceReplaceAllMnemonicTests
{
    [AvaloniaFact]
    public void AltAWithFocusInThePanelTriggersReplaceAll()
    {
        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var ws = new FakeFindReplaceWorkspace();
        var buffer = new FakeBuffer("doc.txt", "cat cat cat");
        ws.Current = buffer;
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        shell.AttachFindReplace(frVm);
        window.UpdateLayout();

        shell.ShowFindCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(); // QA4-8's deferred FocusFind() — focus starts in FindBox.

        frVm.FindText = "cat"; // matches present (Done-when: "with matches present, Alt+A triggers Replace All").
        frVm.ReplaceText = "dog";
        window.UpdateLayout();

        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Alt);
        window.UpdateLayout();

        Assert.Equal("dog dog dog", buffer.Text);
        Assert.Equal("Replaced 3 in current file", frVm.StatusMessage);
    }

    [AvaloniaFact]
    public void AltAWithFocusOutsideThePanelDoesNotTriggerReplaceAll()
    {
        // The panel is closed (never summoned) — no control inside FindReplaceView holds focus, so its
        // KeyBinding is never on the bubble path and Alt+A must not fire Replace All.
        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var ws = new FakeFindReplaceWorkspace();
        var buffer = new FakeBuffer("doc.txt", "cat cat cat");
        ws.Current = buffer;
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll())
        {
            FindText = "cat",
            ReplaceText = "dog",
        };
        shell.AttachFindReplace(frVm);
        window.UpdateLayout();

        Assert.False(frVm.IsVisible);

        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Alt);
        window.UpdateLayout();

        Assert.Equal("cat cat cat", buffer.Text); // untouched — the panel was never open/focused.
    }
}
