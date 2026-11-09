using System.Linq;
using Xunit;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// Migration, version-rejection, and structural-repair tests (PRD §9.1 / architecture §5.1 / TD §3.5):
/// v1→v2 migration; <c>schemaVersion &gt; 2</c> rejection (never silently downgraded); dangling/cyclic
/// <c>parentId</c> repaired to <c>null</c>; over-deep import clamped to tier 2 by the worked-example rule.
/// </summary>
public sealed class HoardMigrationAndRepairTests
{
    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    // ── v1 → v2 migration ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void V1FileMigratesToV2WithParentIdNullAndCollapsedFalse()
    {
        var result = HoardLoader.Load(Normalize(HoardFixtures.V1File));

        Assert.False(result.Rejected);
        var doc = result.Document!;
        Assert.Equal(HoardSchema.CurrentVersion, doc.SchemaVersion);
        Assert.Contains(result.Repairs, r => r.Kind == HoardRepairKind.MigratedV1ToV2);

        // Every item becomes a tier-0 leaf; collapsed defaults false; tier-0 order is preserved.
        Assert.All(doc.Items, item => Assert.Null(item.ParentId));
        Assert.All(doc.Items, item => Assert.False(item.Collapsed));
        Assert.Equal([0, 1], doc.Items.Select(i => i.Order).ToArray());

        // The first re-save emits schemaVersion 2.
        var reSaved = HoardSerializer.Serialize(doc);
        Assert.Contains("\"schemaVersion\": 2", reSaved);
    }

    // ── schemaVersion > 2 rejection ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HigherSchemaVersionIsRejectedNotDowngraded()
    {
        const string future =
"""
{
  "schemaVersion": 3,
  "title": "From the future",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": []
}
""";
        var result = HoardLoader.Load(future);

        Assert.True(result.Rejected);
        Assert.Equal(3, result.RejectedVersion);
        Assert.Null(result.Document);
    }

    // ── dangling parentId → repaired to null ─────────────────────────────────────────────────────────

    [Fact]
    public void DanglingParentIdIsRepairedToNullAndLogged()
    {
        const string dangling =
"""
{
  "schemaVersion": 2,
  "title": "Dangling",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": [
    { "id": "A", "text": "A", "checked": false, "parentId": null, "order": 0, "collapsed": false },
    { "id": "B", "text": "B", "checked": false, "parentId": "GHOST", "order": 0, "collapsed": false }
  ]
}
""";
        var result = HoardLoader.Load(dangling);

        Assert.False(result.Rejected);
        var b = result.Document!.Items.Single(i => i.Id == "B");
        Assert.Null(b.ParentId);
        Assert.Contains(result.Repairs,
            r => r.Kind == HoardRepairKind.DanglingParentReparentedToRoot && r.ItemId == "B");
    }

    // ── cyclic parentId → repaired to null ───────────────────────────────────────────────────────────

    [Fact]
    public void CyclicParentIdIsRepairedToNull()
    {
        // A → B → A is a 2-node cycle; both items are reparented to root.
        const string cyclic =
"""
{
  "schemaVersion": 2,
  "title": "Cycle",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": [
    { "id": "A", "text": "A", "checked": false, "parentId": "B", "order": 0, "collapsed": false },
    { "id": "B", "text": "B", "checked": false, "parentId": "A", "order": 0, "collapsed": false }
  ]
}
""";
        var result = HoardLoader.Load(cyclic);

        Assert.False(result.Rejected);
        Assert.All(result.Document!.Items, item => Assert.Null(item.ParentId));
        Assert.Contains(result.Repairs, r => r.Kind == HoardRepairKind.CyclicParentReparentedToRoot);
    }

    // ── over-deep import → clamped to tier 2 (the PRD §9.1 worked example) ────────────────────────────

    [Fact]
    public void OverDeepImportIsClampedToTier2PerWorkedExample()
    {
        // A(0) → B(1) → C(2) → D(3 overflow), E(3 overflow). D/E reparent to their deepest legal ancestor B.
        const string overDeep =
"""
{
  "schemaVersion": 2,
  "title": "Deep",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": [
    { "id": "A", "text": "A", "checked": false, "parentId": null, "order": 0, "collapsed": false },
    { "id": "B", "text": "B", "checked": false, "parentId": "A", "order": 0, "collapsed": false },
    { "id": "C", "text": "C", "checked": false, "parentId": "B", "order": 0, "collapsed": false },
    { "id": "D", "text": "D", "checked": false, "parentId": "C", "order": 0, "collapsed": false },
    { "id": "E", "text": "E", "checked": false, "parentId": "C", "order": 1, "collapsed": false }
  ]
}
""";
        var result = HoardLoader.Load(overDeep);

        Assert.False(result.Rejected);
        var items = result.Document!.Items.ToDictionary(i => i.Id);

        // A/B/C unchanged; D and E reparent to B (their deepest legal ancestor), becoming tier-2 siblings of C.
        Assert.Null(items["A"].ParentId);
        Assert.Equal("A", items["B"].ParentId);
        Assert.Equal("B", items["C"].ParentId);
        Assert.Equal("B", items["D"].ParentId);
        Assert.Equal("B", items["E"].ParentId);

        // Two clamp repairs recorded (D and E).
        var clamps = result.Repairs.Where(r => r.Kind == HoardRepairKind.OverDeepClampedToTier2).ToList();
        Assert.Equal(2, clamps.Count);
        Assert.Contains(clamps, r => r.ItemId == "D");
        Assert.Contains(clamps, r => r.ItemId == "E");

        // C/D/E now share parent B as a sibling group, renormalized to contiguous 0..2.
        var bGroup = result.Document!.Items
            .Where(i => i.ParentId == "B")
            .OrderBy(i => i.Order)
            .Select(i => i.Order)
            .ToArray();
        Assert.Equal([0, 1, 2], bGroup);
    }

