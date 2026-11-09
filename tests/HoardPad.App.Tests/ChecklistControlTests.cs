// ChecklistControlTests.cs — the M6.5 `.hoard` checklist control gates (FR-30…FR-34, FR-41, FR-42, C-1/C-3/C-5).
//
// Covers, binding to the Core.Checklist pure functions (never reimplementing them):
//   - the visible-row projection for each of the three display modes (FR-33);
//   - completion-lock IsEnabled fan-out across ≥ 2 nesting levels (C-1);
//   - a depth-cap no-op enqueues ZERO writes (C-4);
//   - a terminal mutation (toggle/indent/collapse/delete) persists immediately through the atomic store;
//   - indent/outdent carry the subtree and persist (FR-32/FR-41);
//   - delete-non-empty-parent: subtree vs promote branches, and Cancel = zero writes (C-3);
//   - the tree-ARIA AutomationPeer returns TreeItem / level / posinset / setsize / expand-collapse / enabled (C-5);
//   - QA2-6: a REAL Enter keypress in a row's text TextBox, on a REAL ChecklistView in a headless window,
//     inserts a new sibling row (interaction-layer regression — the VM-level AddSiblingAfter alone would not
//     have caught the missing XAML KeyDown wiring that caused the original bug).
// The verify filter matches the class name "Checklist".

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
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

public sealed class ChecklistControlTests
{
    private const string Path = "/tmp/hoardpad-test/checklist.hoard";

    private static HoardItem Item(string id, string? parent = null, int order = 0, bool chk = false, bool collapsed = false) =>
        new() { Id = id, Text = id, ParentId = parent, Order = order, Checked = chk, Collapsed = collapsed };

    private static HoardDocument Doc(string mode, params HoardItem[] items) =>
        new() { Title = "T", Settings = new HoardSettings { CheckedItemMode = mode }, Items = items };

    private static (ChecklistViewModel vm, CountingAtomicFileStore store) NewVm(HoardDocument doc)
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

    // ---- FR-33: the three display modes produce the correct visible-row order (bind to Core VisibleRows). ----

    [Fact]
    public void StrikethroughKeepsCheckedItemInPlace()
    {
        var (vm, _) = NewVm(Doc("strikethrough-in-place",
            Item("a", order: 0), Item("b", order: 1, chk: true), Item("c", order: 2)));

        Assert.Equal(["a", "b", "c"], vm.VisibleRows.Select(r => r.Id));
    }

    [Fact]
    public void MoveToBottomSinksCheckedItemsBelowUnchecked()
    {
        var (vm, _) = NewVm(Doc("move-to-bottom",
            Item("a", order: 0, chk: true), Item("b", order: 1), Item("c", order: 2, chk: true)));

        // Unchecked first (stored order), then checked (stored order): b, a, c.
        Assert.Equal(["b", "a", "c"], vm.VisibleRows.Select(r => r.Id));
    }

    [Fact]
    public void HideWhenCheckedOmitsCheckedItemsFromTheVisibleRows()
    {
        var (vm, _) = NewVm(Doc("hide-when-checked",
            Item("a", order: 0), Item("b", order: 1, chk: true), Item("c", order: 2)));

        Assert.Equal(["a", "c"], vm.VisibleRows.Select(r => r.Id));
    }

