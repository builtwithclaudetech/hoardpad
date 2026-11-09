// AiInputResizeTests.cs — QA4-16: a draggable divider above the AI panel's prompt input lets the user
// grow it into a multi-line editor.
//
// Mirrors SplitterTests.cs's proven approach (a GridSplitter's drag is pure pointer-event routing, fully
// exercisable via Avalonia.Headless's MouseDown/MouseMove/MouseUp) but drives the REAL AiPanelView (not a
// synthetic mirror grid), so this also proves the splitter/row wiring in the actual view, not just the
// GridSplitter control's own behavior in isolation.
//
// Baselines are read from the REAL RENDERED PromptBox.Bounds.Height (post-layout), never from
// RowDefinition.Height.Value while the row is still GridLength.Auto (== 0 in Avalonia) — the review-caught
// tautology: a pre-drag baseline of 0 makes ANY floored-and-clamped post-drag value "pass" regardless of
// whether the drag direction is even wired correctly.
//
// Class name matches the verify filter "AiInputResize".

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiInputResizeTests
{
    private static (Window Window, GridSplitter Splitter, TextBox PromptBox, ScrollViewer Transcript, Control StatusBar) NewPanel()
    {
        var vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        GridSplitter splitter = view.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.Name == "PromptResizeSplitter");
        TextBox promptBox = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptBox");
        ScrollViewer transcript = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "TranscriptScroll");
        Control statusBar = view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "StatusBarText");
        return (window, splitter, promptBox, transcript, statusBar);
    }

    /// <summary>Drags <paramref name="splitter"/> by <paramref name="deltaY"/> DIPs (negative = up) and settles layout.</summary>
    private static void DragSplitter(Window window, GridSplitter splitter, double deltaY)
    {
        Point splitterCenter = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                                ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Y + 2);
        Point target = splitterCenter + new Point(0, deltaY);

        window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(target, RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(target, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    [AvaloniaFact]
    public void DraggingTheDividerUpGrowsThePromptInput()
    {
        (Window window, GridSplitter splitter, TextBox promptBox, ScrollViewer _, Control _) = NewPanel();
        try
        {
            double heightBefore = promptBox.Bounds.Height;

            // Drag the divider UP (into the Activity-log/Pending tabs region above it): the vacated space
            // goes to the prompt input BELOW it (mirrors SplitterTests' "drag toward the smaller region
            // grows the other side" direction).
            DragSplitter(window, splitter, -60);

            double heightAfter = promptBox.Bounds.Height;
            Assert.True(heightAfter > heightBefore,
                $"expected the prompt input to grow past its real rendered height {heightBefore} after dragging the divider up, but it is {heightAfter}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingTheDividerDownAfterGrowingItShrinksItBackDown()
    {
        // Companion to the "drag up grows" case — proves directionality both ways, not just "some floored
        // value is greater than a always-zero Auto baseline" (the tautology this file used to have).
        (Window window, GridSplitter splitter, TextBox promptBox, ScrollViewer _, Control _) = NewPanel();
        try
        {
            DragSplitter(window, splitter, -80); // grow it first, so there is real room to shrink back from.
            double heightAfterGrow = promptBox.Bounds.Height;

            DragSplitter(window, splitter, 50); // now drag DOWN.
            double heightAfterShrink = promptBox.Bounds.Height;

            Assert.True(heightAfterShrink < heightAfterGrow,
                $"expected dragging down to shrink the prompt input back below {heightAfterGrow}, but it is {heightAfterShrink}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AnExtremeUpwardDragClampsToASaneCeilingAndKeepsChromeVisible()
    {
        // C2: OnPromptSplitterDragDelta must clamp — an unbounded drag must not squeeze the flexible
        // Transcript row (and everything below it, incl. the StatusBar) out of existence.
        (Window window, GridSplitter splitter, TextBox promptBox, ScrollViewer transcript, Control statusBar) = NewPanel();
        try
        {
            DragSplitter(window, splitter, -2000);

            Assert.True(promptBox.Bounds.Height < window.Bounds.Height,
                $"expected the prompt input ({promptBox.Bounds.Height}) to be clamped well below the window height ({window.Bounds.Height})");
            Assert.True(transcript.Bounds.Height > 0,
                $"expected the Transcript row to keep a usable, non-zero height, but it is {transcript.Bounds.Height}");
            Assert.True(statusBar.IsVisible && statusBar.Bounds.Height > 0,
                "expected the StatusBar to remain visible and hit-testable after an extreme upward drag");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheDividerHasNonZeroHitTestBounds()
    {
        // The QA2-8 GridSplitter symptom class: a splitter that compiles/binds but is inert (zero hit-test
        // area from a collapsed Height or IsHitTestVisible=false) — guard it directly, as SplitterTests does.
        (Window window, GridSplitter splitter, TextBox _, ScrollViewer _, Control _) = NewPanel();
        try
        {
            Assert.True(splitter.IsVisible);
            Assert.True(splitter.IsHitTestVisible);
            Assert.True(splitter.Bounds.Width > 0);
            Assert.True(splitter.Bounds.Height > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ThePromptInputAlreadyAcceptsMultiLineText()
    {
        (Window window, GridSplitter _, TextBox promptBox, ScrollViewer _, Control _) = NewPanel();
        try
        {
            Assert.True(promptBox.AcceptsReturn);
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, promptBox.TextWrapping);
        }
        finally
        {
            window.Close();
        }
    }
}
