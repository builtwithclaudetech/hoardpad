// HookObservationEndpoints.cs — the two per-turn transport endpoints (Step 2a).
//
//   UnixObservationEndpoint  (linux-x64, RUNS): an AF_UNIX / SOCK_STREAM listener at a per-turn
//                            socket path. ACCEPT-LOOPS for the turn's life: the real `claude` CLI
//                            runs a FRESH hook process per PreToolUse op, and each process opens its
//                            OWN connection, sends exactly one report, and closes it (FR-10(c) fix —
//                            docs/phase0-fr10c-findings.md). Every accepted connection is served
//                            concurrently on its own HookObservationSession/NetworkStream; all of
//                            their HookOpEvents funnel into one merge Channel<HookOpEvent> that
//                            ObserveAsync drains — mirroring HoardPad.App.Ai.AiTurnRunner's
//                            merge-channel producer/reader shape.
//   WindowsObservationEndpoint (win-x64, COMPILE-ONLY / runtime DEFERRED): a NamedPipeServerStream
//                            leg, guarded so it compiles for win-x64 and never runs on Linux. Mirrors
//                            the SAME accept-loop / per-connection / merge-channel shape: each accepted
//                            client gets a fresh NamedPipeServerStream instance so more than one
//                            connection can be served per turn.
//
// FAIL-CLOSED LIFECYCLE (protocol §4): DisposeAsync tears down the listener AND every live connection.
// A still-blocked hook then reads a closed channel => DENY. The ask round is driven INTERNALLY by each
// connection's read loop (an `ask` line => session.RequestDecisionAsync => the App confirm => the
// decision line written back); RequestDecisionAsync is also exposed on the seam for the host, targeting
// the most-recently-published live session.

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// The linux-x64 per-turn <c>AF_UNIX</c> endpoint (runs). Accept-loops at a per-turn socket path for
/// the turn's life, serving each connection concurrently (a fresh hook process per PreToolUse op opens
/// its own connection — protocol §1.1's "a fresh hook process per invocation MAY reconnect"). All
/// connections' reports fold into one merge sequence via <see cref="ObserveAsync"/>; the approve-each
/// ask round is driven internally and also exposed via <see cref="RequestDecisionAsync"/>. Disposal
/// closes the listener + every live connection — a still-blocked hook then denies (protocol §4).
/// </summary>
internal sealed class UnixObservationEndpoint : IHookObservationEndpoint
{
    // Gates ONLY the fail-SAFE post-cancel accept/drain tail below — it NEVER gates the fail-closed
    // decision path (RequestDecisionAsync / the hook's own timeout still deny on their own terms
    // regardless of this value). A too-short grace can only DROP a report that genuinely arrived late
    // (=> a correct MA14 degrade, never a false allow); it can never turn a missing report into an
    // allow. 300ms is generous: a live probe measured ~6.8s of slack between a last-write hook firing
    // and stdout EOF (docs/phase0-fr10c-findings.md), and an AF_UNIX loopback connect+send is sub-ms —
    // 300ms comfortably covers scheduling jitter without meaningfully delaying turn teardown.
    private static readonly TimeSpan PostCancelDrainGrace = TimeSpan.FromMilliseconds(300);

    private readonly string _socketPath;
    private readonly Socket _listener;
    private readonly Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> _confirm;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, LiveConnection> _liveConnections = new();
    private long _nextConnectionId;

    private HookObservationSession? _session;
    private int _disposed;

    private UnixObservationEndpoint(
        string socketPath,
        Socket listener,
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm)
    {
        _socketPath = socketPath;
        _listener = listener;
        _confirm = confirm;
    }

    internal static UnixObservationEndpoint Open(
        string socketPath,
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm)
    {
        // A stale socket file from a crashed prior turn would make bind() fail with EADDRINUSE; the
        // path is per-turn + owned by HoardPad, so unlinking a leftover is safe and intended.
        TryDeleteFile(socketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        // A fresh hook process per op may connect near-simultaneously with another op's hook process
        // (parallel tool_use). AF_UNIX SOCK_STREAM REFUSES (never retries) a connect once the backlog is
        // full, so a backlog of 1 could genuinely lose a connection's report. 16 gives a safe margin for
        // the per-turn op fan-out this endpoint now expects to accept concurrently.
        listener.Listen(backlog: 16);
        // Restrict the socket node to the current user (no other local user may connect + impersonate
        // the hook on the safety path). Best-effort; the per-turn dir is already 0700.
        HookObservationChannel.TrySetOwnerOnly(socketPath, isDirectory: false);

        return new UnixObservationEndpoint(socketPath, listener, confirm);
    }

    public async IAsyncEnumerable<HookOpEvent> ObserveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);

