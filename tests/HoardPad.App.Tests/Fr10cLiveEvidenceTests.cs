// Fr10cLiveEvidenceTests.cs — FR-10(c) accept-loop fix evidence gate: confirms the manually-driven
// live-verify artifacts (Fr10cLiveWriteVerifyTests, run with HOARDPAD_LIVE_FR10C=1) exist and are
// well-formed. The live drive itself is gated behind that env var (it spends real claude -p API
// budget), so this test does NOT re-run it — it asserts the artifacts that drive produced are present
// and non-trivial. Mirrors StfLiveEvidenceTests.cs's pattern exactly.
//
// Class name contains "Fr10cLive" so the verify filter (`--filter FullyQualifiedName~Fr10cLive`)
// resolves it alongside Fr10cLiveWriteVerifyTests.
//
// artifacts/fr10c-live/ is untracked local evidence and is absent on a fresh clone; the tests then report
// as explicitly SKIPPED (with a reason), never as silently passed. When the directory is present they
// assert in full.

using System.IO;
using System.Text.Json;
using Xunit;

namespace HoardPad.App.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (with an explicit reason) when the untracked
/// <c>artifacts/fr10c-live/</c> evidence directory is absent, e.g. on a fresh clone.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Method)]
public sealed class Fr10cLiveEvidenceFactAttribute : FactAttribute
{
    /// <param name="evidenceFile">The evidence file (under artifacts/fr10c-live/) this test asserts on. The skip keys on the
    /// FILE, not the directory, mirroring StfLiveEvidenceFactAttribute: there, another render test creates the
    /// evidence directory on a fresh clone, so a directory check flipped a second run from skipped to failed.</param>
    public Fr10cLiveEvidenceFactAttribute(string evidenceFile)
    {
        if (!File.Exists(Path.Combine(Fr10cLiveEvidenceTests.ResolveFr10cLiveDir(), evidenceFile)))
            Skip = $"artifacts/fr10c-live/{evidenceFile} (untracked local live-verify evidence) is absent, as on a fresh clone.";
    }
}

public sealed class Fr10cLiveEvidenceTests
{
    /// <summary>
    /// Resolve artifacts/fr10c-live from the repo root (test binary runs deep under bin/, walk up to
    /// the directory containing HoardPad.sln — same pattern as StfLiveEvidenceTests/Run12RenderVerifyTests).
    /// </summary>
    internal static string ResolveFr10cLiveDir()
    {
        DirectoryInfo? dir = new(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? System.AppContext.BaseDirectory;
        return Path.Combine(root, "artifacts", "fr10c-live");
    }

    [Fr10cLiveEvidenceFact("fr10c-armed-no-degrade.png")]
    public void ArmedNoDegradeScreenshotExistsAndIsNonTrivial()
    {
        string path = Path.Combine(ResolveFr10cLiveDir(), "fr10c-armed-no-degrade.png");
        Assert.True(File.Exists(path), $"Missing FR-10(c) armed-no-degrade evidence screenshot: {path}");
        Assert.True(new FileInfo(path).Length > 1024, "Armed-no-degrade screenshot is suspiciously small (< 1 KB).");
    }

    [Fr10cLiveEvidenceFact("transcript-readwrite.json")]
    public void ReadThenWriteTranscriptExistsIsWellFormedAndContainsNoDegradeEvent()
    {
        string path = Path.Combine(ResolveFr10cLiveDir(), "transcript-readwrite.json");
        Assert.True(File.Exists(path), $"Missing FR-10(c) Read-then-Write transcript: {path}");
        AssertTranscriptWellFormedAndDegradeFree(path);
    }

    [Fr10cLiveEvidenceFact("transcript-writelast.json")]
    public void WriteLastTranscriptExistsIsWellFormedAndContainsNoDegradeEvent()
    {
        string path = Path.Combine(ResolveFr10cLiveDir(), "transcript-writelast.json");
        Assert.True(File.Exists(path), $"Missing FR-10(c) Write-last transcript: {path}");
        AssertTranscriptWellFormedAndDegradeFree(path);
    }

    /// <summary>
    /// A well-formed transcript is a non-empty JSON array of line objects, each carrying at least a
    /// "kind" string; carries at least one FileOpChip line with badge "Applied" (the write was
    /// observed applied); and carries NO SafetyEventChip line whose text mentions "degrade" (the
    /// FR-10(c) proof this whole file exists to gate).
    /// </summary>
    private static void AssertTranscriptWellFormedAndDegradeFree(string path)
    {
        Assert.True(new FileInfo(path).Length > 2, $"Transcript is suspiciously small (< 2 bytes): {path}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() > 0, $"Transcript array is empty: {path}");

        bool sawAppliedFileOp = false;
        foreach (JsonElement line in doc.RootElement.EnumerateArray())
        {
            Assert.True(line.TryGetProperty("kind", out JsonElement kindEl), $"A transcript line is missing 'kind': {path}");
            string kind = kindEl.GetString() ?? string.Empty;
            Assert.False(string.IsNullOrEmpty(kind), $"A transcript line has an empty 'kind': {path}");

            if (kind == "SafetyEventChip"
                && line.TryGetProperty("text", out JsonElement textEl)
                && (textEl.GetString() ?? string.Empty).Contains("degrade", System.StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail($"FR-10(c) REGRESSION: a Degrade SafetyEventChip is present in {path}: {textEl.GetString()}");
            }

            if (kind == "FileOpChip"
                && line.TryGetProperty("badge", out JsonElement badgeEl)
                && badgeEl.GetString() == "Applied")
            {
                sawAppliedFileOp = true;
            }
        }

        Assert.True(sawAppliedFileOp, $"No FileOpChip with badge 'Applied' was found in {path}.");
    }
}
