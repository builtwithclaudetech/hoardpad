using HoardPad.Core.Host.Abstractions;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M1.1 regression tests for <see cref="NormcasedPath"/>.
/// Guards the sole-producer invariant and the fail-fast default-value behaviour that prevents
/// a zero-initialized struct from silently mis-evaluating the FR-26 sandbox prefix check (M1.2).
/// </summary>
public sealed class NormcasedPathTests
{
    // ── default / zero-value guard ────────────────────────────────────────────

    [Fact]
    public void DefaultIsDefaultIsTrue()
    {
        NormcasedPath sut = default;
        Assert.True(sut.IsDefault);
    }

    [Fact]
    public void NewIsDefaultIsTrue()
    {
        var sut = new NormcasedPath();
        Assert.True(sut.IsDefault);
    }

    [Fact]
    public void DefaultValueThrowsInvalidOperationExceptionNotNullReferenceException()
    {
        NormcasedPath sut = default;

        // Must throw InvalidOperationException (controlled fail-fast), NOT NullReferenceException
        // (uncontrolled crash). SandboxPolicy (M1.2) does path.Value.StartsWith(…); a silent null
        // would produce a mis-evaluation rather than a clear contract violation.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _ = sut.Value);
        Assert.Contains("default(NormcasedPath)", ex.Message);
    }

    [Fact]
    public void DefaultToStringDoesNotThrowReturnsDefaultSentinel()
    {
        NormcasedPath sut = default;
        string result = sut.ToString();  // must not NRE
        Assert.Equal("(default)", result);
    }

    // ── FromResolved (sole producer) ──────────────────────────────────────────

    [Fact]
    public void FromResolvedProducedValueIsNotDefault()
    {
        NormcasedPath sut = NormcasedPath.FromResolved("/opt/project");
        Assert.False(sut.IsDefault);
    }

    [Fact]
    public void FromResolvedValueRoundTrips()
    {
        const string resolved = "/opt/project/file.hoard";
        NormcasedPath sut = NormcasedPath.FromResolved(resolved);
        Assert.Equal(resolved, sut.Value);
    }

    [Fact]
    public void FromResolvedToStringReturnsValue()
    {
        const string resolved = "/opt/project";
        NormcasedPath sut = NormcasedPath.FromResolved(resolved);
        Assert.Equal(resolved, sut.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromResolvedNullOrEmptyThrowsArgumentException(string? bad)
    {
        Assert.Throws<ArgumentException>(() => NormcasedPath.FromResolved(bad!));
    }
}