        var merge = Channel.CreateUnbounded<HookOpEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,   // many concurrently-served connections may write.
        });

        Task acceptLoop = RunAcceptLoopAsync(merge.Writer, linked.Token);

        await foreach (HookOpEvent ev in merge.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            yield return ev;

        await AwaitQuietlyAsync(acceptLoop).ConfigureAwait(false);
    }

    // Accept connections for the turn's life, then close the tail structurally in two bounded phases
    // (obj 9): (1) a POST-CANCEL ACCEPT-DRAIN — a connection the hook already opened before the turn's
    // cancel (e.g. queued in the listen backlog right at stdout-EOF) still gets accepted + read within a
    // short fresh-clocked grace, not just already-accepted connections; (2) the existing per-connection
    // SERVE-DRAIN, which lets already-in-flight serve tasks (from either phase) finish reading an
    // already-buffered report. Neither phase can fabricate an event — a report that genuinely never
    // arrives still yields nothing (fail-closed).
    private async Task RunAcceptLoopAsync(ChannelWriter<HookOpEvent> writer, CancellationToken ct)
    {
        var inFlight = new ConcurrentDictionary<long, Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket? conn = await TryAcceptAsync(ct).ConfigureAwait(false);
                if (conn is null)
                    break;   // turn ended / Stop / disposed — stop accepting new connections.

                StartServing(conn, writer, inFlight);
            }

            // (1) Post-cancel accept-drain: a connection may already be sitting in the listen backlog at
            // the instant of cancel (the hook connected before the turn ended but the accept loop hadn't
            // reached it yet). Use a FRESH grace clock (NOT the already-cancelled `ct`) so this phase is
            // bounded independently of the turn's own cancellation instant.
            using (var graceCts = new CancellationTokenSource(PostCancelDrainGrace))
            {
                while (true)
                {
                    Socket? conn = await TryAcceptAsync(graceCts.Token).ConfigureAwait(false);
                    if (conn is null)
                        break;   // grace elapsed / listener disposed — no more backlogged connections.

                    StartServing(conn, writer, inFlight);
                }
            }

            // (2) Serve-drain: let every in-flight serve task (from the main loop OR the accept-drain
            // above) finish reading an already-buffered report before this loop completes the writer.
            await DrainRemainingAsync(inFlight, PostCancelDrainGrace).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    // Accept one connection, or return null on any terminal condition (cancelled deadline / disposed
    // listener) — the caller treats null as "stop this phase", never as a fabricated event.
    private async Task<Socket?> TryAcceptAsync(CancellationToken ct)
    {
        try
        {
            return await _listener.AcceptAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;   // listener torn down mid-accept (disposed).
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void StartServing(Socket conn, ChannelWriter<HookOpEvent> writer, ConcurrentDictionary<long, Task> inFlight)
    {
        long connectionId = Interlocked.Increment(ref _nextConnectionId);
        var stream = new NetworkStream(conn, ownsSocket: false);
        var live = new LiveConnection(conn, stream);
        _liveConnections[connectionId] = live;

        var session = new HookObservationSession(stream, RunConfirmRound, IsTorndown);
        // Publish the bound session with a release barrier: RequestDecisionAsync reads it on another
        // thread (the App's confirm), so it must observe the fully-constructed session, not a tearing
        // read. The MOST RECENT connection's session is the one an in-flight ask is answered against
        // (single-connection tests keep passing unmodified).
        Volatile.Write(ref _session, session);

        inFlight[connectionId] = ServeConnectionAsync(connectionId, live, session, writer, inFlight);
    }

    private static async Task DrainRemainingAsync(ConcurrentDictionary<long, Task> inFlight, TimeSpan grace)
    {
        Task[] pending = inFlight.Values.ToArray();
        if (pending.Length == 0)
            return;

        using var graceCts = new CancellationTokenSource(grace);
        try
        {
            await Task.WhenAll(pending).WaitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The grace window elapsed with a connection still serving (e.g. a hook that never sends a
            // report and never closes). We stop waiting — that connection's report, if it never arrives,
            // correctly yields no event (fail-closed); DisposeAsync will forcibly close it at turn end.
        }
    }

    // Serve one accepted connection: fold its report(s) into the merge channel until its EOF/fault, then
    // remove it from the live-connection set. Each connection has its OWN session/stream — no shared
    // mutable session/stream field is read by two connections concurrently.
    private async Task ServeConnectionAsync(
        long connectionId, LiveConnection live, HookObservationSession session,
        ChannelWriter<HookOpEvent> writer, ConcurrentDictionary<long, Task> inFlight)
    {
        try
        {
            // CancellationToken.None: once a report is dequeued from this connection's session it must
            // finish its write to the merge channel even if the outer observe token cancels concurrently
            // (mirrors AiTurnRunner.PumpObserveAsync's writer discipline) — no accepted report is dropped
            // by a race between "cancel just fired" and "already had a report in hand".
            await foreach (HookOpEvent ev in session.ObserveAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    await writer.WriteAsync(ev, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    // The turn has already ended and RunAcceptLoopAsync completed the writer (this
                    // connection outlived both bounded drain phases — e.g. a slow/stuck hook past the
                    // grace). The report is intentionally, FAIL-SAFE dropped here: a dropped report can
                    // only cost a liveness *correlation* (MA14 degrades the turn), never widen the
                    // sandbox or manufacture an allow. Stop reading this connection — nothing further it
                    // sends can reach the (already-completed) merge sequence either.
                    break;
                }
            }
        }
        finally
        {
            inFlight.TryRemove(connectionId, out _);
            _liveConnections.TryRemove(connectionId, out _);
            TryCloseConnection(live);
        }
    }

    public async Task<HookDecisionResponse> RequestDecisionAsync(
        HookDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // No live session (no connection has connected yet, or the endpoint is disposed) => fail-closed
        // deny. We NEVER surface an allow without a connected hook to which the decision was written. The
        // acquire read pairs with the release Volatile.Write in StartServing (no torn/stale session).
        HookObservationSession? session = Volatile.Read(ref _session);
        if (session is null || Volatile.Read(ref _disposed) != 0)
            return HookDecisionResponse.Deny(request.ToolUseId);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        return await session.RequestDecisionAsync(request, linked.Token).ConfigureAwait(false);
    }

    // The confirm round the read loop invokes for an `ask` (driven inside HookObservationSession):
    // it links the per-call deadline with the endpoint lifetime so disposal cancels an in-flight
    // confirm => deny. This is the SAME delegate the App's confirm supplies; the channel adds no logic.
    private async Task<HookDecisionResponse> RunConfirmRound(HookDecisionRequest request, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        return await _confirm(request, linked.Token).ConfigureAwait(false);
    }

    private bool IsTorndown() => Volatile.Read(ref _disposed) != 0;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        // The disposed flag (set above) makes an in-flight ask's decision write short-circuit
        // (the session's IsTorndown guard), so disposal never blocks on any stream it is tearing down.
        //
        // CANCELLATION OFF THE DISPOSE THREAD (the real deadlock fix). The per-call linked CTSs in
        // RequestDecisionAsync / RunConfirmRound register callbacks on _lifetime.Token. If we ran
        // _lifetime.Cancel() ON THIS THREAD, it would synchronously invoke those registrations while
        // an in-flight await is concurrently disposing the SAME linked CTS (its `using`), and
        // CancellationTokenRegistration.DisposeAsync's WaitForCallbackIfNecessary cross-blocks the two
        // threads. Firing Cancel() on a pool thread frees the dispose thread, so neither side waits on
        // the other. _lifetime is disposed on that SAME pool task AFTER Cancel() returns (the callbacks
        // have then run), so the disposable is always reclaimed without blocking the dispose thread.
        CancellationTokenSource lifetime = _lifetime;
        _ = Task.Run(() =>
        {
            try { lifetime.Cancel(); }
            catch { /* benign already-disposed race */ }
            finally { lifetime.Dispose(); }
        });

        // Tear down EVERY live connection (not just one) — a still-blocked hook on ANY of them then
        // reads a closed channel => deny (protocol §4). ORDER MATTERS per connection: close the SOCKET
        // before the NetworkStream (see TryCloseConnection) for the same reason as the original
        // single-connection code — NetworkStream.ReadAsync on Linux does not honor cancellation for a
        // blocked socket read, so closing the socket first forcibly aborts that pending read.
        foreach (long connectionId in _liveConnections.Keys.ToArray())
        {
            if (_liveConnections.TryRemove(connectionId, out LiveConnection? live))
                TryCloseConnection(live);
        }

        _listener.Dispose();   // aborts a pending AcceptAsync (a turn killed before any hook connected).

        TryDeleteFile(_socketPath);
        return ValueTask.CompletedTask;
    }

    // ORDER MATTERS: close the SOCKET before the NetworkStream. NetworkStream.ReadAsync on Linux does NOT
    // honor the cancellation token for a blocked socket read, so a pending observe-loop ReadAsync is
    // still in flight; NetworkStream.Dispose() would BLOCK waiting for it. Closing the underlying socket
    // first forcibly aborts that pending read (it throws), which unblocks the connection's serve loop AND
    // lets the stream dispose without waiting — the fail-closed teardown: the hook then reads a closed
    // channel => deny (protocol §4).
    private static void TryCloseConnection(LiveConnection live)
    {
        try { live.Socket.Shutdown(SocketShutdown.Both); } catch { /* may already be down */ }
        live.Socket.Dispose();       // aborts the pending NetworkStream.ReadAsync (unblocks the serve loop).
        live.Stream.Dispose();       // now non-blocking — the underlying socket is already closed.
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
#pragma warning disable CA1031 // Best-effort join of the accept loop; a fault there must not fault the observe sequence.
        catch { /* the accept loop's own faults are swallowed at its boundary; nothing more to surface. */ }
#pragma warning restore CA1031
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort unlink of the per-turn socket node */ }
    }

    private sealed record LiveConnection(Socket Socket, NetworkStream Stream);
}

