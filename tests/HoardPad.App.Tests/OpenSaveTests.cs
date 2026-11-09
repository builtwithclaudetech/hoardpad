// OpenSaveTests.cs — the FR-3/FR-4 open + save byte-correct round-trip through IAtomicFileStore
// (architecture §1.4, design §3.4). Uses the REAL Host AtomicFileStore + PathResolver + the interim
// UTF-8 detector against a temp directory: write a file, open it, (optionally edit), save, and assert
// the on-disk bytes are exactly what they should be. The save MUST go through the atomic store (the
// milestone's "byte-correct through the atomic store" done-criterion). The verify filter matches the
// class name "OpenSave".

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Hosting;
using HoardPad.App.Editor;
using HoardPad.Host;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class OpenSaveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EditorFileService _service;

    public OpenSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoardpad-opensave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new EditorFileService(new AtomicFileStore(), new PathResolver(), new UtfDefaultEncodingDetector());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public async Task OpenReadsFileContentsAndReportsUtf8()
    {
        string path = TempPath("hello.txt");
        await File.WriteAllTextAsync(path, "hello\nworld\n", new UTF8Encoding(false));

        EditorDocument doc = await _service.OpenAsync(path, CancellationToken.None);

        Assert.Equal("hello\nworld\n", doc.Text);
        Assert.Equal("utf-8", doc.EncodingName);
        Assert.False(doc.HasBom);
    }

    [Fact]
    public async Task NoEditLoadSaveRoundTripsByteForByte()
    {
        string path = TempPath("roundtrip.md");
        byte[] original = Encoding.UTF8.GetBytes("# Title\n\nBody text with unicode: café ☕\n");
        await File.WriteAllBytesAsync(path, original);

        EditorDocument doc = await _service.OpenAsync(path, CancellationToken.None);
        await _service.SaveAsync(doc, CancellationToken.None);

        byte[] saved = await File.ReadAllBytesAsync(path);
        Assert.Equal(original, saved);
    }

    [Fact]
    public async Task EditThenSaveWritesTheEditedBytesThroughTheAtomicStore()
    {
        string path = TempPath("edit.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("before\n"));

        EditorDocument doc = await _service.OpenAsync(path, CancellationToken.None);
        doc.Text = "after the edit\n";
        ReadOnlyMemory<byte> written = await _service.SaveAsync(doc, CancellationToken.None);

        byte[] onDisk = await File.ReadAllBytesAsync(path);
        Assert.Equal(Encoding.UTF8.GetBytes("after the edit\n"), onDisk);
        Assert.Equal(onDisk, written.ToArray());
    }

    [Fact]
    public async Task FirstSaveOfANewFileCreatesItThroughTheAtomicStore()
    {
        string path = TempPath("created.txt");
        var doc = new EditorDocument { Path = path, Text = "fresh file\n", EncodingName = "utf-8" };

        Assert.False(File.Exists(path));
        await _service.SaveAsync(doc, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(Encoding.UTF8.GetBytes("fresh file\n"), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Utf8BomIsPreservedAcrossANoEditRoundTrip()
    {
        string path = TempPath("bom.txt");
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        byte[] body = Encoding.UTF8.GetBytes("with bom\n");
        byte[] original = new byte[bom.Length + body.Length];
        bom.CopyTo(original, 0);
        body.CopyTo(original, bom.Length);
        await File.WriteAllBytesAsync(path, original);

        EditorDocument doc = await _service.OpenAsync(path, CancellationToken.None);
        Assert.True(doc.HasBom);
        Assert.Equal("with bom\n", doc.Text); // the BOM is stripped from the decoded text

        await _service.SaveAsync(doc, CancellationToken.None);
        Assert.Equal(original, await File.ReadAllBytesAsync(path)); // and re-emitted on save
    }

    // ---- UTF-32 BOM end-to-end through the REAL HoardPad.Host.EncodingDetector. ----
    //
    // This closes the M8.3 Critical: the detector adopts utf-32le (BOM FF FE 00 00) at confidence 1.0,
    // but EditorFileService must STRIP the 4-byte BOM on open (no stray U+FEFF in Text, HasBom == true)
    // and RE-EMIT it on save. Wired with the production EncodingDetector (not the interim UTF-8 default)
    // so the open→save round-trip is proven byte-identical through the real detect-then-preserve path.
    [Fact]
    public async Task Utf32LeBomRoundTripsByteForByteThroughTheRealDetector()
    {
        // Register CodePages so TextEncodings.Resolve("utf-32le") -> GetEncoding succeeds (as the app does).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var service = new EditorFileService(new AtomicFileStore(), new PathResolver(), new EncodingDetector());

        string path = TempPath("utf32le.txt");
        var utf32LeWithBom = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        string text = "UTF-32 LE round-trip: café ☕ 世界\n";
        byte[] original = utf32LeWithBom.GetPreamble()
            .Concat(new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetBytes(text))
            .ToArray();
        await File.WriteAllBytesAsync(path, original);

        EditorDocument doc = await service.OpenAsync(path, CancellationToken.None);

        Assert.Equal("utf-32le", doc.EncodingName);
        Assert.True(doc.HasBom);                       // the 4-byte BOM was recognized and recorded
        Assert.Equal(text, doc.Text);                  // and stripped — no stray U+FEFF leading the buffer
        Assert.DoesNotContain('\uFEFF', doc.Text);     // explicit: the BOM char must not survive into Text

        await service.SaveAsync(doc, CancellationToken.None);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));   // byte-identical round-trip
    }

    [Fact]
    public async Task ReinterpretReDecodesTheSameBytesUnderAChosenEncoding()
    {
        // Bytes that are valid Windows-1252 but garbage as UTF-8 (0xE9 = 'é' in 1252).
        string path = TempPath("latin1.txt");
        byte[] bytes = { 0x63, 0x61, 0x66, 0xE9 }; // "caf" + 0xE9
        await File.WriteAllBytesAsync(path, bytes);

        EditorDocument doc = await _service.OpenAsync(path, CancellationToken.None);
        await _service.ReinterpretAsync(doc, "windows-1252", CancellationToken.None);

        Assert.Equal("café", doc.Text);
        Assert.Equal("windows-1252", doc.EncodingName);
    }

    [Fact]
    public async Task CrlfLineEndingIsDetectedOnOpen()
    {
        string path = TempPath("crlf.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("a\r\nb\r\n"));

        EditorDocument doc = await _service.OpenAsync(path, CancellationToken.None);

        Assert.Equal(LineEnding.Crlf, doc.LineEnding);
    }

    // ---- CP-1252 vs Latin-1 differentiating byte: correctness guard for the windows-1252 fix. ----
    //
    // Byte 0x96 is the en-dash (–) in Windows-1252 (CP-1252) but a C1 control char (U+0096) in
    // ISO-8859-1 (Latin-1). If TextEncodings.Resolve("windows-1252") had returned Encoding.Latin1,
    // this test would have decoded to a C1 control and failed. The real CP-1252 encoding is provided by
    // System.Text.Encoding.CodePages + RegisterProvider (done in CompositionRoot.ConfigureServices).
    [Fact]
    public async Task Windows1252ByteDistinctFromLatin1DecodesCorrectlyOnReinterpret()
    {
        // Register the CodePages provider the same way the composition root does at startup.
        // Safe to call multiple times — BCL deduplicates.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        string path = TempPath("cp1252.txt");
        // 0x96 = en-dash in CP-1252; also valid in Latin-1 but as a C1 control (not a printable char).
        byte[] bytes = { 0x61, 0x96, 0x62 }; // "a<0x96>b"
        await File.WriteAllBytesAsync(path, bytes);

        EditorDocument doc = await _service.OpenAsync(path, CancellationToken.None);
        await _service.ReinterpretAsync(doc, "windows-1252", CancellationToken.None);

        // CP-1252: 0x96 → U+2013 EN DASH (–). If Latin-1 were used, it would be U+0096 (C1 control).
        Assert.Equal("a–b", doc.Text);
        Assert.Equal("windows-1252", doc.EncodingName);
        // Confirm Latin-1 would differ — the BCL Latin1 encoding maps 0x96 → U+0096, not U+2013.
        string latin1Decoded = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.NotEqual(latin1Decoded, doc.Text);
    }
}
