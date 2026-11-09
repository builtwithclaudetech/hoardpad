// AiTurnRunnerFileGrantsTests.cs — the PANEL-A2 runner integration (feature-ai-multidoc-panel §5.3 Path A).
//
// Proves the runner's HOARDPAD_FILE_GRANTS delivery: with an armed turn carrying non-empty grant paths the
// launch env carries HOARDPAD_FILE_GRANTS pointing at a file whose parsed content equals the grant paths,
// and the grants file is deleted after the turn; with NO grant paths the env var is absent (byte-identical
// env — the AC-MD-7 frozen-floor guard); a writer that throws falls back to root-only (env var absent, turn
// still proceeds — FR-MD-10 / AC-MD-6). A capturing backend probes the exact env the launch seam received.

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiTurnRunnerFileGrantsTests
{
    private static NormcasedPath TempRoot() =>
        new HoardPad.Host.PathResolver().ResolveCanonical(
            System.IO.Path.GetTempPath(), System.IO.Path.GetTempPath());

    private static Dictionary<string, string> SessionEnv() => new(System.StringComparer.Ordinal)
    {
        ["HOARDPAD_SANDBOX_ROOT"] = "/the/root",
        ["HOARDPAD_CAP_STATE_FILE"] = "/appdata/caps/abc.capstate",
    };

    private static async Task DriveOneTurnAsync(AiTurnRunner runner, TurnRequest request)
    {
        await foreach (TurnEvent _ in runner.RunAsync(request, CancellationToken.None)) { }
    }

    [Fact]
    public async Task ArmedTurnWithGrantPathsInjectsHoardpadFileGrantsPointingAtAFileWithThoseExactPaths()
    {
        var backend = new CapturingAgentBackend();
        var writer = new FakeFileGrantsWriter();
        var runner = new AiTurnRunner(
            backend, TempRoot(), settingsJsonPath: "/tmp/s.json", maxBudgetUsd: 5m, SessionEnv(),
            conversationKey: "conv-1", grantsWriter: writer);

        string[] grants = { "/home/user/a.txt", "/home/user/other/b.md" };

        await DriveOneTurnAsync(runner, new TurnRequest(
            "hello", AgencyMode.AutoApply, WriteAgencyArmed: true, FileGrantPaths: grants));

        Assert.NotNull(backend.CapturedEnv);
        Assert.True(backend.CapturedEnv!.TryGetValue("HOARDPAD_FILE_GRANTS", out string? grantsPath));
        // The env points at exactly the path the writer produced.
        Assert.Equal(writer.LastWrittenPath, grantsPath);
        // The written file's parsed content equals the turn's grant paths (the identical set PANEL-2 gated on).
        Assert.Equal(grants, writer.LastWrittenPaths);
        // Session env is preserved under the grant merge (never dropped).
        Assert.Equal("/the/root", backend.CapturedEnv["HOARDPAD_SANDBOX_ROOT"]);
    }

    [Fact]
    public async Task GrantsFileIsDeletedAfterTheTurn()
    {
        var backend = new CapturingAgentBackend();
        var writer = new FakeFileGrantsWriter();
        var runner = new AiTurnRunner(
            backend, TempRoot(), settingsJsonPath: "/tmp/s.json", maxBudgetUsd: 5m, SessionEnv(),
            conversationKey: "conv-1", grantsWriter: writer);

        string[] grants = { "/x/a.txt" };
        await DriveOneTurnAsync(runner, new TurnRequest(
            "hi", AgencyMode.AutoApply, WriteAgencyArmed: true, FileGrantPaths: grants));

        Assert.Equal(writer.LastWrittenPath, writer.DeletedPath);
        Assert.True(writer.WriteCount == 1 && writer.DeleteCount == 1);
    }

    [Fact]
    public async Task NoGrantPathsLeavesTheEnvByteIdenticalAndNeverCallsTheWriter()
    {
        // AC-MD-7 frozen-floor guard: an armed turn with NO grant paths must leave HOARDPAD_FILE_GRANTS
        // unset and never touch the writer — the env the launch seam receives is byte-identical to today.
        var backend = new CapturingAgentBackend();
        var writer = new FakeFileGrantsWriter();
        Dictionary<string, string> sessionEnv = SessionEnv();
        var runner = new AiTurnRunner(
            backend, TempRoot(), settingsJsonPath: "/tmp/s.json", maxBudgetUsd: 5m, sessionEnv,
            conversationKey: "conv-1", grantsWriter: writer);

        await DriveOneTurnAsync(runner, new TurnRequest(
            "hi", AgencyMode.AutoApply, WriteAgencyArmed: true, FileGrantPaths: null));

        Assert.NotNull(backend.CapturedEnv);
        Assert.False(backend.CapturedEnv!.ContainsKey("HOARDPAD_FILE_GRANTS"));
        Assert.Equal(sessionEnv, backend.CapturedEnv);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task ReadOnlyTurnNeverDeliversGrantsEvenWithPathsPresent()
    {
        // A read-only (not write-armed) turn must never arm grants (FR-MD-5 write-only surface).
        var backend = new CapturingAgentBackend();
        var writer = new FakeFileGrantsWriter();
        var runner = new AiTurnRunner(
            backend, TempRoot(), settingsJsonPath: "/tmp/s.json", maxBudgetUsd: 5m, SessionEnv(),
            conversationKey: "conv-1", grantsWriter: writer);

        string[] grants = { "/x/a.txt" };
        await DriveOneTurnAsync(runner, new TurnRequest(
            "hi", AgencyMode.ReadOnly, WriteAgencyArmed: false, FileGrantPaths: grants));

        Assert.False(backend.CapturedEnv!.ContainsKey("HOARDPAD_FILE_GRANTS"));
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task AWriterThatThrowsFallsBackToRootOnlyAndTheTurnStillProceeds()
    {
        // FR-MD-10 / AC-MD-6: a grants-file write fault sets NO env var and does NOT abort/widen the turn —
        // it proceeds root-only (the out-of-folder writes are simply denied by the hook). Never launch
        // pointing at a stale/failed file.
        var backend = new CapturingAgentBackend();
        var writer = new ThrowingFileGrantsWriter();
        var runner = new AiTurnRunner(
            backend, TempRoot(), settingsJsonPath: "/tmp/s.json", maxBudgetUsd: 5m, SessionEnv(),
            conversationKey: "conv-1", grantsWriter: writer);

        // The turn completes without throwing.
        string[] grants = { "/x/a.txt" };
        await DriveOneTurnAsync(runner, new TurnRequest(
            "hi", AgencyMode.AutoApply, WriteAgencyArmed: true, FileGrantPaths: grants));

        // The launch happened (root-only) and the env var is absent.
        Assert.NotNull(backend.CapturedEnv);
        Assert.False(backend.CapturedEnv!.ContainsKey("HOARDPAD_FILE_GRANTS"));
    }

    // ---- fakes ----

    private sealed class FakeFileGrantsWriter : IFileGrantsWriter
    {
        public string? LastWrittenPath { get; private set; }
        public IReadOnlyCollection<string>? LastWrittenPaths { get; private set; }
        public string? DeletedPath { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<string?> WriteAsync(
            string conversationKey, IReadOnlyCollection<string> resolvedGrantPaths, CancellationToken ct)
        {
            WriteCount++;
            if (resolvedGrantPaths.Count == 0)
                return Task.FromResult<string?>(null); // fail-closed empty (mirrors the real writer).
            LastWrittenPaths = resolvedGrantPaths;
            LastWrittenPath = $"/appdata/filegrants/{conversationKey}.filegrants.json";
            return Task.FromResult<string?>(LastWrittenPath);
        }

        public void Delete(string? filePath)
        {
            DeleteCount++;
            DeletedPath = filePath;
        }
    }

    private sealed class ThrowingFileGrantsWriter : IFileGrantsWriter
    {
        public Task<string?> WriteAsync(
            string conversationKey, IReadOnlyCollection<string> resolvedGrantPaths, CancellationToken ct) =>
            throw new System.IO.IOException("simulated grants-file write failure");

        public void Delete(string? filePath) { }
    }
}
