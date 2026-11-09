// AgentBackendTests.cs — the claude -p launch legs (M5.2, FR-24 / NFR-8).
//
// Covers (on linux-x64):
//   - the FR-24 shim resolver order (claude.cmd -> claude.exe -> configured path; on Linux
//     claude -> configured path), driven by a temp PATH with fake launchers;
//   - the NON-NEGOTIABLE launch invariants, each its own test:
//       * WorkingDirectory = request.WorkingRoot (a launcher that prints its CWD);
//       * NO --cwd flag is ever added (the launched argv equals request.LaunchArgs);
//       * request.LaunchArgs are launched UNCHANGED;
//       * LaunchArgsSafety is RE-ASSERTED at the spawn boundary — a request whose LaunchArgs
//         were mutated past the ctor (via `with`) to carry a disarm token REFUSES to spawn;
//       * bypassPermissions / --dangerously-skip-permissions can never be constructed NOR spawned.
//
// The Windows Job-Object / CREATE_SUSPENDED / taskkill legs COMPILE for win-x64 but are
// runtime-Deferred (SPIKE-4 Decision-2); they are NOT run or asserted on this Linux box.

using System.Collections.Immutable;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class AgentBackendTests : IDisposable
{
    private readonly string _dir;
    private readonly AgentBackend _backend = new();

    public AgentBackendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hoardpad-m52-backend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ---- FR-24 shim resolver order ----

    [Fact]
    public void ResolveLauncherPrefersConfiguredPathOverPosixProbeWhenPathIsEmpty()
    {
        // With a configured path that exists and no `claude` on the (cleared) PATH, the
        // resolver returns the configured path. (We clear PATH for this process scope.)
        string configured = Path.Combine(_dir, "my-claude");
        File.WriteAllText(configured, "#!/bin/sh\n");

        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");   // no `claude` discoverable on PATH.
            LaunchDescriptor d = _backend.ResolveLauncher(configured);
            Assert.Equal(configured, d.ExecutablePath);
            Assert.False(d.FromPath);
            Assert.Equal("configured-path", d.ResolvedVia);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public void ResolveLauncherFindsClaudeOnPathBeforeConfiguredPath()
    {
        // A `claude` binary on PATH takes precedence over the configured override (PATH first).
        string pathDir = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(pathDir);
        string onPath = Path.Combine(pathDir, "claude");
        File.WriteAllText(onPath, "#!/bin/sh\n");
        string configured = Path.Combine(_dir, "configured-claude");
        File.WriteAllText(configured, "#!/bin/sh\n");

        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", pathDir);
            LaunchDescriptor d = _backend.ResolveLauncher(configured);
            Assert.Equal(onPath, d.ExecutablePath);
            Assert.True(d.FromPath);
            Assert.Equal("PATH:claude", d.ResolvedVia);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public void ResolveLauncherThrowsWhenNothingResolves()
    {
        // BUG-4: the fallback (~/.local/bin/claude) is now checked before throwing.
        // If it exists on this machine, the resolver will succeed via the fallback rather than throw —
        // which is the CORRECT behavior. We skip the throw assertion in that case and instead assert
        // the fallback was used.
        string localBinClaude = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "claude");
        bool fallbackExists = File.Exists(localBinClaude);

        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");

            if (fallbackExists)
            {
                // The fallback exists — resolver must NOT throw; it must return the fallback path.
                LaunchDescriptor d = _backend.ResolveLauncher(configuredPath: null);
                Assert.Equal(localBinClaude, d.ExecutablePath);
                Assert.Equal("~/.local/bin/claude", d.ResolvedVia);
            }
            else
            {
                // No fallback on this machine — resolver must throw for both missing inputs.
                Assert.Throws<FileNotFoundException>(() => _backend.ResolveLauncher(configuredPath: null));
                Assert.Throws<FileNotFoundException>(() => _backend.ResolveLauncher(Path.Combine(_dir, "does-not-exist")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public void ResolveLauncherFallsBackToLocalBinClaudeWhenNotOnPath()
    {
        // BUG-4: when `claude` is absent from PATH (e.g. desktop launch without shell-sourced profile),
        // ResolvePosix must probe ~/.local/bin/claude as a fallback. This test verifies that path when the
        // binary is present on the current machine; it is skipped when it is not (the non-existent case is
        // covered by ResolveLauncherThrowsWhenNothingResolves).
        string localBinClaude = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "claude");

        if (!File.Exists(localBinClaude))
        {
            // Nothing to assert — the throw path is covered in ResolveLauncherThrowsWhenNothingResolves.
            return;
        }

        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // Clear PATH so the primary FindOnPath probe finds nothing.
            Environment.SetEnvironmentVariable("PATH", "");

            LaunchDescriptor d = _backend.ResolveLauncher(configuredPath: null);

            Assert.Equal(localBinClaude, d.ExecutablePath);
            Assert.False(d.IsCmd);
            Assert.False(d.FromPath); // resolved via hardcoded fallback, not PATH scan.
            Assert.Equal("~/.local/bin/claude", d.ResolvedVia);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    // ---- launch invariant: WorkingDirectory = root, NO --cwd, args UNCHANGED ----

    [Fact]
    public async Task StartTurnSetsWorkingDirectoryToRootAndPassesArgsUnchangedWithNoCwd()
    {
        // A launcher that prints its CWD and each argv token, so we can prove:
        //   (1) the child's WorkingDirectory == request.WorkingRoot (NOT HoardPad's cwd);
        //   (2) the child's argv == request.LaunchArgs EXACTLY (no --cwd injected, nothing dropped).
        string launcher = WriteEchoLauncher("echo-cwd-args.sh", EchoCwdAndArgsScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");

        string root = Path.Combine(_dir, "open-folder-root");
        Directory.CreateDirectory(root);
        NormcasedPath workingRoot = ResolveRoot(root);

        // A realistic FR-24 §4.1 arg list (the orchestration's flags). NO --cwd present.
        var args = ImmutableArray.Create(
            "-p", "--output-format", "stream-json", "--verbose", "--tools", "Write,Read,Edit", "--strict-mcp-config");
        var request = new AgentTurnRequest("do the thing", workingRoot, args);

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // The launcher echoes 1 CWD line + 7 ARG lines = 8 lines, then sleeps (liveness tail to survive
        // the setsid leadership poll). Read exactly those 8 lines via a bounded readiness wait, then break
        // early — the sleep is never waited on; DisposeAsync reaps the group at test end.
        List<string> lines = await ReadStreamLinesAsync(turn, count: 8, EchoReadinessTimeout);

        Assert.NotEmpty(lines);
        // (1) WorkingDirectory == the resolved root (compare canonical, /tmp may be a symlink).
        Assert.Equal(new DirectoryInfo(workingRoot.Value).FullName, new DirectoryInfo(lines[0][4..]).FullName);
        // (2) argv == request.LaunchArgs EXACTLY (no --cwd injected; nothing added/dropped).
        var observedArgs = lines.Skip(1).Select(l => l["ARG:".Length..]).ToList();
        Assert.Equal(args, observedArgs);
        Assert.DoesNotContain("--cwd", observedArgs);
    }

    // ---- BUG-5: --model / --effort reach the REAL spawned process argv ----

    [Fact]
    public async Task StartTurnSpawnsComposedModelAndEffortFlagsOnTheRealProcessArgv()
    {
        // BUG-5 end-to-end probe: unlike ArchetypeALaunchContractTests (in-memory ImmutableArray
        // assertions) this drives the PRODUCTION ClaudeLaunchContract.Compose output through the REAL
        // AgentBackend.StartTurnAsync spawn boundary (a real OS process, not a mock/fake), and reads the
        // flags back off that process's actual argv — the same non-vacuous echo-launcher technique as
        // StartTurnSetsWorkingDirectoryToRootAndPassesArgsUnchangedWithNoCwd above, but sourcing the args
        // from Compose(modelId:, effortLevel:) rather than a hand-typed list.
        string launcher = WriteEchoLauncher("echo-model-effort.sh", EchoCwdAndArgsScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");

        string root = Path.Combine(_dir, "model-effort-root");
        Directory.CreateDirectory(root);
        NormcasedPath workingRoot = ResolveRoot(root);

        ImmutableArray<string> args = HoardPad.Core.Ai.Provider.ClaudeLaunchContract.Compose(
            maxBudgetUsd: 1m,
            settingsJsonPath: "/s.json",
            modelId: "claude-opus-4-8",
            effortLevel: "high");
        var request = new AgentTurnRequest("do the thing", workingRoot, args);

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // 1 CWD line + one ARG line per composed token.
        List<string> lines = await ReadStreamLinesAsync(turn, count: 1 + args.Length, EchoReadinessTimeout);

        Assert.NotEmpty(lines);
        var observedArgs = lines.Skip(1).Select(l => l["ARG:".Length..]).ToList();
        Assert.Equal(args, observedArgs);

        int modelIdx = observedArgs.IndexOf("--model");
        int effortIdx = observedArgs.IndexOf("--effort");
        Assert.True(modelIdx >= 0, "--model must reach the real process argv");
        Assert.True(effortIdx >= 0, "--effort must reach the real process argv");
        Assert.Equal("claude-opus-4-8", observedArgs[modelIdx + 1]);
        Assert.Equal("high", observedArgs[effortIdx + 1]);
    }

    // ---- QA2-3: the prompt is delivered to the child's stdin at the real launch layer ----

    [Fact]
    public async Task StartTurnDeliversThePromptToTheChildStdin()
    {
        // QA2-3 regression guard: request.Prompt was never wired to the launched process, so the AI
        // panel waited forever. This proves the wire exists at the REAL Process/ProcessStartInfo layer
        // (not a mocked transport): a launcher that reads all of stdin (to EOF) and echoes it back on
        // stdout. If the prompt is not written+closed to the child's stdin, `cat` blocks on EOF and the
        // "STDIN:" line never arrives — the bounded read then fails the Assert loudly (non-vacuous).
        string launcher = WriteEchoLauncher("echo-stdin.sh", EchoStdinScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");

        string root = Path.Combine(_dir, "root-stdin");
        Directory.CreateDirectory(root);

        // A prompt containing a leading '-' and disarm-looking substrings: if a future refactor ever
        // routed the prompt through argv, these would trip LaunchArgsSafety; on stdin they are inert text.
        const string prompt = "-hello from the panel; --dangerously-skip-permissions is just text here";
        var request = new AgentTurnRequest(prompt, ResolveRoot(root), ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // The script echoes exactly one "STDIN:<prompt>" line, then sleeps to hold the group alive past
        // the setsid leadership poll. Read that one readiness line via the bounded deadline, then break.
        List<string> lines = await ReadStreamLinesAsync(turn, count: 1, EchoReadinessTimeout);

        Assert.NotEmpty(lines);
        Assert.Equal($"STDIN:{prompt}", lines[0]);
    }

    [Fact]
    public async Task StartTurnClosesStdinSoAChildBlockedOnEofCanProceed()
    {
        // The hostile-review probe proved `claude -p` blocks forever if stdin is left open after the
        // prompt is written — skipping the CLOSE silently re-creates QA2-3. This test pins the close:
        // the launcher's `cat` only returns (and only then echoes READY) once stdin reaches EOF. If the
        // host wrote the prompt but never closed stdin, `cat` hangs, no READY line arrives, and the
        // bounded read fails. Reaching READY proves the write-end was closed (EOF delivered).
        string launcher = WriteEchoLauncher("stdin-eof.sh", EchoStdinScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-stdin-eof");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest("anything", ResolveRoot(root), ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // The 2nd line the script emits is "READY", printed only AFTER `cat` saw EOF. Its arrival is the
        // proof that stdin was closed. (Line 1 is the echoed prompt; line 2 is READY.)
        List<string> lines = await ReadStreamLinesAsync(turn, count: 2, EchoReadinessTimeout);

        Assert.Contains("READY", lines);
    }

    [Fact]
    public async Task StartTurnClosesStdinEvenForAnEmptyPromptSoTheChildStillReachesEof()
    {
        // Coverage gap: the AgentTurnRequest ctor validates only LaunchArgs, so an EMPTY prompt is a valid
        // input the App can produce (e.g. a resend with no text). WritePromptToStdinAsync must still
        // write-then-CLOSE for a zero-byte payload — otherwise `claude -p` blocks on EOF forever and the
        // empty-prompt turn silently hangs (a QA2-3 variant). The `cat` in EchoStdinScript captures the
        // (empty) stdin and echoes "STDIN:" with nothing after it, then reaches READY only once EOF is
        // delivered. Both the empty STDIN echo AND READY arriving prove the empty write was flushed+closed.
        string launcher = WriteEchoLauncher("echo-stdin-empty.sh", EchoStdinScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-stdin-empty");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest(string.Empty, ResolveRoot(root), ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        List<string> lines = await ReadStreamLinesAsync(turn, count: 2, EchoReadinessTimeout);

        Assert.Equal("STDIN:", lines[0]);   // empty payload — the "STDIN:" prefix with nothing appended.
        Assert.Contains("READY", lines);    // READY is reachable only after EOF, so the empty write closed stdin.
    }

    [Fact]
    public async Task StartTurnDeliversAVeryLargePromptWithoutBlockingTheLaunchOrStdoutDrain()
    {
        // Coverage gap for the WHOLE REASON WritePromptToStdinAsync runs on the async (fire-and-forget)
        // path: a prompt larger than the OS pipe buffer (~64 KB on Linux) cannot be written in one
        // non-blocking shot. If the write were issued synchronously on the launch thread, the write would
        // block once the pipe filled — and because the child only drains stdin AFTER we return and begin
        // draining stdout, the launch (and the whole turn) would deadlock. Here we push ~256 KB (4× the
        // pipe buffer) through and prove the child received EXACTLY that many bytes: the async write drained
        // the pipe concurrently with the child's read, and delivered a CLOSE (EOF) so `wc -c` could finish.
        const int payloadBytes = 256 * 1024;
        string prompt = new string('x', payloadBytes);   // ASCII 'x' → 1 byte each → byte count == char count.

        string launcher = WriteEchoLauncher("stdin-bytecount.sh", CountStdinBytesScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-stdin-large");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest(prompt, ResolveRoot(root), ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // The script prints "BYTES:<n>" only once `wc -c` has consumed ALL of stdin to EOF. A regression to a
        // synchronous/blocking write would deadlock here → no line arrives → the bounded read fails loudly.
        List<string> lines = await ReadStreamLinesAsync(turn, count: 1, EchoReadinessTimeout);

        Assert.NotEmpty(lines);
        Assert.Equal($"BYTES:{payloadBytes}", lines[0]);   // every byte reached the child, and stdin closed.
    }

    [Fact]
    public async Task DisposeWhileALargeStdinWriteIsInFlightCompletesPromptlyWithoutFaulting()
    {
        // Race-fix guard (cs-code-reviewer Warning 1): the stdin write runs on a background Task. If the
        // child never drains stdin, a prompt larger than the ~64 KB OS pipe buffer wedges the write task
        // mid-WriteAsync (the pipe is full). A fast DisposeAsync must NOT race that in-flight write against
        // _process.Dispose(): DisposeAsync awaits the captured write Task (bounded) BEFORE kill/dispose.
        // BoundedSleepScript prints READY then sleeps WITHOUT reading stdin, so the write stays pinned.
        const int payloadBytes = 256 * 1024;   // 4× the pipe buffer — cannot be written in one non-blocking shot.
        string prompt = new string('x', payloadBytes);

        string launcher = WriteEchoLauncher("stdin-nodrain.sh", BoundedSleepScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-stdin-dispose-race");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest(prompt, ResolveRoot(root), ImmutableArray.Create("-p"));

        IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // Wait for READY so the child is up and NOT draining stdin — the write is now wedged on the full pipe.
        string? ready = null;
        await foreach (AgentStreamEvent e in turn.StreamAsync(CancellationToken.None))
        {
            ready = e.RawJsonLine;
            break;
        }
        Assert.Equal("READY", ready);

        // Dispose while the write is in flight. It must complete WELL within the 2 s bounded drain + margin,
        // and must not surface an unobserved-task fault (the killed child breaks the pipe → benign IOException,
        // swallowed by the narrowed catch). A regression that awaits the wedged write unbounded would hang here.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await turn.DisposeAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"DisposeAsync should drain the in-flight stdin write within its bounded window, but took {sw.Elapsed}.");
    }

    [Fact]
    public async Task StartTurnDeliversShellMetacharacterPromptVerbatimOnStdinNeverEvaluated()
    {
        // Coverage gap: prove genuinely dangerous shell metacharacters ride stdin as INERT DATA — never
        // evaluated as a command, never routed to argv. If the prompt were ever spliced into a shell command
        // line or argv, the $(...) / backticks would execute and the round-trip would NOT be byte-identical
        // (and LaunchArgsSafety could even trip). `cat` reads the literal bytes; the echoed line must equal
        // the prompt EXACTLY — command substitution never fired because the text was pure stdin payload.
        const string prompt = "$(rm -rf /) `id` ; echo pwned | tee /tmp/x && : ${NOPE:-inert} \\backslash \"q\"";
        // Reflect via printf '%s' (NOT echo): dash's `echo` interprets backslash escapes in its argument, so a
        // literal "\\" in the payload would collapse on the way BACK out — a harness artifact, not a delivery
        // fault. printf '%s' emits the captured bytes verbatim, so the assertion tests the real round-trip.
        string launcher = WriteEchoLauncher("stdin-metachars.sh", PrintfStdinScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-stdin-meta");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest(prompt, ResolveRoot(root), ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        List<string> lines = await ReadStreamLinesAsync(turn, count: 1, EchoReadinessTimeout);

        Assert.NotEmpty(lines);
        // Byte-identical round-trip: the metacharacters were data on stdin, not an evaluated command.
        Assert.Equal($"STDIN:{prompt}", lines[0]);
    }

    [Fact]
    public async Task StartTurnDeliversMultiByteUnicodePromptAsUtf8WithNoBom()
    {
        // Coverage gap: WritePromptToStdinAsync uses UTF-8 with NO byte-order mark on purpose (a BOM on the
        // first bytes would corrupt the leading prompt token — this project was previously bitten by a
        // UTF-32-BOM corruption class, Run 7 M8.3). Push a multi-byte prompt (CJK, accents, an emoji outside
        // the BMP) and prove the child reads it back byte-for-byte: a BOM prefix, a wrong code page, or a
        // surrogate-pair mishandle would make the round-trip UNequal. The App's stream reader decodes the
        // child's stdout as UTF-8, so an exact string compare is a true UTF-8-in == UTF-8-out proof.
        const string prompt = "こんにちは — café — 𝓗oardPad — 🐉 dragon hoard";
        string launcher = WriteEchoLauncher("stdin-unicode.sh", EchoStdinScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-stdin-unicode");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest(prompt, ResolveRoot(root), ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        List<string> lines = await ReadStreamLinesAsync(turn, count: 1, EchoReadinessTimeout);

        Assert.NotEmpty(lines);
        // No BOM prefix, no mojibake: the UTF-8 no-BOM write round-trips exactly.
        Assert.Equal($"STDIN:{prompt}", lines[0]);
    }

    // ---- launch invariant: re-assert LaunchArgsSafety at the spawn boundary ----

    [Fact]
    public async Task StartTurnRefusesToSpawnWhenLaunchArgsCarryADisarmTokenPastTheCtor()
    {
        // AgentTurnRequest's ctor blocks a disarm token, but a `with` expression copies the
        // record via its init setters WITHOUT re-running the ctor — so a mutated request CAN
        // hold an unsafe arg list. The spawn-boundary re-assertion (M4.2 carry-forward) is the
        // defense-in-depth that catches it: StartTurnAsync must REFUSE rather than spawn.
        string launcher = WriteEchoLauncher("never-runs.sh", EchoCwdAndArgsScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");

        string root = Path.Combine(_dir, "root-disarm");
        Directory.CreateDirectory(root);

        // Construct a SAFE request (passes the ctor), then mutate LaunchArgs past the ctor.
        var safe = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p", "--verbose"));
        AgentTurnRequest smuggled = safe with
        {
            LaunchArgs = ImmutableArray.Create("-p", "--dangerously-skip-permissions"),
        };
        // Sanity: the `with` really did bypass the ctor (the unsafe value is held).
        Assert.False(LaunchArgsSafety.HonorsSafetyInvariants(smuggled.LaunchArgs));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backend.StartTurnAsync(descriptor, smuggled, NoEnv, CancellationToken.None));
        // And no escape file / no spawn side effect — the launcher script never ran.
        Assert.False(File.Exists(Path.Combine(root, "RAN.marker")));
    }

    [Fact]
    public async Task StartTurnRefusesToSpawnWhenLaunchArgsCarryBypassPermissionsPastTheCtor()
    {
        string launcher = WriteEchoLauncher("never-runs-2.sh", EchoCwdAndArgsScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-bypass");
        Directory.CreateDirectory(root);

        var safe = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p"));
        // bypassPermissions smuggled inside a --permission-mode value (Contains, not Equals).
        AgentTurnRequest smuggled = safe with
        {
            LaunchArgs = ImmutableArray.Create("-p", "--permission-mode", "bypassPermissions"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backend.StartTurnAsync(descriptor, smuggled, NoEnv, CancellationToken.None));
    }

    // ---- launch invariant: a never-resolved (default) WorkingRoot can never become a CWD ----

    [Fact]
    public async Task StartTurnThrowsOnADefaultWorkingRootSentinel()
    {
        string launcher = WriteEchoLauncher("never-runs-3.sh", EchoCwdAndArgsScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");

        // default(NormcasedPath) is the invalid sentinel — its .Value throws, so a never-resolved
        // root can never silently become the child's CWD.
        var request = new AgentTurnRequest("p", default, ImmutableArray.Create("-p"));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None));
    }

    // ---- the construction-time guard (the type cannot hold a disarm token) ----

    [Fact]
    public void AgentTurnRequestCtorRejectsDisarmTokensAndEmptyArgs()
    {
        NormcasedPath root = ResolveRoot(_dir);
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("p", root, ImmutableArray.Create("--cwd", "/elsewhere")));
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("p", root, ImmutableArray.Create("--dangerously-skip-permissions")));
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("p", root, ImmutableArray.Create("--permission-mode", "bypassPermissions")));
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("p", root, ImmutableArray<string>.Empty));
    }

    // ---- KillTreeAsync on Linux reaps the launched process group (the sole fail-closed enforcer) ----

    [Fact]
    public async Task KillTreeAsyncReapsTheLaunchedProcessGroupOnLinux()
    {
        // A launcher that signals it is up, then sleeps (a bounded sleep so a missed kill can
        // never leave a forever-orphan in the test harness). KillTreeAsync must reap the whole
        // process group via kill(-pgid, SIGKILL) — the SOLE fail-closed enforcer. We then PROVE
        // the kill worked by asserting the launched process actually exits.
        string launcher = WriteEchoLauncher("spin.sh", BoundedSleepScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-kill");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p"));

        IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // Wait for the launcher to print its "READY" line (the tree is up) before killing, so
        // the kill races a live process — not a not-yet-started one.
        string? ready = null;
        await foreach (AgentStreamEvent e in turn.StreamAsync(CancellationToken.None))
        {
            ready = e.RawJsonLine;
            break;   // first line is the readiness signal; stop draining.
        }
        Assert.Equal("READY", ready);

        // Capture the group-leader PID, then kill the tree.
        int pid = ReadPidFromMarker(Path.Combine(root, "pid.marker"));
        await turn.KillTreeAsync();

        // PROVE the kill reaped the group: the group-leader process is gone within the window.
        // (kill(-pgid, SIGKILL) is immediate; we poll briefly for the kernel to reap it.)
        Assert.True(await WaitForProcessGoneAsync(pid, TimeSpan.FromSeconds(5)),
            $"the launched process group (leader pid {pid}) should be reaped by KillTreeAsync but survived.");

        await turn.DisposeAsync();
    }

    [Fact]
    public async Task KillTreeAsyncReapsGrandchildrenOnLinux()
    {
        // MUST-FIX 6: a 3-level tree (setsid leader -> child shell -> grandchild sleep) must all
        // be reaped by kill(-pgid, SIGKILL). The grandchild PID is written to a marker by the
        // grandchild itself; we assert it is gone within the kill window.
        string launcher = WriteEchoLauncher("grandchild.sh", GrandchildScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-grandchild");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p"));

        IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // Wait for the READY signal (grandchild is up and has written its marker).
        string? ready = null;
        await foreach (AgentStreamEvent e in turn.StreamAsync(CancellationToken.None))
        {
            ready = e.RawJsonLine;
            break;
        }
        Assert.Equal("READY", ready);

        int leaderPid = ReadPidFromMarker(Path.Combine(root, "leader.marker"));
        int grandchildPid = ReadPidFromMarker(Path.Combine(root, "grandchild.marker"));

        await turn.KillTreeAsync();

        // Both the leader AND the grandchild must be gone.
        Assert.True(await WaitForProcessGoneAsync(leaderPid, TimeSpan.FromSeconds(5)),
            $"group leader (pid {leaderPid}) was not reaped by KillTreeAsync.");
        Assert.True(await WaitForProcessGoneAsync(grandchildPid, TimeSpan.FromSeconds(5)),
            $"grandchild (pid {grandchildPid}) was not reaped by KillTreeAsync — whole-tree kill failed.");

        await turn.DisposeAsync();
    }

    [Fact]
    public async Task KillTreeAsyncIsIdempotentDoubleKillDoesNotThrow()
    {
        // MUST-FIX 6: a second call to KillTreeAsync (or a concurrent call from DisposeAsync)
        // must be a no-op, not a double-CloseHandle / double-signal crash. The atomic guard
        // (Interlocked.Exchange on _killedFlag) makes subsequent calls return immediately.
        string launcher = WriteEchoLauncher("double-kill.sh", BoundedSleepScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-double");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p"));

        IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, NoEnv, CancellationToken.None);

        // Drain the READY line so the process is live.
        await foreach (AgentStreamEvent _ in turn.StreamAsync(CancellationToken.None))
            break;

        // Kill twice — neither call must throw.
        await turn.KillTreeAsync();
        var ex = await Record.ExceptionAsync(() => turn.KillTreeAsync());
        Assert.Null(ex);   // second kill must be a silent no-op.

        await turn.DisposeAsync();
    }

    // ---- M11 launch-env seam: the env is APPLIED onto the spawned child env (Option A) ----

    [Fact]
    public async Task StartTurnAppliesTheLaunchEnvOntoTheSpawnedChildEnvironment()
    {
        // The M11 launch-env seam (docs/escalation-m11-launch-env-seam.md): the env dict passed to
        // StartTurnAsync MUST reach the spawned process's environment (the hook reads
        // HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE / HOARDPAD_OBSERVE_* from it). A launcher that
        // echoes the env values back on stdout proves the child actually SEES the injected vars — so a
        // regression that dropped a key or never applied the env BREAKS this test (non-vacuous).
        string launcher = WriteEchoLauncher("echo-env.sh", EchoEnvScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");

        string root = Path.Combine(_dir, "root-env");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p"));

        var capPath = Path.Combine(_dir, "conv.capstate");
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOARDPAD_SANDBOX_ROOT"] = root,
            ["HOARDPAD_CAP_STATE_FILE"] = capPath,
            ["HOARDPAD_OBSERVE_CHANNEL"] = "/run/hp-observe.sock",
            ["HOARDPAD_OBSERVE_MODE"] = "approve",
        };

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, env, CancellationToken.None);

        // Read the four "KEY=value" lines the script echoes (it then sleeps to hold the process group
        // alive past the leadership poll, so we read a fixed count rather than draining to EOF). The read
        // AWAITS that readiness up to a generous bounded deadline rather than assuming the stream yields
        // within an implicit window — a starved child under a full parallel run can slip its scheduling.
        List<string> lines = await ReadStreamLinesAsync(turn, count: 4, EchoReadinessTimeout);

        // Each line is "KEY=value" the child read from ITS OWN environment — the seam delivered them.
        Assert.Contains($"HOARDPAD_SANDBOX_ROOT={root}", lines);
        Assert.Contains($"HOARDPAD_CAP_STATE_FILE={capPath}", lines);
        Assert.Contains("HOARDPAD_OBSERVE_CHANNEL=/run/hp-observe.sock", lines);
        Assert.Contains("HOARDPAD_OBSERVE_MODE=approve", lines);
    }

    [Fact]
    public async Task StartTurnAcceptsAnEmptyLaunchEnvWithoutThrowing()
    {
        // An empty env dict is valid (a turn with no live channel still launches; the App supplies the
        // sandbox/cap env, but an empty map must never throw — only a NULL env is rejected). Uses the
        // briefly-sleeping env script so the setsid leadership poll is not raced by an instant-exit child.
        string launcher = WriteEchoLauncher("echo-empty-env.sh", EchoEnvScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-empty-env");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(
            descriptor, request, new Dictionary<string, string>(), CancellationToken.None);

        // Drains without error — the empty env did not throw and the launch proceeded.
        await foreach (AgentStreamEvent _ in turn.StreamAsync(CancellationToken.None))
            break;
    }

    [Fact]
    public async Task StartTurnThrowsOnNullLaunchEnv()
    {
        string launcher = WriteEchoLauncher("never-runs-env.sh", EchoCwdAndArgsScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        string root = Path.Combine(_dir, "root-null-env");
        Directory.CreateDirectory(root);
        var request = new AgentTurnRequest("p", ResolveRoot(root), ImmutableArray.Create("-p"));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _backend.StartTurnAsync(descriptor, request, null!, CancellationToken.None));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task BinderAssembledEnvReachesTheSpawnedChildViaTheRealLaunchPath()
    {
        // Security finding 1 (Medium, latent) closure — bridges the two previously separate coverage
        // halves: (a) the binder-assembly tests proved the CORRECT DICT is assembled (using a capturing
        // fake backend), and (b) the echo-launcher test proved the REAL launch path delivers a
        // HAND-BUILT dict to the child. This test pins them together end-to-end:
        //
        //   real CapStateStore.ResolveCapStateFilePath + real PathResolver
        //       ↓ assemble env (same formula as AiSessionBinder.BindRootAsync)
        //   real AgentBackend.StartTurnAsync → real LaunchLinux → real ApplyEnv
        //       ↓
        //   echo-launcher child echoes its own environment back on stdout
        //       ↓ assert
        //   HOARDPAD_SANDBOX_ROOT == canonicalRoot.Value
        //   HOARDPAD_CAP_STATE_FILE == capStore.ResolveCapStateFilePath(conversationId)
        //
        // A regression in binder-assembly (wrong property, omitted key) or in ApplyEnv (dropped key)
        // produces a child that echoes the empty/wrong value → Assert.Contains fails.
        if (OperatingSystem.IsWindows())
            return; // win-x64 LaunchWindows leg is Deferred (env-block not yet injected).

        string sandboxDir = Path.Combine(_dir, "binder-e2e-root");
        Directory.CreateDirectory(sandboxDir);

        // Reproduce the binder's env-assembly: real CapStateStore (as AiSessionBinder uses it).
        string capBaseDir = Path.Combine(_dir, "binder-e2e-caps");
        Directory.CreateDirectory(capBaseDir);
        var capStore = new CapStateStore(capBaseDir, new AtomicFileStore());
        const string conversationId = "conv-e2e-env";

        // Canonical root from the real PathResolver (same code path as AiSessionBinder.BindRootAsync).
        NormcasedPath canonicalRoot = new PathResolver().ResolveCanonical(sandboxDir, sandboxDir);

        // Env assembled the same way AiSessionBinder.BindRootAsync assembles it
        // (HookEnvironment is empty when there is no live channel — normal-case here).
        var launchEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOARDPAD_SANDBOX_ROOT"] = canonicalRoot.Value,
            ["HOARDPAD_CAP_STATE_FILE"] = capStore.ResolveCapStateFilePath(conversationId),
        };

        string launcher = WriteEchoLauncher("binder-e2e-echo.sh", EchoEnvScript);
        var descriptor = new LaunchDescriptor(launcher, IsCmd: false, FromPath: false, ResolvedVia: "test");
        var request = new AgentTurnRequest("p", canonicalRoot, ImmutableArray.Create("-p"));

        await using IAgentTurn turn = await _backend.StartTurnAsync(descriptor, request, launchEnv, CancellationToken.None);

        // Read the four env-echo lines then break (the script sleeps 30 s after echoing; breaking early
        // does not wait for the sleep — DisposeAsync reaps the group at test end). The read AWAITS that
        // readiness up to a generous bounded deadline rather than racing a fixed window — under a full
        // parallel run a starved child's scheduling can slip past an implicit timing assumption.
        List<string> lines = await ReadStreamLinesAsync(turn, count: 4, EchoReadinessTimeout);

        // The child echoed what its own process environment contained — the seam delivered the dict.
        Assert.Contains($"HOARDPAD_SANDBOX_ROOT={canonicalRoot.Value}", lines);
        Assert.Contains($"HOARDPAD_CAP_STATE_FILE={capStore.ResolveCapStateFilePath(conversationId)}", lines);
    }

    // ---- helpers ----

    // The empty additional-env dict the launch-invariant tests use (the env is orthogonal to those).
    private static readonly Dictionary<string, string> NoEnv = new(StringComparer.Ordinal);

    // Resolve a real directory to a NormcasedPath via the production resolver (the only producer).
    private static NormcasedPath ResolveRoot(string dir) =>
        new PathResolver().ResolveCanonical(dir, dir);

    private string WriteEchoLauncher(string name, string body)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, body);
        // chmod +x so it can be exec'd via setsid. These tests run on linux-x64 only; the guard
        // keeps the analyzer happy (SetUnixFileMode is Linux/macOS-only) — the Windows legs are
        // runtime-Deferred and never execute here.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    // Prints "CWD:<pwd>" then "ARG:<argv-token>" per arg — one per stream line — then HOLDS the
    // process group alive with a bounded sleep. Without the sleep an instant-exit child can be reaped
    // by the OS before the production WaitForProcessGroupLeadership(pid, 500) poll reads getpgid, returning
    // -1 and throwing from StartTurnAsync. The same liveness reason that motivates EchoEnvScript's sleep.
    // Tests that read these lines use ReadStreamLinesAsync with a bounded readiness deadline and break
    // early; the sleep is never waited on — DisposeAsync reaps the group at test end.
    private const string EchoCwdAndArgsScript =
        "#!/bin/sh\n" +
        "echo \"CWD:$PWD\"\n" +
        "for a in \"$@\"; do echo \"ARG:$a\"; done\n" +
        "sleep 30\n";

    // Echoes the M11 launch-env vars the child read from ITS OWN environment — one "KEY=value" per
    // line — then HOLDS the process group alive with a bounded sleep so the child does not exit before
    // the production setsid process-group-leadership step completes (an instant-exit child can vanish
    // before WaitForProcessGroupLeadership confirms the group leader; the same liveness reason the kill
    // tests use BoundedSleepScript's sleep 30). The TEST reads the four echoed lines via a bounded
    // readiness wait (ReadStreamLinesAsync) — it does not assume the stream yields within an implicit
    // window — then breaks, so it never waits for the sleep; DisposeAsync reaps the group at test end.
    private const string EchoEnvScript =
        "#!/bin/sh\n" +
        "echo \"HOARDPAD_SANDBOX_ROOT=$HOARDPAD_SANDBOX_ROOT\"\n" +
        "echo \"HOARDPAD_CAP_STATE_FILE=$HOARDPAD_CAP_STATE_FILE\"\n" +
        "echo \"HOARDPAD_OBSERVE_CHANNEL=$HOARDPAD_OBSERVE_CHANNEL\"\n" +
        "echo \"HOARDPAD_OBSERVE_MODE=$HOARDPAD_OBSERVE_MODE\"\n" +
        "sleep 30\n";

    // QA2-3 stdin round-trip: read ALL of stdin to EOF (`cat` returns only once the host closes the
    // child's stdin write end), echo it back as one "STDIN:<prompt>" line, then print READY (which can
    // ONLY be reached after `cat` saw EOF — so its arrival proves stdin was closed). Bounded sleep 30 s
    // holds the process group alive past the setsid leadership poll; tests read the readiness lines via
    // ReadStreamLinesAsync and break, so the sleep is never waited on (DisposeAsync reaps the group).
    private const string EchoStdinScript =
        "#!/bin/sh\n" +
        "PROMPT=$(cat)\n" +
        "echo \"STDIN:$PROMPT\"\n" +
        "echo READY\n" +
        "sleep 30\n";

    // Same shape as EchoStdinScript but reflects the captured stdin with `printf '%s'` instead of `echo`, so a
    // payload containing literal backslashes round-trips byte-faithfully (dash's `echo` would interpret \b, \\,
    // … as escapes on the way back out — a harness artifact). Used by the shell-metacharacter round-trip test.
    private const string PrintfStdinScript =
        "#!/bin/sh\n" +
        "PROMPT=$(cat)\n" +
        "printf 'STDIN:%s\\n' \"$PROMPT\"\n" +
        "echo READY\n" +
        "sleep 30\n";

    // Large-prompt round-trip: `wc -c` counts EVERY stdin byte, but only returns once stdin reaches EOF —
    // so "BYTES:<n>" is printed only after the whole payload arrived AND the write end was closed. This is
    // the async-write-path guard: a payload past the ~64 KB pipe buffer would deadlock a synchronous write,
    // so no line would arrive. `tr -d ' '` strips wc's leading pad so the count compares cleanly. Bounded
    // sleep holds the group alive past the setsid leadership poll (read-then-break; sleep is never awaited).
    private const string CountStdinBytesScript =
        "#!/bin/sh\n" +
        "N=$(wc -c | tr -d ' ')\n" +
        "echo \"BYTES:$N\"\n" +
        "sleep 30\n";

    // Emits "READY" + records its own PID (the setsid group leader), then sleeps a BOUNDED 30 s
    // (a safety net: even if a kill were missed, no forever-orphan can accumulate in the harness).
    // WorkingDirectory is the resolved root, so the PID marker is written to CWD.
    private const string BoundedSleepScript =
        "#!/bin/sh\n" +
        "echo $$ > pid.marker\n" +
        "echo READY\n" +
        "sleep 30\n";

    // A 3-level tree (MUST-FIX 6 grandchild test): the leader forks a child which forks a
    // grandchild. All three share the same process group (the setsid group). The grandchild
    // writes its PID to grandchild.marker; the leader writes its PID to leader.marker and emits
    // READY only after the grandchild marker is confirmed present. Bounded sleep safety net: 30 s.
    private const string GrandchildScript =
        "#!/bin/sh\n" +
        "echo $$ > leader.marker\n" +
        // fork a subshell that itself forks a grandchild
        "( sh -c 'echo $$ > grandchild.marker; sleep 30' ) &\n" +
        // wait for the grandchild marker to appear (max 2 s for process startup)
        "i=0; while [ ! -f grandchild.marker ] && [ $i -lt 40 ]; do sleep 0.05; i=$((i+1)); done\n" +
        "echo READY\n" +
        "sleep 30\n";

    private static int ReadPidFromMarker(string markerPath)
    {
        // The launcher writes its PID; poll briefly for the marker to appear.
        for (int i = 0; i < 50 && !File.Exists(markerPath); i++)
            Thread.Sleep(20);
        return int.Parse(File.ReadAllText(markerPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> WaitForProcessGoneAsync(int pid, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!ProcessExists(pid))
                return true;
            await Task.Delay(50);
        }
        return !ProcessExists(pid);
    }

    private static bool ProcessExists(int pid) => Directory.Exists($"/proc/{pid}");

    // Read up to <paramref name="count"/> stream lines, bounded by a GENEROUS readiness deadline rather than
    // racing a fixed wall-clock window. The echo-env tests need exactly the readiness signal of the child's
    // first <paramref name="count"/> echoed lines; under a full-solution parallel run a starved child's
    // scheduling can slip, so we AWAIT that readiness (up to the deadline) instead of assuming the stream
    // yields within an implicit window. The deadline is a safety net only — the loop returns as soon as the
    // count is reached; if it ever elapsed, the caller's Assert.Contains on the missing line fails loudly
    // (never a silent pass). Mirrors the production readiness-poll posture (WaitForProcessGoneAsync above).
    private static async Task<List<string>> ReadStreamLinesAsync(IAgentTurn turn, int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var lines = new List<string>();
        await foreach (AgentStreamEvent e in turn.StreamAsync(cts.Token))
        {
            lines.Add(e.RawJsonLine);
            if (lines.Count == count)
                break;
        }
        return lines;
    }

    // The readiness budget for the echo-env tests — generous enough to absorb scheduler starvation under a
    // full-solution parallel run, while still bounding a genuinely dead stream so a real regression fails fast.
    private static readonly TimeSpan EchoReadinessTimeout = TimeSpan.FromSeconds(20);
}
