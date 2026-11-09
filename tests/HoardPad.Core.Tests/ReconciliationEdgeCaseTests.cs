using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M1.4 — the PRD §9.2 dimension-3 classifier (hash authoritative, mtime only a re-hash-skip hint) and the
/// named §9.2 edge cases: the same-machine <c>seq</c> tie-break and the Save-As stale-path authority rule.
/// A fixed-instant <see cref="FakeTimeProvider"/> sources every recorded instant so no wall clock is touched.
/// </summary>
public sealed class ReconciliationEdgeCaseTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero));
    private const string Baseline = "sha256:baseline";
    private const string Different = "sha256:different";

    // ── Dimension-3 classifier: HASH IS AUTHORITATIVE ────────────────────────────────────────────────

    [Fact]
    public void ClassifyDeletedWhenFileAbsent()
    {
        var state = OnDiskStateClassifier.Classify(Baseline, Clock.GetUtcNow(),
            new CurrentDiskFile(Exists: false, ContentHash: null, Mtime: null));
        Assert.Equal(OnDiskFileState.Deleted, state);
    }

    [Fact]
    public void ClassifyUnchangedWhenCurrentHashMatchesBaseline()
    {
        var state = OnDiskStateClassifier.Classify(Baseline, Clock.GetUtcNow(),
            new CurrentDiskFile(Exists: true, ContentHash: Baseline, Mtime: Clock.GetUtcNow()));
        Assert.Equal(OnDiskFileState.PresentUnchanged, state);
    }

    [Fact]
    public void ClassifyChangedWhenCurrentHashDiffersFromBaseline()
    {
        var state = OnDiskStateClassifier.Classify(Baseline, Clock.GetUtcNow(),
            new CurrentDiskFile(Exists: true, ContentHash: Different, Mtime: Clock.GetUtcNow()));
        Assert.Equal(OnDiskFileState.PresentChangedExternally, state);
    }

    [Fact]
    public void HashOverridesAnEqualMtimeSameTickEditIsNotUnchanged()
    {
        // mtime equal (coarse / same-tick) but content hash differs → the hash wins → changed.
        // (mtime must never produce a false-unchanged verdict — §9.2 / FR-28(ii).)
        var mtime = Clock.GetUtcNow();
        var state = OnDiskStateClassifier.Classify(Baseline, mtime,
            new CurrentDiskFile(Exists: true, ContentHash: Different, Mtime: mtime));
        Assert.Equal(OnDiskFileState.PresentChangedExternally, state);
    }

    [Fact]
    public void HashOverridesADifferingMtimeSyncTouchIsNotChanged()
    {
        // A sync daemon touched mtime (mtime differs) but content hash matches → hash wins → unchanged.
        var newer = Clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        var state = OnDiskStateClassifier.Classify(Baseline, Clock.GetUtcNow(),
            new CurrentDiskFile(Exists: true, ContentHash: Baseline, Mtime: newer));
        Assert.Equal(OnDiskFileState.PresentUnchanged, state);
    }

    // ── Dimension-3 classifier: mtime is ONLY a re-hash-skip hint ─────────────────────────────────────

    [Fact]
    public void RehashSkippedIsUnchangedOnlyWhenMtimeEqualsBaseline()
    {
        var mtime = Clock.GetUtcNow();
        var state = OnDiskStateClassifier.Classify(Baseline, mtime,
            new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: mtime));
        Assert.Equal(OnDiskFileState.PresentUnchanged, state);
    }

    [Fact]
    public void RehashSkippedWithDifferingMtimeFailsSafeToChanged()
    {
        // Host skipped the re-hash but mtime differs → must NOT assume unchanged → surface conflict.
        var state = OnDiskStateClassifier.Classify(Baseline, Clock.GetUtcNow(),
            new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: Clock.GetUtcNow() + TimeSpan.FromSeconds(1)));
        Assert.Equal(OnDiskFileState.PresentChangedExternally, state);
    }

    [Fact]
    public void RehashSkippedWithMissingMtimeFailsSafeToChanged()
    {
        var state = OnDiskStateClassifier.Classify(Baseline, Clock.GetUtcNow(),
            new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        Assert.Equal(OnDiskFileState.PresentChangedExternally, state);
    }

    // ── §9.2 edge case: duplicate-tabId same-machine seq tie-break ────────────────────────────────────

    [Fact]
    public void TieBreakKeepsHigherSeq()
    {
        Assert.Equal(TieBreakOutcome.KeepA, SnapshotTieBreak.Resolve(new(Seq: 7, null), new(Seq: 6, null)));
        Assert.Equal(TieBreakOutcome.KeepB, SnapshotTieBreak.Resolve(new(Seq: 6, null), new(Seq: 7, null)));
    }

    [Fact]
    public void TieBreakFallsBackToSavedAtWhenSeqMissing()
    {
        var earlier = Clock.GetUtcNow();
        var later = Clock.GetUtcNow() + TimeSpan.FromSeconds(2);
        Assert.Equal(TieBreakOutcome.KeepA, SnapshotTieBreak.Resolve(new(null, later), new(null, earlier)));
        Assert.Equal(TieBreakOutcome.KeepB, SnapshotTieBreak.Resolve(new(null, earlier), new(null, later)));
    }

    [Fact]
    public void TieBreakFallsBackToSavedAtWhenSeqTied()
    {
        var earlier = Clock.GetUtcNow();
        var later = Clock.GetUtcNow() + TimeSpan.FromSeconds(2);
        Assert.Equal(TieBreakOutcome.KeepB, SnapshotTieBreak.Resolve(new(Seq: 5, earlier), new(Seq: 5, later)));
    }

    [Fact]
    public void TieBreakSurfacesConflictWhenNeitherSeqNorSavedAtOrdersThem()
    {
        var same = Clock.GetUtcNow();
        Assert.Equal(TieBreakOutcome.SurfaceConflict, SnapshotTieBreak.Resolve(new(Seq: 5, same), new(Seq: 5, same)));
        Assert.Equal(TieBreakOutcome.SurfaceConflict, SnapshotTieBreak.Resolve(new(null, null), new(null, null)));
    }

    // ── §9.2 edge case: Save-As stale-path authority ──────────────────────────────────────────────────

    [Fact]
    public void PathAuthoritySnapshotWinsWhenSessionEntryNotMoreRecent()
    {
        Assert.Equal(PathSource.Snapshot, PathAuthority.Resolve(snapshotSeq: 10, sessionEntrySeq: 10)); // equal
        Assert.Equal(PathSource.Snapshot, PathAuthority.Resolve(snapshotSeq: 10, sessionEntrySeq: 9));  // older
        Assert.Equal(PathSource.Snapshot, PathAuthority.Resolve(snapshotSeq: 10, sessionEntrySeq: null)); // no seq
    }

    [Fact]
    public void PathAuthoritySessionEntryWinsAfterASaveAsMoreRecentSeq()
    {
        // Save-As updated session.json's path after the last snapshot → higher session seq → session wins,
        // so restore does NOT clobber at the stale snapshot path.
        Assert.Equal(PathSource.SessionEntry, PathAuthority.Resolve(snapshotSeq: 10, sessionEntrySeq: 11));
    }

}
