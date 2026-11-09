using Xunit;
using static HoardPad.Core.Checklist.Tests.HoardItemBuilder;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// Derived structure helpers (§9.1 "Derived invariants", architecture §5.1): <c>depth</c>, <c>isParent</c>,
/// <c>isLeaf</c>, sibling-group renormalize, and the binding <b>depth-cap no-op</b> — an interactive indent
/// that would push an item (or any descendant) past tier 2 is a no-op enqueuing ZERO writes (C-4).
/// </summary>
public sealed class HoardDepthTests
{
    private static IReadOnlyList<HoardItem> ThreeTierChain() =>
    [
        Item("t0", 0),
        Item("t1", 0, parentId: "t0"),
        Item("t2", 0, parentId: "t1"),
    ];

    [Fact]
    public void DepthIsStrictAncestorCount()
    {
        var tree = ThreeTierChain();
        Assert.Equal(0, HoardHierarchy.Depth(tree[0], tree));
        Assert.Equal(1, HoardHierarchy.Depth(tree[1], tree));
        Assert.Equal(2, HoardHierarchy.Depth(tree[2], tree));
    }

    [Fact]
    public void IsParentAndIsLeafAreComplementary()
    {
        var tree = ThreeTierChain();
        Assert.True(HoardHierarchy.IsParent(tree[0], tree));
        Assert.False(HoardHierarchy.IsLeaf(tree[0], tree));

        Assert.False(HoardHierarchy.IsParent(tree[2], tree)); // t2 has no children.
        Assert.True(HoardHierarchy.IsLeaf(tree[2], tree));
    }

    [Fact]
    public void RenormalizeSiblingOrdersProducesContiguousZeroToNMinusOnePerGroup()
    {
        // Gappy, out-of-order stored orders within one tier-0 group → renormalized 0,1,2 preserving order.
        var items = new List<HoardItem>
        {
            Item("a", 5),
            Item("b", 2),
            Item("c", 9),
        };

        var normalized = HoardHierarchy.RenormalizeSiblingOrders(items);
        var byId = normalized.ToDictionary(i => i.Id, i => i.Order);

        Assert.Equal(0, byId["b"]); // lowest stored order → 0
        Assert.Equal(1, byId["a"]);
        Assert.Equal(2, byId["c"]);
    }

    [Fact]
    public void RenormalizeIsPerSiblingGroupNotGlobal()
    {
        // Two groups: tier-0 (a) and a's children (x, y). Each group renormalizes independently to 0..n-1.
        var items = new List<HoardItem>
        {
            Item("a", 7),
            Item("x", 4, parentId: "a"),
            Item("y", 8, parentId: "a"),
        };

        var normalized = HoardHierarchy.RenormalizeSiblingOrders(items);
        var byId = normalized.ToDictionary(i => i.Id, i => i.Order);

        Assert.Equal(0, byId["a"]); // sole tier-0 member → 0
        Assert.Equal(0, byId["x"]); // child group renormalizes on its own
        Assert.Equal(1, byId["y"]);
    }

    // ---- Depth-cap no-op ---------------------------------------------------------------------------

