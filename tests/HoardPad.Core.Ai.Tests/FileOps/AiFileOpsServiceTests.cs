using System.Text.Json;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using HoardPad.Core.Undo;
using Xunit;

namespace HoardPad.Core.Ai.Tests.FileOps;

/// <summary>
/// M4.3 — the single shared file-ops service (architecture §4.5, PRD FR-26/FR-28/FR-29/FR-39). This service
/// IS the sandbox enforcement for every AI-applied file op, so each FR-26 invariant is a test:
/// <list type="bullet">
/// <item>the write NEVER escapes the resolved root (sandbox-deny → no mutation, logged blocked);</item>
/// <item>every applied destructive op is undoable — the before-image/journal is durable BEFORE the
///   mutation (M15 ordering, proven by the recorded call order);</item>
/// <item>every applied op is logged (uniform §9.3 record + §9.4 journal entry, C-8);</item>
/// <item>an FR-39 replay runs the full path but is NOT counted against the per-turn caps;</item>
/// <item>a clobbering copy records DestPriorContent and its undo passes destCurrent to the decision;</item>
/// <item>Move/Delete refuse to return Applied when no mutation can be issued (MUST-FIX 1);</item>
/// <item>a mutation failure logs blocked, no raw throw (MUST-FIX 2a);</item>
/// <item>an activity-log append failure after a successful mutation returns AppliedButLogFailed, no raw throw (MUST-FIX 2b);</item>
/// <item>the cap compare-and-write conflict+retry loop accumulates every increment exactly once (MUST-FIX 3);</item>
/// <item>cap-state JSON is exactly the 6 PascalCase contract fields (MUST-FIX 4);</item>
/// <item>public ApplyAsync always counts, even when the caller passes CountsAgainstPerTurnCaps=false (SHOULD-FIX 5);</item>
/// <item>cap trip boundaries are pinned (exactly-at-cap vs one-past, SHOULD-FIX 6).</item>
/// </list>
/// </summary>
public sealed class AiFileOpsServiceTests
{
    private const string Conversation = "conv-1";
    private const string Session = "sess-1";

    private static readonly DateTimeOffset Ts = new(2026, 6, 20, 18, 4, 22, TimeSpan.Zero);

    private static FileOpContext Ctx(bool counts = true) => new(
        Ts: Ts,
        Backend: AiBackend.AnthropicApi,
        ConfiguredMode: AgencyMode.AutoApply,
        AgencyMode: AgencyMode.AutoApply,
        DowngradeReason: DowngradeReason.None,
        CountsAgainstPerTurnCaps: counts);

    private static (AiFileOpsService Svc, RecordingAtomicFileStore Store, InMemoryCapStateStore Caps, FakeSandboxGate Gate)
        Build(FakeSandboxGate? gate = null)
    {
        gate ??= new FakeSandboxGate();
        var store = new RecordingAtomicFileStore();
        var caps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(gate, store, caps, new FakeAppDataPaths(), Session, CapConfig.Pinned);
        return (svc, store, caps, gate);
    }

    private static FileOpRequest Create(string target, string content = "hi", string undoRef = "u1") =>
        new(
            Operation: new FileOperation("turn-1", FileOp.Create, new OpPaths(target)),
            Content: System.Text.Encoding.UTF8.GetBytes(content),
            Classification: new OpClassificationInput(WriteTool.Write, TargetExists: false, 0, content.Length),
            BufferJournalId: "buf-1",
            UndoRef: undoRef,
            PriorContent: null,
            PreOpContentHash: null,
            PreOpMtime: null,
            BytesBefore: 0,
            BytesAfter: content.Length);

    private static FileOpRequest Overwrite(string target, string content = "new", string undoRef = "u1") =>
        new(
            Operation: new FileOperation("turn-1", FileOp.Edit, new OpPaths(target)),
            Content: System.Text.Encoding.UTF8.GetBytes(content),
            Classification: new OpClassificationInput(WriteTool.Write, TargetExists: true, 100, content.Length),
            BufferJournalId: "buf-1",
            UndoRef: undoRef,
            PriorContent: new BeforeImage("blob", $"before/{undoRef}.bin"),
            PreOpContentHash: "sha256:old",
            PreOpMtime: Ts.AddMinutes(-1),
            BytesBefore: 100,
            BytesAfter: content.Length);

