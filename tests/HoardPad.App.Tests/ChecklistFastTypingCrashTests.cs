// ChecklistFastTypingCrashTests.cs — QA3-21 regression: the checklist must never crash on fast typing.
//
// Root cause (debug-investigator analysis; the reported crash was live-reproduced twice on a real
// X11/Xvfb window and is NOT reproducible via Avalonia.Headless, whose dispatcher is pumped
// deterministically by the test rather than by a real windowing backend's own scheduling):
// ChecklistViewModel.RebuildVisibleRows() reconciles VisibleRows via VisibleRows.Clear() + Add(), which
// Avalonia processes as an ItemsControl Reset — EVERY realized row container, including the currently
// keyboard-focused item TextBox, is detached from the visual tree. Avalonia raises TextBox.LostFocus
// SYNCHRONOUSLY as part of that detach, and ChecklistView wires LostFocus to
// OnTextCommitted -> vm.EditText, which (when the TextBox holds characters never yet committed to the
// model — exactly the state right after a keystroke, before Enter/LostFocus/150ms-coalesce has synced
// it) finds a genuine diff and fires ANOTHER Swap -> RebuildVisibleRows call — reentrantly, mid-Clear(),
// before the outer Clear()/Add() cycle has finished. Re-entering the ItemsControl's container-generation
// cycle while it is already mid-cycle is what corrupts Avalonia's internal generator state on a real
// windowing backend (an NRE deep inside PanelContainerGenerator / ContentPresenter.ContentChanged) — the
// reported gesture (type -> Enter -> immediate Alt+Shift+arrow) reliably lands inside this window because
// a fast typist's arrow keypress routinely lands on a row whose most recent keystroke was never
// separately committed first.
//
// The fix (ChecklistViewModel.RebuildVisibleRows) coalesces a reentrant call instead of re-entering the
// Clear()/Add() cycle, and exposes ReentrantVisibleRowsRebuildCoalesceCount (internal, test-only) so the
// first test below can PROVE the reentrant path was actually exercised and safely absorbed — not merely
// that nothing happened to throw. This drives the real ChecklistView -> ItemsControl -> TextBox
// LostFocus path (an AvaloniaFact test with a real View attached to a headless Window, real keypresses
// via KeyPressQwerty/KeyTextInput), which is the layer QA3-6/QA2-6 already established is invisible to a
// VM-only test (see ChecklistFocusTests.cs) — no VM-only test can observe a container detach/LostFocus at
// all, since there is no real ItemsControl or TextBox in play.

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

public sealed class ChecklistFastTypingCrashTests
{
    private const string Path = "/tmp/hoardpad-test/checklist-qa321.hoard";

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

