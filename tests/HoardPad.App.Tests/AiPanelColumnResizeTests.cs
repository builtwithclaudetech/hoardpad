// AiPanelColumnResizeTests.cs — QA5-B2: hiding the AI panel must reclaim its column so the editor can
// expand into it — mirrors FolderPanelResizeTests.cs's approach exactly (F-TREE1/QA4-13 already proved
// this "Auto column, hidden child measures 0" pattern for the folder-tree column).
//
// Before this fix, MainWindow.axaml's content Grid declared column 4 (the AI panel) as a FIXED pixel
// width ("300"), so it reserved that width regardless of AiPanelView.IsVisible — the exact quirk the
// Region-5 header comment and the QA4-13 comment on the folder Border both explicitly called out and
// deferred ("The AI panel's OWN column 4 stays a fixed '300' ... is explicitly OUT OF SCOPE for QA4-13").
//
// Every assertion here reads the MEASURED column/control width (grid.ColumnDefinitions[...].ActualWidth
// or a real control's rendered Bounds.Width), never IsVisible — IsVisible == false already held on the
// unfixed code while the column stayed 300px wide, so an IsVisible-only assertion would pass against the
// bug and prove nothing (the exact trap this file's header exists to avoid, per the task brief).
//
// These tests drive the REAL, XAML-loaded MainWindow (not a synthetic mirror grid) because the round-trip
// requirement (a dragged width surviving hide/show) depends on MainWindow.axaml.cs's
// SyncAiPanelColumnWidth, code-behind logic that only exists on the real window.
// The verify filter matches the class name "AiPanel".

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiPanelColumnResizeTests
{
    private static (Window Window, Grid Grid, GridSplitter Splitter) OpenRealWindow(bool aiPanelVisible)
    {
        var shell = new MainWindowViewModel { IsAiPanelVisible = aiPanelVisible };
        var window = new MainWindow { DataContext = shell, Width = 900, Height = 600 };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var splitter = window.FindControl<GridSplitter>("AiPanelSplitter");
        Assert.NotNull(splitter);
        var grid = splitter!.Parent as Grid;
        Assert.NotNull(grid);

        return (window, grid!, splitter);
    }

    private static void DragSplitter(Window window, GridSplitter splitter, double deltaX)
    {
        Point splitterCenter = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
                                ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);
        Point target = splitterCenter + new Point(deltaX, 0);

        window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(target, RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(target, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    // ---- Acceptance #1: hidden panel reserves ZERO column space, and the editor grows into it. ----

    [AvaloniaFact]
    public void HiddenAiPanelReservesNoColumnSpace()
    {
        (Window window, Grid grid, GridSplitter _) = OpenRealWindow(aiPanelVisible: false);

        // FAILS pre-fix: column 4 was a fixed "300", so ActualWidth is 300 even though the panel is
        // IsVisible=false — this is the whole bug. PASSES post-fix: an "Auto" column with a hidden child
        // measures to 0, exactly like column 0 (F-TREE1/QA4-13) already does.
        Assert.Equal(0, grid.ColumnDefinitions[4].ActualWidth);

        window.Close();
    }

    [AvaloniaFact]
    public void TheEditorGrowsIntoTheReclaimedSpaceWhenTheAiPanelIsHidden()
    {
        (Window shownWindow, Grid _, GridSplitter _) = OpenRealWindow(aiPanelVisible: true);
        var editorWhenShown = shownWindow.FindControl<EditorView>("EditorSurface");
        Assert.NotNull(editorWhenShown);
        double editorWidthWhenShown = editorWhenShown!.Bounds.Width;
        shownWindow.Close();

        (Window hiddenWindow, Grid _, GridSplitter _) = OpenRealWindow(aiPanelVisible: false);
        var editorWhenHidden = hiddenWindow.FindControl<EditorView>("EditorSurface");
        Assert.NotNull(editorWhenHidden);
        double editorWidthWhenHidden = editorWhenHidden!.Bounds.Width;
        hiddenWindow.Close();

        // FAILS pre-fix: the editor's "*" column never grows because the fixed 300px column 4 keeps
        // eating the space regardless of visibility, so both widths are (near-)identical. PASSES
        // post-fix: with column 4 collapsed to 0, the "*" editor column absorbs the reclaimed ~300px.
        Assert.True(editorWidthWhenHidden > editorWidthWhenShown + 200,
            $"expected the editor to grow by roughly the reclaimed AI-panel width when hidden " +
            $"({editorWidthWhenShown} shown vs {editorWidthWhenHidden} hidden)");
    }

    // ---- Acceptance #2: shown panel is back at its expected (default) width. ----

    [AvaloniaFact]
    public void ShownAiPanelColumnIsThreeHundredByDefault()
    {
        // Constructs the shell with IsAiPanelVisible already true (mirrors
        // RealMainWindowFolderColumnIsTwoHundredWhenFolderTreeIsVisible's convention) — the
        // OnDataContextChanged attach-time sync (not a PropertyChanged transition, since there is none
        // here) must still land on the 300 default, not the AI panel's own naturally-wider content size
        // (measured empirically at ~550px) that a bare "Auto" column would settle at.
        (Window window, Grid grid, GridSplitter _) = OpenRealWindow(aiPanelVisible: true);

        Assert.Equal(300, grid.ColumnDefinitions[4].ActualWidth);

        window.Close();
    }

    // ---- Acceptance #4: the AI splitter occupies no space while hidden. ----

    [AvaloniaFact]
    public void AiPanelSplitterOccupiesNoSpaceWhileHidden()
    {
        (Window window, Grid grid, GridSplitter splitter) = OpenRealWindow(aiPanelVisible: false);

        Assert.False(splitter.IsVisible);
        Assert.Equal(0, grid.ColumnDefinitions[3].ActualWidth);
        Assert.Equal(0, splitter.Bounds.Width);

        window.Close();
    }

    // ---- Acceptance #3: round-trip — a dragged width survives hide/show, not just the default. ----
    //
    // The subtler regression a naive "just make column 4 Auto" fix does NOT close on its own: Avalonia's
    // GridSplitter converts an Auto column to a concrete Pixel GridLength the moment it is dragged (the
    // same mechanism FolderPanelResizeTests.cs's DragThenHideResetsTheFolderColumnToZeroWidth already
    // proves for column 0), and a Pixel column's width is independent of its child's visibility — so
    // without MainWindow.axaml.cs's SyncAiPanelColumnWidth remembering + restoring the value, a dragged
    // width would EITHER stay permanently reserved when hidden (if nothing resets it) OR collapse and
    // then come back at the 300 default instead of the user's chosen size (if something resets it the
    // way ResetFolderColumnWidth does for the folder column, which explicitly does NOT remember).

    [AvaloniaFact]
    public void DragThenHideResetsTheAiPanelColumnToZeroWidth()
    {
        (Window window, Grid grid, GridSplitter splitter) = OpenRealWindow(aiPanelVisible: true);

        double widthBeforeDrag = grid.ColumnDefinitions[4].ActualWidth;
        Assert.Equal(300, widthBeforeDrag);

        // Drag 60px to the LEFT: the AI panel column (to the splitter's right) must grow.
        DragSplitter(window, splitter, -60);

        double widthAfterDrag = grid.ColumnDefinitions[4].ActualWidth;
        Assert.True(widthAfterDrag > widthBeforeDrag,
            $"pre-condition: the drag itself must have grown the column past {widthBeforeDrag}, but it stayed at {widthAfterDrag}");
        Assert.Equal(GridUnitType.Pixel, grid.ColumnDefinitions[4].Width.GridUnitType); // confirms the Auto->Pixel conversion fired.

        // Now hide the panel — the same VM flip the "AI ▸" toolbar button / View ▸ Toggle AI Panel drive.
        ((MainWindowViewModel)window.DataContext!).IsAiPanelVisible = false;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // FAILS pre-fix (fixed "300" column): stays at the dragged width, never reaches 0.
        Assert.Equal(0, grid.ColumnDefinitions[4].ActualWidth);

        window.Close();
    }

    [AvaloniaFact]
    public void DragThenHideThenShowAgainRestoresTheDraggedWidthNotTheDefault()
    {
        (Window window, Grid grid, GridSplitter splitter) = OpenRealWindow(aiPanelVisible: true);
        var shell = (MainWindowViewModel)window.DataContext!;

        DragSplitter(window, splitter, -60); // grow it past the 300 default.
        double draggedWidth = grid.ColumnDefinitions[4].ActualWidth;
        Assert.True(draggedWidth > 300,
            $"pre-condition: the drag must grow the column past the 300 default, but it is {draggedWidth}");

        shell.IsAiPanelVisible = false;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.Equal(0, grid.ColumnDefinitions[4].ActualWidth); // collapsed while hidden.

        shell.IsAiPanelVisible = true;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // FAILS pre-fix (no such code path exists — the column was always a fixed 300, so it would never
        // even reach 0 above). Also demonstrates the fix must do MORE than a bare "reset to Auto on
        // hide" (which alone would restore 300, the default, not the dragged width) — the true assertion
        // is against the REMEMBERED dragged width, never a hardcoded literal.
        Assert.Equal(draggedWidth, grid.ColumnDefinitions[4].ActualWidth);

        window.Close();
    }

    [AvaloniaFact]
    public void SimulatedFourTwentyDragWidthSurvivesAHideShowRoundTripExactly()
    {
        // Same round-trip, but simulates the drag by setting the column's GridLength directly (the exact
        // effect a real GridSplitter drag has on the column, per DraggingTheSplitterResizesTheAiPanelColumn
        // in SplitterTests.cs) to a precise, non-default value — avoids any pixel-arithmetic imprecision
        // from a live simulated mouse drag and asserts an exact round-trip number, per the task brief's
        // literal "simulating a splitter drag, e.g. 420" example.
        (Window window, Grid grid, GridSplitter _) = OpenRealWindow(aiPanelVisible: true);
        var shell = (MainWindowViewModel)window.DataContext!;

        grid.ColumnDefinitions[4].Width = new GridLength(420, GridUnitType.Pixel);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.Equal(420, grid.ColumnDefinitions[4].ActualWidth);

        shell.IsAiPanelVisible = false;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.Equal(0, grid.ColumnDefinitions[4].ActualWidth);

        shell.IsAiPanelVisible = true;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // FAILS pre-fix (fixed "300" column never reaches 0 above, so this line is unreachable in a
        // meaningful state pre-fix; against a naive "reset to Auto with no memory" fix this would fail by
        // reading back 300 instead of 420). PASSES only when the exact dragged width is remembered and
        // restored.
        Assert.Equal(420, grid.ColumnDefinitions[4].ActualWidth);

        window.Close();
    }
}
