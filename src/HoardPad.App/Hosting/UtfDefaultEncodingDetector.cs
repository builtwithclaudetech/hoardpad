// UtfDefaultEncodingDetector.cs — the INTERIM UTF-8-default encoding detector (M6.1).
//
// FR-6 byte-sniff/statistical detection (the real UTF.Unknown-backed EncodingDetector)
// is the deferred cut-tail M8.3 and is OUT OF SCOPE for M6.1. This minimal stand-in lets
// the composition root resolve IEncodingDetector now AND matches the M6.2 "UTF-8 is the
// default for new buffers" behavior (design §3.4 / NFR-9): it always reports utf-8.
//
// REPLACED BY M8.3. When the UTF.Unknown-backed detector ships, it supersedes this
// registration in the composition root; UTF.Unknown must NEVER enter HoardPad.Core
// (architecture §2.1 invariant 1 / OQ-ARCH-4) — the seam keeps it on the App/Host side.

using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Hosting;

/// <summary>
/// Interim <see cref="IEncodingDetector"/> that always classifies the sample as UTF-8 with a
/// documented default confidence. It performs no statistical sniffing — the real heuristic
/// (charset-package-backed) is the deferred cut-tail M8.3, which replaces this registration.
/// </summary>
internal sealed class UtfDefaultEncodingDetector : IEncodingDetector
{
    /// <summary>The encoding the interim detector always reports — UTF-8 is HoardPad's default (NFR-9).</summary>
    private const string DefaultEncodingName = "utf-8";

    /// <summary>
    /// The default confidence reported for the unconditional UTF-8 classification. Deliberately
    /// below 1.0: this is a default, not a positive byte-sniff result, so the M1.3 pure
    /// detect-then-preserve decision can distinguish "defaulted to UTF-8" from "sniffed UTF-8"
    /// once the real M8.3 detector is wired in.
    /// </summary>
    private const double DefaultConfidence = 0.5;

    public EncodingCandidate Detect(ReadOnlyMemory<byte> sample) =>
        new(DefaultEncodingName, DefaultConfidence);
}
