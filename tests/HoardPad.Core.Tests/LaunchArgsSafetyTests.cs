using System.Collections.Immutable;
using HoardPad.Core.Host.Abstractions;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M4.2 Gate-1 — the safety-by-construction seam: <see cref="LaunchArgsSafety"/> (the single Core source of truth
/// for FR-24 §4.1 invariants) and the <see cref="AgentTurnRequest"/> validating compact constructor (the type cannot
/// hold a disarm token). Placed in HoardPad.Core.Tests so it tests at the Core seam — independent of the Core.Ai
/// provider that calls <c>LaunchArgsSafety</c> via <c>ClaudeLaunchContract</c>.
/// </summary>
public sealed class LaunchArgsSafetyTests
{
    // A minimal valid arg list — does not have to be the full FR-24 §4.1 contract; just safe and non-empty.
    private static readonly ImmutableArray<string> SafeArgs =
        ImmutableArray.Create("-p", "--output-format", "stream-json");

    // ---- LaunchArgsSafety.HonorsSafetyInvariants ---------------------------------------------------------

    [Fact]
    public void HonorsSafetyInvariantsReturnsTrueForSafeArgs()
    {
        Assert.True(LaunchArgsSafety.HonorsSafetyInvariants(SafeArgs));
    }

    [Fact]
    public void HonorsSafetyInvariantsReturnsFalseForDefaultImmutableArray()
    {
        // A default (uninitialized) ImmutableArray<string> is treated as a violation — not a valid arg list.
        Assert.False(LaunchArgsSafety.HonorsSafetyInvariants(default));
    }

    [Fact]
    public void HonorsSafetyInvariantsReturnsFalseForEmptyArray()
    {
        Assert.False(LaunchArgsSafety.HonorsSafetyInvariants(ImmutableArray<string>.Empty));
    }

    [Fact]
    public void HonorsSafetyInvariantsReturnsFalseWhenCwdFlagPresent()
    {
        var args = ImmutableArray.Create("-p", "--cwd", "/some/root");
        Assert.False(LaunchArgsSafety.HonorsSafetyInvariants(args));
    }

    [Fact]
    public void HonorsSafetyInvariantsReturnsFalseWhenSkipPermissionsFlagPresent()
    {
        var args = ImmutableArray.Create("-p", "--dangerously-skip-permissions");
        Assert.False(LaunchArgsSafety.HonorsSafetyInvariants(args));
    }

    [Fact]
    public void HonorsSafetyInvariantsReturnsFalseWhenBypassPermissionsContainedInAnyArgValue()
    {
        // The bypass token can be smuggled inside a --permission-mode or --settings value — Contains check is correct.
        var args = ImmutableArray.Create("-p", "--permission-mode", "bypassPermissions");
        Assert.False(LaunchArgsSafety.HonorsSafetyInvariants(args));
    }

    // ---- AgentTurnRequest validating constructor — Gate-1 safety by construction -------------------------

    [Fact]
    public void AgentTurnRequestConstructionSucceedsWithSafeArgs()
    {
        // Should not throw.
        var req = new AgentTurnRequest("hello", default(NormcasedPath), SafeArgs);
        Assert.Equal(SafeArgs, req.LaunchArgs);
    }

    [Fact]
    public void AgentTurnRequestConstructionThrowsForDefaultLaunchArgs()
    {
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("hello", default(NormcasedPath), default(ImmutableArray<string>)));
    }

    [Fact]
    public void AgentTurnRequestConstructionThrowsForEmptyLaunchArgs()
    {
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("hello", default(NormcasedPath), ImmutableArray<string>.Empty));
    }

    [Fact]
    public void AgentTurnRequestConstructionThrowsWhenCwdFlagPresent()
    {
        var badArgs = ImmutableArray.Create("-p", "--cwd", "/bad");
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("hello", default(NormcasedPath), badArgs));
    }

    [Fact]
    public void AgentTurnRequestConstructionThrowsWhenSkipPermissionsFlagPresent()
    {
        var badArgs = ImmutableArray.Create("-p", "--dangerously-skip-permissions");
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("hello", default(NormcasedPath), badArgs));
    }

    [Fact]
    public void AgentTurnRequestConstructionThrowsWhenBypassPermissionsTokenPresent()
    {
        var badArgs = ImmutableArray.Create("-p", "--permission-mode", "bypassPermissions");
        Assert.Throws<ArgumentException>(
            () => new AgentTurnRequest("hello", default(NormcasedPath), badArgs));
    }
}
