using System.Text.Json;

namespace HoardPad.Core.Checklist;

/// <summary>
/// PURE load pipeline for a <c>.hoard</c> file (§9.1 / architecture §5.1). No I/O — the caller supplies the
/// JSON string. It applies, in order:
/// <list type="number">
/// <item><b>Version gate.</b> <c>schemaVersion &gt; 2</c> is REJECTED (never silently downgraded).</item>
/// <item><b>v1→v2 migration.</b> A <c>schemaVersion: 1</c> file (no <c>parentId</c>/<c>collapsed</c>) loads
/// with every item at tier 0 (<c>parentId=null</c>, <c>collapsed=false</c> — the model's default-on-absence)
/// and is stamped <c>schemaVersion: 2</c>; the first re-save emits v2.</item>
/// <item><b>Dangling/cyclic parent repair.</b> A <c>parentId</c> that references no existing item, or that
/// forms a cycle, is repaired to <c>null</c> (the item promotes to tier 0) — the file is never rejected.</item>
/// <item><b>Over-deep clamp.</b> An item whose derived depth exceeds tier 2 is reparented to its deepest
/// legal ancestor (the worked-example rule, applied bottom-up).</item>
/// <item><b>Sibling-group renormalize.</b> Each sibling group's <c>order</c> is renormalized to contiguous
/// <c>0..n-1</c>, preserving relative order.</item>
/// </list>
/// Every repair is recorded in <see cref="HoardLoadResult.Repairs"/> for the host to log (the loader writes
/// no log). Malformed JSON throws <see cref="JsonException"/> (a genuinely exceptional input).
/// </summary>
public static class HoardLoader
{
    /// <summary>Load + migrate + repair + clamp + renormalize a <c>.hoard</c> JSON string.</summary>
    public static HoardLoadResult Load(string json)
    {
        var raw = HoardSerializer.Deserialize(json)
            ?? throw new JsonException("A .hoard file must be a JSON object, not a null literal.");

        if (raw.SchemaVersion > HoardSchema.CurrentVersion)
            return HoardLoadResult.Reject(raw.SchemaVersion);

        var repairs = new List<HoardRepair>();

        // Work on a mutable copy of the items so each pass reparents/renormalizes independently.
        // Coalesce null to empty: a foreign file with "items": null (or an absent "items" key) is a
        // structural irregularity that repairs to an empty checklist — it must never throw (§9.1 durability).
        var items = (raw.Items ?? []).ToList();

        if (raw.SchemaVersion < HoardSchema.CurrentVersion)
            repairs.Add(new HoardRepair(HoardRepairKind.MigratedV1ToV2, string.Empty));

        items = RepairParentReferences(items, repairs);
        items = ClampOverDeep(items, repairs);
        items = RenormalizeSiblingOrders(items);

        var document = raw with
        {
            SchemaVersion = HoardSchema.CurrentVersion,
            Items = items,
        };

        return HoardLoadResult.Loaded(document, repairs);
    }

    /// <summary>Repair any <c>parentId</c> that is dangling (no such id) or cyclic (a parent chain that
    /// loops back) to <c>null</c>. After this pass the items form a valid forest.
    /// <para>Items with an empty <c>id</c> (<c>""</c>) are excluded from the lookup table and can never be a
    /// parent target — a <c>parentId</c> of <c>""</c> is therefore treated as dangling and repaired to <c>null</c>.
    /// </para></summary>
    private static List<HoardItem> RepairParentReferences(List<HoardItem> items, List<HoardRepair> repairs)
    {
        var byId = items
            .Where(i => i.Id.Length > 0)
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<HoardItem>(items.Count);
        foreach (var item in items)
        {
            if (item.ParentId is null)
            {
                result.Add(item);
                continue;
            }

            if (!byId.ContainsKey(item.ParentId))
            {
                repairs.Add(new HoardRepair(HoardRepairKind.DanglingParentReparentedToRoot, item.Id));
                result.Add(item with { ParentId = null });
                continue;
            }

            if (IsCyclic(item, byId))
            {
                repairs.Add(new HoardRepair(HoardRepairKind.CyclicParentReparentedToRoot, item.Id));
                result.Add(item with { ParentId = null });
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>Walk the parent chain from <paramref name="start"/>; return true if it revisits a node
    /// (a cycle) or exceeds the item count (a defensive bound).</summary>
    private static bool IsCyclic(HoardItem start, Dictionary<string, HoardItem> byId)
    {
        var seen = new HashSet<string>();
        var current = start;
        while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
        {
            if (!seen.Add(current.Id))
                return true;
            current = parent;
        }

        return false;
    }

    /// <summary>Clamp any item deeper than tier 2 to its deepest legal ancestor (the item at chain index
    /// <see cref="HoardSchema.MaxDepth"/>−1, i.e. depth 1), so the clamped item lands at depth 2. Applied
    /// bottom-up (deepest first) so each overflowing item's deepest-legal-ancestor is already within the
    /// cap — matching the PRD §9.1 worked example.</summary>
    private static List<HoardItem> ClampOverDeep(List<HoardItem> items, List<HoardRepair> repairs)
    {
        var current = items;
        bool changed;
        do
        {
            var byId = current
                .Where(i => i.Id.Length > 0)
                .GroupBy(i => i.Id)
                .ToDictionary(g => g.Key, g => g.First());

            // Find the deepest over-cap item this pass; reparent it to its ancestor at depth MaxDepth−1.
            HoardItem? offender = null;
            List<HoardItem>? offenderChain = null;
            var deepest = HoardSchema.MaxDepth;

            foreach (var item in current)
            {
                var chain = AncestorChain(item, byId); // root → … → item
                var depth = chain.Count - 1;
                if (depth > deepest)
                {
                    deepest = depth;
                    offender = item;
                    offenderChain = chain;
                }
            }

            if (offender is null || offenderChain is null)
            {
                changed = false;
            }
            else
            {
                // Deepest legal ancestor = the chain node at depth (MaxDepth − 1) so the item lands at MaxDepth.
                var newParent = offenderChain[HoardSchema.MaxDepth - 1];
                repairs.Add(new HoardRepair(HoardRepairKind.OverDeepClampedToTier2, offender.Id));
                current = current
                    .Select(i => ReferenceEquals(i, offender) ? i with { ParentId = newParent.Id } : i)
                    .ToList();
                changed = true;
            }
        }
        while (changed);

        return current;
    }

    /// <summary>The ancestor chain from the root down to (and including) <paramref name="item"/>. Assumes a
    /// valid forest (cycles already repaired), so the walk terminates.</summary>
    private static List<HoardItem> AncestorChain(HoardItem item, Dictionary<string, HoardItem> byId)
    {
        var reversed = new List<HoardItem> { item };
        var current = item;
        while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
        {
            reversed.Add(parent);
            current = parent;
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>Renormalize each sibling group (items sharing a <c>parentId</c>) to contiguous <c>order</c>
    /// <c>0..n-1</c>, preserving each group's relative order by current <c>order</c> then original index
    /// (a stable tie-break). The output array order itself is left as-is.</summary>
    private static List<HoardItem> RenormalizeSiblingOrders(List<HoardItem> items)
    {
        // Keyed by original array index (not id) so empty/duplicate ids cannot collide.
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
}
