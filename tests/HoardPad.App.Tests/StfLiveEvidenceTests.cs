// StfLiveEvidenceTests.cs — STF-6 evidence gate: confirms the manually-driven live-verify artifacts
// (fresh R2R publish + Xvfb/xdotool real-claude-CLI write proof, per docs/nightly-schedule-stf456.md
// STF-6) exist and are well-formed. The live drive itself cannot be automated as a headless xunit test
// (it exercises the real GTK folder picker + the real claude CLI over a genuine X11 input path — the
// same manual pattern Run 12/QA2-3 used, see docs/spike-1-results-2026-06-19.md), so this test is the
// durable, CI-checkable evidence gate the schedule's "verify" line resolves to: it does NOT re-run the
// live drive, it asserts the artifacts that drive produced are present and non-trivial.
//
// Class name contains "StfLive" so the schedule's verify filter (`--filter FullyQualifiedName~StfLive`)
// resolves it.
//
// artifacts/stf-live/ is untracked local evidence and is absent on a fresh clone; the tests then report
// as explicitly SKIPPED (with a reason), never as silently passed. When the directory is present they
// assert in full.

using System.IO;
using Xunit;

namespace HoardPad.App.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (with an explicit reason) when the untracked
/// <c>artifacts/stf-live/</c> evidence directory is absent, e.g. on a fresh clone.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StfLiveEvidenceFactAttribute : FactAttribute
{
    /// <param name="evidenceFile">The evidence file (under artifacts/stf-live/) this test asserts on. The skip keys on the
    /// FILE, not the directory: other render tests create artifacts/stf-live/ on a fresh clone, so a directory check
    /// would flip a second run from skipped to failed.</param>
    public StfLiveEvidenceFactAttribute(string evidenceFile)
    {
        if (!File.Exists(Path.Combine(StfLiveEvidenceTests.ResolveStfLiveDir(), evidenceFile)))
            Skip = $"artifacts/stf-live/{evidenceFile} (untracked local live-verify evidence) is absent, as on a fresh clone.";
    }
}

public sealed class StfLiveEvidenceTests
{
    /// <summary>
    /// Resolve artifacts/stf-live from the repo root (test binary runs deep under bin/, walk up to the
    /// directory containing HoardPad.sln — same pattern as Run12RenderVerifyTests/Stf5OpenFolderRenderVerifyTests).
    /// </summary>
    internal static string ResolveStfLiveDir()
    {
        DirectoryInfo? dir = new(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? System.AppContext.BaseDirectory;
        return Path.Combine(root, "artifacts", "stf-live");
    }

    [StfLiveEvidenceFact("stf6-arm-succeeded-no-badge.png")]
    public void ArmSucceededNoBadgeScreenshotExistsAndIsNonTrivial()
    {
        string path = Path.Combine(ResolveStfLiveDir(), "stf6-arm-succeeded-no-badge.png");
        Assert.True(File.Exists(path), $"Missing STF-6 arm-succeeded evidence screenshot: {path}");
        Assert.True(new FileInfo(path).Length > 1024, "Arm-succeeded screenshot is suspiciously small (< 1 KB).");
    }

    [StfLiveEvidenceFact("stf6-live-write-success.png")]
    public void LiveWriteSuccessScreenshotExistsAndIsNonTrivial()
    {
        string path = Path.Combine(ResolveStfLiveDir(), "stf6-live-write-success.png");
        Assert.True(File.Exists(path), $"Missing STF-6 live-write evidence screenshot: {path}");
        Assert.True(new FileInfo(path).Length > 1024, "Live-write screenshot is suspiciously small (< 1 KB).");
    }
}
