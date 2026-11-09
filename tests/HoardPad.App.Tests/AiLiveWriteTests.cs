// AiLiveWriteTests.cs — the M11 AI live-write path, App CONSUMPTION half (Run 8).
//
// docs/escalation-ai-live-write-path.md §5 (invariants) + docs/m11-channel-protocol.md §6 (the wiring
// sequence). These gates drive the App-side consumption of the ALREADY-SHIPPED + gated M11 spine + the
// launch-env seam: the per-turn open → observe → chip/cap/MA14 → dispose flow (a fake channel/endpoint
// feeding HookOpEvents); the approve-each confirm returning allow/deny through the channel's confirm
// delegate; the no-reply/Stop → deny + park; ArmAgencyAsync running the channel-kill self-test in approve
// mode + failing closed on its failure; and the live cap meter == LiveCapMeter.Read (no parallel counter).
//
// The App adds NO safety/decision logic — these tests prove it CONSUMES the spine's fail-closed state.
// The fakes drive the SAME seam contracts the production wiring uses; no real claude -p, no real IPC.
//
// Class names resolve the verify filter "LiveWrite | LiveOp | ChannelKill | HookDecision".

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.App.Tests;

// ---------------------------------------------------------------------------------------------------
//  Fakes — a scripted-stream backend, a fake observation channel/endpoint feeding HookOpEvents, and a
//  cap-state store returning a scripted snapshot. None re-implements safety logic.
// ---------------------------------------------------------------------------------------------------

/// <summary>A backend whose turn streams a SCRIPTED set of stream-json lines, then ends.</summary>
internal sealed class ScriptedStreamBackend : IAgentBackend
{
    private readonly IReadOnlyList<string> _streamLines;

    /// <summary>The env the last StartTurnAsync captured (to assert the per-turn observe merge).</summary>
    public IReadOnlyDictionary<string, string>? CapturedEnv { get; private set; }

    public ScriptedStreamBackend(params string[] streamLines) => _streamLines = streamLines;

    public LaunchDescriptor ResolveLauncher(string? configuredPath) =>
        new("/usr/bin/claude", IsCmd: false, FromPath: true, ResolvedVia: "scripted");

    public Task<IAgentTurn> StartTurnAsync(
        LaunchDescriptor launcher, AgentTurnRequest request, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        CapturedEnv = env;
        return Task.FromResult<IAgentTurn>(new ScriptedTurn(_streamLines));
    }

    private sealed class ScriptedTurn : IAgentTurn
    {
        private readonly IReadOnlyList<string> _streamLines;
        public ScriptedTurn(IReadOnlyList<string> streamLines) => _streamLines = streamLines;

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (string line in _streamLines)
            {
                ct.ThrowIfCancellationRequested();
                yield return new AgentStreamEvent(line);
                await Task.Yield();
            }
        }

        public Task KillTreeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// A fake <see cref="IHookObservationChannel"/> whose endpoint yields a SCRIPTED set of HookOpEvents.
/// It records whether the endpoint was disposed (the fail-closed teardown trigger) and exposes the
/// channel address it would resolve (so the env-merge can be asserted). It models the ResolveChannelAddress
/// the binder passes to the runner via a fixed address.
/// </summary>
internal sealed class FakeObservationChannel : IHookObservationChannel
{
    private readonly IReadOnlyList<HookOpEvent> _events;
    public const string Address = "/run/hoardpad/observe-fake.sock";

    public FakeObservationEndpoint? LastEndpoint { get; private set; }
    public int OpenCalls { get; private set; }

    public FakeObservationChannel(params HookOpEvent[] events) => _events = events;

