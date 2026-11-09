using Xunit;
using static HoardPad.Core.Checklist.Tests.HoardItemBuilder;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// QA4-3 fix 1 (H1 frozen oracle, pre-registered before the fix — mandatory per the High-Effort Rigor
/// Harness): <see cref="HoardHierarchy.TryIndent"/> must resolve the reparent target from the DISPLAY-
/// adjacent preceding sibling, not the raw-stored-order preceding sibling. Under
/// <see cref="CheckedItemDisplayMode.MoveToBottom"/>/<see cref="CheckedItemDisplayMode.HideWhenChecked"/>,
/// when a sibling between the target and the "obvious" stored-order predecessor is checked, the two orders
/// diverge and the OLD (buggy) behavior reparented under the wrong — possibly checked/hidden — sibling.
/// <para>Exhaustive table: {StrikethroughInPlace, MoveToBottom, HideWhenChecked} × {no checked sibling, a
/// checked sibling immediately BEFORE the target in stored order, a checked sibling AFTER the target in
/// stored order}. Plus two property tests binding the guardrails from the fix brief: an indent never crosses
/// a stored-<c>parentId</c> group boundary, and never pushes anything past the 2-tier depth cap.</para>
/// </summary>
public sealed class HoardModeAwareIndentTests
{
    private static readonly CheckedItemDisplayMode[] AllModes =
    [
        CheckedItemDisplayMode.StrikethroughInPlace,
        CheckedItemDisplayMode.MoveToBottom,
        CheckedItemDisplayMode.HideWhenChecked,
    ];

    // ---- Exhaustive table: {mode} x {no checked / checked-before-target / checked-after-target} --------

    // Scenario 1 — no checked sibling at all: P(0), A(1), B(2), C(3) [target]. Display == stored order in
    // every mode, so Indent(C) always reparents under B, the plain stored-order predecessor.
    [Theory]
    [InlineData(CheckedItemDisplayMode.StrikethroughInPlace)]
    [InlineData(CheckedItemDisplayMode.MoveToBottom)]
    [InlineData(CheckedItemDisplayMode.HideWhenChecked)]
    public void NoCheckedSiblingIndentReparentsUnderThePlainStoredOrderPredecessor(CheckedItemDisplayMode mode)
    {
        var items = new List<HoardItem>
        {
            Item("P", 0),
            Item("A", 1),
            Item("B", 2),
            Item("C", 3),
        };

        var result = HoardHierarchy.TryIndent("C", items, mode);

        Assert.NotNull(result);
        Assert.Equal("B", result!.Single(i => i.Id == "C").ParentId);
    }

    // Scenario 2 — the EXACT live-repro shape from the QA4-3 brief: group [P, A, B(checked), C]. Under
    // MoveToBottom the display is P, A, C, B — Indent(C) must nest C under A (the display-adjacent
    // predecessor), never under B (checked/sunk below C in the visible list).
    [Fact]
    public void CheckedSiblingImmediatelyBeforeTargetStrikethroughReparentsUnderTheCheckedSibling()
    {
        // Strikethrough keeps checked items in place — display order == stored order, so B (checked) is
        // still the item directly above C. This is the STRIKETHROUGH baseline the mode-aware fix must NOT
        // disturb.
        var items = new List<HoardItem>
        {
            Item("P", 0),
            Item("A", 1),
            Item("B", 2, @checked: true),
            Item("C", 3),
        };

        var result = HoardHierarchy.TryIndent("C", items, CheckedItemDisplayMode.StrikethroughInPlace);

        Assert.NotNull(result);
        Assert.Equal("B", result!.Single(i => i.Id == "C").ParentId);
    }

    [Fact]
    public void CheckedSiblingImmediatelyBeforeTargetMoveToBottomReparentsUnderTheDisplayAdjacentPredecessorNotTheCheckedOne()
    {
        // THE live-repro case: display is P, A, C, B (B sinks below C). Indent(C) must nest C under A.
        var items = new List<HoardItem>
        {
            Item("P", 0),
            Item("A", 1),
            Item("B", 2, @checked: true),
            Item("C", 3),
        };

        Assert.Equal("P,A,C,B", VisibleSeq(items, CheckedItemDisplayMode.MoveToBottom)); // sanity on the display walk itself.

        var result = HoardHierarchy.TryIndent("C", items, CheckedItemDisplayMode.MoveToBottom);

        Assert.NotNull(result);
        Assert.Equal("A", result!.Single(i => i.Id == "C").ParentId); // NOT "B" — the pre-fix bug's target.
    }

    [Fact]
    public void CheckedSiblingImmediatelyBeforeTargetHideWhenCheckedReparentsUnderTheDisplayAdjacentPredecessorNotTheHiddenOne()
    {
        // B is hidden entirely; display is P, A, C. Indent(C) must nest C under A.
        var items = new List<HoardItem>
        {
            Item("P", 0),
            Item("A", 1),
            Item("B", 2, @checked: true),
            Item("C", 3),
        };

        Assert.Equal("P,A,C", VisibleSeq(items, CheckedItemDisplayMode.HideWhenChecked)); // sanity on the display walk itself.

        var result = HoardHierarchy.TryIndent("C", items, CheckedItemDisplayMode.HideWhenChecked);

        Assert.NotNull(result);
        Assert.Equal("A", result!.Single(i => i.Id == "C").ParentId); // NOT "B" — the pre-fix bug's target.
    }

