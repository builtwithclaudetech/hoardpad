// Qa318AutoScrollTests.cs — QA3-18: AI Panel History does not auto-scroll when a new message is added.
//
// Hosts a REAL AiPanelView (in a real headless Window, narrow enough that a handful of transcript lines
// overflow the visible height) and drives it through AiPanelViewModel.Transcript — the same collection the
// production AddLine path appends to on every streamed message / chip. Two interaction-layer facts:
//   (1) appending lines advances the ScrollViewer's offset to the bottom of the new content (the QA3-18 fix).
//   (2) if the user has manually scrolled away from the bottom, a subsequent append does NOT yank them back
//       down — the "respecting the user's manual scroll-up" half of the deliverable.
//
// The class name contains "Qa318" so the --filter "FullyQualifiedName~Qa318" resolves it.

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Qa318AutoScrollTests
{
    // A short panel height so a modest number of transcript lines overflow the viewport, forcing real
    // scrollable content — mirrors Qa317HistoryWrapRenderTests' fixed-size-host pattern.
    private const double PanelWidth = 300;
    private const double PanelHeight = 200;

    private static (Window window, AiPanelViewModel vm, ScrollViewer scroll) Host()
    {
        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = PanelWidth, Height = PanelHeight };
        window.Show();
        Pump(window);

        ScrollViewer scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "TranscriptScroll");
        return (window, vm, scroll);
    }

    /// <summary>Flush layout + the dispatcher-posted ScrollToEnd (Loaded priority) + a render tick.</summary>
    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AddLines(AiPanelViewModel vm, Window window, int count, string textPrefix)
    {
        for (int i = 0; i < count; i++)
        {
            vm.Transcript.Add(new TranscriptLine
            {
                Kind = TranscriptKind.AssistantMessage,
                Text = $"{textPrefix} line {i} — enough text on this row to take up real vertical space.",
            });
            Pump(window);
        }
    }

    [AvaloniaFact]
    public void AppendingTranscriptLinesScrollsToTheNewestMessage()
    {
        (Window window, AiPanelViewModel vm, ScrollViewer scroll) = Host();

        // Nothing to scroll yet.
        Assert.Equal(0, scroll.Offset.Y);

        AddLines(vm, window, count: 15, textPrefix: "assistant");

        double maxOffsetY = System.Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Assert.True(maxOffsetY > 0, "the transcript must have overflowed the panel height for this test to be meaningful.");
        Assert.True(scroll.Offset.Y > 0, "the scroll offset must have advanced past the top as messages were appended.");
        Assert.True(
            scroll.Offset.Y >= maxOffsetY - 1,
            $"expected the transcript scrolled to the newest message (offset {scroll.Offset.Y} vs max {maxOffsetY}).");

        window.Close();
    }

    [AvaloniaFact]
    public void ManuallyScrollingUpIsRespectedOnTheNextAppend()
    {
        (Window window, AiPanelViewModel vm, ScrollViewer scroll) = Host();

        // Get some real overflow + confirm we start stuck to the bottom (proven by the sibling fact above).
        AddLines(vm, window, count: 15, textPrefix: "assistant");
        double maxOffsetYBefore = System.Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Assert.True(scroll.Offset.Y >= maxOffsetYBefore - 1);

        // Simulate the user manually scrolling up to read earlier history.
        scroll.Offset = new Vector(0, 0);
        Pump(window);
        Assert.Equal(0, scroll.Offset.Y);

        // A new message arrives — it must NOT yank the view back down to the bottom.
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.AssistantMessage, Text = "a fresh message while scrolled up" });
        Pump(window);

        Assert.Equal(0, scroll.Offset.Y);

        window.Close();
    }
}
