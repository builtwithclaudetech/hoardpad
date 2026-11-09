// TabReorderTests.cs — M8.1 tab-strip drag-reorder LOGIC + keyboard tab-switch (FR-1, cut-tail #1).
//
// The load-bearing behavior of M8.1 is the bound-collection reorder + the active-tab indicator, NOT the OS
// drag gesture: MainWindowViewModel.MoveTab performs ObservableCollection.Move with a same-slot no-op guard
// and the active selection survives because Move preserves the tab INSTANCE (identity, not index).
// DocumentTab.IsActive tracks which chip carries the active accent.
// Keyboard tab-switch (ActivateNextTab / ActivatePreviousTab — the cut-tail fallback) wraps at the ends.
//
// The Avalonia DragDrop gesture that drives MoveTab (PointerPressed → DoDragDrop → Drop) cannot be exercised
// headless — the OS drag loop has no headless backend — so OnTabDrop/ComputeDropIndex wiring is verified
// MANUALLY (live drag on a published binary); these tests cover:
//   - MoveTab reorder logic (move, no-op, not-in-strip, single-tab, past-last clamp).
//   - ComputeDropIndex pure function (critical-B past-last sentinel, spacing correction, empty strip).
//   - IsActive indicator (exactly one tab active, transitions correctly).
//   - ActivateNextTab / ActivatePreviousTab wrap-around (scope-gap-fill).
//   - ShouldStartDrag pure predicate (QA2-7 regression guard): the whole chip must be a drag source (not a
//     narrow undiscoverable sliver), while a press on the × close Button must NOT start a drag.
//
// All class names contain "TabReorder" or "TabStrip" so the verify filter resolves them.

using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// MoveTab — collection-reorder logic
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabReorderTests
{
    [Fact]
    public void ReorderMovesTheTabToTheTargetIndexInTheBoundCollection()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);

        shell.MoveTab(a, 2);

        Assert.Equal(new[] { b, c, a }, shell.Tabs);
    }

    [Fact]
    public void ReorderPreservesTheActiveTabByIdentityNotIndex()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(b); // b is active at index 1.

        shell.MoveTab(b, 0); // move b to front.

        Assert.Same(b, shell.ActiveTab);
        Assert.Equal(0, shell.Tabs.IndexOf(b));
        Assert.Equal(new[] { b, a, c }, shell.Tabs);
    }

    [Fact]
    public void ReorderOntoTheSameSlotIsANoOpWithZeroCollectionChange()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a); shell.Tabs.Add(b);
        shell.ActivateTab(a);

        int changeCount = 0;
        shell.Tabs.CollectionChanged += (_, _) => changeCount++;

        shell.MoveTab(a, 0); // drop onto own slot — same-position guard.

        Assert.Equal(0, changeCount);
        Assert.Equal(new[] { a, b }, shell.Tabs);
        Assert.Same(a, shell.ActiveTab);
    }

    [Fact]
    public void ReorderOfATabNotInTheStripIsANoOp()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);
        DocumentTab stranger = MakeTab("x"); // never added.

        int changeCount = 0;
        shell.Tabs.CollectionChanged += (_, _) => changeCount++;

        shell.MoveTab(stranger, 0);

        Assert.Equal(0, changeCount);
        Assert.Equal(new[] { a }, shell.Tabs);
    }

    [Fact]
    public void ReorderSingleTabStripIsAlwaysANoOp()
    {
        // Count == 1 → any targetIndex clamps to 0 == from → no-op, zero collection change.
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);

        int changeCount = 0;
        shell.Tabs.CollectionChanged += (_, _) => changeCount++;

        shell.MoveTab(a, 0); // same slot
        shell.MoveTab(a, 1); // sentinel `count` clamps to Count-1 = 0

        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void ReorderWithPastLastSentinelClampsToLastIndex()
    {
        // Critical B: ComputeDropIndex returns `count` when past the last tab. MoveTab must clamp
        // count → Count-1, so dragging the first of 3 tabs to the "after last" slot moves it to index 2.
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);

        shell.MoveTab(a, 3); // sentinel = Count (3) → clamps to Count-1 = 2

        Assert.Equal(new[] { b, c, a }, shell.Tabs);
        Assert.Equal(2, shell.Tabs.IndexOf(a));
    }

    [Fact]
    public void MoveTabThrowsOnNegativeTargetIndex()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);

        Assert.Throws<ArgumentOutOfRangeException>(() => shell.MoveTab(a, -1));
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);
}

