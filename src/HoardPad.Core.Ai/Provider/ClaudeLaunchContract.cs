using System.Collections.Immutable;
using System.Globalization;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Core.Ai.Provider;

/// <summary>
/// The FR-24 §4.1 <c>claude -p</c> launch flag contract (TD §4.1, architecture §4.2 step 1), composed verbatim
/// by the Archetype-A orchestration. This type OWNS the flag set; the actual OS process launch is a
/// <c>HoardPad.Host</c> concern (M5.2, <see cref="HoardPad.Core.Host.Abstractions.IAgentBackend.StartTurnAsync"/>).
/// The composed argument list is carried opaque to the host via <c>AgentTurnRequest.LaunchArgs</c>; the host
/// passes it through to the subprocess unchanged and sets <c>WorkingDirectory</c> to the resolved root (there is
/// NO <c>--cwd</c> flag for <c>-p</c> — SPIKE-3 §A2).
/// </summary>
/// <remarks>
/// <para>
/// The two load-bearing safety invariants are made <em>testable</em> here rather than left to the host: the
/// composed args NEVER contain <c>--cwd</c> and NEVER contain <c>bypassPermissions</c> /
/// <c>--dangerously-skip-permissions</c> (TD §4.1, architecture §4.2). <see cref="Compose"/> asserts both before
/// returning, so a build regression that tries to add either fails fast at the composition seam, not silently at
/// runtime where the sandbox would be disarmed.
/// </para>
/// <para>
/// Pure + platform-free: this is string composition over the BCL only — no process launch, no clock, no
/// P/Invoke. The min CLI version floor (2.1.179, TD §1.7) is a host-side readiness check (it probes the installed
/// CLI), not a flag — it is not composed here.
/// </para>
/// </remarks>
public static class ClaudeLaunchContract
{
    /// <summary>The FR-24 §4.1 offered-tool roster — exactly these three built-in tools (TD §4.1, SPIKE-3 §A16).</summary>
    public const string OfferedToolRoster = "Write,Read,Edit";

    /// <summary>The forbidden permission-mode token that disarms the sandbox — NEVER composed (TD §4.1).</summary>
    public const string ForbiddenBypassMode = "bypassPermissions";

    /// <summary>The forbidden skip-permissions flag — NEVER composed (TD §4.1).</summary>
    public const string ForbiddenSkipFlag = "--dangerously-skip-permissions";

    /// <summary>The absent (for <c>-p</c>) working-directory flag — NEVER composed; WorkingDirectory is the host's job (SPIKE-3 §A2).</summary>
    public const string ForbiddenCwdFlag = "--cwd";

