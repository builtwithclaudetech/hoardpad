namespace HoardPad.Core.Checklist;

/// <summary>The three per-list checked-item display modes (§9.1 <c>settings.checkedItemMode</c>, FR-33).
/// Parsed from the stored string via <see cref="HoardDisplayModeExtensions.ParseCheckedItemMode"/>; an
/// unknown/foreign string falls back to the default <see cref="StrikethroughInPlace"/> rather than throwing
/// (the stored value round-trips losslessly as a string in <see cref="HoardSettings.CheckedItemMode"/>).</summary>
public enum CheckedItemDisplayMode
{
    /// <summary>Checked item stays in its stored position, shown struck through (the default).</summary>
    StrikethroughInPlace,

    /// <summary>Checked items are collected at the bottom of their sibling group, in stored order.</summary>
    MoveToBottom,

    /// <summary>Checked items are hidden from view but retained in the file.</summary>
    HideWhenChecked,
}

/// <summary>Parse the stored <c>settings.checkedItemMode</c> string into the typed mode.</summary>
public static class HoardDisplayModeExtensions
{
    /// <summary>Map the stored string to the typed mode; any unknown value falls back to the default
    /// <see cref="CheckedItemDisplayMode.StrikethroughInPlace"/> (the string round-trips losslessly on disk —
    /// this typed view is a UI concern, §9.1).</summary>
    public static CheckedItemDisplayMode ParseCheckedItemMode(string mode) => mode switch
    {
        "move-to-bottom" => CheckedItemDisplayMode.MoveToBottom,
        "hide-when-checked" => CheckedItemDisplayMode.HideWhenChecked,
        _ => CheckedItemDisplayMode.StrikethroughInPlace,
    };
}

/// <summary>
/// PURE display logic for a <c>.hoard</c> checklist (§9.1 "Hierarchy-aware reorder-vs-display-mode
/// semantics", architecture §6.5, TD §5.7). No I/O, no platform dependency (C-9). Two responsibilities:
/// <list type="number">
/// <item><b>The visible-row walk</b> — the pre-order sequence of rows actually shown, honoring the active
/// checked-item display mode AND the collapsed-parent rule (Gap B). A completed (checked) parent's whole
/// subtree moves/hides as one contiguous unit; a collapsed parent's descendants are absent regardless of its
/// <c>checked</c> state, while the collapsed parent's own row stays present.</item>
/// <item><b>The display-index → stored-<c>order</c> mapping</b> — given a drop at a visible gap within a
/// sibling group, the resulting stored <c>order</c> per mode (§9.1 per-mode arithmetic, including the
/// reverse-ordered-neighbors and partition-edge cases).</item>
/// </list>
/// The checklist control calls these functions; it embeds no partition arithmetic.
/// </summary>
public static class HoardDisplay
{
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

    /// <summary>
    /// The visible-row sequence (pre-order walk) under <paramref name="mode"/>, honoring collapse. This is
    /// the single sequence the display-index and drop-gap arithmetic run over (§9.1 Gap B). Walk rule per
    /// sibling group, recursing into each visible row's children:
    /// <list type="bullet">
    /// <item><b>strikethrough-in-place:</b> group order == stored order; checked items stay in place.</item>
    /// <item><b>move-to-bottom:</b> the group is <c>[unchecked in stored order] ++ [checked in stored
    /// order]</c>; a checked PARENT's whole subtree travels as one contiguous unit within the partition.</item>
    /// <item><b>hide-when-checked:</b> checked items (and a checked parent's whole subtree) are absent.</item>
    /// </list>
    /// In all modes, a <c>collapsed</c> parent's descendants are absent regardless of its <c>checked</c>
    /// state, while the collapsed parent's own row remains present (Gap B). A checked parent under
    /// move-to-bottom/hide-when-checked is treated by its CHECKED state (block moves/hides); collapse only
    /// suppresses the descendant rows under a parent whose own row is still shown.
    /// </summary>
    public static IReadOnlyList<HoardItem> VisibleRows(
        IReadOnlyList<HoardItem> items,
        CheckedItemDisplayMode mode)
    {
        var rows = new List<HoardItem>(items.Count);
        AppendGroup(parentId: null, items, mode, rows);
        return rows;
    }

