// Qa317HistoryWrapRenderTests.cs — QA3-17: AI Panel History — some characters run off-screen despite an
// attempted word-wrap on panel resize.
//
// Both transcript TextBlocks in AiPanelView.axaml already carried TextWrapping="Wrap" (§6.4). The real bug
// (same family as QA2-16/QA2-11 — a wrapping/measure bug, not a missing property) is that the transcript's
// ScrollViewer left HorizontalScrollBarVisibility at its Avalonia default "Auto", which measures its content
// with UNBOUNDED width so a horizontal scrollbar can exist at all — so every child TextBlock, wrap flag or
// not, was handed infinite available width and never actually wrapped; long lines ran off the visible panel
// with no scrollbar to reveal them (none of the child controls declare a MinWidth). The fix sets
// HorizontalScrollBarVisibility="Disabled" on that ScrollViewer so measurement is bounded to the panel's real
// width, letting Wrap take effect.
//
// This test hosts a REAL AiPanelView in a REAL headless Window at a narrow, QA2-8-resized panel width (mirrors
// AiPanelHeaderLayoutTests' fixed-width-column pattern for QA2-16), injects a long assistant transcript
// message, and asserts (a) no rendered transcript TextBlock's right edge exceeds the window/panel width and
// (b) the long message's TextBlock actually grew taller than a single text line — proof it wrapped onto
// multiple lines rather than being silently clipped or scrolled off-screen. It also captures a PNG for visual
// review. The class name contains "Qa317" so the --filter "FullyQualifiedName~Qa317" resolves it.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Qa317HistoryWrapRenderTests
{
    // A narrow, QA2-8-resized panel width — well under the default 300px column — so a long message MUST wrap
    // across several lines to stay on-screen at all.
    private const double NarrowPanelWidth = 180;

    private const string LongMessage =
        "This is a deliberately long assistant reply that must wrap across several lines instead of running " +
        "off the right edge of a narrow AI panel column, exercising the exact QA3-17 regression scenario end " +
        "to end with no single word anywhere near panel width.";

    private static string ResolveScreenshotDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "qa3-17-smoke", "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }

    [AvaloniaFact]
    public void LongTranscriptMessageWrapsInsideANarrowResizedPanelWithNoOffScreenOverflow()
    {
        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.AssistantMessage, Text = LongMessage });

        var view = new AiPanelView { DataContext = vm };
        // Mirrors AiPanelHeaderLayoutTests' fixed-width-column host, but narrowed to simulate the QA2-8
        // splitter having been dragged down to a small panel width (the failure mode QA3-17 describes).
        var window = new Window { Content = view, Width = NarrowPanelWidth, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        TextBlock? messageBlock = view
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == LongMessage);

        Assert.NotNull(messageBlock);
        Assert.True(messageBlock!.IsEffectivelyVisible);
        Assert.True(messageBlock.Bounds.Width > 0 && messageBlock.Bounds.Height > 0);

        // (a) no off-screen overflow: the message's rendered right edge lands inside the window.
        Point? topLeft = messageBlock.TranslatePoint(new Point(0, 0), window);
        Point? bottomRight = messageBlock.TranslatePoint(
            new Point(messageBlock.Bounds.Width, messageBlock.Bounds.Height), window);
        Assert.True(topLeft is not null && bottomRight is not null);
        Assert.True(
            topLeft!.Value.X >= 0 && bottomRight!.Value.X <= window.Bounds.Width,
            $"Long transcript message (X {topLeft.Value.X}..{bottomRight.Value.X}) must be fully within the " +
            $"panel width {window.Bounds.Width} — it must wrap, not run off-screen.");

        // (b) it actually WRAPPED (multiple lines), not merely fit by luck: a single line of this font at this
        // narrow a width would be far shorter than the message's full rendered height.
        double singleLineHeight = messageBlock.FontSize * 1.2;
        Assert.True(
            messageBlock.Bounds.Height > singleLineHeight * 2,
            $"Expected the long message to wrap across multiple lines (height {messageBlock.Bounds.Height} " +
            $"vs one line ~{singleLineHeight}) — a single unwrapped line would indicate the wrap regression.");

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(ResolveScreenshotDir(), "ai-panel-history-wrap-narrow.png");
        frame?.Save(path);
        Assert.True(File.Exists(path), $"expected the narrow-panel wrap screenshot at {path}");

        window.Close();
    }
}
