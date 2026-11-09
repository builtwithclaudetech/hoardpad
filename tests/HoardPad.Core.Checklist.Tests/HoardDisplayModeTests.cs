using Xunit;
using static HoardPad.Core.Checklist.Tests.HoardItemBuilder;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// FR-33 hierarchy-aware display-mode mapping (§9.1, architecture §6.5, TD §5.7). Covers, per the three
/// checked-item display modes: the visible-row walk (incl. the contiguous-subtree + collapsed-parent rules,
/// Gap B), and the display-index → stored-<c>order</c> drop mapping (incl. the named §9.1 edge cases —
/// reverse-ordered-neighbors, no-upper-neighbor, no-lower-neighbor, partition-boundary). The named worked
/// examples are encoded as example-based tests alongside the per-mode sweeps (architecture §1.6: "a pure
/// sweep can pass while never hitting the anchor rule").
/// </summary>
public sealed class HoardDisplayModeTests
{
    // ---- Mode parsing -------------------------------------------------------------------------------

    [Theory]
    [InlineData("strikethrough-in-place", CheckedItemDisplayMode.StrikethroughInPlace)]
    [InlineData("move-to-bottom", CheckedItemDisplayMode.MoveToBottom)]
    [InlineData("hide-when-checked", CheckedItemDisplayMode.HideWhenChecked)]
    [InlineData("some-foreign-mode", CheckedItemDisplayMode.StrikethroughInPlace)] // unknown → default
    public void ParseCheckedItemModeMapsStringToTypedModeWithDefaultFallback(string raw, CheckedItemDisplayMode expected) =>
        Assert.Equal(expected, HoardDisplayModeExtensions.ParseCheckedItemMode(raw));

    // ---- Per-mode visible-row sweeps (flat group) ---------------------------------------------------

    // A flat tier-0 group: Milk, Eggs, Bread(checked), Coffee — the design §5.5 wireframe.
    private static IReadOnlyList<HoardItem> GroceryList() =>
    [
        Item("milk", 0),
        Item("eggs", 1),
        Item("bread", 2, @checked: true),
        Item("coffee", 3),
    ];

    [Fact]
    public void StrikethroughInPlaceKeepsCheckedItemInStoredPosition() =>
        Assert.Equal("milk,eggs,bread,coffee", VisibleSeq(GroceryList(), CheckedItemDisplayMode.StrikethroughInPlace));

    [Fact]
    public void MoveToBottomCollectsCheckedItemsBelowUncheckedInStoredOrder() =>
        Assert.Equal("milk,eggs,coffee,bread", VisibleSeq(GroceryList(), CheckedItemDisplayMode.MoveToBottom));

    [Fact]
    public void HideWhenCheckedDropsCheckedItemsFromTheVisibleWalk() =>
        Assert.Equal("milk,eggs,coffee", VisibleSeq(GroceryList(), CheckedItemDisplayMode.HideWhenChecked));

    // ---- Contiguous-subtree: a checked PARENT block travels/hides as one unit (the binding rule) -----

    // The design §5.5 worked example tree:
    //   Flights (tier-0, order 0, unchecked) — Outbound, Return (children)
    //   Hotel   (tier-0, order 1, CHECKED)   — Book room (unchecked, locked), Confirm (checked, locked)
    //   Packing (tier-0, order 2, unchecked, collapsed — has no children here)
    private static IReadOnlyList<HoardItem> TripTree(bool hotelChecked = true) =>
    [
        Item("flights", 0),
        Item("outbound", 0, parentId: "flights"),
        Item("return", 1, parentId: "flights"),
        Item("hotel", 1, @checked: hotelChecked),
        Item("bookroom", 0, parentId: "hotel"),
        Item("confirm", 1, parentId: "hotel", @checked: true),
        Item("packing", 2, collapsed: true),
    ];

    [Fact]
    public void MoveToBottomSinksCheckedParentBlockAsOneContiguousUnit()
    {
        // §9.1 worked move-to-bottom example: the whole Hotel block sinks below the unchecked siblings,
        // and its subtree stays immediately under it (parent then children, never split).
        var visible = VisibleIds(TripTree(hotelChecked: true), CheckedItemDisplayMode.MoveToBottom);

        Assert.Equal("flights,outbound,return,packing,hotel,bookroom,confirm", Seq(visible));

        // The contiguous-unit guarantee, asserted directly: hotel is immediately followed by its subtree.
        var hotelIndex = visible.ToList().IndexOf("hotel");
        Assert.Equal("bookroom", visible[hotelIndex + 1]);
        Assert.Equal("confirm", visible[hotelIndex + 2]);
    }

