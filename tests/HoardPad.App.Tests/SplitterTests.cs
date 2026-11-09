// SplitterTests.cs — QA2-8: the AI/editor split divider must be draggable to resize the two halves.
//
// MainWindow.axaml hosts a GridSplitter (originally Grid.Column="2", now Grid.Column="3" after QA4-13
// inserted a folder-panel splitter column ahead of it — see FolderPanelResizeTests.cs) between the editor
// column ("*") and the AI panel column ("300"). Unlike the tab-strip drag (QA2-7, which rides Avalonia's OS
// DragDrop loop and has no headless backend), GridSplitter resize is driven entirely by pointer events
// routed through the Avalonia input pipeline, which IS exercisable headless via Avalonia.Headless's
// MouseDown/MouseMove/MouseUp extensions. This test drives a REAL GridSplitter inside a REAL headless
// Window and asserts the AI panel column's rendered width actually changes — the layer that would have
// caught QA2-8 (a GridSplitter that compiles and binds but is inert, e.g. because IsVisible/Width leaves it
// with zero hit-test area).
//
// NOTE (post-QA4-13): BuildRow below is a SELF-CONTAINED synthetic replica of just the editor/AI-splitter/
// AI-panel slice of the real content row — Auto,*,Auto,300 relative shape (splitter | editor | splitter |
// AI panel), not the real window's actual column INDICES, which are now Auto,Auto,*,Auto,300 (folder |
// folder-splitter | editor | AI-splitter | AI-panel) after QA4-13 inserted the folder-panel splitter ahead
// of this pair. It intentionally omits the folder column entirely — this file only exercises the QA2-8
// AI/editor divider, unaffected by QA4-13's change.
// The verify filter matches the class name "Splitter".

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class SplitterTests
{
    // Self-contained synthetic slice: splitter | editor | splitter | AI panel (Auto,*,Auto,300) — the
    // QA2-8 AI/editor divider only. See the file-header NOTE above re: real-window column indices.
    private static (Window window, Grid grid, GridSplitter splitter) BuildRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,300"),
        };

        var editor = new Border { Background = Brushes.Transparent };
        Grid.SetColumn(editor, 1);

        var splitter = new GridSplitter
        {
            Width = 4,
            ResizeDirection = GridResizeDirection.Columns,
            Background = Brushes.Gray,
        };
        Grid.SetColumn(splitter, 2);

        var aiPanel = new Border { Background = Brushes.Transparent };
        Grid.SetColumn(aiPanel, 3);

        grid.Children.Add(editor);
        grid.Children.Add(splitter);
        grid.Children.Add(aiPanel);

        var window = new Window { Content = grid, Width = 900, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return (window, grid, splitter);
    }

    [AvaloniaFact]
    public void DraggingTheSplitterResizesTheAiPanelColumn()
    {
        var (window, grid, splitter) = BuildRow();

        double widthBefore = grid.ColumnDefinitions[3].Width.Value;
        Point splitterCenter = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                                ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);

        // Drag the splitter 60px to the LEFT: the AI panel column (to the splitter's right) must grow.
        window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(splitterCenter + new Point(-60, 0), RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(splitterCenter + new Point(-60, 0), MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        double widthAfter = grid.ColumnDefinitions[3].Width.Value;

        Assert.True(widthAfter > widthBefore,
            $"expected the AI panel column to grow past {widthBefore} after a left-drag, but it stayed at {widthAfter}");

        window.Close();
    }

    [AvaloniaFact]
    public void SplitterHasNonZeroHitTestBoundsWhenVisible()
    {
        // QA2-8's original symptom: the splitter existed but was not draggable — a common cause is zero
        // hit-test area (collapsed Width/Height or IsHitTestVisible=false). Guard that directly.
        var (window, _, splitter) = BuildRow();

        Assert.True(splitter.IsVisible);
        Assert.True(splitter.IsHitTestVisible);
        Assert.True(splitter.Bounds.Width > 0);
        Assert.True(splitter.Bounds.Height > 0);

        window.Close();
    }
}
