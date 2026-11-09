// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
using System;
using System.Collections.Immutable;

namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// The single, authoritative source-of-truth for the FR-24 §4.1 safety invariants on a launch argument list.
/// Enforced at the <see cref="AgentTurnRequest"/> construction site (the type cannot hold a disarm token) and
/// reused by <c>HoardPad.Core.Ai.Provider.ClaudeLaunchContract</c> so there is exactly one place that defines what
/// constitutes an unsafe arg list (Core.Ai → Core dependency direction is preserved).
/// </summary>
/// <remarks>
/// <para>
/// Three tokens are unconditionally forbidden (TD §4.1):
/// <list type="bullet">
///   <item><c>--cwd</c> — <c>claude -p</c> sets the working directory via the host's <c>ProcessStartInfo.WorkingDirectory</c>;
///   passing <c>--cwd</c> in the arg list is a footgun and is never required (SPIKE-3 §A2).</item>
///   <item><c>--dangerously-skip-permissions</c> — disarms the permission hook layer entirely.</item>
///   <item>Any arg whose VALUE <c>contains</c> the string <c>bypassPermissions</c> — this token can be smuggled inside
///   a <c>--permission-mode</c> or <c>--settings</c> value (equality alone is insufficient; Contains is intentional).</item>
/// </list>
/// The first two are tested with <c>Equals</c> (exact flag tokens); the third uses <c>Contains</c> — the asymmetry is
/// correct.
/// </para>
/// <para>
/// Pure + platform-free: BCL string logic only — no I/O, no clock, no P/Invoke. The BannedApiAnalyzer passes on this file.
/// </para>
/// </remarks>
public static class LaunchArgsSafety
{
    // The forbidden tokens — duplicated as constants here so Core has no dependency on Core.Ai.
    private const string ForbiddenCwdFlag = "--cwd";
    private const string ForbiddenSkipFlag = "--dangerously-skip-permissions";
    private const string ForbiddenBypassMode = "bypassPermissions";

    /// <summary>
    /// Returns <c>true</c> iff <paramref name="args"/> honors both non-negotiable safety invariants:
    /// no <c>--cwd</c> flag, and no <c>bypassPermissions</c> / <c>--dangerously-skip-permissions</c> token (TD §4.1).
    /// An empty or default <see cref="ImmutableArray{T}"/> returns <c>false</c> — a zero-arg list cannot represent
    /// a valid FR-24 §4.1 contract.
    /// </summary>
    public static bool HonorsSafetyInvariants(ImmutableArray<string> args)
    {
        if (args.IsDefaultOrEmpty)
            return false;

        foreach (var a in args)
        {
            if (a.Equals(ForbiddenCwdFlag, StringComparison.Ordinal) ||
                a.Equals(ForbiddenSkipFlag, StringComparison.Ordinal) ||
                a.Contains(ForbiddenBypassMode, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <paramref name="args"/> violates a safety invariant or is
    /// empty/default. Called by <see cref="AgentTurnRequest"/>'s compact constructor so a disarm token cannot be
    /// held in the type.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="args"/> is default/empty or contains <c>--cwd</c>, <c>--dangerously-skip-permissions</c>, or
    /// any arg value containing <c>bypassPermissions</c>.
    /// </exception>
    public static void AssertSafe(ImmutableArray<string> args)
    {
        if (!HonorsSafetyInvariants(args))
            throw new ArgumentException(
                "LaunchArgs violates an FR-24 §4.1 safety invariant: the arg list must be non-empty and must not " +
                "contain --cwd, --dangerously-skip-permissions, or any arg value containing bypassPermissions (TD §4.1). " +
                "Build the arg list via HoardPad.Core.Ai.Provider.ClaudeLaunchContract.Compose to guarantee compliance.",
                nameof(args));
    }
}
