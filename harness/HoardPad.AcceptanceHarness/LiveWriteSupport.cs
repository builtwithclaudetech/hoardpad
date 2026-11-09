// LiveWriteSupport.cs — the M11 live-path driver for the extended SC-5 gates (Step 4, ADDITIVE).
//
// This file owns the one reusable piece every G-live gate needs that the M9.3 HarnessSupport did
// not: LiveHookDriver, which drives the REAL production hook (hooks/deny_by_default.py) as a
// `python3` subprocess WITH the M11 observation/ask leg engaged — i.e. with HOARDPAD_OBSERVE_CHANNEL
// + HOARDPAD_OBSERVE_MODE set, connected to a REAL HookObservationChannel endpoint the harness opens
// and answers. The harness plays the App's allow/deny role over the channel (the `confirm` delegate),
// observes the per-op reports the hook sends (the chip / cap-read / MA14 feed), and parses the hook's
// final allow/deny verdict back from stdout — EXACTLY the production wiring, no fakes:
//
//   - the channel is the production HoardPad.Host.HookObservationChannel over a real AF_UNIX socket;
//   - the hook is the shipped hooks/deny_by_default.py (HookDriver.ProductionHookPath());
//   - the cap-state, sandbox gate, atomic store + AiFileOpsService are the M9.3 RealStack (no fakes).
//
// linux-x64 only (C-6 / spec §6): the AF_UNIX socket + python3 hook are the Linux production legs;
// the win-x64 named-pipe IPC leg is compile-confirmed-only / runtime Deferred.

using System.Diagnostics;
using System.Text.Json;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;

namespace HoardPad.AcceptanceHarness;

/// <summary>
/// The outcome of driving the production hook over ONE op WITH the live observation channel engaged:
/// the hook's final allow/deny verdict (post AND-narrowing) AND the per-op reports the harness
/// observed over the channel (one <see cref="HookOpEvent"/> per gated op — the chip / cap-read / MA14
/// feed source).
/// </summary>
/// <param name="Decision">The hook's FINAL verdict parsed from stdout (after the approve-each AND-combine).</param>
/// <param name="Reason">The hook's <c>permissionDecisionReason</c> (carries the fail-closed cause on a deny).</param>
/// <param name="Observed">The reports the harness observed over the channel for this op (usually one).</param>
internal readonly record struct LiveHookOutcome(
    HookDecision Decision, string Reason, IReadOnlyList<HookOpEvent> Observed);

/// <summary>
/// Drives the REAL production deny-by-default hook (<c>hooks/deny_by_default.py</c>) as a
/// <c>python3</c> subprocess WITH the M11 observation/ask leg engaged: a real
/// <see cref="HookObservationChannel"/> endpoint is opened, the channel address + mode are injected
/// into the hook env (exactly as <c>BuildHookSettings</c> does in production), the hook connects,
/// REPORTs each gated op (and, in <c>approve</c> mode, ASKs for a hook-allow), and the harness — playing
/// the App — answers over the channel. The harness both OBSERVES the per-op reports and PARSES the
/// hook's final stdout verdict. This is the SC-5 live-path authority: the SHIPPED python artifact +
/// the SHIPPED C# channel are the things being gated, not a reimplementation of either.
/// </summary>
internal sealed class LiveHookDriver : IAsyncDisposable
{
    private readonly RealStack _stack;
    private readonly string _runtimeDir;
    private readonly HookObserveMode _mode;
    private readonly Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> _confirm;
    private readonly HookObservationChannel _channel;
    private readonly TimeSpan _confirmDeadline;

    /// <summary>The reports the harness observed across every op driven through this driver (the chip / MA14 feed).</summary>
    internal List<HookOpEvent> AllObserved { get; } = new();