    public Task<IHookObservationEndpoint> OpenForTurnAsync(
        NormcasedPath workingRoot, string conversationKey, CancellationToken ct)
    {
        OpenCalls++;
        var endpoint = new FakeObservationEndpoint(_events);
        LastEndpoint = endpoint;
        return Task.FromResult<IHookObservationEndpoint>(endpoint);
    }
}

/// <summary>The fake endpoint: yields the scripted events, then waits until disposed (fail-closed teardown).</summary>
internal sealed class FakeObservationEndpoint : IHookObservationEndpoint
{
    private readonly IReadOnlyList<HookOpEvent> _events;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; private set; }

    public FakeObservationEndpoint(IReadOnlyList<HookOpEvent> events) => _events = events;

    public async IAsyncEnumerable<HookOpEvent> ObserveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (HookOpEvent ev in _events)
        {
            ct.ThrowIfCancellationRequested();
            yield return ev;
            await Task.Yield();
        }
        // After the scripted events, the observe loop idles until the turn ends + the endpoint is disposed.
        try
        {
            await _disposed.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
        }
    }

    public Task<HookDecisionResponse> RequestDecisionAsync(HookDecisionRequest request, CancellationToken ct) =>
        Task.FromResult(HookDecisionResponse.Deny(request.ToolUseId));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A fake observation endpoint that holds back its events until a caller releases a gate, then yields
