using HoardPad.Core.Encoding;
using HoardPad.Core.Host.Abstractions;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M1.3 tests for the PURE FR-6 detect-then-preserve decision <see cref="EncodingDecision.Decide"/>
/// (architecture §1.2/§1.3, OQ-ARCH-4). The decision is a pure function over the
/// <see cref="IEncodingDetector"/> classification — these tests stand up no host and touch no charset
/// package. Threshold = <see cref="EncodingDecision.ConfidenceThreshold"/> (0.5, strictly-greater
/// adopts); default = <see cref="EncodingDecision.DefaultEncoding"/> (utf-8, FR-6 / A-8 cut-item (4)).
/// </summary>
public sealed class EncodingDecisionTests
{
    private static EncodingCandidate Candidate(string name, double confidence) => new(name, confidence);

    // ── High-confidence detection adopts the detected encoding ────────────────

    [Theory]
    [InlineData("utf-8", 0.99)]
    [InlineData("windows-1252", 0.80)]
    [InlineData("shift_jis", 0.51)] // just above threshold
    public void HighConfidenceAdoptsDetectedEncoding(string name, double confidence)
    {
        var result = EncodingDecision.Decide(Candidate(name, confidence));

        Assert.Equal(name, result.EffectiveEncoding);
        Assert.Equal(EncodingSource.Detected, result.Source);
    }

    // ── Low / at-threshold confidence falls back to the FR-6 default (utf-8) ───

    [Theory]
    [InlineData("windows-1252", 0.50)] // AT threshold → fallback (strictly-greater adopts)
    [InlineData("shift_jis", 0.30)]
    [InlineData("utf-8", 0.0)]
    public void LowOrAtThresholdConfidenceFallsBackToUtf8Default(string name, double confidence)
    {
        var result = EncodingDecision.Decide(Candidate(name, confidence));

        Assert.Equal(EncodingDecision.DefaultEncoding, result.EffectiveEncoding);
        Assert.Equal("utf-8", result.EffectiveEncoding);
        Assert.Equal(EncodingSource.DefaultFallback, result.Source);
    }

    [Fact]
    public void ConfidentButBlankNameFallsBackToDefault()
    {
        var result = EncodingDecision.Decide(Candidate("   ", 0.99));

        Assert.Equal(EncodingDecision.DefaultEncoding, result.EffectiveEncoding);
        Assert.Equal(EncodingSource.DefaultFallback, result.Source);
    }

    // ── Boundary: the exact threshold value (0.5) is NOT adopted ───────────────

    [Fact]
    public void ExactThresholdDoesNotAdoptDetected()
    {
        var result = EncodingDecision.Decide(Candidate("windows-1252", EncodingDecision.ConfidenceThreshold));

        Assert.Equal(EncodingSource.DefaultFallback, result.Source);
    }

    [Fact]
    public void JustAboveThresholdAdoptsDetected()
    {
        var result = EncodingDecision.Decide(Candidate("windows-1252", EncodingDecision.ConfidenceThreshold + 0.0001));

        Assert.Equal("windows-1252", result.EffectiveEncoding);
        Assert.Equal(EncodingSource.Detected, result.Source);
    }

    // ── Preserve-on-save: save encoding == effective encoding unless overridden ─

    [Fact]
    public void PreserveOnSaveDetectedEncodingIsPreserved()
    {
        var result = EncodingDecision.Decide(Candidate("windows-1252", 0.95));

        // FR-6: the file's original (detected) encoding is preserved on save.
        Assert.Equal(result.EffectiveEncoding, result.SaveEncoding);
        Assert.Equal("windows-1252", result.SaveEncoding);
    }

    [Fact]
    public void PreserveOnSaveDefaultFallbackIsPreserved()
    {
        var result = EncodingDecision.Decide(Candidate("windows-1252", 0.10));

        Assert.Equal("utf-8", result.SaveEncoding);
        Assert.Equal(result.EffectiveEncoding, result.SaveEncoding);
    }

    // ── User override wins over detection AND default, and drives save ─────────

    [Fact]
    public void UserOverrideWinsOverHighConfidenceDetection()
    {
        var result = EncodingDecision.Decide(Candidate("windows-1252", 0.99), userOverride: "utf-16");

        Assert.Equal("utf-16", result.EffectiveEncoding);
        Assert.Equal("utf-16", result.SaveEncoding); // user "changed it" → preserve the user's choice
        Assert.Equal(EncodingSource.UserOverride, result.Source);
    }

    [Fact]
    public void UserOverrideWinsOverLowConfidenceDefault()
    {
        var result = EncodingDecision.Decide(Candidate("shift_jis", 0.1), userOverride: "windows-1252");

        Assert.Equal("windows-1252", result.EffectiveEncoding);
        Assert.Equal("windows-1252", result.SaveEncoding);
        Assert.Equal(EncodingSource.UserOverride, result.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankUserOverrideIsTreatedAsNoOverride(string? blank)
    {
        var result = EncodingDecision.Decide(Candidate("utf-8", 0.99), userOverride: blank);

        Assert.Equal(EncodingSource.Detected, result.Source);
        Assert.Equal("utf-8", result.EffectiveEncoding);
    }
}