    private LiveHookDriver(
        RealStack stack,
        string runtimeDir,
        HookObserveMode mode,
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm,
        TimeSpan confirmDeadline)
    {
        _stack = stack;
        _runtimeDir = runtimeDir;
        _mode = mode;
        _confirm = confirm;
        _confirmDeadline = confirmDeadline;
        // The PRODUCTION channel — composes the harness's App-role confirm over a real AF_UNIX socket.
        _channel = new HookObservationChannel(_confirm, _runtimeDir);
    }

    /// <summary>
    /// Build a live driver bound to a <see cref="RealStack"/> sandbox, a given observe
    /// <paramref name="mode"/> (<c>approve</c> = REPORT+ask; <c>report</c> = REPORT-only), and the
    /// harness's App-role <paramref name="confirm"/> answer (the narrowing allow/deny the channel
    /// transports to the blocked hook). <paramref name="confirmDeadline"/> bounds the App answer (the
    /// no-reply timer); when it trips the channel fails closed to a deny.
    /// </summary>
    internal static LiveHookDriver Create(
        RealStack stack,
        HookObserveMode mode,
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm,
        TimeSpan? confirmDeadline = null)
    {
        string runtimeDir = Path.Combine(stack.Base, "observe-runtime");
        Directory.CreateDirectory(runtimeDir);
        return new LiveHookDriver(stack, runtimeDir, mode, confirm, confirmDeadline ?? TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// The conversation cap-state key the channel + the hook env are bound to (the per-turn channel is
    /// keyed by working root + conversation key, protocol §1.1). Stable for the driver's life.
    /// </summary>
    internal string ConversationKey { get; } = "conv-live-" + Guid.NewGuid().ToString("N")[..8];

    // The same resolved root the RealStack gate uses (re-resolved deterministically over the stack's
    // SandboxRoot with the production PathResolver — RealStack keeps its resolved root private, and we
    // must not modify HarnessSupport.cs, so we reproduce the identical canonicalization here).
    private NormcasedPath WorkingRoot => _stack.Resolver.ResolveCanonical(_stack.SandboxRoot, _stack.SandboxRoot);

    /// <summary>
    /// Drive the production hook over ONE crafted PreToolUse op WITH the channel live. Opens a per-turn
    /// endpoint, starts observing, injects HOARDPAD_OBSERVE_CHANNEL + HOARDPAD_OBSERVE_MODE (+ the M9.3
    /// sandbox/cap env) into the hook subprocess, runs it to completion, and returns the hook's FINAL
    /// verdict plus the reports observed. <paramref name="capStateFileOverride"/> points the hook at a
    /// specific cap-state payload (e.g. a tripped one) when a gate needs it; otherwise the env default.
    /// <paramref name="killChannelBeforeRun"/> disposes the endpoint BEFORE the hook connects, modeling
    /// the channel-unreachable fail-closed case (G-live-1).
    /// </summary>
    internal async Task<LiveHookOutcome> RunOpAsync(
        string toolName,
        IReadOnlyDictionary<string, string> toolInput,
        string toolUseId,
        string? capStateFileOverride,
        bool killChannelBeforeRun,
        CancellationToken ct)
    {
        string address = _channel.ResolveChannelAddress(WorkingRoot, ConversationKey);
        IHookObservationEndpoint endpoint =
            await _channel.OpenForTurnAsync(WorkingRoot, ConversationKey, ct).ConfigureAwait(false);

        var observedHere = new List<HookOpEvent>();

        if (killChannelBeforeRun)
        {
            // G-live-1: tear the endpoint down BEFORE the hook runs (and before any observe loop is
            // started) so the per-turn socket node is unlinked. The hook's connect then hits a dead
            // address and must fail closed to a deny (protocol §4 unreachable). No observe loop is
            // started here — there is nothing to observe on a dead channel, and racing dispose against
            // an in-flight AcceptAsync is exactly the flakiness we avoid by disposing first.
            await endpoint.DisposeAsync().ConfigureAwait(false);

            Dictionary<string, string?> killEnv = _stack.HookEnv();
            killEnv[ObserveChannelEnvName] = address;
            killEnv[ObserveModeEnvName] = _mode == HookObserveMode.Approve ? "approve" : "report";
            if (capStateFileOverride is not null)
                killEnv["HOARDPAD_CAP_STATE_FILE"] = capStateFileOverride;

            string killEventJson = BuildEventWithToolUseId(toolName, toolInput, toolUseId);
            HookOutcome killOutcome = await RunHookAsync(killEventJson, killEnv, ct).ConfigureAwait(false);
            return new LiveHookOutcome(killOutcome.Decision, killOutcome.Reason, observedHere);
        }

        // GUARANTEE endpoint disposal on EVERY path: `await using` tears down the AF_UNIX socket +
        // listener even if RunHookAsync throws or `ct` cancels (otherwise a thrown turn leaks the socket
        // node + listener — the BLOCK-2 leak). The explicit DisposeAsync near the end stays so the observe
        // loop drains the hook's pre-exit reports while the hook is known-exited; this `await using` only
        // backstops the fault/cancel paths and is a harmless no-op second dispose on the happy path
        // (DisposeAsync is idempotent via the _disposed Interlocked guard).
        await using (endpoint.ConfigureAwait(false))
        {
            using var observeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task observe = Task.Run(async () =>
            {
                // The observe loop is a best-effort REPORT/chip drain — NEVER on the safety path (protocol
                // §2: report delivery is never on the safety path). Disposal of the endpoint while a read is
                // in flight can surface a socket/disposed fault; swallow EVERYTHING here so a teardown race
                // can never fault the test. The safety verdict is the hook's STDOUT, asserted separately.
                try
                {
                    await foreach (HookOpEvent ev in endpoint.ObserveAsync(observeCts.Token).ConfigureAwait(false))
                    {
                        lock (observedHere)
                        {
                            observedHere.Add(ev);
                            AllObserved.Add(ev);
                        }
                    }
                }
#pragma warning disable CA1031 // The observe drain is off the safety path; any teardown-race fault is benign.
                catch
                {
                    // Cancellation / socket-disposed / EOF race on teardown — the reports drained so far stand.
                }
#pragma warning restore CA1031
            }, observeCts.Token);

            // Cancel + await the observe task on EVERY exit path (including a RunHookAsync throw / ct
            // cancel), so the background task never outlives this scope and the `await using` dispose
            // never races a still-running read it is tearing down.
            try
            {
                Dictionary<string, string?> env = _stack.HookEnv();
                env[ObserveChannelEnvName] = address;
                env[ObserveModeEnvName] = _mode == HookObserveMode.Approve ? "approve" : "report";
                if (capStateFileOverride is not null)
                    env["HOARDPAD_CAP_STATE_FILE"] = capStateFileOverride;

                string eventJson = BuildEventWithToolUseId(toolName, toolInput, toolUseId);
                HookOutcome hookOutcome = await RunHookAsync(eventJson, env, ct).ConfigureAwait(false);

                // The hook has exited and sent its per-op REPORT before exit. Mirror the PRODUCTION
                // AiTurnRunner.CompleteWhenBothDrainAsync, which completes ONLY after BOTH producers drain:
                // before we dispose the endpoint (which cancels the observe read), AWAIT the expected report's
                // arrival in `observedHere`, bounded by the EXISTING confirm/turn deadline (no new/extended
                // deadline). Under CPU starvation the background observe Task.Run may not be scheduled before
                // teardown; this deterministic drain removes that empty-collection race instead of leaving the
                // drain best-effort AFTER teardown. The drain is off the safety path (the verdict is the hook's
                // STDOUT, already captured) — a timeout here changes only the observed feed, never the verdict.
                await DrainObserveReportAsync(observedHere, ct).ConfigureAwait(false);

                // The report has drained (or the bounded deadline elapsed); tear the endpoint down. The observe
                // task swallows its own teardown fault (above).
                await endpoint.DisposeAsync().ConfigureAwait(false);
                return new LiveHookOutcome(hookOutcome.Decision, hookOutcome.Reason, observedHere);
            }
            finally
            {
                // Stop the observe loop and join it on every path (success, throw, or cancel) before the
                // `await using` disposes the endpoint — no orphaned background read survives this scope.
                await observeCts.CancelAsync().ConfigureAwait(false);
#pragma warning disable CA1031 // The observe task is off the safety path; a teardown-race fault here is benign.
                try { await observe.ConfigureAwait(false); }
                catch { /* observe loop already swallows; this guards the Task.Run wrapper itself. */ }
#pragma warning restore CA1031
            }
        }
    }

    /// <summary>
    /// Deterministically wait for the hook's per-op REPORT to land in <paramref name="observed"/> before the
    /// caller disposes the endpoint, bounded by the EXISTING confirm/turn deadline (<see cref="_confirmDeadline"/>)
    /// — no new or extended deadline is introduced. Every non-kill op reports exactly one observed event (the
    /// G-live gates assert <c>Assert.Single</c>), so one drained event satisfies the gate; we wait for the first.
    /// The list is mutated by the observe loop under a lock on itself, so each poll reads the count under that
    /// same lock. The drain is off the safety path: if the bounded deadline elapses we return regardless (the
    /// verdict is already the hook's STDOUT), so a starved feed degrades to the old best-effort behavior rather
    /// than hanging — it never blocks teardown past the deadline the harness already tolerates.
    /// </summary>
    private async Task DrainObserveReportAsync(List<HookOpEvent> observed, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + _confirmDeadline;
        while (DateTime.UtcNow < deadline)
        {
            lock (observed)
            {
                if (observed.Count >= 1)
                    return;
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // turn cancelled — drain ends with the turn (caller handles disposal in finally).
            }
        }
    }

    // ---- the PreToolUse event with an explicit top-level tool_use_id (G-live-6 correlation) ----

    /// <summary>
    /// Build the crafted PreToolUse event carrying an explicit top-level <c>tool_use_id</c> (protocol §2
    /// / the hook's <c>_tool_use_id</c> reader). G-live-6 drives this so the observed report carries a
    /// REAL id the harness controls, making the MA14 correlation genuinely non-vacuous (see the §6
    /// real-world limitation note: the live PreToolUse stdin is not guaranteed to surface a top-level
    /// tool_use_id, so the harness supplies one explicitly).
    /// </summary>
    internal static string BuildEventWithToolUseId(
        string toolName, IReadOnlyDictionary<string, string> toolInput, string toolUseId)
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in toolInput)
            input[kv.Key] = kv.Value;
        var ev = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool_name"] = toolName,
            ["tool_input"] = input,
        };
        if (toolUseId.Length > 0)
            ev["tool_use_id"] = toolUseId;   // the explicit MA14 key (G-live-6).
        return JsonSerializer.Serialize(ev);
    }

    // The env var names the host injects (mirrors ToolPermissionHookHost's constants / protocol §1.1).
    private const string ObserveChannelEnvName = "HOARDPAD_OBSERVE_CHANNEL";
    private const string ObserveModeEnvName = "HOARDPAD_OBSERVE_MODE";

    /// <summary>
    /// Run the production hook subprocess over <paramref name="eventJson"/>. A copy of HarnessSupport's
    /// HookDriver loop, but here the hook BLOCKS on the live channel (the approve-each ask), so the
    /// stdin is closed promptly and stdout/stderr are drained concurrently to avoid a pipe deadlock.
    /// </summary>
    private static async Task<HookOutcome> RunHookAsync(
        string eventJson, IReadOnlyDictionary<string, string?> env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(HookDriver.ProductionHookPath());
        foreach (var kv in env)
        {
            if (kv.Value is null)
                psi.Environment.Remove(kv.Key);
            else
                psi.Environment[kv.Key] = kv.Value;
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.StandardInput.WriteAsync(eventJson.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        return HookDriver.Parse(stdout, proc.ExitCode);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_runtimeDir, recursive: true); }
        catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}