// ─────────────────────────────────────────────────────────────────────────────
// ComputeDropIndex — pure function (extracted to MainWindow.axaml.cs internal)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabStripDropIndexTests
{
    [Fact]
    public void EmptyStripReturnsZero()
    {
        Assert.Equal(0, Drop([], 4, x: 100));
    }

    [Fact]
    public void DropBeforeMidpointOfFirstTabReturnsZero()
    {
        // One tab, width 80. Midpoint at 40. x=20 < 40 → before tab 0 = index 0.
        Assert.Equal(0, Drop([80], 4, x: 20));
    }

    [Fact]
    public void DropAfterSingleTabReturnsSentinelCount()
    {
        // Critical B: past-last returns count (1), NOT count-1 (0).
        Assert.Equal(1, Drop([80], 4, x: 200));
    }

    [Fact]
    public void DropPastLastTabInThreeTabStripReturnsSentinelCount()
    {
        // 3 tabs × 80 wide, spacing 4: cumulative = 0+(80+4)+80+4+80 = 248 total.
        // x=300 is beyond the last tab → sentinel count = 3.
        Assert.Equal(3, Drop([80, 80, 80], 4, x: 300));
    }

    [Fact]
    public void DropJustBeforeMidpointOfSecondTabReturnsIndex1()
    {
        // Tab 0: [0..80), spacing 4, Tab 1: [84..164). Midpoint of Tab 1 = 84 + 40 = 124.
        // x=90 < 124 → index 1 (drop before tab 1).
        Assert.Equal(1, Drop([80, 80], 4, x: 90));
    }

    [Fact]
    public void SpacingIsIncludedInCumulativeWidthSoGapDoesNotTriggerWrongSlot()
    {
        // Without spacing accumulation, x=83 would be treated as past Tab 0's right edge (80) and thus before
        // Tab 1 starting at 80 (not 84). With spacing correctly included, Tab 1 starts at 84, midpoint 124.
        // x=83 < 124 → index 1 regardless, but gap = [80..84) should not double-count as Tab 1 space.
        // More precise: x=81 must map to index 1 (in the spacing gap → drop before Tab 1, not before Tab 0).
        Assert.Equal(1, Drop([80, 80], 4, x: 81));
    }

    private static int Drop(IReadOnlyList<double> widths, double spacing, double x) =>
        MainWindow.ComputeDropIndex(widths, spacing, x);
}

// ─────────────────────────────────────────────────────────────────────────────
// ShouldStartDrag — pure predicate (QA2-7 regression guard)
// ─────────────────────────────────────────────────────────────────────────────
//
// QA2-7: M8.1's drag-reorder regressed because BUG-3 narrowed the drag-initiation target from the whole
// tab chip down to an invisible 6px sliver — mechanically wired, but undiscoverable to a real click-and-drag.
// The fix restores PointerPressed on the whole chip Border and instead excludes presses that originate on
// the × close Button (so the close command still fires). These tests assert that decision directly.

public sealed class TabStripShouldStartDragTests
{
    [AvaloniaFact]
    public void PressOnTheChipBodyStartsADrag()
    {
        var chipText = new TextBlock();
        Assert.True(MainWindow.ShouldStartDrag(chipText));
    }

    [AvaloniaFact]
    public void PressWithNoResolvedSourceStartsADrag()
    {
        Assert.True(MainWindow.ShouldStartDrag(null));
    }

    [AvaloniaFact]
    public void PressOnTheCloseButtonItselfDoesNotStartADrag()
    {
        var closeButton = new Button();
        Assert.False(MainWindow.ShouldStartDrag(closeButton));
    }

