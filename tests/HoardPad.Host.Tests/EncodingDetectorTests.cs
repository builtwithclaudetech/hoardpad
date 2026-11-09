// EncodingDetectorTests.cs — the FR-6 byte-sniff/BOM detector against a sample-file corpus (M8.3).
//
// Asserts HoardPad.Host.EncodingDetector classifies a built-in byte corpus (no external files,
// per the brief) and feeds a faithful (candidate, confidence) into the pure Core EncodingDecision:
//   - BOM-marked files (UTF-8 / UTF-16 LE / UTF-16 BE) → the seam name, confidence 1.0 (BOM sniff).
//   - no-BOM Unicode + non-Latin multibyte (UTF-8, Shift-JIS) → the right charset, high confidence.
//   - windows-1252 with the distinguishing 0x80–0x9F bytes → windows-1252 above threshold.
//   - plain ASCII → us-ascii (UTF.Unknown's "ascii" normalized to the seam vocabulary).
//   - the unsure case (empty input, Detected == null) → (utf-8, 0.0) → EncodingDecision defaults.
//
// The corpus bytes are the ones empirically verified to classify under UTF.Unknown 2.6.0 (the
// windows-125x family is statistically ambiguous, so the 1252 sample is the rich-quotes text that
// reliably resolves to 1252, not 1250/1258). The detector is the seam, so each test also runs the
// candidate through EncodingDecision to assert the end-to-end "detect-then-preserve" outcome.