/// <summary>
/// The win-x64 per-turn named-pipe endpoint — COMPILE-CONFIRMED ONLY; runtime DEFERRED (same shape
/// as the Windows Job-Object / MoveFileEx legs). Guarded by <see cref="OperatingSystem.IsWindows"/> +
/// <see cref="SupportedOSPlatformAttribute"/> so it compiles for win-x64 and NEVER runs on Linux.
/// Mirrors the Unix endpoint's accept-loop / per-connection / merge-channel shape: each accepted client
/// gets a FRESH <see cref="System.IO.Pipes.NamedPipeServerStream"/> instance (a single instance serves
/// only one client at a time), so more than one per-turn connection can be served, matching the real
/// `claude` CLI's one-connection-per-hook-process behavior. Wiring the runtime named-pipe accept/serve
/// is the Windows-validation milestone's job; on this box it is authored as a faithful shape over the
/// SAME <see cref="HookObservationSession"/> framing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsObservationEndpoint : IHookObservationEndpoint
{
    // See the Unix endpoint's identical constant for the full rationale: this gates ONLY the fail-SAFE
    // post-cancel accept/drain tail, never the fail-closed decision path; a too-short grace can only
    // DROP a report (=> MA14 degrade), never fail open.
    private static readonly TimeSpan PostCancelDrainGrace = TimeSpan.FromMilliseconds(300);
    private const int MaxServerInstances = 16;

    private readonly string _pipeName;
    private readonly Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> _confirm;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, System.IO.Pipes.NamedPipeServerStream> _liveConnections = new();
    private long _nextConnectionId;

    private HookObservationSession? _session;
    private int _disposed;

    private WindowsObservationEndpoint(
        string pipeName,
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm)
    {
        _pipeName = pipeName;
        _confirm = confirm;
    }

    internal static string PipeName(string turnToken) => $"hoardpad-observe-{turnToken}";

    internal static WindowsObservationEndpoint Open(
        string turnToken,
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm) =>
        new(PipeName(turnToken), confirm);

    // One fresh NamedPipeServerStream instance PER accepted connection (a single instance only ever
    // serves one client — protocol §1.1's per-op reconnect). maxNumberOfServerInstances allows several
    // instances to coexist so concurrent tool_use hook processes are not refused.
    private System.IO.Pipes.NamedPipeServerStream CreateServerInstance() =>
        new(
            _pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            MaxServerInstances,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);

    public async IAsyncEnumerable<HookOpEvent> ObserveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);

        var merge = Channel.CreateUnbounded<HookOpEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        Task acceptLoop = RunAcceptLoopAsync(merge.Writer, linked.Token);

        await foreach (HookOpEvent ev in merge.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            yield return ev;

        await AwaitQuietlyAsync(acceptLoop).ConfigureAwait(false);
    }

    // Mirrors UnixObservationEndpoint.RunAcceptLoopAsync exactly (obj 9): the main accept loop, then a
    // bounded post-cancel accept-drain phase on a FRESH grace clock (a client may already be dialing the
    // pipe at the instant of cancel), then the existing serve-drain, then complete the writer.
    private async Task RunAcceptLoopAsync(ChannelWriter<HookOpEvent> writer, CancellationToken ct)
    {
        var inFlight = new ConcurrentDictionary<long, Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                System.IO.Pipes.NamedPipeServerStream? pipe = await TryAcceptAsync(ct).ConfigureAwait(false);
                if (pipe is null)
                    break;   // turn ended / Stop / disposed — stop accepting new connections.

                StartServing(pipe, writer, inFlight);
            }

            using (var graceCts = new CancellationTokenSource(PostCancelDrainGrace))
            {
                while (true)
                {
                    System.IO.Pipes.NamedPipeServerStream? pipe = await TryAcceptAsync(graceCts.Token).ConfigureAwait(false);
                    if (pipe is null)
                        break;   // grace elapsed / disposed — no more backlogged connections.

                    StartServing(pipe, writer, inFlight);
                }
            }

            await DrainRemainingAsync(inFlight, PostCancelDrainGrace).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    // Create a fresh server instance and wait for its client, or return null on any terminal condition
    // (cancelled deadline / disposed) — never a fabricated connection.
    private async Task<System.IO.Pipes.NamedPipeServerStream?> TryAcceptAsync(CancellationToken ct)
    {
        System.IO.Pipes.NamedPipeServerStream pipe = CreateServerInstance();
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            return pipe;
        }
        catch (OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (ObjectDisposedException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private void StartServing(
        System.IO.Pipes.NamedPipeServerStream pipe, ChannelWriter<HookOpEvent> writer,
        ConcurrentDictionary<long, Task> inFlight)
    {
        long connectionId = Interlocked.Increment(ref _nextConnectionId);
        _liveConnections[connectionId] = pipe;

        var session = new HookObservationSession(pipe, RunConfirmRound, IsTorndown);
        Volatile.Write(ref _session, session);   // release barrier — see the Unix endpoint note.

        inFlight[connectionId] = ServeConnectionAsync(connectionId, pipe, session, writer, inFlight);
    }

    private static async Task DrainRemainingAsync(ConcurrentDictionary<long, Task> inFlight, TimeSpan grace)
    {
        Task[] pending = inFlight.Values.ToArray();
        if (pending.Length == 0)
            return;

        using var graceCts = new CancellationTokenSource(grace);
        try
        {
            await Task.WhenAll(pending).WaitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Grace elapsed with a connection still serving — see the Unix endpoint's identical note.
        }
    }

    private async Task ServeConnectionAsync(
        long connectionId, System.IO.Pipes.NamedPipeServerStream pipe, HookObservationSession session,
        ChannelWriter<HookOpEvent> writer, ConcurrentDictionary<long, Task> inFlight)
    {
        try
        {
            await foreach (HookOpEvent ev in session.ObserveAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    await writer.WriteAsync(ev, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    // See the Unix endpoint's identical catch: the turn already ended and the writer
                    // completed — the report is intentionally, FAIL-SAFE dropped (MA14 degrades; never
                    // widens). Stop reading this connection.
                    break;
                }
            }
        }
        finally
        {
            inFlight.TryRemove(connectionId, out _);
            _liveConnections.TryRemove(connectionId, out _);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<HookDecisionResponse> RequestDecisionAsync(
        HookDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        HookObservationSession? session = Volatile.Read(ref _session);
        if (session is null || Volatile.Read(ref _disposed) != 0)
            return HookDecisionResponse.Deny(request.ToolUseId);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        return await session.RequestDecisionAsync(request, linked.Token).ConfigureAwait(false);
    }

    private async Task<HookDecisionResponse> RunConfirmRound(HookDecisionRequest request, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        return await _confirm(request, linked.Token).ConfigureAwait(false);
    }

    private bool IsTorndown() => Volatile.Read(ref _disposed) != 0;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // PARITY with the Unix endpoint's deadlock fix (verified there; mirrored here so the Windows leg
        // has the SAME teardown posture, not merely a claim of one). The disposed flag (set above) makes
        // an in-flight decision write short-circuit; firing _lifetime.Cancel() OFF the dispose thread frees
        // the dispose thread from synchronously running the per-call linked-CTS cancellation registrations
        // while an in-flight await concurrently disposes the SAME linked CTS (the WaitForCallbackIfNecessary
        // cross-block). _lifetime is disposed on that pool task AFTER Cancel() returns.
        CancellationTokenSource lifetime = _lifetime;
        _ = Task.Run(() =>
        {
            try { lifetime.Cancel(); }
            catch { /* benign already-disposed race */ }
            finally { lifetime.Dispose(); }
        });

        foreach (long connectionId in _liveConnections.Keys.ToArray())
        {
            if (_liveConnections.TryRemove(connectionId, out System.IO.Pipes.NamedPipeServerStream? pipe))
                await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
#pragma warning disable CA1031 // Best-effort join of the accept loop; a fault there must not fault the observe sequence.
        catch { /* the accept loop's own faults are swallowed at its boundary; nothing more to surface. */ }
#pragma warning restore CA1031
    }
}
