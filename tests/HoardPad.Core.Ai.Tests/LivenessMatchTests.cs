using System.Collections.Immutable;
using HoardPad.Core.Ai.Provider;
using Xunit;
// StreamJsonTaxonomy is in the same namespace — no extra using needed.

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.2 — the MA14 liveness-match (TD §4.7, PRD FR-26 per-op liveness, SPIKE-3 I): correlate each stream-json
/// WRITE tool-use against an observed per-op hook callback by <c>tool_use_id</c>; an UNMATCHED write tool-use
/// (a write the hook never gated ⇒ the sandbox may be silently disarmed) fires the fail-closed degrade. All pure
/// — synthetic event lists, no harness, no subprocess, no clock.
/// </summary>
public sealed class LivenessMatchTests
{
    private static WriteToolUseEvent Write(string id, string tool = "Write") => new(id, tool);
    private static HookCallbackObservation Callback(string id) => new(id);

    // ---- The core MA14 property: unmatched write ⇒ degrade; fully matched ⇒ no degrade --------------------

    [Fact]
    public void FullyMatchedWriteListDoesNotDegrade()
    {
        var writes = new[] { Write("tu_1"), Write("tu_2", "Edit"), Write("tu_3") };
        var callbacks = new[] { Callback("tu_1"), Callback("tu_2"), Callback("tu_3") };

        var verdict = LivenessMatch.Evaluate(writes, callbacks);

        Assert.False(verdict.ShouldDegrade);
        Assert.Empty(verdict.UnmatchedWriteEvents);
    }

    [Fact]
    public void OneUnmatchedWriteFiresTheDegrade()
    {
        // tu_2 was emitted as a write tool-use but the hook never fired for it — the silent-disarm signal.
        var writes = new[] { Write("tu_1"), Write("tu_2"), Write("tu_3") };
        var callbacks = new[] { Callback("tu_1"), Callback("tu_3") };

        var verdict = LivenessMatch.Evaluate(writes, callbacks);

        Assert.True(verdict.ShouldDegrade);
        var unmatched = Assert.Single(verdict.UnmatchedWriteEvents);
        Assert.Equal("tu_2", unmatched.ToolUseId);
    }

    [Fact]
    public void EveryWriteUnmatchedDegradesAndListsThemAll()
    {
        // A wholesale silent disarm: the hook stopped firing entirely mid-turn (TD §4.7 — no negative CLI signal).
        var writes = new[] { Write("tu_1"), Write("tu_2") };
        var callbacks = Array.Empty<HookCallbackObservation>();

        var verdict = LivenessMatch.Evaluate(writes, callbacks);

        Assert.True(verdict.ShouldDegrade);
        Assert.Equal(2, verdict.UnmatchedWriteEvents.Length);
    }

    [Fact]
    public void NoWritesNoCallbacksDoesNotDegrade()
    {
        // A read-only / chat turn emits no write tool-uses — nothing to gate, nothing to degrade.
        var verdict = LivenessMatch.Evaluate(
            Array.Empty<WriteToolUseEvent>(), Array.Empty<HookCallbackObservation>());

        Assert.False(verdict.ShouldDegrade);
        Assert.Empty(verdict.UnmatchedWriteEvents);
    }

    [Fact]
    public void SpareHookCallbackWithNoMatchingWriteIsHarmless()
    {
        // A callback for a Read gate (or any id that is not an emitted write) must not falsely satisfy or fail
        // the match — the verdict is directional: every WRITE needs a callback; a spare callback is ignored.
        var writes = new[] { Write("tu_1") };
        var callbacks = new[] { Callback("tu_1"), Callback("tu_read_999") };

        var verdict = LivenessMatch.Evaluate(writes, callbacks);

        Assert.False(verdict.ShouldDegrade);
    }

    [Fact]
    public void DuplicateCallbackForOneWriteStillMatches()
    {
        // Defensive: a doubled callback for the same id cannot turn a matched write into an unmatched one.
        var writes = new[] { Write("tu_1") };
        var callbacks = new[] { Callback("tu_1"), Callback("tu_1") };

        var verdict = LivenessMatch.Evaluate(writes, callbacks);

        Assert.False(verdict.ShouldDegrade);
    }

    // ---- The orchestration-level MA14 path: streamed events ⇒ DegradeFailClosed action --------------------

