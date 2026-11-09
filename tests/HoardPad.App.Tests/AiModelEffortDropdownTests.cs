// AiModelEffortDropdownTests.cs — QA4-2 (reopens QA3-11/BUG-5): a real, live-applied Model + Effort
// ComboBox pair in the AI panel header.
//
// Earlier closures (BUG-5/QA3-11) proved the wiring via argv/interaction-layer proof only — the panel
// showed model/effort as a READ-ONLY info line (ModelEffortText), with nothing to drive, and this
// project's live-GUI gate cannot paint a native MenuItem/Flyout popup under its no-WM Xvfb. This file
// drives the REAL AiPanelView's ModelSelector/EffortSelector ComboBoxes — a control choice that, unlike
// a MenuFlyout, Avalonia's headless test platform (a fully virtual backend, unrelated to the no-WM Xvfb
// popup-paint gap) selects and binds reliably — and proves the change reaches the composed TurnRequest.
//
// QA4-21 retired the persist/W-1 panel-vs-settings-lost-update tests that used to live here (the panel no
// longer writes settings.json at all — see AiModelEffortEphemeralTests.cs for the new ephemeral-override
// coverage). The rendering/turn-composition tests below are unaffected by that change and stay.
//
// Class name matches the verify filter "AiModelEffortDropdown".

using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiModelEffortDropdownTests
{
    // Reuses the RecordingTurnRunner already defined in Feat14AiSettingsChangeTests.cs (records every
    // TurnRequest it receives in order) rather than duplicating a near-identical fake.
    private static (AiPanelViewModel Vm, RecordingTurnRunner Runner, ComboBox ModelSelector, ComboBox EffortSelector, Window Window) NewPanel()
    {
        var runner = new RecordingTurnRunner([HoardPad.App.Ai.TurnEvent.Assistant("ok")]);
        var vm = AiPanelTestFactory.New(runner);
        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        ComboBox modelSelector = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ModelSelector");
        ComboBox effortSelector = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "EffortSelector");

        return (vm, runner, modelSelector, effortSelector, window);
    }

    [AvaloniaFact]
    public async Task ChangingTheRealDropdownsFeedsTheNextTurnRequest()
    {
        (AiPanelViewModel vm, RecordingTurnRunner runner, ComboBox modelSelector, ComboBox effortSelector, Window window) = NewPanel();
        try
        {
            // Drive the REAL bound ComboBoxes (a SelectedItem write through the compiled binding), not a
            // direct VM property set — this proves the XAML wiring, not just AiPanelViewModel in isolation.
            modelSelector.SelectedItem = "claude-haiku-4-5-20251001";
            effortSelector.SelectedItem = "xhigh";
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            Assert.Equal("claude-haiku-4-5-20251001", vm.CurrentModelId);
            Assert.Equal("xhigh", vm.CurrentEffortLevel);

            await AiPanelTestFactory.RunTurnAsync(vm, "hello");

            Assert.Single(runner.Requests);
            Assert.Equal("claude-haiku-4-5-20251001", runner.Requests[0].ModelId);
            Assert.Equal("xhigh", runner.Requests[0].EffortLevel);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EffortSelectorOffersExactlyTheFourStandardLevels()
    {
        (_, _, _, ComboBox effortSelector, Window window) = NewPanel();
        try
        {
            Assert.Equal(["low", "medium", "high", "xhigh"], effortSelector.ItemsSource!.Cast<string>().ToList());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ModelSelectorOffersTheKnownClaudePresets()
    {
        (_, _, ComboBox modelSelector, _, Window window) = NewPanel();
        try
        {
            Assert.Equal(
                ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"],
                modelSelector.ItemsSource!.Cast<string>().ToList());
        }
        finally
        {
            window.Close();
        }
    }
}
