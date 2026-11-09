// AgentBackend.cs — the claude -p launch + whole-tree kill (M5.2, FR-24 / NFR-8).
//
// This is the production port of the SPIKE-4 ShimResolver + WindowsJobObject
// prototype (spikes/spike-4/kill/*). It is the OS leg of Archetype A (architecture
// §4.2 step 1+5): resolve the launcher, launch one turn with the FR-24 §4.1 flags
// (passed through VERBATIM — the orchestration owns the flag set), and kill the
// WHOLE process tree on cancel. The kill is the SOLE fail-closed enforcer (§4.4) —
// it must fire before any hook timeout.
//
// NON-NEGOTIABLE FR-24/FR-26 launch invariants (each is a test in M5.2):
//   - WorkingDirectory = the resolved open-folder root (request.WorkingRoot).
//   - NO --cwd flag is ever added (there is no --cwd for -p; SPIKE-3 §A2).
//   - request.LaunchArgs are launched UNCHANGED — the host authors no flags.
//   - LaunchArgsSafety.HonorsSafetyInvariants is RE-ASSERTED at the spawn boundary
//     (defense-in-depth on top of AgentTurnRequest's ctor assertion, M4.2 carry-fwd):
//     a violating arg list REFUSES to spawn.
//   - bypassPermissions / --dangerously-skip-permissions are NEVER added.
//
// CROSS-PLATFORM (architecture §4.2 step 1, TD §2.3, SPIKE-4):
//   Linux (runs):  launch via `setsid` so the child LEADS its own process group;
//                  KillTree = kill(-pgid, SIGKILL) on that group. Verified-shape
//                  analogue of the SPIKE-4 harness no-orphan run.
//   Windows (compile-only / Deferred): CREATE_SUSPENDED -> AssignProcessToJobObject
//                  (while suspended) -> ResumeThread; KillTree = TerminateJobObject
//                  + taskkill /T /F fallback. Guarded by OperatingSystem.IsWindows()
//                  + [SupportedOSPlatform("windows")]; never run/asserted on Linux.
//
// KILL SAFETY (MUST-FIX 6 hardening):
//   The PGID is captured ONCE at launch while the child is known-live. KillTreeAsync
//   reuses that captured PGID — it does NOT re-derive via getpgid at kill time (PID-reuse
//   race + the setsid-already-leader fork edge). The kill guard is atomic via
//   Interlocked.Exchange on an int field (prevents double-CloseHandle on Windows and
//   double-kill on Linux). The managed Process.Kill(entireProcessTree:true) backstop
//   fires unconditionally — even when HasExited — because the process tree may have
//   outlived the leader.
//
// STDERR (MUST-FIX 7):
//   stderr is NOT redirected on the stream-pump PSI. A chatty agent that writes >~64 KB
//   to stderr would fill the pipe and deadlock the child (blocking on write) if we
//   redirect without draining. The fix is simpler than a background drain: let stderr
//   inherit the parent's handle so it flows to the terminal / log without buffering.
//   (The self-test PSI in ToolPermissionHookHost keeps stderr redirected and drains it.)

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// Launches <c>claude -p</c> and kills the whole process tree on cancel (NFR-8).
/// Implements <see cref="IAgentBackend"/>. Linux uses <c>setsid</c> + process-group
/// <c>SIGKILL</c>; Windows uses a Job Object + <c>taskkill</c> fallback (compiled,
/// runtime Deferred).
/// </summary>
public sealed class AgentBackend : IAgentBackend
{
    // Linux: the absolute path to setsid(1). The child is launched through it so the
    // child becomes its own session/process-group leader (architecture §4.2 step 1).
    // Killing kill(-pgid, SIGKILL) then reaps the whole tree without touching HoardPad.
    private const string SetsidPath = "/usr/bin/setsid";

    // ---- FR-24 shim resolver (claude.cmd -> claude.exe -> configured path) ----

    public LaunchDescriptor ResolveLauncher(string? configuredPath)
    {
        return OperatingSystem.IsWindows()
            ? ResolveWindows(configuredPath)
            : ResolvePosix(configuredPath);
    }