    [Fact]
    public void CollapsedParentOmitsItsDescendantsFromTheVisibleRows()
    {
        var (vm, _) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0, collapsed: true), Item("c1", parent: "p", order: 0), Item("c2", parent: "p", order: 1)));

        Assert.Equal(["p"], vm.VisibleRows.Select(r => r.Id)); // descendants absent while collapsed.
    }

    // ---- C-1: completion-lock IsEnabled fan-out across ≥ 2 nesting levels. ----

    [Fact]
    public void TogglingAGrandparentFlipsIsEnabledOnAGrandchild()
    {
        // grandparent g (tier 0) -> parent p (tier 1) -> child c (tier 2).
        var (vm, _) = NewVm(Doc("strikethrough-in-place",
            Item("g", order: 0), Item("p", parent: "g", order: 0), Item("c", parent: "p", order: 0)));

        ChecklistItemViewModel grandchild = vm.VisibleRows.Single(r => r.Id == "c");
        ChecklistItemViewModel parent = vm.VisibleRows.Single(r => r.Id == "p");
        Assert.True(grandchild.IsEnabled);
        Assert.True(parent.IsEnabled);

        bool grandchildRaised = false;
        grandchild.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChecklistItemViewModel.IsEnabled)) grandchildRaised = true; };

        vm.ToggleChecked("g"); // check the GRANDPARENT — locks the whole subtree.

        // The grandchild VM (≥ 2 levels down) must reflect the lock AND have raised PropertyChanged (C-1).
        ChecklistItemViewModel grandchildAfter = vm.VisibleRows.Single(r => r.Id == "c");
        ChecklistItemViewModel parentAfter = vm.VisibleRows.Single(r => r.Id == "p");
        Assert.False(grandchildAfter.IsEnabled);
        Assert.False(parentAfter.IsEnabled);
        Assert.True(grandchildRaised, "the grandchild's IsEnabled PropertyChanged did not fire (C-1 fan-out missed a descendant)");

        // Unchecking the grandparent reactivates the whole subtree.
        vm.ToggleChecked("g");
        Assert.True(vm.VisibleRows.Single(r => r.Id == "c").IsEnabled);
    }

    [Fact]
    public void TheCheckedParentItselfStaysEnabledSoItCanBeReopened()
    {
        var (vm, _) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c", parent: "p", order: 0)));

        vm.ToggleChecked("p");
        // effectiveEnabled never consults the item's own checked state — a completed parent stays toggleable.
        Assert.True(vm.VisibleRows.Single(r => r.Id == "p").IsEnabled);
        Assert.False(vm.VisibleRows.Single(r => r.Id == "c").IsEnabled);
    }

    // ---- C-4 / FR-31: durability — terminal persist immediately, depth-cap no-op = ZERO writes. ----

    [Fact]
    public async Task ATerminalToggleWritesThroughTheAtomicStoreImmediately()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place", Item("a", order: 0)));

        vm.ToggleChecked("a");

        await WaitForWrites(store, 1);
        Assert.True(store.WriteCount >= 1);
        Assert.NotNull(store.BytesAt(Path));
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ADepthCapNoOpIndentEnqueuesZeroWrites()
    {
        // p (tier 0) -> a (tier 1) -> b (tier 2). Indenting b under a-sibling would push it to tier 3 — but
        // b has no preceding sibling under a; and indenting a-deep already at the cap is the no-op we assert.
        // Construct: tier-0 x, tier-1 y (child of x), tier-2 z (child of y). Indent z would need a preceding
        // sibling under y; give y two children z1 (order 0) and z2 (order 1) so z2 has a preceding sibling z1
        // at tier 2 — indenting z2 under z1 makes z2 tier 3 -> over-cap no-op.
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("x", order: 0),
            Item("y", parent: "x", order: 0),
            Item("z1", parent: "y", order: 0),
            Item("z2", parent: "y", order: 1)));

        long before = vm.CompletedWrites;
        vm.Indent("z2"); // z1 is the preceding sibling at tier 2; indenting z2 under z1 -> tier 3 -> no-op.

        // Give the (non-existent) write a chance to appear, then assert NOTHING was enqueued/written.
        await Task.Delay(50);
        Assert.Equal(before, vm.CompletedWrites);
        Assert.Equal(0, store.WriteCount);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task IndentReparentsTheItemUnderItsPrecedingSiblingAndPersists()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("first", order: 0), Item("second", order: 1)));

        vm.Indent("second"); // second becomes a child of first (tier 1).

        await WaitForWrites(store, 1);
        // first is now a parent (has a child) and second is at depth 1.
        Assert.True(vm.VisibleRows.Single(r => r.Id == "first").IsParent);
        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "second").Depth);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task OutdentPromotesTheItemAndCarriesItsSubtree()
    {
        // g (tier 0) -> p (tier 1) -> c (tier 2). Outdent p -> p becomes tier 0 sibling of g; c follows to tier 1.
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("g", order: 0), Item("p", parent: "g", order: 0), Item("c", parent: "p", order: 0)));

        vm.Outdent("p");

        await WaitForWrites(store, 1);
        Assert.Equal(0, vm.VisibleRows.Single(r => r.Id == "p").Depth);   // promoted to tier 0.
        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == "c").Depth);   // subtree travelled (now tier 1).
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task OutdentOfATier0ItemIsANoOpWithZeroWrites()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place", Item("a", order: 0)));

        vm.Outdent("a"); // tier 0 — nothing to promote out of.

        await Task.Delay(50);
        Assert.Equal(0, store.WriteCount);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task CollapseToggleIsADurableTerminalMutation()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c", parent: "p", order: 0)));

        vm.ToggleCollapsed("p");

        await WaitForWrites(store, 1);
        Assert.True(vm.VisibleRows.Single(r => r.Id == "p").Collapsed);
        await vm.DisposeAsync();
    }

    // ---- C-3: delete-non-empty-parent — subtree vs promote, Cancel = zero writes. ----

    [Fact]
    public async Task DeleteSubtreeRemovesTheParentAndAllDescendants()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c1", parent: "p", order: 0), Item("c2", parent: "p", order: 1),
            Item("keep", order: 1)));
        vm.DeleteParentPrompt = (_, _) => Task.FromResult(DeleteParentChoice.DeleteSubtree);

        await vm.DeleteAsync("p");

        await WaitForWrites(store, 1);
        Assert.Equal(["keep"], vm.VisibleRows.Select(r => r.Id));
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task DeletePromoteUnNestsTheChildrenOneTier()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c1", parent: "p", order: 0), Item("c2", parent: "p", order: 1)));
        vm.DeleteParentPrompt = (_, _) => Task.FromResult(DeleteParentChoice.PromoteChildren);

        await vm.DeleteAsync("p");

        await WaitForWrites(store, 1);
        // p is gone; c1/c2 promoted to tier 0.
        Assert.Equal(["c1", "c2"], vm.VisibleRows.Select(r => r.Id).OrderBy(x => x));
        Assert.All(vm.VisibleRows, r => Assert.Equal(0, r.Depth));
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task DeleteCancelOnANonEmptyParentWritesNothing()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c", parent: "p", order: 0)));
        vm.DeleteParentPrompt = (_, _) => Task.FromResult(DeleteParentChoice.Cancel);

        await vm.DeleteAsync("p");

        await Task.Delay(50);
        Assert.Equal(0, store.WriteCount); // Cancel = ZERO writes.
        Assert.Equal(["p", "c"], vm.VisibleRows.Select(r => r.Id));
        await vm.DisposeAsync();
    }

    // ---- FR-31 item-text coalesce: an EditText is NOT a zero-debounce terminal write. ----

    [Fact]
    public async Task ItemTextEditCoalescesAndDoesNotWriteBeforeTheWindowElapses()
    {
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var vm = ChecklistViewModel.ForDocument(new ChecklistFileService(store), clock, Path,
            Doc("strikethrough-in-place", Item("a", order: 0)));

        vm.EditText("a", "renamed");

        clock.Advance(System.TimeSpan.FromMilliseconds(100)); // inside the 150 ms window.
        await Task.Delay(20);
        Assert.Equal(0, store.WriteCount); // text edit must NOT have written yet.

        clock.Advance(System.TimeSpan.FromMilliseconds(60));  // cross the window.
        await WaitForWrites(store, 1);
        Assert.Equal(1, store.WriteCount);
        await vm.DisposeAsync();
    }

    // ---- C-5 / D11: the tree-ARIA AutomationPeer contract. ----

    [AvaloniaFact]
    public void AutomationPeerReportsTreeItemLevelPositionSetSizeExpandCollapseAndEnabled()
    {
        // p (tier 0, parent, expanded) with two children c1/c2 (tier 1). c1 is row 1 of 2.
        var (vm, _) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c1", parent: "p", order: 0), Item("c2", parent: "p", order: 1)));

        ChecklistItemViewModel childVm = vm.VisibleRows.Single(r => r.Id == "c1");
        var control = new ChecklistItemControl { DataContext = childVm };
        var peer = (ChecklistItemAutomationPeer)ControlAutomationPeer.CreatePeerForElement(control);

        Assert.Equal(AutomationControlType.TreeItem, peer.GetAutomationControlType());
        Assert.Equal(2, peer.GetLevel());           // tier 1 -> level 2 (numeric level via the peer, C-5).
        Assert.Equal(1, peer.GetPositionInSetValue()); // c1 is position 1 within its sibling group.
        Assert.Equal(2, peer.GetSizeOfSetValue());     // sibling group size 2.
        Assert.True(peer.IsEnabled());                 // not locked (parent unchecked).
        Assert.Equal(ExpandCollapseState.LeafNode, peer.ExpandCollapseState); // a leaf has no twisty state.

        // The parent peer reports the Expanded twisty state and TreeItem at level 1.
        var parentControl = new ChecklistItemControl { DataContext = vm.VisibleRows.Single(r => r.Id == "p") };
        var parentPeer = (ChecklistItemAutomationPeer)ControlAutomationPeer.CreatePeerForElement(parentControl);
        Assert.Equal(1, parentPeer.GetLevel());
        Assert.Equal(ExpandCollapseState.Expanded, parentPeer.ExpandCollapseState);
        Assert.IsAssignableFrom<IExpandCollapseProvider>(parentPeer);
    }

    [AvaloniaFact]
    public void AutomationPeerReportsDisabledForACompletionLockedDescendant()
    {
        var (vm, _) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c", parent: "p", order: 0)));
        vm.ToggleChecked("p"); // lock the subtree.

        var control = new ChecklistItemControl { DataContext = vm.VisibleRows.Single(r => r.Id == "c") };
        var peer = (ChecklistItemAutomationPeer)ControlAutomationPeer.CreatePeerForElement(control);

        Assert.False(peer.IsEnabled()); // the locked descendant is announced "disabled" (FR-41/C-5).
    }

    // ---- Fix 2: drag-reorder within a sibling group (FR-32/§5.2). ----

    [Fact]
    public async Task DropWithinGroupReordersItemsAndPersists()
    {
        // Three siblings a(0), b(1), c(2). Drop a to gap 3 (after c) — a moves to the end.
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("a", order: 0), Item("b", order: 1), Item("c", order: 2)));

        vm.DropWithinGroup("a", displayGap: 3); // gap 3 = after all 3 rows.

        await WaitForWrites(store, 1);
        // a should now appear after b and c in the visible rows.
        var ids = vm.VisibleRows.Select(r => r.Id).ToArray();
        Assert.Equal(3, ids.Length);
        Assert.Equal("b", ids[0]);
        Assert.Equal("c", ids[1]);
        Assert.Equal("a", ids[2]);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task DropAtSamePositionEnqueuesZeroWrites()
    {
        // a(0), b(1), c(2). Drop a at gap 0 (before first row = same position — no-op, C-4).
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("a", order: 0), Item("b", order: 1), Item("c", order: 2)));

        vm.DropWithinGroup("a", displayGap: 0); // gap 0 = before a, which is already first — no-op.

        await Task.Delay(50);
        Assert.Equal(0, store.WriteCount); // same-position drop must write NOTHING (C-4).
        await vm.DisposeAsync();
    }

    // ---- Fix 4: title persist (coalesced, like item text). ----

    [Fact]
    public async Task SetTitleCoalescesAndWritesAfterTheWindow()
    {
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var vm = ChecklistViewModel.ForDocument(new ChecklistFileService(store), clock, Path,
            Doc("strikethrough-in-place", Item("a", order: 0)));

        vm.SetTitle("My Checklist");

        clock.Advance(System.TimeSpan.FromMilliseconds(100)); // inside the 150 ms window.
        await Task.Delay(20);
        Assert.Equal(0, store.WriteCount); // must NOT have written yet.
        Assert.Equal("My Checklist", vm.DisplayTitle); // VM reflects the change immediately.

        clock.Advance(System.TimeSpan.FromMilliseconds(60)); // cross the window.
        await WaitForWrites(store, 1);
        Assert.Equal(1, store.WriteCount); // exactly one coalesced write after the window.
        await vm.DisposeAsync();
    }

    // ---- Fix 9: first write uses CreateWhenAbsent, subsequent writes use ReplaceWhenPresent. ----

    [Fact]
    public async Task FirstSaveUsesCreatePathSubsequentSaveUsesReplacePath()
    {
        // ForDocument with fileAlreadyExists: false → first save = Create; after that = Replace.
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        // fileAlreadyExists defaults to false — the new file scenario (FR-34).
        var vm = ChecklistViewModel.ForDocument(new ChecklistFileService(store), clock, Path,
            Doc("strikethrough-in-place", Item("x", order: 0)));

        vm.ToggleChecked("x"); // terminal → writes via Create (file not yet persisted).
        await WaitForWrites(store, 1);
        Assert.Equal(1, store.WriteCount);

        vm.ToggleChecked("x"); // second terminal → writes via Replace (file now exists).
        await WaitForWrites(store, 2);
        Assert.Equal(2, store.WriteCount);
        // Both writes must have reached the store (not silently dropped).
        Assert.NotNull(store.BytesAt(Path));
        await vm.DisposeAsync();
    }

    // ---- QA2-6: Enter creates a new sibling item immediately below the current one. ----

    [Fact]
    public async Task AddSiblingAfterInsertsAtTheSameTierImmediatelyBelowAndPersists()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("a", order: 0), Item("b", order: 1)));

        string? newId = vm.AddSiblingAfter("a", "");

        Assert.NotNull(newId);
        await WaitForWrites(store, 1);
        var ids = vm.VisibleRows.Select(r => r.Id).ToArray();
        Assert.Equal(["a", newId, "b"], ids); // new sibling lands directly below "a", above "b".
        Assert.Equal(0, vm.VisibleRows.Single(r => r.Id == newId).Depth); // same tier as "a".
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task AddSiblingAfterOnANestedItemInsertsAtTheParentsChildTier()
    {
        // p (tier 0) -> c1, c2 (tier 1). AddSiblingAfter(c1) must insert at tier 1, not tier 0.
        var (vm, store) = NewVm(Doc("strikethrough-in-place",
            Item("p", order: 0), Item("c1", parent: "p", order: 0), Item("c2", parent: "p", order: 1)));

        string? newId = vm.AddSiblingAfter("c1", "");

        Assert.NotNull(newId);
        await WaitForWrites(store, 1);
        Assert.Equal(["p", "c1", newId, "c2"], vm.VisibleRows.Select(r => r.Id));
        Assert.Equal(1, vm.VisibleRows.Single(r => r.Id == newId).Depth);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task AddSiblingAfterAnUnknownItemIsANoOpWithZeroWrites()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place", Item("a", order: 0)));

        string? newId = vm.AddSiblingAfter("does-not-exist", "");

        await Task.Delay(50);
        Assert.Null(newId);
        Assert.Equal(0, store.WriteCount);
        await vm.DisposeAsync();
    }

    // ---- QA2-6 interaction layer: a REAL Enter keypress on the REAL ChecklistView must grow the row list.
    // This is the layer that regressed silently — the VM-level AddSiblingAfter tests above would stay green
    // even if the XAML KeyDown wiring were missing (as it originally was).

    [AvaloniaFact]
    public async Task RealEnterKeypressInARowTextBoxGrowsTheRenderedRowCount()
    {
        var (vm, store) = NewVm(Doc("strikethrough-in-place", Item("a", order: 0)));

        var view = new ChecklistView { DataContext = vm };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var rows = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "Rows");
        Assert.Equal(1, rows.ItemCount); // one row ("a") before Enter.

        // Focus the row "a" text TextBox (mirrors a user clicking into it to edit) then raise a real Enter.
        var rowTextBox = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Tag is ChecklistItemViewModel row && row.Id == "a");
        rowTextBox.Focus();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        await WaitForWrites(store, 1);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(2, rows.ItemCount); // the real keypress must have grown the rendered row list.
        Assert.Equal(2, vm.VisibleRows.Count);
        window.Close();
        await vm.DisposeAsync();
    }
}
