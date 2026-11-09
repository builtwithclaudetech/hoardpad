using Xunit;
using static HoardPad.Core.Checklist.Tests.HoardItemBuilder;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// F-QA4-3a (H1-style frozen oracle, pre-registered before the fix): <see cref="HoardHierarchy.TryOutdent"/>
/// always reparents under the unambiguous grandparent, but its resulting DISPLAY PLACEMENT must be
/// mode-aware. Under <see cref="CheckedItemDisplayMode.MoveToBottom"/>, checked/unchecked siblings render
/// as two separate blocks (<see cref="HoardDisplay.DisplayOrderedGroup"/>) regardless of raw stored order.
/// When the promoted item's own <c>checked</c> state DIFFERS from its (checked) old parent's, anchoring the
/// promoted item's raw order "immediately after the old parent" scatters it mid-block instead of at the
/// partition boundary — the pre-fix bug this file exposes.
/// <para><see cref="CheckedItemDisplayMode.StrikethroughInPlace"/> (display == stored order) and <see
/// cref="CheckedItemDisplayMode.HideWhenChecked"/> (raw order, checked rows simply filtered — no
/// reordering) were ALREADY correct: neither mode reorders siblings relative to a checked/unchecked
/// boundary, so the plain raw-order-adjacent placement is exactly right in both. Those cases are asserted
/// here as REGRESSION GUARDS (unchanged pre/post fix), not new failing-then-passing cases.</para>
/// </summary>
public sealed class HoardModeAwareOutdentTests
{
    // ---- THE bug: checked old parent, promoted item UNCHECKED, under move-to-bottom -------------------
    //
    // Grandparent group (raw stored order): U1(unchecked,0), P(checked,1) [old parent], U2(unchecked,2),
    // U3(unchecked,3). P has one child X (unchecked). Before outdent, move-to-bottom displays
    // U1,U2,U3,P,X (P's whole checked subtree sinks to the bottom, X travels with it). Outdenting X
    // promotes it to a grandparent-level sibling; since X is unchecked it must land in the UNCHECKED
    // block. The correct position is the boundary edge — LAST in the unchecked block, immediately before
    // the checked block starts — never scattered to some raw-order-adjacent slot inside the block.
    [Fact]
    public void CheckedOldParentMoveToBottomPromotedUncheckedItemLandsLastInTheUncheckedBlock()
    {
        var items = new List<HoardItem>
        {
            Item("U1", 0),
            Item("P", 1, @checked: true),
            Item("U2", 2),
            Item("U3", 3),
            Item("X", 0, parentId: "P"),
        };

        Assert.Equal("U1,U2,U3,P,X", VisibleSeq(items, CheckedItemDisplayMode.MoveToBottom)); // sanity.

        var result = HoardHierarchy.TryOutdent("X", items, CheckedItemDisplayMode.MoveToBottom);

        Assert.NotNull(result);
        Assert.Null(result!.Single(i => i.Id == "X").ParentId); // promoted to tier 0 (gp of P is the root).
        Assert.Equal("U1,U2,U3,X,P", VisibleSeq(result, CheckedItemDisplayMode.MoveToBottom));
        // NOT "U1,X,U2,U3,P" — the pre-fix bug's raw-order-adjacent placement.
    }

    // The exact same shape, but the checked old parent is the FIRST item in the grandparent group — the
    // pre-fix bug put the promoted item at the very FRONT (ahead of every unrelated unchecked sibling)
    // instead of at the correct boundary position (last-in-unchecked, i.e. immediately before the old
    // parent's now-sunk block).
    [Fact]
    public void CheckedOldParentFirstInGroupMoveToBottomPromotedItemStillLandsAtTheBoundaryNotTheFront()
    {
        var items = new List<HoardItem>
        {
            Item("P", 0, @checked: true),
            Item("U1", 1),
            Item("U2", 2),
            Item("X", 0, parentId: "P"),
        };

        Assert.Equal("U1,U2,P,X", VisibleSeq(items, CheckedItemDisplayMode.MoveToBottom)); // sanity.

        var result = HoardHierarchy.TryOutdent("X", items, CheckedItemDisplayMode.MoveToBottom);

        Assert.NotNull(result);
        Assert.Equal("U1,U2,X,P", VisibleSeq(result!, CheckedItemDisplayMode.MoveToBottom));
        // NOT "X,U1,U2,P" — the pre-fix bug anchored X to P's raw slot (position 0), stranding it ahead
        // of every unrelated unchecked sibling.
    }

