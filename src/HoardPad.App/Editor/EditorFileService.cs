// EditorFileService.cs — FR-3/FR-4 open + save (architecture §1.4, design §3.4, TD §5.5).
//
// OPEN: read raw bytes (off the UI thread), strip + record any BOM, run the IEncodingDetector seam +
// the pure Core EncodingDecision (M1.3 detect-then-preserve), decode with the chosen encoding, and
// detect the EOL style. The interim M6.1 detector always reports UTF-8 (design §3.4 / NFR-9), so v1
// opens UTF-8 by default; the manual reinterpret path (Reopen) re-decodes the same bytes under a
// user-chosen encoding (FR-6 reinterpret, design §3.4 / ODQ-5). Auto-detect is the deferred M8.3.
//
// SAVE: encode the buffer with the document's preserve-on-save encoding (re-emitting the original BOM),
// then write through IAtomicFileStore — CreateWhenAbsentAsync for a first save, ReplaceWhenPresentAsync
// for an existing file (the atomic temp -> fsync -> rename path, NFR-4). A no-edit load->save therefore
// round-trips byte-for-byte. Never System.IO.File for the WRITE — the atomic store owns durable writes.

using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.Core.Encoding;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Editor;

/// <summary>Opens and saves <see cref="EditorDocument"/>s, with byte-correct encoding/EOL preservation.</summary>
internal sealed class EditorFileService
{
    // BOM samples we recognize on open (so they are stripped from the decoded text and re-emitted on save).
    // The UTF-32 BOMs are the canonical BCL preambles (UTF32Encoding.GetPreamble) so encode/decode stay
    // symmetric with TextEncodings.Resolve's GetEncoding("utf-32le"/"utf-32be"). Order is irrelevant here
    // (each arm is matched by encoding name), but note FF-FE-00-00 (UTF-32 LE) has FF-FE (UTF-16 LE) as a
    // prefix — the detector resolves that ambiguity longest-first; this map is keyed by the decided name.
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };
    private static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };
    private static readonly byte[] Utf32LeBom = { 0xFF, 0xFE, 0x00, 0x00 };
    private static readonly byte[] Utf32BeBom = { 0x00, 0x00, 0xFE, 0xFF };

    // How many leading bytes the detector samples (the BOM + a statistical-heuristic window).
    private const int DetectSampleBytes = 4096;

    private readonly IAtomicFileStore _store;
    private readonly IPathResolver _pathResolver;
    private readonly IEncodingDetector _detector;

    public EditorFileService(IAtomicFileStore store, IPathResolver pathResolver, IEncodingDetector detector)
    {
        _store = store;
        _pathResolver = pathResolver;
        _detector = detector;
    }

    /// <summary>
    /// Open a file into a new <see cref="EditorDocument"/>: read the bytes, decide the encoding via the
    /// detector seam + pure Core decision, decode, and capture the EOL style. Runs the byte read off the
    /// UI thread (architecture §3.1 — file I/O never blocks the dispatcher).
    /// </summary>
    public async Task<EditorDocument> OpenAsync(string rawPath, CancellationToken ct)
    {
        // PathResolver requires a rooted base for relative-path resolution; an absolute rawPath
        // ignores it. With no open-folder yet (M6.2), resolve relatives against the current directory.
        string root = Path.IsPathRooted(rawPath)
            ? Path.GetDirectoryName(rawPath) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();
        NormcasedPath canonical = _pathResolver.ResolveCanonical(rawPath, root);
        string path = canonical.Value;

        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        int sampleLength = System.Math.Min(bytes.Length, DetectSampleBytes);
        EncodingCandidate candidate = _detector.Detect(bytes.AsMemory(0, sampleLength));
        EncodingDecisionResult decision = EncodingDecision.Decide(candidate);

        var document = new EditorDocument { Path = path, EncodingName = decision.SaveEncoding };
        DecodeInto(document, bytes, decision.EffectiveEncoding);
        return document;
    }

    /// <summary>
    /// Re-decode an already-open document's on-disk bytes under a user-chosen encoding (FR-6 reinterpret,
    /// design §3.4). For a mis-detected file: same bytes, different decode. A scratch buffer (no path)
    /// only changes its declared encoding (no bytes to re-read).
    /// </summary>
    public async Task ReinterpretAsync(EditorDocument document, string encodingName, CancellationToken ct)
    {
        if (document.Path is null)
        {
            document.EncodingName = encodingName;
            return;
        }

        // Re-canonicalize the stored path (idempotent — it is already canonical from open) so the
        // re-read goes through the same host resolver leg, then re-decode the same bytes under the
        // user's chosen encoding (FR-6 reinterpret for a mis-detected file).
        string root = Path.GetDirectoryName(document.Path) ?? Directory.GetCurrentDirectory();
        NormcasedPath canonical = _pathResolver.ResolveCanonical(document.Path, root);

        byte[] bytes = await File.ReadAllBytesAsync(canonical.Value, ct).ConfigureAwait(false);
        document.EncodingName = encodingName;
        DecodeInto(document, bytes, encodingName);
    }

    /// <summary>
    /// Save the document to its path through the atomic store (FR-4 / NFR-4). Encodes with the
    /// preserve-on-save encoding and re-emits the original BOM, so a no-edit round-trip is byte-correct.
    /// New file -> CreateWhenAbsentAsync; existing -> ReplaceWhenPresentAsync. Returns the bytes written
    /// (so a caller/test can assert the round-trip). Runs off the UI thread.
    /// </summary>
    public async Task<ReadOnlyMemory<byte>> SaveAsync(EditorDocument document, CancellationToken ct)
    {
        if (document.Path is null)
            throw new System.InvalidOperationException("Cannot save a scratch buffer with no path; choose a path first.");

        ReadOnlyMemory<byte> content = Encode(document);

        bool exists = File.Exists(document.Path);
        if (exists)
            await _store.ReplaceWhenPresentAsync(document.Path, content, ct).ConfigureAwait(false);
        else
            await _store.CreateWhenAbsentAsync(document.Path, content, ct).ConfigureAwait(false);

        return content;
    }

    /// <summary>Encode a document to its on-disk byte form (BOM + text), the inverse of <see cref="DecodeInto"/>.</summary>
    internal static ReadOnlyMemory<byte> Encode(EditorDocument document)
    {
        Encoding encoding = TextEncodings.Resolve(document.EncodingName);
        byte[] textBytes = encoding.GetBytes(document.Text);

        if (!document.HasBom)
            return textBytes;

        byte[] bom = BomFor(document.EncodingName);
        if (bom.Length == 0)
            return textBytes;

        var withBom = new byte[bom.Length + textBytes.Length];
        bom.CopyTo(withBom, 0);
        textBytes.CopyTo(withBom, bom.Length);
        return withBom;
    }

    /// <summary>Decode raw bytes into the document under the given encoding, stripping + recording any BOM.</summary>
    private static void DecodeInto(EditorDocument document, byte[] bytes, string encodingName)
    {
        int bomLength = BomLength(bytes, encodingName);
        document.HasBom = bomLength > 0;

        Encoding encoding = TextEncodings.Resolve(encodingName);
        document.Text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        document.LineEnding = LineEndings.Detect(document.Text);
    }

    private static int BomLength(byte[] bytes, string encodingName)
    {
        return encodingName.ToUpperInvariant() switch
        {
            "UTF-8" when StartsWith(bytes, Utf8Bom) => Utf8Bom.Length,
            "UTF-32LE" when StartsWith(bytes, Utf32LeBom) => Utf32LeBom.Length,
            "UTF-32BE" when StartsWith(bytes, Utf32BeBom) => Utf32BeBom.Length,
            "UTF-16LE" when StartsWith(bytes, Utf16LeBom) => Utf16LeBom.Length,
            "UTF-16BE" when StartsWith(bytes, Utf16BeBom) => Utf16BeBom.Length,
            _ => 0,
        };
    }

    private static byte[] BomFor(string encodingName) => encodingName.ToUpperInvariant() switch
    {
        "UTF-8" => Utf8Bom,
        "UTF-32LE" => Utf32LeBom,
        "UTF-32BE" => Utf32BeBom,
        "UTF-16LE" => Utf16LeBom,
        "UTF-16BE" => Utf16BeBom,
        _ => System.Array.Empty<byte>(),
    };

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
                return false;
        }
        return true;
    }
}
