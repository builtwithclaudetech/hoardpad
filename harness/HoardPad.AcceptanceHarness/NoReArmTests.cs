// NoReArmTests.cs — the R5-BL1 cross-continuation no-re-arm step (M9.3).
//
// This is the deterministic executor for FR-40's cap-state-lifetime property (PRD §5 SC-5(i)
// no-re-arm clause, FR-40 Done-when). It proves — with NO live LLM — that a tripped cap does NOT
// re-arm across a HoardPad-initiated continuation turn:
//
//   1. A first BATCH of crafted ops is driven through the REAL AiFileOpsService (over the REAL
//      O_EXCL CapStateStore) under a conversation id, tripping the FR-40 distinct-files cap.
//   2. A SECOND service instance (a NEW invocation — distinct session id, the continuation turn)
//      is constructed over the SAME CapStateStore directory + the SAME conversation id (the
//      conversation-keyed cap-state the host persists across continuations). A further distinct-file
//      op into that resumed state STILL confirms (RequiresConfirm == true) — the (N+1)th cumulative
//      distinct file did NOT re-arm. An invocation-scoped counter would have reset here.
//   3. A NEW user-initiated conversation id (a fresh CapState.Empty) resets — its first op is under cap.
//   4. The substitution-after-pause assertion (R5-mi3): a non-allowlisted tool (AskUserQuestion /
//      ToolSearch) stays denied by the REAL hook after the FR-40 pause, against the tripped cap-state.
//
// The "same conversation id, new invocation" is the load-bearing simulation: because "continue" is a
// NEW `claude -p` invocation with a distinct session_id and no cross-call hook memory, only HoardPad's
// conversation-keyed persistence keeps the cap tripped. We model that by constructing a fresh
// AiFileOpsService (a new invocation) bound to the same CapStateStore + conversation id.

using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Records;
using HoardPad.Host;
using Xunit;

namespace HoardPad.AcceptanceHarness;

public sealed class NoReArmTests
{
    // A small FR-40 cap keeps the batch fast while preserving the exact off-by-one semantics
    // (the Nth distinct file trips; the (N+1)th still confirms). Create cap kept generous so the
    // FR-40 aggregate is the cap under test, not FR-11.
    private static readonly CapConfig SmallFr40 = new(
        CreateCap: 100, DestructiveCap: 100, FilesTouchedCap: 3, DestructiveMagnitudePercent: 80);

    // ---- the cross-continuation no-re-arm property ----

    [Fact]
    public async Task Fr40StaysTrippedAcrossASimulatedContinuationOnTheSameConversationId()
    {
        using var stack = new RealStack(SmallFr40);
        const string conv = "conv-user-initiated-1";

        // BATCH 1 (the first invocation): create FilesTouchedCap distinct files. The 3rd distinct file
        // (== cap) trips FR-40 (files_after_count >= cap), so it RequiresConfirm.
        CapDecision? lastBatch1 = null;
        for (int i = 0; i < SmallFr40.FilesTouchedCap; i++)
        {
            string target = stack.InRoot($"b1-{i}.txt");
            FileOpResult r = await stack.Service.ApplyAsync(
                conv, OpFactory.Create(target, $"b1-{i}"), OpFactory.Context(), CancellationToken.None);
            Assert.Equal(ActivityOutcome.Applied, r.Outcome);
            lastBatch1 = r.CapDecision;
        }
        Assert.NotNull(lastBatch1);
        Assert.True(lastBatch1!.RequiresConfirm, "the Nth distinct file (== FR-40 cap) must trip and require a confirm.");
        Assert.Equal(CapTrigger.FilesTouchedCap, lastBatch1.Trigger);
        Assert.True(lastBatch1.State.FilesTouchedCapTripped, "the FR-40 tripped flag must be sticky (persisted to cap-state).");

        // SIMULATE THE CONTINUATION: a NEW invocation (distinct session id) resuming the SAME
        // conversation. It binds to the SAME CapStateStore directory + the SAME conversation id, so the
        // host's conversation-keyed persistence is what carries the tripped state across the new invocation.
        var continuationService = new AiFileOpsService(
            stack.Gate, stack.Store, new CapStateStore(stack.CapStateDirectory, stack.Store),
            stack.Paths, "sess-continuation-" + Guid.NewGuid().ToString("N"), SmallFr40);

        // BATCH 2 (the continuation): the (N+1)th cumulative distinct file. If the cap had re-armed on
        // the new invocation, this would be op #1 of a fresh count and would be UNDER cap (no confirm).
        // Because the conversation-keyed state stayed tripped, it STILL confirms.
        FileOpResult cont = await continuationService.ApplyAsync(
            conv, OpFactory.Create(stack.InRoot("b2-continuation.txt"), "n+1"), OpFactory.Context(), CancellationToken.None);
        Assert.Equal(ActivityOutcome.Applied, cont.Outcome);
        Assert.NotNull(cont.CapDecision);
        Assert.True(cont.CapDecision!.RequiresConfirm,
            "the (N+1)th cumulative distinct file across the continuation must STILL confirm — the cap did NOT re-arm (R5-BL1).");
        Assert.True(cont.CapDecision.State.FilesTouchedCapTripped);
    }

