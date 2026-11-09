using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Records;
using HoardPad.Core.Undo;
using Xunit;

namespace HoardPad.Core.Ai.Tests.FileOps;

/// <summary>
/// M4.3 — the FR-39 pending-proposals queue + replay through the SAME shared file-ops service (architecture
/// §4.6), and the binding replay cap-state accounting: a user-initiated replay runs the full
/// sandbox + before-image + log/journal path but is governed by FR-10(b) per-op confirm, NOT the per-turn
/// caps — so it is NOT counted against them.
/// </summary>
public sealed class FileOpsReplayTests
{
    private const string OriginConversation = "conv-origin";
    private const string ReplayConversation = "conv-replay";
    private const string Session = "sess-1";

    private static readonly DateTimeOffset Ts = new(2026, 6, 20, 18, 4, 22, TimeSpan.Zero);

    private static FileOpContext Ctx(bool counts) => new(
        Ts: Ts,
        Backend: AiBackend.ClaudeCli,
        ConfiguredMode: AgencyMode.ApproveEach,
        AgencyMode: AgencyMode.ApproveEach,
        DowngradeReason: DowngradeReason.None,
        CountsAgainstPerTurnCaps: counts);

    private static (AiFileOpsService Svc, RecordingAtomicFileStore Store, InMemoryCapStateStore Caps)
        Build()
    {
        var store = new RecordingAtomicFileStore();
        var caps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(new FakeSandboxGate(), store, caps, new FakeAppDataPaths(), Session, CapConfig.Pinned);
        return (svc, store, caps);
    }

    private static FileOpRequest Create(string target, string undoRef = "u1") =>
        new(
            Operation: new FileOperation("turn-replay", FileOp.Create, new OpPaths(target)),
            Content: System.Text.Encoding.UTF8.GetBytes("body"),
            Classification: new OpClassificationInput(WriteTool.Write, TargetExists: false, 0, 4),
            BufferJournalId: "buf-1",
            UndoRef: undoRef,
            PriorContent: null,
            PreOpContentHash: null,
            PreOpMtime: null,
            BytesAfter: 4);

    // ============ The pending-proposals queue (FR-39 park / find / discard) =================================

    [Fact]
    public void PendingProposalsParkThenFindThenRemove()
    {
        var queue = PendingProposals.Empty;
        var proposal = new PendingProposal("p1", "turn-1", OriginConversation, "{\"file_path\":\"/root/a.md\"}");

        queue = queue.Park(proposal);
        Assert.Single(queue.Proposals);
        Assert.Equal(proposal, queue.Find("p1"));

        queue = queue.Remove("p1");
        Assert.Empty(queue.Proposals);
        Assert.Null(queue.Find("p1"));
    }

    // ============ Replay runs the full path but is NOT counted against the per-turn caps ===================

    [Fact]
    public async Task ReplayIsNotCountedAgainstPerTurnCaps()
    {
        // The crux: a user-initiated replay runs the full sandbox + before-image + log/journal path, but it
        // is governed by FR-10(b) per-op confirm — it must NOT touch the per-turn cap-state at all.
        var (svc, store, caps) = Build();

        FileOpResult result = await svc.ReplayAsync(
            ReplayConversation, Create("/root/parked.md"), Ctx(counts: true), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);  // the op still applies
        Assert.NotNull(result.JournalEntry);                    // still undoable (before-image/journal path ran)
        Assert.Contains("create:/root/parked.md", store.Mutations); // still mutated
        Assert.Null(result.CapDecision);                        // but NO cap decision — not counted
        // The cap-state store was never read or written for the replay conversation — the per-turn caps
        // were never consulted (the binding §4.6 accounting rule).
        Assert.Equal(0, caps.ReadCount.GetValueOrDefault(ReplayConversation));
        Assert.Equal(0, caps.WriteCount.GetValueOrDefault(ReplayConversation));
    }

