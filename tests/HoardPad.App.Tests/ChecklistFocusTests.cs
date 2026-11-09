// ChecklistFocusTests.cs — QA3-6 / QA3-19 regression: Indent/Outdent/MoveWithinGroup must re-focus the
// moved item's row after RebuildVisibleRows() drops keyboard focus entirely (ChecklistViewModel's
// FocusRequested event + ChecklistView's OnChecklistFocusRequested wiring).
//
// QA3-19: after any of the three ops, keyboard focus must land back on the SAME item's row TextBox — not
// nowhere, not a neighbor — so a repeated keypress keeps acting on the same item.
// QA3-6: the reported "Outdent moves both the current item and the item above it" is a downstream symptom
// of the QA3-19 focus loss (debug-investigator + hostile-reviewer confirmed via a live disambiguation
// harness: FocusManager.GetFocusedElement() returns NONE after an Outdent, not a neighbor row). The
// DoubleOutdent... test below proves a second keypress fired with no manual re-focus in between still
// targets the originally-focused item.
//
// These are interaction-layer tests (a real ChecklistView attached to a headless Window, real keypresses
// via window.KeyPressQwerty) because a VM-only test cannot see the focus drop: RebuildVisibleRows clears
// and re-adds the ObservableCollection, which makes the ItemsControl recycle every row container — only
// the real FocusManager state exposes the regression.