    // ── depth-derivation sanity: max depth after clamp is 2 ───────────────────────────────────────────

    // ── null or absent "items" array → loads as empty list (§9.1 durability guarantee) ──────────────────

    [Fact]
    public void NullItemsArrayLoadsAsEmptyList()
    {
        const string nullItems =
"""
{
  "schemaVersion": 2,
  "title": "Null items",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": null
}
""";
        var result = HoardLoader.Load(nullItems);

        Assert.False(result.Rejected);
        Assert.NotNull(result.Document);
        Assert.Empty(result.Document!.Items);
    }

    [Fact]
    public void AbsentItemsKeyLoadsAsEmptyList()
    {
        const string noItemsKey =
"""
{
  "schemaVersion": 2,
  "title": "No items key",
  "settings": { "checkedItemMode": "strikethrough-in-place" }
}
""";
        var result = HoardLoader.Load(noItemsKey);

        Assert.False(result.Rejected);
        Assert.NotNull(result.Document);
        Assert.Empty(result.Document!.Items);
    }

    // ── self-parent cycle (id == parentId) → repaired to root ───────────────────────────────────────────

    [Fact]
    public void SelfParentCycleIsRepairedToRoot()
    {
        const string selfParent =
"""
{
  "schemaVersion": 2,
  "title": "Self parent",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": [
    { "id": "A", "text": "A", "checked": false, "parentId": "A", "order": 0, "collapsed": false }
  ]
}
""";
        var result = HoardLoader.Load(selfParent);

        Assert.False(result.Rejected);
        var item = result.Document!.Items.Single();
        Assert.Null(item.ParentId);
        Assert.Contains(result.Repairs, r =>
            r.Kind == HoardRepairKind.CyclicParentReparentedToRoot && r.ItemId == "A");
    }

    // ── item whose parentId points to an empty-id item → treated as dangling ────────────────────────────

    [Fact]
    public void ParentIdPointingAtEmptyIdItemIsTreatedAsDangling()
    {
        // An item with id="" cannot be a parent target (empty-id items are excluded from the lookup table).
        // The child whose parentId is "" sees no matching entry and is reparented to root.
        const string emptyIdParent =
"""
{
  "schemaVersion": 2,
  "title": "Empty id parent",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": [
    { "id": "", "text": "Empty id", "checked": false, "parentId": null, "order": 0, "collapsed": false },
    { "id": "B", "text": "B", "checked": false, "parentId": "", "order": 0, "collapsed": false }
  ]
}
""";
        var result = HoardLoader.Load(emptyIdParent);

        Assert.False(result.Rejected);
        var b = result.Document!.Items.Single(i => i.Id == "B");
        Assert.Null(b.ParentId);
        Assert.Contains(result.Repairs,
            r => r.Kind == HoardRepairKind.DanglingParentReparentedToRoot && r.ItemId == "B");
    }

    // ── depth-derivation sanity: max depth after clamp is 2 ───────────────────────────────────────────

    [Fact]
    public void NoItemExceedsTierTwoAfterClamp()
    {
        const string overDeep =
"""
{
  "schemaVersion": 2,
  "title": "Deep chain",
  "settings": { "checkedItemMode": "strikethrough-in-place" },
  "items": [
    { "id": "A", "text": "A", "checked": false, "parentId": null, "order": 0, "collapsed": false },
    { "id": "B", "text": "B", "checked": false, "parentId": "A", "order": 0, "collapsed": false },
    { "id": "C", "text": "C", "checked": false, "parentId": "B", "order": 0, "collapsed": false },
    { "id": "D", "text": "D", "checked": false, "parentId": "C", "order": 0, "collapsed": false },
    { "id": "E", "text": "E", "checked": false, "parentId": "D", "order": 0, "collapsed": false }
  ]
}
""";
        var doc = HoardLoader.Load(overDeep).Document!;
        var byId = doc.Items.ToDictionary(i => i.Id);

        foreach (var item in doc.Items)
        {
            var depth = 0;
            var current = item;
            while (current.ParentId is { } pid && byId.TryGetValue(pid, out var parent))
            {
                depth++;
                current = parent;
            }

            Assert.True(depth <= HoardSchema.MaxDepth, $"item {item.Id} has depth {depth}");
        }
    }
}