    /// <summary>
    /// Compose the FR-24 §4.1 launch argument list, verbatim and in the spec order
    /// (TD §4.1; architecture §4.2 step 1):
    /// <c>-p --output-format stream-json --verbose --include-partial-messages --max-budget-usd &lt;ceiling&gt;
    /// --settings &lt;json&gt; --tools "Write,Read,Edit" --strict-mcp-config</c>. The prompt and the
    /// <c>WorkingDirectory</c> are NOT in this list — the prompt is the <c>AgentTurnRequest.Prompt</c> the host
    /// passes on stdin/argv, and the working root is set by the host (no <c>--cwd</c>).
    /// </summary>
    /// <param name="maxBudgetUsd">
    /// The FR-24 <c>--max-budget-usd</c> coarse cost ceiling (a runaway bound, not a precise per-op budget, TD §4.1).
    /// Must be positive.
    /// </param>
    /// <param name="settingsJsonPath">
    /// The absolute path to the generated <c>--settings</c> JSON wiring the production deny-by-default hook
    /// (<see cref="HoardPad.Core.Host.Abstractions.IToolPermissionHookHost.BuildHookSettings"/>, materialized by
    /// the host). Must be non-empty — HoardPad NEVER relies on ambient user settings (TD §4.1, FR-24 §3).
    /// </param>
    /// <param name="modelId">
    /// BUG-5: the AI model override (e.g. <c>claude-opus-4-8</c>); <c>null</c> or whitespace = use the backend
    /// default. When non-empty, appends <c>--model &lt;modelId&gt;</c> to the arg list.
    /// </param>
    /// <param name="effortLevel">
    /// BUG-5: the reasoning effort override (<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>); <c>null</c> or
    /// whitespace = use the backend default. When non-empty, appends <c>--effort &lt;effortLevel&gt;</c>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBudgetUsd"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="settingsJsonPath"/> is null/whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// A composed arg violates a safety invariant (<c>--cwd</c> or a bypass token present). This is an internal
    /// guard against a regression in this method — it cannot fire for valid inputs.
    /// </exception>
    public static ImmutableArray<string> Compose(
        decimal maxBudgetUsd,
        string settingsJsonPath,
        string? modelId = null,
        string? effortLevel = null)
    {
        if (maxBudgetUsd <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(maxBudgetUsd), maxBudgetUsd, "The FR-24 --max-budget-usd ceiling must be positive.");
        if (string.IsNullOrWhiteSpace(settingsJsonPath))
            throw new ArgumentException(
                "The FR-24 --settings JSON path must be a non-empty absolute path (HoardPad never relies on ambient settings, TD §4.1).",
                nameof(settingsJsonPath));

        var builder = ImmutableArray.CreateBuilder<string>();
        builder.Add("-p");
        builder.Add("--output-format"); builder.Add("stream-json");
        builder.Add("--verbose");                        // stream-json REQUIRES --verbose (SPIKE-3 §A2)
        builder.Add("--include-partial-messages");
        builder.Add("--max-budget-usd"); builder.Add(maxBudgetUsd.ToString(CultureInfo.InvariantCulture));
        builder.Add("--settings"); builder.Add(settingsJsonPath);
        builder.Add("--tools"); builder.Add(OfferedToolRoster); // constrains the OFFERED roster — NOT --allowedTools (SPIKE-3 §A16)
        builder.Add("--strict-mcp-config");              // strips ambient mcp__* from the roster (SPIKE-3 §B)

        // BUG-5: optional model/effort overrides — appended after the fixed contract so the core flag
        // order is stable and every safety invariant (no --cwd, no bypass token) is still satisfied.
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            builder.Add("--model"); builder.Add(modelId);
        }
        if (!string.IsNullOrWhiteSpace(effortLevel))
        {
            builder.Add("--effort"); builder.Add(effortLevel);
        }

        var args = builder.ToImmutable();
        AssertSafetyInvariants(args);
        return args;
    }

    /// <summary>
    /// True iff <paramref name="args"/> honors the two non-negotiable safety invariants: no <c>--cwd</c> flag and
    /// no <c>bypassPermissions</c> / <c>--dangerously-skip-permissions</c> token (TD §4.1). The MA14/self-test
    /// gate is separate; this is purely the launch-flag invariant the host must honor.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="LaunchArgsSafety.HonorsSafetyInvariants"/> — the single source of truth in
    /// HoardPad.Core. This keeps the predicate testable at the Core seam and ensures a future caller that
    /// constructs an <see cref="HoardPad.Core.Host.Abstractions.AgentTurnRequest"/> directly (bypassing
    /// <see cref="Compose"/>) is still covered by the same logic.
    /// </remarks>
    public static bool HonorsSafetyInvariants(ImmutableArray<string> args) =>
        LaunchArgsSafety.HonorsSafetyInvariants(args);

    private static void AssertSafetyInvariants(ImmutableArray<string> args)
    {
        if (!HonorsSafetyInvariants(args))
            throw new InvalidOperationException(
                "FR-24 launch-contract invariant violated: composed args must never include --cwd or a bypassPermissions/--dangerously-skip-permissions token (TD §4.1).");
    }
}