    [Fact]
    public async Task ReplayForcesNotCountedEvenWhenCallerPassesCountsTrue()
    {
        // ReplayAsync overrides CountsAgainstPerTurnCaps to false regardless of the caller — a replay can
        // never burn (or be blocked by) a per-turn cap from a turn that already ended.
        var (svc, _, caps) = Build();

        // Pre-trip the ORIGIN conversation's cap-state so that, IF the replay were counted there, it would
        // be blocked. The replay runs under a NEW conversation and must be unaffected.
        await svc.ApplyAsync(OriginConversation, Create("/root/x.cs", "ux"), Ctx(counts: true), CancellationToken.None);
        int originWritesBefore = caps.WriteCount.GetValueOrDefault(OriginConversation);

        await svc.ReplayAsync(ReplayConversation, Create("/root/parked.md", "up"), Ctx(counts: true), CancellationToken.None);

        // The origin cap-state is untouched by the replay; the replay conversation's cap-state is never written.
        Assert.Equal(originWritesBefore, caps.WriteCount.GetValueOrDefault(OriginConversation));
        Assert.Equal(0, caps.WriteCount.GetValueOrDefault(ReplayConversation));
    }

    [Fact]
    public async Task ReplayStillEnforcesSandboxOutOfRootIsDenied()
    {
        // A replay is NOT a sandbox bypass — an out-of-root parked op still denies (the write never escapes
        // the root even on the replay path, FR-26).
        var (svc, store, _) = Build();

        FileOpResult result = await svc.ReplayAsync(
            ReplayConversation, Create("/etc/evil"), Ctx(counts: true), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Blocked, result.Outcome);
        Assert.Empty(store.Mutations);
    }

    [Fact]
    public async Task NormalApplyIsCountedContrastWithReplay()
    {
        // Contrast: a NORMAL (non-replay) apply DOES consult the per-turn caps for the same op.
        var (svc, _, caps) = Build();

        FileOpResult result = await svc.ApplyAsync(
            ReplayConversation, Create("/root/normal.md"), Ctx(counts: true), CancellationToken.None);

        Assert.NotNull(result.CapDecision);
        Assert.True(caps.ReadCount.GetValueOrDefault(ReplayConversation) >= 1);
        Assert.True(caps.WriteCount.GetValueOrDefault(ReplayConversation) >= 1);
    }

    // ============ MF1.4 — replay with a before-image: cap state must remain untouched ====================

    [Fact]
    public async Task ReplayWithBeforeImageIsNotCountedAgainstPerTurnCaps()
    {
        // An edit replay carries a PriorContent blob ref (the before-image was staged before the mutation).
        // Even with the full before-image / journal / mutation path executing, the cap-state must remain
        // unread and unwritten for the replay conversation (FR-39 §4.6 / architecture §4.6).
        var (svc, store, caps) = Build();

        var editWithBlob = new FileOpRequest(
            Operation: new FileOperation("replay-edit-blob", FileOp.Edit, new OpPaths("/root/existing.md")),
            Content: System.Text.Encoding.UTF8.GetBytes("new content"),
            Classification: null,        // replay — not cap-classified (FR-10(b) governs this op)
            BufferJournalId: "buf-blob",
            UndoRef: "u-blob",
            PriorContent: new BeforeImage("blob", "/data/undo/before/u-blob.bin"),
            PreOpContentHash: "sha256:before",
            PreOpMtime: Ts,
            BytesBefore: 80,
            BytesAfter: 11);

        FileOpResult result = await svc.ReplayAsync(
            ReplayConversation, editWithBlob, Ctx(counts: false), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);  // the edit still applies
        Assert.NotNull(result.JournalEntry);                    // the journal entry was written (undoable)
        // M15: the journal entry precedes the mutation.
        int journalIdx = store.Calls.FindIndex(c => c.StartsWith("append:/data/undo/u-blob.entry", StringComparison.Ordinal));
        int mutationIdx = store.Calls.FindIndex(c => c.StartsWith("replace:", StringComparison.Ordinal));
        Assert.True(journalIdx >= 0, "The journal append must precede the mutation (M15).");
        Assert.True(mutationIdx > journalIdx, "The replace must come after the journal append (M15 ordering).");
        // Cap-state was never touched for the replay conversation.
        Assert.Equal(0, caps.ReadCount.GetValueOrDefault(ReplayConversation));
        Assert.Equal(0, caps.WriteCount.GetValueOrDefault(ReplayConversation));
        Assert.Null(result.CapDecision);
    }

