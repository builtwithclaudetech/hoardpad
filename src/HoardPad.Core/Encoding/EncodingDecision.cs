using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Core.Encoding;

/// <summary>How the buffer's effective encoding was chosen (for audit + testability).</summary>
public enum EncodingSource
{
    /// <summary>The <see cref="IEncodingDetector"/> candidate met the confidence threshold and was adopted.</summary>
    Detected,

    /// <summary>The candidate was below the confidence threshold; the FR-6 default (UTF-8) was used.</summary>
    DefaultFallback,

    /// <summary>The user explicitly chose an encoding; it overrides both detection and the default (FR-6).</summary>
    UserOverride,
}

/// <summary>
/// The result of the pure FR-6 detect-then-preserve decision: the buffer's effective encoding, the
/// encoding to use on save (the preserve-on-save rule), and how each was chosen.
/// </summary>
/// <param name="EffectiveEncoding">The encoding the buffer is decoded/displayed as.</param>
/// <param name="SaveEncoding">
/// The encoding to write on save. By FR-6 this preserves the file's original (detected/effective)
/// encoding — UNLESS the user changed it, in which case it is the user's chosen encoding.
/// </param>
/// <param name="Source">How <see cref="EffectiveEncoding"/> was chosen.</param>
public sealed record EncodingDecisionResult(
    string EffectiveEncoding,
    string SaveEncoding,
    EncodingSource Source);

/// <summary>
/// The pure FR-6 encoding detect-then-preserve DECISION (architecture §1.2, §1.3, OQ-ARCH-4).
/// Given the host <see cref="IEncodingDetector"/>'s classification (an <see cref="EncodingCandidate"/>:
/// encoding name + 0..1 confidence) plus an optional explicit user override, it decides (a) the buffer's
/// effective encoding and (b) the on-save preserve rule. It does NO detection itself (that is the host
/// seam's byte-sniff) and references NO charset package — it is a pure function over the seam's output,
/// unit-testable with a fixture corpus. This keeps the charset-detector NuGet out of <c>HoardPad.Core</c>
/// (§2.1 invariant 1 stays absolute).
/// </summary>
public static class EncodingDecision
{
    /// <summary>
    /// The FR-6 default encoding (A-8 cut-item (4): "ship UTF-8-only"; build-context: ".hoard = UTF-8 JSON").
    /// Adopted when detection is below the confidence threshold.
    /// </summary>
    public const string DefaultEncoding = "utf-8";

    /// <summary>
    /// The confidence threshold for adopting a detected encoding. The docs name no numeric value for the
    /// decision (the spike caps FR-11/FR-38/FR-40 are unrelated), so this is the chosen default: the
    /// 0..1 midpoint — the natural "more likely than not" boundary. A candidate with
    /// <see cref="EncodingCandidate.Confidence"/> &gt; this is adopted; at-or-below falls back to
    /// <see cref="DefaultEncoding"/>.
    /// </summary>
    public const double ConfidenceThreshold = 0.5;

    /// <summary>
    /// Decide the buffer's effective encoding and the on-save preserve rule from a detector candidate.
    /// </summary>
    /// <param name="detected">
    /// The <see cref="IEncodingDetector"/> classification: encoding name + 0..1 confidence.
    /// </param>
    /// <param name="userOverride">
    /// The encoding the user explicitly chose for this buffer (FR-6 encoding display), or <c>null</c> when
    /// the user has not overridden. When set, it wins over detection AND the default, and becomes the
    /// save encoding (the user "changed it", so preserve-on-save preserves the user's choice).
    /// </param>
    /// <returns>The effective encoding, the save encoding, and the source of the effective encoding.</returns>
    public static EncodingDecisionResult Decide(EncodingCandidate detected, string? userOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(userOverride))
            return new EncodingDecisionResult(userOverride, userOverride, EncodingSource.UserOverride);

        // Detection adopted only when confident; otherwise the FR-6 default. The save encoding
        // preserves whatever the buffer's effective encoding is (FR-6 preserve-on-save).
        var (effective, source) =
            detected.Confidence > ConfidenceThreshold && !string.IsNullOrWhiteSpace(detected.EncodingName)
                ? (detected.EncodingName, EncodingSource.Detected)
                : (DefaultEncoding, EncodingSource.DefaultFallback);

        return new EncodingDecisionResult(effective, effective, source);
    }
}
