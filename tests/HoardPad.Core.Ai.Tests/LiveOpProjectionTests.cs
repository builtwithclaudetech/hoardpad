using System.Collections.Immutable;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M11 Step 1 — the platform-free orchestration that maps an observed <see cref="HookOpEvent"/> from the
/// <see cref="IHookObservationChannel"/> into (a) a file-op CHIP, (b) the live cap-meter READ, and (c) the MA14
/// observed-callback feed (docs/escalation-ai-live-write-path.md §4, §7 G-live-6). All pure — synthetic events
/// + a serialized cap-state payload, no channel, no subprocess, no clock.
///
/// The non-negotiable invariants exercised here (spec §5): a hook-DENY never advances the cap meter and renders
/// a blocked chip (invariant 1); the live meter is a READ of the O_EXCL cap-state, never a parallel counter
/// (invariant 4); the observed-callback feed reuses the EXISTING M4.2 liveness fold, so an unmatched stream-json
/// write (no observed callback) — and vice-versa — degrades fail-closed (§7 G-live-6 / invariant 8 compat).
/// </summary>
public sealed class LiveOpProjectionTests
{
    private static HookOpEvent Op(
        string id,
        string tool = "Write",
        string target = "/root/file.txt",
        HookOpClass cls = HookOpClass.Create,
        long magnitude = 0,
        HookDecision decision = HookDecision.Allow) =>
        new(id, tool, target, cls, magnitude, decision);

    // ---- (a) the file-op CHIP mapping --------------------------------------------------------------------

    [Fact]
    public void AllowedCreateProjectsToAnAppliedCreateChip()
    {
        var chip = LiveOpProjection.ToChip(Op("tu_1", cls: HookOpClass.Create, decision: HookDecision.Allow));

        Assert.True(chip.WasApplied);
        Assert.Equal(ActivityOp.Create, chip.Op);
        Assert.Equal("/root/file.txt", chip.TargetPath);
        Assert.Equal(HookOpClass.Create, chip.OpClass);
    }

    [Fact]
    public void AllowedOverwriteProjectsToAnAppliedEditChip()
    {
        // An overwrite maps to the §9.3 `edit` op vocabulary (there is no `overwrite` token); the OpClass
        // preserves the distinction for the chip's detail label.
        var chip = LiveOpProjection.ToChip(Op("tu_2", cls: HookOpClass.Overwrite, decision: HookDecision.Allow));

        Assert.True(chip.WasApplied);
        Assert.Equal(ActivityOp.Edit, chip.Op);
        Assert.Equal(HookOpClass.Overwrite, chip.OpClass);
    }

    [Fact]
    public void DeniedOpProjectsToANonAppliedBlockedChip()
    {
        // INVARIANT 1: a hook-deny is the FLOOR. The chip is rendered blocked (WasApplied == false) and — by the
        // App contract — does NOT advance the cap meter. The projection carries the deny faithfully.
        var chip = LiveOpProjection.ToChip(
            Op("tu_3", cls: HookOpClass.Create, decision: HookDecision.Deny));

        Assert.False(chip.WasApplied);
        Assert.Equal(ActivityOp.Create, chip.Op);
    }

    [Fact]
    public void DeniedOpWithEmptyTargetStillProjectsAChip()
    {
        // A malformed / no-path deny (the hook denied before resolving a path) still yields a chip — with an
        // empty target the App can render as "blocked (no path)". It must not throw.
        var chip = LiveOpProjection.ToChip(Op("tu_4", target: "", decision: HookDecision.Deny));

        Assert.False(chip.WasApplied);
        Assert.Equal(string.Empty, chip.TargetPath);
    }

    // ---- (b) the live cap-meter READ (never a parallel counter — invariant 4) -----------------------------

    [Fact]
    public void LiveCapMeterReadsTheThreeCountsFromTheCapState()
    {
        // INVARIANT 4: the meter is a READ of the already-incremented O_EXCL cap-state — the SAME PascalCase
        // payload the hook reads. Build a real CapState, serialize it exactly as the store persists it, and read.
        var state = new CapState(
            CreateCount: 7,
            DestructiveCount: 3,
            FilesTouched: ImmutableHashSet.Create(StringComparer.Ordinal, "/root/a.txt", "/root/b.txt"),
            CreateCapTripped: false,
            DestructiveCapTripped: false,
            FilesTouchedCapTripped: false);
        string payload = CapStateSerialization.Serialize(state);
        var snapshot = new CapStateSnapshot(payload, Version: 42);

        LiveCapMeterReading reading = LiveCapMeter.Read(snapshot);

        Assert.Equal(7, reading.Created);
        Assert.Equal(3, reading.Destructive);
        Assert.Equal(2, reading.Touched); // the FR-40 set cardinality, not a separate counter
    }

    [Fact]
    public void LiveCapMeterReadsEmptyForANeverPersistedConversation()
    {
        // A new user-initiated conversation has no persisted state (CapStateSnapshot.Empty) — every meter at 0.
        LiveCapMeterReading reading = LiveCapMeter.Read(CapStateSnapshot.Empty);

        Assert.Equal(0, reading.Created);
        Assert.Equal(0, reading.Destructive);
        Assert.Equal(0, reading.Touched);
    }

    [Fact]
    public void LiveCapMeterIsAPureReadIdenticalAcrossRepeatedCalls()
    {
        // No accumulation: reading the same snapshot twice yields the same value (the read holds no counter that
        // could drift). This is the property that forecloses double-count / re-arm via the live meter (invariant 4).
        var state = CapState.Empty with { CreateCount = 5, DestructiveCount = 2 };
        var snapshot = new CapStateSnapshot(CapStateSerialization.Serialize(state), Version: 1);

        LiveCapMeterReading first = LiveCapMeter.Read(snapshot);
        LiveCapMeterReading second = LiveCapMeter.Read(snapshot);

        Assert.Equal(first, second);
        Assert.Equal(5, first.Created);
    }

