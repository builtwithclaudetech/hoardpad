// ToolPermissionHookHost.cs — the --settings injection + the fail-closed self-test (M5.2).
//
// Architecture §4.3 (hook deployment/injection) + §4.2 step 5 (the self-test gate). This is
// the OS leg that:
//   1. materializes the --settings JSON wiring the bundled hooks/deny_by_default.py as the
//      PreToolUse '*'-matcher hook with a NO/LONG timeout (>= the FR-39 no-reply timer + margin;
//      default >= 600 s) so the hook's OWN timeout can never win the fail-open race (§4.2 step 4);
//   2. probes the Python interpreter + the TD-1 floor (3.12+ on Windows);
//   3. RUNS the FR-24 §3 fail-closed self-test — THE SOLE GUARD against a silently-disarmed
//      sandbox (-p silently ignores an invalid hook config, SPIKE-3 D3). It is NOT a stub: it
//      launches the CALLER-SUPPLIED launcher (see STF-3 note below) with the SAME --settings argv
//      the production path uses (by calling BuildHookSettings and passing the JSON path as
//      --settings), so a --settings-wiring regression WOULD fail the self-test. The test passes
//      ONLY IF (a) no file appears at the out-of-sandbox target AND (b) a denial is recorded.
//      Fail closed on either condition.
//
// STF-3 (docs/stf-diagnosis.md, Option 1): these methods launch WHATEVER LaunchDescriptor the
// caller passes as `launcher` — they always have, and that is unchanged here (existing test
// coverage in HookHostTests.cs / ChannelKillSelfTestTests.cs depends on injecting controllable
// Python stand-ins this way). The defect STF-3 fixes lives at the PRODUCTION call site
// (AiPanelViewModel.ArmAgencyAsync), which used to pass `_backend.ResolveLauncher(...)` — the real
// `claude` CLI — as `launcher`. The real CLI has no knowledge of either descriptor protocol below
// and can never drive the crafted event through the hook, so arming write agency against it always
// failed closed ("Sandbox could not be armed"). ArmAgencyAsync now passes a LaunchDescriptor
// pointing at the bundled HoardPad.SelfTestStandIn executable instead — a tiny stand-in that
// reproduces, 1:1, the behavior already proven correct by the Python reference stand-ins here. The
// PRODUCTION turn launch (AgentBackend.StartTurnAsync) is completely untouched — it still resolves
// and launches the real `claude` CLI exactly as before; only the self-test CALL SITE's launcher
// choice changed. This file's self-test bodies are BYTE-UNMODIFIED by STF-3 except this banner.
//
// WHAT IS VERIFIED DETERMINISTICALLY vs MANUALLY (the gap — make it explicit, not silent):
//   Deterministically verified each startup (this class):
//     - The --settings JSON wires the hook correctly (BuildHookSettings produces a parseable config).
//     - The stand-in agent receives --settings and the injected hook IS the denial gate
//       (a disarmed stand-in that ignores --settings writes the escape file => Passed=false).
//     - A faithful stand-in that reads --settings and invokes the production hook honors the deny
//       (no file written + denial recorded => Passed=true).
//   Confirmed ONLY by the manual SC-5(ii) live-`claude -p` leg (NOT done in this class):
//     - That the real `claude -p` CLI actually honors the --settings PreToolUse hook at runtime
//       (not just that the JSON is well-formed). The CLI silently ignores invalid hook configs
//       (SPIKE-3 D3), so the green self-test proves the wiring is CORRECT but does NOT guarantee
//       the production CLI version that ships with HoardPad will honor it. SC-5(ii) closes that gap.
//
// SELF-TEST TRANSPORT (so it is testable on Linux with a controllable stand-in agent):
//   The host calls BuildHookSettings and writes the settings JSON to a temp file, then passes
//   --settings <path> to the launched agent exactly as production does. The host also writes a
//   descriptor (a JSON file) describing the scratch sandbox root, the out-of-sandbox target the
//   Write must NOT reach, the denial-record path, and the cap-state file. The descriptor path is
//   passed via the HOARDPAD_SELFTEST_DESCRIPTOR env var. A faithful stand-in that reads --settings
//   and invokes the hook (the exact config BuildHookSettings produced) must deny the Write; a
//   disarmed stand-in that ignores --settings writes the escape file => Passed=false.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// Materializes the deny-by-default hook's <c>--settings</c> injection and runs the FR-24 §3
/// fail-closed self-test. Implements <see cref="IToolPermissionHookHost"/>. The self-test
/// ACTUALLY RUNS (no stub), passes <c>--settings</c> to the launched agent exactly as production
/// does, and fails closed when the hook does not deny or a file appears.
/// </summary>
public sealed class ToolPermissionHookHost : IToolPermissionHookHost
{
    // The no/long hook timeout floor (seconds). >= the FR-39 no-reply timer + margin, so the
    // hook's own timeout NEVER wins the fail-open race — HoardPad's process-tree kill is the
    // sole fail-closed enforcer (§4.2 step 4, SPIKE-4 windows settings used 600).
    private const int HookTimeoutFloorSeconds = 600;