    /// <summary>
    /// ONE sibling group (the direct children of <paramref name="parentId"/>), in the order the user
    /// actually SEES them under <paramref name="mode"/> — the same per-group partition/hide rule
    /// <see cref="AppendGroup"/> applies for the full visible-row walk, but scoped to this one group (no
    /// recursion into children). This is the shared building block between the visible-row walk and any
    /// consumer that needs "this group in display order" without walking the whole tree — e.g.
    /// <see cref="HoardHierarchy.TryIndent"/>'s reparent-target resolution (QA4-3): the target must be the
    /// DISPLAY-adjacent preceding sibling, never one that merely precedes it in STORED order.
    /// </summary>
    public static IReadOnlyList<HoardItem> DisplayOrderedGroup(
        string? parentId,
        IReadOnlyList<HoardItem> items,
        CheckedItemDisplayMode mode)
    {
        var group = HoardHierarchy.ChildrenOf(parentId, items);

        var ordered = mode switch
        {
            // move-to-bottom partitions THIS group: unchecked first (stored order), then checked (stored
            // order). Each member that is a checked parent still recurses as a contiguous block below.
            CheckedItemDisplayMode.MoveToBottom =>
                group.Where(i => !i.Checked).Concat(group.Where(i => i.Checked)).ToList(),
            _ => group, // strikethrough + hide keep stored order; hide drops checked members below.
        };

        // hide-when-checked: a checked item (leaf OR parent block) is absent from the visible rows.
        return mode == CheckedItemDisplayMode.HideWhenChecked
            ? ordered.Where(i => !i.Checked).ToList()
            : ordered;
    }

    private static void AppendGroup(
        string? parentId,
        IReadOnlyList<HoardItem> items,
        CheckedItemDisplayMode mode,
        List<HoardItem> rows)
    {
        foreach (var item in DisplayOrderedGroup(parentId, items, mode))
        {
            rows.Add(item);

            // Collapsed parent: descendants are absent from the visible-row walk regardless of checked
            // state (Gap B) — but the parent's own row above stays present.
            if (item.Collapsed)
                continue;

            // Recurse into children. The contiguous-subtree guarantee falls out of pre-order recursion:
            // a checked parent that survives to here (strikethrough/move-to-bottom) emits its whole
            // subtree immediately after its own row, as one block, before the next sibling.
            AppendGroup(item.Id, items, mode, rows);
        }
    }

    /// <summary>
    /// The display-index → stored-<c>order</c> mapping for a drag-drop WITHIN a single sibling group under
    /// <paramref name="mode"/> (§9.1, binding). The drop is expressed as a <paramref name="displayGap"/> —
    /// the number of VISIBLE rows of this sibling group above the drop line (0 = above the first visible
    /// member, group.VisibleCount = below the last) — for the dragged item identified by
    /// <paramref name="draggedId"/>. Returns the target stored <c>order</c> the dragged item should take
    /// within its group; the caller assigns it and then renormalizes the whole group to <c>0..n-1</c>
    /// (so the integer returned here is a fractional-rank anchor, not the final contiguous value).
    /// <para>The dragged item's <c>checked</c> state is NEVER changed by a drop — a partition-boundary drop
    /// resolves by the item's own <c>checked</c> state, not the pixel (§9.1 partition-boundary rule).</para>
    /// <para>Pure; operates on the sibling group derived from <paramref name="items"/> + the dragged item's
    /// <c>parentId</c>. The returned value is a stored-order anchor to be fed into a re-rank + renormalize.</para>
    /// </summary>
    public static int MapDropToStoredOrder(
        string draggedId,
        int displayGap,
        IReadOnlyList<HoardItem> items,
        CheckedItemDisplayMode mode)
    {
        var byId = IndexById(items);
        if (!byId.TryGetValue(draggedId, out var dragged))
            return 0;

        // The sibling group, in stored order (order asc, stable). This is the partition the drop lands in.
        var group = HoardHierarchy.ChildrenOf(dragged.ParentId, items);

        return mode switch
        {
            CheckedItemDisplayMode.StrikethroughInPlace =>
                // Display order == stored order: a drop at display gap g maps directly to stored order = g.
                MapStrikethrough(group, displayGap),
            CheckedItemDisplayMode.HideWhenChecked =>
                MapHideWhenChecked(group, displayGap),
            CheckedItemDisplayMode.MoveToBottom =>
                MapMoveToBottom(group, dragged, displayGap),
            _ => displayGap,
        };
    }

    /// <summary>strikethrough-in-place: every member is a visible row in stored order, so the display gap is
    /// the stored gap directly (§9.1). The anchor stored order is the gap index itself.</summary>
    private static int MapStrikethrough(IReadOnlyList<HoardItem> group, int displayGap)
    {
        var clamped = Math.Clamp(displayGap, 0, group.Count);
        // Anchor between the member at clamped-1 and clamped (stored order). Returning the lower neighbor's
        // stored order + a +0/+1 nudge then renormalize is equivalent to "stored order = g" here because
        // strikethrough's visible order IS stored order.
        return clamped == group.Count
            ? (group.Count == 0 ? 0 : group[^1].Order + 1)
            : group[clamped].Order;
    }

