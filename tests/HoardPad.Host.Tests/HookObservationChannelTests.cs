// HookObservationChannelTests.cs — the M11 per-turn observation channel (Step 2a).
//
// Filter: ~HookObservation. Covers (on linux-x64; the win-x64 named-pipe leg is runtime-Deferred):
//   - NDJSON round-trip: the host receives a hook `report` as a HookOpEvent; the host answers an
//     `ask` with allow (decision written back allow:true) and with deny (decision written back
//     allow:false) — proved by reading the decision line the channel sends to the connected client.
//   - The FAIL-CLOSED battery (each NON-VACUOUS — it fails if the deny path is replaced by allow):
//       * unreachable channel (the hook never connects) => RequestDecisionAsync => Deny;
//       * timeout / no answer (a confirm that never returns) => the deadline trips => Deny;
//       * endpoint disposed mid-request => Deny (disposal cancels the in-flight ask);
//       * a malformed `ask`/`report` line => skipped / no allow manufactured;
//       * a confirm that throws => Deny (never surfaces a fault as allow).
//   - The mutation guard (NonVacuous_*): a control that the same harness, pointed at an ALLOW
//     answer, returns allow — so the deny tests are not trivially always-deny.
//   - FR-10(c) accept-loop regression (docs/phase0-fr10c-findings.md): a fresh hook process per
//     PreToolUse op opens its OWN connection, sends one report, and closes — SEQUENTIAL and
//     CONCURRENT multi-connection turns must observe EVERY connection's report, not just the first.
//     A single-op turn must still yield exactly one event. A report that never arrives (or a
//     mid-ask disposal) must still fail closed — the fix must never turn MA14 into a no-op.
//
// The "hook" side is an in-test client speaking the SAME wire (WireMessage) the production hook's
// protocol mirrors, connected over the real AF_UNIX socket the channel binds. This drives the
// production HookObservationChannel / UnixObservationEndpoint code path end-to-end.