/// them. Used to prove the drain-ordering fix: stdout completes first, the gate releases one event, and
/// the event must still render a chip (not be dropped as a spurious degrade). The gate is a
/// <see cref="TaskCompletionSource"/> the test controls — no sleep, fully deterministic.
/// </summary>
internal sealed class GatedObservationEndpoint : IHookObservationEndpoint
{
    private readonly TaskCompletionSource _gate;
    private readonly HookOpEvent _event;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; private set; }

    public GatedObservationEndpoint(TaskCompletionSource gate, HookOpEvent @event)
    {
        _gate = gate;
        _event = @event;
    }

    public async IAsyncEnumerable<HookOpEvent> ObserveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Block until the gate is released (stdout finished) OR the turn is cancelled.
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        yield return _event;

        // After the event, idle until disposed (the normal endpoint contract).
        try
        {
            await _disposed.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public Task<HookDecisionResponse> RequestDecisionAsync(HookDecisionRequest request, CancellationToken ct) =>
        Task.FromResult(HookDecisionResponse.Deny(request.ToolUseId));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A fake <see cref="IHookObservationChannel"/> backed by a <see cref="GatedObservationEndpoint"/>.
/// The test holds the gate TCS and releases it after stdout has already ended.
/// </summary>
internal sealed class GatedObservationChannel : IHookObservationChannel
{
    private readonly TaskCompletionSource _gate;
    private readonly HookOpEvent _event;

    public GatedObservationChannel(TaskCompletionSource gate, HookOpEvent @event)
    {
        _gate = gate;
        _event = @event;
    }

    public Task<IHookObservationEndpoint> OpenForTurnAsync(
        NormcasedPath workingRoot, string conversationKey, CancellationToken ct) =>
        Task.FromResult<IHookObservationEndpoint>(new GatedObservationEndpoint(_gate, _event));
}

/// <summary>A cap-state store returning a SCRIPTED snapshot payload (the live meter READs it — invariant 4).</summary>
internal sealed class ScriptedCapStateStore : ICapStateStore
{
    private readonly string? _payloadJson;
    public ScriptedCapStateStore(int created, int destructive, int touched)
    {
        // FilesTouchedCount is derived from the cardinality of FilesTouched — seed N distinct paths so the
        // serialized cap-state reads back the intended touched count (the live meter reads this exact state).
        var files = System.Collections.Immutable.ImmutableHashSet.CreateRange(
            System.StringComparer.Ordinal, Enumerable.Range(0, touched).Select(i => $"/the/root/touched-{i}.txt"));
        var state = new CapState(
            CreateCount: created,
            DestructiveCount: destructive,
            FilesTouched: files,
            CreateCapTripped: false,
            DestructiveCapTripped: false,
            FilesTouchedCapTripped: false);
        _payloadJson = CapStateSerialization.Serialize(state);
    }

    public Task<CapStateSnapshot> ReadAsync(string conversationId, CancellationToken ct) =>
        Task.FromResult(new CapStateSnapshot(_payloadJson, Version: 1));

    public Task<CapStateWriteResult> WriteAsync(string conversationId, string payloadJson, long expectedVersion, CancellationToken ct) =>
        Task.FromResult(CapStateWriteResult.Ok(expectedVersion + 1));

    public string ResolveCapStateFilePath(string conversationId) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hoardpad-scripted-{conversationId}.capstate");
}

/// <summary>
/// A scripted backend that, after its last stream line, sets a <see cref="TaskCompletionSource"/> before
/// the stream ends. This lets the test gate the observe endpoint on "stdout has finished producing" in a
/// fully deterministic way (no sleep, no timing dependencies).
/// </summary>
internal sealed class GatingStreamBackend : IAgentBackend
{
    private readonly IReadOnlyList<string> _streamLines;
    private readonly TaskCompletionSource _stdoutDoneGate;

    public GatingStreamBackend(TaskCompletionSource stdoutDoneGate, params string[] streamLines)
    {
        _stdoutDoneGate = stdoutDoneGate;
        _streamLines = streamLines;
    }

    public LaunchDescriptor ResolveLauncher(string? configuredPath) =>
        new("/usr/bin/claude", IsCmd: false, FromPath: true, ResolvedVia: "gating");

    public Task<IAgentTurn> StartTurnAsync(
        LaunchDescriptor launcher, AgentTurnRequest request, IReadOnlyDictionary<string, string> env, CancellationToken ct) =>
        Task.FromResult<IAgentTurn>(new GatingTurn(_streamLines, _stdoutDoneGate));

    private sealed class GatingTurn : IAgentTurn
    {
        private readonly IReadOnlyList<string> _streamLines;
        private readonly TaskCompletionSource _gate;
        public GatingTurn(IReadOnlyList<string> streamLines, TaskCompletionSource gate)
        {
            _streamLines = streamLines;
            _gate = gate;
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (string line in _streamLines)
            {
                ct.ThrowIfCancellationRequested();
                yield return new AgentStreamEvent(line);
                await Task.Yield();
            }
            // Signal that stdout has finished — the gated observe endpoint may now yield its event.
            _gate.TrySetResult();
        }

        public Task KillTreeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// ---------------------------------------------------------------------------------------------------
//  Shared support: a runner factory + stream-line + hook-event builders.
// ---------------------------------------------------------------------------------------------------

internal static class LiveWriteSupport
{
    public static NormcasedPath Root() =>
        new HoardPad.Host.PathResolver().ResolveCanonical(System.IO.Path.GetTempPath(), System.IO.Path.GetTempPath());

    public static Dictionary<string, string> SessionEnv() => new(System.StringComparer.Ordinal)
    {
        ["HOARDPAD_SANDBOX_ROOT"] = "/the/root",
        ["HOARDPAD_CAP_STATE_FILE"] = "/appdata/caps/abc.capstate",
    };

    /// <summary>A stream-json assistant line carrying ONE write tool_use (id + name) for the MA14 side.</summary>
    public static string WriteToolUseLine(string toolUseId, string toolName = "Write") =>
        $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"tool_use\",\"id\":\"{toolUseId}\",\"name\":\"{toolName}\",\"input\":{{}}}}]}}}}";

    public static HookOpEvent Report(
        string toolUseId, string targetPath, HookOpClass opClass, HookDecision decision, string toolName = "Write") =>
        new(toolUseId, toolName, targetPath, opClass, Magnitude: 0, decision);

    public static AiTurnRunner Runner(
        IAgentBackend backend, IHookObservationChannel channel, ICapStateStore capStore,
        string conversationKey = "conv-live", string address = FakeObservationChannel.Address) =>
        new(
            backend, Root(), settingsJsonPath: "/tmp/s.json", maxBudgetUsd: 5m, SessionEnv(),
            channel: channel,
            hookHost: new ScriptedHookHost(selfTestPasses: true),
            capStore: capStore,
            conversationKey: conversationKey,
            channelAddress: address);

    public static async Task<List<TurnEvent>> DrainAsync(AiTurnRunner runner, AgencyMode mode, bool armed = true)
    {
        var events = new List<TurnEvent>();
        await foreach (TurnEvent ev in runner.RunAsync(new TurnRequest("go", mode, WriteAgencyArmed: armed), CancellationToken.None))
            events.Add(ev);
        return events;
    }
}

// ---------------------------------------------------------------------------------------------------
//  (1) The per-turn open → observe → chip/cap/MA14 → dispose flow.
// ---------------------------------------------------------------------------------------------------

public sealed class LiveWriteObserveLoopTests
{
    [Fact]
    public async Task ObservedHookAllowRendersAnAppliedChipWithTheCapStateRead()
    {
        // The agent streams a write tool_use; the hook reports an ALLOW for the same id. The runner renders
        // an APPLIED file-op chip and the cap snapshot is a READ of the cap-state (created=3,dest=1,touched=4).
        var channel = new FakeObservationChannel(
            LiveWriteSupport.Report("toolu_a", "/the/root/a.txt", HookOpClass.Create, HookDecision.Allow));
        var caps = new ScriptedCapStateStore(created: 3, destructive: 1, touched: 4);
        var backend = new ScriptedStreamBackend(LiveWriteSupport.WriteToolUseLine("toolu_a"));
        var runner = LiveWriteSupport.Runner(backend, channel, caps);

        List<TurnEvent> events = await LiveWriteSupport.DrainAsync(runner, AgencyMode.AutoApply);

        TurnEvent chip = Assert.Single(events, e => e.Kind == TurnEventKind.FileOp);
        Assert.Equal(ActivityOp.Create, chip.Op);
        Assert.Equal("/the/root/a.txt", chip.TargetPath);
        Assert.Equal(OpOutcomeBadge.Applied, chip.Outcome);
        // The cap snapshot equals LiveCapMeter.Read of the SAME cap-state snapshot — never a parallel counter
        // (spec §5 invariant 4). Assert against the Read result directly so the "meter == Read" contract is
        // explicit and the assertion does not drift if the scripted counts above are ever changed.
        Assert.NotNull(chip.Caps);
        CapStateSnapshot snap = await caps.ReadAsync("conv-live", CancellationToken.None);
        LiveCapMeterReading expected = LiveCapMeter.Read(snap);
        Assert.Equal(expected.Created, chip.Caps!.Value.Created);
        Assert.Equal(expected.Destructive, chip.Caps!.Value.Destructive);
        Assert.Equal(expected.Touched, chip.Caps!.Value.Touched);
        // No degrade: the observed callback cleared the streamed write (MA14 healthy).
        Assert.DoesNotContain(events, e => e.Kind == TurnEventKind.SafetyEvent && e.SafetyKind == SafetyEventKind.Degraded);
        Assert.True(channel.LastEndpoint!.Disposed);   // the endpoint is disposed at turn end (fail-closed teardown).
    }

    [Fact]
    public async Task ObservedHookDenyRendersABlockedChipAndDoesNotApply()
    {
        // A hook-DENY (out of sandbox) reports a blocked chip — the App renders it blocked + the cap meter
        // does not advance past the cap-state read (the read is the authority; the chip badge is Blocked).
        var channel = new FakeObservationChannel(
            LiveWriteSupport.Report("toolu_b", "/etc/passwd", HookOpClass.Overwrite, HookDecision.Deny));
        var caps = new ScriptedCapStateStore(created: 0, destructive: 0, touched: 0);
        var backend = new ScriptedStreamBackend(LiveWriteSupport.WriteToolUseLine("toolu_b"));
        var runner = LiveWriteSupport.Runner(backend, channel, caps);

        List<TurnEvent> events = await LiveWriteSupport.DrainAsync(runner, AgencyMode.AutoApply);

        TurnEvent chip = Assert.Single(events, e => e.Kind == TurnEventKind.FileOp);
        Assert.Equal(OpOutcomeBadge.Blocked, chip.Outcome);
        Assert.Equal("/etc/passwd", chip.TargetPath);
    }

    [Fact]
    public async Task ObservedCallbackFeedsMa14SoAStreamedWriteDoesNotDegrade()
    {
        // MA14: a streamed write tool_use with a MATCHING observed hook callback does NOT degrade. Without
        // the observed callback the same streamed write would degrade at turn end (proven by the sibling test).
        var channel = new FakeObservationChannel(
            LiveWriteSupport.Report("toolu_c", "/the/root/c.txt", HookOpClass.Create, HookDecision.Allow));
        var backend = new ScriptedStreamBackend(LiveWriteSupport.WriteToolUseLine("toolu_c"));
        var runner = LiveWriteSupport.Runner(backend, channel, new ScriptedCapStateStore(1, 0, 1));

        List<TurnEvent> events = await LiveWriteSupport.DrainAsync(runner, AgencyMode.AutoApply);

        Assert.DoesNotContain(events, e => e.Kind == TurnEventKind.SafetyEvent && e.SafetyKind == SafetyEventKind.Degraded);
    }

    [Fact]
    public async Task AStreamedWriteWithNoObservedCallbackDegradesAtTurnEnd()
    {
        // MA14 degrade: a streamed write tool_use with NO observed hook callback (a silently-disarmed hook)
        // degrades the turn fail-closed at turn end (the channel reports nothing for that id).
        var channel = new FakeObservationChannel(/* no events */);
        var backend = new ScriptedStreamBackend(LiveWriteSupport.WriteToolUseLine("toolu_missing"));
        var runner = LiveWriteSupport.Runner(backend, channel, new ScriptedCapStateStore(0, 0, 0));

        List<TurnEvent> events = await LiveWriteSupport.DrainAsync(runner, AgencyMode.AutoApply);

        Assert.Contains(events, e => e.Kind == TurnEventKind.SafetyEvent && e.SafetyKind == SafetyEventKind.Degraded);
    }

    [Fact]
    public async Task ATurnWithNoLiveChannelOpensNoEndpointAndStreamsAsBefore()
    {
        // A read-only turn (write agency not armed) opens NO endpoint — the runner is the pre-M11 path.
        var channel = new FakeObservationChannel(
            LiveWriteSupport.Report("toolu_z", "/the/root/z.txt", HookOpClass.Create, HookDecision.Allow));
        var backend = new ScriptedStreamBackend(/* empty stream */);
        var runner = LiveWriteSupport.Runner(backend, channel, new ScriptedCapStateStore(0, 0, 0));

        await LiveWriteSupport.DrainAsync(runner, AgencyMode.ReadOnly, armed: false);

        Assert.Equal(0, channel.OpenCalls);   // no endpoint opened when write agency is not armed.
    }
}

// ---------------------------------------------------------------------------------------------------
//  (2) The per-turn observe env merges into the launch env (mode-dependent).
// ---------------------------------------------------------------------------------------------------

public sealed class LiveWriteEnvMergeTests
{
    [Fact]
    public async Task ApproveEachTurnMergesTheObserveChannelAndApproveModeOntoTheSandboxCapEnv()
    {
        var channel = new FakeObservationChannel(/* no events */);
        var backend = new ScriptedStreamBackend(/* empty stream */);
        var runner = LiveWriteSupport.Runner(backend, channel, new ScriptedCapStateStore(0, 0, 0));

        await LiveWriteSupport.DrainAsync(runner, AgencyMode.ApproveEach);

        Assert.NotNull(backend.CapturedEnv);
        // The session sandbox/cap env is preserved AND the per-turn observe pair is merged on top.
        Assert.Equal("/the/root", backend.CapturedEnv!["HOARDPAD_SANDBOX_ROOT"]);
        Assert.Equal("/appdata/caps/abc.capstate", backend.CapturedEnv!["HOARDPAD_CAP_STATE_FILE"]);
        Assert.Equal(FakeObservationChannel.Address, backend.CapturedEnv!["HOARDPAD_OBSERVE_CHANNEL"]);
        Assert.Equal("approve", backend.CapturedEnv!["HOARDPAD_OBSERVE_MODE"]);
    }

    [Fact]
    public async Task AutoApplyTurnMergesReportModeNotApprove()
    {
        var channel = new FakeObservationChannel(/* no events */);
        var backend = new ScriptedStreamBackend(/* empty stream */);
        var runner = LiveWriteSupport.Runner(backend, channel, new ScriptedCapStateStore(0, 0, 0));

        await LiveWriteSupport.DrainAsync(runner, AgencyMode.AutoApply);

        Assert.Equal(FakeObservationChannel.Address, backend.CapturedEnv!["HOARDPAD_OBSERVE_CHANNEL"]);
        Assert.Equal("report", backend.CapturedEnv!["HOARDPAD_OBSERVE_MODE"]);   // auto-apply = report mode.
    }
}

// ---------------------------------------------------------------------------------------------------
//  (3) The approve-each confirm + the no-reply/Stop → deny + park, via the panel's confirm delegate.
// ---------------------------------------------------------------------------------------------------

public sealed class HookDecisionConfirmTests
{
    private static AiPanelViewModel PanelWithConfirm(IAiConfirmService confirm)
    {
        var vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]), confirm);
        // Bind a session so SandboxRoot is set (the confirm shows it). The confirm path is what we exercise.
        vm.BindSession("/the/root", FakeFileOps(), conversationId: "conv-confirm");
        return vm;
    }

    private static HoardPad.Core.Ai.FileOps.AiFileOpsService FakeFileOps() =>
        new(
            new ChecklistTestSupportSandboxGate(),
            new CountingAtomicFileStore(),
            new NullCapStateStore(),
            new FakeAppDataPaths(),
            sessionId: "s-confirm",
            CapConfig.Pinned);

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ConfirmApproveReturnsAllowedThroughTheChannelDelegate()
    {
        var confirm = new ScriptedConfirmService(approveAnswers: [true]);
        var vm = PanelWithConfirm(confirm);

        HookDecisionResponse answer = await vm.ConfirmHookDecisionAsync(
            new HookDecisionRequest("toolu_ok", "Write", "/the/root/ok.txt", HookOpClass.Create), CancellationToken.None);

        Assert.True(answer.Allow);
        Assert.Equal("toolu_ok", answer.ToolUseId);
        Assert.Single(confirm.ApproveRequests);
        Assert.Empty(vm.Pending);   // an allowed op is NOT parked.
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ConfirmDenyReturnsDenyAndParksToPending()
    {
        var confirm = new ScriptedConfirmService(approveAnswers: [false]);
        var vm = PanelWithConfirm(confirm);

        HookDecisionResponse answer = await vm.ConfirmHookDecisionAsync(
            new HookDecisionRequest("toolu_no", "Write", "/the/root/no.txt", HookOpClass.Overwrite), CancellationToken.None);

        Assert.False(answer.Allow);
        // The denied op is parked to the FR-39 pending surface (the existing park path).
        PendingProposalViewModel parked = Assert.Single(vm.Pending);
        Assert.Equal("toolu_no", parked.ProposalId);
        Assert.Equal("/the/root/no.txt", parked.TargetPath);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task NoReplyOrStopCancelledTokenReturnsDenyAndParks()
    {
        // A no-reply deadline / Stop cancels the confirm token → the confirm throws OperationCanceledException
        // → the App fails closed: returns Deny AND parks the op (the channel also resolves Deny — invariant 6).
        var confirm = new CancellingConfirmService();
        var vm = PanelWithConfirm(confirm);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        HookDecisionResponse answer = await vm.ConfirmHookDecisionAsync(
            new HookDecisionRequest("toolu_timeout", "Edit", "/the/root/t.txt", HookOpClass.Edit), cts.Token);

        Assert.False(answer.Allow);
        Assert.Single(vm.Pending);
    }

    /// <summary>A confirm that always honors cancellation (models the no-reply deadline / Stop).</summary>
    private sealed class CancellingConfirmService : IAiConfirmService
    {
        public Task<bool> ConfirmApproveEachAsync(ApproveEachRequest request, CancellationToken ct) =>
            Task.FromCanceled<bool>(ct);

        public Task<bool> ConfirmCapReachedAsync(CapReachedRequest request, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> ConfirmSettingsChangeAsync(SettingsChangeConfirmRequest request, CancellationToken ct) =>
            Task.FromResult(false);
    }
}

// ---------------------------------------------------------------------------------------------------
//  (4) Invariant 5: ArmAgencyAsync runs the channel-kill self-test in approve mode + fails closed.
// ---------------------------------------------------------------------------------------------------

public sealed class ChannelKillSelfTestArmTests
{
    private static AiPanelViewModel WithSession(bool selfTestPasses, bool channelKillPasses)
    {
        var vm = AiPanelTestFactory.New(
            new ScriptedTurnRunner([]), selfTestPasses: selfTestPasses, channelKillPasses: channelKillPasses);
        vm.BindSession("/the/root", FakeFileOps(), conversationId: "conv-ck");
        return vm;
    }

    private static HoardPad.Core.Ai.FileOps.AiFileOpsService FakeFileOps() =>
        new(
            new ChecklistTestSupportSandboxGate(),
            new CountingAtomicFileStore(),
            new NullCapStateStore(),
            new FakeAppDataPaths(),
            sessionId: "s-ck",
            CapConfig.Pinned);

    [Fact]
    public async Task ApproveEachArmsOnlyWhenBOTHSelfTestsPass()
    {
        var vm = WithSession(selfTestPasses: true, channelKillPasses: true);
        vm.ConfiguredMode = AgencyMode.ApproveEach;

        AgencyResolution resolution = await vm.ArmAgencyAsync();

        Assert.True(resolution.WriteAgencyArmed);
        Assert.Equal(AgencyMode.ApproveEach, vm.EffectiveMode);
        Assert.Equal(SandboxState.Armed, vm.Sandbox);
    }

    [Fact]
    public async Task ApproveEachFailsClosedWhenTheChannelKillSelfTestFails()
    {
        // The fail-closed self-test passes but the killed-channel deny self-test FAILS → write agency is
        // NOT armed (invariant 5). The panel downgrades to read-only + a downgrade badge (fail-closed).
        var vm = WithSession(selfTestPasses: true, channelKillPasses: false);
        vm.ConfiguredMode = AgencyMode.ApproveEach;

        AgencyResolution resolution = await vm.ArmAgencyAsync();

        Assert.False(resolution.WriteAgencyArmed);
        Assert.True(resolution.GateDenied);
        Assert.Equal(AgencyMode.ReadOnly, vm.EffectiveMode);
        Assert.Equal(SandboxState.SelfTestFailed, vm.Sandbox);
        Assert.True(vm.HasDowngrade);
    }

    [Fact]
    public async Task AutoApplyWithTheFR10aTriggerOffDoesNotRunTheChannelKillSelfTest()
    {
        // Plain auto-apply (no possible FR-10(a) downgrade to a live approve channel — FS-5's
        // ConfirmLargeHomeFolder toggle explicitly OFF here) uses REPORT mode (no blocking ask), so
        // there is no killed-channel ask path to gate — only the existing fail-closed self-test runs.
        //
        // FS-5 (docs/decisions.md DR-1, cross-platform-review REVISE fix, 2026-07-11) RETIRED this
        // test's original "channel-kill gate is approve-each-only" premise: when ConfirmLargeHomeFolder
        // is ON (the default), an auto-apply arm CAN downgrade EffectiveMode to ApproveEach and drive a
        // live approve channel, so the gate now ALSO runs in that case — see
        // ConfirmLargeFolderSettingViewModelTests.AnAutoApplyArmWithTheToggleOnRunsTheChannelKillSelfTest*
        // in ConfirmLargeFolderSettingTests.cs for that (new) coverage. This test is retained, narrowed
        // to the toggle-OFF case, to keep proving the gate is genuinely conditional, not unconditional.
        var host = new ScriptedHookHost(selfTestPasses: true, channelKillPasses: true);
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]), new ScriptedConfirmService(), host, new FakeAgentBackend(),
            TimeProvider.System);
        vm.BindSession("/the/root", FakeFileOps(), conversationId: "conv-aa");
        vm.ConfiguredMode = AgencyMode.AutoApply;
        vm.ConfirmLargeHomeFolder = false;

        await vm.ArmAgencyAsync();

        Assert.True(host.SelfTestCalls >= 1);
        Assert.Equal(0, host.ChannelKillCalls);   // no possible approve-channel path -> gate skipped.
    }
}