using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Checklist;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Checklist;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ChecklistFocusTests
{
    private const string Path = "/tmp/hoardpad-test/checklist-focus.hoard";

    private static HoardItem Item(string id, string? parent = null, int order = 0) =>
        new() { Id = id, Text = id, ParentId = parent, Order = order };

    private static HoardDocument Doc(params HoardItem[] items) =>
        new() { Title = "T", Settings = new HoardSettings { CheckedItemMode = "strikethrough-in-place" }, Items = items };

    private static (ChecklistViewModel vm, CountingAtomicFileStore store) NewVm(HoardDocument doc)
    {
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var vm = ChecklistViewModel.ForDocument(new ChecklistFileService(store), clock, Path, doc);
        return (vm, store);
    }

    /// <summary>Attach a real ChecklistView to a headless Window (subscribes vm.FocusRequested) and force a render pass.</summary>
    private static (Window window, ChecklistView view) Attach(ChecklistViewModel vm)
    {
        var view = new ChecklistView { DataContext = vm };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return (window, view);
    }

    private static TextBox RowTextBox(ChecklistView view, string itemId) =>
        view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Tag is ChecklistItemViewModel row && row.Id == itemId);

    /// <summary>Read the currently keyboard-focused row's item id via FocusManager (mirrors ChecklistView.FocusedRow / AiPanelTests' pattern).</summary>
    private static string? FocusedRowId(ChecklistView view)
    {
        if (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() is not Visual focused)
            return null;
        foreach (Visual ancestor in focused.GetSelfAndVisualAncestors())
        {
            if (ancestor is ChecklistItemControl row && row.Item is { } item)
                return item.Id;
        }
        return null;
    }

    /// <summary>
    /// Pump the dispatcher (flushing the posted refocus — Dispatcher.UIThread.Post at Loaded priority,
    /// ChecklistView.OnChecklistFocusRequested) and force a layout/render pass so the target row's
    /// container is realized before FocusRowTextBox looks it up.
    /// </summary>
    private static string? WaitForFocusedRow(ChecklistView view)
    {
        string? last = null;
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            last = FocusedRowId(view);
            if (last is not null)
                return last;
        }
        return last;
    }

    /// <summary>Activate the headless window's focus scope (a real click, matching AiPanelTests) then focus a row's TextBox.</summary>
    private static void ActivateAndFocus(Window window, TextBox box)
    {
        window.MouseDown(new Point(1, 1), MouseButton.Left);
        window.MouseUp(new Point(1, 1), MouseButton.Left);
        box.Focus();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    // ---- QA3-19: focus follows the moved item after each op (row keyboard shortcut path). ----

    [AvaloniaFact]
    public async Task OutdentKeepsFocusOnTheOutdentedItemsOwnRow()
    {
        // p (tier0) -> a (tier1, child of p). Outdent a -> a promotes to tier0.
        var (vm, _) = NewVm(Doc(Item("p", order: 0), Item("a", parent: "p", order: 0)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "a"));
        Assert.Equal("a", FocusedRowId(view));

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt | RawInputModifiers.Shift);

        string? focused = WaitForFocusedRow(view);
        Assert.Equal("a", focused); // focus followed the outdented item — not dropped to none, not a neighbor.
        Assert.Equal(0, vm.VisibleRows.Single(r => r.Id == "a").Depth);

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task IndentKeepsFocusOnTheIndentedItemsOwnRow()
    {
        // "first"(0), "second"(1) siblings at tier0. Indent second -> child of first, tier1.
        var (vm, _) = NewVm(Doc(Item("first", order: 0), Item("second", order: 1)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "second"));

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt | RawInputModifiers.Shift);

        string? focused = WaitForFocusedRow(view);
        Assert.Equal("second", focused);
        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "second").Depth);

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task MoveUpKeepsFocusOnTheMovedItemsOwnRow()
    {
        var (vm, _) = NewVm(Doc(Item("a", order: 0), Item("b", order: 1)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "b"));

        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);

        string? focused = WaitForFocusedRow(view);
        Assert.Equal("b", focused);
        Assert.Equal("b", vm.VisibleRows[0].Id); // b moved to the front.

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task MoveDownKeepsFocusOnTheMovedItemsOwnRow()
    {
        var (vm, _) = NewVm(Doc(Item("a", order: 0), Item("b", order: 1)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "a"));

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);

        string? focused = WaitForFocusedRow(view);
        Assert.Equal("a", focused);
        Assert.Equal("a", vm.VisibleRows[1].Id); // a moved to the second position.

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- Menu-command path: MainWindowViewModel.ChecklistOutdent delegates to the same vm.Outdent, so it
    // benefits from the FocusRequested wiring with no separate fix. ----

    [AvaloniaFact]
    public async Task MenuCommandPathAlsoRefocusesTheOutdentedItemBecauseItRoutesThroughTheSameVmMethod()
    {
        var (vm, _) = NewVm(Doc(Item("p", order: 0), Item("a", parent: "p", order: 0)));
        var (window, view) = Attach(vm); // subscribes vm.FocusRequested via OnDataContextChanged.
        var shell = new MainWindowViewModel();
        shell.ActiveChecklist = vm;
        shell.FocusedChecklistItemId = "a";

        shell.ChecklistOutdentCommand.Execute(null); // MainWindowViewModel.ChecklistOutdent -> vm.Outdent("a").

        string? focused = WaitForFocusedRow(view);
        Assert.Equal("a", focused); // the top-menu path benefits automatically — the fix lives in one place.
        Assert.Equal(0, vm.VisibleRows.Single(r => r.Id == "a").Depth);

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- QA3-6 decisive regression: two consecutive Outdents fired with NO manual re-focus in between must
    // both target the originally-focused item — the neighbor that shifts into its old visual slot must
    // never be silently caught by the second keypress. ----

    [AvaloniaFact]
    public async Task DoubleOutdentWithoutManualRefocusActsOnTheSameItemBothTimesAndNeverTouchesTheNeighbor()
    {
        // P (tier0) -> X, Y (tier1, children of P) ; Q (tier0, sibling of P). The exact QA3-6 repro shape:
        // outdenting X once promotes it to tier0 right after P, and Y (P's only remaining child) shifts up
        // into X's old visual row slot — the shape that, pre-fix, tricked a re-click into hitting Y.
        var (vm, _) = NewVm(Doc(
            Item("P", order: 0),
            Item("X", parent: "P", order: 0),
            Item("Y", parent: "P", order: 1),
            Item("Q", order: 1)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "X"));
        Assert.Equal("X", FocusedRowId(view));

        // First Outdent: X promotes to tier0; Y becomes P's sole child (and slides into X's old row slot).
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt | RawInputModifiers.Shift);
        string? focusedAfterFirst = WaitForFocusedRow(view);
        Assert.Equal("X", focusedAfterFirst); // focus followed X — not dropped to none, not shifted to Y.
        Assert.Equal(0, vm.VisibleRows.Single(r => r.Id == "X").Depth);
        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "Y").Depth); // Y untouched — still a child of P.

        // Second Outdent, fired immediately with NO manual re-selection between presses (the exact QA3-6
        // gesture). X is already tier0, so a correct fix makes this a no-op FOR X; the bug would instead
        // have silently outdented Y (whose row now occupies X's former slot).
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt | RawInputModifiers.Shift);
        string? focusedAfterSecond = WaitForFocusedRow(view);
        Assert.Equal("X", focusedAfterSecond); // still X — the second press targeted X again, not Y.
        Assert.Equal(0, vm.VisibleRows.Single(r => r.Id == "X").Depth); // X: correctly a no-op (already tier0).
        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "Y").Depth); // Y: STILL untouched — QA3-6 is fixed.
        Assert.Equal(["P", "Y", "X", "Q"], vm.VisibleRows.Select(r => r.Id)); // no 3-tier / extra-promoted state.

        window.Close();
        await vm.DisposeAsync();
    }
}
