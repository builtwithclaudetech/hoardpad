namespace HoardPad.Core.Checklist;

/// <summary>
/// PURE derived-structure helpers over the flat <see cref="HoardItem"/> model (§9.1 "Derived invariants",
/// architecture §5.1, TD §5.7). None of these are persisted — all are computed from the flat <c>items[]</c>
/// + <c>parentId</c>. No I/O, no clock, no platform dependency (C-9). The checklist control reads these; it
/// embeds no gating, partition, or depth-cap logic.
/// </summary>
public static class HoardHierarchy
{
    /// <summary>Build the <c>id → item</c> lookup used by every chain walk. Items with an empty <c>id</c>
    /// are excluded (they can never be a parent target — the same rule <see cref="HoardLoader"/> applies);
    /// duplicate ids keep the first occurrence.</summary>
    private static Dictionary<string, HoardItem> IndexById(IReadOnlyList<HoardItem> items)
    {
        var byId = new Dictionary<string, HoardItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Id.Length > 0)
                byId.TryAdd(item.Id, item);
        }

        return byId;
    }

    /// <summary>The strict-ancestor chain from <paramref name="item"/>'s parent up to the root, nearest
    /// ancestor first. Assumes a valid forest (the loader has already repaired cycles); a defensive
    /// visited-set still bounds the walk so a hand-constructed cyclic list cannot loop forever.
    /// <para>The cycle-guard keys on object reference identity (<see cref="ReferenceEqualityComparer"/>),
    /// not on <c>Id</c> strings — an empty-id item's string is not a stable per-node key and its
    /// <c>parentId</c> collides with the parent's real <c>Id</c> on the very next iteration, terminating
    /// the walk one hop early.</para></summary>
    private static IEnumerable<HoardItem> StrictAncestors(HoardItem item, Dictionary<string, HoardItem> byId)
    {
        var seen = new HashSet<HoardItem>(ReferenceEqualityComparer.Instance);
        var current = item;
        while (current.ParentId is { } parentId
               && byId.TryGetValue(parentId, out var parent)
               && seen.Add(current))
        {
            yield return parent;
            current = parent;
        }
    }

    /// <summary><c>depth(item)</c> = strict-ancestor count (§9.1). A tier-0 item is depth 0; the cap is
    /// <see cref="HoardSchema.MaxDepth"/> (= 2, three tiers).</summary>
    public static int Depth(HoardItem item, IReadOnlyList<HoardItem> items) =>
        Depth(item, IndexById(items));

    private static int Depth(HoardItem item, Dictionary<string, HoardItem> byId) =>
        StrictAncestors(item, byId).Count();

    /// <summary><c>isParent(item)</c> = has ≥1 child in <paramref name="items"/> (§9.1).</summary>
    public static bool IsParent(HoardItem item, IReadOnlyList<HoardItem> items)
    {
        if (item.Id.Length == 0)
            return false;

        foreach (var candidate in items)
        {
            if (candidate.ParentId == item.Id)
                return true;
        }

        return false;
    }

    /// <summary><c>isLeaf(item)</c> = 0 children (§9.1) — the complement of <see cref="IsParent"/>.</summary>
    public static bool IsLeaf(HoardItem item, IReadOnlyList<HoardItem> items) => !IsParent(item, items);

    /// <summary>
    /// <c>effectiveEnabled(item)</c> = <b>every strict ancestor of <paramref name="item"/> has
    /// <c>checked == false</c></b> (§9.1 / FR-41 / architecture §6.5 / TD §5.7). A tier-0 item, having no
    /// ancestors, is always enabled. Checking ANY ancestor locks the node (the FR-41 inert/greyed state).
    /// <b>The item itself stays toggleable regardless of its own <c>checked</c> state</b> — its own
    /// <c>checked</c> is never consulted — so a completed parent can always be unchecked to reopen its block.
    /// A pure ancestor-chain function; it never mutates state and is the sole driver of the lock.
    /// </summary>
    public static bool EffectiveEnabled(HoardItem item, IReadOnlyList<HoardItem> items) =>
        EffectiveEnabled(item, IndexById(items));

    private static bool EffectiveEnabled(HoardItem item, Dictionary<string, HoardItem> byId) =>
        StrictAncestors(item, byId).All(ancestor => !ancestor.Checked);

    /// <summary>The direct children of <paramref name="parentId"/> (<c>null</c> = the tier-0 group),
    /// ordered by stored <c>order</c> then original array index (a stable tie-break). This is one sibling
    /// group.</summary>
    public static IReadOnlyList<HoardItem> ChildrenOf(string? parentId, IReadOnlyList<HoardItem> items) =>
        items
            .Select((item, index) => (item, index))
            .Where(t => t.item.ParentId == parentId)
            .OrderBy(t => t.item.Order)
            .ThenBy(t => t.index)
            .Select(t => t.item)
            .ToList();

    /// <summary>
    /// Renormalize EVERY sibling group to contiguous <c>order</c> <c>0..n-1</c>, preserving each group's
    /// relative order by current <c>order</c> then original index (§9.1 "sibling-group renormalize"). The
    /// flat array's own element order is left unchanged; only <c>order</c> values are rewritten. Returns a
    /// new list (the model is immutable); items already canonical are returned by reference.
    /// </summary>
    public static IReadOnlyList<HoardItem> RenormalizeSiblingOrders(IReadOnlyList<HoardItem> items)
    {
        var newOrderByIndex = new int[items.Count];
        var groups = items
            .Select((item, index) => (item, index))
            .GroupBy(t => t.item.ParentId);

        foreach (var group in groups)
        {
            var ranked = group
                .OrderBy(t => t.item.Order)
                .ThenBy(t => t.index)
                .Select((t, rank) => (t.index, rank));

            foreach (var (index, rank) in ranked)
                newOrderByIndex[index] = rank;
        }

        return items
            .Select((item, index) => newOrderByIndex[index] != item.Order
                ? item with { Order = newOrderByIndex[index] }
                : item)
            .ToList();
    }

    /// <summary>
    /// Attempt an interactive INDENT of <paramref name="itemId"/> under its DISPLAY-adjacent preceding
    /// sibling (the standard outline indent: an item becomes a child of the sibling the user sees directly
    /// above it — QA4-3 fix 1). Carries the whole subtree (D9). <b>Depth-cap rule (§9.1 / architecture §5.1,
    /// C-4 binding):</b> if the indent would push <paramref name="itemId"/> OR any of its descendants past
    /// <see cref="HoardSchema.MaxDepth"/>, this is a <b>no-op</b> — it returns <c>null</c> (no mutation, no
    /// error). The caller enqueues a write ONLY on a non-<c>null</c> result, so an over-cap indent (incl.
    /// held-key auto-repeat) enqueues ZERO writes and triggers ZERO <c>fsync</c>. On success it returns the
    /// new item list with <paramref name="itemId"/> reparented and both affected sibling groups renormalized.
    /// <para>The reparent target is resolved from <see cref="HoardDisplay.DisplayOrderedGroup"/> — the same
    /// per-group partition/hide logic the visible-row walk uses for <paramref name="mode"/> — NOT from raw
    /// stored order. Under <c>move-to-bottom</c>/<c>hide-when-checked</c>, a checked sibling that merely
    /// precedes <paramref name="itemId"/> in STORED order is never picked; the target is always the sibling
    /// displayed immediately above it. Scoped to <c>ChildrenOf(item.ParentId, items)</c> — this can never
    /// select a row from a different <c>parentId</c>, a different depth, or across a collapsed-parent
    /// boundary; the candidate set IS <paramref name="itemId"/>'s own stored sibling group, nothing else.</para>
    /// <para>Returns <c>null</c> (no-op) when the item is absent, already at the top of its group's display
    /// order (no display-adjacent preceding sibling to indent under), or the move would breach the depth cap.</para>
    /// </summary>
    public static IReadOnlyList<HoardItem>? TryIndent(
        string itemId,
        IReadOnlyList<HoardItem> items,
        CheckedItemDisplayMode mode)
    {
        var byId = IndexById(items);
        if (!byId.TryGetValue(itemId, out var item))
            return null;

        // The DISPLAY-adjacent preceding sibling (same parentId group, mode-aware order) becomes the new
        // parent — QA4-3 fix 1. This is the exact per-group ordering HoardDisplay.VisibleRows renders.
        var displaySiblings = HoardDisplay.DisplayOrderedGroup(item.ParentId, items, mode);
        var positionInGroup = -1;
        for (var i = 0; i < displaySiblings.Count; i++)
        {
            if (displaySiblings[i].Id == itemId)
            {
                positionInGroup = i;
                break;
            }
        }

        if (positionInGroup <= 0)
            return null; // no display-adjacent preceding sibling — cannot indent.

        var newParent = displaySiblings[positionInGroup - 1];

        // Depth-cap check: the moved item's new depth is depth(newParent) + 1; the deepest descendant
        // shifts by the same delta. If any descendant would exceed MaxDepth, the whole move is a no-op.
        var newParentDepth = Depth(newParent, byId);
        var currentItemDepth = Depth(item, byId);
        var delta = (newParentDepth + 1) - currentItemDepth; // always +1 for a standard indent, but compute it.
        var deepestInSubtree = MaxDepthInSubtree(itemId, items, byId);
        if (deepestInSubtree + delta > HoardSchema.MaxDepth)
            return null; // over-cap — ZERO writes.

        // Reparent the item (its descendants follow automatically — they reference it by id, D9). The
        // new sibling group (newParent's children) and the old sibling group both renormalize.
        var moved = items
            .Select(i => i.Id == itemId ? i with { ParentId = newParent.Id } : i)
            .ToList();

        return RenormalizeSiblingOrders(moved);
    }

    /// <summary>
    /// Attempt an interactive OUTDENT (promote) of <paramref name="itemId"/> one tier: it becomes a
    /// sibling of its current parent (i.e. a child of its grandparent — <c>null</c> grandparent = tier 0),
    /// inserted into the grandparent group <b>immediately after the old parent</b> so the outline reads
    /// naturally (the standard outline promote). Carries the whole subtree (D9) — its descendants follow by
    /// reference. Outdent strictly REDUCES depth, so the depth cap can never be breached; no cap check is
    /// needed. The new <c>order</c> is computed so the promoted item sits just after its old parent, then
    /// both affected sibling groups renormalize to contiguous <c>0..n-1</c>.
    /// <para>Returns <c>null</c> (no-op) when the item is absent or already at tier 0 (no parent to promote
    /// out of) — the caller enqueues a write ONLY on a non-<c>null</c> result (C-4 zero-write no-op).</para>
    /// <para>Outdent's REPARENT TARGET is always the unambiguous grandparent (<c>item.ParentId.ParentId</c>)
    /// — never chosen from a sibling group the way Indent's preceding-sibling is — so <paramref name="mode"/>
    /// never affects WHICH item it reparents under. It DOES affect the resulting DISPLAY PLACEMENT
    /// (F-QA4-3a): under <see cref="CheckedItemDisplayMode.MoveToBottom"/>, checked/unchecked siblings
    /// render as two separate blocks (unchecked block, then checked block — <see
    /// cref="HoardDisplay.DisplayOrderedGroup"/>) regardless of raw stored order. When the promoted item's
    /// own <c>checked</c> state differs from its old parent's (in EITHER direction — an unchecked item out of
    /// a checked parent, OR a checked item out of an unchecked parent), anchoring its
    /// raw order "immediately after the old parent" can drop it into the WRONG block's raw-order
    /// neighborhood — e.g. an unchecked promoted item scattered mid-way through the unchecked block instead
    /// of landing at the boundary the old parent's checked block begins. See the cross-partition branch
    /// below for the fix; <see cref="CheckedItemDisplayMode.StrikethroughInPlace"/> (display == stored
    /// order) and <see cref="CheckedItemDisplayMode.HideWhenChecked"/> (raw order, checked rows simply
    /// filtered out — no reordering) are unaffected, so they keep the plain raw-order-adjacent placement.</para>
    /// </summary>
    public static IReadOnlyList<HoardItem>? TryOutdent(
        string itemId,
        IReadOnlyList<HoardItem> items,
        CheckedItemDisplayMode mode)
    {
        var byId = IndexById(items);
        if (!byId.TryGetValue(itemId, out var item))
            return null;

        // Tier-0 items have no parent to promote out of — outdent is a no-op (ZERO writes).
        if (item.ParentId is not { } oldParentId || !byId.TryGetValue(oldParentId, out var oldParent))
            return null;

        var grandParentId = oldParent.ParentId; // null => the promoted item lands at tier 0.

        // F-QA4-3a: move-to-bottom cross-partition case, fired by the SYMMETRIC guard below — the promoted
        // item's checked state differs from the old parent's in EITHER direction (unchecked item out of a
        // checked parent, or checked item out of an unchecked parent). Only move-to-bottom reorders siblings
        // into checked/unchecked blocks, and only a cross-partition promotion diverges from raw-order
        // placement (strikethrough/hide-when-checked never reorder), so this is the sole case needing a fix.
        // Snap the promoted item to the partition
        // BOUNDARY nearest the old parent instead of the old parent's raw slot: an unchecked promotion
        // becomes the LAST member of the unchecked block (right before the checked block starts); a checked
        // promotion becomes the FIRST member of the checked block. Global min-1 / max+1 (over the WHOLE
        // grandparent group, not just the target partition) guarantees the new order is strictly outside
        // every existing sibling's order — no renormalize (Order, index) tie-break ambiguity.
        if (mode == CheckedItemDisplayMode.MoveToBottom && item.Checked != oldParent.Checked)
        {
            var grandSiblings = ChildrenOf(grandParentId, items); // grandparent's existing children
                                                                  // (always includes oldParent); itemId is
                                                                  // not yet reparented here.
            var boundaryOrder = item.Checked
                ? grandSiblings.Min(i => i.Order) - 1  // first-in-checked-block.
                : grandSiblings.Max(i => i.Order) + 1; // last-in-unchecked-block.

            var reparented = items
                .Select(i => i.Id == itemId ? i with { ParentId = grandParentId, Order = boundaryOrder } : i)
                .ToList();

            return RenormalizeSiblingOrders(reparented);
        }

        // Place the promoted item immediately after its old parent within the grandparent group, by giving
        // it a fractional rank (oldParent.Order + 0.5 expressed as oldParent.Order, then renormalize breaks
        // the tie by original array index — but to guarantee it sorts AFTER the old parent, nudge to +1 and
        // shift the trailing grandparent-group members up first is unnecessary: renormalize re-ranks by
        // (Order asc, index). Assigning oldParent.Order + 1 and leaving the rest puts it on a par with the
        // member after the old parent; the stable index tie-break then orders deterministically. To make the
        // intent unambiguous we set Order to oldParent.Order then rely on the post-pass below.
        var moved = items
            .Select(i => i.Id == itemId
                ? i with { ParentId = grandParentId, Order = oldParent.Order }
                : i)
            .ToList();

        // After reparenting, the promoted item and the old parent share Order in the grandparent group. We
        // want the promoted item AFTER the old parent: set the promoted item to oldParent.Order + 1, and
        // bump every other grandparent-group member that is already at or above that slot by +2 (so they
        // all sort strictly after the promoted item), then let RenormalizeSiblingOrders compact the group
        // back to 0..n-1 with the natural "just after parent" ordering.
        var placed = moved
            .Select(i =>
            {
                if (i.Id == itemId)
                    return i with { Order = oldParent.Order + 1 };
                if (i.ParentId == grandParentId && i.Id != oldParentId && i.Order > oldParent.Order)
                    return i with { Order = i.Order + 2 };
                return i;
            })
            .ToList();

        return RenormalizeSiblingOrders(placed);
    }

    /// <summary>The maximum depth among <paramref name="rootId"/> and all its descendants.</summary>
    private static int MaxDepthInSubtree(string rootId, IReadOnlyList<HoardItem> items, Dictionary<string, HoardItem> byId)
    {
        var max = 0;
        foreach (var candidate in items)
        {
            if (IsInSubtree(candidate, rootId, byId))
                max = Math.Max(max, Depth(candidate, byId));
        }

        return max;
    }

    /// <summary>True if <paramref name="candidate"/> is <paramref name="rootId"/> itself or a descendant of
    /// it (walking <c>parentId</c> upward).</summary>
    private static bool IsInSubtree(HoardItem candidate, string rootId, Dictionary<string, HoardItem> byId)
    {
        if (candidate.Id == rootId)
            return true;

        foreach (var ancestor in StrictAncestors(candidate, byId))
        {
            if (ancestor.Id == rootId)
                return true;
        }

        return false;
    }
}