    // Symmetric mirror: old parent UNCHECKED, promoted item ITSELF checked (a checked leaf under an
    // unchecked parent is legal — checking a child never requires the parent to be checked). Under
    // move-to-bottom the promoted item must become the FIRST member of the checked block.
    [Fact]
    public void UncheckedOldParentMoveToBottomPromotedCheckedItemLandsFirstInTheCheckedBlock()
    {
        var items = new List<HoardItem>
        {
            Item("P", 0), // unchecked old parent, first in the grandparent group.
            Item("Q", 1, @checked: true), // a pre-existing checked sibling.
            Item("X", 0, parentId: "P", @checked: true),
        };

        Assert.Equal("P,X,Q", VisibleSeq(items, CheckedItemDisplayMode.MoveToBottom)); // sanity (X nests under P, recursion doesn't repartition by X's own checked state).

        var result = HoardHierarchy.TryOutdent("X", items, CheckedItemDisplayMode.MoveToBottom);

        Assert.NotNull(result);
        Assert.Equal("P,X,Q", VisibleSeq(result!, CheckedItemDisplayMode.MoveToBottom)); // X now first-in-checked-block.
    }

    // Same mirror, but with NO pre-existing checked sibling — the "empty target partition" edge the fix's
    // global min/max arithmetic must also get right (X becomes the checked block's only, and therefore
    // first, member).
    [Fact]
    public void UncheckedOldParentMoveToBottomPromotedCheckedItemWithNoOtherCheckedSiblingStillLandsAfterTheUncheckedBlock()
    {
        var items = new List<HoardItem>
        {
            Item("P", 0),
            Item("U2", 1),
            Item("X", 0, parentId: "P", @checked: true),
        };

        var result = HoardHierarchy.TryOutdent("X", items, CheckedItemDisplayMode.MoveToBottom);

        Assert.NotNull(result);
        Assert.Equal("P,U2,X", VisibleSeq(result!, CheckedItemDisplayMode.MoveToBottom));
    }

    // ---- Regression guard: matching checked state (SAME partition) is unaffected by the fix ------------
    //
    // Old parent checked, promoted item ALSO checked: both belong to the checked block, so raw-order-
    // adjacent placement already puts the promoted item directly after its old parent within that shared
    // block. This must stay exactly as it was.
    [Fact]
    public void CheckedOldParentMoveToBottomPromotedCheckedItemStaysAdjacentToTheOldParent()
    {
        var items = new List<HoardItem>
        {
            Item("U1", 0),
            Item("P", 1, @checked: true),
            Item("U2", 2),
            Item("X", 0, parentId: "P", @checked: true),
        };

        var result = HoardHierarchy.TryOutdent("X", items, CheckedItemDisplayMode.MoveToBottom);

        Assert.NotNull(result);
        Assert.Equal("U1,U2,P,X", VisibleSeq(result!, CheckedItemDisplayMode.MoveToBottom));
    }

    // ---- Regression guard: strikethrough-in-place is display == stored order, untouched by the fix ------
    [Fact]
    public void CheckedOldParentStrikethroughPromotedItemStaysImmediatelyAfterTheOldParent()
    {
        var items = new List<HoardItem>
        {
            Item("U1", 0),
            Item("P", 1, @checked: true),
            Item("U2", 2),
            Item("X", 0, parentId: "P"),
        };

        var result = HoardHierarchy.TryOutdent("X", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.NotNull(result);
        Assert.Equal("U1,P,X,U2", VisibleSeq(result!, CheckedItemDisplayMode.StrikethroughInPlace));
    }

    // ---- Regression guard: hide-when-checked has no partition reordering — raw-order-adjacent placement
    // was ALREADY correct (the promoted item simply takes its old parent's raw slot in the filtered view,
    // which is exactly right since hide-when-checked never reorders, only removes checked rows).
    [Fact]
    public void CheckedOldParentHideWhenCheckedPromotedItemTakesTheOldParentsRawSlotInTheFilteredView()
    {
        var items = new List<HoardItem>
        {
            Item("U1", 0),
            Item("P", 1, @checked: true),
            Item("U2", 2),
            Item("U3", 3),
            Item("X", 0, parentId: "P"),
        };

        Assert.Equal("U1,U2,U3", VisibleSeq(items, CheckedItemDisplayMode.HideWhenChecked)); // P+X hidden.

        var result = HoardHierarchy.TryOutdent("X", items, CheckedItemDisplayMode.HideWhenChecked);

        Assert.NotNull(result);
        Assert.Equal("U1,X,U2,U3", VisibleSeq(result!, CheckedItemDisplayMode.HideWhenChecked));
    }

    // ---- Property test: for EVERY mode, the reparent target is always the unambiguous grandparent -------
    // (F-QA4-3a only changes ORDER placement, never the target).
    [Theory]
    [InlineData(CheckedItemDisplayMode.StrikethroughInPlace)]
    [InlineData(CheckedItemDisplayMode.MoveToBottom)]
    [InlineData(CheckedItemDisplayMode.HideWhenChecked)]
    public void OutdentTargetIsAlwaysTheGrandparentRegardlessOfMode(CheckedItemDisplayMode mode)
    {
        var items = new List<HoardItem>
        {
            Item("gp", 0),
            Item("P", 0, parentId: "gp", @checked: true),
            Item("sib", 1, parentId: "gp"),
            Item("X", 0, parentId: "P"),
        };

        var result = HoardHierarchy.TryOutdent("X", items, mode);

        Assert.NotNull(result);
        Assert.Equal("gp", result!.Single(i => i.Id == "X").ParentId);
    }
}
