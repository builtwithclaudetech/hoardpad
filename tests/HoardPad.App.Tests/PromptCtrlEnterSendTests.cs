// PromptCtrlEnterSendTests.cs — FEAT-4: AI prompt box Ctrl+Enter sends; Enter/Shift+Enter stay soft-enter.
//
// Interaction-layer coverage on the REAL AiPanelView + a real headless Window (the layer that would catch
// a missing/omitted KeyDown wire-up — mirrors RedoRebindTests.cs / ChecklistControlTests.cs's real-keypress
// pattern). Drives window.KeyPressQwerty against the real PromptBox TextBox — never calls the code-behind
// handler or the VM command directly — so this proves the Tunnel handler AiPanelView.axaml.cs's constructor
// wires onto PromptBox actually intercepts the key before TextBox's own AcceptsReturn newline-insert runs.
//
// Class name matches the verify filter "AiPanel".

using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class PromptCtrlEnterSendTests
{
    private static (AiPanelViewModel Vm, TextBox PromptBox, Window Window) NewPanel(ScriptedTurnRunner runner)
    {
        var vm = AiPanelTestFactory.New(runner);
        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        TextBox promptBox = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptBox");
        promptBox.Focus();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return (vm, promptBox, window);
    }

    [AvaloniaFact]
    public async Task CtrlEnterSendsAndInsertsNoStrayNewline()
    {
        var runner = new ScriptedTurnRunner([TurnEvent.Assistant("OK")]);
        (AiPanelViewModel vm, TextBox promptBox, Window window) = NewPanel(runner);
        try
        {
            vm.PromptInput = "refactor main()";
            promptBox.CaretIndex = promptBox.Text?.Length ?? 0;

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            // The scripted runner's single event ends the turn almost immediately — spin until it does.
            for (int i = 0; i < 200 && vm.Transcript.Count == 0; i++)
                await Task.Delay(5);

            // The exact prompt text reached the transcript — no stray newline from the Enter half of the
            // chord (proves the Tunnel handler consumed the key before TextBox's own insert ran).
            Assert.Contains(vm.Transcript, t => t.Kind == TranscriptKind.UserMessage && t.Text == "refactor main()");
            Assert.False(vm.IsTurnInFlight); // the turn completed.
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PlainEnterInsertsASoftNewlineAndDoesNotSend()
    {
        var runner = new ScriptedTurnRunner([TurnEvent.Assistant("should never run")]);
        (AiPanelViewModel vm, TextBox promptBox, Window window) = NewPanel(runner);
        try
        {
            vm.PromptInput = "hello";
            promptBox.CaretIndex = promptBox.Text?.Length ?? 0;

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20); // give an (incorrect) fire-and-forget send a chance to prove itself.

            Assert.StartsWith("hello", vm.PromptInput);
            Assert.True(vm.PromptInput.Length > "hello".Length, "Enter must insert a soft newline, not send.");
            Assert.Empty(vm.Transcript); // no turn was sent.
            Assert.False(vm.IsTurnInFlight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ShiftEnterInsertsASoftNewlineAndDoesNotSend()
    {
        var runner = new ScriptedTurnRunner([TurnEvent.Assistant("should never run")]);
        (AiPanelViewModel vm, TextBox promptBox, Window window) = NewPanel(runner);
        try
        {
            vm.PromptInput = "hello";
            promptBox.CaretIndex = promptBox.Text?.Length ?? 0;

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);

            Assert.StartsWith("hello", vm.PromptInput);
            Assert.True(vm.PromptInput.Length > "hello".Length, "Shift+Enter must insert a soft newline, not send.");
            Assert.Empty(vm.Transcript);
            Assert.False(vm.IsTurnInFlight);
        }
        finally
        {
            window.Close();
        }
    }
}