    // Scenario 3 — a checked sibling AFTER the target (regression guard): checking a LATER sibling must
    // never perturb the target's own display-adjacent predecessor in any mode. Group: P, A, C(target),
    // D(checked, stored AFTER C).
    [Theory]
    [InlineData(CheckedItemDisplayMode.StrikethroughInPlace)]
    [InlineData(CheckedItemDisplayMode.MoveToBottom)]
    [InlineData(CheckedItemDisplayMode.HideWhenChecked)]
    public void CheckedSiblingAfterTargetNeverPerturbsTheTargetsDisplayAdjacentPredecessor(CheckedItemDisplayMode mode)
    {
        var items = new List<HoardItem>
        {
            Item("P", 0),
            Item("A", 1),
            Item("C", 2),
            Item("D", 3, @checked: true),
        };

        var result = HoardHierarchy.TryIndent("C", items, mode);

        Assert.NotNull(result);
        Assert.Equal("A", result!.Single(i => i.Id == "C").ParentId); // D being checked-after has zero effect.
    }

    // ---- Property test 1: an indent NEVER crosses a stored-parentId group boundary --------------------

    [Theory]
    [MemberData(nameof(GroupBoundaryTrees))]
    public void IndentNeverCrossesAStoredParentIdGroupBoundary(
        List<HoardItem> items, string targetId, CheckedItemDisplayMode mode)
    {
        var before = items.Single(i => i.Id == targetId);
        var result = HoardHierarchy.TryIndent(targetId, items, mode);

        if (result is null)
            return; // a legitimate no-op (top-of-group / depth-cap) proves nothing here.

        var movedItem = result.Single(i => i.Id == targetId);
        var newParent = items.SingleOrDefault(i => i.Id == movedItem.ParentId); // look up in the ORIGINAL list.

        Assert.NotNull(newParent); // the new parent must have existed as a real item pre-move.
        Assert.Equal(before.ParentId, newParent!.ParentId); // the new parent was ALWAYS a sibling of the target
                                                             // in the target's OWN stored parentId group — never
                                                             // a row from a different group, depth, or branch.
    }

    public static IEnumerable<object[]> GroupBoundaryTrees()
    {
        foreach (var mode in AllModes)
        {
            // Flat tier-0 group with a mix of checked/unchecked/collapsed siblings.
            yield return new object[]
            {
                new List<HoardItem>
                {
                    Item("p0", 0),
                    Item("p1", 1, @checked: true),
                    Item("p2", 2),
                    Item("p3", 3, @checked: true),
                    Item("p4", 4), // target
                },
                "p4",
                mode,
            };

            // Nested tree — a tier-1 group with its own checked members, sitting under an unrelated tier-0
            // sibling that must never be selected as the reparent target.
            yield return new object[]
            {
                new List<HoardItem>
                {
                    Item("root", 0),
                    Item("other", 1), // a DIFFERENT tier-0 branch — must never be chosen.
                    Item("c1", 0, parentId: "root", @checked: true),
                    Item("c2", 1, parentId: "root"),
                    Item("c3", 2, parentId: "root"), // target — its group is {c1, c2, c3} only.
                },
                "c3",
                mode,
            };
        }
    }

    // ---- Property test 2: an indent never pushes anything past the 2-tier depth cap ------------------

    [Theory]
    [MemberData(nameof(DepthCapTrees))]
    public void IndentNeverExceedsTheDepthCapForAnyMode(List<HoardItem> items, string targetId, CheckedItemDisplayMode mode)
    {
        var result = HoardHierarchy.TryIndent(targetId, items, mode);
        if (result is null)
            return; // no-op — nothing to check.

        foreach (var item in result)
            Assert.True(HoardHierarchy.Depth(item, result) <= HoardSchema.MaxDepth,
                $"{item.Id} exceeded MaxDepth ({HoardSchema.MaxDepth}) after indenting {targetId} under mode {mode}.");
    }

    public static IEnumerable<object[]> DepthCapTrees()
    {
        foreach (var mode in AllModes)
        {
            // At the cap already: t0 -> t1 -> t2, with a checked t1-sibling before a second t2 chain, so the
            // mode-aware predecessor differs from the stored-order one but the cap must still bind.
            yield return new object[]
            {
                new List<HoardItem>
                {
                    Item("t0", 0),
                    Item("t1a", 0, parentId: "t0", @checked: true),
                    Item("t1b", 1, parentId: "t0"),
                    Item("t2", 0, parentId: "t1b"),
                    Item("t2sib", 1, parentId: "t1b"), // target: indenting under t2 would push it to tier 3.
                },
                "t2sib",
                mode,
            };

            // Held-key repeat at the cap must stay a no-op under every mode too.
            yield return new object[]
            {
                new List<HoardItem>
                {
                    Item("t0", 0),
                    Item("t1", 0, parentId: "t0"),
                    Item("t2", 0, parentId: "t1", @checked: true),
                    Item("t2b", 1, parentId: "t1"),
                },
                "t2b",
                mode,
            };
        }
    }
}
