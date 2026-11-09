using HoardPad.Core.Ai.Caps;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.1 — the FR-35 clamp validation (§8.2 / FR-35 Done-when): a user-configured cap value cannot be set to 0
/// or unbounded in a way that disables the injection backstop. Also pins the four MEASURED cap constants
/// (TD §1.7) so a future edit that silently changes a pinned value trips a test.
/// </summary>
public sealed class CapClampTests
{
    // ---- Pin the exact MEASURED cap numbers (TD §1.7 lines 102–105) -----------------------------------

    [Fact]
    public void PinnedCapValuesMatchSpike3MeasuredConstants()
    {
        // TD §1.7 / PRD FR-11/FR-38/FR-40/FR-35, build-context "Caps pinned: FR-11=25, FR-38=10/X%=80%, FR-40=20".
        // These are the SECURITY backstop constants — changing one is a deliberate re-measure, not a casual edit.
        Assert.Equal(25, CapConfig.PinnedCreateCap);
        Assert.Equal(10, CapConfig.PinnedDestructiveCap);
        Assert.Equal(20, CapConfig.PinnedFilesTouchedCap);
        Assert.Equal(80, CapConfig.PinnedDestructiveMagnitudePercent);

        Assert.Equal(25, CapConfig.Pinned.CreateCap);
        Assert.Equal(10, CapConfig.Pinned.DestructiveCap);
        Assert.Equal(20, CapConfig.Pinned.FilesTouchedCap);
        Assert.Equal(80, CapConfig.Pinned.DestructiveMagnitudePercent);
    }

    // ---- The pinned config is valid; reasonable overrides are accepted --------------------------------

    [Fact]
    public void PinnedConfigPassesTheClamp() =>
        Assert.True(CapClamp.IsValid(CapConfig.Pinned));

    [Theory]
    [InlineData(1, 1, 1, 1)]      // minimal but bounded — accepted
    [InlineData(50, 20, 40, 50)]  // larger-but-finite override — accepted
    [InlineData(25, 10, 20, 100)] // 100% magnitude — the upper end of the meaningful range
    public void BoundedPositiveOverridesAreAccepted(int create, int destructive, int files, int magnitude) =>
        Assert.True(CapClamp.IsValid(new CapConfig(create, destructive, files, magnitude)));

    // ---- Disabling values are rejected (FR-35) --------------------------------------------------------

    [Fact]
    public void ZeroCreateCapIsRejectedAsNonPositive()
    {
        var errors = CapClamp.Validate(CapConfig.Pinned with { CreateCap = 0 });
        Assert.Contains(errors, e => e.Field == CapField.CreateCap && e.Reason == CapClampReason.NonPositive);
    }

    [Fact]
    public void NegativeDestructiveCapIsRejectedAsNonPositive()
    {
        var errors = CapClamp.Validate(CapConfig.Pinned with { DestructiveCap = -1 });
        Assert.Contains(errors, e => e.Field == CapField.DestructiveCap && e.Reason == CapClampReason.NonPositive);
    }

    [Fact]
    public void UnboundedFilesTouchedCapIsRejectedAsUnbounded()
    {
        // int.MaxValue is the "unbounded" disabling value — a cap so high the backstop never fires.
        var errors = CapClamp.Validate(CapConfig.Pinned with { FilesTouchedCap = int.MaxValue });
        Assert.Contains(errors, e => e.Field == CapField.FilesTouchedCap && e.Reason == CapClampReason.Unbounded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void MagnitudeOutOfRangeIsRejected(int magnitude)
    {
        var errors = CapClamp.Validate(CapConfig.Pinned with { DestructiveMagnitudePercent = magnitude });
        Assert.Contains(errors, e => e.Field == CapField.DestructiveMagnitudePercent && e.Reason == CapClampReason.MagnitudeOutOfRange);
    }

    [Fact]
    public void MultipleDisablingValuesAreAllReported()
    {
        var errors = CapClamp.Validate(new CapConfig(CreateCap: 0, DestructiveCap: int.MaxValue, FilesTouchedCap: -1, DestructiveMagnitudePercent: 0));
        Assert.Equal(4, errors.Count);
    }
}
