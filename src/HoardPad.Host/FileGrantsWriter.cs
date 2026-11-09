// FileGrantsWriter.cs — the host-side per-turn AI grants-file writer (PANEL-A2,
// feature-ai-multidoc-panel §5.3 Path A). The ONLY layer allowed System.IO (C-9): it serializes the
// turn's resolved grant paths to an app-controlled JSON array file the PANEL-A1 hook reads via
// HOARDPAD_FILE_GRANTS, so Archetype A (claude -p) admits exactly the same out-of-folder paths the
// Archetype-B UnionSandboxGate does.
//
// ATOMIC WRITE (mirrors CapStateStore's discipline, minus the CAS lock):
//   The write goes through IAtomicFileStore (temp -> fsync -> atomic rename), so the hook never reads a
//   partial file. Unlike the cap-state file, the grants file is NOT O_EXCL-locked: it is immutable for
//   the turn (written before launch, deleted after) and is only ever READ by the hook during the turn —
//   there is no concurrent mutation for a lock to serialize.
//
// APP-CONTROLLED PATH (deterministic per conversation):
//   The leaf is derived from a SHA-256 of the conversation id (the SAME StableFileName scheme CapStateStore
//   uses), so it is process-restart-stable and collision-safe, and lives under a "filegrants" subdir of the
//   IAppDataPaths root — the same app-owned tree the cap-state file uses. Overwritten each turn (the grant
//   set is per-turn, FR-MD-9); Delete removes it at turn end.
//
// FAIL-CLOSED (FR-MD-10 / AC-MD-6):
//   An empty grant collection writes NO file and returns null — the runner then never sets
//   HOARDPAD_FILE_GRANTS and the hook's grant leg stays inert (root-only). An empty-array file is never
//   written: it could be mistaken for "grants armed". A write fault propagates to the runner, which falls
//   back to root-only (never launches pointing at a stale/failed file).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// The production <see cref="IFileGrantsWriter"/> (feature-ai-multidoc-panel §5.3). Serializes the turn's
/// resolved grant paths to a JSON array, written atomically via <see cref="IAtomicFileStore"/> to a
/// per-conversation deterministic file under the <see cref="IAppDataPaths"/> root. Read-only to the
/// <c>deny_by_default.py</c> hook; the host is the sole writer. An empty grant set writes no file (FR-MD-10).
/// </summary>
public sealed class FileGrantsWriter : IFileGrantsWriter
{
    private readonly string _baseDirectory;
    private readonly IAtomicFileStore _atomicStore;
    private readonly IAppDataPaths _paths;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Construct the writer rooted under <paramref name="paths"/>' app-data root. The grants files live in a
    /// "filegrants" subdir there (created + user-restricted on first write), so they share the app-owned
    /// tree the cap-state file uses and never escape it.
    /// </summary>
    /// <param name="paths">The per-user data paths seam (supplies the app-data root + user restriction).</param>
    /// <param name="atomicStore">The atomic-durable write seam (temp + rename — no partial file visible).</param>
    public FileGrantsWriter(IAppDataPaths paths, IAtomicFileStore atomicStore)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _atomicStore = atomicStore ?? throw new ArgumentNullException(nameof(atomicStore));
        _baseDirectory = Path.Combine(_paths.AppDataRoot, "filegrants");
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<string?> WriteAsync(
        string conversationKey,
        IReadOnlyCollection<string> resolvedGrantPaths,
        CancellationToken ct)
    {
        ValidateConversationKey(conversationKey);
        ArgumentNullException.ThrowIfNull(resolvedGrantPaths);

        // Fail-closed: an empty grant set writes NO file (FR-MD-10). The runner reads null and leaves
        // HOARDPAD_FILE_GRANTS unset → the hook grant leg is inert → root-only. Never write an empty array.
        if (resolvedGrantPaths.Count == 0)
            return null;

        EnsureBaseDirectory();

        string path = GrantsPath(conversationKey);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(resolvedGrantPaths, JsonOptions);
        ReadOnlyMemory<byte> content = json;

        // Atomic write (temp + rename): the hook never observes a partial file. Overwrite-when-present since
        // the same conversation's grants file is re-written each turn.
        if (File.Exists(path))
            await _atomicStore.ReplaceWhenPresentAsync(path, content, ct).ConfigureAwait(false);
        else
            await _atomicStore.CreateWhenAbsentAsync(path, content, ct).ConfigureAwait(false);

        return path;
    }

    /// <inheritdoc />
    public void Delete(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return;
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException) { /* best-effort: overwritten next turn, never read outside an armed turn */ }
        catch (UnauthorizedAccessException) { /* best-effort — same rationale */ }
    }

    private void EnsureBaseDirectory()
    {
        if (Directory.Exists(_baseDirectory))
            return;
        Directory.CreateDirectory(_baseDirectory);
        _paths.RestrictToCurrentUser(_baseDirectory);
    }

    private string GrantsPath(string conversationKey) =>
        Path.Combine(_baseDirectory, StableFileName(conversationKey) + ".filegrants.json");

    private static void ValidateConversationKey(string conversationKey)
    {
        if (string.IsNullOrEmpty(conversationKey))
            throw new ArgumentException("A conversation key is required.", nameof(conversationKey));
    }

    // Deterministic, filesystem-safe, process-restart-stable leaf — the SAME scheme CapStateStore uses so a
    // conversation always maps to the same grants file (SHA-256, not the per-process-randomized GetHashCode).
    private static string StableFileName(string conversationKey)
    {
        var sb = new StringBuilder(conversationKey.Length + 9);
        foreach (char c in conversationKey)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        sb.Append('-');

        byte[] idBytes = Utf8NoBom.GetBytes(conversationKey);
        byte[] hash = SHA256.HashData(idBytes);
        sb.Append(Convert.ToHexString(hash, 0, 4).ToLowerInvariant());
        return sb.ToString();
    }
}
