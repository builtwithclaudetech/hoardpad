using System.Collections.Immutable;
using HoardPad.Core.Ai.Provider;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.2 — the FR-24 §4.1 <c>claude -p</c> launch flag contract (TD §4.1, architecture §4.2 step 1): the verbatim
/// flag set and the two non-negotiable safety invariants (NEVER <c>bypassPermissions</c> /
/// <c>--dangerously-skip-permissions</c>; NO <c>--cwd</c>). Made testable here so a regression that adds either
/// fails at the composition seam, not silently at runtime. Pure — string composition only.
/// </summary>
public sealed class ArchetypeALaunchContractTests
{
    private static readonly string[] ExpectedFlagSet =
    {
        "-p",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--max-budget-usd", "2.5",
        "--settings", "/abs/hook-settings.json",
        "--tools", "Write,Read,Edit",
        "--strict-mcp-config",
    };

    // ImmutableArray<string> — matches the HonorsSafetyInvariants signature (Gate-2 W1).
    private static readonly ImmutableArray<string> CwdBad =
        ImmutableArray.Create("-p", "--cwd", "/some/root");
    private static readonly ImmutableArray<string> SkipBad =
        ImmutableArray.Create("-p", "--dangerously-skip-permissions");
    private static readonly ImmutableArray<string> BypassBad =
        ImmutableArray.Create("-p", "--permission-mode", "bypassPermissions");

    [Fact]
    public void ComposeProducesTheVerbatimFR24FlagSetInOrder()
    {
        var args = ClaudeLaunchContract.Compose(maxBudgetUsd: 2.5m, settingsJsonPath: "/abs/hook-settings.json");

        Assert.Equal(ExpectedFlagSet, args);
    }

    [Fact]
    public void ComposeOffersExactlyTheThreeBuiltInToolsNotAllowedTools()
    {
        var args = ClaudeLaunchContract.Compose(maxBudgetUsd: 1m, settingsJsonPath: "/s.json");

        // --tools constrains the OFFERED roster (SPIKE-3 §A16); --allowedTools is a permission list and must NOT
        // be used as the roster constraint.
        Assert.Contains("--tools", args);
        Assert.DoesNotContain("--allowedTools", args);
        Assert.Equal("Write,Read,Edit", ClaudeLaunchContract.OfferedToolRoster);
    }

    [Fact]
    public void ComposeNeverEmitsBypassPermissionsOrSkipFlag()
    {
        var args = ClaudeLaunchContract.Compose(maxBudgetUsd: 1m, settingsJsonPath: "/s.json");

        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.DoesNotContain(args, a => a.Contains("bypassPermissions", StringComparison.Ordinal));
        Assert.True(ClaudeLaunchContract.HonorsSafetyInvariants(args));
    }

    [Fact]
    public void ComposeNeverEmitsCwdFlag()
    {
        var args = ClaudeLaunchContract.Compose(maxBudgetUsd: 1m, settingsJsonPath: "/s.json");

        Assert.DoesNotContain("--cwd", args);
    }

    // ---- HonorsSafetyInvariants delegates to LaunchArgsSafety (single source of truth) ----------------------

    [Fact]
    public void HonorsSafetyInvariantsRejectsCwd()
    {
        Assert.False(ClaudeLaunchContract.HonorsSafetyInvariants(CwdBad));
    }

    [Fact]
    public void HonorsSafetyInvariantsRejectsSkipPermissionsFlag()
    {
        Assert.False(ClaudeLaunchContract.HonorsSafetyInvariants(SkipBad));
    }

    [Fact]
    public void HonorsSafetyInvariantsRejectsBypassPermissionsTokenAnywhereInAnArg()
    {
        // A regression that smuggles the bypass mode into a --settings/--permission-mode value must be caught too.
        Assert.False(ClaudeLaunchContract.HonorsSafetyInvariants(BypassBad));
    }

    [Fact]
    public void HonorsSafetyInvariantsAcceptsTheComposedContract()
    {
        var args = ClaudeLaunchContract.Compose(maxBudgetUsd: 1m, settingsJsonPath: "/s.json");
        Assert.True(ClaudeLaunchContract.HonorsSafetyInvariants(args));
    }

    // ---- Input guards ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ComposeRejectsNonPositiveBudget(int budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClaudeLaunchContract.Compose(budget, "/s.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ComposeRejectsEmptySettingsPath(string path)
    {
        Assert.Throws<ArgumentException>(() => ClaudeLaunchContract.Compose(1m, path));
    }

    // ---- BUG-5: model/effort injection -----------------------------------------------------------------------

    [Fact]
    public void ComposeWithModelIdAppendsModelFlag()
    {
        var args = ClaudeLaunchContract.Compose(1m, "/s.json", modelId: "claude-opus-4-8");

        int modelIdx = args.IndexOf("--model");
        Assert.True(modelIdx >= 0, "--model flag must be present");
        Assert.Equal("claude-opus-4-8", args[modelIdx + 1]);
        Assert.True(ClaudeLaunchContract.HonorsSafetyInvariants(args));
    }

    [Fact]
    public void ComposeWithEffortLevelAppendsEffortFlag()
    {
        var args = ClaudeLaunchContract.Compose(1m, "/s.json", effortLevel: "high");

        int effortIdx = args.IndexOf("--effort");
        Assert.True(effortIdx >= 0, "--effort flag must be present");
        Assert.Equal("high", args[effortIdx + 1]);
        Assert.True(ClaudeLaunchContract.HonorsSafetyInvariants(args));
    }

    [Fact]
    public void ComposeWithBothModelAndEffortAppendsBothAfterFixedContract()
    {
        var args = ClaudeLaunchContract.Compose(1m, "/s.json", modelId: "claude-sonnet-4-6", effortLevel: "xhigh");

        // Both flags must appear after the fixed contract (--strict-mcp-config).
        int strictIdx = args.IndexOf("--strict-mcp-config");
        int modelIdx = args.IndexOf("--model");
        int effortIdx = args.IndexOf("--effort");

        Assert.True(modelIdx > strictIdx, "--model must appear after --strict-mcp-config");
        Assert.True(effortIdx > strictIdx, "--effort must appear after --strict-mcp-config");
        Assert.Equal("claude-sonnet-4-6", args[modelIdx + 1]);
        Assert.Equal("xhigh", args[effortIdx + 1]);
        Assert.True(ClaudeLaunchContract.HonorsSafetyInvariants(args));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, "")]
    [InlineData(null, "  ")]
    public void ComposeWithNullOrWhitespaceModelOrEffortOmitsThoseFlags(string? modelId, string? effortLevel)
    {
        // Use the same budget+path as ExpectedFlagSet so we can do a full sequence equality check.
        var args = ClaudeLaunchContract.Compose(2.5m, "/abs/hook-settings.json", modelId: modelId, effortLevel: effortLevel);

        Assert.DoesNotContain("--model", args);
        Assert.DoesNotContain("--effort", args);
        // The base flag set is unchanged when no overrides are supplied.
        Assert.Equal(ExpectedFlagSet, args);
    }

    [Fact]
    public void ComposeWithModelAndEffortStillHonorsSafetyInvariants()
    {
        var args = ClaudeLaunchContract.Compose(2m, "/s.json", modelId: "claude-opus-4-8", effortLevel: "low");

        Assert.True(ClaudeLaunchContract.HonorsSafetyInvariants(args));
        Assert.DoesNotContain("--cwd", args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.DoesNotContain(args, a => a.Contains("bypassPermissions", StringComparison.Ordinal));
    }
}