    // Python floor (TD-1): below 3.12 on Windows, pathlib.resolve() does not expand 8.3 short
    // names => a sandbox-escape vector. The hook self-guards too, but the host refuses to arm
    // write agency below the floor on Windows.
    private static readonly Version WindowsPythonFloor = new(3, 12);

    // The HOARDPAD_SELFTEST_DESCRIPTOR env var carries the self-test descriptor path to the agent.
    internal const string SelfTestDescriptorEnv = "HOARDPAD_SELFTEST_DESCRIPTOR";

    // M11 (docs/m11-channel-protocol.md §1.1): the per-turn observation channel address + mode the
    // hook reads at run time. Injected via HookSettings.HookEnvironment (the SAME process-env path as
    // HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE). ABSENT ⇒ the hook's autonomous path runs
    // UNCHANGED (and the existing 29 SC-5 gates stay byte-identical).
    internal const string ObserveChannelEnv = "HOARDPAD_OBSERVE_CHANNEL";
    internal const string ObserveModeEnv = "HOARDPAD_OBSERVE_MODE";

    private readonly string _hookScriptPath;
    private readonly int _hookTimeoutSeconds;

    /// <summary>
    /// Construct a hook host bound to the bundled production hook at <paramref name="hookScriptPath"/>
    /// (architecture §7.4 — resolved next to the published app at runtime).
    /// </summary>
    /// <param name="hookScriptPath">Absolute path to the bundled <c>hooks/deny_by_default.py</c>.</param>
    /// <param name="hookTimeoutSeconds">
    /// The PreToolUse hook timeout to wire (seconds). Clamped UP to the <see cref="HookTimeoutFloorSeconds"/>
    /// floor — a shorter value could let the hook timeout fail open and win the race (§4.2 step 4).
    /// </param>
    public ToolPermissionHookHost(string hookScriptPath, int hookTimeoutSeconds = HookTimeoutFloorSeconds)
    {
        if (string.IsNullOrEmpty(hookScriptPath))
            throw new ArgumentException("The bundled hook script path is required.", nameof(hookScriptPath));
        _hookScriptPath = Path.GetFullPath(hookScriptPath);
        // Floor-clamp: never wire a timeout below the floor (a host-config bug must not undercut
        // the no-reply timer and re-open the fail-open hole).
        _hookTimeoutSeconds = Math.Max(hookTimeoutSeconds, HookTimeoutFloorSeconds);
    }

    // ---- BuildHookSettings: the --settings JSON ----

