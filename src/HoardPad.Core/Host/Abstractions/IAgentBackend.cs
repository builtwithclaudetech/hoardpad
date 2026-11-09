// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// HOST [host]. Launches the claude -p subprocess, streams stream-json, and kills the WHOLE
/// process tree on cancel (NFR-8). The launch contract (flags) is §4.1; the kill design is below.
public interface IAgentBackend
{
    /// Resolve the launcher per the FR-24 shim order: claude.cmd -> claude.exe -> configured path
    /// (SPIKE-4 ShimResolver). UseShellExecute = false (required for Job-Object assignment).
    LaunchDescriptor ResolveLauncher(string? configuredPath);

    /// Launch a single turn. On Windows: CREATE_SUSPENDED -> AssignProcessToJobObject (while
    /// suspended) -> ResumeThread (the suspend-before-assign ordering closes the race where the
    /// .cmd shim spawns node before the Job assignment). On Linux: setsid() so the child leads
    /// its own process group. WorkingDirectory = resolved open-folder root (NO --cwd flag, §4.1).
    ///
    /// <paramref name="env"/> is the ADDITIONAL process environment the host applies on top of the
    /// inherited environment (M11 launch-env seam, docs/escalation-m11-launch-env-seam.md Option A):
    /// the hook reads HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE (the fail-closed prerequisite)
    /// and HOARDPAD_OBSERVE_CHANNEL / HOARDPAD_OBSERVE_MODE (the live observation leg) from the
    /// spawned process env. An EMPTY map is valid (a turn with no live channel still needs the
    /// sandbox/cap env); the guarded <see cref="AgentTurnRequest"/> ctor + LaunchArgsSafety are
    /// UNTOUCHED — the env never becomes a launch arg (invariant 7 preserved).
    Task<IAgentTurn> StartTurnAsync(
        LaunchDescriptor launcher, AgentTurnRequest request, IReadOnlyDictionary<string, string> env, CancellationToken ct);
}

/// One in-flight turn. Streams parsed stream-json events; KillTree() terminates the whole tree.
public interface IAgentTurn : IAsyncDisposable
{
    IAsyncEnumerable<AgentStreamEvent> StreamAsync(CancellationToken ct);

    /// Cancel by killing the ENTIRE process tree (the agent may spawn children).
    /// Windows PRIMARY: TerminateJobObject + CloseHandle (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE).
    /// Windows FALLBACK: taskkill /T /F /PID <pid> (breakaway is version-dependent - TD-5).
    /// Linux: kill(-pgid, SIGKILL) on the setsid process group.
    /// This is the SOLE fail-closed enforcer (§4.4) - it must fire before any hook timeout.
    Task KillTreeAsync();
}

public sealed record LaunchDescriptor(string ExecutablePath, bool IsCmd, bool FromPath, string ResolvedVia);
