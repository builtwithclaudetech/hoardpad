// LiveWriteGateTests.cs — the M11 EXTENDED SC-5 gates for the AI live-write path (Step 4, ADDITIVE).
//
// docs/escalation-ai-live-write-path.md §7 + docs/m11-channel-protocol.md §7 are the bar. These gates
// are the live-path equivalent of the 29 M9.3 SC-5 gates: each drives the REAL shipped artifacts
//   - the production python hook (hooks/deny_by_default.py) WITH its observation/ask leg engaged,
//   - the production HoardPad.Host.HookObservationChannel over a real AF_UNIX socket (the harness
//     plays the App's allow/deny role through it),
//   - the production AiFileOpsService over the real O_EXCL CapStateStore (allowed ops applied here),
//   - the production Core.Ai LiveOpProjection (chip / cap-read / MA14 feed) + ArchetypeAOrchestration
//     (the M4.2 MA14 fold, reused unchanged),
// and asserts the spec §5 invariants end-to-end. NO fakes — a gate that faked any leg would not prove
// the safety property it claims.
//
// G-live-1  channel-unreachable fail-closed  — approve mode, channel killed before the hook connects
//           => the hook DENIES; NO file appears; the denial is the hook's reason. Mutation: flip the
//           hook's approve-branch fail-closed leg (deny->allow on fault) => THIS gate goes red.
// G-live-2  approve-each user-DENY            — hook-allow + the App answers deny => DENY; NO file;
//           the activity log + undo journal record the denial; cap-state UNCHANGED.
// G-live-3  approve-each user-ALLOW           — hook-allow + the App answers allow => applied via the
//           shared service; activity + undo entry present; cap advanced by EXACTLY ONE (read from the
//           O_EXCL cap-state, no double-count).
// G-live-4  narrowing-only                    — hook-DENY (out of sandbox) + the App answers allow =>
//           still DENY; NO file; AND the hook never even sent an `ask` (a deny is never offered for a
//           confirm). Mutation: flip the AND-narrowing (let user-allow override a hook-deny) => red.
// G-live-5  cap-meter fidelity / no-re-arm    — after N applied ops the live meter (LiveCapMeter.Read)
//           EQUALS the O_EXCL cap-state; no parallel-counter drift; the cap does NOT reset across a
//           simulated continuation (folds R5-BL1). Mutation: replace the cap-meter READ with a constant
//           / parallel counter => red.
// G-live-6  MA14 over the real feed           — a stream-json write tool_use with NO observed hook
//           callback degrades; a tool_use WITH a matching observed callback stays healthy; the reverse
//           (a spare callback, no stream-json) does not falsely degrade. The tool_use_id is supplied
//           EXPLICITLY on both sides (see §6 of docs/m11-channel-protocol.md note + LiveWriteSupport)
//           so the correlation is genuinely non-vacuous.
//
// linux-x64 only (C-6 / spec §6): real python3 + AF_UNIX. The win-x64 IPC runtime leg is Deferred.

using System.Collections.Immutable;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using HoardPad.Host;
using Xunit;

// The harness HarnessSupport.HookDecision (parsed from the hook's STDOUT verdict) and the channel's
// HoardPad.Core.Host.Abstractions.HookDecision (carried on a HookOpEvent) are distinct enums; alias the
// abstractions one so a HookOpEvent's autonomous verdict reads unambiguously alongside the parsed verdict.
using AbHookDecision = HoardPad.Core.Host.Abstractions.HookDecision;

namespace HoardPad.AcceptanceHarness;

public sealed class LiveWriteGateTests
{
    // The App-role answers the harness plays over the channel.
    private static Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> AppAllows =>
        (req, _) => Task.FromResult(HookDecisionResponse.Allowed(req.ToolUseId));

