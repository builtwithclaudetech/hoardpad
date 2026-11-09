// ChecklistMenuCommandTests.cs — unit tests for the top-menu Checklist command path
// (MainWindowViewModel.ChecklistIndent/Outdent/MoveUp/MoveDown, M6.5 / Fix 3).
//
// These tests prove the command logic is correct even though the live wiring
// (setting ActiveChecklist + FocusedChecklistItemId) is deferred to M6.3.
//
// Proven here:
//   - CanExecute is false when ActiveChecklist is null (no active document).
//   - Setting ActiveChecklist + FocusedChecklistItemId flips CanExecute to true and
//     NotifyCanExecuteChanged fires (the ICommand.CanExecuteChanged subscriber is notified).
//   - Executing ChecklistIndentCommand routes to vm.Indent(id) — the correct VM mutation —
//     and the mutation persists via the write queue (one write, not zero).
//   - Executing ChecklistOutdentCommand routes to vm.Outdent(id) and tier-0 no-op = zero writes.
//   - Executing ChecklistMoveUpCommand routes to vm.MoveWithinGroup(id, -1) — mutation + persist.
//   - QA4-3 fix 2: a null FocusedChecklistItemId with a non-null ActiveChecklist now correctly
//     DISABLES CanExecute (the menu greys out honestly instead of looking enabled and silently
//     no-opping — see ChecklistIndentOutdentTests.cs for the dedicated fix 2/fix 3 coverage).