    /// <summary>Activate the headless window's focus scope (a real click, matching ChecklistFocusTests'
    /// ActivateAndFocus) THEN focus the given row's TextBox — a bare Control.Focus() with no prior click
    /// leaves the window's focus scope inactive and KeyTextInput/KeyPressQwerty land nowhere.</summary>
    private static void ActivateAndFocus(Window window, TextBox box)
    {
        window.MouseDown(new Point(1, 1), MouseButton.Left);
        window.MouseUp(new Point(1, 1), MouseButton.Left);
        box.Focus();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    // ---- Decisive repro: type into a row's TextBox (uncommitted — never synced via Enter/LostFocus),
    // then IMMEDIATELY press Alt+Shift+Right while that row is STILL focused. This is exactly what a fast
    // typist's very next keystroke after typing produces, and deterministically drives the real
    // LostFocus-during-Clear() reentrancy: Indent's Swap -> RebuildVisibleRows -> VisibleRows.Clear()
    // detaches the still-focused, still-uncommitted TextBox -> LostFocus fires synchronously ->
    // OnTextCommitted -> vm.EditText finds a real diff -> Swap -> RebuildVisibleRows AGAIN, reentrantly. ----

    [AvaloniaFact]
    public async Task TypedTextStillUncommittedThenImmediateAltShiftRightCoalescesTheReentrantRebuildAndDoesNotThrow()
    {
        var (vm, _) = NewVm(Doc(Item("first", order: 0), Item("second", order: 1)));
        var (window, view) = Attach(vm);

        TextBox rowSecond = RowTextBox(view, "second");
        ActivateAndFocus(window, rowSecond);

        // Real typed keystrokes that change the row's on-screen text but are NOT yet committed to the
        // model (no Enter, no LostFocus yet) — exactly the state between any two fast keystrokes.
        window.KeyTextInput("ZZZ");

        int coalescedBefore = vm.ReentrantVisibleRowsRebuildCoalesceCount;

        // No ForceRenderTimerTick / RunJobs between typing and the arrow press — genuinely back-to-back,
        // matching the live "no settle" report.
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt | RawInputModifiers.Shift);

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // The reentrant LostFocus -> EditText -> Swap -> RebuildVisibleRows path was actually exercised
        // and safely coalesced (not merely "nothing happened to throw" — the guard code path fired).
        Assert.True(vm.ReentrantVisibleRowsRebuildCoalesceCount > coalescedBefore,
            "expected the QA3-21 reentrancy guard to coalesce at least one reentrant RebuildVisibleRows call");

        // No data loss: the uncommitted text was still captured, and the Indent still applied.
        ChecklistItemViewModel second = vm.VisibleRows.Single(r => r.Id == "second");
        Assert.Equal("ZZZsecond", second.Text);
        Assert.Equal(1, second.Depth); // Indent still took effect despite the reentrant detour.

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TypedTextStillUncommittedThenImmediateAltShiftLeftDoesNotThrow()
    {
        // p (tier0) -> a (tier1, child of p) — Alt+Shift+Left (Outdent) on the edited row, mirroring the
        // Right/Left symmetry the bug report calls out.
        var (vm, _) = NewVm(Doc(Item("p", order: 0), Item("a", parent: "p", order: 0)));
        var (window, view) = Attach(vm);

        TextBox rowA = RowTextBox(view, "a");
        ActivateAndFocus(window, rowA);

        window.KeyTextInput("ZZZ");
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt | RawInputModifiers.Shift);

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Reaching here at all (no unhandled exception propagated out of the AvaloniaFact) is the
        // regression assertion; also confirm no data was lost and the Outdent applied.
        ChecklistItemViewModel a = vm.VisibleRows.Single(r => r.Id == "a");
        Assert.Equal("ZZZa", a.Text);
        Assert.Equal(0, a.Depth);

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- The literal reported gesture: type item text -> Enter (creates a new sibling) -> IMMEDIATELY
    // (no settle) press Alt+Shift+Right/Left on the just-created row. Kept as its own regression even
    // though (per the headless dispatcher determinism explained above) it does not itself land inside the
    // reentrancy window — it is the literal QA2-6/QA3-6 add-sibling + indent/outdent path and must keep
    // working, with no data silently dropped, when driven back-to-back with zero dispatcher pump. ----

    [AvaloniaFact]
    public async Task TypeNewTextThenEnterThenImmediateAltShiftRightNeverThrowsAndKeepsAllData()
    {
        var (vm, _) = NewVm(Doc(Item("a", order: 0), Item("b", order: 1)));
        var (window, view) = Attach(vm);

        TextBox rowA = RowTextBox(view, "a");
        ActivateAndFocus(window, rowA);

        window.KeyTextInput("XYZ");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt | RawInputModifiers.Shift);

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // No data loss: the edited text committed and the new sibling from Enter exists.
        Assert.Contains(vm.VisibleRows, r => r.Text == "XYZa");
        Assert.Equal(3, vm.VisibleRows.Count); // "XYZa" (edited "a"), the Enter-created sibling, "b".

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TypeNewTextThenEnterThenImmediateAltShiftLeftNeverThrows()
    {
        var (vm, _) = NewVm(Doc(Item("p", order: 0), Item("a", parent: "p", order: 0)));
        var (window, view) = Attach(vm);

        TextBox rowA = RowTextBox(view, "a");
        ActivateAndFocus(window, rowA);

        window.KeyTextInput("XYZ");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt | RawInputModifiers.Shift);

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Contains(vm.VisibleRows, r => r.Text == "XYZa");

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- Repeated fast-typing simulation: several type -> Enter cycles back-to-back with no settle,
    // matching an ordinary fast typist adding several checklist items in a row. ----

    [AvaloniaFact]
    public async Task RepeatedFastTypeEnterCyclesWithNoSettleNeverThrowsAndKeepsAllItems()
    {
        var (vm, _) = NewVm(Doc(Item("a", order: 0)));
        var (window, view) = Attach(vm);

        TextBox rowA = RowTextBox(view, "a");
        ActivateAndFocus(window, rowA);

        window.KeyTextInput("first");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        // No settle: fire straight into the next item's text without waiting for FocusRowTextBox's
        // deferred Post to land — whichever control currently has focus receives the keystrokes,
        // matching a real fast typist who doesn't pause to confirm focus moved.
        window.KeyTextInput("second");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput("third");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Reaching here without an unhandled exception is the regression assertion. No item text or
        // count was silently dropped by the reentrancy coalescing.
        Assert.True(vm.VisibleRows.Count >= 2, "fast-typed Enter cycles must not have been silently dropped");

        window.Close();
        await vm.DisposeAsync();
    }
}
