using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.Provider;

/// <summary>
/// The Archetype-A PreToolUse-hook permission posture an <see cref="AgencyMode"/> maps to (TD §4.3,
/// architecture §4.2 step 2). This is the <em>posture the hook config is built for</em>, not the per-op
/// allow/deny decision (that is the cap/sandbox logic the hook calls).
/// </summary>
public enum PermissionPosture
{
    /// <summary>
    /// <c>read-only</c> → the matcher offers NO write tools; the agent may Read and chat only (TD §4.3).
    /// This is also the FORCED posture whenever the fail-closed self-test has not passed (FR-24 §3, TD §4.4):
    /// write agency is never armed without a passing self-test.
    /// </summary>
    NoWriteTools,

    /// <summary>
    /// <c>auto-apply</c> → <c>acceptEdits</c> permission-mode. The hook STILL gates each write against the
    /// sandbox and the FR-11/FR-38/FR-40 caps (acceptEdits does not bypass the hook — TD §4.3).
    /// </summary>
    AcceptEdits,

    /// <summary>
    /// <c>approve-each</c> → the hook blocks synchronously while HoardPad shows its confirm/reject dialog, then
    /// returns nested-JSON <c>permissionDecision: allow|deny</c> (TD §4.3, SPIKE-3 C1).
    /// </summary>
    SynchronousBlock,
}

/// <summary>
/// The resolved write-agency posture for one turn: the <see cref="Posture"/> the hook config is built for and
/// whether write tools are armed. <see cref="WriteAgencyArmed"/> is the single fact the rest of the
/// orchestration reads to decide whether a write tool may be offered at all.
/// </summary>
/// <param name="Posture">The hook permission posture (TD §4.3).</param>
/// <param name="WriteAgencyArmed">
/// <c>true</c> only when the configured mode allows writes AND the fail-closed self-test passed (TD §4.4 / FR-24 §3).
/// </param>
/// <param name="GateDenied">
/// <c>true</c> when a write-capable configured mode was DOWNGRADED to <see cref="PermissionPosture.NoWriteTools"/>
/// because the self-test did not pass — surfaced so the UI can show "sandbox could not be armed" (FR-24 Done-when).
/// </param>
public sealed record AgencyResolution(PermissionPosture Posture, bool WriteAgencyArmed, bool GateDenied);

/// <summary>
/// The pure agency→permission mapping (TD §4.3) and the self-test-gate-before-write-agency rule (TD §4.4, FR-24
/// §3). Both are platform-free decisions over the configured <see cref="AgencyMode"/> and the host-run
/// <see cref="SelfTestResult"/> — no I/O, no clock. The host runs
/// <see cref="IToolPermissionHookHost.RunFailClosedSelfTestAsync"/>; THIS logic decides what its result permits.
/// </summary>
/// <remarks>
/// The gate is fail-closed by construction (FR-24 §3, TD §4.4): a <c>read-only</c> turn needs no self-test, but
/// any write-capable mode (<c>auto-apply</c> / <c>approve-each</c>) yields write agency ONLY when the self-test
/// <see cref="SelfTestResult.Passed"/> — because <c>claude -p</c> silently ignores an invalid hook config
/// (SPIKE-3 D3), a version/auth/PATH check alone cannot prove the sandbox armed. A failed or absent self-test
/// degrades the turn to read-only rather than running with a possibly-disarmed sandbox.
/// </remarks>
public static class AgencyMapping
{
    /// <summary>The hook posture a configured <paramref name="mode"/> maps to, IGNORING the self-test gate (TD §4.3).</summary>
    /// <remarks>Use <see cref="Resolve"/> to apply the self-test gate; this is the bare mapping.</remarks>
    public static PermissionPosture PostureFor(AgencyMode mode) => mode switch
    {
        AgencyMode.ReadOnly => PermissionPosture.NoWriteTools,
        AgencyMode.AutoApply => PermissionPosture.AcceptEdits,
        AgencyMode.ApproveEach => PermissionPosture.SynchronousBlock,
        _ => PermissionPosture.NoWriteTools, // defensive: an unknown mode arms no writes (fail-closed).
    };

    /// <summary>True when <paramref name="mode"/> would offer write tools (everything except <c>read-only</c>).</summary>
    public static bool IsWriteCapable(AgencyMode mode) => mode != AgencyMode.ReadOnly;

    /// <summary>
    /// Resolve the effective write-agency posture for a turn, applying the fail-closed self-test gate (FR-24 §3,
    /// TD §4.4): a write-capable mode arms write tools ONLY when the self-test passed; otherwise it degrades to
    /// <see cref="PermissionPosture.NoWriteTools"/>. A <c>read-only</c> mode needs no self-test.
    /// </summary>
    /// <param name="configuredMode">The user-configured agency mode (FR-27).</param>
    /// <param name="selfTest">
    /// The host's fail-closed self-test result, or <c>null</c> when the self-test has not (yet) been run —
    /// treated identically to a FAILED self-test (absent proof of arming ⇒ no write agency, TD §4.4).
    /// </param>
    public static AgencyResolution Resolve(AgencyMode configuredMode, SelfTestResult? selfTest)
    {
        if (!IsWriteCapable(configuredMode))
            return new AgencyResolution(PermissionPosture.NoWriteTools, WriteAgencyArmed: false, GateDenied: false);

        // Write-capable: the self-test is a HARD gate. null (not yet run) is treated as not-passed (fail-closed).
        var passed = selfTest is { Passed: true };
        return passed
            ? new AgencyResolution(PostureFor(configuredMode), WriteAgencyArmed: true, GateDenied: false)
            : new AgencyResolution(PermissionPosture.NoWriteTools, WriteAgencyArmed: false, GateDenied: true);
    }
}
