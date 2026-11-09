// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

// IEncodingDetector has NO verbatim signature in TD §2 (TD §2 specifies five seams; the sixth is
// added by architecture §1.2/§1.3 / OQ-ARCH-4 to keep the charset-detection package out of Core).
// It is introduced here from the architecture §1.3 contract: "the FR-6 byte-sniff/BOM heuristic
// (uses the charset-detection package); returns a candidate encoding + confidence the Core decision
// consumes." The concrete EncodingDetector (which references the charset-detection package, pinned
// in Directory.Packages.props) lives in HoardPad.Host/App (M8.3) — NEVER Core. The PURE
// detect-then-preserve DECISION over (candidate, confidence) is M1.3, not this seam.

/// HOST [host]. The FR-6 byte-sniff/BOM heuristic. Returns a candidate encoding + confidence the
/// pure Core decision (M1.3) consumes. The charset-detection package (Directory.Packages.props)
/// lives behind THIS seam in HoardPad.Host/App; it never enters HoardPad.Core (§2.1 invariant 1 /
/// OQ-ARCH-4).
public interface IEncodingDetector
{
    /// Sniff a file's leading bytes (BOM + statistical heuristic) and return the most-likely
    /// encoding plus a 0..1 confidence. The Core decision decides the buffer's effective encoding
    /// and the on-save preserve rule from this classification.
    EncodingCandidate Detect(ReadOnlyMemory<byte> sample);
}

/// The detector's classification: an encoding identifier (e.g. "utf-8", "windows-1252") and a
/// 0..1 confidence. A pure value the Core detect-then-preserve decision (M1.3) consumes.
public sealed record EncodingCandidate(string EncodingName, double Confidence);
