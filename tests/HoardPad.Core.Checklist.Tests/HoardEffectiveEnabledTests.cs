using Xunit;
using static HoardPad.Core.Checklist.Tests.HoardItemBuilder;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// The FR-41 completion-lock contract: <c>effectiveEnabled(item)</c> = every strict ancestor has
/// <c>checked == false</c> (§9.1, architecture §6.5, TD §5.7). A checked ancestor disables the whole subtree;
/// siblings of a checked item are unaffected; the item itself stays toggleable regardless of its own state.
/// </summary>
public sealed class HoardEffectiveEnabledTests
{
    [Fact]
    public void Tier0ItemIsAlwaysEnabled()
    {
        var tree = new List<HoardItem> { Item("root", 0) };
        Assert.True(HoardHierarchy.EffectiveEnabled(tree[0], tree));
    }

    [Fact]
    public void Tier0ItemIsEnabledEvenWhenItselfChecked()
    {
        // The item's OWN checked state never disables it (so a completed parent can always be unchecked).
        var tree = new List<HoardItem> { Item("root", 0, @checked: true) };
        Assert.True(HoardHierarchy.EffectiveEnabled(tree[0], tree));
    }

    [Fact]
    public void CheckedParentDisablesItsChild()
    {
        var tree = new List<HoardItem>
        {
            Item("parent", 0, @checked: true),
            Item("child", 0, parentId: "parent"),
        };

        Assert.True(HoardHierarchy.EffectiveEnabled(tree[0], tree));   // parent itself toggleable.
        Assert.False(HoardHierarchy.EffectiveEnabled(tree[1], tree));  // child locked by checked parent.
    }

    [Fact]
    public void UncheckedParentLeavesChildEnabled()
    {
        var tree = new List<HoardItem>
        {
            Item("parent", 0, @checked: false),
            Item("child", 0, parentId: "parent"),
        };

        Assert.True(HoardHierarchy.EffectiveEnabled(tree[1], tree));
    }

    [Fact]
    public void ChildOwnCheckedStateDoesNotAffectItsEnablement()
    {
        // A checked child under an UNCHECKED parent is still enabled — its own checked is irrelevant.
        var tree = new List<HoardItem>
        {
            Item("parent", 0, @checked: false),
            Item("child", 0, parentId: "parent", @checked: true),
        };

        Assert.True(HoardHierarchy.EffectiveEnabled(tree[1], tree));
    }

    [Fact]
    public void SiblingsOfACheckedItemAreUnaffected()
    {
        // checkedLeaf is checked; its sibling shares the parent but is NOT a descendant of checkedLeaf, so
        // it stays enabled (the lock is ancestor-scoped, never sibling-scoped).
        var tree = new List<HoardItem>
        {
            Item("parent", 0, @checked: false),
            Item("checkedLeaf", 0, parentId: "parent", @checked: true),
            Item("sibling", 1, parentId: "parent", @checked: false),
        };

        Assert.True(HoardHierarchy.EffectiveEnabled(tree[2], tree));
    }

    [Fact]
    public void MultiLevelAncestorChainAnyCheckedAncestorDisablesTheLeaf()
    {
        // tier0 (unchecked) → tier1 (CHECKED) → tier2 leaf. The mid-chain checked ancestor disables the leaf.
        var tree = new List<HoardItem>
        {
            Item("a", 0),
            Item("b", 0, parentId: "a", @checked: true),
            Item("c", 0, parentId: "b"),
        };

        Assert.True(HoardHierarchy.EffectiveEnabled(tree[0], tree));   // a: no ancestors.
        Assert.True(HoardHierarchy.EffectiveEnabled(tree[1], tree));   // b: ancestor a unchecked → enabled.
        Assert.False(HoardHierarchy.EffectiveEnabled(tree[2], tree));  // c: ancestor b checked → disabled.
    }

    [Fact]
    public void TopAncestorCheckedDisablesEntireSubtree()
    {
        // a (CHECKED) → b → c : both b and c are disabled because the top ancestor a is checked.
        var tree = new List<HoardItem>
        {
            Item("a", 0, @checked: true),
            Item("b", 0, parentId: "a"),
            Item("c", 0, parentId: "b"),
        };

        Assert.True(HoardHierarchy.EffectiveEnabled(tree[0], tree));   // a toggleable.
        Assert.False(HoardHierarchy.EffectiveEnabled(tree[1], tree));  // b locked.
        Assert.False(HoardHierarchy.EffectiveEnabled(tree[2], tree));  // c locked (any checked ancestor).
    }

    [Fact]
    public void EmptyIdItemAncestorChainWalksAllHopsToCheckedRoot()
    {
        // Regression: the old cycle-guard keyed the visited-set on (current.Id.Length > 0 ? current.Id :
        // parentId). When current.Id == "" it adds parentId="p" to the set. On the next iteration current
        // is the parent whose real Id is "p" — seen.Add("p") returns false (collision), terminating the
        // walk one hop short. Result: the checked grandparent "q" was never visited, so EffectiveEnabled
        // returned true (WRONG) and Depth returned 1 (WRONG, should be 2).
        // The fix: key on object-reference identity (ReferenceEqualityComparer) — no string collision.
        //
        // Forest: emptyId (parentId="p") → p (parentId="q") → q (CHECKED, tier-0).
        // All three items stay in the list after HoardLoader repair: HoardLoader only repairs DANGLING or
        // CYCLIC parentIds; an item with an empty Id that references a valid parent is kept as-is (M3.1).
        var tree = new List<HoardItem>
        {
            Item("q", 0, @checked: true),           // tier-0, checked — should lock the whole chain.
            Item("p", 0, parentId: "q"),             // tier-1, unchecked.
            Item("", 0, parentId: "p"),              // tier-2, empty Id — the M3.1 edge case.
        };

        var emptyIdItem = tree[2];

        Assert.Equal(2, HoardHierarchy.Depth(emptyIdItem, tree));          // must walk all 2 ancestors.
        Assert.False(HoardHierarchy.EffectiveEnabled(emptyIdItem, tree));  // q is checked → locked.
    }
}