    [Fact]
    public void HideWhenCheckedHidesCheckedParentWholeBlock()
    {
        // §9.1 worked hide-when-checked example: checking Hotel hides the entire Hotel block.
        var visible = VisibleIds(TripTree(hotelChecked: true), CheckedItemDisplayMode.HideWhenChecked);

        Assert.Equal("flights,outbound,return,packing", Seq(visible));
        Assert.DoesNotContain("hotel", visible);
        Assert.DoesNotContain("bookroom", visible); // the locked children hide WITH the parent block.
        Assert.DoesNotContain("confirm", visible);
    }

    [Fact]
    public void StrikethroughKeepsCheckedParentBlockInPlaceWithSubtreeFollowing()
    {
        var visible = VisibleSeq(TripTree(hotelChecked: true), CheckedItemDisplayMode.StrikethroughInPlace);
        // Hotel stays in stored position 1 (between Flights' subtree and Packing), subtree follows it.
        Assert.Equal("flights,outbound,return,hotel,bookroom,confirm,packing", visible);
    }

    // ---- Collapsed-parent visible-row rule (Gap B), incl. collapsed-but-UNCHECKED ------------------

    [Fact]
    public void CollapsedParentDescendantsAbsentRegardlessOfCheckedState()
    {
        // Flights is UNCHECKED but collapsed → its children must be absent from the visible walk, while
        // Flights' own row stays present (Gap B, the collapsed-but-unchecked case).
        var tree = new List<HoardItem>
        {
            Item("flights", 0, collapsed: true),
            Item("outbound", 0, parentId: "flights"),
            Item("return", 1, parentId: "flights"),
            Item("packing", 1),
        };

        var visible = VisibleIds(tree, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.Equal("flights,packing", Seq(visible));
        Assert.Contains("flights", visible);           // collapsed parent's OWN row stays present.
        Assert.DoesNotContain("outbound", visible);    // descendants absent though parent is unchecked.
        Assert.DoesNotContain("return", visible);
    }

    [Fact]
    public void CollapsedUncheckedParentChildrenStayActiveInModel()
    {
        // Collapse only suppresses rendering — the children are present in the file and effectiveEnabled.
        var tree = new List<HoardItem>
        {
            Item("flights", 0, collapsed: true),
            Item("outbound", 0, parentId: "flights"),
        };

        Assert.True(HoardHierarchy.EffectiveEnabled(tree[1], tree)); // unchecked parent → child active.
    }

    // ---- strikethrough drop mapping: display gap == stored order -----------------------------------

    [Theory]
    [InlineData(0, "milk")]   // drop above first → before milk
    [InlineData(2, "bread")]  // drop before bread (the checked item, in place)
    [InlineData(4, null)]     // drop at end → tail anchor (order = last+1)
    public void StrikethroughDropMapsDisplayGapDirectlyToStoredOrder(int gap, string? anchorId)
    {
        var items = GroceryList();
        var target = HoardDisplay.MapDropToStoredOrder("coffee", gap, items, CheckedItemDisplayMode.StrikethroughInPlace);

        if (anchorId is null)
            Assert.Equal(items.Max(i => i.Order) + 1, target);
        else
            Assert.Equal(items.Single(i => i.Id == anchorId).Order, target);
    }

    // ---- move-to-bottom: reverse-ordered-neighbours (upper-neighbour-wins) --------------------------

    [Fact]
    public void MoveToBottomReverseOrderedNeighboursUpperNeighbourWins()
    {
        // Unchecked partition has B(order 0), A(order 1) but the display shows them in stored order B,A.
        // Drop the dragged unchecked item X between them: the upper neighbour (B, stored 0) is the anchor;
        // target = upper.Order + 1 = 1 (deterministic, NOT "between two reverse points").
        var items = new List<HoardItem>
        {
            Item("B", 0),
            Item("A", 1),
            Item("X", 2),
            Item("done", 3, @checked: true),
        };

        // displayGap = 1 → between B (idx0) and A (idx1) in the unchecked partition.
        var target = HoardDisplay.MapDropToStoredOrder("X", 1, items, CheckedItemDisplayMode.MoveToBottom);

        Assert.Equal(1, target); // upper neighbour B (order 0) + 1.
    }

    [Fact]
    public void MoveToBottomNoUpperNeighbourAnchorsBeforeLowerNeighbour()
    {
        // Drop X at the very top of the unchecked partition (gap 0): no upper neighbour → anchor before the
        // lower neighbour (the first unchecked member). §9.1 no-upper-neighbour case.
        var items = new List<HoardItem>
        {
            Item("first", 0),
            Item("second", 1),
            Item("X", 2),
            Item("done", 3, @checked: true),
        };

        var target = HoardDisplay.MapDropToStoredOrder("X", 0, items, CheckedItemDisplayMode.MoveToBottom);

        Assert.Equal(0, target); // anchors before "first" (stored order 0).
    }

    [Fact]
    public void MoveToBottomNoLowerNeighbourAnchorsAfterUpperNeighbour()
    {
        // Drop X at the very bottom of the unchecked partition (gap == unchecked count): no lower neighbour
        // in the partition → anchor after the upper neighbour. §9.1 no-lower-neighbour case.
        var items = new List<HoardItem>
        {
            Item("a", 0),
            Item("b", 1),
            Item("X", 2),
            Item("done", 3, @checked: true),
        };

        // Unchecked partition = a, b, X (3 members). Gap 3 = below the last unchecked, at the boundary.
        var target = HoardDisplay.MapDropToStoredOrder("X", 3, items, CheckedItemDisplayMode.MoveToBottom);

        Assert.Equal(items.Single(i => i.Id == "X").Order + 1, target); // after upper neighbour X (order 2).
    }

    [Fact]
    public void MoveToBottomPartitionBoundaryResolvesByDraggedCheckedState()
    {
        // The drop pixel sits exactly at the unchecked/checked boundary. An UNCHECKED dragged item must
        // resolve into the unchecked partition (never silently flipping to checked), §9.1 partition-boundary.
        var items = new List<HoardItem>
        {
            Item("u1", 0),
            Item("u2", 1),
            Item("c1", 2, @checked: true),
            Item("c2", 3, @checked: true),
        };

        // u2 is the dragged unchecked item; gap == 2 = the boundary (after the 2 unchecked, before checked).
        var target = HoardDisplay.MapDropToStoredOrder("u2", 2, items, CheckedItemDisplayMode.MoveToBottom);

        // Resolves to the END of the unchecked partition: after u1 (the surviving upper unchecked neighbour).
        Assert.True(target <= 2, $"unchecked drop must stay in the unchecked partition (got {target}).");
    }

    // ---- hide-when-checked drop mapping: gap counts visible items only -----------------------------

    [Fact]
    public void HideWhenCheckedDropMapsVisibleGapToStoredOrderOfVisibleItemAfterGap()
    {
        // Group: a(0), HIDDEN h(1, checked), b(2), c(3). Visible = a, b, c. Drop at visible gap 1 (between
        // a and b) → stored order of the visible item after the gap (b, stored order 2). §9.1.
        var items = new List<HoardItem>
        {
            Item("a", 0),
            Item("h", 1, @checked: true),
            Item("b", 2),
            Item("c", 3),
        };

        var target = HoardDisplay.MapDropToStoredOrder("c", 1, items, CheckedItemDisplayMode.HideWhenChecked);

        Assert.Equal(2, target); // visible item after the gap is b (stored order 2).
    }

    [Fact]
    public void HideWhenCheckedDropAtEndAnchorsAfterHighestStoredOrderIncludingHidden()
    {
        // Hidden items keep their stored positions and renormalize alongside the visible ones, so a drop at
        // the end anchors after the MAX stored order in the whole group (hidden included), §9.1.
        var items = new List<HoardItem>
        {
            Item("a", 0),
            Item("b", 1),
            Item("h", 2, @checked: true), // hidden, highest stored order
        };

        // Visible = a, b (2 visible). Gap 2 = past the last visible item.
        var target = HoardDisplay.MapDropToStoredOrder("a", 2, items, CheckedItemDisplayMode.HideWhenChecked);

        Assert.Equal(3, target); // after the group max stored order (h = 2) → 3.
    }

    // ---- Property-ish sweep: every mode preserves the same set of non-hidden ids -------------------

    [Theory]
    [InlineData(CheckedItemDisplayMode.StrikethroughInPlace)]
    [InlineData(CheckedItemDisplayMode.MoveToBottom)]
    public void StrikethroughAndMoveToBottomVisibleRowsArePermutationsOfAllItems(CheckedItemDisplayMode mode)
    {
        // No collapse, no hide: strikethrough and move-to-bottom both render EVERY item (a permutation).
        var tree = TripTree(hotelChecked: true).Where(i => i.Id != "packing").ToList(); // drop the collapsed one
        var visible = VisibleIds(tree, mode);

        Assert.Equal(
            tree.Select(i => i.Id).OrderBy(x => x),
            visible.OrderBy(x => x));
    }
}