using System.Linq;
using System.Threading.Tasks;
using HoardPad.App.Checklist;
using HoardPad.App.ViewModels;
using HoardPad.Core.Checklist;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ChecklistMenuCommandTests
{
    private const string Path = "/tmp/hoardpad-test/menu.hoard";

    private static HoardItem Item(string id, string? parent = null, int order = 0) =>
        new() { Id = id, Text = id, ParentId = parent, Order = order };

    private static HoardDocument Doc(params HoardItem[] items) =>
        new() { Title = "T", Settings = new HoardSettings { CheckedItemMode = "strikethrough-in-place" }, Items = items };

    private static (ChecklistViewModel checklistVm, CountingAtomicFileStore store) NewChecklistVm(HoardDocument doc)
    {
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var vm = ChecklistViewModel.ForDocument(new ChecklistFileService(store), clock, Path, doc);
        return (vm, store);
    }

    private static async Task WaitForWrites(CountingAtomicFileStore store, int expected)
    {
        for (int i = 0; i < 200 && store.WriteCount < expected; i++)
            await Task.Delay(5);
        Assert.True(store.WriteCount >= expected, $"expected ≥ {expected} writes, saw {store.WriteCount}");
    }

    // ---- CanExecute contract (enabled/disabled based on ActiveChecklist nullability). ----

    [Fact]
    public void ChecklistCommandsAreDisabledWhenActiveChecklistIsNull()
    {
        var shell = new MainWindowViewModel();

        // No active checklist — all four commands must be disabled.
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));
        Assert.False(shell.ChecklistOutdentCommand.CanExecute(null));
        Assert.False(shell.ChecklistMoveUpCommand.CanExecute(null));
        Assert.False(shell.ChecklistMoveDownCommand.CanExecute(null));
    }

    [Fact]
    public async Task SettingActiveChecklistFlipsCanExecuteToTrueAndNotifiesSubscribers()
    {
        var shell = new MainWindowViewModel();
        var (checklistVm, _) = NewChecklistVm(Doc(Item("a", order: 0)));

        bool indentCanExecuteChanged = false;
        bool outdentCanExecuteChanged = false;
        shell.ChecklistIndentCommand.CanExecuteChanged += (_, _) => indentCanExecuteChanged = true;
        shell.ChecklistOutdentCommand.CanExecuteChanged += (_, _) => outdentCanExecuteChanged = true;

        // Setting ActiveChecklist fires NotifyCanExecuteChanged on all four commands.
        shell.ActiveChecklist = checklistVm;
        shell.FocusedChecklistItemId = "a";

        Assert.True(shell.ChecklistIndentCommand.CanExecute(null), "Indent must be enabled when an active checklist is set");
        Assert.True(shell.ChecklistOutdentCommand.CanExecute(null));
        Assert.True(shell.ChecklistMoveUpCommand.CanExecute(null));
        Assert.True(shell.ChecklistMoveDownCommand.CanExecute(null));
        Assert.True(indentCanExecuteChanged, "CanExecuteChanged must fire when ActiveChecklist is assigned");
        Assert.True(outdentCanExecuteChanged);

        // Clearing the active checklist disables them again.
        shell.ActiveChecklist = null;
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));

        await checklistVm.DisposeAsync();
    }

    // ---- Command routing: Indent routes to vm.Indent(id) → mutation persists. ----

    [Fact]
    public async Task ChecklistIndentCommandRoutesToVmIndentAndPersists()
    {
        // two siblings: "first"(0), "second"(1). Indent "second" → it becomes child of "first".
        var (checklistVm, store) = NewChecklistVm(Doc(Item("first", order: 0), Item("second", order: 1)));
        var shell = new MainWindowViewModel();
        shell.ActiveChecklist = checklistVm;
        shell.FocusedChecklistItemId = "second";

        shell.ChecklistIndentCommand.Execute(null);

        // "second" must now be at depth 1 (child of "first") and the write must persist.
        await WaitForWrites(store, 1);
        Assert.Equal(1, checklistVm.VisibleRows.Single(r => r.Id == "second").Depth);
        Assert.True(checklistVm.VisibleRows.Single(r => r.Id == "first").IsParent);

        await checklistVm.DisposeAsync();
    }

    // ---- Outdent: tier-0 item routes to vm.Outdent(id) → no-op → zero writes. ----

    [Fact]
    public async Task ChecklistOutdentCommandOnATier0ItemIsANoOpWithZeroWrites()
    {
        // "a" is tier-0; outdenting it is a pure no-op (TryOutdent returns null).
        var (checklistVm, store) = NewChecklistVm(Doc(Item("a", order: 0)));
        var shell = new MainWindowViewModel();
        shell.ActiveChecklist = checklistVm;
        shell.FocusedChecklistItemId = "a";

        shell.ChecklistOutdentCommand.Execute(null);

        await Task.Delay(50);
        Assert.Equal(0, store.WriteCount); // tier-0 outdent → ZERO writes.

        await checklistVm.DisposeAsync();
    }

    // ---- MoveUp routes to vm.MoveWithinGroup(id, -1) → mutation persists. ----

    [Fact]
    public async Task ChecklistMoveUpCommandRoutesMutationAndPersists()
    {
        // "a"(0), "b"(1): Move "b" up — it swaps with "a".
        var (checklistVm, store) = NewChecklistVm(Doc(Item("a", order: 0), Item("b", order: 1)));
        var shell = new MainWindowViewModel();
        shell.ActiveChecklist = checklistVm;
        shell.FocusedChecklistItemId = "b";

        shell.ChecklistMoveUpCommand.Execute(null);

        await WaitForWrites(store, 1);
        // After move-up, "b" should be first in the visible row order.
        Assert.Equal("b", checklistVm.VisibleRows[0].Id);
        Assert.Equal("a", checklistVm.VisibleRows[1].Id);

        await checklistVm.DisposeAsync();
    }

    // ---- Null FocusedChecklistItemId with non-null ActiveChecklist: CanExecute is honestly disabled ----
    // (QA4-3 fix 2 — was previously enabled-but-a-silent-no-op; the menu now greys out instead).

    [Fact]
    public async Task CommandWithNullFocusedItemIdIsDisabledAndSafeIfExecutedDirectlyAnyway()
    {
        var (checklistVm, store) = NewChecklistVm(Doc(Item("a", order: 0)));
        var shell = new MainWindowViewModel();
        shell.ActiveChecklist = checklistVm;
        shell.FocusedChecklistItemId = null; // no focused row yet.

        // CanExecute is now FALSE (QA4-3 fix 2) — ActiveChecklist alone is no longer enough.
        Assert.False(shell.ChecklistIndentCommand.CanExecute(null));
        shell.ChecklistIndentCommand.Execute(null); // defense-in-depth: still must not throw or write even if forced.

        await Task.Delay(50);
        Assert.Equal(0, store.WriteCount);

        await checklistVm.DisposeAsync();
    }
}