    [Fact]
    public async Task ANewUserInitiatedConversationIdResetsSoTheFirstOpIsUnderCap()
    {
        using var stack = new RealStack(SmallFr40);

        // Trip the cap on conversation A.
        for (int i = 0; i < SmallFr40.FilesTouchedCap; i++)
        {
            await stack.Service.ApplyAsync(
                "conv-A", OpFactory.Create(stack.InRoot($"a-{i}.txt"), $"a-{i}"), OpFactory.Context(), CancellationToken.None);
        }

        // A NEW user-initiated conversation (conv-B) starts from CapState.Empty: its first op is UNDER cap.
        FileOpResult first = await stack.Service.ApplyAsync(
            "conv-B", OpFactory.Create(stack.InRoot("b-first.txt"), "b"), OpFactory.Context(), CancellationToken.None);
        Assert.NotNull(first.CapDecision);
        Assert.False(first.CapDecision!.RequiresConfirm,
            "a NEW user-initiated conversation resets the cap-state — its first op is under cap.");
        Assert.False(first.CapDecision.State.FilesTouchedCapTripped);
    }

    // ---- the substitution-after-pause assertion (R5-mi3) ----

    [Fact]
    public async Task ANonAllowlistedSubstituteToolStaysDeniedByTheRealHookAfterTheFr40Pause()
    {
        // After an FR-40 pause, an agent might try to SUBSTITUTE a different tool to keep writing.
        // The deny-by-default allowlist denies every non-allowlisted tool regardless of cap-state — so
        // AskUserQuestion / ToolSearch (both live in 2.1.179) stay denied after the pause. We drive the
        // REAL hook with a cap-state file pre-tripped at FR-40 and assert the substitution is denied.
        using var stack = new RealStack(SmallFr40);

        // Pre-trip the FR-40 cap-state via the real store (a conversation already paused at the cap).
        for (int i = 0; i < SmallFr40.FilesTouchedCap; i++)
        {
            await stack.Service.ApplyAsync("conv-paused",
                OpFactory.Create(stack.InRoot($"p{i}.txt"), i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                OpFactory.Context(), CancellationToken.None);
        }

        // The substituted tool is denied at the allowlist step (before any cap check), so it stays denied
        // after the pause.
        foreach (string substitute in new[] { "AskUserQuestion", "ToolSearch" })
        {
            string ev = HookDriver.BuildEvent(substitute, new Dictionary<string, string>
            {
                ["file_path"] = stack.InRoot("substitute.txt"),
                ["content"] = "post-pause substitution",
            });
            HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(SmallFr40), CancellationToken.None);
            Assert.Equal(HookDecision.Deny, outcome.Decision);
        }
    }

    // ---- the hook itself denies a further in-sandbox create once FR-40 is tripped in cap-state ----

    [Fact]
    public async Task TheRealHookDeniesAFurtherCreateAgainstATrippedFr40CapState()
    {
        // The end-to-end no-re-arm proof through the PYTHON hook (the production enforcer): once the
        // conversation cap-state has the FR-40 flag tripped, the hook denies a further in-sandbox create
        // — confirming the C# service and the python hook agree on the tripped state read from the SAME
        // conversation-keyed cap-state file.
        using var stack = new RealStack(SmallFr40);

        for (int i = 0; i < SmallFr40.FilesTouchedCap; i++)
        {
            await stack.Service.ApplyAsync("conv-hook",
                OpFactory.Create(stack.InRoot($"h{i}.txt"), i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                OpFactory.Context(), CancellationToken.None);
        }

        // The service persisted the conversation cap-state under a SHA-256-derived leaf in the cap-state
        // dir. There is exactly one persisted conversation in this fresh stack, so the single .capstate
        // payload file IS conv-hook's state — point the hook env at it so the hook sees the tripped FR-40.
        string convCapFile = stack.SoleConversationCapStateFile();

        Dictionary<string, string?> env = stack.HookEnv(SmallFr40);
        env["HOARDPAD_CAP_STATE_FILE"] = convCapFile;

        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = stack.InRoot("post-trip.txt"),
            ["content"] = "should be denied: FR-40 tripped",
        });
        HookOutcome outcome = await HookDriver.RunAsync(ev, env, CancellationToken.None);
        Assert.Equal(HookDecision.Deny, outcome.Decision);
        Assert.Contains("FR-40", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
