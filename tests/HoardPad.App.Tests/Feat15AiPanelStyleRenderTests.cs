// Feat15AiPanelStyleRenderTests.cs — FEAT-15: Settings — new "AI" section (input font name/size, response
// font/size/color, user-message font/size/color).
//
// AiPanelStyleApplier is the single writer of the FEAT-15 style resource keys (mirrors SettingsFontApplier,
// QA3-1's fix): AiPanelStyleApplierTests (SettingsTests.cs) proves the resource-dictionary writes directly.
// This file proves the RENDER-level consequence — the real rendered AiPanelView actually reflects the
// setting, not just that Application.Resources holds the right value (the exact QA3-1 chrome-reflow gap
// this project has been burned by before, see SettingsFontChromeReflowTests). Class name contains "Feat15"
// so `--filter "FullyQualifiedName~Feat15"` resolves it.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Feat15AiPanelStyleRenderTests
{
    [AvaloniaFact]
    public void SettingAiInputFontAppliesLiveToTheAlreadyRenderedPromptBox()
    {
        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        TextBox promptBox = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "PromptBox");

        // Sanity: not already the target font before the change.
        Assert.NotEqual("Roboto", promptBox.FontFamily.Name);

        // The PromptBox is a SINGLE persistent element bound via DynamicResource (not per-item), so a
        // resource write must reflect on the ALREADY-RENDERED control with no re-render call needed —
        // proving the live-apply path without a restart, exactly like the FEAT-1 chrome-font reflow.
        AiPanelStyleApplier.Apply(
            Application.Current!,
            inputFontFamily: "Roboto", inputFontSize: 18,
            responseFontFamily: null, responseFontSize: 12, responseColor: null,
            userFontFamily: null, userFontSize: 12, userColor: null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Roboto", promptBox.FontFamily.Name);
        Assert.Equal(18d, promptBox.FontSize);

        window.Close();
    }

    [AvaloniaFact]
    public void ResponseAndUserMessagesRenderWithTheirConfiguredFontsAndColorsAfterASettingsChange()
    {
        // Configure BEFORE the messages are appended — new transcript items pick up the current style
        // (the same "new content reflects the new setting" posture the FEAT-1 editor/chrome font uses).
        AiPanelStyleApplier.Apply(
            Application.Current!,
            inputFontFamily: null, inputFontSize: 12,
            responseFontFamily: "Fira Code", responseFontSize: 17, responseColor: "#E5B93D",
            userFontFamily: "Roboto", userFontSize: 15, userColor: "#5CC0CF");

        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.UserMessage, Text = "hello there" });
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.AssistantMessage, Text = "hi, how can I help" });

        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        TextBlock userBlock = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "hello there");
        TextBlock assistantBlock = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "hi, how can I help");

        Assert.Equal("Roboto", userBlock.FontFamily.Name);
        Assert.Equal(15d, userBlock.FontSize);
        Assert.Equal(Color.Parse("#5CC0CF"), Assert.IsAssignableFrom<ISolidColorBrush>(userBlock.Foreground).Color);

        Assert.Equal("Fira Code", assistantBlock.FontFamily.Name);
        Assert.Equal(17d, assistantBlock.FontSize);
        Assert.Equal(Color.Parse("#E5B93D"), Assert.IsAssignableFrom<ISolidColorBrush>(assistantBlock.Foreground).Color);

        window.Close();
    }

    [AvaloniaFact]
    public void NonMessageTranscriptKindsAreUnaffectedByTheAiStyleSettings()
    {
        AiPanelStyleApplier.Apply(
            Application.Current!,
            inputFontFamily: null, inputFontSize: 12,
            responseFontFamily: "Fira Code", responseFontSize: 20, responseColor: "#E5B93D",
            userFontFamily: "Roboto", userFontSize: 20, userColor: "#5CC0CF");

        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.SystemNote, Text = "— stopped —" });
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.SafetyEventChip, Text = "blocked", IsDanger = true });

        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        TextBlock noteBlock = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "— stopped —");
        TextBlock dangerBlock = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "blocked");

        // Neither a system note nor a danger chip took the 20pt AI-message font override.
        Assert.NotEqual(20d, noteBlock.FontSize);
        Assert.NotEqual(20d, dangerBlock.FontSize);

        // The danger chip still renders in the danger token, NOT the response/user color override —
        // safety events must stay visually distinct regardless of the AI style settings.
        Color dangerColor = Assert.IsAssignableFrom<ISolidColorBrush>(dangerBlock.Foreground).Color;
        Assert.NotEqual(Color.Parse("#E5B93D"), dangerColor);
        Assert.NotEqual(Color.Parse("#5CC0CF"), dangerColor);

        window.Close();
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task SavingAiStyleInSettingsPersistsAndAppliesLiveToTheRealAiPanel()
    {
        // End-to-end: a real SettingsView edit -> Save -> a real AiPanelView reflects it, AND the value
        // round-trips through a fresh SettingsStore load (the relaunch path) — the phase's Done-when.
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel settingsVm = await SettingsViewModel.LoadAsync(store, System.Threading.CancellationToken.None);

        var settingsView = new SettingsView { DataContext = settingsVm };
        var settingsWindow = new Window { Content = settingsView, Width = 640, Height = 1400 };
        settingsWindow.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        settingsVm.AiResponseFontFamily = "Fira Code";
        settingsVm.AiResponseFontSize = 19;
        settingsVm.AiResponseColor = "#E5B93D";
        await settingsVm.SaveCommand.ExecuteAsync(null);

        // Persistence: a fresh load (simulating relaunch) sees the saved values.
        HoardPadSettings reloaded = await store.LoadAsync(System.Threading.CancellationToken.None);
        Assert.Equal("Fira Code", reloaded.AiResponseFontFamily);
        Assert.Equal(19, reloaded.AiResponseFontSize);
        Assert.Equal("#E5B93D", reloaded.AiResponseColor);

        // Live-apply: a NEW AI-panel message created after the Save renders with the saved style.
        AiPanelViewModel panelVm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        panelVm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.AssistantMessage, Text = "styled reply" });

        var panelView = new AiPanelView { DataContext = panelVm };
        var panelWindow = new Window { Content = panelView, Width = 300, Height = 600 };
        panelWindow.Show();
        panelWindow.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        TextBlock replyBlock = panelView.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "styled reply");
        Assert.Equal("Fira Code", replyBlock.FontFamily.Name);
        Assert.Equal(19d, replyBlock.FontSize);
        Assert.Equal(Color.Parse("#E5B93D"), Assert.IsAssignableFrom<ISolidColorBrush>(replyBlock.Foreground).Color);

        settingsWindow.Close();
        panelWindow.Close();
    }
}
