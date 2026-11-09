// ChecklistRowLayoutTests.cs — QA5-B5 / F-CHK-WRAP / QA5-B4: one coordinated pass over the ChecklistView
// row Grid (all three tasklist items touch the same row layout — coordinated per the tasklist's explicit
// "do them in one pass" note).
//
// QA5-B5: the twisty's 22px column exists only when a row IsParent — a leaf row's Auto column collapses to
// 0 (Avalonia's IsVisible="False" removes a control from an Auto column's measurement pass entirely), so a
// leaf child's checkbox lands ~22px SHORT of the full 20px-per-depth step, reading as "aligned" with its
// parent's checkbox rather than nested. Fix: give the twisty column a FIXED pixel width (not Auto) so the
// slot is reserved on every row regardless of IsParent — a fixed-width Grid column does not depend on child
// measurement, so it stays 22px whether or not the twisty inside it is IsVisible.
//
// F-CHK-WRAP: item text must word-wrap instead of clip/truncate; the row must grow with wrapped content.
// QA5-B4: the item text must never draw over the fixed-width "✕" delete button.
//
// Both WRAP and B4 share one root cause: the text cell was a horizontal StackPanel, which offers its
// children UNBOUNDED width during measure — so TextWrapping="Wrap" has no effect (nothing ever constrains
// the available width) and, because Avalonia panels don't clip children to their own bounds by default, an
// unwrapped long line visually overflows past the StackPanel's Grid column and draws over the "✕" button
// with no exception raised (a silent visual defect, not a crash). Fix: replace the StackPanel with a
// two-column Grid ("*" for the text, "Auto" for the "(section complete)" badge) so the text cell has a real
// bounded width for Wrap to measure against and Grid's column-based layout prevents the delete button's
// column from ever being drawn into.
//
// These are interaction-layer tests (a real ChecklistView hosted in a real headless Window) because the
// defect is a LAYOUT/measure behavior — a VM-only test cannot see Grid column collapse or StackPanel's
// unbounded-width measurement; only the real Avalonia layout pass exposes it. Each oracle below is a
// MEASUREMENT (Bounds.X / Bounds.Height / Bounds.Width translated into a shared coordinate space), not an
// assumption, and was proven RED against the pre-fix Grid before the fix was applied (see the report).

