// SessionTestSupport.cs — fakes for the M6.3 session/recovery + restore-conflict + lazy-folder gates.
//
//   - FakeRecoveryFileSystem: a deterministic IRecoveryFileSystem — the on-disk probe (dimension 3) is
//     scripted, so the §9.2 reconciliation drive runs without touching the real filesystem for the
//     target-file dimension.
//   - ScriptedRestoreConflictPrompt: records the files it was asked about and returns a scripted choice.
//   - ScriptedOpenFolderPrompt: records the re-prompt calls and returns a scripted root (or null = decline).
//   - TempAppDataPaths: a real-temp-dir IAppDataPaths so SessionStore writes session.json + snapshots to a
//     throwaway directory (the recovery-dir scan is exercised against real atomic writes via the store).
//   - RealAtomicFileStore: a faithful IAtomicFileStore that writes real bytes to disk (the session/recovery
//     I/O needs real files for the directory scan). Distinct from CountingAtomicFileStore (in-memory).

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Session;
using HoardPad.App.Views;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Recovery;

namespace HoardPad.App.Tests;

/// <summary>A deterministic <see cref="IRecoveryFileSystem"/>: scripted dimension-3 probes.</summary>
internal sealed class FakeRecoveryFileSystem : IRecoveryFileSystem
{
    private readonly Dictionary<string, CurrentDiskFile> _probes = new();

    /// <summary>Script the dimension-3 probe result for a path.</summary>
    public void SetProbe(string path, CurrentDiskFile result) => _probes[path] = result;

    public Task<CurrentDiskFile> ProbeAsync(string path, TabKind kind, CancellationToken ct) =>
        Task.FromResult(_probes.TryGetValue(path, out var r)
            ? r
            : new CurrentDiskFile(Exists: false, ContentHash: null, Mtime: null));
}

/// <summary>A restore-conflict prompt that records the files asked + returns a scripted choice.</summary>
internal sealed class ScriptedRestoreConflictPrompt : IRestoreConflictPrompt
{
    private readonly RestoreConflictChoice _choice;
    public List<string> AskedFiles { get; } = [];

    public ScriptedRestoreConflictPrompt(RestoreConflictChoice choice) => _choice = choice;

    public Task<RestoreConflictChoice> AskAsync(string fileName, CancellationToken ct)
    {
        AskedFiles.Add(fileName);
        return Task.FromResult(_choice);
    }
}

/// <summary>
/// FS-1: a stub "My Documents" resolver for tests that never reach tier (iii) — returns a fixed,
/// non-existent placeholder path so a test that accidentally hits tier (iii) fails loudly (e.g. a bind
/// against a non-existent root) rather than silently touching the real environment/filesystem.
/// </summary>
internal static class StubMyDocuments
{
    public static string Resolve() => "/hoardpad-test-should-not-resolve-here/Documents";
}

/// <summary>An MA5 re-prompt that records calls + returns a scripted root (null = the user declined).</summary>
internal sealed class ScriptedOpenFolderPrompt : IOpenFolderPrompt
{
    private readonly string? _root;
    public int CallCount { get; private set; }
    public string? LastPriorRoot { get; private set; }

    public ScriptedOpenFolderPrompt(string? root) => _root = root;

    public Task<string?> RePromptForRootAsync(string? priorRoot, CancellationToken ct)
    {
        CallCount++;
        LastPriorRoot = priorRoot;
        return Task.FromResult(_root);
    }
}

/// <summary>An <see cref="IAppDataPaths"/> rooted at a real throwaway temp directory for the session store.</summary>
internal sealed class TempAppDataPaths : IAppDataPaths, System.IDisposable
{
    public TempAppDataPaths()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "hoardpad-session-test-" + Guid.NewGuid().ToString("N"));
        AppDataRoot = Path.Combine(baseDir, "appdata");
        RecoveryDirectory = Path.Combine(baseDir, "recovery");
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(RecoveryDirectory);
        _baseDir = baseDir;
    }

    private readonly string _baseDir;

    public string AppDataRoot { get; }
    public string RecoveryDirectory { get; }
    public string SettingsFilePath => Path.Combine(AppDataRoot, "settings.json");
    public string ActivityLogPath(string sessionId) => Path.Combine(AppDataRoot, "activity", $"{sessionId}.log");
    public string UndoJournalRoot => Path.Combine(AppDataRoot, "undo");
    public void RestrictToCurrentUser(string path) { }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}

/// <summary>A faithful disk-backed <see cref="IAtomicFileStore"/> for the session/recovery I/O tests.</summary>
internal sealed class RealAtomicFileStore : IAtomicFileStore
{
    public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        return File.WriteAllBytesAsync(destPath, content.ToArray(), ct);
    }

    public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        return File.WriteAllBytesAsync(destPath, content.ToArray(), ct);
    }

    public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        Task.FromResult(0L);

    public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct) =>
        Task.FromResult(new AppendLogRepairResult([], 0, false));

    public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct) =>
        Task.FromResult(AtomicWriteCapability.Atomic);
}

/// <summary>
/// A test helper for the M11 hook-observation channel the <see cref="HoardPad.App.Session.AiSessionBinder"/>
/// now requires. A real <see cref="HoardPad.Host.HookObservationChannel"/> with a deny confirm + a scratch
/// runtime dir — the binder only calls ResolveChannelAddress on it (no socket is opened in these tests).
/// </summary>
internal static class TestObservationChannelFactory
{
    public static HoardPad.Host.HookObservationChannel Create() =>
        new(
            confirm: (req, _) => Task.FromResult(HookDecisionResponse.Deny(req.ToolUseId)),
            runtimeDir: System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hoardpad-test-observe-{System.Guid.NewGuid():N}"));
}

/// <summary>
/// An <see cref="IAgentBackend"/> that CAPTURES the env passed to <see cref="StartTurnAsync"/> and returns
/// a turn with an empty stream (so a driven turn completes immediately, no real <c>claude</c>). The M11
/// launch-env seam tests use it to assert the host-built env reaches the launch seam verbatim.
/// </summary>
internal sealed class CapturingAgentBackend : IAgentBackend
{
    public IReadOnlyDictionary<string, string>? CapturedEnv { get; private set; }

    public LaunchDescriptor ResolveLauncher(string? configuredPath) =>
        new("/usr/bin/claude", IsCmd: false, FromPath: true, ResolvedVia: "capture");

    public Task<IAgentTurn> StartTurnAsync(
        LaunchDescriptor launcher, AgentTurnRequest request,
        IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        CapturedEnv = env;
        return Task.FromResult<IAgentTurn>(new EmptyAgentTurn());
    }

    private sealed class EmptyAgentTurn : IAgentTurn
    {
        public async System.Collections.Generic.IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task KillTreeAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
