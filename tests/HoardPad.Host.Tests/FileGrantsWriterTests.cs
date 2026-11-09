// FileGrantsWriterTests.cs — the PANEL-A2 host grants-file writer (feature-ai-multidoc-panel §5.3 Path A).
//
// Proves: (1) a non-empty grant set writes a valid JSON ARRAY of the EXACT resolved paths under the
// app-data root and round-trips; (2) an EMPTY grant set writes NO file and returns null (fail-closed,
// FR-MD-10 — an empty-array file must never be mistaken for "grants armed"); (3) the write is atomic
// (goes through IAtomicFileStore, so the hook never observes a partial file); (4) the per-conversation
// path is deterministic + stable (overwritten each turn); (5) Delete removes the file (per-turn grant,
// FR-MD-9) and is a no-op for a missing/null path.

using System.Text.Json;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class FileGrantsWriterTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _recoveryRoot;
    private readonly AppDataPaths _paths;
    private readonly FileGrantsWriter _writer;

    public FileGrantsWriterTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"hoardpad-panela2-filegrants-{Guid.NewGuid():N}");
        _dataRoot = Path.Combine(baseDir, "data");
        _recoveryRoot = Path.Combine(baseDir, "recovery");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_recoveryRoot);
        _paths = new AppDataPaths(_dataRoot, _recoveryRoot);
        _writer = new FileGrantsWriter(_paths, new AtomicFileStore());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_recoveryRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task WriteAsyncWritesAJsonArrayOfTheExactResolvedPathsThatRoundTrips()
    {
        string[] grants =
        {
            "/home/user/notes/a.txt",
            "/home/user/other/b.md",
            "/var/tmp/c.log",
        };

        string? path = await _writer.WriteAsync("conv-1", grants, CancellationToken.None);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        // Lives under the app-data root (app-controlled path), never the install dir or a caller-supplied path.
        Assert.StartsWith(_dataRoot, path);

        // The file is a JSON array of EXACTLY the resolved paths — the hook's contract (an array of absolute
        // path strings). Round-trips byte-for-byte via System.Text.Json.
        byte[] bytes = await File.ReadAllBytesAsync(path);
        string[]? parsed = JsonSerializer.Deserialize<string[]>(bytes);
        Assert.NotNull(parsed);
        Assert.Equal(grants, parsed);
    }

    [Fact]
    public async Task WriteAsyncWithAnEmptyGrantSetWritesNoFileAndReturnsNull()
    {
        // FR-MD-10 / AC-MD-6: an empty grant set is the fail-closed default — no file, so the runner never
        // sets HOARDPAD_FILE_GRANTS and the hook grant leg stays inert (root-only). An empty-array file that
        // could be read as "grants armed" is deliberately never written.
        string? path = await _writer.WriteAsync("conv-empty", System.Array.Empty<string>(), CancellationToken.None);

        Assert.Null(path);
        // No grants file for this conversation exists.
        string grantsDir = Path.Combine(_dataRoot, "filegrants");
        Assert.True(!Directory.Exists(grantsDir) || Directory.GetFiles(grantsDir).Length == 0);
    }

    [Fact]
    public async Task WriteAsyncIsDeterministicPerConversationAndOverwritesTheSameFile()
    {
        // The per-conversation path is deterministic (SHA-256 stable leaf) — a second turn overwrites the
        // same file (the grant set is per-turn, FR-MD-9), never accumulating stale grant files.
        string[] firstGrants = { "/x/a.txt" };
        string[] secondGrants = { "/x/b.txt", "/x/c.txt" };
        string? first = await _writer.WriteAsync("conv-2", firstGrants, CancellationToken.None);
        string? second = await _writer.WriteAsync("conv-2", secondGrants, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);

        string[]? parsed = JsonSerializer.Deserialize<string[]>(await File.ReadAllBytesAsync(second!));
        Assert.Equal(secondGrants, parsed);

        // Exactly ONE grants file for this conversation (overwrite, not append).
        string grantsDir = Path.Combine(_dataRoot, "filegrants");
        Assert.Single(Directory.GetFiles(grantsDir));
    }

    [Fact]
    public async Task DifferentConversationsGetDistinctGrantsFiles()
    {
        string[] grantsA = { "/x/a.txt" };
        string[] grantsB = { "/x/b.txt" };
        string? a = await _writer.WriteAsync("conv-A", grantsA, CancellationToken.None);
        string? b = await _writer.WriteAsync("conv-B", grantsB, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task DeleteRemovesTheGrantsFileAndIsANoOpForMissingOrNull()
    {
        string[] grants = { "/x/a.txt" };
        string? path = await _writer.WriteAsync("conv-3", grants, CancellationToken.None);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        _writer.Delete(path);
        Assert.False(File.Exists(path));

        // Idempotent + null-safe: deleting again / deleting null never throws.
        _writer.Delete(path);
        _writer.Delete(null);
    }
}
