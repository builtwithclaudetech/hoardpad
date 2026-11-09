using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.2 — the Archetype-A safety gates: the agency→permission mapping (TD §4.3), the self-test-gate-before-write-
/// agency rule (TD §4.4 / FR-24 §3), and the orchestration <c>Begin</c> gate. All pure — no harness, no
/// subprocess, no clock.
/// </summary>
public sealed class ArchetypeAGateTests
{
    private static SelfTestResult Passed => new(Passed: true, Detail: null);
    private static SelfTestResult Failed => new(Passed: false, Detail: "deny did not prevent the write");

    // ---- Agency → permission mapping (TD §4.3) -----------------------------------------------------------

    [Theory]
    [InlineData(AgencyMode.AutoApply, PermissionPosture.AcceptEdits)]
    [InlineData(AgencyMode.ApproveEach, PermissionPosture.SynchronousBlock)]
    [InlineData(AgencyMode.ReadOnly, PermissionPosture.NoWriteTools)]
    public void PostureForMapsEachAgencyMode(AgencyMode mode, PermissionPosture expected)
    {
        Assert.Equal(expected, AgencyMapping.PostureFor(mode));
    }

    [Theory]
    [InlineData(AgencyMode.AutoApply, true)]
    [InlineData(AgencyMode.ApproveEach, true)]
    [InlineData(AgencyMode.ReadOnly, false)]
    public void IsWriteCapableOnlyNonReadOnlyModes(AgencyMode mode, bool expected)
    {
        Assert.Equal(expected, AgencyMapping.IsWriteCapable(mode));
    }

    // ---- Self-test gate before write agency (TD §4.4, FR-24 §3) ------------------------------------------

    [Theory]
    [InlineData(AgencyMode.AutoApply, PermissionPosture.AcceptEdits)]
    [InlineData(AgencyMode.ApproveEach, PermissionPosture.SynchronousBlock)]
    public void ResolveWriteCapableModeArmsWritesOnlyWhenSelfTestPassed(AgencyMode mode, PermissionPosture armedPosture)
    {
        var armed = AgencyMapping.Resolve(mode, Passed);

        Assert.True(armed.WriteAgencyArmed);
        Assert.False(armed.GateDenied);
        Assert.Equal(armedPosture, armed.Posture);
    }

    [Theory]
    [InlineData(AgencyMode.AutoApply)]
    [InlineData(AgencyMode.ApproveEach)]
    public void ResolveWriteCapableModeFailedSelfTestDegradesToNoWriteTools(AgencyMode mode)
    {
        var resolution = AgencyMapping.Resolve(mode, Failed);

        Assert.False(resolution.WriteAgencyArmed);
        Assert.True(resolution.GateDenied);
        Assert.Equal(PermissionPosture.NoWriteTools, resolution.Posture);
    }

    [Theory]
    [InlineData(AgencyMode.AutoApply)]
    [InlineData(AgencyMode.ApproveEach)]
    public void ResolveWriteCapableModeNoSelfTestRunDegradesToNoWriteTools(AgencyMode mode)
    {
        // A null (not-yet-run) self-test is treated identically to a failed one — absent proof of arming ⇒ no
        // write agency (TD §4.4). This is the riskiest path: it MUST fail closed.
        var resolution = AgencyMapping.Resolve(mode, selfTest: null);

        Assert.False(resolution.WriteAgencyArmed);
        Assert.True(resolution.GateDenied);
        Assert.Equal(PermissionPosture.NoWriteTools, resolution.Posture);
    }

    [Fact]
    public void ResolveReadOnlyNeedsNoSelfTestNeverArmsWrites()
    {
        // read-only arms no writes regardless of the self-test (it never needs the gate, and never claims write agency).
        foreach (var selfTest in new SelfTestResult?[] { Passed, Failed, null })
        {
            var resolution = AgencyMapping.Resolve(AgencyMode.ReadOnly, selfTest);
            Assert.False(resolution.WriteAgencyArmed);
            Assert.False(resolution.GateDenied); // read-only is not a "denied" write mode — it never asked for writes.
            Assert.Equal(PermissionPosture.NoWriteTools, resolution.Posture);
        }
    }

    // ---- Orchestration Begin: the gate decides whether the turn starts armed or already degraded ----------

    [Fact]
    public void BeginWriteCapableModeFailedSelfTestStartsDegradedFailClosed()
    {
        var (state, _) = ArchetypeAOrchestration.Begin(
            AgencyMode.AutoApply, Failed, maxBudgetUsd: 5m, settingsJsonPath: "/abs/settings.json");

        Assert.True(state.IsDegraded);
        Assert.Equal(TurnAction.DegradeFailClosed, state.Action);
        Assert.Equal(DegradeCause.SelfTestNotPassed, state.Cause);
        Assert.False(state.Agency.WriteAgencyArmed);
    }