    public HookSettings BuildHookSettings(HookConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string interpreter = ProbePythonInterpreter();
        // The hook command: "<python> <hook-script>". The CLI runs this as the PreToolUse hook;
        // stdin carries the event JSON, stdout/exit carries the decision (the hook owns the protocol).
        string command = $"{QuoteForShellCommand(interpreter)} {QuoteForShellCommand(_hookScriptPath)}";

        // The hook reads HOARDPAD_SANDBOX_ROOT + the cap-state env at run time; those are wired into
        // the launched agent's environment by IAgentBackend, NOT into the --settings JSON. The
        // --settings JSON wires ONLY the hook registration: the '*'-matcher PreToolUse hook with the
        // floored timeout (>= FR-39 no-reply + margin), so the hook's timeout never wins the race.
        var settings = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "*",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command,
                                timeout = _hookTimeoutSeconds,
                            },
                        },
                    },
                },
            },
        };

        string json = JsonSerializer.Serialize(settings, SettingsJsonOptions);
        return new HookSettings(json) { HookEnvironment = BuildObserveEnvironment(config) };
    }

    /// <summary>
    /// Build the ADDITIVE M11 observation env the host sets on the launched agent process (protocol
    /// §1.1). Returns an EMPTY map when <see cref="HookConfig.ObserveChannelAddress"/> is null — so a
    /// turn with no live channel injects NO HOARDPAD_OBSERVE_* var and the hook runs its autonomous
    /// path UNCHANGED (the 29 SC-5 gates stay byte-identical). When a channel IS configured it emits
    /// HOARDPAD_OBSERVE_CHANNEL (the socket path / pipe name) and HOARDPAD_OBSERVE_MODE (report|approve).
    /// </summary>
    private static Dictionary<string, string> BuildObserveEnvironment(HookConfig config)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(config.ObserveChannelAddress))
        {
            env[ObserveChannelEnv] = config.ObserveChannelAddress;
            env[ObserveModeEnv] = config.ObserveMode == HookObserveMode.Approve ? "approve" : "report";
        }

        return env;
    }

    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = false };

    // ---- RunFailClosedSelfTestAsync: the sole guard, ACTUALLY runs + passes --settings ----

    public async Task<SelfTestResult> RunFailClosedSelfTestAsync(LaunchDescriptor launcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ct.ThrowIfCancellationRequested();

        // TD-1 floor: on Windows below Python 3.12, refuse to arm write agency (the short-name
        // sandbox-escape vector). A failed floor probe is itself a fail-closed self-test failure.
        (bool floorOk, string? floorDetail) = CheckPythonFloor();
        if (!floorOk)
            return new SelfTestResult(false, floorDetail);

        // Build a throwaway scratch sandbox + a sibling out-of-sandbox target the Write must
        // NEVER reach, plus a denial-record path the run must populate on a deny.
        string scratchBase = Path.Combine(Path.GetTempPath(), $"hoardpad-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchBase);
        string sandbox = Path.Combine(scratchBase, "sandbox");
        Directory.CreateDirectory(sandbox);
        string escapeTarget = Path.Combine(scratchBase, "ESCAPED.txt");   // sibling of the sandbox.
        string denialRecord = Path.Combine(scratchBase, "denial-record.json");
        string descriptorPath = Path.Combine(scratchBase, "selftest-descriptor.json");
        string capStateFile = Path.Combine(scratchBase, "selftest.capstate");
        string settingsFile = Path.Combine(scratchBase, "selftest-settings.json");

        try
        {
            // Build the --settings JSON EXACTLY as production does and write it to a temp file.
            // The launched agent receives --settings <path> just like the real claude -p does —
            // so a BuildHookSettings regression would wire a malformed config and the stand-in
            // would not see a denying hook => escape file appears => Passed=false.
            HookSettings hookSettings = BuildHookSettings(
                new HookConfig(new PathResolver().ResolveCanonical(sandbox, sandbox), NoReplyTimeout: TimeSpan.FromSeconds(60)));
            await File.WriteAllTextAsync(settingsFile, hookSettings.SettingsJson, ct).ConfigureAwait(false);

            string interpreter = ProbePythonInterpreter();
            // The descriptor the launched agent reads: it tells the stand-in EXACTLY which
            // out-of-sandbox Write to attempt through the production hook (via the --settings wiring),
            // and where to record the denial.
            var descriptor = new
            {
                sandboxRoot = sandbox,
                escapeTarget,
                denialRecordPath = denialRecord,
                capStateFile,
                hookCommand = new { interpreter, script = _hookScriptPath },
                toolName = "Write",
                content = "fail-closed self-test escape attempt — MUST be denied",
            };
            await File.WriteAllTextAsync(descriptorPath, JsonSerializer.Serialize(descriptor, SettingsJsonOptions), ct)
                .ConfigureAwait(false);

            // Launch the agent with --settings <path> exactly as production does. This means
            // a --settings-wiring regression (e.g. malformed JSON, wrong hook path) causes the
            // stand-in to see no hook config and the escape check reveals a disarmed state.
            // WorkingDirectory = sandbox (same WorkingDirectory=root invariant the real launch uses).
            var psi = new ProcessStartInfo
            {
                FileName = launcher.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,   // drained below (MUST-FIX 3: prevent pipe deadlock).
                WorkingDirectory = sandbox,
            };
            psi.ArgumentList.Add("--settings");
            psi.ArgumentList.Add(settingsFile);
            psi.ArgumentList.Add(descriptorPath);
            psi.Environment[SelfTestDescriptorEnv] = descriptorPath;
            // The hook reads these at run time; the self-test delivers them through the SAME production
            // env-application code the real launch uses (AgentBackend.ApplyEnv → LaunchLinux's
            // psi.Environment writes). Routing here — not a direct psi.Environment write — ensures
            // the production seam is exercised.
            //
            // NOTE: this self-test proves fail-closed-on-deny (the hook blocks an out-of-sandbox write
            // regardless of whether HOARDPAD_SANDBOX_ROOT is set — in_sandbox("", target) also denies).
            // The NON-VACUITY of the production env-DELIVERY (a dropped key or renamed var is caught) is
            // proven by the echo-launcher test (AgentBackendTests.StartTurnAppliesTheLaunchEnvOntoThe
            // SpawnedChildEnvironment + BinderAssembledEnvReachesTheSpawnedChildViaTheRealLaunchPath)
            // and the binder integration test (SessionMa5AndIntegrationTests.BoundTurnDelivers…),
            // not by this self-test.
            AgentBackend.ApplyEnv(psi, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOARDPAD_SANDBOX_ROOT"] = sandbox,
                ["HOARDPAD_CAP_STATE_FILE"] = capStateFile,
            });

            using Process? process = Process.Start(psi);
            if (process is null)
                return new SelfTestResult(false,
                    $"self-test could not launch the agent at '{launcher.ExecutablePath}'.");

            // MUST-FIX 3: drain stdout AND stderr concurrently before WaitForExitAsync.
            // A child that writes >~64 KB to either pipe blocks on write and the self-test hangs
            // forever (until ct). Draining both concurrently prevents the deadlock. Keep stderr
            // output in the failure Detail so the caller can diagnose.
            Task<string> outTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            string stderrOutput = errTask.Result;

            // (a) NO file may appear at the out-of-sandbox target. If one did, the hook did NOT
            //     prevent the Write — the sandbox is disarmed (or --settings wiring was broken).
            bool escapeFileAppeared = File.Exists(escapeTarget);
            // (b) A denial must be recorded (the hook actively denied, not just "agent did nothing").
            bool denialRecorded = File.Exists(denialRecord);

            if (escapeFileAppeared)
                return new SelfTestResult(false,
                    $"FAIL-CLOSED: a file appeared at the out-of-sandbox target '{escapeTarget}' — the production "
                    + "hook did NOT prevent the Write (or the --settings wiring was silently disarmed). "
                    + "The sandbox is not armed; write agency MUST NOT be enabled."
                    + (string.IsNullOrEmpty(stderrOutput) ? "" : $"\nstderr: {stderrOutput}"));

            if (!denialRecorded)
                return new SelfTestResult(false,
                    "FAIL-CLOSED: no denial was recorded for the out-of-sandbox Write. The hook did not actively "
                    + "deny (it may have been silently ignored, or the --settings wiring is broken — SPIKE-3 D3); "
                    + "write agency MUST NOT be enabled."
                    + (string.IsNullOrEmpty(stderrOutput) ? "" : $"\nstderr: {stderrOutput}"));

            return new SelfTestResult(true,
                "fail-closed self-test passed: the out-of-sandbox Write was denied and no file appeared at the target. "
                + "NOTE: this proves the --settings wiring is correct and the hook actively denies. "
                + "It does NOT prove the production `claude -p` CLI honors the hook at runtime — that is "
                + "confirmed by the manual SC-5(ii) live-`claude -p` leg, which must be run separately.");
        }
        finally
        {
            TryDeleteDirectory(scratchBase);
        }
    }

    // ---- M11 extension (spec §5 invariant 5): the killed-channel deny self-test ----

    /// <summary>
    /// The M11 extension to the self-test gate (spec §5 invariant 5, protocol §4): before write
    /// agency is armed with a LIVE observation channel, prove that a turn whose approve-each channel
    /// is KILLED mid-ask DENIES and leaves NO file. This is the sole guard that a channel fault cannot
    /// silently arm a widened sandbox — it must pass IN ADDITION to
    /// <see cref="RunFailClosedSelfTestAsync"/> before the live approve-each path is enabled.
    /// </summary>
    /// <remarks>
    /// The host opens a real <see cref="HookObservationChannel"/> endpoint in
    /// <see cref="HookObserveMode.Approve"/> mode and launches a stand-in modeling the hook's ask leg:
    /// it connects, sends an <c>ask</c>, and BLOCKS reading the <c>decision</c>. The host then DISPOSES
    /// the endpoint mid-ask (the killed-channel event). The stand-in reads a closed channel ⇒ per
    /// protocol §4 it DENIES: it writes NOTHING at the target and records the denial. The self-test
    /// passes ONLY IF (a) no file appeared AND (b) the denial was recorded — fail closed otherwise.
    /// </remarks>
    // Instance method by design (API symmetry with the paired RunFailClosedSelfTestAsync gate),
    // even though it drives a freshly-constructed channel rather than host state.
