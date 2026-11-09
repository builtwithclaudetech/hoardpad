// FolderPanelResizeTests.cs — QA4-13: the folder panel must be resizable via a draggable splitter, the
// same pattern SplitterTests.cs already proves for the AI/editor divider (QA2-8).
//
// MainWindow.axaml hosts a GridSplitter (Grid.Column="1") between the folder-tree column (Grid.Column="0",
// "Auto") and the editor column (Grid.Column="2", "*"). The folder column is deliberately "Auto", NOT a
// fixed pixel width (hostile-review CRITICAL, fix-first): a fixed-pixel column reserves its declared width
// REGARDLESS of the hosted Border's IsVisible (empirically confirmed — this is the SAME pre-existing quirk
// the AI panel's own column 4 ("300") already has, out of scope here), so with no folder open
// (IsFolderTreeVisible defaults false — the app's most common startup state) a fixed-pixel column 0 would
// silently reserve ~200px of dead space and shift the editor's left edge away from the window edge. "Auto"
// instead measures its one child's desired size, so it collapses to 0 exactly like the AI-splitter's own
// Auto column (index 3) already does when hidden. The folder Border carries MinWidth="200" (F-TREE1: was a
// fixed Width="200", which pinned the Stretch TreeView to 200px even after a splitter widen) so the
// Auto column has a concrete ≥200px starting size to collapse from — and empirically, Avalonia's GridSplitter converts an
// Auto column to a concrete Pixel GridLength on drag (using the column's current ActualWidth as the
// starting point, the same mechanism WPF's GridSplitter uses), so the splitter still works once the panel
// is visible.
//
// This file mirrors SplitterTests.cs exactly for the drag mechanics: a real GridSplitter inside a real
// headless Window with the real ColumnDefinitions shape from MainWindow.axaml, asserting the folder
// column's rendered width actually changes on drag — the layer that would catch a splitter that compiles
// and binds but is inert (zero hit-test area, or an Auto neighbor with nothing concrete to mutate). It also
// adds the CRITICAL-fix regression coverage: the folder column must consume ZERO width when the panel is
// hidden, both on a synthetic replica of the row AND on the REAL, XAML-loaded MainWindow bound to a fresh
// MainWindowViewModel (IsFolderTreeVisible defaults false — no folder ever opened).
// The verify filter matches the class name "FolderPanelResize".

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FolderPanelResizeTests
{
    // Mirrors MainWindow.axaml's content row: Auto,Auto,*,Auto,300
    // (folder tree | folder splitter | editor | AI splitter | AI panel). folderVisible controls both the
    // folder Border's and its splitter's IsVisible, exactly as MainWindow.axaml binds both to
    // IsFolderTreeVisible.
    private static (Window window, Grid grid, GridSplitter splitter) BuildRow(bool folderVisible = true)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,300"),
        };

        var folderTree = new Border { Background = Brushes.Transparent, Width = 200, IsVisible = folderVisible };
        Grid.SetColumn(folderTree, 0);

        var splitter = new GridSplitter
        {
            Width = 4,
            ResizeDirection = GridResizeDirection.Columns,
            Background = Brushes.Gray,
            IsVisible = folderVisible,
        };
        Grid.SetColumn(splitter, 1);

        var editor = new Border { Background = Brushes.Transparent };
        Grid.SetColumn(editor, 2);

        grid.Children.Add(folderTree);
        grid.Children.Add(splitter);
        grid.Children.Add(editor);

        var window = new Window { Content = grid, Width = 900, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return (window, grid, splitter);
    }

    [AvaloniaFact]
    public void DraggingTheSplitterResizesTheFolderPanelColumn()
    {
        var (window, grid, splitter) = BuildRow();

        double widthBefore = grid.ColumnDefinitions[0].ActualWidth;
        Assert.Equal(200, widthBefore); // the Auto column measures the Border's explicit Width when visible.

        Point splitterCenter = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                                ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);

        // Drag the splitter 40px to the RIGHT: the folder panel column (to the splitter's left) must grow.
        window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(splitterCenter + new Point(40, 0), RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(splitterCenter + new Point(40, 0), MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        double widthAfter = grid.ColumnDefinitions[0].ActualWidth;

        Assert.True(widthAfter > widthBefore,
            $"expected the folder panel column to grow past {widthBefore} after a right-drag, but it stayed at {widthAfter}");

        window.Close();
    }

    [AvaloniaFact]
    public void SplitterHasNonZeroHitTestBoundsWhenVisible()
    {
        // QA4-13's failure mode is a splitter that exists but is not draggable — a common cause is zero
        // hit-test area (collapsed Width/Height, IsHitTestVisible=false, or an Auto-sized neighbor column
        // GridSplitter can't concretely mutate). Guard that directly.
        var (window, _, splitter) = BuildRow();

        Assert.True(splitter.IsVisible);
        Assert.True(splitter.IsHitTestVisible);
        Assert.True(splitter.Bounds.Width > 0);
        Assert.True(splitter.Bounds.Height > 0);

        window.Close();
    }

    [AvaloniaFact]
    public void HiddenFolderPanelReservesNoColumnSpace()
    {
        // The CRITICAL regression this guards: a fixed-pixel column 0 would keep reserving its declared
        // width even with the Border/splitter both IsVisible=false. An "Auto" column must not.
        var (window, grid, splitter) = BuildRow(folderVisible: false);

        Assert.False(splitter.IsVisible);
        Assert.Equal(0, grid.ColumnDefinitions[0].ActualWidth);

        window.Close();
    }

    // ---- Real-window regression: the actual, XAML-loaded MainWindow bound to a fresh
    //      MainWindowViewModel — IsFolderTreeVisible defaults false (no folder ever opened, the app's most
    //      common startup state). Proves the CRITICAL fix on the real binding chain, not a synthetic replica. ----

    [AvaloniaFact]
    public void RealMainWindowReservesNoFolderColumnSpaceWhenNoFolderIsOpen()
    {
        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.False(shell.IsFolderTreeVisible);

        var splitter = window.FindControl<GridSplitter>("FolderPanelSplitter");
        Assert.NotNull(splitter);
        var grid = splitter!.Parent as Grid;
        Assert.NotNull(grid);

        Assert.Equal(0, grid!.ColumnDefinitions[0].ActualWidth);

        window.Close();
    }

    [AvaloniaFact]
    public void RealMainWindowFolderColumnIsTwoHundredWhenFolderTreeIsVisible()
    {
        var shell = new MainWindowViewModel { IsFolderTreeVisible = true };
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var splitter = window.FindControl<GridSplitter>("FolderPanelSplitter");
        Assert.NotNull(splitter);
        Assert.True(splitter!.IsVisible);
        var grid = splitter.Parent as Grid;
        Assert.NotNull(grid);

        Assert.Equal(200, grid!.ColumnDefinitions[0].ActualWidth);

        window.Close();
    }

    // ---- Drag-then-hide regression (hostile-review CRITICAL #2): GridSplitter converts the folder
    //      column's "Auto" GridLength to a concrete Pixel value on drag and never reverts it on its own.
    //      Without MainWindow.axaml.cs's OnShellPropertyChanged -> ResetFolderColumnWidth fix, the ordinary
    //      flow "open folder -> drag splitter wider -> hide the tree" would leave the dragged width
    //      permanently reserved as dead space even with the panel hidden. This test drives a REAL drag on
    //      the REAL, XAML-loaded MainWindow's FolderPanelSplitter, then toggles IsFolderTreeVisible off, and
    //      asserts the column collapses back to 0 — MUST fail without the fix, pass with it. ----

    [AvaloniaFact]
    public void DragThenHideResetsTheFolderColumnToZeroWidth()
    {
        var shell = new MainWindowViewModel { IsFolderTreeVisible = true };
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var splitter = window.FindControl<GridSplitter>("FolderPanelSplitter");
        Assert.NotNull(splitter);
        var grid = splitter!.Parent as Grid;
        Assert.NotNull(grid);

        double widthBeforeDrag = grid!.ColumnDefinitions[0].ActualWidth;
        Assert.Equal(200, widthBeforeDrag);

        Point splitterCenter = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                                ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);

        // Drag 60px to the right — mirrors the reviewer's reproduction (Auto -> Pixel=260).
        window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(splitterCenter + new Point(60, 0), RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(splitterCenter + new Point(60, 0), MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        double widthAfterDrag = grid.ColumnDefinitions[0].ActualWidth;
        Assert.True(widthAfterDrag > widthBeforeDrag,
            $"pre-condition: the drag itself must have grown the column past {widthBeforeDrag}, but it stayed at {widthAfterDrag}");
        Assert.Equal(GridUnitType.Pixel, grid.ColumnDefinitions[0].Width.GridUnitType); // confirms GridSplitter's Auto->Pixel conversion fired.

        // Now hide the folder tree — the same VM flip View ▸ Folder Tree / the toolbar [Folder] button drive.
        shell.IsFolderTreeVisible = false;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(0, grid.ColumnDefinitions[0].ActualWidth);

        window.Close();
    }

    [AvaloniaFact]
    public void DragThenHideThenShowAgainRestoresTwoHundredAndStaysDraggable()
    {
        // Re-verify the show-again path: after a drag-then-hide reset, toggling the tree back on must
        // still produce a usable (200px) panel with a working, hit-testable splitter — not a stuck 0.
        var shell = new MainWindowViewModel { IsFolderTreeVisible = true };
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var splitter = window.FindControl<GridSplitter>("FolderPanelSplitter");
        Assert.NotNull(splitter);
        var grid = splitter!.Parent as Grid;
        Assert.NotNull(grid);

        Point splitterCenter = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                                ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);
        window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(splitterCenter + new Point(60, 0), RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(splitterCenter + new Point(60, 0), MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        shell.IsFolderTreeVisible = false;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.Equal(0, grid!.ColumnDefinitions[0].ActualWidth);

        // Show again.
        shell.IsFolderTreeVisible = true;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(splitter!.IsVisible);
        Assert.True(splitter.IsHitTestVisible);
        Assert.Equal(200, grid.ColumnDefinitions[0].ActualWidth);

        // And it must still be draggable from this restored state.
        Point centerAgain = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                             ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);
        window.MouseDown(centerAgain, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(centerAgain + new Point(30, 0), RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(centerAgain + new Point(30, 0), MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(grid.ColumnDefinitions[0].ActualWidth > 200,
            $"expected the re-shown splitter to still be draggable past 200, but got {grid.ColumnDefinitions[0].ActualWidth}");

        window.Close();
    }

    // ---- F-TREE1: the panel must not just report a resized COLUMN — the TreeView filling it must actually
    //      grow to match. Every test above only asserts grid.ColumnDefinitions[0].ActualWidth, which
    //      GridSplitter updates regardless of what the hosted Border does with the extra space — a fixed
    //      Width="200" Border (the pre-fix state) would pass every test above while staying visually pinned
    //      at 200px, because none of them look at the CHILD's own rendered Bounds.Width. This closes that gap
    //      on the REAL, XAML-loaded MainWindow: the fix is MinWidth="200" (a floor, not a pin) on the folder
    //      Border, leaving its default HorizontalAlignment="Stretch" free to fill a wider column. ----

    [AvaloniaFact]
    public void DraggingTheSplitterAlsoGrowsTheRealTreeViewNotJustTheColumn()
    {
        var shell = new MainWindowViewModel { IsFolderTreeVisible = true };
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var splitter = window.FindControl<GridSplitter>("FolderPanelSplitter");
        Assert.NotNull(splitter);
        var tree = window.FindControl<TreeView>("FolderTree");
        Assert.NotNull(tree);

        double treeWidthBefore = tree!.Bounds.Width;
        // Sanity floor, not a magic-number pin: proves the pre-drag TreeView is already filling the ~200px
        // panel (not some degenerate near-zero size) so the post-drag growth assertion below is meaningful.
        Assert.True(treeWidthBefore > 150,
            $"expected the TreeView to already fill roughly the 200px panel before any drag, but it was {treeWidthBefore}");

        Point splitterCenter = splitter!.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                                ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);

        window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(splitterCenter + new Point(80, 0), RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(splitterCenter + new Point(80, 0), MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        double treeWidthAfter = tree.Bounds.Width;

        // Without the fix, treeWidthAfter == treeWidthBefore (the Border stayed pinned at 200 regardless of
        // the drag) — any real growth already proves it; +40 (half the 80px drag) leaves headroom for layout
        // rounding while staying comfortably non-vacuous.
        Assert.True(treeWidthAfter > treeWidthBefore + 40,
            $"expected the TreeView itself (not just the grid column) to grow past {treeWidthBefore} after an 80px right-drag, but it stayed at {treeWidthAfter}");

        window.Close();
    }
}