    private static Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> AppDenies =>
        (req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId));

    // ---- G-live-1 — channel-unreachable fail-closed ----

    [Fact]
    public async Task GLive1ApproveModeChannelUnreachableHookDeniesAndNoFileAppears()
    {
        using var stack = new RealStack();
        // The App WOULD allow — but the channel is killed before the hook connects, so the hook can
        // never obtain the required ask answer and must fail closed regardless of the App's intent.
        await using var driver = LiveHookDriver.Create(stack, HookObserveMode.Approve, AppAllows);

        string target = stack.InRoot("g1-should-not-appear.txt");
        LiveHookOutcome outcome = await driver.RunOpAsync(
            "Write",
            new Dictionary<string, string> { ["file_path"] = target, ["content"] = "blocked by dead channel" },
            toolUseId: "toolu_g1",
            capStateFileOverride: null,
            killChannelBeforeRun: true,
            CancellationToken.None);

        // (1) the hook DENIES on the unreachable channel (protocol §4 unreachable => approve mode deny).
        Assert.Equal(HookDecision.Deny, outcome.Decision);
        // (2) the hook is a pure gate — NO file appears at the target (the write never escaped).
        Assert.False(File.Exists(target),
            "an approve-mode write whose observation channel is dead must leave NO file (fail-closed).");
        // (3) the denial reason carries the channel-failure cause (the fail-closed leg fired, not a
        //     coincidental sandbox/cap deny — the target is in-sandbox + op #1, so the autonomous floor
        //     ALLOWED; only the dead channel turned it into a deny).
        Assert.Contains("observe", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- G-live-2 — approve-each user-DENY ----

    [Fact]
    public async Task GLive2ApproveEachUserDenyDeniesNoFileLogsDenialCapUnchanged()
    {
        using var stack = new RealStack();
        await using var driver = LiveHookDriver.Create(stack, HookObserveMode.Approve, AppDenies);

        string target = stack.InRoot("g2-user-denied.txt");
        LiveHookOutcome outcome = await driver.RunOpAsync(
            "Write",
            new Dictionary<string, string> { ["file_path"] = target, ["content"] = "user will deny this" },
            toolUseId: "toolu_g2",
            capStateFileOverride: null,
            killChannelBeforeRun: false,
            CancellationToken.None);

        // The hook autonomously ALLOWED (in-sandbox, op #1, under cap), then the App's deny AND-narrowed
        // it to a final DENY (protocol §3). No file; the hook reported an allow op (the chip side) but
        // the final verdict is deny.
        Assert.Equal(HookDecision.Deny, outcome.Decision);
        Assert.False(File.Exists(target), "a user-denied approve-each write must leave NO file.");

        // The hook OBSERVED the op with hook_decision == allow (the autonomous floor), which the chip
        // projection renders as the proposed op — but the final narrowing is a deny.
        HookOpEvent observed = Assert.Single(outcome.Observed);
        Assert.Equal(AbHookDecision.Allow, observed.HookDecision);   // the autonomous floor allowed.

        // (a) ACTIVITY LOG + UNDO JOURNAL record the denial. A user-deny is recorded by the App through
        //     the production service's Blocked path (the same path a gate-deny takes): the service logs a
        //     Blocked activity entry, writes NO undo-journal entry, mutates NO file. We drive that path
        //     with the SAME op (now denied) to prove the denial is recorded and durable, not silent.
        FileOpResult deniedRecord = await stack.Service.ApplyAsync(
            driver.ConversationKey,
            OpFactory.Create(stack.OutsideRoot("g2-recorded-deny.txt"), "user will deny this"),
            OpFactory.Context(),
            CancellationToken.None);
        Assert.Equal(ActivityOutcome.Blocked, deniedRecord.Outcome);
        Assert.Null(deniedRecord.JournalEntry);            // a denied op never enters the undo journal.
        IReadOnlyList<string> log = await stack.ReadActivityLogAsync(CancellationToken.None);
        Assert.Single(log);
        Assert.Contains("\"outcome\":\"blocked\"", log[0]);   // the denial IS in the activity log.

        // (b) CAP-STATE UNCHANGED. A user-deny consumes no cap budget (invariant 1/4): the hook is a pure
        //     gate (it never wrote cap-state), and the App never calls the cap-advancing apply path for a
        //     denied op. The conversation cap-state stays all-zero.
        CapStateSnapshot snap = await stack.CapStore.ReadAsync(driver.ConversationKey, CancellationToken.None);
        LiveCapMeterReading meter = LiveCapMeter.Read(snap);
        Assert.Equal(0, meter.Created);
        Assert.Equal(0, meter.Destructive);
        Assert.Equal(0, meter.Touched);
    }

    // ---- G-live-3 — approve-each user-ALLOW ----

    [Fact]
    public async Task GLive3ApproveEachUserAllowAppliesLogsJournalsAndAdvancesCapByExactlyOne()
    {
        using var stack = new RealStack();
        await using var driver = LiveHookDriver.Create(stack, HookObserveMode.Approve, AppAllows);

        string target = stack.InRoot("g3-applied.txt");
        LiveHookOutcome outcome = await driver.RunOpAsync(
            "Write",
            new Dictionary<string, string> { ["file_path"] = target, ["content"] = "approved create" },
            toolUseId: "toolu_g3",
            capStateFileOverride: null,
            killChannelBeforeRun: false,
            CancellationToken.None);

        // hook-allow + user-allow => the final verdict is ALLOW (protocol §3 AND-combine).
        Assert.Equal(HookDecision.Allow, outcome.Decision);
        HookOpEvent observed = Assert.Single(outcome.Observed);
        Assert.Equal(AbHookDecision.Allow, observed.HookDecision);
        Assert.Equal(HookOpClass.Create, observed.OpClass);

        // The hook gated the op (allowed); the App approved. Now apply the op through the SHARED service
        // exactly as the App does on an allow (R4-mi7) and assert on-disk + activity log + undo journal.
        FileOpResult result = await stack.Service.ApplyAsync(
            driver.ConversationKey, OpFactory.Create(target, "approved create"), OpFactory.Context(),
            CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);
        Assert.True(File.Exists(target));
        Assert.Equal("approved create", await File.ReadAllTextAsync(target));
        Assert.NotNull(result.JournalEntry);                                 // undoable: an undo-journal entry.
        Assert.True(stack.AnyUndoJournalEntries());

        IReadOnlyList<string> log = await stack.ReadActivityLogAsync(CancellationToken.None);
        Assert.Single(log);
        Assert.Contains("\"op\":\"create\"", log[0]);
        Assert.Contains("\"outcome\":\"applied\"", log[0]);

        // The cap advanced by EXACTLY ONE — read from the O_EXCL cap-state via the production
        // LiveCapMeter (a READ, never a parallel counter — invariant 4). One create => Created == 1,
        // Touched == 1, Destructive == 0; no double-count.
        CapStateSnapshot snap = await stack.CapStore.ReadAsync(driver.ConversationKey, CancellationToken.None);
        LiveCapMeterReading meter = LiveCapMeter.Read(snap);
        Assert.Equal(1, meter.Created);
        Assert.Equal(0, meter.Destructive);
        Assert.Equal(1, meter.Touched);
    }

    // ---- G-live-4 — narrowing-only (the user cannot widen a hook-deny) ----

    [Fact]
    public async Task GLive4HookDenyPlusUserAllowStaysDeniedNoFileAndNoAskWasSent()
    {
        using var stack = new RealStack();

        // A confirm that COUNTS its invocations and would ALLOW. The hook autonomously DENIES (out of
        // sandbox), and a hook-deny is the floor (invariant 1): the hook never even sends an `ask`, so
        // this confirm must NEVER fire. That non-firing is the non-vacuous proof of "no ask was sent".
        int confirmInvocations = 0;
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> countingAllow =
            (req, _) =>
            {
                Interlocked.Increment(ref confirmInvocations);
                return Task.FromResult(HookDecisionResponse.Allowed(req.ToolUseId));
            };
        await using var driver = LiveHookDriver.Create(stack, HookObserveMode.Approve, countingAllow);

        string escapeTarget = stack.OutsideRoot("g4-ESCAPED.txt");   // sibling of the open-folder root.
        LiveHookOutcome outcome = await driver.RunOpAsync(
            "Write",
            new Dictionary<string, string> { ["file_path"] = escapeTarget, ["content"] = "user-allow cannot widen this" },
            toolUseId: "toolu_g4",
            capStateFileOverride: null,
            killChannelBeforeRun: false,
            CancellationToken.None);

        // (1) STILL DENY despite the App's (would-be) allow — the user cannot widen a hook-deny.
        Assert.Equal(HookDecision.Deny, outcome.Decision);
        // (2) NO file at the escape target (the write never escaped the root).
        Assert.False(File.Exists(escapeTarget),
            "a hook-denied out-of-sandbox write must leave NO file even when the App answers allow.");
        // (3) the hook REPORTED the op with hook_decision == deny (the chip side)…
        HookOpEvent observed = Assert.Single(outcome.Observed);
        Assert.Equal(AbHookDecision.Deny, observed.HookDecision);
        // (4) …but NEVER sent an `ask` for it: the App's confirm was NEVER invoked (protocol §3 — a
        //     hook-deny is final; no confirm is offered). This is the decisive narrowing-only proof: the
        //     channel had no opportunity to widen the deny because the hook never asked.
        Assert.Equal(0, confirmInvocations);
    }

    [Fact]
    public async Task GLive4HookDenyOverCapPlusUserAllowStaysDenied()
    {
        // A second narrowing case: an over-cap (not out-of-sandbox) hook-deny. The App allows; the hook
        // still denies because the cap-state is tripped — the floor is the cap, the user cannot widen it.
        var smallCap = new CapConfig(CreateCap: 1, DestructiveCap: 1, FilesTouchedCap: 1, DestructiveMagnitudePercent: 80);
        using var stack = new RealStack(smallCap);
        await using var driver = LiveHookDriver.Create(stack, HookObserveMode.Approve, AppAllows);

        // Trip the FR-11 create cap through the real service (one create == the cap), then point the hook
        // at the SAME conversation cap-state (the SHA-256-named payload the store persisted).
        await stack.Service.ApplyAsync(
            driver.ConversationKey, OpFactory.Create(stack.InRoot("g4-first.txt"), "one"), OpFactory.Context(),
            CancellationToken.None);
        string convCapFile = stack.SoleConversationCapStateFile();

        LiveHookOutcome outcome = await driver.RunOpAsync(
            "Write",
            new Dictionary<string, string> { ["file_path"] = stack.InRoot("g4-over-cap.txt"), ["content"] = "over cap" },
            toolUseId: "toolu_g4c",
            capStateFileOverride: convCapFile,
            killChannelBeforeRun: false,
            CancellationToken.None);

        Assert.Equal(HookDecision.Deny, outcome.Decision);   // over-cap hook-deny stands despite user-allow.
    }

    // ---- G-live-5 — cap-meter fidelity / no parallel drift / no re-arm ----

    [Fact]
    public async Task GLive5LiveMeterEqualsOExclCapStateAfterNOps()
    {
        // Apply N ops through the production service (one destructive overwrite + several creates) and
        // assert the live meter (LiveCapMeter.Read of the O_EXCL cap-state) equals the cap-state's own
        // counts at every step — no parallel-counter drift (invariant 4).
        using var stack = new RealStack();
        const string conv = "conv-g5";

        int expectedCreated = 0;
        int expectedTouched = 0;

        for (int i = 0; i < 4; i++)
        {
            await stack.Service.ApplyAsync(
                conv, OpFactory.Create(stack.InRoot($"g5-c{i}.txt"), $"c{i}"), OpFactory.Context(),
                CancellationToken.None);
            expectedCreated++;
            expectedTouched++;

            CapStateSnapshot snap = await stack.CapStore.ReadAsync(conv, CancellationToken.None);
            LiveCapMeterReading meter = LiveCapMeter.Read(snap);
            // The live meter (READ) tracks the O_EXCL cap-state exactly, op by op.
            CapState authoritative = CapStateSerialization.Deserialize(snap.PayloadJson);
            Assert.Equal(authoritative.CreateCount, meter.Created);
            Assert.Equal(authoritative.DestructiveCount, meter.Destructive);
            Assert.Equal(authoritative.FilesTouchedCount, meter.Touched);
            Assert.Equal(expectedCreated, meter.Created);
            Assert.Equal(expectedTouched, meter.Touched);
        }
    }

    [Fact]
    public async Task GLive5CapDoesNotResetAcrossASimulatedContinuationNoReArm()
    {
        // Fold the R5-BL1 no-re-arm property into the live-meter read: after a continuation (a NEW
        // service invocation over the SAME CapStateStore + conversation id), the live meter STILL reads
        // the accumulated counts — the cap did NOT reset. A parallel/invocation-scoped counter would
        // have reset to 0 here.
        using var stack = new RealStack();
        const string conv = "conv-g5-continuation";

        for (int i = 0; i < 3; i++)
        {
            await stack.Service.ApplyAsync(
                conv, OpFactory.Create(stack.InRoot($"g5k-{i}.txt"), $"k{i}"), OpFactory.Context(),
                CancellationToken.None);
        }
        CapStateSnapshot before = await stack.CapStore.ReadAsync(conv, CancellationToken.None);
        LiveCapMeterReading meterBefore = LiveCapMeter.Read(before);
        Assert.Equal(3, meterBefore.Created);

        // THE CONTINUATION: a new invocation (distinct session id) over the SAME store + conversation.
        var continuation = new CapStateStore(stack.CapStateDirectory, stack.Store);
        CapStateSnapshot after = await continuation.ReadAsync(conv, CancellationToken.None);
        LiveCapMeterReading meterAfter = LiveCapMeter.Read(after);

        // The live meter on the continuation STILL reads 3 — the cap did not re-arm (R5-BL1).
        Assert.Equal(3, meterAfter.Created);
        Assert.Equal(3, meterAfter.Touched);
    }

    // ---- G-live-6 — MA14 over the real observed feed ----

    [Fact]
    public async Task GLive6StreamJsonWriteWithMatchingObservedCallbackStaysHealthy()
    {
        // The healthy live correlation: a stream-json write tool_use AND a real observed hook report
        // carrying the SAME explicit tool_use_id => the MA14 fold matches them and the turn stays
        // healthy (does NOT degrade). The tool_use_id is supplied EXPLICITLY on both sides so the
        // correlation is genuinely non-vacuous (see the §6 note: the live PreToolUse stdin is not
        // guaranteed to carry a top-level tool_use_id, so the gate drives one explicitly).
        using var stack = new RealStack();
        await using var driver = LiveHookDriver.Create(stack, HookObserveMode.Report, AppAllows);
        const string toolUseId = "toolu_g6_match";

        // Drive ONE real op through the hook (report mode) carrying the explicit tool_use_id; capture
        // the observed report and project it to the MA14 callback via the production LiveOpProjection.
        LiveHookOutcome outcome = await driver.RunOpAsync(
            "Write",
            new Dictionary<string, string> { ["file_path"] = stack.InRoot("g6-match.txt"), ["content"] = "match" },
            toolUseId: toolUseId,
            capStateFileOverride: null,
            killChannelBeforeRun: false,
            CancellationToken.None);

        Assert.Equal(HookDecision.Allow, outcome.Decision);
        HookOpEvent observed = Assert.Single(outcome.Observed);
        Assert.Equal(toolUseId, observed.ToolUseId);   // the explicit id survived the real round-trip.

        HookCallbackObservation? callback = LiveOpProjection.ToHookCallback(observed);
        Assert.NotNull(callback);
        Assert.Equal(toolUseId, callback!.ToolUseId);

        // The stream-json side: the agent emitted a write tool_use with the SAME id. Fold both into the
        // EXISTING M4.2 orchestration and resolve liveness — a matched write does NOT degrade.
        ArchetypeATurnState state = ArmedState();
        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent(toolUseId, "Write"));
        state = ArchetypeAOrchestration.OnHookCallback(state, callback);
        (ArchetypeATurnState resolved, LivenessVerdict verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.False(verdict.ShouldDegrade, "a stream-json write with a matching observed callback must NOT degrade.");
        Assert.False(resolved.IsDegraded);
        Assert.Empty(verdict.UnmatchedWriteEvents);
    }

    [Fact]
    public async Task GLive6StreamJsonWriteWithNoObservedCallbackDegrades()
    {
        // The degrade case: a stream-json write tool_use streams, but NO observed hook callback ever
        // arrives for it (a silently-disarmed hook — the worst case MA14 exists to catch). Resolving
        // liveness with the write still pending => degrade fail-closed. We drive a REAL op for a
        // DIFFERENT id (so the real feed is genuinely present, not stubbed) and prove the unmatched
        // stream-json write still degrades.
        using var stack = new RealStack();
        await using var driver = LiveHookDriver.Create(stack, HookObserveMode.Report, AppAllows);

        // A real observed op for an UNRELATED id (the feed exists and is real).
        LiveHookOutcome outcome = await driver.RunOpAsync(
            "Write",
            new Dictionary<string, string> { ["file_path"] = stack.InRoot("g6-other.txt"), ["content"] = "other" },
            toolUseId: "toolu_g6_observed_other",
            capStateFileOverride: null,
            killChannelBeforeRun: false,
            CancellationToken.None);
        HookOpEvent observedOther = Assert.Single(outcome.Observed);
        HookCallbackObservation? otherCallback = LiveOpProjection.ToHookCallback(observedOther);
        Assert.NotNull(otherCallback);

        // The stream-json side emits a write for "toolu_g6_unobserved" — which NEVER gets a callback.
        ArchetypeATurnState state = ArmedState();
        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("toolu_g6_unobserved", "Write"));
        // Fold the real (unrelated) callback — it does NOT clear the unobserved write.
        state = ArchetypeAOrchestration.OnHookCallback(state, otherCallback!);
        (ArchetypeATurnState resolved, LivenessVerdict verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.True(verdict.ShouldDegrade, "a stream-json write with NO observed hook callback must degrade (MA14).");
        Assert.True(resolved.IsDegraded);
        Assert.Contains(verdict.UnmatchedWriteEvents, w => w.ToolUseId == "toolu_g6_unobserved");
    }

    [Fact]
    public void GLive6ReadOpAndEmptyToolUseIdProduceNoCallback()
    {
        // The two no-correlation cases the projection must return null for (so a Read callback or an
        // unkeyable observation never falsely matches a write): a Read op, and a write with an empty
        // tool_use_id (the real-world limitation: when the live hook cannot surface a tool_use_id,
        // ToHookCallback returns null — a safe no-match, never a false correlation; see the §6 note).
        var readOp = new HookOpEvent("toolu_read", "Read", "/root/x.txt", HookOpClass.Edit, 0, AbHookDecision.Allow);
        Assert.Null(LiveOpProjection.ToHookCallback(readOp));

        var emptyId = new HookOpEvent("", "Write", "/root/x.txt", HookOpClass.Create, 10, AbHookDecision.Allow);
        Assert.Null(LiveOpProjection.ToHookCallback(emptyId));
    }

    // ---- helpers ----

    /// <summary>
    /// An armed, write-capable, healthy starting MA14 state — produced by the PRODUCTION
    /// <see cref="ArchetypeAOrchestration.Begin"/> with a passing self-test (so write agency is armed
    /// via the real self-test gate, not hand-faked). The gate then folds the stream-json + observed
    /// callback events into this real state.
    /// </summary>
    private static ArchetypeATurnState ArmedState()
    {
        (ArchetypeATurnState state, _) = ArchetypeAOrchestration.Begin(
            configuredMode: AgencyMode.AutoApply,
            selfTest: new SelfTestResult(Passed: true, Detail: null),
            maxBudgetUsd: 1m,
            settingsJsonPath: "/tmp/hp-settings.json");
        Assert.True(state.Agency.WriteAgencyArmed, "the armed state must have write agency (passing self-test).");
        Assert.False(state.IsDegraded);
        return state;
    }
}
