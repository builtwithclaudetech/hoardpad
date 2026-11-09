// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

// The IToolPermissionHookHost signatures in TD §2.4 reference HookConfig, HookSettings and
// SelfTestResult but do NOT define them (TD §2.4 specifies only the interface verbatim). They are
// introduced here as the MINIMAL shapes the seam needs to compile. The hook-deployment detail
// (§4.2 deny-by-default protocol, §4.3 agency mapping, the §3 self-test wiring) is owned by M5.2
// (Host ToolPermissionHookHost); that milestone fleshes these out.

/// <summary>
/// The M11 hook-observation mode (docs/m11-channel-protocol.md §1.1 <c>HOARDPAD_OBSERVE_MODE</c>):
/// <see cref="Report"/> = auto-apply / read-only (REPORT only, the hook never blocks for an ask);
/// <see cref="Approve"/> = approve-each (REPORT then BLOCK for the narrowing ask). When a turn has no
/// live channel the mode is irrelevant — the env var is absent and the hook's autonomous path runs.
/// </summary>
public enum HookObserveMode
{
    /// <summary>REPORT only — the hook's autonomous allow/deny IS final; no blocking ask (protocol §1.1).</summary>
    Report,

    /// <summary>Approve-each — REPORT then BLOCK reading the narrowing <c>decision</c> (protocol §1.1, §3).</summary>
    Approve,
}

/// Input to BuildHookSettings: the resolved open-folder root the hook sandboxes against and the
/// no-reply timeout (>= FR-39 no-reply timer + margin) the host must not undercut (§4.4 fail-open).
/// The M11 observation fields are OPTIONAL and ADDITIVE (docs/m11-channel-protocol.md §1.1): when
/// <see cref="ObserveChannelAddress"/> is null the hook's autonomous path runs UNCHANGED (no
/// HOARDPAD_OBSERVE_* env is emitted), so the existing 29 SC-5 gates are byte-identical.
public sealed record HookConfig(NormcasedPath OpenFolderRoot, TimeSpan NoReplyTimeout)
{
    /// <summary>
    /// The M11 per-turn observation channel address (the AF_UNIX socket path / named-pipe name) the
    /// hook connects to (protocol §1.1 <c>HOARDPAD_OBSERVE_CHANNEL</c>). <c>null</c> ⇒ no live channel:
    /// the hook runs its autonomous path UNCHANGED and no HOARDPAD_OBSERVE_* env is injected.
    /// </summary>
    public string? ObserveChannelAddress { get; init; }

    /// <summary>
    /// The M11 observation mode (protocol §1.1 <c>HOARDPAD_OBSERVE_MODE</c>). Only meaningful when
    /// <see cref="ObserveChannelAddress"/> is set; defaults to <see cref="HookObserveMode.Report"/>.
    /// </summary>
    public HookObserveMode ObserveMode { get; init; } = HookObserveMode.Report;
}

/// The --settings JSON payload wiring the production PreToolUse '*'-matcher hook for one turn, plus
/// the run-time ENVIRONMENT the host must set on the launched agent process (the mechanism by which
/// the hook reads HOARDPAD_OBSERVE_CHANNEL / HOARDPAD_OBSERVE_MODE — protocol §1.1). The env map is
/// EMPTY when the M11 channel is not in play (the autonomous path is then byte-identical).
public sealed record HookSettings(string SettingsJson)
{
    /// <summary>
    /// The additional environment variables the host must set on the launched agent process so the
    /// hook can read them at run time (the SAME injection mechanism as HOARDPAD_SANDBOX_ROOT /
    /// HOARDPAD_CAP_STATE_FILE). Empty unless the M11 observation channel is configured. The host
    /// applies these ON TOP of the sandbox/cap env it already sets; they never enter the
    /// <see cref="SettingsJson"/> (which carries only the hook registration).
    /// </summary>
    public IReadOnlyDictionary<string, string> HookEnvironment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// Outcome of the FR-24 §3 fail-closed self-test: pass only if no file appeared AND the deny was
/// recorded in permission_denials. Detail carries the failure reason when Passed is false.
public sealed record SelfTestResult(bool Passed, string? Detail);
