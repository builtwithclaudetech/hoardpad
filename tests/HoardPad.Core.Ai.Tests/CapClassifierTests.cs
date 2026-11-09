using HoardPad.Core.Ai.Caps;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.1 — the FR-38 counted-op classifier predicate over <c>(tool, target_pre_op_bytes, proposed_bytes)</c>
/// (PRD FR-38 / TD §4.5), the create-vs-overwrite routing on BOTH <c>Write</c> and <c>Edit</c> (R4-BL1), and
/// FR-40 misroute-invariance (R4-MA4): a misrouted create/overwrite cannot push the distinct-files count past
/// the FR-40 cap.
/// </summary>
public sealed class CapClassifierTests
{
    private const int X = CapConfig.PinnedDestructiveMagnitudePercent; // 80

    private static OpKind Classify(WriteTool tool, bool exists, long pre, long proposed) =>
        CapClassifier.Classify(new OpClassificationInput(tool, exists, pre, proposed), X);

    // ---- Create-vs-overwrite split (on BOTH Write and Edit) -------------------------------------------

    [Fact]
    public void WriteToNonExistentTargetIsCreate() =>
        Assert.Equal(OpKind.Create, Classify(WriteTool.Write, exists: false, pre: 0, proposed: 500));

    [Fact]
    public void EditToNonExistentTargetIsCreateTheEditCreateIdiomR4BL1() =>
        // An Edit with empty old_string on a non-existent path is the agent's file-create idiom — it must
        // route to the FR-11 create cap, not be silently let through (R4-BL1, live-reproduced bypass).
        Assert.Equal(OpKind.Create, Classify(WriteTool.Edit, exists: false, pre: 0, proposed: 200));

    [Fact]
    public void WholeFileWriteOntoExistingTargetIsDestructiveRegardlessOfMagnitude() =>
        // A whole-file Write onto an existing file is destructive regardless of the byte delta (FR-38).
        Assert.Equal(OpKind.DestructiveOverwrite, Classify(WriteTool.Write, exists: true, pre: 1000, proposed: 1000));

    // ---- FR-38 magnitude predicate (the X% = 80% growth/shrink rule) ----------------------------------

    [Fact]
    public void EditAtOrAboveThresholdIsDestructive()
    {
        // A single full-file Edit hunk that replaces ≥ 80% of the prior bytes is a destructive overwrite even
        // though it presents as Edit (the magnitude predicate, not a Write-keyed counter — R3-BL1). A 1000→100
        // byte rewrite is a 90% shrink, ≥ 80%.
        Assert.Equal(OpKind.DestructiveOverwrite, Classify(WriteTool.Edit, exists: true, pre: 1000, proposed: 100));
        // Exactly 80% growth (1000 → 1800, delta 800 = 80% of 1000) is at the threshold → counts.
        Assert.Equal(OpKind.DestructiveOverwrite, Classify(WriteTool.Edit, exists: true, pre: 1000, proposed: 1800));
    }

    [Fact]
    public void EditBelowThresholdIsSubThresholdEditExcluded()
    {
        // A 20-line edit in a 200-line file ≈ 10% — a legitimate partial edit. Excluded from the FR-38 cap so
        // a multi-file refactor is never nagged (FR-38), but it STILL increments FR-40 (asserted in accounting).
        Assert.Equal(OpKind.SubThresholdEdit, Classify(WriteTool.Edit, exists: true, pre: 2000, proposed: 2100));
        // Just under the threshold (1000 → 1799, delta 799 < 800) is sub-X%.
        Assert.Equal(OpKind.SubThresholdEdit, Classify(WriteTool.Edit, exists: true, pre: 1000, proposed: 1799));
    }

    [Fact]
    public void EditOntoEmptyExistingFileIsSubThresholdNotDestructiveViaMagnitude() =>
        // A prior size of 0 cannot have ≥ X% of its bytes replaced by a delta measure; a destructive empty-file
        // overwrite reaches Archetype A as a whole-file Write (caught by the tool-name branch), not as an Edit.
        Assert.Equal(OpKind.SubThresholdEdit, Classify(WriteTool.Edit, exists: true, pre: 0, proposed: 5000));

    // ---- FR-40 misroute-invariance (R4-MA4) -----------------------------------------------------------

    [Fact]
    public void MisrouteCreateVsOverwriteCannotExceedFr40FileCountedOnce()
    {
        // The stat-race (R4-MA4): the SAME file can be misrouted to create on one path and overwrite on the
        // other. FR-40 counts distinct paths regardless of routing, so the file is counted exactly once and the
        // misroute cannot push the distinct-files count past the cap.
        const string path = "/root/contended.cs";

        // Route the same path as a create, then as an overwrite — FR-40 set must hold cardinality 1.
        var asCreate = CapAccounting.Account(CapState.Empty, OpKind.Create, path, CapConfig.Pinned);
        var thenOverwrite = CapAccounting.Account(asCreate.State, OpKind.DestructiveOverwrite, path, CapConfig.Pinned);
        Assert.Equal(1, thenOverwrite.State.FilesTouchedCount);

        // And the reverse order — equally once.
        var asOverwrite = CapAccounting.Account(CapState.Empty, OpKind.DestructiveOverwrite, path, CapConfig.Pinned);
        var thenCreate = CapAccounting.Account(asOverwrite.State, OpKind.Create, path, CapConfig.Pinned);
        Assert.Equal(1, thenCreate.State.FilesTouchedCount);
    }

    [Fact]
    public void Fr40BoundsSubThresholdMassOverwriteRegardlessOfPerOpRouting()
    {
        // The R4-BL2 backstop: N distinct files each overwritten sub-X% trips NEITHER FR-11 nor FR-38 (each is
        // a SubThresholdEdit), yet FR-40 holds them at the distinct-files cap. Misroute-invariance in aggregate:
        // whichever per-op bucket each op fell into, FR-40 bounds the total distinct files.
        var cfg = new CapConfig(CreateCap: 25, DestructiveCap: 10, FilesTouchedCap: 4, DestructiveMagnitudePercent: 80);
        var state = CapState.Empty;
        CapDecision d = default!;
        for (var i = 1; i <= 5; i++) // 5 distinct sub-threshold edits, cap = 4
        {
            d = CapAccounting.Account(state, OpKind.SubThresholdEdit, $"/root/f{i}.cs", cfg);
            state = d.State;
        }
        Assert.Equal(0, state.CreateCount); // no per-op create cap touched
        Assert.Equal(0, state.DestructiveCount); // no per-op destructive cap touched
        Assert.True(state.FilesTouchedCapTripped); // FR-40 is the bound
        Assert.Equal(CapTrigger.FilesTouchedCap, d.Trigger);
    }
}