    // ---- (c) the MA14 observed-callback feed (reuses the EXISTING M4.2 liveness fold) ----------------------

    [Fact]
    public void WriteOpProjectsToAHookCallbackKeyedByToolUseId()
    {
        HookCallbackObservation? cb = LiveOpProjection.ToHookCallback(Op("tu_9", tool: "Write"));

        Assert.NotNull(cb);
        Assert.Equal("tu_9", cb!.ToolUseId);
    }

    [Fact]
    public void ReadOpProducesNoHookCallback()
    {
        // A Read callback does not gate a write and must not feed MA14 (mirrors StreamJsonTaxonomy, which surfaces
        // only WRITE tool-use events). A spare Read callback would be harmless but it is cleaner not to emit one.
        HookCallbackObservation? cb = LiveOpProjection.ToHookCallback(Op("tu_read", tool: "Read"));

        Assert.Null(cb);
    }

    [Fact]
    public void EmptyToolUseIdProducesNoHookCallback()
    {
        // An unkeyable observation cannot correlate against any write — do not feed it.
        HookCallbackObservation? cb = LiveOpProjection.ToHookCallback(Op("", tool: "Write"));

        Assert.Null(cb);
    }

    [Fact]
    public void DeniedWriteStillProducesAHookCallbackBecauseTheHookRanForIt()
    {
        // The MA14 degrade signal is the ABSENCE of a callback for an emitted write (a silently-disarmed hook),
        // NOT a deny. A denied write the hook observed is positive proof the hook ran — so it still produces a
        // callback that clears the matching stream-json write from the pending set. The deny shows on the chip.
        HookCallbackObservation? cb = LiveOpProjection.ToHookCallback(
            Op("tu_denied", tool: "Write", decision: HookDecision.Deny));

        Assert.NotNull(cb);
        Assert.Equal("tu_denied", cb!.ToolUseId);
    }

    [Fact]
    public void ObservedCallbackFeedClearsTheMatchingStreamJsonWriteAndStaysContinue()
    {
        // The end-to-end live-path MA14 wiring: a stream-json write event is folded in (OnWriteToolUse), then an
        // OBSERVED hook op (projected via ToHookCallback) is folded into the EXISTING OnHookCallback. The matching
        // write clears ⇒ no degrade. This proves the live channel reuses the proven M4.2 liveness logic.
        var (state, _) = BeginArmedTurn();

        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("tu_1", "Write"));

        HookCallbackObservation? cb = LiveOpProjection.ToHookCallback(Op("tu_1", tool: "Write"));
        Assert.NotNull(cb);
        state = ArchetypeAOrchestration.OnHookCallback(state, cb!);

        var (resolved, verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.False(verdict.ShouldDegrade);
        Assert.Equal(TurnAction.Continue, resolved.Action);
    }

    [Fact]
    public void StreamJsonWriteWithNoObservedCallbackDegradesFailClosed()
    {
        // §7 G-live-6 (first direction): a stream-json write tool-use with NO observed hook callback (the channel
        // never reported it ⇒ the hook may be silently disarmed for that op) degrades per MA14. The live channel
        // does not weaken this — the absence of a ToHookCallback feed is exactly the M4.2 unmatched-write case.
        var (state, _) = BeginArmedTurn();

        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("tu_1", "Write"));
        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("tu_2", "Write"));
        // Only tu_1 is observed over the channel; tu_2 is never reported.
        HookCallbackObservation? cb1 = LiveOpProjection.ToHookCallback(Op("tu_1", tool: "Write"));
        state = ArchetypeAOrchestration.OnHookCallback(state, cb1!);

        var (resolved, verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.True(verdict.ShouldDegrade);
        Assert.Equal(DegradeCause.UnmatchedWriteToolUse, resolved.Cause);
        var unmatched = Assert.Single(verdict.UnmatchedWriteEvents);
        Assert.Equal("tu_2", unmatched.ToolUseId);
    }

    [Fact]
    public void ObservedCallbackBeforeItsStreamJsonWriteMatchesOrderIndependently()
    {
        // §7 G-live-6 (cross-channel reordering): the observed-channel op may arrive BEFORE its stream-json write
        // event. The EXISTING GatedToolUseIds reconciliation makes the match order-independent — a benign
        // reordering must not false-degrade. The live projection inherits this for free.
        var (state, _) = BeginArmedTurn();

        HookCallbackObservation? cb = LiveOpProjection.ToHookCallback(Op("tu_1", tool: "Write"));
        state = ArchetypeAOrchestration.OnHookCallback(state, cb!);              // observed op first
        state = ArchetypeAOrchestration.OnWriteToolUse(state, new WriteToolUseEvent("tu_1", "Write")); // then the stream-json write

        var (resolved, verdict) = ArchetypeAOrchestration.ResolveLiveness(state);

        Assert.False(verdict.ShouldDegrade);
        Assert.Equal(TurnAction.Continue, resolved.Action);
    }

    private static (ArchetypeATurnState State, ImmutableArray<string> LaunchArgs) BeginArmedTurn() =>
        ArchetypeAOrchestration.Begin(
            AgencyMode.AutoApply,
            new SelfTestResult(Passed: true, Detail: null),
            maxBudgetUsd: 5m,
            settingsJsonPath: "/abs/settings.json");
}