// ---------------------------------------------------------------------------------------------------
//  (5) Drain-ordering regression: an observe event that arrives AFTER stdout ends still renders a chip.
//
//  This test was added to cover the drain-ordering race described in cs Warning 1 / spec Finding 2:
//  before the fix, PumpObserveAsync wrote with the mergeCts.Token, so an event dequeued-but-not-yet-
//  written at the cancel instant threw OCE and was dropped, producing a spurious end-of-turn MA14
//  degrade. The fix: ObserveAsync uses mergeCts.Token (stops producing), but WriteAsync uses
//  CancellationToken.None (in-flight events drain through).
//
//  Determinism guarantee: the gate is a TaskCompletionSource set by the stdout stream ITSELF after
//  its last line — no sleep, no timing. The observe endpoint waits on the gate before yielding its
//  event. The coordinator cancels the merge CTS AFTER awaiting the stdout pump — so the observe pump
//  runs AFTER the gate is set, and the WriteAsync completes before the observe pump sees the cancel.
// ---------------------------------------------------------------------------------------------------

public sealed class DrainOrderingRegressionTests
{
    [Fact]
    public async Task ObserveEventArrivingAfterStdoutEndStillRendersChipNotDegrade()
    {
        // Arrange: the stdout stream has ONE write tool_use, then ends and releases the gate. The observe
        // endpoint waits for that gate, then yields its Allow callback for the SAME id. Without the fix
        // the WriteAsync would throw OCE and the event would be dropped → spurious degrade. With the fix
        // the event drains through → chip rendered, no degrade.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateEvent = LiveWriteSupport.Report("toolu_late", "/the/root/late.txt", HookOpClass.Create, HookDecision.Allow);
        var backend = new GatingStreamBackend(gate, LiveWriteSupport.WriteToolUseLine("toolu_late"));
        var channel = new GatedObservationChannel(gate, lateEvent);
        var caps = new ScriptedCapStateStore(created: 1, destructive: 0, touched: 1);
        var runner = LiveWriteSupport.Runner(backend, channel, caps);

        List<TurnEvent> events = await LiveWriteSupport.DrainAsync(runner, AgencyMode.AutoApply);

        // The chip must be rendered (the late observe event was not dropped).
        TurnEvent chip = Assert.Single(events, e => e.Kind == TurnEventKind.FileOp);
        Assert.Equal(OpOutcomeBadge.Applied, chip.Outcome);
        Assert.Equal("/the/root/late.txt", chip.TargetPath);
        // MA14: the observed callback cleared the streamed write → no degrade.
        Assert.DoesNotContain(events, e => e.Kind == TurnEventKind.SafetyEvent && e.SafetyKind == SafetyEventKind.Degraded);
    }
}