using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Checklist;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Checklist;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ChecklistRowLayoutTests
{
    private const string Path = "/tmp/hoardpad-test/checklist-row-layout.hoard";

    private const string LongItemText =
        "This is a deliberately long checklist item that must wrap across several lines instead of " +
        "clipping or drawing over the row's delete button when it exceeds the available row width.";

    private static HoardItem Item(string id, string? parent = null, int order = 0) =>
        new() { Id = id, Text = id, ParentId = parent, Order = order };

    private static HoardItem ItemWithText(string id, string text, int order = 0) =>
        new() { Id = id, Text = text, ParentId = null, Order = order };

    private static HoardDocument Doc(params HoardItem[] items) =>
        new() { Title = "T", Settings = new HoardSettings { CheckedItemMode = "strikethrough-in-place" }, Items = items };

    private static (ChecklistViewModel vm, CountingAtomicFileStore store) NewVm(HoardDocument doc)
    {
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var vm = ChecklistViewModel.ForDocument(new ChecklistFileService(store), clock, Path, doc);
        return (vm, store);
    }

    private static (Window window, ChecklistView view) Attach(ChecklistViewModel vm, double width, double height)
    {
        var view = new ChecklistView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return (window, view);
    }

    private static CheckBox RowCheckBox(ChecklistView view, string itemId) =>
        view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Tag is ChecklistItemViewModel row && row.Id == itemId);

    private static TextBox RowTextBox(ChecklistView view, string itemId) =>
        view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Tag is ChecklistItemViewModel row && row.Id == itemId);

    private static ChecklistItemControl RowControl(ChecklistView view, string itemId) =>
        view.GetVisualDescendants().OfType<ChecklistItemControl>().Single(c => c.Item is { } item && item.Id == itemId);

    private static Button DeleteButton(ChecklistView view, string itemId) =>
        RowControl(view, itemId).GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("delete"));

    /// <summary>The X of <paramref name="control"/>'s top-left corner, translated into <paramref name="window"/>'s
    /// coordinate space so rows from different (independently-arranged) containers are directly comparable.</summary>
    private static double AbsoluteLeftX(Visual control, Window window)
    {
        Point? p = control.TranslatePoint(new Point(0, 0), window);
        Assert.True(p is not null, "control must be attached to the same visual tree as the window");
        return p!.Value.X;
    }

    private static double AbsoluteRightX(Visual control, Window window) =>
        AbsoluteLeftX(control, window) + control.Bounds.Width;

    private static double AbsoluteLeftY(Visual control, Window window)
    {
        Point? p = control.TranslatePoint(new Point(0, 0), window);
        Assert.True(p is not null, "control must be attached to the same visual tree as the window");
        return p!.Value.Y;
    }

    private static double AbsoluteLeftYOfBottom(Visual control, Window window) =>
        AbsoluteLeftY(control, window) + control.Bounds.Height;

    // ---- QA5-B5: a same-depth leaf sibling and a same-depth twisty (itself-a-parent) sibling must land at
    // the IDENTICAL checkbox X — one full 20px depth step right of their shared parent — regardless of
    // whether either row shows its own twisty. Pre-fix, only the twisty sibling (which happens to occupy
    // its own 22px Auto column already) passes; the leaf sibling is 22px short. ----

    [AvaloniaFact]
    public async Task DepthOneLeafChildCheckboxLandsOneFullDepthStepRightOfItsParentsCheckbox()
    {
        // P (depth0, parent) -> A (depth1, leaf — no children of its own, so its twisty column is empty).
        var (vm, _) = NewVm(Doc(Item("P", order: 0), Item("A", parent: "P", order: 0)));
        var (window, view) = Attach(vm, width: 400, height: 300);

        double parentX = AbsoluteLeftX(RowCheckBox(view, "P"), window);
        double leafChildX = AbsoluteLeftX(RowCheckBox(view, "A"), window);

        Assert.Equal(parentX + 20, leafChildX, precision: 1);

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task DepthOneTwistyChildCheckboxLandsOneFullDepthStepRightOfItsParentsCheckbox()
    {
        // P (depth0, parent) -> B (depth1, itself a parent of C — B shows its OWN twisty).
        var (vm, _) = NewVm(Doc(
            Item("P", order: 0),
            Item("B", parent: "P", order: 0),
            Item("C", parent: "B", order: 0)));
        var (window, view) = Attach(vm, width: 400, height: 300);

        double parentX = AbsoluteLeftX(RowCheckBox(view, "P"), window);
        double twistyChildX = AbsoluteLeftX(RowCheckBox(view, "B"), window);

        Assert.Equal(parentX + 20, twistyChildX, precision: 1);

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task LeafAndTwistySiblingsAtTheSameDepthLandAtTheIdenticalCheckboxX()
    {
        // The most direct proof of the always-reserved slot: A (leaf) and B (itself-a-parent) are BOTH
        // depth-1 children of P. Pre-fix, B's own twisty column gives it 22px A never gets — they diverge.
        // Post-fix both reserve the slot regardless of their own IsParent — they must land identically.
        var (vm, _) = NewVm(Doc(
            Item("P", order: 0),
            Item("A", parent: "P", order: 0),
            Item("B", parent: "P", order: 1),
            Item("C", parent: "B", order: 0)));
        var (window, view) = Attach(vm, width: 400, height: 300);

        double leafChildX = AbsoluteLeftX(RowCheckBox(view, "A"), window);
        double twistyChildX = AbsoluteLeftX(RowCheckBox(view, "B"), window);

        Assert.Equal(twistyChildX, leafChildX, precision: 1);

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- F-CHK-WRAP: a long item wraps to multiple lines and grows the row's height; a short item stays
    // a single line. Both rows are read from the SAME attached view so the comparison is apples-to-apples. ----

    [AvaloniaFact]
    public async Task LongItemTextWrapsAndGrowsTheRowTallerThanAShortItemsRow()
    {
        var (vm, _) = NewVm(Doc(ItemWithText("short", "short", order: 0), ItemWithText("long", LongItemText, order: 1)));
        // Narrow enough that the long item MUST wrap across several lines to stay on-screen at all.
        var (window, view) = Attach(vm, width: 260, height: 400);

        double shortRowHeight = RowControl(view, "short").Bounds.Height;
        double longRowHeight = RowControl(view, "long").Bounds.Height;
        TextBox longTextBox = RowTextBox(view, "long");

        Assert.True(
            longRowHeight > shortRowHeight,
            $"expected the long item's row ({longRowHeight}px) to grow taller than the short item's row " +
            $"({shortRowHeight}px) — a clipped/unwrapped long line would leave both rows the same height.");

        // Not merely fit-by-luck-and-clipped: the wrapped text control itself grew past one line of text.
        double singleLineHeight = longTextBox.FontSize * 1.2;
        Assert.True(
            longTextBox.Bounds.Height > singleLineHeight * 1.5,
            $"expected the long item's TextBox to wrap across multiple lines (height {longTextBox.Bounds.Height} " +
            $"vs one line ~{singleLineHeight}) — a single unwrapped/clipped line would indicate no real wrap.");

        // Not clipped off the visible window either (mirrors Qa317HistoryWrapRenderTests' off-screen check).
        double rightEdge = AbsoluteRightX(longTextBox, window);
        Assert.True(
            rightEdge <= window.Bounds.Width,
            $"long item TextBox right edge ({rightEdge}) must stay within the window ({window.Bounds.Width}).");

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- QA5-B4: a long item's text container must never draw over the row's "✕" delete button — proven
    // independently of the WRAP fix, per the task's instruction not to assume one fix implies the other. ----

    [AvaloniaFact]
    public async Task LongItemTextRightEdgeNeverOverlapsTheRowsDeleteButton()
    {
        var (vm, _) = NewVm(Doc(ItemWithText("long", LongItemText, order: 0)));
        var (window, view) = Attach(vm, width: 260, height: 400);

        TextBox longTextBox = RowTextBox(view, "long");
        Button deleteButton = DeleteButton(view, "long");

        double textRightEdge = AbsoluteRightX(longTextBox, window);
        double deleteLeftEdge = AbsoluteLeftX(deleteButton, window);

        Assert.True(
            textRightEdge <= deleteLeftEdge + 0.5, // 0.5px float-arrange tolerance.
            $"item text right edge ({textRightEdge}) must not extend past the delete button's left edge " +
            $"({deleteLeftEdge}) — the text must never draw over the '✕' control.");

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- C1 (review round 2, Critical): a CHECKED PARENT's "( section complete )" badge combined with
    // long wrapping text at a narrow width. Pre-fix, the badge shared the text's row via a same-row
    // "*,Auto" Grid split — the Auto badge column claimed its ~140px BEFORE the text got any width, so a
    // narrow panel squeezed the text column to ~20px, wrapping almost character-by-character (measured:
    // 1136px row height at a 260px window — see the RED capture in the report). Fixed by moving the badge
    // onto its OWN row directly below the text (both rows share the identical single "*" column), so the
    // badge can never claim width from the text and can never overlap the delete button either. ----

    private static HoardItem CheckedParentWithText(string id, string text, int order = 0) =>
        new() { Id = id, Text = text, Checked = true, ParentId = null, Order = order };

    private static HoardItem CheckedChild(string id, string parentId, int order = 0) =>
        new() { Id = id, Text = id, Checked = true, ParentId = parentId, Order = order };

    private static TextBlock RowBadge(ChecklistView view, string itemId) =>
        RowControl(view, itemId).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "( section complete )");

    private static TextBlock RowCheckedText(ChecklistView view, string itemId, string text) =>
        RowControl(view, itemId).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);

    [AvaloniaTheory]
    [InlineData(260)]
    [InlineData(320)]
    [InlineData(380)]
    public async Task CheckedParentBadgeWithLongWrappingTextKeepsTheRowHeightBoundedAndNeverOverlapsDelete(double width)
    {
        // A checked parent with a checked child (IsCompletedParent = Checked && IsParent, an entirely
        // ordinary state) whose OWN text is the long wrap fixture — the exact combination the review found
        // untested: every prior wrap/B4 test used a childless, unchecked item, so the badge-visible path
        // was never exercised.
        var (vm, _) = NewVm(Doc(CheckedParentWithText("P", LongItemText, order: 0), CheckedChild("C", "P", order: 0)));
        var (window, view) = Attach(vm, width: width, height: 900);

        ChecklistItemControl rowControl = RowControl(view, "P");
        TextBlock textBlock = RowCheckedText(view, "P", LongItemText);
        TextBlock badge = RowBadge(view, "P");
        Button deleteButton = DeleteButton(view, "P");

        Assert.True(
            rowControl.Bounds.Height < 200,
            $"expected the completed-parent row's height ({rowControl.Bounds.Height}px at width={width}) to stay " +
            $"bounded under ~200px — an unbounded text column squeezed by an unconditional badge column wraps " +
            $"almost character-by-character (previously measured 1136px at 260px width).");

        // The badge must sit directly adjacent to the (possibly multi-line) text — never floating further
        // down / vertically centered across an oversized row ("mid-paragraph").
        double textBottomY = AbsoluteLeftYOfBottom(textBlock, window);
        double badgeTopY = AbsoluteLeftY(badge, window);
        Assert.True(
            badgeTopY <= textBottomY + 10, // small tolerance for the row's 2px badge margin + float arrange.
            $"expected the badge (top Y {badgeTopY}) to sit directly below the text's own bottom edge " +
            $"(Y {textBottomY}), not floating further down inside an oversized row.");

        // Never overlaps the delete button — the exact NEW defect a naive MinWidth-floor fix reintroduced
        // (measured: badge right edge 350px vs delete button left edge 230px at a 260px window).
        double badgeRightEdge = AbsoluteRightX(badge, window);
        double textRightEdge = AbsoluteRightX(textBlock, window);
        double deleteLeftEdge = AbsoluteLeftX(deleteButton, window);
        Assert.True(
            badgeRightEdge <= deleteLeftEdge + 0.5,
            $"badge right edge ({badgeRightEdge}) must not extend past the delete button's left edge ({deleteLeftEdge}).");
        Assert.True(
            textRightEdge <= deleteLeftEdge + 0.5,
            $"text right edge ({textRightEdge}) must not extend past the delete button's left edge ({deleteLeftEdge}).");

        window.Close();
        await vm.DisposeAsync();
    }
}