    [Fact]
    public void IndentWithinCapSucceedsAndReparents()
    {
        // Two tier-0 siblings; indent the second under the first → it becomes tier 1 (within the cap).
        var items = new List<HoardItem>
        {
            Item("first", 0),
            Item("second", 1),
        };

        var result = HoardHierarchy.TryIndent("second", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.NotNull(result);
        var moved = result!.Single(i => i.Id == "second");
        Assert.Equal("first", moved.ParentId);
        Assert.Equal(1, HoardHierarchy.Depth(moved, result));
    }

    [Fact]
    public void IndentThatWouldPushItemPastTier2IsANoOpReturningNull()
    {
        // t0 → t1 → t2 already at the cap. A second tier-2 sibling "t2b" indented under t2 would land at
        // tier 3 → must be a NO-OP (null), enqueuing ZERO writes (C-4). We give t2 a sibling to indent.
        var items = new List<HoardItem>
        {
            Item("t0", 0),
            Item("t1", 0, parentId: "t0"),
            Item("t2", 0, parentId: "t1"),
            Item("t2b", 1, parentId: "t1"), // sibling of t2, also at tier 2
        };

        // Indent t2b under t2 → t2b would become tier 3. Over-cap → no-op.
        var result = HoardHierarchy.TryIndent("t2b", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.Null(result); // ZERO writes: the caller enqueues nothing on a null result.
    }

    [Fact]
    public void IndentThatWouldPushADescendantPastTier2IsANoOp()
    {
        // Group at tier 0: "branch" (with a tier-1 child "leaf") and a preceding sibling "anchor" at tier 1
        // already (anchor is a child of nothing... we need anchor to be tier1). Construct: t0 has children
        // anchor(tier1) and branch(tier1, with child leaf at tier2). Indenting branch under anchor pushes
        // branch→tier2 and its child leaf→tier3 → over-cap via the DESCENDANT → no-op.
        var items = new List<HoardItem>
        {
            Item("t0", 0),
            Item("anchor", 0, parentId: "t0"),
            Item("branch", 1, parentId: "t0"),
            Item("leaf", 0, parentId: "branch"),
        };

        var result = HoardHierarchy.TryIndent("branch", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.Null(result); // the descendant "leaf" would breach tier 2 → whole move is a no-op.
    }

    [Fact]
    public void IndentWithNoPrecedingSiblingIsANoOp()
    {
        // An item at the top of its group has no sibling above it to indent under → no-op (null).
        var items = new List<HoardItem>
        {
            Item("only", 0),
            Item("second", 1),
        };

        Assert.Null(HoardHierarchy.TryIndent("only", items, CheckedItemDisplayMode.StrikethroughInPlace));
    }

    [Fact]
    public void HeldKeyRepeatAtCapStaysANoOpEveryCall()
    {
        // Simulate held-Alt+Shift+→ at the cap: repeated TryIndent on an over-cap target stays null every
        // time — no fsync burst, no state drift (C-4 binding: held-key at tier 2 enqueues no writes).
        var items = new List<HoardItem>
        {
            Item("t0", 0),
            Item("t1", 0, parentId: "t0"),
            Item("t2", 0, parentId: "t1"),
            Item("t2b", 1, parentId: "t1"),
        };

        for (var i = 0; i < 5; i++)
            Assert.Null(HoardHierarchy.TryIndent("t2b", items, CheckedItemDisplayMode.StrikethroughInPlace));
    }

    // ---- Outdent (promote) — the symmetric pure function (FR-32/FR-41, ODQ-11 D9) --------------------

    [Fact]
    public void OutdentPromotesAnItemToBeASiblingOfItsParent()
    {
        // t0 → t1 → t2. Outdent t2 → it becomes a child of t0 (tier 1, sibling of t1).
        var items = ThreeTierChain();

        var result = HoardHierarchy.TryOutdent("t2", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.NotNull(result);
        var moved = result!.Single(i => i.Id == "t2");
        Assert.Equal("t0", moved.ParentId);                 // promoted out one tier (now under the grandparent).
        Assert.Equal(1, HoardHierarchy.Depth(moved, result)); // depth reduced from 2 to 1.
    }

    [Fact]
    public void OutdentCarriesTheWholeSubtree()
    {
        // g → p → c. Outdent p → p becomes tier 0; c follows p and lands at tier 1 (subtree travels, D9).
        var items = new List<HoardItem>
        {
            Item("g", 0),
            Item("p", 0, parentId: "g"),
            Item("c", 0, parentId: "p"),
        };

        var result = HoardHierarchy.TryOutdent("p", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.NotNull(result);
        Assert.Null(result!.Single(i => i.Id == "p").ParentId);          // p promoted to tier 0.
        Assert.Equal("p", result.Single(i => i.Id == "c").ParentId);     // c still a child of p…
        Assert.Equal(1, HoardHierarchy.Depth(result.Single(i => i.Id == "c"), result)); // …now at tier 1.
    }

    [Fact]
    public void OutdentPlacesThePromotedItemImmediatelyAfterItsOldParent()
    {
        // Grandparent group: gp with children A, B. A has a child a1. Outdent a1 → it joins the gp group
        // immediately AFTER A (its old parent), before B.
        var items = new List<HoardItem>
        {
            Item("gp", 0),
            Item("A", 0, parentId: "gp"),
            Item("B", 1, parentId: "gp"),
            Item("a1", 0, parentId: "A"),
        };

        var result = HoardHierarchy.TryOutdent("a1", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.NotNull(result);
        var gpGroup = HoardHierarchy.ChildrenOf("gp", result!);
        Assert.Equal(["A", "a1", "B"], gpGroup.Select(i => i.Id)); // a1 sits just after its old parent A.
    }

    [Fact]
    public void OutdentOfATier0ItemIsANoOpReturningNull()
    {
        // A tier-0 item has no parent to promote out of → no-op (null), ZERO writes (C-4 symmetry).
        var items = new List<HoardItem> { Item("a", 0), Item("b", 1) };

        Assert.Null(HoardHierarchy.TryOutdent("a", items, CheckedItemDisplayMode.StrikethroughInPlace));
    }

    [Fact]
    public void OutdentOfAnAbsentItemIsANoOp()
    {
        var items = new List<HoardItem> { Item("a", 0) };

        Assert.Null(HoardHierarchy.TryOutdent("ghost", items, CheckedItemDisplayMode.StrikethroughInPlace));
    }
}