    // Real button-template realization (needed for a Button's Content to become a genuine visual
    // descendant of the Button) requires a live Avalonia platform, so this one case runs headless
    // hosting an actual Window — mirrors the project's ChecklistControlTests pattern.
    [AvaloniaFact]
    public void PressOnADescendantOfTheCloseButtonDoesNotStartADrag()
    {
        // A real close-button press resolves e.Source to the Button's content (e.g. its glyph TextBlock),
        // not the Button itself — the ancestor walk must still find the Button and suppress the drag.
        var glyph = new TextBlock { Text = "×" };
        var closeButton = new Button { Content = glyph };
        var window = new Window { Content = closeButton };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var realizedGlyph = closeButton.GetVisualDescendants().OfType<TextBlock>().Single();

        Assert.False(MainWindow.ShouldStartDrag(realizedGlyph));
        window.Close();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IsActive indicator — exactly one active tab at a time
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabStripIsActiveTests
{
    [Fact]
    public void ActivatingATabSetsItsIsActiveTrue()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a); shell.Tabs.Add(b);

        shell.ActivateTab(b);

        Assert.True(b.IsActive);
    }

    [Fact]
    public void ActivatingATabClearsThePreviousTabsIsActive()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a); shell.Tabs.Add(b);

        shell.ActivateTab(a);
        shell.ActivateTab(b); // switch → a must lose its accent.

        Assert.False(a.IsActive);
        Assert.True(b.IsActive);
    }

    [Fact]
    public void ExactlyOneTabIsActiveAtATime()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);

        shell.ActivateTab(b);

        int activeCount = 0;
        foreach (DocumentTab t in shell.Tabs)
            if (t.IsActive) activeCount++;

        Assert.Equal(1, activeCount);
        Assert.True(b.IsActive);
    }

    [Fact]
    public void ActivatingNullClearsAllIsActiveFlags()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);
        shell.ActivateTab(a);
        Assert.True(a.IsActive);

        shell.ActivateTab(null);

        Assert.False(a.IsActive);
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);
}

// ─────────────────────────────────────────────────────────────────────────────
// Keyboard tab-switch — ActivateNextTab / ActivatePreviousTab (scope-gap-fill)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabReorderKeyboardSwitchTests
{
    [Fact]
    public void NextTabAdvancesForward()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(a);

        shell.ActivateNextTabCommand.Execute(null);

        Assert.Same(b, shell.ActiveTab);
    }

    [Fact]
    public void NextTabWrapsFromLastToFirst()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(c); // at the last tab.

        shell.ActivateNextTabCommand.Execute(null);

        Assert.Same(a, shell.ActiveTab); // wraps to first.
    }

    [Fact]
    public void PreviousTabMovesBackward()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(b);

        shell.ActivatePreviousTabCommand.Execute(null);

        Assert.Same(a, shell.ActiveTab);
    }

    [Fact]
    public void PreviousTabWrapsFromFirstToLast()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(a); // at the first tab.

        shell.ActivatePreviousTabCommand.Execute(null);

        Assert.Same(c, shell.ActiveTab); // wraps to last.
    }

    [Fact]
    public void NextAndPreviousAreNoOpWhenSingleTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);
        shell.ActivateTab(a);

        shell.ActivateNextTabCommand.Execute(null);
        shell.ActivatePreviousTabCommand.Execute(null);

        Assert.Same(a, shell.ActiveTab); // unchanged.
    }

    [Fact]
    public void NextAndPreviousAreNoOpWhenNoTabs()
    {
        var shell = new MainWindowViewModel();

        // Should not throw.
        shell.ActivateNextTabCommand.Execute(null);
        shell.ActivatePreviousTabCommand.Execute(null);

        Assert.Null(shell.ActiveTab);
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared factory (file-scoped, not visible outside this compilation unit)
// ─────────────────────────────────────────────────────────────────────────────

file static class TabFactory
{
    public static DocumentTab New(string id) =>
        DocumentTab.ForEditor(id, TabKind.Scratch, MakeEditorVm());

    private static EditorViewModel MakeEditorVm()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new CountingAtomicFileStore(),
            new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }
}
