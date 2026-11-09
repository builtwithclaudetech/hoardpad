// ChecklistIndentOutdentTests.cs — QA4-3 fix 2 + fix 3 dedicated coverage.
//
// Fix 2 (sub-symptom A, mouse/menu no-op): MainWindowViewModel's CanExecute for the top-menu
// Indent/Outdent/MoveUp/MoveDown commands must require BOTH ActiveChecklist AND FocusedChecklistItemId —
// not ActiveChecklist alone (the doc comment always claimed the latter; the code didn't do it). Proven here
// as a CanExecute state table + a NotifyCanExecuteChanged wiring test + a regression guard that Save-As
// (which shares the underlying ActiveChecklist gate) does NOT require a focused row.
//
// Fix 3 (sub-symptom A, keyboard chord no-op): ChecklistView.OnRowKeyDown's Alt+Shift+arrow /
// Alt+Up/Down dispatch must fall back to the most-recently-focused row when FocusManager resolves to
// nothing genuinely inside a checklist row (Avalonia's own Alt-mnemonic access-key handling can steal the
// chord). Proven here via a real headless Window + ChecklistView + real KeyPressQwerty input, moving real
// focus to ChecklistView's own AddItemBox (a genuine descendant that is NOT a ChecklistItemControl) to
// deterministically reproduce "FocusedRow() returns null while the event still reaches the handler" without
// depending on the exact widget that stole focus in production. The competing-focus test proves the
// fallback always reflects the MOST RECENT row focus, never a stale one.

