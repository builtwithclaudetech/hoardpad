namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// Terse construction helpers for the pure-function tests (display-mode mapping, effectiveEnabled, depth).
/// These build small in-memory <see cref="HoardItem"/> forests without touching JSON — the functions under
/// test are pure over the model, so no serialization round-trip is needed.
/// </summary>
internal static class HoardItemBuilder
{
    /// <summary>One item. <paramref name="parentId"/> <c>null</c> = tier-0.</summary>
    public static HoardItem Item(
        string id,
        int order,
        string? parentId = null,
        bool @checked = false,
        bool collapsed = false) =>
        new()
        {
            Id = id,
            Text = id,
            Checked = @checked,
            ParentId = parentId,
            Order = order,
            Collapsed = collapsed,
        };

    /// <summary>The visible-row ids under a mode (the sequence the display-index arithmetic runs over).</summary>
    public static IReadOnlyList<string> VisibleIds(
        IReadOnlyList<HoardItem> items,
        CheckedItemDisplayMode mode) =>
        HoardDisplay.VisibleRows(items, mode).Select(i => i.Id).ToList();

    /// <summary>The ids of one sibling group, in renormalized stored order (order asc).</summary>
    public static IReadOnlyList<string> StoredOrderIds(string? parentId, IReadOnlyList<HoardItem> items) =>
        HoardHierarchy.ChildrenOf(parentId, items).Select(i => i.Id).ToList();

    /// <summary>Join an id sequence into one comparable string (avoids inline-array assertions; gives a
    /// legible diff on failure — the whole visible sequence at a glance).</summary>
    public static string Seq(IEnumerable<string> ids) => string.Join(",", ids);

    /// <summary>The visible-row ids under a mode, joined to a single string for sequence assertions.</summary>
    public static string VisibleSeq(IReadOnlyList<HoardItem> items, CheckedItemDisplayMode mode) =>
        Seq(VisibleIds(items, mode));
}