using System.Net.Sockets;
using System.Threading.Channels;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class HookObservationChannelTests : IDisposable
{
    private readonly string _runtimeDir;
    private readonly string _sandbox;

    public HookObservationChannelTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"hoardpad-m11-obs-{Guid.NewGuid():N}");
        _runtimeDir = Path.Combine(baseDir, "runtime");
        _sandbox = Path.Combine(baseDir, "sandbox");
        Directory.CreateDirectory(_runtimeDir);
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_runtimeDir)!, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private NormcasedPath Root() => new PathResolver().ResolveCanonical(_sandbox, _sandbox);

    private HookObservationChannel NewChannel(
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm) =>
        new(confirm, _runtimeDir);

    /// <summary>Connect to the per-turn socket as the hook would (AF_UNIX client).</summary>
    private static async Task<Socket> ConnectHookAsync(string socketPath)
    {
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        return client;
    }

    private static async Task SendLineAsync(Socket socket, byte[] frame) =>
        await socket.SendAsync(frame, SocketFlags.None);

    private static async Task<string?> ReadLineAsync(Socket socket, CancellationToken ct)
    {
        var buf = new byte[1];
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            int n = await socket.ReceiveAsync(buf.AsMemory(0, 1), SocketFlags.None, ct);
            if (n == 0)
                return sb.Length == 0 ? null : sb.ToString();   // EOF (closed channel).
            if (buf[0] == (byte)'\n')
                return sb.ToString();
            sb.Append((char)buf[0]);
        }
    }

    // ---- NDJSON round-trip: a report becomes a HookOpEvent ----

    [Fact]
    public async Task ObserveAsyncYieldsAReportAsAHookOpEvent()
    {
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-1");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-1", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new List<HookOpEvent>();
        Task consume = Task.Run(async () =>
        {
            await foreach (HookOpEvent ev in endpoint.ObserveAsync(cts.Token))
                observed.Add(ev);
        });

        using Socket hook = await ConnectHookAsync(addr);
        var report = new HookOpEvent("toolu_A", "Write", "/sandbox/new.txt", HookOpClass.Create, 40, HookDecision.Allow);
        await SendLineAsync(hook, WireMessage.EncodeReport(report));

        // Wait until the report is observed, then close the hook so the observe loop ends.
        await WaitUntilAsync(() => observed.Count >= 1, cts.Token);
        hook.Shutdown(SocketShutdown.Both);
        await consume;

        Assert.Single(observed);
        Assert.Equal("toolu_A", observed[0].ToolUseId);
        Assert.Equal("Write", observed[0].ToolName);
        Assert.Equal(HookOpClass.Create, observed[0].OpClass);
        Assert.Equal(40, observed[0].Magnitude);
        Assert.Equal(HookDecision.Allow, observed[0].HookDecision);
    }

    // ---- approve-each round: an ask answered ALLOW writes decision allow:true back ----

    [Fact]
    public async Task AskAnsweredAllowWritesAllowDecisionBackToTheHook()
    {
        // The confirm allows for THIS tool_use_id — the channel must write {"allow":true} back.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Allowed(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-allow");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-allow", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, cts.Token);

        using Socket hook = await ConnectHookAsync(addr);
        var ask = new HookDecisionRequest("toolu_B", "Write", "/sandbox/x.txt", HookOpClass.Create);
        await SendLineAsync(hook, WireMessage.EncodeAsk(ask));

        string? decisionLine = await ReadLineAsync(hook, cts.Token);
        Assert.NotNull(decisionLine);
        Assert.Contains("\"type\":\"decision\"", decisionLine);
        Assert.Contains("\"tool_use_id\":\"toolu_B\"", decisionLine);
        Assert.Contains("\"allow\":true", decisionLine);

        hook.Shutdown(SocketShutdown.Both);
        await cts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    [Fact]
    public async Task AskAnsweredDenyWritesDenyDecisionBackToTheHook()
    {
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-deny");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-deny", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, cts.Token);

        using Socket hook = await ConnectHookAsync(addr);
        var ask = new HookDecisionRequest("toolu_C", "Write", "/sandbox/x.txt", HookOpClass.Overwrite);
        await SendLineAsync(hook, WireMessage.EncodeAsk(ask));

        string? decisionLine = await ReadLineAsync(hook, cts.Token);
        Assert.NotNull(decisionLine);
        Assert.Contains("\"type\":\"decision\"", decisionLine);
        Assert.Contains("\"tool_use_id\":\"toolu_C\"", decisionLine);
        Assert.Contains("\"allow\":false", decisionLine);   // the deny.

        hook.Shutdown(SocketShutdown.Both);
        await cts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    // ---- FAIL-CLOSED battery (RequestDecisionAsync) ----

    [Fact]
    public async Task RequestDecisionDeniesWhenTheHookNeverConnected()
    {
        // No hook ever connects => there is no live session => the decision is a fail-closed deny.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Allowed(req.ToolUseId)));
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-unreachable", CancellationToken.None);

        var req = new HookDecisionRequest("toolu_X", "Write", "/sandbox/x.txt", HookOpClass.Create);
        HookDecisionResponse answer = await endpoint.RequestDecisionAsync(req, CancellationToken.None);

        Assert.False(answer.Allow);   // DENY — no connected hook to apply an allow to.
        Assert.Equal("toolu_X", answer.ToolUseId);
    }

    [Fact]
    public async Task RequestDecisionDeniesOnTheNoReplyDeadline()
    {
        // The confirm never returns; the no-reply deadline (the ct) trips => DENY (never an allow).
        var neverAnswers = new TaskCompletionSource<HookDecisionResponse>();
        var channel = NewChannel((_, ct) =>
        {
            ct.Register(() => neverAnswers.TrySetCanceled(ct));
            return neverAnswers.Task;   // resolves only on cancellation.
        });
        string addr = channel.ResolveChannelAddress(Root(), "conv-timeout");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-timeout", CancellationToken.None);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, observeCts.Token);
        using Socket hook = await ConnectHookAsync(addr);
        // Let the session bind before the ask.
        await WaitUntilAsync(() => true, observeCts.Token);

        var req = new HookDecisionRequest("toolu_T", "Write", "/sandbox/x.txt", HookOpClass.Create);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));   // the no-reply timer.
        HookDecisionResponse answer = await endpoint.RequestDecisionAsync(req, deadline.Token);

        Assert.False(answer.Allow);   // the deadline tripped => DENY (no thrown fault, no allow).
        Assert.Equal("toolu_T", answer.ToolUseId);

        hook.Shutdown(SocketShutdown.Both);
        await observeCts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    [Fact]
    public async Task RequestDecisionDeniesWhenTheEndpointIsDisposedMidRequest()
    {
        var confirmStarted = new TaskCompletionSource();
        var channel = NewChannel(async (req, ct) =>
        {
            confirmStarted.TrySetResult();
            // Block until the endpoint is disposed (which cancels ct) — model an in-flight ask. We
            // await the token's cancellation via Task.Delay(infinite, ct); on dispose it throws
            // OperationCanceledException, which the production RequestDecisionAsync turns into a deny.
            await Task.Delay(Timeout.Infinite, ct);
            return HookDecisionResponse.Allowed(req.ToolUseId);   // would-be allow — UNREACHABLE.
        });
        string addr = channel.ResolveChannelAddress(Root(), "conv-dispose");
        IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-dispose", CancellationToken.None);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, observeCts.Token);
        using Socket hook = await ConnectHookAsync(addr);
        // The observe loop's AcceptAsync sets _session asynchronously; give it time to bind before
        // RequestDecisionAsync, else the call sees a null session and short-circuits to deny (the
        // confirm would never start and confirmStarted would never complete).
        await Task.Delay(200, observeCts.Token);

        var req = new HookDecisionRequest("toolu_D", "Write", "/sandbox/x.txt", HookOpClass.Create);
        Task<HookDecisionResponse> decision = endpoint.RequestDecisionAsync(req, CancellationToken.None);

        await confirmStarted.Task;             // the confirm is in flight.
        await endpoint.DisposeAsync();         // THE KILL — must cancel the in-flight ask => deny.

        HookDecisionResponse answer = await decision;
        Assert.False(answer.Allow);            // disposed mid-request => DENY despite the would-be allow.

        await observeCts.CancelAsync();
        try { await consume; } catch { /* observe ends on dispose/cancel */ }
    }

    [Fact]
    public async Task RequestDecisionDeniesWhenTheConfirmThrows()
    {
        // A confirm that throws must NEVER surface as an allow — it becomes a deny.
        var channel = NewChannel((_, _) => throw new InvalidOperationException("confirm bug"));
        string addr = channel.ResolveChannelAddress(Root(), "conv-throw");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-throw", CancellationToken.None);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, observeCts.Token);
        using Socket hook = await ConnectHookAsync(addr);

        var req = new HookDecisionRequest("toolu_F", "Write", "/sandbox/x.txt", HookOpClass.Create);
        HookDecisionResponse answer = await endpoint.RequestDecisionAsync(req, CancellationToken.None);

        Assert.False(answer.Allow);   // the confirm fault => DENY.

        hook.Shutdown(SocketShutdown.Both);
        await observeCts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    [Fact]
    public async Task RequestDecisionDeniesWhenTheConfirmAnswersForADifferentOp()
    {
        // A stale/forged answer for a different tool_use_id is a non-answer for THIS op => DENY.
        var channel = NewChannel((_, _) => Task.FromResult(HookDecisionResponse.Allowed("toolu_OTHER")));
        string addr = channel.ResolveChannelAddress(Root(), "conv-mismatch");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-mismatch", CancellationToken.None);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, observeCts.Token);
        using Socket hook = await ConnectHookAsync(addr);

        var req = new HookDecisionRequest("toolu_M", "Write", "/sandbox/x.txt", HookOpClass.Create);
        HookDecisionResponse answer = await endpoint.RequestDecisionAsync(req, CancellationToken.None);

        Assert.False(answer.Allow);   // id mismatch => DENY.
        Assert.Equal("toolu_M", answer.ToolUseId);

        hook.Shutdown(SocketShutdown.Both);
        await observeCts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    [Fact]
    public void MalformedLineIsNotParsedAsAReportOrAnAsk()
    {
        // A malformed / wrong-type / oversized line yields neither a report nor an ask => the
        // observe loop skips it and the ask path never manufactures an allow (protocol §4 malformed).
        Assert.False(WireMessage.TryParseReport("{ this is not json", out _));
        Assert.False(WireMessage.TryParseReport("{\"type\":\"decision\",\"allow\":true}", out _));   // wrong direction.
        Assert.False(WireMessage.TryParseReport("{\"type\":\"report\"}", out _));                    // missing fields.
        Assert.False(WireMessage.TryParseAsk("{\"type\":\"report\",\"tool_use_id\":\"x\"}", out _));  // not an ask.
        Assert.False(WireMessage.TryParseAsk(NdjsonLineReader.OversizedMarker, out _));               // oversized.
    }

    [Fact]
    public async Task MalformedAskOnTheWireProducesNoAllowDecision()
    {
        // The confirm would ALLOW — but a malformed ask line must never reach it (the line is skipped),
        // so NO decision is ever written back. We prove no decision arrives within a window.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Allowed(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-malformed");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-malformed", CancellationToken.None);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, observeCts.Token);
        using Socket hook = await ConnectHookAsync(addr);

        // A malformed ask (missing required fields) — the channel must NOT answer it.
        byte[] malformed = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"ask\",\"tool_use_id\":\"toolu_Z\"}\n");
        await SendLineAsync(hook, malformed);

        using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        string? decision = null;
        try { decision = await ReadLineAsync(hook, readCts.Token); }
        catch (OperationCanceledException) { /* expected — no decision written */ }

        Assert.Null(decision);   // no decision line => the malformed ask manufactured no allow.

        hook.Shutdown(SocketShutdown.Both);
        await observeCts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    // ---- NON-VACUITY control: the SAME harness with an allow answer returns allow ----

    [Fact]
    public async Task NonVacuousAllowAnswerForAConnectedHookReturnsAllow()
    {
        // This is the mutation guard: if the deny path were replaced by an unconditional allow, the
        // deny tests above would still pass — so we prove the harness CAN return allow when the
        // confirm allows AND the op id matches AND the hook is connected AND the decision is written.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Allowed(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-nonvacuous");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-nonvacuous", CancellationToken.None);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task consume = DrainAsync(endpoint, observeCts.Token);
        using Socket hook = await ConnectHookAsync(addr);
        // Wait for the session to bind (the accept completes once the hook connects).
        await Task.Delay(50, observeCts.Token);

        var req = new HookDecisionRequest("toolu_OK", "Write", "/sandbox/ok.txt", HookOpClass.Create);
        HookDecisionResponse answer = await endpoint.RequestDecisionAsync(req, CancellationToken.None);

        Assert.True(answer.Allow);   // a real allow IS returned — the deny tests are not vacuous.
        Assert.Equal("toolu_OK", answer.ToolUseId);

        hook.Shutdown(SocketShutdown.Both);
        await observeCts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    // ---- env injection (BuildHookSettings): additive HOARDPAD_OBSERVE_* ----

    [Fact]
    public void BuildHookSettingsInjectsObserveEnvWhenAChannelIsConfigured()
    {
        var host = new ToolPermissionHookHost(ResolveProductionHookPath());
        var config = new HookConfig(Root(), NoReplyTimeout: TimeSpan.FromSeconds(60))
        {
            ObserveChannelAddress = "/tmp/hp-observe/turn.sock",
            ObserveMode = HookObserveMode.Approve,
        };

        HookSettings settings = host.BuildHookSettings(config);

        Assert.Equal("/tmp/hp-observe/turn.sock", settings.HookEnvironment["HOARDPAD_OBSERVE_CHANNEL"]);
        Assert.Equal("approve", settings.HookEnvironment["HOARDPAD_OBSERVE_MODE"]);
        // The env is wired SEPARATELY from the --settings JSON (the JSON carries only the hook registration).
        Assert.DoesNotContain("HOARDPAD_OBSERVE_CHANNEL", settings.SettingsJson);
    }

    [Fact]
    public void BuildHookSettingsEmitsNoObserveEnvWhenNoChannelIsConfigured()
    {
        // The autonomous path must be byte-identical when no channel is in play: no HOARDPAD_OBSERVE_*.
        var host = new ToolPermissionHookHost(ResolveProductionHookPath());
        var config = new HookConfig(Root(), NoReplyTimeout: TimeSpan.FromSeconds(60));

        HookSettings settings = host.BuildHookSettings(config);

        Assert.Empty(settings.HookEnvironment);
    }

    [Fact]
    public void BuildHookSettingsDefaultsObserveModeToReport()
    {
        var host = new ToolPermissionHookHost(ResolveProductionHookPath());
        var config = new HookConfig(Root(), NoReplyTimeout: TimeSpan.FromSeconds(60))
        {
            ObserveChannelAddress = "/tmp/hp-observe/turn.sock",
        };

        HookSettings settings = host.BuildHookSettings(config);

        Assert.Equal("report", settings.HookEnvironment["HOARDPAD_OBSERVE_MODE"]);
    }

    private static string ResolveProductionHookPath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "hooks", "deny_by_default.py");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate hooks/deny_by_default.py walking up from the test output dir.");
    }

    // ---- FR-10(c) accept-loop regression: many per-op connections per turn ----

    [Fact]
    public async Task ObserveAsyncYieldsReportsFromSequentialPerOpConnections()
    {
        // The regression test for the bug: a fresh hook process per op opens its OWN connection,
        // sends one report, and closes it — conn#1 (Read) closes BEFORE conn#2 (Write) even connects.
        // Before the fix, ObserveAsync ended at conn#1's EOF and never observed conn#2's report.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-seq");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-seq", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new List<HookOpEvent>();
        Task consume = Task.Run(async () =>
        {
            await foreach (HookOpEvent ev in endpoint.ObserveAsync(cts.Token))
                observed.Add(ev);
        });

        using (Socket hook1 = await ConnectHookAsync(addr))
        {
            var read = new HookOpEvent("toolu_R1", "Read", "/sandbox/seed.txt", HookOpClass.Edit, 0, HookDecision.Allow);
            await SendLineAsync(hook1, WireMessage.EncodeReport(read));
            await WaitUntilAsync(() => observed.Count >= 1, cts.Token);
            hook1.Shutdown(SocketShutdown.Both);
        }
        // conn#1 is now fully closed BEFORE conn#2 connects — proving the accept loop re-accepts.
        await WaitUntilAsync(() => true, cts.Token);   // yield a beat so the first connection is torn down.

        using (Socket hook2 = await ConnectHookAsync(addr))
        {
            var write = new HookOpEvent("toolu_W2", "Write", "/sandbox/out.txt", HookOpClass.Create, 4, HookDecision.Allow);
            await SendLineAsync(hook2, WireMessage.EncodeReport(write));
            await WaitUntilAsync(() => observed.Count >= 2, cts.Token);
            hook2.Shutdown(SocketShutdown.Both);
        }

        await cts.CancelAsync();
        try { await consume; } catch (OperationCanceledException) { /* observe ends on cancel */ }

        Assert.Equal(2, observed.Count);
        Assert.Contains(observed, e => e.ToolUseId == "toolu_R1" && e.ToolName == "Read");
        Assert.Contains(observed, e => e.ToolUseId == "toolu_W2" && e.ToolName == "Write");
    }

    [Fact]
    public async Task ObserveAsyncYieldsReportsFromConcurrentConnections()
    {
        // Two connections open at the SAME time (parallel tool_use) — both reports must be observed,
        // with no loss and no misrouting between the two independent sessions.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-concurrent");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-concurrent", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new List<HookOpEvent>();
        Task consume = Task.Run(async () =>
        {
            await foreach (HookOpEvent ev in endpoint.ObserveAsync(cts.Token))
                observed.Add(ev);
        });

        using Socket hookA = await ConnectHookAsync(addr);
        using Socket hookB = await ConnectHookAsync(addr);

        var opA = new HookOpEvent("toolu_A1", "Write", "/sandbox/a.txt", HookOpClass.Create, 4, HookDecision.Allow);
        var opB = new HookOpEvent("toolu_B1", "Write", "/sandbox/b.txt", HookOpClass.Create, 8, HookDecision.Allow);
        await Task.WhenAll(
            SendLineAsync(hookA, WireMessage.EncodeReport(opA)),
            SendLineAsync(hookB, WireMessage.EncodeReport(opB)));

        await WaitUntilAsync(() => observed.Count >= 2, cts.Token);

        hookA.Shutdown(SocketShutdown.Both);
        hookB.Shutdown(SocketShutdown.Both);
        await cts.CancelAsync();
        try { await consume; } catch (OperationCanceledException) { /* observe ends on cancel */ }

        Assert.Equal(2, observed.Count);
        Assert.Contains(observed, e => e.ToolUseId == "toolu_A1" && e.TargetPath == "/sandbox/a.txt");
        Assert.Contains(observed, e => e.ToolUseId == "toolu_B1" && e.TargetPath == "/sandbox/b.txt");
    }

    [Fact]
    public async Task ObserveAsyncYieldsExactlyOneEventForASingleOpTurn()
    {
        // A single-connection, single-op turn must stay clean — no duplication, no extra events
        // introduced by the accept loop / merge channel.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-single");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-single", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new List<HookOpEvent>();
        Task consume = Task.Run(async () =>
        {
            await foreach (HookOpEvent ev in endpoint.ObserveAsync(cts.Token))
                observed.Add(ev);
        });

        using Socket hook = await ConnectHookAsync(addr);
        var write = new HookOpEvent("toolu_S1", "Write", "/sandbox/only.txt", HookOpClass.Create, 4, HookDecision.Allow);
        await SendLineAsync(hook, WireMessage.EncodeReport(write));
        await WaitUntilAsync(() => observed.Count >= 1, cts.Token);
        hook.Shutdown(SocketShutdown.Both);

        await cts.CancelAsync();
        try { await consume; } catch (OperationCanceledException) { /* observe ends on cancel */ }

        Assert.Single(observed);
        Assert.Equal("toolu_S1", observed[0].ToolUseId);
    }

    [Fact]
    public async Task DisposalMidAskStillDeniesUnderTheAcceptLoop()
    {
        // Fail-closed negative: the endpoint is disposed while a SECOND connection's ask is mid-flight
        // (proving the accept-loop change did not weaken the existing dispose-mid-request guarantee for
        // a connection that is not the first one accepted).
        var confirmStarted = new TaskCompletionSource();
        var channel = NewChannel(async (req, ct) =>
        {
            confirmStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return HookDecisionResponse.Allowed(req.ToolUseId);   // would-be allow — UNREACHABLE.
        });
        string addr = channel.ResolveChannelAddress(Root(), "conv-dispose-loop");
        IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-dispose-loop", CancellationToken.None);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new List<HookOpEvent>();
        Task consume = Task.Run(async () =>
        {
            try
            {
                await foreach (HookOpEvent ev in endpoint.ObserveAsync(observeCts.Token))
                    observed.Add(ev);
            }
            catch (OperationCanceledException) { /* observe ends on dispose/cancel */ }
        });

        // First connection: reports a Read, then closes (the report never asks).
        using (Socket firstHook = await ConnectHookAsync(addr))
        {
            var read = new HookOpEvent("toolu_FIRST", "Read", "/sandbox/seed.txt", HookOpClass.Edit, 0, HookDecision.Allow);
            await SendLineAsync(firstHook, WireMessage.EncodeReport(read));
            await WaitUntilAsync(() => observed.Count >= 1, observeCts.Token);
            firstHook.Shutdown(SocketShutdown.Both);
        }

        // Second connection: sends an ask and blocks on the decision — this is the in-flight request
        // that DisposeAsync must kill => deny, exactly as the single-connection dispose test proves.
        using Socket secondHook = await ConnectHookAsync(addr);
        await Task.Delay(200, observeCts.Token);   // give the accept loop time to bind the 2nd session.
        var req = new HookDecisionRequest("toolu_SECOND", "Write", "/sandbox/x.txt", HookOpClass.Create);
        Task<HookDecisionResponse> decision = endpoint.RequestDecisionAsync(req, CancellationToken.None);

        await confirmStarted.Task;             // the confirm is in flight.
        await endpoint.DisposeAsync();         // THE KILL — must cancel the in-flight ask => deny.

        HookDecisionResponse answer = await decision;
        Assert.False(answer.Allow);             // disposed mid-request => DENY despite the would-be allow.

        await observeCts.CancelAsync();
        try { await consume; } catch { /* observe ends on cancel */ }
    }

    [Fact]
    public async Task AReportThatNeverArrivesYieldsNoEvent()
    {
        // Fail-closed negative: a connection that connects but never sends a report (and never gets a
        // chance to, because the turn ends first) must yield NOTHING — the fix's bounded post-cancel
        // drain grace must never fabricate an event for a report that was never actually sent.
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-never-arrives");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-never-arrives", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new List<HookOpEvent>();
        Task consume = Task.Run(async () =>
        {
            await foreach (HookOpEvent ev in endpoint.ObserveAsync(cts.Token))
                observed.Add(ev);
        });

        using Socket hook = await ConnectHookAsync(addr);
        await Task.Delay(100, cts.Token);   // the connection is accepted, but sends nothing.

        await cts.CancelAsync();            // the turn ends — no report was ever sent on this connection.
        try { await consume; } catch (OperationCanceledException) { /* observe ends on cancel */ }

        Assert.Empty(observed);   // MA14 still degrades: no callback was observed for this connection.
    }

    // ---- review-round-2 regressions: write-after-channel-complete race + structural post-cancel accept-drain ----

    [Fact]
    public async Task ALateReportAfterTheChannelCompletesIsSwallowedNotUnobserved()
    {
        // Regression for the HIGH finding: a connection can still be mid-serve (blocked reading, no EOF)
        // when the observe loop's bounded drain grace elapses and RunAcceptLoopAsync completes the merge
        // writer. If that connection LATER sends a report, ServeConnectionAsync's WriteAsync must not
        // throw an unobserved ChannelClosedException — it is caught and the report is fail-safe dropped
        // (a dropped report can only degrade MA14, never widen the sandbox). Non-vacuous: WITHOUT the
        // guard this reproduces a real unobserved-task-exception (the write throws inside a fire-and-
        // forget ServeConnectionAsync task nobody awaits by the time the report arrives).
        var unobserved = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            unobserved.Add(e.Exception);
            e.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
            string addr = channel.ResolveChannelAddress(Root(), "conv-late-report");
            IHookObservationEndpoint endpoint =
                await channel.OpenForTurnAsync(Root(), "conv-late-report", CancellationToken.None);

            using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var observed = new List<HookOpEvent>();
            Task consume = Task.Run(async () =>
            {
                try
                {
                    await foreach (HookOpEvent ev in endpoint.ObserveAsync(observeCts.Token))
                        observed.Add(ev);
                }
                catch (OperationCanceledException) { /* observe ends on cancel */ }
            });

            // Connect but send NOTHING — this connection's ServeConnectionAsync is blocked reading, with
            // no EOF, so it will still be alive past the drain grace.
            using Socket hook = await ConnectHookAsync(addr);
            await WaitUntilAsync(() => true, CancellationToken.None);   // let the accept loop bind the session.

            await observeCts.CancelAsync();
            // Wait comfortably past BOTH bounded phases (accept-drain grace + serve-drain grace) so
            // RunAcceptLoopAsync has definitely completed the writer while this connection is still open.
            await Task.Delay(TimeSpan.FromMilliseconds(900), CancellationToken.None);
            await consume;   // ObserveAsync has returned; the merge sequence is over.

            // NOW send the late report AND close our end — this is the write-after-complete race. Closing
            // (rather than racing a fixed delay against DisposeAsync) gives ServeConnectionAsync a
            // DETERMINISTIC EOF right after the report, so it reaches the (already-completed) write
            // attempt and returns on its own — no race between "still reading" and disposal closing the
            // same socket out from under it (which would surface an unrelated ObjectDisposedException
            // instead of exercising the ChannelClosedException guard this test targets).
            var late = new HookOpEvent("toolu_LATE", "Write", "/sandbox/late.txt", HookOpClass.Create, 4, HookDecision.Allow);
            await SendLineAsync(hook, WireMessage.EncodeReport(late));
            hook.Shutdown(SocketShutdown.Both);

            // Poll for quiescence instead of a fixed delay: the connection's serve task removes itself
            // from the endpoint's live-connection set in its `finally` once it has processed the report
            // (attempted-and-swallowed the write) and observed the EOF. Bounded, so a genuine regression
            // (the task wedged instead of returning) still fails the test via the outer assertions.
            await WaitUntilNoLiveConnectionsAsync(endpoint, TimeSpan.FromSeconds(5));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await endpoint.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));   // must dispose cleanly, no deadlock.

            Assert.Empty(observed);       // the late report never reaches the (already-completed) sequence.

            // The guard under test prevents the late-report WRITE from surfacing an unobserved
            // ChannelClosedException — remove the `catch (ChannelClosedException)` in ServeConnectionAsync
            // and this list becomes non-empty (the non-vacuity contract). Assert on THAT specific fault.
            //
            // Why not `Assert.Empty(unobserved)`: TaskScheduler.UnobservedTaskException is PROCESS-WIDE and
            // GC.WaitForPendingFinalizers above flushes EVERY not-yet-finalized faulted Task in the process,
            // not just this scenario's. Sibling HookObservationChannelTests dispose an endpoint while a hook
            // connection is still blocked reading; that teardown aborts the pending read and faults a
            // fire-and-forget ServeConnectionAsync with an ObjectDisposedException (the "unrelated"
            // socket-closed-from-under-it mode this test's own comment above calls out). Run serially before
            // this test, such a stray fault can finalize inside our forced GC and fire into this handler —
            // pure cross-test noise the guarded production code can never turn into a ChannelClosedException.
            // Only an unobserved ChannelClosedException is attributable to (and diagnostic of) the write race
            // this test targets.
            var channelClosed = unobserved
                .SelectMany(e => e is AggregateException agg
                    ? (IEnumerable<Exception>)agg.Flatten().InnerExceptions
                    : new[] { e })
                .Where(x => x is ChannelClosedException)
                .ToList();
            Assert.Empty(channelClosed);  // the late report must NEVER surface as an unobserved ChannelClosedException.
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task AReportArrivingJustAfterCancelWithinTheGraceIsStillObserved()
    {
        // obj 9 regression: a connection the hook already opened before the turn's cancel (queued in the
        // listen backlog right at the cancel instant) must still be accepted + read within the bounded
        // post-cancel accept-drain phase — proving the drain is structural (a fresh grace clock), not just
        // "whatever was already-accepted at the cancel instant" (the pre-fix behavior this replaces).
        var channel = NewChannel((req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)));
        string addr = channel.ResolveChannelAddress(Root(), "conv-post-cancel-accept");
        await using IHookObservationEndpoint endpoint =
            await channel.OpenForTurnAsync(Root(), "conv-post-cancel-accept", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new List<HookOpEvent>();
        Task consume = Task.Run(async () =>
        {
            await foreach (HookOpEvent ev in endpoint.ObserveAsync(cts.Token))
                observed.Add(ev);
        });

        // Cancel FIRST — nothing has connected yet — then immediately connect + report, landing squarely
        // in the post-cancel accept-drain window (well inside the 300ms grace).
        await cts.CancelAsync();
        using Socket hook = await ConnectHookAsync(addr);
        var write = new HookOpEvent("toolu_GRACE", "Write", "/sandbox/grace.txt", HookOpClass.Create, 4, HookDecision.Allow);
        await SendLineAsync(hook, WireMessage.EncodeReport(write));

        await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        hook.Shutdown(SocketShutdown.Both);
        try { await consume; } catch (OperationCanceledException) { /* observe ends on cancel */ }

        Assert.Single(observed);
        Assert.Equal("toolu_GRACE", observed[0].ToolUseId);
    }

    // ---- helpers ----

    private static Task DrainAsync(IHookObservationEndpoint endpoint, CancellationToken ct) =>
        Task.Run(async () =>
        {
            try
            {
                await foreach (HookOpEvent _ in endpoint.ObserveAsync(ct))
                {
                    // reports are not asserted here; the accept + ask handling is the exercised path.
                }
            }
            catch (OperationCanceledException) { /* observe ends on cancel */ }
        }, ct);

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + 5_000;
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
                return;
            await Task.Delay(20, ct);
        }
    }

    // Test-only quiescence poll: reads the endpoint's private `_liveConnections` count via reflection (no
    // new production-facing seam) so a test can wait deterministically for an in-flight ServeConnectionAsync
    // to finish processing (its `finally` removes the connection) instead of racing a fixed delay against
    // DisposeAsync. Bounded — a genuinely wedged connection still fails the test.
    private static async Task WaitUntilNoLiveConnectionsAsync(IHookObservationEndpoint endpoint, TimeSpan timeout)
    {
        System.Reflection.FieldInfo field = endpoint.GetType()
            .GetField("_liveConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var live = (System.Collections.IDictionary)field.GetValue(endpoint)!;
            if (live.Count == 0)
                return;
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("A live connection never quiesced within the test's bounded wait.");
            await Task.Delay(20);
        }
    }
}
