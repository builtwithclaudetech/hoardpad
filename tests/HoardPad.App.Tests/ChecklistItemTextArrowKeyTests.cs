// ChecklistItemTextArrowKeyTests.cs — QA5-B6 regression: bare ← / → while a row's item-text TextBox has
// focus must move the caret (even at the very start/end of the text), never collapse/expand the row.
//
// Root cause (confirmed against live source, ChecklistView.axaml.cs OnRowKeyDown): the FR-42 collapse/
// expand cases matched on ANY bare-modifier ← / → landing on a focused parent row, with no check for
// whether the keyboard focus was actually inside that row's item-text TextBox as opposed to some other
// focusable part of the row (checkbox, twisty, delete button). The fix adds an early guard — bare ← / →
// while focus resolves inside the row's item-text TextBox returns without setting e.Handled, so the key
// flows through to TextBox's own caret handling instead of ever reaching the switch below.
//
// Each assertion below is a positive pair (per CLAUDE.md §4 / claude_coding.md §12): "caret moved" AND
// "row did not collapse/expand" are both asserted, so a broken fix that swallows the key and leaves the
// caret motionless (but also doesn't collapse) would still fail these tests — it is not enough to prove
// the row didn't collapse.

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

public sealed class ChecklistItemTextArrowKeyTests
{
    private const string Path = "/tmp/hoardpad-test/checklist-arrow-caret.hoard";

    private static HoardItem Parent(string id, bool collapsed, string text) =>
        new() { Id = id, Text = text, ParentId = null, Order = 0, Collapsed = collapsed };

    private static HoardItem Child(string id, string parentId, int order = 0) =>
        new() { Id = id, Text = id, ParentId = parentId, Order = order };

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

    private static Button TwistyButton(ChecklistView view, string itemId)
    {
        var control = view.GetVisualDescendants()
            .OfType<ChecklistItemControl>()
            .Single(c => c.Item is { } item && item.Id == itemId);
        return control.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("twisty"));
    }

    /// <summary>Activate the headless window's focus scope (a real click) then focus the given control,
    /// mirroring ChecklistFocusTests/ChecklistIndentOutdentTests' ActivateAndFocus pattern.</summary>
    private static void ActivateAndFocus(Window window, InputElement control)
    {
        window.MouseDown(new Point(1, 1), MouseButton.Left);
        window.MouseUp(new Point(1, 1), MouseButton.Left);
        control.Focus();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    // ---- 1. Mid-string: caret moves, row does not collapse/expand. ----

    [AvaloniaFact]
    public async Task BareLeftArrowMidStringMovesCaretAndDoesNotCollapseTheFocusedParent()
    {
        var (vm, _) = NewVm(Doc(Parent("p", collapsed: false, text: "parent-label"), Child("c", "p")));
        var (window, view) = Attach(vm);

        TextBox box = RowTextBox(view, "p");
        ActivateAndFocus(window, box);
        box.CaretIndex = 6; // mid-string.

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(5, box.CaretIndex); // the caret actually moved.
        Assert.False(vm.VisibleRows.Single(r => r.Id == "p").Collapsed); // the row did not collapse.

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task BareRightArrowMidStringMovesCaretAndDoesNotExpandTheFocusedCollapsedParent()
    {
        var (vm, _) = NewVm(Doc(Parent("p", collapsed: true, text: "parent-label"), Child("c", "p")));
        var (window, view) = Attach(vm);

        TextBox box = RowTextBox(view, "p");
        ActivateAndFocus(window, box);
        box.CaretIndex = 6; // mid-string.

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(7, box.CaretIndex); // the caret actually moved.
        Assert.True(vm.VisibleRows.Single(r => r.Id == "p").Collapsed); // the row did not expand.

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- 2. Explicit edge cases: caret at index 0 pressing Left, and at the end pressing Right. The row
    // must still not toggle collapse/expand — this is the case the original bug fired on unconditionally. ----

    [AvaloniaFact]
    public async Task BareLeftArrowAtCaretIndexZeroStillDoesNotCollapseTheFocusedParent()
    {
        var (vm, _) = NewVm(Doc(Parent("p", collapsed: false, text: "parent-label"), Child("c", "p")));
        var (window, view) = Attach(vm);

        TextBox box = RowTextBox(view, "p");
        ActivateAndFocus(window, box);
        box.CaretIndex = 0; // already at the start.

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(0, box.CaretIndex); // stays at the start (can't move further left).
        Assert.False(vm.VisibleRows.Single(r => r.Id == "p").Collapsed); // must NOT have collapsed.

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task BareRightArrowAtEndOfTextStillDoesNotExpandTheFocusedCollapsedParent()
    {
        var (vm, _) = NewVm(Doc(Parent("p", collapsed: true, text: "parent-label"), Child("c", "p")));
        var (window, view) = Attach(vm);

        TextBox box = RowTextBox(view, "p");
        ActivateAndFocus(window, box);
        box.CaretIndex = box.Text!.Length; // already at the end.

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(box.Text!.Length, box.CaretIndex); // stays at the end (can't move further right).
        Assert.True(vm.VisibleRows.Single(r => r.Id == "p").Collapsed); // must NOT have expanded.

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- 3. Regression guard: focus NOT in the item-text TextBox — bare ← / → must still collapse/expand
    // a focused parent exactly as before (FR-42 must survive this fix). ----

    [AvaloniaFact]
    public async Task BareArrowsStillCollapseAndExpandWhenFocusIsOnTheTwistyNotTheTextBox()
    {
        var (vm, _) = NewVm(Doc(Parent("p", collapsed: false, text: "parent-label"), Child("c", "p")));
        var (window, view) = Attach(vm);

        ActivateAndFocus(window, TwistyButton(view, "p"));

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.True(vm.VisibleRows.Single(r => r.Id == "p").Collapsed); // collapsed via the twisty focus.

        // ToggleCollapsed rebuilds VisibleRows (Clear+Add), which resets every row container and drops
        // keyboard focus (a pre-existing, out-of-scope-for-QA5-B6 behavior — unlike Indent/Outdent it does
        // not raise FocusRequested). Re-focus the twisty for the second press, same as a real user re-click.
        ActivateAndFocus(window, TwistyButton(view, "p"));

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.False(vm.VisibleRows.Single(r => r.Id == "p").Collapsed); // expanded back.

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- 4. Regression guard: Alt+Shift+←/→ indent/outdent must still work while the item-text TextBox is
    // focused — the guard only intercepts an UNMODIFIED ← / →, never the Alt+Shift chord. ----

    [AvaloniaFact]
    public async Task AltShiftRightStillIndentsWhileTheItemTextBoxIsFocused()
    {
        var (vm, _) = NewVm(Doc(Item("first", order: 0), Item("second", order: 1)));
        var (window, view) = Attach(vm);

        TextBox box = RowTextBox(view, "second");
        ActivateAndFocus(window, box);
        box.CaretIndex = 3; // mid-string, same focus state that blocks the bare arrow tests above.

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt | RawInputModifiers.Shift);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "second").Depth); // indented under "first".

        window.Close();
        await vm.DisposeAsync();
    }
}