    private static LaunchDescriptor ResolveWindows(string? configuredPath)
    {
        // 1. PATH probe for claude.cmd (typical npm/volta/nvm install — a .cmd shim).
        string? cmdPath = FindOnPath("claude.cmd");
        if (cmdPath is not null)
            return new LaunchDescriptor(cmdPath, IsCmd: true, FromPath: true, ResolvedVia: "PATH:claude.cmd");

        // 2. PATH probe for claude.exe (native install, less common).
        string? exePath = FindOnPath("claude.exe");
        if (exePath is not null)
            return new LaunchDescriptor(exePath, IsCmd: false, FromPath: true, ResolvedVia: "PATH:claude.exe");

        // 3. User-configured explicit path (HoardPad settings override).
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
        {
            bool isCmd = configuredPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            return new LaunchDescriptor(configuredPath, IsCmd: isCmd, FromPath: false, ResolvedVia: "configured-path");
        }

        throw new FileNotFoundException(
            "Could not resolve the claude CLI: no claude.cmd or claude.exe on PATH and no valid configured path "
            + "(FR-24 shim order: claude.cmd -> claude.exe -> configured path).");
    }

    private static LaunchDescriptor ResolvePosix(string? configuredPath)
    {
        // 1. PATH probe for the bare `claude` binary.
        string? binaryPath = FindOnPath("claude");
        if (binaryPath is not null)
            return new LaunchDescriptor(binaryPath, IsCmd: false, FromPath: true, ResolvedVia: "PATH:claude");

        // 2. User-configured explicit path.
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
            return new LaunchDescriptor(configuredPath, IsCmd: false, FromPath: false, ResolvedVia: "configured-path");

        // 3. BUG-4 fallback: ~/.local/bin/claude is the npm/nvm install location that desktop
        //    launchers omit from PATH (only shell-sourced profiles add it). No shell expansion,
        //    no process spawn — a plain File.Exists + execute-bit check is sufficient.
        string localBinClaude = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "claude");
        if (File.Exists(localBinClaude) && IsExecutable(localBinClaude))
            return new LaunchDescriptor(localBinClaude, IsCmd: false, FromPath: false, ResolvedVia: "~/.local/bin/claude");