    /// <summary>hide-when-checked: hidden (checked) members are absent from the display but retain their
    /// stored order. A drop at a visible gap maps to the stored order of the visible item AFTER the gap (or
    /// end-of-group); hidden items keep their positions and renormalize alongside (§9.1).</summary>
    private static int MapHideWhenChecked(IReadOnlyList<HoardItem> group, int displayGap)
    {
        var visible = group.Where(i => !i.Checked).ToList();
        var clamped = Math.Clamp(displayGap, 0, visible.Count);

        if (visible.Count == 0)
            return group.Count == 0 ? 0 : group[^1].Order + 1;

        // Stored order of the visible item AFTER the gap; if the gap is past the last visible item, anchor
        // after the highest stored order in the whole group (hidden items included) so the dropped item
        // renormalizes to the tail.
        return clamped == visible.Count
            ? group.Max(i => i.Order) + 1
            : visible[clamped].Order;
    }

    /// <summary>
    /// move-to-bottom: the display is <c>[unchecked stored-order] ++ [checked stored-order]</c>. A drop
    /// resolves WITHIN the partition (unchecked vs checked) chosen by the DRAGGED item's <c>checked</c>
    /// state — never the pixel — so a boundary drop never silently flips <c>checked</c> (§9.1). The anchor
    /// is the upper visible neighbor's stored order + 1 (upper-neighbor-wins, the reverse-ordered rule);
    /// a no-upper-neighbor drop anchors immediately before the lower neighbor (§9.1 partition-edge cases).
    /// </summary>
    private static int MapMoveToBottom(IReadOnlyList<HoardItem> group, HoardItem dragged, int displayGap)
    {
        var unchecked_ = group.Where(i => !i.Checked).ToList();
        var checked_ = group.Where(i => i.Checked).ToList();

        // The display sequence the user dropped into: unchecked partition then checked partition.
        var display = unchecked_.Concat(checked_).ToList();
        var gap = Math.Clamp(displayGap, 0, display.Count);

        // The partition this dragged item belongs to is decided by its OWN checked state (§9.1
        // partition-boundary rule): an unchecked item joins the unchecked partition, a checked item joins
        // the checked partition — regardless of where the boundary pixel fell.
        var partition = dragged.Checked ? checked_ : unchecked_;

        // Resolve the upper/lower visible neighbours from the full display sequence, then constrain them to
        // the dragged item's partition. The upper neighbour anchors the target (upper-neighbour-wins).
        var upper = gap > 0 ? display[gap - 1] : null;
        var lower = gap < display.Count ? display[gap] : null;

        // Constrain the anchor to the dragged item's partition: a drop at the boundary resolves to the END
        // of the unchecked partition (if dragged is unchecked) or the START of the checked partition (if
        // dragged is checked) — never the other partition.
        if (partition.Count == 0)
            // Empty target partition: anchor after the highest stored order in the group (the new tail).
            return group.Count == 0 ? 0 : group.Max(i => i.Order) + 1;

        // Upper-neighbour-wins: if the upper visible neighbour is in the dragged item's partition, the
        // target stored order is immediately AFTER it (the reverse-ordered-neighbours case resolves the
        // same way — the upper neighbour is the deterministic anchor, §9.1).
        if (upper is not null && partition.Any(p => p.Id == upper.Id))
            return upper.Order + 1;

        // No-upper-neighbour within the partition (drop at the very top of the partition): anchor
        // immediately BEFORE the lower neighbour if it is in the partition; else the partition's first
        // member. (§9.1 no-upper-neighbour case — the lower neighbour is the anchor.)
        if (lower is not null && partition.Any(p => p.Id == lower.Id))
            return lower.Order; // re-rank places the dragged item just before the lower neighbour.

        // Neither neighbour is in the partition (e.g. dragged is unchecked but the gap sits in the checked
        // region): clamp to the dragged item's partition — top if dropping above it, bottom otherwise.
        var partitionTopStored = partition.Min(i => i.Order);
        var partitionBottomStored = partition.Max(i => i.Order);
        // A drop above the partition anchors at its top; a drop below anchors after its bottom.
        return gap <= unchecked_.Count && !dragged.Checked
            ? partitionTopStored
            : partitionBottomStored + 1;
    }
}