    [Fact]
    public void BeginWriteCapableModeNoSelfTestStartsDegradedFailClosed()
    {
        var (state, _) = ArchetypeAOrchestration.Begin(
            AgencyMode.ApproveEach, selfTest: null, maxBudgetUsd: 5m, settingsJsonPath: "/abs/settings.json");

        Assert.True(state.IsDegraded);
        Assert.Equal(DegradeCause.SelfTestNotPassed, state.Cause);
        Assert.False(state.Agency.WriteAgencyArmed);
    }

    [Fact]
    public void BeginPassedSelfTestStartsHealthyAndArmed()
    {
        var (state, args) = ArchetypeAOrchestration.Begin(
            AgencyMode.AutoApply, Passed, maxBudgetUsd: 5m, settingsJsonPath: "/abs/settings.json");

        Assert.False(state.IsDegraded);
        Assert.Equal(TurnAction.Continue, state.Action);
        Assert.True(state.Agency.WriteAgencyArmed);
        Assert.NotEmpty(args);
    }

    [Fact]
    public void BeginReadOnlyStartsHealthyWithNoWriteToolsAndNeedsNoSelfTest()
    {
        var (state, args) = ArchetypeAOrchestration.Begin(
            AgencyMode.ReadOnly, selfTest: null, maxBudgetUsd: 5m, settingsJsonPath: "/abs/settings.json");

        Assert.False(state.IsDegraded);
        Assert.Equal(PermissionPosture.NoWriteTools, state.Agency.Posture);
        Assert.False(state.Agency.WriteAgencyArmed);
        Assert.NotEmpty(args); // a read-only turn still launches (the matcher offers no write tools).
    }

    [Fact]
    public void DegradedTurnIgnoresFurtherWriteEventsAndCallbacks()
    {
        // Once failing closed (self-test gate), folding more events must not re-open write agency or un-degrade.
        var (state, _) = ArchetypeAOrchestration.Begin(
            AgencyMode.AutoApply, Failed, maxBudgetUsd: 5m, settingsJsonPath: "/abs/settings.json");

        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("tu_1", "Write"));
        state = ArchetypeAOrchestration.OnHookCallback(state, new HookCallbackObservation("tu_1"));

        Assert.True(state.IsDegraded);
        Assert.Equal(DegradeCause.SelfTestNotPassed, state.Cause);
    }

    // ---- Gate-1 Warning #2: write event on an unarmed turn is itself a degrade signal ----------------------

    [Fact]
    public void WriteToolUseOnReadOnlyTurnDegradesSelf()
    {
        // A read-only turn never arms writes. If the agent emits a write tool-use on such a turn it means the
        // tool roster restriction was bypassed (the hook silently failed, or an ambient --dangerously-skip-permissions
        // path was used). Fail closed immediately — this is the strongest possible disarm signal (MA14, TD §4.7).
        var (state, _) = ArchetypeAOrchestration.Begin(
            AgencyMode.ReadOnly, selfTest: null, maxBudgetUsd: 5m, settingsJsonPath: "/abs/settings.json");

        Assert.False(state.IsDegraded);                     // healthy read-only turn starts Continue
        Assert.False(state.Agency.WriteAgencyArmed);        // no write agency — no writes offered

        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("tu_1", "Write"));

        Assert.True(state.IsDegraded);
        Assert.Equal(TurnAction.DegradeFailClosed, state.Action);
        Assert.Equal(DegradeCause.UnmatchedWriteToolUse, state.Cause);
    }

    [Fact]
    public void WriteToolUseOnGateDeniedTurnIsAlreadyDegradedAndStaysDegrade()
    {
        // A gate-denied turn (write-capable mode + failed self-test) starts already degraded. A subsequent write
        // tool-use must NOT re-arm or change the cause — the first-degraded state stands (IsDegraded short-circuit).
        var (state, _) = ArchetypeAOrchestration.Begin(
            AgencyMode.AutoApply, Failed, maxBudgetUsd: 5m, settingsJsonPath: "/abs/settings.json");

        Assert.True(state.IsDegraded);
        Assert.Equal(DegradeCause.SelfTestNotPassed, state.Cause);

        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("tu_1", "Write"));

        // Cause must remain SelfTestNotPassed — the earlier degrade wins.
        Assert.Equal(DegradeCause.SelfTestNotPassed, state.Cause);
    }
}