    // ============ Clobbering copy: records DestPriorContent; undo passes destCurrent =====================

    [Fact]
    public async Task ClobberingCopyRecordsDestPriorContentAndUndoPassesDestCurrentToDecision()
    {
        // A clobbering copy (dest pre-existed) must record destPriorContent + its hash in the journal entry,
        // so the undo path can pass the host's current read of the clobbered dest as the 3rd arg to
        // UndoReverseDecision.Decide — the destination-axis TOCTOU re-verify only fires when destCurrent is
        // supplied. (Move uses the same BuildJournalEntry path but is not yet supported at the store layer;
        // copy is the supported clobbering op at this milestone.)
        var (svc, store, _) = Build();

        var copy = new FileOpRequest(
            Operation: new FileOperation("turn-1", FileOp.Copy, new OpPaths("/root/a.md", Source: "/root/a.md", Dest: "/root/b.md")),
            Content: System.Text.Encoding.UTF8.GetBytes("copied content"),
            Classification: null,                       // copy via FR-39 replay, not the per-turn caps
            BufferJournalId: "buf-1",
            UndoRef: "um",
            PriorContent: new BeforeImage("blob", "before/um-src.bin"),
            PreOpContentHash: "sha256:src",
            PreOpMtime: Ts,
            DestPriorContent: new BeforeImage("blob", "before/um-dest.bin"),   // the CLOBBERED dest's prior bytes
            DestPriorContentHash: "sha256:dest-prior");

        FileOpResult result = await svc.ApplyAsync(OriginConversation, copy, Ctx(counts: true), CancellationToken.None);

        // The journal entry records the clobbered destination's before-image + hash (M10 / §9.4).
        UndoJournalEntry j = result.JournalEntry!;
        Assert.NotNull(j.DestPriorContent);
        Assert.Equal("sha256:dest-prior", j.DestPriorContentHash);

        // M15: the journal entry was durable before any mutation occurred.
        int journalIdx = store.Calls.FindIndex(c => c == "append:/data/undo/um.entry");
        Assert.True(journalIdx >= 0);

        // The undo path MUST pass destCurrent (the host's current read of the clobbered dest) — the dest-axis
        // re-verify (UndoReverseDecision step 3) only fires when destCurrent is supplied. Prove both verdicts:
        // (a) when the clobbered dest is unchanged → UndoCopy + RestoreClobberedDestination.
        var destUnchanged = new CurrentTargetState(Exists: true, ContentHash: "sha256:dest-prior", Mtime: Ts);
        var srcUnchanged = new CurrentTargetState(Exists: true, ContentHash: "sha256:src", Mtime: Ts);
        ReverseAction okAction = UndoReverseDecision.Decide(j, srcUnchanged, destUnchanged);
        Assert.Equal(ReverseActionKind.UndoCopy, okAction.Kind);
        Assert.True(okAction.RestoreClobberedDestination);

        // (b) when the clobbered dest was externally modified → RefuseReprompt (the re-verify caught it).
        var destChanged = new CurrentTargetState(Exists: true, ContentHash: "sha256:dest-DIFFERENT", Mtime: Ts);
        ReverseAction refuse = UndoReverseDecision.Decide(j, srcUnchanged, destChanged);
        Assert.Equal(ReverseActionKind.RefuseReprompt, refuse.Kind);

        // (c) the carry-forward guard, shown by contrast: when destCurrent is OMITTED (default Exists:false)
        // the dest-axis re-verify cannot run a real comparison and conservatively refuses — so a LEGITIMATE
        // unchanged-dest undo (case (a)) would be wrongly refused. That is precisely why the apply records
        // DestPriorContentHash and the undo MUST pass the host's real destCurrent read: omitting it breaks the
        // correct verdict. (Refusing-when-unknown is the fail-safe side, never a silent clobber.)
        ReverseAction omitted = UndoReverseDecision.Decide(j, srcUnchanged /* destCurrent omitted */);
        Assert.Equal(ReverseActionKind.RefuseReprompt, omitted.Kind); // omitted dest read → conservative refuse
    }
}
