// EncodingDetector.cs — the FR-6 byte-sniff/BOM heuristic (M8.3; architecture §1.3 / OQ-ARCH-4).
//
// This is the ONE place the UTF.Unknown charset NuGet lives behind the IEncodingDetector seam.
// UTF.Unknown NEVER enters HoardPad.Core (§2.1 invariant 1 is absolute); the seam interface is in
// Core (HoardPad.Core.Host.Abstractions) but this concrete impl + its package are Host-only. The
// PURE detect-then-preserve DECISION over the (candidate, confidence) this returns is M1.3
// (Core EncodingDecision) — this detector only classifies; it makes no policy choice.
//
// Two-stage detection (brief: "BOM sniff first … then UTF.Unknown's CharsetDetector"):
//   1. BOM sniff — an explicit, deterministic leading-byte check for the Unicode BOMs (UTF-8,
//      UTF-16 LE/BE, UTF-32 LE/BE). A BOM is an unambiguous on-disk encoding declaration, so a
//      match returns confidence 1.0 with NO statistical pass. Done explicitly (not delegated to
//      UTF.Unknown's internal BOM handling) because the brief makes it the contract and it keeps
//      the Unicode-BOM result independent of the package's heuristic internals.
//   2. No-BOM statistical pass — UTF.Unknown's CharsetDetector.DetectFromBytes over the sample.
//      Its Detected.EncodingName (a charset id) + Detected.Confidence (0..1) map straight onto the
//      seam's EncodingCandidate; the name is normalized to the EncodingDecision/App vocabulary.
//
// Unsure case: when CharsetDetector cannot decide (Detected == null — e.g. an empty file), this
// returns (utf-8, 0.0). 0.0 is below EncodingDecision.ConfidenceThreshold (0.5), so the pure
// decision falls back to the FR-6 default (UTF-8) — the correct "couldn't tell, default it" path.

using System.Runtime.InteropServices;
using System.Text;
using HoardPad.Core.Host.Abstractions;
using UtfUnknown;

namespace HoardPad.Host;

/// <summary>
/// The FR-6 byte-sniff/BOM encoding detector (architecture §1.3, OQ-ARCH-4). Implements
/// <see cref="IEncodingDetector"/>: a BOM sniff first, then a UTF.Unknown statistical pass for the
/// no-BOM case, returning a candidate charset name + 0..1 confidence the pure Core decision (M1.3)
/// consumes. The UTF.Unknown package lives behind this seam in Host — never <c>HoardPad.Core</c>.
/// </summary>
public sealed class EncodingDetector : IEncodingDetector
{
    // The Unicode BOMs we recognize explicitly (longest-first so UTF-32 wins over its UTF-16 prefix).
    // A BOM is an unambiguous on-disk encoding marker: a match short-circuits with full confidence.
    private static readonly (byte[] Bom, string EncodingName)[] BomTable =
    {
        (new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "utf-32be"),
        (new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "utf-32le"),
        (new byte[] { 0xEF, 0xBB, 0xBF }, "utf-8"),
        (new byte[] { 0xFE, 0xFF }, "utf-16be"),
        (new byte[] { 0xFF, 0xFE }, "utf-16le"),
    };

    public EncodingCandidate Detect(ReadOnlyMemory<byte> sample)
    {
        ReadOnlySpan<byte> span = sample.Span;

        // Stage 1: explicit BOM sniff — an unambiguous declaration → confidence 1.0, no heuristic.
        foreach ((byte[] bom, string encodingName) in BomTable)
        {
            if (StartsWith(span, bom))
                return new EncodingCandidate(encodingName, 1.0);
        }

        // Stage 2: no BOM → UTF.Unknown statistical pass over the exact sample window. The caller hands
        // us an array-backed slice (EditorFileService passes bytes.AsMemory(0, n)), so TryGetArray gives
        // the offset/len overload the underlying buffer with NO copy; ToArray is only the rare fallback
        // (a non-array-backed ReadOnlyMemory). A null Detected means "couldn't decide" (e.g. empty input).
        DetectionResult result = MemoryMarshal.TryGetArray(sample, out ArraySegment<byte> segment)
            ? CharsetDetector.DetectFromBytes(segment.Array!, segment.Offset, segment.Count)
            : CharsetDetector.DetectFromBytes(sample.ToArray());
        DetectionDetail? detected = result.Detected;
        if (detected is null || string.IsNullOrWhiteSpace(detected.EncodingName))
            return new EncodingCandidate("utf-8", 0.0);

        return new EncodingCandidate(Normalize(detected.EncodingName), detected.Confidence);
    }

    /// <summary>
    /// Map a UTF.Unknown charset name onto the EncodingDecision/App vocabulary. Most names already
    /// match the seam ids (utf-8, utf-16le, utf-16be, windows-1252, shift-jis …) since UTF.Unknown
    /// reports lower-case BCL-style ids; the one rename is UTF.Unknown's <c>ascii</c> → the seam's
    /// <c>us-ascii</c>. Any other name is passed through lower-cased so the App resolves it via the
    /// CodePages provider (TextEncodings.Resolve) rather than mis-decoding it as UTF-8.
    /// </summary>
    private static string Normalize(string utfUnknownName)
    {
        string lower = utfUnknownName.Trim().ToLowerInvariant();
        return lower == "ascii" ? "us-ascii" : lower;
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);
}