    [Fact]
    public void OrchestrationUnmatchedWriteDegradesFailClosedAtLivenessCheckpoint()
    {
        var (state, _) = BeginArmedTurn();

        // The agent emits two writes; the hook fires for only the first.
        state = ArchetypeAOrchestration.OnWriteToolUse(state, Write("tu_1"));
        state = ArchetypeAOrchestration.OnWriteToolUse(state, Write("tu_2"));
        state = ArchetypeAOrchestration.OnHookCallback(state, Callback("tu_1"));

        // Before the checkpoint the turn is still continuing (tu_2's callback could still arrive on the stream).
        Assert.Equal(TurnAction.Continue, state.Action);

        // At the liveness checkpoint (stream end), tu_2 is still pending ⇒ fail closed.
        var (resolved, verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.Equal(TurnAction.DegradeFailClosed, resolved.Action);
        Assert.Equal(DegradeCause.UnmatchedWriteToolUse, resolved.Cause);
        Assert.True(resolved.IsDegraded);
        Assert.True(verdict.ShouldDegrade);
        var unmatched = Assert.Single(verdict.UnmatchedWriteEvents);
        Assert.Equal("tu_2", unmatched.ToolUseId);
    }

    [Fact]
    public void OrchestrationAllWritesMatchedStaysContinue()
    {
        var (state, _) = BeginArmedTurn();

        state = ArchetypeAOrchestration.OnWriteToolUse(state, Write("tu_1"));
        state = ArchetypeAOrchestration.OnWriteToolUse(state, Write("tu_2"));
        state = ArchetypeAOrchestration.OnHookCallback(state, Callback("tu_1"));
        state = ArchetypeAOrchestration.OnHookCallback(state, Callback("tu_2"));

        var (resolved, verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.Equal(TurnAction.Continue, resolved.Action);
        Assert.Equal(DegradeCause.None, resolved.Cause);
        Assert.False(verdict.ShouldDegrade);
    }

    [Fact]
    public void OrchestrationCallbackBeforeWriteEventMatchesOrderIndependently()
    {
        // Stream ordering across the two channels (stdout stream vs hook-observation) is not guaranteed; a hook
        // callback observed BEFORE its tool-use event must still match — a benign reordering must not false-degrade.
        var (state, _) = BeginArmedTurn();

        state = ArchetypeAOrchestration.OnHookCallback(state, Callback("tu_1")); // callback first
        state = ArchetypeAOrchestration.OnWriteToolUse(state, Write("tu_1"));    // then the write event

        var (resolved, verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.False(verdict.ShouldDegrade);
        Assert.Equal(TurnAction.Continue, resolved.Action);
    }

    // ---- Cheap hardening: a forged/spare callback cannot launder a genuinely-unmatched write ----------------

    [Fact]
    public void ForgedCallbackDoesNotLaunderADifferentUnmatchedWrite()
    {
        // Security hardening: if a hook callback id arrives that never corresponded to an actual write event
        // (a "spare" callback — for a Read gate, a spurious id, etc.) it must NOT mask a DIFFERENT write that
        // went genuinely unmatched. The GatedToolUseIds reconciliation only clears the write whose id matches;
        // a stray id cannot launder tu_2's missing callback.
        //
        // Note: GatedToolUseIds trusts hook-channel id integrity — M5.2's hook-observation layer must only
        // surface real hook-stdin ids (ids the CLI itself emits). The guarantee here is that a spare callback
        // for "spare_id" cannot satisfy the pending write for "tu_2", because the match is id-exact.
        var (state, _) = BeginArmedTurn();

        state = ArchetypeAOrchestration.OnWriteToolUse(state, Write("tu_1"));
        state = ArchetypeAOrchestration.OnWriteToolUse(state, Write("tu_2")); // this one will stay unmatched
        state = ArchetypeAOrchestration.OnHookCallback(state, Callback("tu_1"));
        state = ArchetypeAOrchestration.OnHookCallback(state, Callback("spare_id")); // spare — does not match tu_2

        var (resolved, verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.True(verdict.ShouldDegrade, "tu_2 is unmatched; a spare callback for a different id cannot launder it.");
        var unmatched = Assert.Single(verdict.UnmatchedWriteEvents);
        Assert.Equal("tu_2", unmatched.ToolUseId);
        Assert.Equal(TurnAction.DegradeFailClosed, resolved.Action);
    }

    // ---- SPIKE-3 §A/I stream-json fixture: assert ParseWriteToolUses against the documented 2.1.179 shape ---

    [Fact]
    public void Spike3DocumentedWriteToolUseLineShapeIsRecognizedByParser()
    {
        // SPIKE-3 §A/I documents the `claude -p --output-format stream-json` assistant message shape for a write
        // tool-use in claude-code v2.1.179. This fixture pins that exact shape so a future schema drift in the CLI
        // is caught at the parser, not silently at the liveness match (where a missed parse = no event = no degrade
        // despite a real write occurring). Guards against the one place where a parse miss is a liveness miss.
        const string spike3WriteLine =
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01XyzTestSpike3","name":"Write","input":{"file_path":"/some/file.txt","content":"hello"}}]}}""";

        const string spike3EditLine =
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01XyzTestSpike3Edit","name":"Edit","input":{"file_path":"/some/file.txt","old_string":"x","new_string":"y"}}]}}""";

        var writeEvents = StreamJsonTaxonomy.ParseWriteToolUses(spike3WriteLine);
        var writeEv = Assert.Single(writeEvents);
        Assert.Equal("toolu_01XyzTestSpike3", writeEv.ToolUseId);
        Assert.Equal("Write", writeEv.ToolName);

        var editEvents = StreamJsonTaxonomy.ParseWriteToolUses(spike3EditLine);
        var editEv = Assert.Single(editEvents);
        Assert.Equal("toolu_01XyzTestSpike3Edit", editEv.ToolUseId);
        Assert.Equal("Edit", editEv.ToolName);
    }

    private static (ArchetypeATurnState State, ImmutableArray<string> LaunchArgs) BeginArmedTurn() =>
        ArchetypeAOrchestration.Begin(
            HoardPad.Core.Records.AgencyMode.AutoApply,
            new HoardPad.Core.Host.Abstractions.SelfTestResult(Passed: true, Detail: null),
            maxBudgetUsd: 5m,
            settingsJsonPath: "/abs/settings.json");
}