using System.Text;
using HoardPad.Core.Encoding;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class EncodingDetectorTests
{
    private readonly EncodingDetector _detector = new();

    public EncodingDetectorTests()
    {
        // windows-1252 / shift_jis byte generation in this test needs the CodePages provider (the
        // app registers it in CompositionRoot; the test registers it here). Idempotent.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // ---- BOM sniff: unambiguous declaration → seam name + confidence 1.0 (no heuristic). ----

    [Fact]
    public void DetectsUtf8WithBomAsUtf8AtFullConfidence()
    {
        byte[] bytes = WithPreamble(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), "Héllo wörld café\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("utf-8", candidate.EncodingName);
        Assert.Equal(1.0, candidate.Confidence);
        AssertAdopted(candidate, "utf-8");
    }

    [Fact]
    public void DetectsUtf16LeWithBomAsUtf16LeAtFullConfidence()
    {
        byte[] bytes = WithPreamble(new UnicodeEncoding(bigEndian: false, byteOrderMark: true), "Hello UTF-16 LE world\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("utf-16le", candidate.EncodingName);
        Assert.Equal(1.0, candidate.Confidence);
        AssertAdopted(candidate, "utf-16le");
    }

    [Fact]
    public void DetectsUtf16BeWithBomAsUtf16BeAtFullConfidence()
    {
        byte[] bytes = WithPreamble(new UnicodeEncoding(bigEndian: true, byteOrderMark: true), "Hello UTF-16 BE world\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("utf-16be", candidate.EncodingName);
        Assert.Equal(1.0, candidate.Confidence);
        AssertAdopted(candidate, "utf-16be");
    }

    [Fact]
    public void DetectsUtf32LeWithBomAsUtf32LeAtFullConfidence()
    {
        byte[] bytes = WithPreamble(new UTF32Encoding(bigEndian: false, byteOrderMark: true), "Hello UTF-32 LE world\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("utf-32le", candidate.EncodingName);
        Assert.Equal(1.0, candidate.Confidence);
        AssertAdopted(candidate, "utf-32le");
    }

    [Fact]
    public void DetectsUtf32BeWithBomAsUtf32BeAtFullConfidence()
    {
        byte[] bytes = WithPreamble(new UTF32Encoding(bigEndian: true, byteOrderMark: true), "Hello UTF-32 BE world\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("utf-32be", candidate.EncodingName);
        Assert.Equal(1.0, candidate.Confidence);
        AssertAdopted(candidate, "utf-32be");
    }

    // Disambiguation oracle: the UTF-32 LE BOM (FF FE 00 00) has the UTF-16 LE BOM (FF FE) as a prefix.
    // A bare 4-byte FF-FE-00-00 buffer MUST classify as utf-32le, NOT utf-16le — this is the only test
    // that proves the BomTable's longest-first ordering is load-bearing (reorder it and this fails).
    [Fact]
    public void Utf32LeBomOutranksUtf16LeBomViaLongestFirstOrdering()
    {
        byte[] bytes = { 0xFF, 0xFE, 0x00, 0x00 };

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("utf-32le", candidate.EncodingName);
        Assert.Equal(1.0, candidate.Confidence);
    }

    // ---- No-BOM statistical pass. ----

    [Fact]
    public void DetectsUtf8WithoutBomAsUtf8()
    {
        // Multibyte UTF-8 sequences (accented Latin) push the detector off plain ASCII onto utf-8.
        byte[] bytes = new UTF8Encoding(false).GetBytes(
            "This is plain prose with accents: café déjà vu naïve résumé, jalapeño piñata.\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("utf-8", candidate.EncodingName);
        Assert.True(candidate.Confidence > EncodingDecision.ConfidenceThreshold);
        AssertAdopted(candidate, "utf-8");
    }

    [Fact]
    public void DetectsPlainAsciiAsUsAscii()
    {
        // UTF.Unknown reports "ascii"; the detector normalizes it to the seam vocabulary "us-ascii".
        byte[] bytes = Encoding.ASCII.GetBytes("Just plain ASCII text with numbers 12345 and symbols !@#$%.\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("us-ascii", candidate.EncodingName);
        Assert.True(candidate.Confidence > EncodingDecision.ConfidenceThreshold);
        AssertAdopted(candidate, "us-ascii");
    }

    [Fact]
    public void DetectsWindows1252WithC1DistinguishingBytesAsWindows1252()
    {
        // CP-1252 puts printable glyphs in 0x80–0x9F (€ smart-quotes em-dash ellipsis) where
        // ISO-8859-1 has C1 controls. Repeated rich text reliably resolves to 1252 (the 125x family
        // is otherwise ambiguous). The decoded text round-trips the distinguishing bytes.
        var w1252 = Encoding.GetEncoding("windows-1252");
        string sample = string.Concat(Enumerable.Repeat(
            "“Smart” quotes, em—dashes, ellipses… and the € euro — résumé, naïve, jalapeño.\n",
            6));
        byte[] bytes = w1252.GetBytes(sample);
        Assert.Contains((byte)0x80, bytes);   // the € byte — distinguishing from Latin-1.

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("windows-1252", candidate.EncodingName);
        Assert.True(candidate.Confidence > EncodingDecision.ConfidenceThreshold,
            $"windows-1252 confidence {candidate.Confidence} should exceed the {EncodingDecision.ConfidenceThreshold} adopt threshold");
        AssertAdopted(candidate, "windows-1252");
    }

    [Fact]
    public void DetectsShiftJisNonLatinMultibyteAsShiftJis()
    {
        byte[] bytes = Encoding.GetEncoding("shift_jis").GetBytes(
            "こんにちは世界、これはシフトJISのエンコーディング検出のテストです。\n");

        EncodingCandidate candidate = _detector.Detect(bytes);

        Assert.Equal("shift-jis", candidate.EncodingName);
        Assert.True(candidate.Confidence > EncodingDecision.ConfidenceThreshold);
        AssertAdopted(candidate, "shift-jis");
    }

    // ---- The unsure case: nothing to classify → default-with-zero-confidence. ----

    [Fact]
    public void EmptyInputIsUnsureDefaultsToUtf8AtZeroConfidence()
    {
        EncodingCandidate candidate = _detector.Detect(System.Array.Empty<byte>());

        Assert.Equal("utf-8", candidate.EncodingName);
        Assert.Equal(0.0, candidate.Confidence);

        // 0.0 is at/below the threshold, so the pure decision FALLS BACK to UTF-8 (not "adopted").
        EncodingDecisionResult decision = EncodingDecision.Decide(candidate);
        Assert.Equal("utf-8", decision.EffectiveEncoding);
        Assert.Equal(EncodingSource.DefaultFallback, decision.Source);
    }

    // ---- Helpers ----

    /// <summary>Encode text with the encoding's preamble (BOM) prepended — a BOM-marked file's bytes.</summary>
    private static byte[] WithPreamble(Encoding encoding, string text)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(text);
        byte[] all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }

    /// <summary>Assert the pure Core decision ADOPTS the detected candidate as the effective encoding.</summary>
    private static void AssertAdopted(EncodingCandidate candidate, string expectedEncoding)
    {
        EncodingDecisionResult decision = EncodingDecision.Decide(candidate);
        Assert.Equal(expectedEncoding, decision.EffectiveEncoding);
        Assert.Equal(EncodingSource.Detected, decision.Source);
    }
}