        throw new FileNotFoundException(
            "Could not resolve the claude CLI: no claude binary on PATH and no valid configured path "
            + "(FR-24 shim order: claude -> configured path -> ~/.local/bin/claude).");
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> has at least one execute bit set.
    /// On Posix systems this uses <see cref="File.GetUnixFileMode"/>; on Windows (compile-only, Deferred)
    /// we conservatively return <c>true</c> because POSIX bits do not apply.
    /// </summary>
    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return true; // Windows: execute semantics are extension-based; the caller guards on Posix only.
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false; // stat failed — treat as non-executable.
        }
    }

    /// <summary>Scan each <c>PATH</c> directory for <paramref name="fileName"/>.</summary>
    private static string? FindOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
            return null;

        char separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (string dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // ---- launch one turn ----

    public Task<IAgentTurn> StartTurnAsync(
        LaunchDescriptor launcher, AgentTurnRequest request, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(env);
        ct.ThrowIfCancellationRequested();

        // ---- DEFENSE-IN-DEPTH: re-assert the FR-24 §4.1 launch-safety invariants at
        // the spawn boundary (M4.2 carry-forward). AgentTurnRequest's ctor already
        // asserted these, but a host bug or a future refactor could route an arg list
        // here without going through that ctor — so we re-check and REFUSE to spawn if
        // it ever fails. This is the one place a disarm token could reach the process,
        // so it is the one place that re-validates, independent of the type guard. ----
        if (!LaunchArgsSafety.HonorsSafetyInvariants(request.LaunchArgs))
            throw new InvalidOperationException(
                "Refusing to spawn the agent: request.LaunchArgs violates an FR-24 §4.1 safety invariant "
                + "(empty, --cwd, --dangerously-skip-permissions, or a bypassPermissions token). This is the "
                + "spawn-boundary re-assertion of LaunchArgsSafety (M4.2 carry-forward) — the sandbox is never "
                + "disarmed by a smuggled launch flag.");

        // WorkingDirectory IS the resolved open-folder root; NO --cwd flag is added.
        // request.WorkingRoot.Value throws if the path is the default sentinel (NormcasedPath),
        // so a never-resolved root can never become a silent CWD.
        string workingRoot = request.WorkingRoot.Value;
        if (!Directory.Exists(workingRoot))
            throw new DirectoryNotFoundException(
                $"The resolved open-folder root '{workingRoot}' does not exist; refusing to launch the agent there.");

        IAgentTurn turn = OperatingSystem.IsWindows()
            ? LaunchWindows(launcher, request, workingRoot, env)
            : LaunchLinux(launcher, request, workingRoot, env);

        return Task.FromResult(turn);
    }

    // ---- M11 launch-env seam (Option A): the SINGLE env-application point both LaunchLinux and the
    // self-test route through, so the production env-delivery is the gated code path (a mutation here —
    // dropping HOARDPAD_SANDBOX_ROOT, flipping a key — breaks the self-test that drives the real spawn).
    // The child inherits the parent env by default; these keys are ADDED/OVERRIDDEN on top of it. The
    // env never becomes a launch arg — the guarded AgentTurnRequest ctor + LaunchArgsSafety are untouched
    // (invariant 7). ----

    internal static void ApplyEnv(ProcessStartInfo psi, IReadOnlyDictionary<string, string> env)
    {
        foreach (KeyValuePair<string, string> kv in env)
            psi.Environment[kv.Key] = kv.Value;
    }

    // ---- QA2-3 prompt delivery: write request.Prompt to the child's stdin, then close it (EOF). ----
    //
    // Requires RedirectStandardInput = true on the child's PSI. The write runs on a background Task so it
    // does NOT block the synchronous launch return — stdout draining (which begins after StartTurnAsync
    // returns) can proceed concurrently even for a large prompt. The returned Task is CAPTURED by the
    // caller onto the LinuxAgentTurn so DisposeAsync can await it (bounded) before killing/disposing the
    // process — otherwise a fast dispose races the in-flight WriteAsync/FlushAsync against Dispose().
    // The write-then-CLOSE is mandatory: `claude -p` does not act until stdin reaches EOF, so leaving
    // the stream open reproduces QA2-3 exactly (hostile-review probe 1). UTF-8 with NO byte-order mark
    // (a BOM on the first bytes would corrupt the leading prompt token).
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static Task WritePromptToStdinAsync(Process process, string prompt)
    {
        return Task.Run(async () =>
        {
            try
            {
                await using StreamWriter stdin = process.StandardInput;
                Stream baseStream = stdin.BaseStream;
                byte[] bytes = Utf8NoBom.GetBytes(prompt);
                await baseStream.WriteAsync(bytes).ConfigureAwait(false);
                await baseStream.FlushAsync().ConfigureAwait(false);
                // `await using` disposes the writer here, closing stdin and sending EOF to the child.
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Expected benign race ONLY: the child already exited / closed its stdin (broken pipe),
                // or the process handle was disposed by a concurrent teardown. The turn ends via the
                // stdout EOF path. Any OTHER exception propagates — the caller (DisposeAsync) awaits this
                // Task, so it is observed rather than crashing the process as an unobserved-task fault.
            }
        });
    }

    // ---- Linux launch (runs): setsid -> child leads its own process group ----

    private static LinuxAgentTurn LaunchLinux(
        LaunchDescriptor launcher, AgentTurnRequest request, string workingRoot,
        IReadOnlyDictionary<string, string> env)
    {
        // Launch through setsid(1) so the child becomes its own session + process-group
        // leader; killing kill(-pgid, SIGKILL) then reaps the whole tree (claude shim ->
        // node worker -> any grandchildren) without signalling HoardPad's own group.
        // The harness no-orphan run uses exactly this pattern (spike-4 harness §sleeper).
        //
        // stderr is NOT redirected here (MUST-FIX 7): a chatty agent writing >~64 KB to
        // stderr would block on write and deadlock the child. Let it inherit the parent's handle.
        var psi = new ProcessStartInfo
        {
            FileName = SetsidPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,   // inherit — do NOT redirect (MUST-FIX 7).
            RedirectStandardInput = true,     // QA2-3: the prompt is delivered on the child's stdin (see WritePromptToStdinAsync).
            WorkingDirectory = workingRoot,   // FR-24: WorkingDirectory = resolved root, NO --cwd.
        };
        // argv = setsid <claude> <launchArgs...> — launchArgs UNCHANGED (host authors no flags).
        psi.ArgumentList.Add(launcher.ExecutablePath);
        foreach (string arg in request.LaunchArgs)
            psi.ArgumentList.Add(arg);

        // M11 launch-env seam (Option A): apply the host-built env (HOARDPAD_SANDBOX_ROOT /
        // HOARDPAD_CAP_STATE_FILE + the optional HOARDPAD_OBSERVE_* channel vars) ON TOP of the
        // inherited environment. setsid(1) exec-replaces itself with the child, so the env on this PSI
        // is inherited straight through to the claude process the hook runs under.
        ApplyEnv(psi, env);

        Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch the agent via setsid for '{launcher.ExecutablePath}'.");

        // QA2-3: deliver request.Prompt to the child on stdin, then CLOSE stdin (send EOF).
        // The prompt is carried on AgentTurnRequest.Prompt and NEVER enters argv — so
        // LaunchArgsSafety / the AgentTurnRequest guarded ctor are untouched (frozen). The write
        // runs on a background Task, so it does NOT gate this synchronous launch return and stdout
        // draining can proceed concurrently with a large stdin write. The Task is CAPTURED onto the
        // turn so DisposeAsync can await it (bounded) before kill/dispose — closing the disposal race.
        //   - CLOSE is mandatory: `claude -p` blocks forever waiting for EOF if stdin is left open
        //     (hostile-review probe 1) — skipping the close silently re-creates QA2-3.
        //   - UTF-8, NO BOM: a BOM on the first stdin bytes would corrupt the leading prompt token
        //     (this project was previously bitten by a UTF-32-BOM corruption class, Run 7 M8.3).
        Task stdinWriteTask = WritePromptToStdinAsync(process, request.Prompt);

        // Capture the PGID after setsid has had time to call setsid() and exec the child.
        // setsid(1) exec-replaces itself with the target program, so _process.Id is also the
        // group leader PID — but we must poll briefly because getpgid() on the process between
        // fork and exec still reflects the PARENT's group. Once setsid() has been called
        // (inside the child, before exec), getpgid returns the child's own PID.
        int pid = process.Id;
        int pgid = WaitForProcessGroupLeadership(pid, timeoutMs: 500);
        if (pgid != pid)
        {
            // Unexpected: the child did not become the process-group leader within the window.
            // Kill it and refuse — rather than silently mis-targeting kill(-pgid).
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            process.Dispose();
            throw new InvalidOperationException(
                $"setsid launch invariant violated: expected getpgid({pid}) == {pid} but got {pgid} "
                + "after a 500 ms wait. The child is not the process-group leader — kill(-pgid) "
                + "would target the wrong group. Refusing to leave the agent running uncontrolled (fail-closed).");
        }

        return new LinuxAgentTurn(process, pgid, stdinWriteTask);
    }

    // ---- Windows launch (compile-only / Deferred): CREATE_SUSPENDED -> Job -> Resume ----

    [SupportedOSPlatform("windows")]
    private static WindowsAgentTurn LaunchWindows(
        LaunchDescriptor launcher, AgentTurnRequest request, string workingRoot,
        IReadOnlyDictionary<string, string> env)
    {
        // M11 launch-env seam — win-x64 env-block injection Deferred (runtime leg). The native
        // CreateProcessW path below passes lpEnvironment = nint.Zero (inherit). Delivering the M11 env
        // here means building a custom UTF-16 environment block (the inherited block + these keys) and
        // passing it through CreateProcessW — a Windows-hardware task, Deferred consistent with the rest
        // of the Windows AI spine (Job-Object kill, taskkill fallback). The `env` param is accepted so the
        // seam signature is uniform across RIDs and win-x64 builds 0W/0E; it is intentionally NOT applied
        // on this never-run-on-Linux path. NOTE: when this runtime leg is built, the env MUST be delivered
        // (a Windows turn with no HOARDPAD_SANDBOX_ROOT would fail-closed-deny every write).
        _ = env;

        // Step 1: create the Job Object with KILL_ON_JOB_CLOSE so the whole tree dies
        // when TerminateJobObject is called or the handle closes (SPIKE-4 primary).
        nint jobHandle = WindowsAgentNative.CreateJobObject(nint.Zero, null);
        if (jobHandle == nint.Zero)
            throw new InvalidOperationException(
                $"CreateJobObject failed with Win32 error {Marshal.GetLastWin32Error()}.");

        var extInfo = new WindowsAgentNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new WindowsAgentNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = WindowsAgentNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        int extInfoSize = Marshal.SizeOf<WindowsAgentNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!WindowsAgentNative.SetInformationJobObject(
                jobHandle,
                WindowsAgentNative.JobObjectInformationClass.ExtendedLimitInformation,
                ref extInfo,
                extInfoSize))
        {
            WindowsAgentNative.CloseHandle(jobHandle);
            throw new InvalidOperationException(
                $"SetInformationJobObject failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        // Step 2: launch CREATE_SUSPENDED so the child is assigned to the Job BEFORE it
        // can spawn the node worker (the suspend-before-assign ordering closes the race
        // where the .cmd shim spawns node outside the Job).
        // Build the command line: "<launcher>" <launchArgs...> — launchArgs UNCHANGED.
        // stderr is NOT redirected (MUST-FIX 7): inherit the parent handle.
        //
        // QA2-3 (structural parity with the Linux leg — RUNTIME-DEFERRED, untestable on a Linux build host):
        // create an inheritable stdin pipe and pass its READ end via STARTUPINFOW.hStdInput so the child
        // reads request.Prompt from stdin (mirrors LaunchLinux's RedirectStandardInput + write-then-close).
        // The prompt NEVER enters argv — launchArgs stay UNCHANGED and LaunchArgsSafety / the guarded ctor
        // are untouched. The write end is returned so the prompt can be written and CLOSED (EOF) below;
        // the close is mandatory (`claude -p` blocks until stdin EOF).
        string commandLine = WindowsAgentNative.BuildCommandLine(launcher.ExecutablePath, request.LaunchArgs);
        nint processHandle = WindowsAgentNative.CreateSuspendedProcess(
            commandLine, workingRoot, out int childPid, out nint stdinWriteHandle);
        if (processHandle == nint.Zero)
        {
            WindowsAgentNative.CloseHandle(jobHandle);
            throw new InvalidOperationException(
                $"CreateProcess(CREATE_SUSPENDED) failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        // Step 3: assign the SUSPENDED child to the Job, then resume.
        if (!WindowsAgentNative.AssignProcessToJobObject(jobHandle, processHandle))
        {
            WindowsAgentNative.TerminateProcess(processHandle, 1);
            WindowsAgentNative.CloseHandle(processHandle);
            WindowsAgentNative.CloseHandle(jobHandle);
            throw new InvalidOperationException(
                $"AssignProcessToJobObject failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        WindowsAgentNative.ResumeMainThread(childPid);
        WindowsAgentNative.CloseHandle(processHandle);

        // QA2-3 (runtime-Deferred, untestable on Linux): deliver request.Prompt to the child's stdin,
        // then CLOSE the write end (EOF). Structural parity with LaunchLinux's WritePromptToStdinAsync —
        // UTF-8 no-BOM, write-then-close (the close is mandatory or `claude -p` blocks awaiting EOF).
        WindowsAgentNative.WritePromptAndCloseStdin(stdinWriteHandle, request.Prompt);

        Process process = Process.GetProcessById(childPid);
        return new WindowsAgentTurn(process, jobHandle);
    }

    // ---- Linux turn: stream-json pump + process-group SIGKILL ----

    private sealed class LinuxAgentTurn : IAgentTurn
    {
        private readonly Process _process;
        // The PGID captured once at launch (while the child is known-live). Reused at kill
        // time — do NOT re-derive via getpgid (PID-reuse race after exit).
        private readonly int _pgid;
        // The QA2-3 stdin-write Task. DisposeAsync awaits it (bounded) BEFORE kill/dispose so a fast
        // teardown does not race the in-flight WriteAsync/FlushAsync against _process.Dispose().
        private readonly Task _stdinWriteTask;
        // Atomic kill guard: Interlocked.Exchange(ref _killedFlag, 1) prevents double-kill
        // (double-CloseHandle on Windows turn; on Linux prevents re-signalling a dead group).
        private int _killedFlag;

        public LinuxAgentTurn(Process process, int pgid, Task stdinWriteTask)
        {
            _process = process;
            _pgid = pgid;
            _stdinWriteTask = stdinWriteTask;
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            // The CLI emits one stream-json object per line on stdout. We surface each raw
            // line as an AgentStreamEvent; the §4.2 domain-side liveness match parses it.
            // Reading runs on the async I/O path (no blocking the caller); a background
            // pump is unnecessary because the enumerator IS the pump.
            // stderr is NOT redirected (MUST-FIX 7) — it inherits the parent's handle.
            StreamReader stdout = _process.StandardOutput;
            while (true)
            {
                string? line;
                try
                {
                    line = await stdout.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (line is null)
                    yield break;   // EOF — the agent exited or its stdout closed.
                if (line.Length == 0)
                    continue;      // skip blank separators between stream-json records.

                yield return new AgentStreamEvent(line);
            }
        }

        public Task KillTreeAsync()
        {
            // SOLE fail-closed enforcer (§4.4): kill the ENTIRE process group with SIGKILL.
            // The child was launched via setsid; the PGID was captured at launch (known-live).
            // kill(-pgid, SIGKILL) reaps all tree members (claude -> node -> grandchildren)
            // in one signal; HoardPad's own group is untouched.
            //
            // Atomic guard: only the first caller kills; subsequent calls are no-ops.
            if (System.Threading.Interlocked.Exchange(ref _killedFlag, 1) != 0)
                return Task.CompletedTask;

            // Primary: kill the whole captured process group.
            if (_pgid > 0)
                _ = PosixProcess.kill(-_pgid, PosixProcess.SIGKILL);

            // Backstop: the managed whole-tree kill fires UNCONDITIONALLY (even when HasExited)
            // because the process tree may have outlived the leader. HasExited refers only to the
            // setsid leader; grandchildren may still be running.
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best-effort — the SIGKILL above is the primary path */ }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            // Drain the in-flight stdin write BEFORE kill/dispose so it does not race _process.Dispose()
            // (the write task holds process.StandardInput). Bounded: a hung write (e.g. a wedged child
            // that read nothing) must not block disposal forever — after the timeout we proceed to kill,
            // and the abandoned write observes its exception benignly via the narrowed catch.
            Task timeout = Task.Delay(TimeSpan.FromSeconds(2));
            await Task.WhenAny(_stdinWriteTask, timeout).ConfigureAwait(false);

            await KillTreeAsync().ConfigureAwait(false);
            _process.Dispose();
        }
    }

    // ---- Windows turn (compile-only / Deferred): Job-Object kill + taskkill ----

    [SupportedOSPlatform("windows")]
    private sealed class WindowsAgentTurn : IAgentTurn
    {
        private readonly Process _process;
        private readonly nint _jobHandle;
        // Atomic kill guard: Interlocked.Exchange prevents double-CloseHandle on _jobHandle
        // (a double-close on a Win32 handle is undefined behavior / may close an aliased handle).
        private int _killedFlag;

        public WindowsAgentTurn(Process process, nint jobHandle)
        {
            _process = process;
            _jobHandle = jobHandle;
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            // stderr is NOT redirected (MUST-FIX 7) — it inherits the parent's handle.
            StreamReader stdout = _process.StandardOutput;
            while (true)
            {
                string? line;
                try
                {
                    line = await stdout.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (line is null)
                    yield break;
                if (line.Length == 0)
                    continue;

                yield return new AgentStreamEvent(line);
            }
        }

        public Task KillTreeAsync()
        {
            // SOLE fail-closed enforcer (§4.4), Windows leg — compile-confirmed, runtime
            // Deferred (SPIKE-4 Decision-2). PRIMARY: TerminateJobObject reaps every
            // process in the Job (the .cmd shim + node worker + grandchildren).
            // FALLBACK: taskkill /T /F /PID (for the breakaway-from-Job case, TD-5).
            //
            // Atomic guard: only the first caller kills; subsequent calls are no-ops
            // (prevents double-CloseHandle on _jobHandle — UB / aliased-handle risk).
            if (System.Threading.Interlocked.Exchange(ref _killedFlag, 1) != 0)
                return Task.CompletedTask;

            bool jobKilled = false;
            if (_jobHandle != nint.Zero)
            {
                jobKilled = WindowsAgentNative.TerminateJobObject(_jobHandle, exitCode: 1);
                WindowsAgentNative.CloseHandle(_jobHandle);
            }

            if (!jobKilled)
                TaskkillFallback(SafePid(_process));

            // Backstop fires UNCONDITIONALLY (even when HasExited) — the tree may have
            // outlived the leader (breakaway-from-Job processes, TD-5 edge).
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await KillTreeAsync().ConfigureAwait(false);
            _process.Dispose();
        }

        private static void TaskkillFallback(int pid)
        {
            if (pid <= 0)
                return;
            try
            {
                using Process? p = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p?.WaitForExit(5_000);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>The child's PID, or 0 if it has already exited (reading Id then throws).</summary>
    private static int SafePid(Process process)
    {
        try { return process.HasExited ? 0 : process.Id; }
        catch { return 0; }
    }

    /// <summary>
    /// Poll <c>getpgid(pid)</c> until it equals <c>pid</c> (the child has called <c>setsid()</c>)
    /// or the timeout elapses. setsid(1) exec-replaces itself with the target program; the setsid()
    /// syscall happens inside the child between fork and exec, so there is a brief window where
    /// getpgid still reflects the parent's group. The poll window is negligible in practice
    /// (&lt;1 ms for a fast fork/exec) but the hard timeout makes the wait bounded.
    /// </summary>
    private static int WaitForProcessGroupLeadership(int pid, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int pgid = PosixProcess.getpgid(pid);
            if (pgid == pid || pgid < 0)   // pgid < 0: process already gone — exit early.
                return pgid;
            if (Environment.TickCount64 > deadline)
                return pgid;   // return whatever we got; caller checks == pid.
            Thread.Sleep(1);   // 1 ms spin — the transition is typically sub-millisecond.
        }
    }
}

// ---- POSIX P/Invoke (Linux process-group kill) ----

/// <summary>
/// libc <c>kill(2)</c> / <c>getpgid(2)</c> for the Linux whole-tree SIGKILL. Same P/Invoke
/// posture as <see cref="AtomicFileStore"/> / <see cref="PathResolver"/> (SetLastError; runs
/// only on the Linux path). <c>kill(-pgid, SIGKILL)</c> targets a whole process group.
/// </summary>
internal static class PosixProcess
{
    internal const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true)]
    internal static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    internal static extern int getpgid(int pid);
}

// ---- Windows Job-Object P/Invoke — COMPILE-ONLY on this box; runtime DEFERRED ----
//
// Port of spikes/spike-4/kill/WindowsJobObject.cs. All Win32 P/Invoke is behind
// [SupportedOSPlatform("windows")] + OperatingSystem.IsWindows(), so the file compiles
// for both linux-x64 and win-x64 with no conditional compilation and NEVER runs on Linux.
// The runtime no-orphan-after-kill assertion is Deferred to Windows hardware (SPIKE-4).

[SupportedOSPlatform("windows")]
internal static class WindowsAgentNative
{
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    internal enum JobObjectInformationClass : uint
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const uint CREATE_SUSPENDED = 0x00000004;

    // QA2-3 stdin-pipe constants (runtime-Deferred Windows leg).
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out nint hReadPipe, out nint hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(nint hFile, ReadOnlySpan<byte> lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(
        nint hJob,
        JobObjectInformationClass jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        int cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateJobObject(nint hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(nint hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint hThread);

    /// <summary>
    /// Quote one argv token for a Win32 command line per the CommandLineToArgvW rules
    /// (escape embedded quotes and the backslash run preceding a quote). The host passes
    /// launchArgs through unchanged at the argv level; this only re-encodes them into the
    /// single CreateProcessW command-line string Windows requires.
    /// </summary>
    internal static string BuildCommandLine(string executable, IReadOnlyList<string> args)
    {
        var sb = new System.Text.StringBuilder();
        AppendQuoted(sb, executable);
        foreach (string arg in args)
        {
            sb.Append(' ');
            AppendQuoted(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(System.Text.StringBuilder sb, string token)
    {
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in token)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                backslashes = 0;
            }
            else if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    internal static nint CreateSuspendedProcess(
        string commandLine, string? workingDirectory, out int childPid, out nint stdinWriteHandle)
    {
        // QA2-3 (runtime-Deferred): create an anonymous stdin pipe whose READ end the child inherits
        // (STARTUPINFOW.hStdInput) and whose WRITE end the parent keeps to deliver request.Prompt.
        // We must handle-inherit for stdin to reach the child, so we pass the parent's own stdout/stderr
        // handles through STARTF_USESTDHANDLES to preserve the existing "stderr/stdout inherit" behavior
        // (MUST-FIX 7) while adding a redirected stdin.
        stdinWriteHandle = nint.Zero;
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = nint.Zero,
            bInheritHandle = true,   // the pipe's read end must be inheritable by the child.
        };
        if (!CreatePipe(out nint stdinReadHandle, out nint stdinWrite, ref sa, 0))
        {
            childPid = 0;
            return nint.Zero;
        }
        // The WRITE end must NOT be inherited by the child (only the parent writes to it), or the child
        // would keep it open and never see EOF.
        SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0);

        var si = new STARTUPINFOW
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFOW>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdInput = stdinReadHandle,
            hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE),   // preserve inherited stdout/stderr (MUST-FIX 7).
            hStdError = GetStdHandle(STD_ERROR_HANDLE),
        };
        bool ok = CreateProcessW(
            null,
            commandLine,
            nint.Zero,
            nint.Zero,
            bInheritHandles: true,   // required so the child inherits the stdin read handle.
            CREATE_SUSPENDED,
            nint.Zero,
            workingDirectory,
            ref si,
            out PROCESS_INFORMATION pi);

        // The parent no longer needs the read end (the child owns its copy).
        CloseHandle(stdinReadHandle);

        if (!ok)
        {
            CloseHandle(stdinWrite);
            childPid = 0;
            return nint.Zero;
        }

        childPid = pi.dwProcessId;
        stdinWriteHandle = stdinWrite;
        CloseHandle(pi.hThread);
        return pi.hProcess;
    }

    /// <summary>
    /// QA2-3 (runtime-Deferred): write <paramref name="prompt"/> to the child's stdin pipe as UTF-8 with
    /// NO byte-order mark, then CLOSE the write handle to send EOF (mandatory — <c>claude -p</c> blocks
    /// until stdin reaches EOF). Structural parity with the Linux <c>WritePromptToStdinAsync</c>.
    /// </summary>
    internal static void WritePromptAndCloseStdin(nint stdinWriteHandle, string prompt)
    {
        if (stdinWriteHandle == nint.Zero)
            return;
        try
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(prompt);
            int offset = 0;
            while (offset < bytes.Length)
            {
                bool ok = WriteFile(stdinWriteHandle, bytes.AsSpan(offset), (uint)(bytes.Length - offset), out uint written, nint.Zero);
                if (!ok || written == 0)
                    break;   // best-effort: broken pipe if the child already exited.
                offset += (int)written;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Expected benign race ONLY: the child already closed its stdin (broken pipe) or the handle
            // was torn down concurrently. The turn ends via the stdout EOF path. Any other exception
            // propagates. (WriteFile's bool return already handles the broken-pipe case via `break`.)
        }
        finally
        {
            CloseHandle(stdinWriteHandle);   // EOF: mandatory or `claude -p` waits forever.
        }
    }

    internal static void ResumeMainThread(int pid)
    {
        nint snap = Toolhelp32.CreateToolhelp32Snapshot(Toolhelp32.TH32CS_SNAPTHREAD, (uint)pid);
        if (snap == new nint(-1))
            return;

        try
        {
            var entry = new Toolhelp32.THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<Toolhelp32.THREADENTRY32>() };
            if (!Toolhelp32.Thread32First(snap, ref entry))
                return;

            do
            {
                if (entry.th32OwnerProcessID == (uint)pid)
                {
                    nint threadHandle = Toolhelp32.OpenThread(
                        Toolhelp32.THREAD_SUSPEND_RESUME, false, entry.th32ThreadID);
                    if (threadHandle != nint.Zero)
                    {
                        _ = ResumeThread(threadHandle);
                        CloseHandle(threadHandle);
                        return;   // resume only the primary thread.
                    }
                }
            } while (Toolhelp32.Thread32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class Toolhelp32
{
    internal const uint TH32CS_SNAPTHREAD = 0x00000004;
    internal const uint THREAD_SUSPEND_RESUME = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool Thread32First(nint hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool Thread32Next(nint hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
}
