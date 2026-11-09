// TabOrderEqualityTests.cs — FS-2: session.json save→restore ORDER EQUALITY (deterministic, no set-hashing).
//
// FS-1 tier (ii) derives the AI write root from "the first path-bearing tab in restore order", so a
// silent reorder here silently moves the AI write boundary onto the wrong tab's folder — this is why FS-2
// is correctness-critical, not merely cosmetic. Hostile-review CRITICAL (2026-07-11, live-reproduced):
// SessionReconciler used to seed its restore-order iteration from a HashSet built off snapshotByTab.Keys
// (DIRTY tabs, which have recovery snapshots) BEFORE unioning sessionByTab.Keys (the ordered save order),
// so a dirty tab could enumerate ahead of an earlier-saved clean tab. The fix drives restore order directly
// off session.Tabs' array order (a List, never a HashSet), with snapshot-only orphans appended afterward in
// a deterministic (id-sorted) tail. Class name contains "TabOrderEquality" per the verify filter.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Session;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class TabOrderEqualityTests
{
    private static SessionTab CleanFileTab(string id, string path) =>
        new(id, TabKind.File, path, "Plain Text", IsDirty: false, Cursor: null, RecoveryRef: null, Seq: 1);

    private static SessionTab DirtyFileTab(string id, string path) =>
        new(id, TabKind.File, path, "Plain Text", IsDirty: true, Cursor: null, RecoveryRef: $"{id}", Seq: 2);

    private static SessionTab DirtyScratchTab(string id) =>
        new(id, TabKind.Scratch, Path: null, "Plain Text", IsDirty: true, Cursor: null, RecoveryRef: $"{id}", Seq: 2);

    private static RecoverySnapshot SnapshotFor(SessionTab tab, string content) =>
        new(
            TabId: tab.Id,
            Kind: tab.Kind,
            Path: tab.Path,
            Language: tab.Language,
            Cursor: null,
            SavedAt: DateTimeOffset.UnixEpoch,
            Seq: tab.Seq ?? 2,
            // Null baseline hash routes ReconcileOneAsync through the "session entry alone, probe
            // present-vs-deleted" branch — the on-disk hash-classify machinery is orthogonal to this test.
            BaselineContentHash: null,
            BaselineMtime: null,
            ContentBase64: SessionStore.EncodeContent(content));

    /// <summary>
    /// The ORDER-CRITICAL fixture (this is the exact shape the hostile review live-reproduced): a CLEAN
    /// tab with a saved path FIRST, a DIRTY tab with a saved path SECOND (dirty ⇒ has a recovery snapshot;
    /// under the pre-fix HashSet-seeded-from-snapshots bug, this dirty tab could enumerate AHEAD of the
    /// clean one), and a dirty SCRATCH buffer as a third entry to prove non-file tabs stay in place too.
    /// </summary>
    private static (SessionState Session, IReadOnlyList<RecoverySnapshot> Snapshots, SessionTab TabA, SessionTab TabB, SessionTab TabScratch)
        BuildMixedCleanDirtyFixture()
    {
        SessionTab tabA = CleanFileTab("tab-a-clean-first", "/proj/a.txt");   // saved FIRST, clean (no snapshot).
        SessionTab tabB = DirtyFileTab("tab-b-dirty-second", "/proj/b.txt"); // saved SECOND, dirty (has a snapshot).
        SessionTab tabScratch = DirtyScratchTab("tab-c-scratch-third");      // saved THIRD, dirty scratch (has a snapshot).

        var session = new SessionState(
            SessionState.CurrentSchemaVersion,
            OpenFolder: null,
            CanonicalOpenFolder: null,
            ActiveTabId: tabA.Id,
            Tabs: [tabA, tabB, tabScratch],
            NextSeq: 3);

        IReadOnlyList<RecoverySnapshot> snapshots =
        [
            SnapshotFor(tabB, "b unsaved content"),
            SnapshotFor(tabScratch, "scratch unsaved content"),
        ];

        return (session, snapshots, tabA, tabB, tabScratch);
    }

    [Fact]
    public async Task RestoreOrderExactlyEqualsSaveOrderForAMixOfCleanAndDirtyTabs()
    {
        (SessionState session, IReadOnlyList<RecoverySnapshot> snapshots, SessionTab tabA, SessionTab tabB, SessionTab tabScratch) =
            BuildMixedCleanDirtyFixture();

        var fs = new FakeRecoveryFileSystem();
        fs.SetProbe(tabA.Path!, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        fs.SetProbe(tabB.Path!, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        var reconciler = new SessionReconciler(fs);

        SessionReconcileResult result = await reconciler.ReconcileAsync(session, snapshots, CancellationToken.None);

        // The ORDERED list of restored tab identities EXACTLY equals the ordered list saved — the clean
        // tab (saved first) must NOT be jumped by the dirty tab (saved second), regardless of which one
        // has a recovery snapshot.
        Assert.Equal(
            [tabA.Id, tabB.Id, tabScratch.Id],
            result.Tabs.Select(t => t.TabId).ToArray());

        // ...and the ordered list of PATHS (incl. the scratch buffer's null path, "in place") matches too.
        Assert.Equal(
            new string?[] { tabA.Path, tabB.Path, null },
            result.Tabs.Select(t => t.Path).ToArray());
    }

    [Fact]
    public async Task RestoreOrderIsStableAcrossManyCleanDirtyPermutations()
    {
        // Property-style coverage: whichever of five tabs are dirty (has a snapshot) must never change the
        // restored order away from the SAVED session.Tabs array order.
        var tabs = new List<SessionTab>();
        var snapshots = new List<RecoverySnapshot>();
        var fs = new FakeRecoveryFileSystem();

        for (int i = 0; i < 5; i++)
        {
            string id = $"tab-{i}";
            string path = $"/proj/file{i}.txt";
            bool dirty = i % 2 == 1; // odd-indexed tabs are dirty (have a snapshot); even are clean.
            SessionTab tab = dirty ? DirtyFileTab(id, path) : CleanFileTab(id, path);
            tabs.Add(tab);
            fs.SetProbe(path, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
            if (dirty)
                snapshots.Add(SnapshotFor(tab, $"unsaved-{i}"));
        }

        var session = new SessionState(
            SessionState.CurrentSchemaVersion, OpenFolder: null, CanonicalOpenFolder: null,
            ActiveTabId: tabs[0].Id, Tabs: tabs, NextSeq: 10);

        var reconciler = new SessionReconciler(fs);
        SessionReconcileResult result = await reconciler.ReconcileAsync(session, snapshots, CancellationToken.None);

        Assert.Equal(tabs.Select(t => t.Id), result.Tabs.Select(t => t.TabId));
    }

    [Fact]
    public async Task SnapshotOnlyOrphansAreAppendedAfterEverySessionOrderedTabInADeterministicIdSortedTail()
    {
        // A tabId with a recovery snapshot but NO session.json entry (e.g. a crash before the very FIRST
        // persist ever ran) has no genuine save order to honor — it must be appended AFTER every
        // session-ordered tab, deterministically (sorted by id), never via filesystem enumeration order.
        SessionTab tabA = CleanFileTab("tab-a", "/proj/a.txt");
        var session = new SessionState(
            SessionState.CurrentSchemaVersion, OpenFolder: null, CanonicalOpenFolder: null,
            ActiveTabId: tabA.Id, Tabs: [tabA], NextSeq: 2);

        // Two snapshot-only orphans, deliberately inserted out of id-sort order in the snapshots list.
        var orphanZ = new RecoverySnapshot(
            TabId: "orphan-zzz", Kind: TabKind.Scratch, Path: null, Language: "Plain Text", Cursor: null,
            SavedAt: DateTimeOffset.UnixEpoch, Seq: 1, BaselineContentHash: null, BaselineMtime: null,
            ContentBase64: SessionStore.EncodeContent("z"));
        var orphanA = new RecoverySnapshot(
            TabId: "orphan-aaa", Kind: TabKind.Scratch, Path: null, Language: "Plain Text", Cursor: null,
            SavedAt: DateTimeOffset.UnixEpoch, Seq: 1, BaselineContentHash: null, BaselineMtime: null,
            ContentBase64: SessionStore.EncodeContent("a"));

        var fs = new FakeRecoveryFileSystem();
        fs.SetProbe(tabA.Path!, new CurrentDiskFile(Exists: true, ContentHash: null, Mtime: null));
        var reconciler = new SessionReconciler(fs);

        SessionReconcileResult result = await reconciler.ReconcileAsync(
            session, [orphanZ, orphanA], CancellationToken.None);

        Assert.Equal(["tab-a", "orphan-aaa", "orphan-zzz"], result.Tabs.Select(t => t.TabId).ToArray());
    }
}
