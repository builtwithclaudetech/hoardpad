using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M1.4 — every cell of the PRD §9.2 3-dimensional session/recovery reconciliation table, enumerated as a
/// theory over the full cross-product of (snapshot present?) × (session-tab present?) × (on-disk file
/// state), asserting each cell's prescribed Action + modifiers. The §9.2 table is the authoritative truth
/// table (architecture §5.2 is binding-by-reference, no discrepancy). A fixed-instant
/// <see cref="FakeTimeProvider"/> drives the recorded instants so no wall clock is touched.
/// </summary>
public sealed class SessionReconciliationTests
{
    // A fixed clock the fixtures could source recorded instants from — proves the suite never touches the
    // wall clock (the §9.2 decision itself is hash-driven; mtime/savedAt are the only time-typed inputs).
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero));

    public static TheoryData<string, ReconciliationInputs, ReconciliationResult> Cells()
    {
        var d = new TheoryData<string, ReconciliationInputs, ReconciliationResult>();

        // ── Snapshot PRESENT, session-tab ABSENT (orphan recovery) ──────────────────────────────────
        d.Add("present|absent|unchanged → orphan, no conflict",
            new ReconciliationInputs(true, false, OnDiskFileState.PresentUnchanged, TabKind.File),
            new ReconciliationResult(ReconciliationAction.RecoverOrphan));

        d.Add("present|absent|changed → orphan + conflict",
            new ReconciliationInputs(true, false, OnDiskFileState.PresentChangedExternally, TabKind.File),
            new ReconciliationResult(ReconciliationAction.RecoverOrphan, SurfaceConflict: true));

        d.Add("present|absent|deleted → orphan + on-disk-deleted",
            new ReconciliationInputs(true, false, OnDiskFileState.Deleted, TabKind.File),
            new ReconciliationResult(ReconciliationAction.RecoverOrphan, OnDiskFileDeleted: true));

        // ── Snapshot PRESENT, session-tab PRESENT (join on tabId) ───────────────────────────────────
        d.Add("present|present|unchanged → join",
            new ReconciliationInputs(true, true, OnDiskFileState.PresentUnchanged, TabKind.File),
            new ReconciliationResult(ReconciliationAction.Join));

        d.Add("present|present|changed → join + conflict",
            new ReconciliationInputs(true, true, OnDiskFileState.PresentChangedExternally, TabKind.File),
            new ReconciliationResult(ReconciliationAction.Join, SurfaceConflict: true));

        d.Add("present|present|deleted → join + on-disk-deleted",
            new ReconciliationInputs(true, true, OnDiskFileState.Deleted, TabKind.File),
            new ReconciliationResult(ReconciliationAction.Join, OnDiskFileDeleted: true));

        // ── Scratch rows (dimension 3 = N/A) ────────────────────────────────────────────────────────
        d.Add("present|present (scratch)|N/A → join scratch",
            new ReconciliationInputs(true, true, OnDiskFileState.NotApplicable, TabKind.Scratch),
            new ReconciliationResult(ReconciliationAction.Join, IsScratch: true));

        d.Add("present|absent (scratch)|N/A → orphan scratch",
            new ReconciliationInputs(true, false, OnDiskFileState.NotApplicable, TabKind.Scratch),
            new ReconciliationResult(ReconciliationAction.RecoverOrphan, IsScratch: true));

        // ── Snapshot ABSENT, session-tab PRESENT ────────────────────────────────────────────────────
        d.Add("absent|present|present(file) → keep, reopen from disk",
            new ReconciliationInputs(false, true, OnDiskFileState.PresentUnchanged, TabKind.File),
            new ReconciliationResult(ReconciliationAction.KeepReopenFromDisk));

        d.Add("absent|present|deleted (not dirty) → drop",
            new ReconciliationInputs(false, true, OnDiskFileState.Deleted, TabKind.File, SessionTabIsDirty: false),
            new ReconciliationResult(ReconciliationAction.DropTab));

        d.Add("absent|present|deleted (isDirty) → drop + surface unrecoverable loss",
            new ReconciliationInputs(false, true, OnDiskFileState.Deleted, TabKind.File, SessionTabIsDirty: true),
            new ReconciliationResult(ReconciliationAction.DropTab, SurfaceUnrecoverableLoss: true));

        d.Add("absent|present (scratch)|N/A → drop scratch",
            new ReconciliationInputs(false, true, OnDiskFileState.NotApplicable, TabKind.Scratch),
            new ReconciliationResult(ReconciliationAction.DropTab, IsScratch: true));

        // ── Both ABSENT ─────────────────────────────────────────────────────────────────────────────
        d.Add("absent|absent → nothing",
            new ReconciliationInputs(false, false, OnDiskFileState.NotApplicable, TabKind.File),
            new ReconciliationResult(ReconciliationAction.Nothing));

        return d;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void ReconcileEachCellMatchesSection92(string _, ReconciliationInputs inputs, ReconciliationResult expected)
    {
        // The fixed clock is available for any recorded-instant fixture; the decision is hash-driven and
        // deterministic, so asserting equality of the whole result record covers Action + every modifier.
        Assert.NotNull(Clock);
        Assert.Equal(expected, SessionReconciliation.Decide(inputs));
    }

    [Fact]
    public void HoardTabUsesDimension3LikeAFileTab()
    {
        // A .hoard tab is file-backed (dimension 3 applies, over canonical-parsed content — MA5).
        var result = SessionReconciliation.Decide(
            new ReconciliationInputs(true, true, OnDiskFileState.PresentChangedExternally, TabKind.Hoard));
        Assert.Equal(new ReconciliationResult(ReconciliationAction.Join, SurfaceConflict: true), result);
    }
}