using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using HoardPad.App.Checklist;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Checklist;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ChecklistIndentOutdentTests
{
    private const string Path = "/tmp/hoardpad-test/indent-outdent.hoard";

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

    // ---- Fix 2: CanExecute requires BOTH ActiveChecklist and FocusedChecklistItemId -------------------

    [Fact]
    public async Task NavCommandsRequireBothActiveChecklistAndFocusedRow()
    {
        var (vm, _) = NewVm(Doc(Item("a")));
        var shell = new MainWindowViewModel();

        // Neither set — disabled.
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));

        // ActiveChecklist set, no focused row — STILL disabled (QA4-3 fix 2 — this is the bug being fixed).
        shell.ActiveChecklist = vm;
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));
        Assert.False(shell.ChecklistOutdentCommand.CanExecute(null));
        Assert.False(shell.ChecklistMoveUpCommand.CanExecute(null));
        Assert.False(shell.ChecklistMoveDownCommand.CanExecute(null));

        // Both set — enabled.
        shell.FocusedChecklistItemId = "a";
        Assert.True(shell.ChecklistIndentCommand.CanExecute(null));
        Assert.True(shell.ChecklistOutdentCommand.CanExecute(null));
        Assert.True(shell.ChecklistMoveUpCommand.CanExecute(null));
        Assert.True(shell.ChecklistMoveDownCommand.CanExecute(null));

        // Focused row cleared again (the reattach reset, ChecklistView.OnDataContextChanged) — disabled again.
        shell.FocusedChecklistItemId = null;
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SettingFocusedChecklistItemIdFiresCanExecuteChangedForAllFourNavCommands()
    {
        var (vm, _) = NewVm(Doc(Item("a")));
        var shell = new MainWindowViewModel { ActiveChecklist = vm };

        bool indentFired = false, outdentFired = false, upFired = false, downFired = false;
        shell.ChecklistIndentCommand.CanExecuteChanged += (_, _) => indentFired = true;
        shell.ChecklistOutdentCommand.CanExecuteChanged += (_, _) => outdentFired = true;
        shell.ChecklistMoveUpCommand.CanExecuteChanged += (_, _) => upFired = true;
        shell.ChecklistMoveDownCommand.CanExecuteChanged += (_, _) => downFired = true;

        shell.FocusedChecklistItemId = "a"; // null -> "a" is a real change.

        Assert.True(indentFired);
        Assert.True(outdentFired);
        Assert.True(upFired);
        Assert.True(downFired);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SaveAsChecklistCommandDoesNotRequireAFocusedRow()
    {
        // Regression guard: Save-As shares the underlying "active checklist" gate but must NOT be coupled
        // to row focus the way Indent/Outdent/MoveUp/MoveDown now are (QA4-3 fix 2 must not overreach).
        var (vm, _) = NewVm(Doc(Item("a")));
        var shell = new MainWindowViewModel { ActiveChecklist = vm }; // no FocusedChecklistItemId set.

        Assert.True(shell.SaveAsChecklistCommand.CanExecute(null));

        await vm.DisposeAsync();
    }

    // ---- Fix 3: keyboard fallback to the most-recently-focused row ------------------------------------

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

    /// <summary>ChecklistView's own always-present "add item" TextBox — a genuine descendant that is NOT a
    /// ChecklistItemControl, so focusing it deterministically makes FocusedRow() return null while staying
    /// inside ChecklistView's routed-event subtree (unlike focusing something outside the view entirely).</summary>
    private static TextBox AddItemBox(ChecklistView view) =>
        view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "AddItemBox");

    private static void ActivateAndFocus(Window window, InputElement control)
    {
        window.MouseDown(new Point(1, 1), MouseButton.Left);
        window.MouseUp(new Point(1, 1), MouseButton.Left);
        control.Focus();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    [AvaloniaFact]
    public async Task AltShiftRightIndentsViaTheLastFocusedRowWhenNoRowIsGenuinelyFocused()
    {
        var (vm, _) = NewVm(Doc(Item("first", order: 0), Item("second", order: 1)));
        var (window, view) = Attach(vm);

        // Focus "second" for real, then steal focus away to a non-row control inside the same view — the
        // FocusedRow() walk now finds nothing, mirroring the production symptom.
        ActivateAndFocus(window, RowTextBox(view, "second"));
        ActivateAndFocus(window, AddItemBox(view));
        Assert.Null(FocusedRowIdViaFocusManager(view)); // sanity: genuinely no row focused right now.

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt | RawInputModifiers.Shift);

        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "second").Depth); // indented under "first" via the fallback.

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task AltUpMovesViaTheLastFocusedRowWhenNoRowIsGenuinelyFocused()
    {
        var (vm, _) = NewVm(Doc(Item("a", order: 0), Item("b", order: 1)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "b"));
        ActivateAndFocus(window, AddItemBox(view));
        Assert.Null(FocusedRowIdViaFocusManager(view));

        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);

        Assert.Equal("b", vm.VisibleRows[0].Id); // "b" moved up via the fallback, not silently ignored.

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task KeyboardFallbackNeverActsOnAStaleRowIdAfterACompetingFocusChange()
    {
        // "a" and "b" are independent tier-0 items (neither can indent under the other via a single
        // Alt+Shift+Right from tier 0 without a preceding sibling — use MoveUp instead, which is
        // observable regardless of position): a(0), b(1), c(2). Focus "a" first (stale candidate), then
        // genuinely focus "c" (the real, most-recent focus), THEN steal focus away — the fallback must act
        // on "c", never the stale "a".
        var (vm, _) = NewVm(Doc(Item("a", order: 0), Item("b", order: 1), Item("c", order: 2)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "a")); // stale candidate — must NOT be the one acted on.
        ActivateAndFocus(window, RowTextBox(view, "c")); // the real, most-recent row focus.
        ActivateAndFocus(window, AddItemBox(view));      // steal focus away — FocusedRow() now null.
        Assert.Null(FocusedRowIdViaFocusManager(view));

        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt); // Move up via the fallback.

        // "c" (order 2) must have moved up to swap with "b" (order 1) — "a" (order 0) must be untouched.
        Assert.Equal(["a", "c", "b"], vm.VisibleRows.Select(r => r.Id));

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task AGenuineFocusOnADifferentRowAlwaysTakesPriorityOverTheFallback()
    {
        // Focus "a", then genuinely focus "b" (no steal in between) — Alt+Shift+Right must act on "b" via
        // FocusedRow() directly, never fall back to the stale "a".
        var (vm, _) = NewVm(Doc(Item("a", order: 0), Item("b", order: 1)));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, RowTextBox(view, "a"));
        ActivateAndFocus(window, RowTextBox(view, "b"));
        Assert.Equal("b", FocusedRowIdViaFocusManager(view)); // genuinely focused — no fallback needed.

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt | RawInputModifiers.Shift);

        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "b").Depth); // "b" indented under "a".
        Assert.Equal(0, vm.VisibleRows.Single(r => r.Id == "a").Depth); // "a" untouched.

        window.Close();
        await vm.DisposeAsync();
    }

    /// <summary>Read the currently keyboard-focused row's item id via FocusManager (mirrors ChecklistView.FocusedRow).</summary>
    private static string? FocusedRowIdViaFocusManager(ChecklistView view)
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
}