#pragma warning disable CA1822
    public async Task<SelfTestResult> RunChannelKillSelfTestAsync(LaunchDescriptor launcher, CancellationToken ct)
#pragma warning restore CA1822
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ct.ThrowIfCancellationRequested();

        string scratchBase = Path.Combine(Path.GetTempPath(), $"hoardpad-chan-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchBase);
        string sandbox = Path.Combine(scratchBase, "sandbox");
        Directory.CreateDirectory(sandbox);
        string writeTarget = Path.Combine(sandbox, "approved.txt");   // IN-sandbox: the hook would allow autonomously.
        string denialRecord = Path.Combine(scratchBase, "denial-record.json");
        string descriptorPath = Path.Combine(scratchBase, "chan-selftest-descriptor.json");

        // The confirm BLOCKS until disposal cancels it — modeling an in-flight approve-each round at
        // the instant the channel is killed. When we dispose the endpoint mid-ask, this confirm is
        // cancelled (=> the channel's fail-closed RequestDecisionAsync returns Deny and, being torn
        // down, writes NO decision), so the hook reads a CLOSED CHANNEL and denies (protocol §4). A
        // confirm that returned an answer synchronously would instead test the answer path, not the
        // killed-channel path — so blocking is what makes this the killed-channel deny test.
        var channel = new HookObservationChannel(
            confirm: async (req, confirmCt) =>
            {
                await Task.Delay(Timeout.Infinite, confirmCt).ConfigureAwait(false);
                return HookDecisionResponse.Allowed(req.ToolUseId);   // UNREACHABLE (cancelled by the kill).
            },
            runtimeDir: Path.Combine(scratchBase, "runtime"));

        try
        {
            NormcasedPath root = new PathResolver().ResolveCanonical(sandbox, sandbox);
            const string conversationKey = "chan-selftest";
            string channelAddress = channel.ResolveChannelAddress(root, conversationKey);

            var descriptor = new
            {
                channelAddress,
                writeTarget,
                denialRecordPath = denialRecord,
                toolUseId = "toolu_chankill",
                content = "channel-kill self-test — MUST be denied on a closed channel",
            };
            await File.WriteAllTextAsync(descriptorPath, JsonSerializer.Serialize(descriptor, SettingsJsonOptions), ct)
                .ConfigureAwait(false);

            await using IHookObservationEndpoint endpoint =
                await channel.OpenForTurnAsync(root, conversationKey, ct).ConfigureAwait(false);

            // Drive the endpoint's observe loop (so it accepts the stand-in's connection and the
            // ask round is processed); we don't consume reports here — the loop's side effect (accept
            // + ask handling) is what we exercise.
            using var observeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task observe = DrainObserveAsync(endpoint, observeCts.Token);

            var psi = new ProcessStartInfo
            {
                FileName = launcher.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = sandbox,
            };
            psi.ArgumentList.Add(descriptorPath);
            // Deliver the M11 channel env through the SAME production env-application code the real
            // launch uses (AgentBackend.ApplyEnv → LaunchLinux), so this killed-channel gate exercises —
            // and a mutation to — the production env-delivery seam (spec §5 invariant 5). Dropping
            // HOARDPAD_OBSERVE_CHANNEL here means the stand-in hook never receives the channel address.
            AgentBackend.ApplyEnv(psi, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ObserveChannelEnv] = channelAddress,
                [ObserveModeEnv] = "approve",
                ["HOARDPAD_SANDBOX_ROOT"] = sandbox,
            });

            using Process? process = Process.Start(psi);
            if (process is null)
                return new SelfTestResult(false,
                    $"channel-kill self-test could not launch the stand-in at '{launcher.ExecutablePath}'.");

            Task<string> outTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errTask = process.StandardError.ReadToEndAsync(ct);

            // Give the stand-in a moment to connect + send its ask and block, THEN KILL the channel
            // (dispose the endpoint). The stand-in then reads a closed channel ⇒ deny (protocol §4).
            await WaitForMarkerAsync(connectedMarker: descriptorPath + ".connected", ct).ConfigureAwait(false);
            await endpoint.DisposeAsync().ConfigureAwait(false);   // THE KILL.
            await observeCts.CancelAsync().ConfigureAwait(false);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            try { await observe.ConfigureAwait(false); } catch { /* observe ends on cancel/dispose */ }
            string stderrOutput = errTask.Result;

            bool escapeFileAppeared = File.Exists(writeTarget);
            bool denialRecorded = File.Exists(denialRecord);

            if (escapeFileAppeared)
                return new SelfTestResult(false,
                    $"FAIL-CLOSED: a file appeared at '{writeTarget}' after the observation channel was killed "
                    + "mid-ask — the approve-each path did NOT deny on a dead channel (protocol §4). Live write "
                    + "agency MUST NOT be armed."
                    + (string.IsNullOrEmpty(stderrOutput) ? "" : $"\nstderr: {stderrOutput}"));

            if (!denialRecorded)
                return new SelfTestResult(false,
                    "FAIL-CLOSED: no denial was recorded after the channel was killed mid-ask. The killed-channel "
                    + "deny could not be confirmed; live write agency MUST NOT be armed."
                    + (string.IsNullOrEmpty(stderrOutput) ? "" : $"\nstderr: {stderrOutput}"));

            return new SelfTestResult(true,
                "channel-kill self-test passed: with the observation channel killed mid-ask, the approve-each op "
                + "was denied and no file appeared. This proves the live approve-each path fails closed on a dead "
                + "channel (spec §5 invariant 5, protocol §4).");
        }
        finally
        {
            TryDeleteDirectory(scratchBase);
        }
    }

    private static async Task DrainObserveAsync(IHookObservationEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            await foreach (HookOpEvent _ in endpoint.ObserveAsync(ct).ConfigureAwait(false))
            {
                // The channel-kill self-test does not consume reports; the observe loop's accept +
                // inline ask handling is the exercised behavior.
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on the kill: the observe loop ends when the endpoint is disposed.
        }
    }

    private static async Task WaitForMarkerAsync(string connectedMarker, CancellationToken ct)
    {
        // Poll for the stand-in's "connected + ask sent" marker so the kill happens AFTER the ask is
        // in flight (not before the hook even connects). Bounded so the test never hangs.
        long deadline = Environment.TickCount64 + 5_000;
        while (!File.Exists(connectedMarker))
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
                return;   // proceed to kill anyway — a never-connected stand-in is itself a fail-closed outcome.
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    // ---- Python interpreter probe + TD-1 floor ----

    /// <summary>
    /// Pick the Python interpreter: prefer <c>python</c>, then the Windows <c>py</c> launcher.
    /// On Linux <c>python3</c> is also tried. Returns the first that resolves on PATH; falls back
    /// to <c>python</c> (the hook self-guards the floor regardless).
    /// </summary>
    private static string ProbePythonInterpreter()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "python", "py", "python3" }
            : new[] { "python3", "python" };

        foreach (string candidate in candidates)
        {
            if (IsOnPath(candidate))
                return candidate;
        }

        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    private static bool IsOnPath(string command)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
            return false;

        char separator = OperatingSystem.IsWindows() ? ';' : ':';
        string[] exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (string dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts)
            {
                if (File.Exists(Path.Combine(dir.Trim(), command + ext)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enforce the TD-1 Python floor (3.12+ on Windows). On Linux the floor is not required
    /// (no 8.3 short names). Returns (true, null) when the floor is satisfied (or not applicable).
    /// </summary>
    private static (bool Ok, string? Detail) CheckPythonFloor()
    {
        if (!OperatingSystem.IsWindows())
            return (true, null);   // Linux: no short-name escape vector; floor not applicable.

        return CheckWindowsPythonFloor();
    }

    [SupportedOSPlatform("windows")]
    private static (bool Ok, string? Detail) CheckWindowsPythonFloor()
    {
        Version? probed = ProbePythonVersion();
        if (probed is null)
            return (false,
                "fail-closed: could not probe the Python version on Windows; the TD-1 3.12+ floor "
                + "(8.3 short-name sandbox-escape) cannot be confirmed, so write agency is not armed.");
        if (probed < WindowsPythonFloor)
            return (false,
                $"fail-closed: Python {probed} is below the {WindowsPythonFloor} Windows floor (TD-1 short-name "
                + "sandbox-escape vector); write agency is not armed.");
        return (true, null);
    }

    [SupportedOSPlatform("windows")]
    private static Version? ProbePythonVersion()
    {
        try
        {
            string interpreter = ProbePythonInterpreter();
            using Process? p = Process.Start(new ProcessStartInfo
            {
                FileName = interpreter,
                Arguments = "-c \"import sys;print(f'{sys.version_info.major}.{sys.version_info.minor}')\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return Version.TryParse(output, out Version? v) ? v : null;
        }
        catch
        {
            return null;   // probe failure => caller fails closed.
        }
    }

    // ---- helpers ----

    private static string QuoteForShellCommand(string token)
    {
        // The CLI runs the hook command via the platform shell; quote if the path contains spaces.
        if (token.Length > 0 && !token.Contains(' ', StringComparison.Ordinal))
            return token;
        return "\"" + token.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort scratch cleanup */ }
    }
}
