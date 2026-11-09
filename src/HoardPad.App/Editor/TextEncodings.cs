// TextEncodings.cs — maps the FR-6 encoding NAMES (the IEncodingDetector / EncodingDecision string ids)
// to System.Text.Encoding instances, and back. The charset names ("utf-8", "utf-16le", …) are the
// Core/seam vocabulary; the BCL Encoding object is an App-layer concern (Core never touches it).
//
// Round-trip discipline: the BOM is tracked separately (EditorDocument.HasBom) so save re-emits the
// original BOM presence — Encoding.GetBytes never prepends a BOM, so BOM handling is explicit here.
//
// CodePages prerequisite: windows-1252 requires Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
// to have been called before Resolve("windows-1252"). The composition root does this at startup
// (CompositionRoot.ConfigureServices) before any TextEncodings.Resolve call.

using System.Collections.Generic;
using System.Text;

namespace HoardPad.App.Editor;

/// <summary>Resolves FR-6 encoding names to <see cref="Encoding"/> and enumerates the reinterpret choices.</summary>
internal static class TextEncodings
{
    // The encodings offered in the manual reinterpret menu (design §3.4 / ODQ-5: display + reinterpret).
    // Names match the EncodingDecision / IEncodingDetector vocabulary (lower-case, no-BOM ids).
    //
    // windows-1252: We do NOT store this in the static dictionary because the CodePagesEncodingProvider
    // must be registered BEFORE Encoding.GetEncoding("windows-1252") is called, and static-field
    // initializers run before any instance code. We resolve it lazily in Resolve() instead.
    private static readonly Dictionary<string, Encoding> ByName =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            // (emitBOM: false, throwOnInvalidBytes: false) — we manage the BOM explicitly (HasBom) and
            // never throw mid-decode on a mis-detected file (the reinterpret menu is the recovery path).
            ["utf-8"] = new UTF8Encoding(false, false),
            ["utf-16le"] = new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
            ["utf-16be"] = new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
            ["us-ascii"] = Encoding.ASCII,
        };

    /// <summary>Resolve an FR-6 encoding name to a BCL <see cref="Encoding"/>; UTF-8 (no BOM) when unknown.</summary>
    public static Encoding Resolve(string name)
    {
        if (ByName.TryGetValue(name, out Encoding? enc))
            return enc;

        // A name not in the curated set (windows-1252, or an M8.3 auto-detected charset such as
        // shift-jis / euc-jp / windows-1250) is resolved via Encoding.GetEncoding so the real
        // CodePagesEncodingProvider decodes it — NOT silently mis-decoded as UTF-8. windows-1252 is
        // the canonical case: CP-1252 differs from ISO-8859-1 (Latin-1) in the 0x80–0x9F range
        // (printable € ‚ ƒ „ … smart quotes / em-dash vs C1 controls), so Encoding.Latin1 would lie.
        // CodePagesEncodingProvider must be registered (done in CompositionRoot.ConfigureServices).
        // An id GetEncoding cannot resolve (a genuinely unknown name) throws — we fall back to UTF-8.
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (System.ArgumentException)
        {
            // Unknown / unrecognised name: fall back to UTF-8 (no BOM).
            return ByName["utf-8"];
        }
    }

    /// <summary>The human-facing status-bar label for an encoding name (e.g. "UTF-8", "UTF-16 LE").</summary>
    public static string DisplayLabel(string name) => name.ToUpperInvariant() switch
    {
        "UTF-8" => "UTF-8",
        "UTF-16LE" => "UTF-16 LE",
        "UTF-16BE" => "UTF-16 BE",
        "WINDOWS-1252" => "Windows-1252",
        "US-ASCII" => "US-ASCII",
        _ => name,
    };
}
