// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// HOST [host]. Materializes the deny-by-default PreToolUse hook the CLI invokes, wires its
/// --settings injection, and bridges its stdin event JSON to the domain's pure decision logic.
public interface IToolPermissionHookHost
{
    /// Produce the --settings JSON wiring the production hook as the PreToolUse '*'-matcher hook
    /// with a no/long timeout (>= the FR-39 no-reply timer + margin; default 600s) so the hook's
    /// own timeout never wins the race (§4.4 fail-open). Probe the Python floor (TD-1: 3.12+ on
    /// Windows, or GetLongPathName hardening); pick the interpreter (python / py launcher).
    HookSettings BuildHookSettings(HookConfig config);

    /// Run the fail-closed self-test (FR-24 §3) before write agency is enabled: launch claude -p
    /// in a scratch dir with the production hook, prompt a Write/Edit the hook is configured to DENY,
    /// pass only if (a) no file appears AND (b) permission_denials is populated. Returns the result.
    Task<SelfTestResult> RunFailClosedSelfTestAsync(LaunchDescriptor launcher, CancellationToken ct);

    /// The M11 extension to the self-test gate (escalation-ai-live-write-path.md §5 invariant 5,
    /// m11-channel-protocol.md §4): before write agency is armed for a LIVE approve-each channel,
    /// prove that a turn whose observation channel is KILLED mid-ask DENIES and leaves NO file. Pass
    /// only if (a) no file appears AND (b) the denial was recorded — fail closed otherwise. The App
    /// runs this ALONGSIDE RunFailClosedSelfTestAsync when arming the approve-each live path. The impl
    /// already exists and is gated; this is an additive exposure of an already-built method, not new logic.
    Task<SelfTestResult> RunChannelKillSelfTestAsync(LaunchDescriptor launcher, CancellationToken ct);
}