    // ============ Sandbox-deny: the write NEVER escapes the root ============================================

    [Fact]
    public async Task SandboxDenyDoesNotMutateAndLogsBlocked()
    {
        // An out-of-root target must be denied: NO create/replace call is ever issued (the write never
        // escapes the root, FR-26), and the op is logged blocked for the audit trail.
        var (svc, store, caps, _) = Build();

        FileOpResult result = await svc.ApplyAsync(
            Conversation, Create("/etc/passwd"), Ctx(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Blocked, result.Outcome);
        Assert.Empty(store.Mutations);                       // the load-bearing invariant: NO mutation issued
        Assert.Null(result.JournalEntry);                    // a blocked op never enters the undo journal
        Assert.Null(result.CapDecision);                     // a denied op never burns a cap
        Assert.Equal(ActivityOp.Blocked, result.ActivityRecord.Op);
        Assert.Equal(0, caps.WriteCount.GetValueOrDefault(Conversation)); // cap-state untouched on a deny
    }

    [Fact]
    public async Task SandboxDenyReasonReadsOutsideDocumentFolderNotSandbox()
    {
        // FS-3 Decision B (FINAL, 2026-07-10): the activity-log outcome reason drops "sandbox" wording —
        // the deny DECISION itself (asserted above) is unchanged, only its recorded reason text.
        var (svc, _, _, _) = Build();

        FileOpResult result = await svc.ApplyAsync(
            Conversation, Create("/etc/passwd"), Ctx(), CancellationToken.None);

        Assert.Equal("outside document folder: write denied (FR-26)", result.ActivityRecord.Error);
        Assert.DoesNotContain("sandbox", result.ActivityRecord.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SandboxDenyStillAppendsExactlyOneActivityRecord()
    {
        // The deny is logged (FR-29) — exactly one append, to the activity log, no journal append.
        var (svc, store, _, _) = Build();

        await svc.ApplyAsync(Conversation, Create("/outside.txt"), Ctx(), CancellationToken.None);

        Assert.Single(store.AppendCalls);
        Assert.StartsWith("append:/data/activity/", store.AppendCalls[0]);
    }

    [Fact]
    public async Task ClobberingMoveWithOutOfRootDestinationIsDeniedNoMutation()
    {
        // The destination of a move is ALSO a write target — an in-root source but out-of-root dest must
        // still be denied, or the op could overwrite a file outside the sandbox.
        var gate = new FakeSandboxGate();
        var (svc, store, _, _) = Build(gate);

        var req = new FileOpRequest(
            Operation: new FileOperation("turn-1", FileOp.Move, new OpPaths("/root/a.md", Source: "/root/a.md", Dest: "/etc/evil")),
            Content: ReadOnlyMemory<byte>.Empty,
            Classification: null,
            BufferJournalId: "buf-1",
            UndoRef: "u9",
            PriorContent: new BeforeImage("blob", "before/u9.bin"),
            PreOpContentHash: "sha256:src",
            PreOpMtime: Ts);

        FileOpResult result = await svc.ApplyAsync(Conversation, req, Ctx(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Blocked, result.Outcome);
        Assert.Empty(store.Mutations);
    }

    // ============ MUST-FIX 1: Move/Delete must NEVER return Applied with no mutation ====================

    [Fact]
    public async Task MoveOpThrowsNotSupportedNeverReturnsApplied()
    {
        // Move and Delete have no rename/unlink primitive in IAtomicFileStore M2.1. A silent no-op that
        // returns Applied while leaving the filesystem unchanged would corrupt the undo journal — a Move
        // journal entry with MoveBack action would be emitted for an op that never moved anything. The
        // service throws NotSupportedException so the caller error surfaces immediately rather than
        // producing a bogus Applied result. M6.x adds the real host seam leg.
        var (svc, store, _, _) = Build();

        var move = new FileOpRequest(
            Operation: new FileOperation("turn-1", FileOp.Move, new OpPaths("/root/a.md", Source: "/root/a.md", Dest: "/root/b.md")),
            Content: ReadOnlyMemory<byte>.Empty,
            Classification: null,
            BufferJournalId: "buf-1",
            UndoRef: "um",
            PriorContent: new BeforeImage("blob", "before/um.bin"),
            PreOpContentHash: "sha256:src",
            PreOpMtime: Ts);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => svc.ApplyAsync(Conversation, move, Ctx(), CancellationToken.None));

        // The load-bearing assertion: no mutation was applied.
        Assert.Empty(store.Mutations);
    }

    // ============ M15: before-image/journal durable BEFORE the mutation ====================================

    [Fact]
    public async Task DestructiveOverwriteAppendsJournalBeforeApplyingMutation()
    {
        // The M15 ordering proof: the undo-journal entry is appended (and fsync'd, per the store contract)
        // BEFORE the replace mutation. The recorded call order makes this observable.
        var (svc, store, _, _) = Build();

        await svc.ApplyAsync(Conversation, Overwrite("/root/todo.md"), Ctx(), CancellationToken.None);

        int journalIdx = store.Calls.FindIndex(c => c == "append:/data/undo/u1.entry");
        int mutateIdx = store.Calls.FindIndex(c => c == "replace:/root/todo.md");

        Assert.True(journalIdx >= 0, "the undo-journal entry must be appended");
        Assert.True(mutateIdx >= 0, "the overwrite must be applied");
        Assert.True(journalIdx < mutateIdx,
            $"M15: the journal append (idx {journalIdx}) must precede the mutation (idx {mutateIdx}). Trace: {string.Join(" | ", store.Calls)}");
    }

    [Fact]
    public async Task CreateAppliesViaCreateWhenAbsentAndJournalsForDeleteUndo()
    {
        // A create routes to CreateWhenAbsentAsync, and journals an entry (priorContent null) so undo can
        // delete the created file — the journal is still written before the create (uniform ordering).
        var (svc, store, _, _) = Build();

        FileOpResult result = await svc.ApplyAsync(Conversation, Create("/root/new.cs"), Ctx(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);
        Assert.Contains("create:/root/new.cs", store.Mutations);
        Assert.NotNull(result.JournalEntry);
        Assert.Null(result.JournalEntry!.PriorContent);  // create: nothing existed before
        int journalIdx = store.Calls.FindIndex(c => c == "append:/data/undo/u1.entry");
        int createIdx = store.Calls.FindIndex(c => c == "create:/root/new.cs");
        Assert.True(journalIdx < createIdx);
    }

    // ============ Uniform log/journal emission (C-8) =======================================================

    [Fact]
    public async Task AppliedOverwriteEmitsUniformActivityRecordAndJournalEntry()
    {
        var (svc, store, _, _) = Build();

        FileOpResult result = await svc.ApplyAsync(Conversation, Overwrite("/root/a.md", "newbody"), Ctx(), CancellationToken.None);

        // Activity record shape (§9.3).
        ActivityLogRecord r = result.ActivityRecord;
        Assert.Equal(ActivityOp.Edit, r.Op);
        Assert.Equal(ActivityOutcome.Applied, r.Outcome);
        Assert.Equal("/root/a.md", r.Paths.Target);
        Assert.Equal("u1", r.UndoRef);
        Assert.Equal(AiBackend.AnthropicApi, r.Backend);
        Assert.Equal(Ts, r.Ts);

        // Journal entry shape (§9.4) — matches the activity record's undoRef (the §9.3 join).
        UndoJournalEntry j = result.JournalEntry!;
        Assert.Equal("u1", j.UndoRef);
        Assert.Equal(FileOp.Edit, j.Op);
        Assert.Equal("buf-1", j.BufferJournalId);
        Assert.Equal("sha256:old", j.PreOpContentHash);
        Assert.NotNull(j.PriorContent);

        // Both append calls happened (journal + activity), to the right logs.
        Assert.Contains("append:/data/undo/u1.entry", store.AppendCalls);
        Assert.Contains("append:/data/activity/sess-1.log", store.AppendCalls);
    }

    // ============ Carry-forward: control-char path to ONE NDJSON line ======================================

    [Fact]
    public async Task PathWithEmbeddedNewlineProducesExactlyOneNdjsonRecordLine()
    {
        // M2.2 Gate-1 W3: a path containing a newline must serialize as a JSON-escaped string so it stays
        // ONE NDJSON record line — it cannot forge a second log record.
        var gate = new FakeSandboxGate();
        const string evilTarget = "/root/a\nFORGED";
        gate.Overrides[evilTarget] = HoardPad.Core.Safety.SandboxDecision.Allow;
        var (svc, store, _, _) = Build(gate);

        await svc.ApplyAsync(Conversation, Create(evilTarget, "x"), Ctx(), CancellationToken.None);

        // Read back the activity-log payload bytes.
        List<ReadOnlyMemory<byte>> activity = store.Appended["/data/activity/sess-1.log"];
        Assert.Single(activity);
        string text = RecordSerialization.ToText(activity[0]);
        // The raw payload (one record) contains NO literal newline — the newline is JSON-escaped (\n).
        Assert.DoesNotContain('\n', text);
        Assert.Contains("\\n", text); // the escaped form is present
        // And it parses as exactly one JSON object.
        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    // ============ Cap accounting on a counted apply ========================================================

    [Fact]
    public async Task CountedApplyRunsCapAccountingAndReturnsCapDecision()
    {
        var (svc, _, caps, _) = Build();

        FileOpResult result = await svc.ApplyAsync(Conversation, Create("/root/n1.cs"), Ctx(), CancellationToken.None);

        Assert.NotNull(result.CapDecision);
        Assert.False(result.CapDecision!.RequiresConfirm);              // first create is within the cap
        Assert.Equal(1, result.CapDecision.State.CreateCount);
        Assert.True(caps.WriteCount.GetValueOrDefault(Conversation) >= 1); // the cap-state was persisted
    }

    [Fact]
    public async Task CountedApplyPastCreateCapFlagsDowngradeReasonCreateCap()
    {
        // Drive past a small cap and assert the activity record's downgradeReason reflects the FR-11 trip.
        var smallCaps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(
            new FakeSandboxGate(), new RecordingAtomicFileStore(), smallCaps, new FakeAppDataPaths(), Session,
            new CapConfig(CreateCap: 2, DestructiveCap: 2, FilesTouchedCap: 10, DestructiveMagnitudePercent: 80));

        FileOpResult last = null!;
        for (int i = 1; i <= 3; i++)
            last = await svc.ApplyAsync(Conversation, Create($"/root/n{i}.cs", "x", $"u{i}"), Ctx(), CancellationToken.None);

        Assert.True(last.CapDecision!.RequiresConfirm);
        Assert.Equal(CapTrigger.CreateCap, last.CapDecision.Trigger);
        Assert.Equal(DowngradeReason.CreateCap, last.ActivityRecord.DowngradeReason);
    }

    // ============ MUST-FIX 2a: mutation failure blocked result, no raw throw ==========================

    [Fact]
    public async Task MutationThrowsAtomicWriteUnavailableLogsBlockedNoRawThrow()
    {
        // If the store throws AtomicWriteUnavailable on the mutation call, the service must:
        //   (a) NOT propagate the exception as a raw throw;
        //   (b) return Blocked (not Applied) — the filesystem is unchanged;
        //   (c) append exactly one blocked activity-log record (FR-29 audit still fires);
        //   (d) NOT emit an Applied journal entry (the mutation never happened).
        var store = new ThrowOnMutationStore();
        var caps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(new FakeSandboxGate(), store, caps, new FakeAppDataPaths(), Session, CapConfig.Pinned);

        // This must NOT throw — the exception is caught inside the service.
        FileOpResult result = await svc.ApplyAsync(Conversation, Create("/root/new.txt"), Ctx(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Blocked, result.Outcome);
        Assert.Null(result.JournalEntry);          // no successful mutation — no undoable journal entry returned
        Assert.False(result.ActivityLogWriteFailed); // the blocked record was logged without error
        // The activity-log append still happened (blocked result is logged).
        Assert.Contains(store.AppendCalls, c => c.StartsWith("append:/data/activity/", StringComparison.Ordinal));
    }

    // ============ MUST-FIX 2b: activity-log append failure after mutation AppliedButLogFailed =========

    [Fact]
    public async Task ActivityLogAppendFailureAfterMutationReturnsAppliedButLogFailed()
    {
        // If both attempts at the activity-log append fail AFTER a successful mutation, the service must:
        //   (a) NOT propagate the exception as a raw throw;
        //   (b) return Applied (the mutation DID happen — it is applied and undoable via the journal);
        //   (c) set ActivityLogWriteFailed = true so the host knows the audit record is missing.
        var store = new ThrowOnActivityLogStore { ActivityLogThrowCount = 2 };
        var caps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(new FakeSandboxGate(), store, caps, new FakeAppDataPaths(), Session, CapConfig.Pinned);

        FileOpResult result = await svc.ApplyAsync(Conversation, Create("/root/logfail.txt"), Ctx(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);  // the op IS applied
        Assert.NotNull(result.JournalEntry);                    // and journaled (undoable)
        Assert.True(result.ActivityLogWriteFailed);             // but the audit record is missing — host must warn
    }

    // ============ MUST-FIX 3: cap compare-and-write conflict+retry — no lost increment ==================

    [Fact]
    public async Task CapConflictRetryConvergesWithNoLostIncrement()
    {
        // AccountAsync's while(true) read-Account-WriteAsync(expectedVersion) retry loop must accumulate
        // every increment exactly once. This test injects 2 conflicts before allowing a commit, then
        // drives 3 creates through and asserts the final CreateCount is exactly 3 (no double-count, no
        // lost update) and the no-re-arm CreateCapTripped flag is still false (cap not reached, so it
        // must not be spuriously set by a stale read across the retry).
        var conflictingCaps = new ConflictingCapStateStore { ConflictsBeforeCommit = 2 };
        var svc = new AiFileOpsService(
            new FakeSandboxGate(), new RecordingAtomicFileStore(), conflictingCaps,
            new FakeAppDataPaths(), Session,
            new CapConfig(CreateCap: 10, DestructiveCap: 10, FilesTouchedCap: 20, DestructiveMagnitudePercent: 80));

        // Drive 3 creates through the service. The ConflictingCapStateStore injects 2 conflicts on the
        // first write attempt of each call, so AccountAsync must retry each time.
        for (int i = 1; i <= 3; i++)
            await svc.ApplyAsync(Conversation, Create($"/root/f{i}.txt", "x", $"u{i}"), Ctx(), CancellationToken.None);

        // Read the final persisted state directly from the store.
        CapStateSnapshot snapshot = await conflictingCaps.ReadAsync(Conversation, CancellationToken.None);
        CapState final = HoardPad.Core.Ai.FileOps.CapStateSerialization.Deserialize(snapshot.PayloadJson);

        Assert.Equal(3, final.CreateCount);           // every increment is present exactly once — no lost update
        Assert.Equal(3, final.FilesTouched.Count);    // the FR-40 set has 3 distinct paths
        Assert.False(final.CreateCapTripped);         // cap = 10, count = 3 — must NOT be tripped
    }

    // ============ MUST-FIX 4: cap-state JSON is exactly the 6 PascalCase contract fields ===============

    [Fact]
    public void CapStateSerializationProducesExactlySixPascalCaseContractFields()
    {
        // M5.1 Python deny-by-default hook reads the cap-state file as PascalCase JSON. The serialized
        // payload MUST contain exactly the 6 contract fields — no extra computed properties like
        // FilesTouchedCount (which would break the cross-language contract). FilesTouched must be an
        // array (the ordinal set members), not a count integer.
        var state = new CapState(
            CreateCount: 1,
            DestructiveCount: 2,
            FilesTouched: System.Collections.Immutable.ImmutableHashSet.Create(StringComparer.Ordinal, "/root/a.cs"),
            CreateCapTripped: false,
            DestructiveCapTripped: false,
            FilesTouchedCapTripped: false);

        string json = HoardPad.Core.Ai.FileOps.CapStateSerialization.Serialize(state);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // Collect the actual property names in the JSON.
        HashSet<string> actualNames = root.EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The exact six PascalCase contract fields.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "CreateCount", "DestructiveCount", "FilesTouched",
            "CreateCapTripped", "DestructiveCapTripped", "FilesTouchedCapTripped"
        };

        Assert.Equal(expected, actualNames);

        // FilesTouched is an array (the set members), not a count integer.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("FilesTouched").ValueKind);
        Assert.Equal(1, root.GetProperty("FilesTouched").GetArrayLength());
        Assert.Equal("/root/a.cs", root.GetProperty("FilesTouched").EnumerateArray().First().GetString());

        // Ordinal rebuild on deserialize: same file path maps to count 1, not double-counted.
        CapState roundTripped = HoardPad.Core.Ai.FileOps.CapStateSerialization.Deserialize(json);
        Assert.Single(roundTripped.FilesTouched);
        Assert.Equal(1, roundTripped.FilesTouchedCount); // derived property correct post round-trip
    }

    // ============ SHOULD-FIX 5: public ApplyAsync always counts; caller cannot opt out ==================

    [Fact]
    public async Task PublicApplyAlwaysCountsEvenWhenCallerPassesCountsFalse()
    {
        // The public ApplyAsync forces CountsAgainstPerTurnCaps=true, regardless of the caller. A caller
        // cannot silently bypass the per-turn caps by passing counts:false through the public surface.
        // Only ReplayAsync reaches the internal not-counted path.
        var (svc, _, caps, _) = Build();

        // Pass counts:false explicitly — must still be counted.
        FileOpResult result = await svc.ApplyAsync(
            Conversation, Create("/root/test.cs"), Ctx(counts: false), CancellationToken.None);

        Assert.NotNull(result.CapDecision);           // cap decision IS present — it was counted
        Assert.True(caps.ReadCount.GetValueOrDefault(Conversation) >= 1);
        Assert.True(caps.WriteCount.GetValueOrDefault(Conversation) >= 1);
    }

    // ============ SHOULD-FIX 6: cap trip boundaries: exactly-at-cap vs one-past =======================
    //
    // Asymmetry (documented-intentional): FR-40 trips on >= cap (the Nth DISTINCT file), while
    // Create/Destructive trip on > cap (the (cap+1)-th op). This is the MA12 "reach-or-exceed" spec for
    // FR-40 vs the "exceed" spec for FR-11/FR-38. Tests for each side pin the boundary.
    //
    // FR-40 uses >=  : filesTouchedCapTripped when filesTouched.Count reaches FilesTouchedCap.
    // FR-11 uses >   : createCapTripped when createCount exceeds CreateCap (i.e. on the cap+1 op).

    [Fact]
    public async Task CreateCapTripsBoundaryExactlyAtCapPlusOne()
    {
        // FR-11: CreateCap = 2. Creates #1 and #2 are within the cap (RequiresConfirm = false);
        // create #3 (cap+1) trips it (RequiresConfirm = true).
        var caps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(
            new FakeSandboxGate(), new RecordingAtomicFileStore(), caps, new FakeAppDataPaths(), Session,
            new CapConfig(CreateCap: 2, DestructiveCap: 20, FilesTouchedCap: 20, DestructiveMagnitudePercent: 80));

        FileOpResult r1 = await svc.ApplyAsync(Conversation, Create("/root/a.cs", "x", "u1"), Ctx(), CancellationToken.None);
        FileOpResult r2 = await svc.ApplyAsync(Conversation, Create("/root/b.cs", "x", "u2"), Ctx(), CancellationToken.None);
        FileOpResult r3 = await svc.ApplyAsync(Conversation, Create("/root/c.cs", "x", "u3"), Ctx(), CancellationToken.None);

        Assert.False(r1.CapDecision!.RequiresConfirm); // #1 — within cap
        Assert.False(r2.CapDecision!.RequiresConfirm); // #2 — exactly at cap — NOT yet tripped (> not >=)
        Assert.True(r3.CapDecision!.RequiresConfirm);  // #3 — cap+1 — tripped
        Assert.Equal(CapTrigger.CreateCap, r3.CapDecision.Trigger);
    }

    [Fact]
    public async Task FilesTouchedCapTripsBoundaryAtExactCapCount()
    {
        // FR-40: FilesTouchedCap = 3. The Nth DISTINCT file (n == cap) trips the flag (>= semantics).
        // Touches #1 and #2 are within; touch #3 (= cap) trips it.
        var caps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(
            new FakeSandboxGate(), new RecordingAtomicFileStore(), caps, new FakeAppDataPaths(), Session,
            new CapConfig(CreateCap: 20, DestructiveCap: 20, FilesTouchedCap: 3, DestructiveMagnitudePercent: 80));

        // Create three distinct files (each a different canonical path — three distinct FR-40 entries).
        FileOpResult r1 = await svc.ApplyAsync(Conversation, Create("/root/f1.cs", "x", "u1"), Ctx(), CancellationToken.None);
        FileOpResult r2 = await svc.ApplyAsync(Conversation, Create("/root/f2.cs", "x", "u2"), Ctx(), CancellationToken.None);
        FileOpResult r3 = await svc.ApplyAsync(Conversation, Create("/root/f3.cs", "x", "u3"), Ctx(), CancellationToken.None);

        Assert.False(r1.CapDecision!.RequiresConfirm); // 1 distinct file — within cap
        Assert.False(r2.CapDecision!.RequiresConfirm); // 2 distinct files — within cap
        Assert.True(r3.CapDecision!.RequiresConfirm);  // 3 distinct files = FilesTouchedCap — TRIPPED (>=)
        Assert.Equal(CapTrigger.FilesTouchedCap, r3.CapDecision.Trigger);
    }

    [Fact]
    public async Task DestructiveCapTripsBoundaryExactlyAtCapPlusOne()
    {
        // FR-38: DestructiveCap = 2. Overwrites #1 and #2 are within; #3 (cap+1) trips it.
        var caps = new InMemoryCapStateStore();
        var svc = new AiFileOpsService(
            new FakeSandboxGate(), new RecordingAtomicFileStore(), caps, new FakeAppDataPaths(), Session,
            new CapConfig(CreateCap: 20, DestructiveCap: 2, FilesTouchedCap: 20, DestructiveMagnitudePercent: 80));

        FileOpResult r1 = await svc.ApplyAsync(Conversation, Overwrite("/root/a.md", "new1", "u1"), Ctx(), CancellationToken.None);
        FileOpResult r2 = await svc.ApplyAsync(Conversation, Overwrite("/root/b.md", "new2", "u2"), Ctx(), CancellationToken.None);
        FileOpResult r3 = await svc.ApplyAsync(Conversation, Overwrite("/root/c.md", "new3", "u3"), Ctx(), CancellationToken.None);

        Assert.False(r1.CapDecision!.RequiresConfirm);
        Assert.False(r2.CapDecision!.RequiresConfirm);
        Assert.True(r3.CapDecision!.RequiresConfirm);
        Assert.Equal(CapTrigger.DestructiveCap, r3.CapDecision.Trigger);
    }
}
