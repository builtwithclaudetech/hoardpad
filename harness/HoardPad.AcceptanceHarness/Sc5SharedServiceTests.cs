// Sc5SharedServiceTests.cs — SC-5(ia): the Archetype-B shared-service gate + A-6 fixtures (M9.3).
//
// Drives mock tool-calls through the REAL AiFileOpsService (the Archetype-B execution path where
// HoardPad executes the op itself) and asserts the architecture §4.5 ordering end-to-end:
//   - the DENY path (out-of-sandbox) leaves NO file and logs `blocked` with NO undo entry;
//   - the APPLY path mutates on disk, logs `applied` (§9.3), and journals an undo entry (§9.4);
//   - the A-6 full-file-`Edit` injection-defense fixture (a single-hunk full-file Edit that replaces
//     ≥ X% of prior bytes routes to DestructiveOverwrite — the Write-keyed counter is NOT the sole
//     trigger, R3-BL1);
//   - the A-6 `Edit`-create fixture (Edit onto a non-existent path = create, R4-BL1);
//   - the FR-40 sub-threshold backstop (a sub-X% edit increments no per-op cap but DOES count toward
//     the FR-40 distinct-files aggregate, R4-BL2).
//
// Unlike SC-5(i) (which drives the python hook), (ia) drives the C# shared service — the two gates
// are complementary: a mock fed to the service cannot fire the Archetype-A PreToolUse hook, and the
// hook cannot exercise the service's apply/log/journal ordering.

using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.AcceptanceHarness;

public sealed class Sc5SharedServiceTests
{
    // ---- the DENY path: out-of-sandbox leaves NO file, logs blocked, NO undo entry ----

    [Fact]
    public async Task OutOfSandboxOpLeavesNoFileLogsBlockedAndWritesNoUndoEntry()
    {
        using var stack = new RealStack();
        string escapeTarget = stack.OutsideRoot("ESCAPED.txt");

        FileOpResult result = await stack.Service.ApplyAsync(
            "conv-1", OpFactory.Create(escapeTarget, "escape"), OpFactory.Context(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Blocked, result.Outcome);
        Assert.False(File.Exists(escapeTarget),
            "the shared service must NEVER issue an atomic-store mutation for a denied op (FR-26).");
        Assert.Null(result.JournalEntry);                  // a blocked op never enters the undo journal (§9.3 mi5).
        Assert.False(stack.AnyUndoJournalEntries());

        IReadOnlyList<string> log = await stack.ReadActivityLogAsync(CancellationToken.None);
        Assert.Single(log);
        Assert.Contains("\"op\":\"blocked\"", log[0]);
        Assert.Contains("\"outcome\":\"blocked\"", log[0]);
    }

    // ---- the APPLY path: in-sandbox create mutates, logs applied, journals ----

    [Fact]
    public async Task InSandboxCreateAppliesLogsAppliedAndJournals()
    {
        using var stack = new RealStack();
        string target = stack.InRoot("created.txt");

        FileOpResult result = await stack.Service.ApplyAsync(
            "conv-1", OpFactory.Create(target, "body"), OpFactory.Context(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);
        Assert.Equal("body", await File.ReadAllTextAsync(target));
        Assert.NotNull(result.JournalEntry);
        Assert.True(stack.AnyUndoJournalEntries());
        Assert.NotNull(result.CapDecision);                 // a counted applied op carries a cap decision.
        Assert.False(result.CapDecision!.RequiresConfirm);  // op #1, under cap.

        IReadOnlyList<string> log = await stack.ReadActivityLogAsync(CancellationToken.None);
        Assert.Single(log);
        Assert.Contains("\"op\":\"create\"", log[0]);
        Assert.Contains("\"outcome\":\"applied\"", log[0]);
    }

    // ---- A-6: the full-file-`Edit` injection-defense fixture (R3-BL1) ----

    [Fact]
    public async Task FullFileEditReplacingMostOfPriorBytesRoutesToDestructiveOverwrite()
    {
        // A single-hunk full-file Edit that replaces ≥ X% of prior bytes is functionally a full
        // overwrite and must route to FR-38 (DestructiveOverwrite) even though it presents as Edit —
        // the Write-keyed counter is not the sole trigger. We prove the routing via the classifier
        // (the same predicate the hook ports) AND that the service applies + journals it.
        using var stack = new RealStack();
        string target = stack.InRoot("Program.cs");
        string prior = new string('x', 200);     // 200 bytes prior.
        await File.WriteAllTextAsync(target, prior);

        // Replace it with a 10-byte stub: delta=190, 190*100 (19000) >= 80*200 (16000) => destructive.
        var classification = new OpClassificationInput(WriteTool.Edit, TargetExists: true, TargetPreOpBytes: prior.Length, ProposedBytes: 10);
        OpKind kind = CapClassifier.Classify(classification, CapConfig.PinnedDestructiveMagnitudePercent);
        Assert.Equal(OpKind.DestructiveOverwrite, kind);   // the routing the Write-keyed counter would miss.

        FileOpResult result = await stack.Service.ApplyAsync(
            "conv-1", OpFactory.Edit(target, new string('y', 10), priorBytes: prior.Length, tool: WriteTool.Edit),
            OpFactory.Context(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);
        Assert.NotNull(result.JournalEntry);
        Assert.NotNull(result.JournalEntry!.PriorContent);  // a destructive overwrite captures a before-image.
    }

    // ---- A-6: the `Edit`-create fixture (R4-BL1) routes to Create, not a bypass ----

    [Fact]
    public void EditCreateOnNonExistentPathRoutesToCreate()
    {
        var classification = new OpClassificationInput(WriteTool.Edit, TargetExists: false, TargetPreOpBytes: 0, ProposedBytes: 12);
        OpKind kind = CapClassifier.Classify(classification, CapConfig.PinnedDestructiveMagnitudePercent);
        Assert.Equal(OpKind.Create, kind);   // an Edit to a non-existent path is a create (counts FR-11 + FR-40).
    }

    // ---- A-6: the sub-threshold-overwrite backstop (R4-BL2) — counts FR-40, not FR-38 ----

    [Fact]
    public void SubThresholdEditRoutesToSubThresholdButStillIncrementsFr40()
    {
        // A 5-byte shrink of a 200-byte file (~2.5%) is below the 80% magnitude threshold: it routes to
        // SubThresholdEdit (no per-op cap) but must STILL increment the FR-40 distinct-files set — the
        // aggregate backstop for the sub-threshold mass-overwrite vector.
        var classification = new OpClassificationInput(WriteTool.Edit, TargetExists: true, TargetPreOpBytes: 200, ProposedBytes: 195);
        OpKind kind = CapClassifier.Classify(classification, CapConfig.PinnedDestructiveMagnitudePercent);
        Assert.Equal(OpKind.SubThresholdEdit, kind);

        // Account it: the per-op (destructive) counter stays 0, but FilesTouched gains the path.
        CapDecision decision = CapAccounting.Account(CapState.Empty, kind, "/root/f.txt", CapConfig.Pinned);
        Assert.Equal(0, decision.State.DestructiveCount);
        Assert.Equal(1, decision.State.FilesTouchedCount);   // FR-40 still counts the file.
        Assert.False(decision.RequiresConfirm);
    }
}
