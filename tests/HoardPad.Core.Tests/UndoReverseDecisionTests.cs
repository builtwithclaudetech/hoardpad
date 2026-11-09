using HoardPad.Core.Records;
using HoardPad.Core.Undo;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M1.4 — the PRD §9.4 undo-journal reverse decision: per-op reversal semantics, the over-cap
/// <c>snapshotSkipped</c> never-restore guard, and the M14 TOCTOU re-verify on BOTH axes (source/target
/// AND clobbered-destination, FR-28(ii) / §9.4 <c>destPriorContentHash</c>). Hash authoritative, mtime
/// only a re-hash-skip hint. A fixed-instant <see cref="FakeTimeProvider"/> sources every recorded mtime
/// so no wall clock is touched.
/// </summary>
public sealed class UndoReverseDecisionTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 6, 20, 18, 4, 22, TimeSpan.Zero));
    private const string PreHash = "sha256:preop";
    private const string DiffHash = "sha256:external";
    private const string DestHash = "sha256:dest";

    private static readonly DateTimeOffset PreMtime = Clock.GetUtcNow();

    private static UndoJournalEntry Entry(
        FileOp op,
        BeforeImage? priorContent = null,
        BeforeImage? destPriorContent = null,
        string? destPriorContentHash = null,
        string? preOpContentHash = PreHash,
        DateTimeOffset? preOpMtime = null,
        bool snapshotSkipped = false) =>
        new(
            UndoRef: "undo/turn-7f3a-03",
            BufferJournalId: "buf-1",
            RequestId: "turn-7f3a",
            Op: op,
            Paths: new OpPaths("/root/todo.md", Source: "/root/a.md", Dest: "/root/dest.md"),
            PriorContent: priorContent,
            DestPriorContent: destPriorContent,
            DestPriorContentHash: destPriorContentHash,
            PreOpContentHash: preOpContentHash,
            PreOpMtime: preOpMtime ?? PreMtime,
            SnapshotSkipped: snapshotSkipped);

    // Source/target unchanged-since-op (hash matches the recorded pre-op hash).
    private static CurrentTargetState Unchanged =>
        new(Exists: true, ContentHash: PreHash, Mtime: PreMtime);

    // Clobbered destination unchanged-since-op (hash matches the recorded destPriorContentHash).
    private static CurrentTargetState DestUnchanged =>
        new(Exists: true, ContentHash: DestHash, Mtime: PreMtime);

    private static readonly BeforeImage Blob = new("blob", "before/turn-7f3a-03.bin");

    // ── Over-cap guard: snapshotSkipped NEVER restores ────────────────────────────────────────────────

    [Theory]
    [InlineData(FileOp.Create)]
    [InlineData(FileOp.Edit)]
    [InlineData(FileOp.Delete)]
    [InlineData(FileOp.Move)]
    [InlineData(FileOp.Copy)]
    public void SnapshotSkippedEntryIsAlwaysUnavailable(FileOp op)
    {
        // Over-cap → no before-image → restore path never entered, regardless of op or current state.
        var action = UndoReverseDecision.Decide(
            Entry(op, snapshotSkipped: true, preOpContentHash: null, preOpMtime: PreMtime),
            Unchanged);
        Assert.Equal(ReverseActionKind.Unavailable, action.Kind);
    }

    [Fact]
    public void SnapshotSkippedTakesPrecedenceOverAnExternalModification()
    {
        // Even with an externally-modified target, snapshotSkipped short-circuits to Unavailable
        // (it must NEVER reach the restore/refuse comparison — there is nothing to restore).
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Edit, snapshotSkipped: true),
            new CurrentTargetState(Exists: true, ContentHash: DiffHash, Mtime: PreMtime));
        Assert.Equal(ReverseActionKind.Unavailable, action.Kind);
    }

    // ── Per-op §9.4 reversal (guards passed: source unchanged, destination unchanged where applicable) ──

    [Fact]
    public void CreateReversesToDeleteCreated()
    {
        // create → undo deletes the created file. priorContent / preOpContentHash are null (nothing before);
        // the create path is NOT subject to the decision-time hash compare.
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Create, priorContent: null, preOpContentHash: null, preOpMtime: null),
            new CurrentTargetState(Exists: true, ContentHash: "sha256:created", Mtime: PreMtime));
        Assert.Equal(ReverseActionKind.DeleteCreated, action.Kind);
        Assert.False(action.RestoreClobberedDestination);
    }

    [Fact]
    public void EditReversesToRestoreTarget()
    {
        var action = UndoReverseDecision.Decide(Entry(FileOp.Edit, priorContent: Blob), Unchanged);
        Assert.Equal(ReverseActionKind.RestoreTarget, action.Kind);
    }

    [Fact]
    public void DeleteReversesToRestoreTarget()
    {
        // The host PRE-STAGES (resurrects) the target before calling Decide for a delete undo, so
        // Exists: true here — without pre-staging, !Exists would trip ExternallyModified and refuse.
        // This test pins the required host ordering (see CurrentTargetState XML doc).
        var action = UndoReverseDecision.Decide(Entry(FileOp.Delete, priorContent: Blob), Unchanged);
        Assert.Equal(ReverseActionKind.RestoreTarget, action.Kind);
    }

    [Fact]
    public void MoveNonClobberingReversesToMoveBackWithoutDestRestore()
    {
        var action = UndoReverseDecision.Decide(Entry(FileOp.Move, destPriorContent: null), Unchanged);
        Assert.Equal(ReverseActionKind.MoveBack, action.Kind);
        Assert.False(action.RestoreClobberedDestination);
    }

    [Fact]
    public void MoveClobberingReversesToMoveBackAndRestoresClobberedDestination()
    {
        // Supply destCurrent matching the recorded destPriorContentHash so the dest re-verify passes.
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Move, destPriorContent: Blob, destPriorContentHash: DestHash),
            current: Unchanged,
            destCurrent: DestUnchanged);
        Assert.Equal(ReverseActionKind.MoveBack, action.Kind);
        Assert.True(action.RestoreClobberedDestination);
    }

    [Fact]
    public void CopyNonClobberingReversesToUndoCopyWithoutDestRestore()
    {
        var action = UndoReverseDecision.Decide(Entry(FileOp.Copy, destPriorContent: null), Unchanged);
        Assert.Equal(ReverseActionKind.UndoCopy, action.Kind);
        Assert.False(action.RestoreClobberedDestination);
    }

    [Fact]
    public void CopyClobberingReversesToUndoCopyAndRestoresClobberedDestination()
    {
        // Supply destCurrent matching the recorded destPriorContentHash so the dest re-verify passes.
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Copy, destPriorContent: Blob, destPriorContentHash: DestHash),
            current: Unchanged,
            destCurrent: DestUnchanged);
        Assert.Equal(ReverseActionKind.UndoCopy, action.Kind);
        Assert.True(action.RestoreClobberedDestination);
    }

    // ── M14 TOCTOU re-verify — source/target axis: external modification → refuse ───────────────────────

    [Theory]
    [InlineData(FileOp.Edit)]
    [InlineData(FileOp.Delete)]
    [InlineData(FileOp.Move)]
    [InlineData(FileOp.Copy)]
    public void ExternalModificationCurrentHashDiffersRefuses(FileOp op)
    {
        var action = UndoReverseDecision.Decide(
            Entry(op, priorContent: Blob),
            new CurrentTargetState(Exists: true, ContentHash: DiffHash, Mtime: PreMtime));
        Assert.Equal(ReverseActionKind.RefuseReprompt, action.Kind);
    }

    [Fact]
    public void TargetVanishedUnderUsRefuses()
    {
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Edit, priorContent: Blob),
            new CurrentTargetState(Exists: false, ContentHash: null, Mtime: null));
        Assert.Equal(ReverseActionKind.RefuseReprompt, action.Kind);
    }

    [Fact]
    public void EqualMtimeSameTickEditStillRefusesWhenHashDiffers()
    {
        // mtime equal (coarse / same-tick) cannot mask a real edit: the hash is authoritative → refuse.
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Edit, priorContent: Blob, preOpMtime: PreMtime),
            new CurrentTargetState(Exists: true, ContentHash: DiffHash, Mtime: PreMtime));
        Assert.Equal(ReverseActionKind.RefuseReprompt, action.Kind);
    }

    [Fact]
    public void DifferingMtimeSyncTouchDoesNotBlockWhenHashMatches()
    {
        // A sync daemon touched mtime (differs) but content hash matches the recorded pre-op hash →
        // not externally modified → reversal proceeds (mtime is only a skip hint, not the basis).
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Edit, priorContent: Blob, preOpMtime: PreMtime),
            new CurrentTargetState(Exists: true, ContentHash: PreHash, Mtime: PreMtime + TimeSpan.FromMinutes(5)));
        Assert.Equal(ReverseActionKind.RestoreTarget, action.Kind);
    }

    // ── M14 re-verify — source axis mtime-only skip path (no current hash supplied) ──────────────────────

    [Fact]
    public void RehashSkippedProceedsWhenMtimeEqualsRecorded()
    {
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Edit, priorContent: Blob, preOpMtime: PreMtime),
            new CurrentTargetState(Exists: true, ContentHash: null, Mtime: PreMtime));
        Assert.Equal(ReverseActionKind.RestoreTarget, action.Kind);
    }

    [Fact]
    public void RehashSkippedRefusesWhenMtimeDiffers()
    {
        // No current hash + mtime differs from recorded → cannot prove unmodified → refuse (fail safe).
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Edit, priorContent: Blob, preOpMtime: PreMtime),
            new CurrentTargetState(Exists: true, ContentHash: null, Mtime: PreMtime + TimeSpan.FromSeconds(1)));
        Assert.Equal(ReverseActionKind.RefuseReprompt, action.Kind);
    }

    [Fact]
    public void RehashSkippedRefusesWhenMtimeMissing()
    {
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Edit, priorContent: Blob, preOpMtime: PreMtime),
            new CurrentTargetState(Exists: true, ContentHash: null, Mtime: null));
        Assert.Equal(ReverseActionKind.RefuseReprompt, action.Kind);
    }

    // ── M14 TOCTOU re-verify — DESTINATION axis (FR-28(ii) / §9.4 destPriorContentHash) ───────────────

    [Fact]
    public void ClobberingMoveTargetUnchangedButDestModifiedExternallyRefuses()
    {
        // The source/target is unchanged (hash matches PreHash), but a third party modified the clobbered
        // destination between the AI move and this undo. Restoring stale destPriorContent onto the modified
        // dest would silently destroy the newer dest content — must refuse, NOT silently restore (FR-28(ii)).
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Move, destPriorContent: Blob, destPriorContentHash: DestHash),
            current: Unchanged,
            destCurrent: new CurrentTargetState(Exists: true, ContentHash: DiffHash, Mtime: PreMtime));
        Assert.Equal(ReverseActionKind.RefuseReprompt, action.Kind);
    }

    [Fact]
    public void ClobberingCopyTargetUnchangedButDestModifiedExternallyRefuses()
    {
        // Same destination-axis external-mod guard for a clobbering copy undo.
        var action = UndoReverseDecision.Decide(
            Entry(FileOp.Copy, destPriorContent: Blob, destPriorContentHash: DestHash),
            current: Unchanged,
            destCurrent: new CurrentTargetState(Exists: true, ContentHash: DiffHash, Mtime: PreMtime));
        Assert.Equal(ReverseActionKind.RefuseReprompt, action.Kind);
    }
}
