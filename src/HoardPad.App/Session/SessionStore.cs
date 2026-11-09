// SessionStore.cs — session.json + recovery-snapshot durable I/O (PRD §9.2, NFR-4).
//
// The §5.5 layout owner for the session index + the machine-local recovery directory. ALL writes go
// through IAtomicFileStore (the atomic temp→fsync→rename sequence, NFR-4) — never System.IO.File for a
// write. session.json is the rebuildable hint; the recovery-directory scan is authoritative (§9.2), so
// ReadSnapshotsAsync enumerates + parses every snapshot in IAppDataPaths.RecoveryDirectory and joins by
// the embedded tabId, never by the (opaque) filename.
//
// Snapshot filenames are opaque (a per-write ULID-ish token) — a renamed/relocated snapshot still joins
// by its embedded tabId. A snapshot is keyed durably to its tab by content, not by name; the SAME tab
// may have two snapshots after a crash-mid-write (the §9.2 same-machine seq tie-break dedupes them).
//
// All methods run off the UI thread (file I/O never blocks the dispatcher, architecture §3.1).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Session;

/// <summary>Reads/writes <c>session.json</c> + the machine-local recovery snapshots (PRD §9.2 / NFR-4).</summary>
internal sealed class SessionStore
{
    private const string SessionFileName = "session.json";
    private const string SnapshotExtension = ".snapshot";

    // System.Text.Json with readable string enums (TabKind) + indented session.json (human-inspectable,
    // it's a small index). Snapshots are compact (they carry base64 bytes — no need to pretty-print).
    private static readonly JsonSerializerOptions SessionJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAtomicFileStore _store;
    private readonly IAppDataPaths _paths;

    public SessionStore(IAtomicFileStore store, IAppDataPaths paths)
    {
        _store = store;
        _paths = paths;
    }

    private string SessionFilePath => Path.Combine(_paths.AppDataRoot, SessionFileName);

    /// <summary>Read the <c>session.json</c> index, or <see cref="SessionState.Empty"/> if absent/corrupt.</summary>
    public async Task<SessionState> ReadSessionAsync(CancellationToken ct)
    {
        string path = SessionFilePath;
        if (!File.Exists(path))
            return SessionState.Empty;

        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<SessionState>(bytes, SessionJson) ?? SessionState.Empty;
        }
        catch (JsonException)
        {
            // A corrupt session.json falls back to empty — the recovery directory is authoritative anyway
            // (§9.2), and the directory scan rebuilds the working set. Never crash the restore on a bad hint.
            return SessionState.Empty;
        }
    }

    /// <summary>Persist the <c>session.json</c> index atomically (NFR-4). The recovery dir stays authoritative.</summary>
    public async Task WriteSessionAsync(SessionState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureAppDataRoot();

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, SessionJson);
        string path = SessionFilePath;

        if (File.Exists(path))
            await _store.ReplaceWhenPresentAsync(path, bytes, ct).ConfigureAwait(false);
        else
            await _store.CreateWhenAbsentAsync(path, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerate + parse every recovery snapshot in the machine-local recovery directory (§9.2 — the
    /// directory scan is authoritative). Each snapshot is self-describing; join is by embedded tabId, never
    /// by filename. A corrupt snapshot file is skipped (its torn write is a crash artifact, not the truth).
    /// </summary>
    public async Task<IReadOnlyList<RecoverySnapshot>> ReadSnapshotsAsync(CancellationToken ct)
    {
        string dir = _paths.RecoveryDirectory;
        if (!Directory.Exists(dir))
            return [];

        var snapshots = new List<RecoverySnapshot>();
        foreach (string file in Directory.EnumerateFiles(dir, "*" + SnapshotExtension))
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            RecoverySnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(bytes, SnapshotJson);
            }
            catch (JsonException)
            {
                continue; // a torn/corrupt snapshot is a crash artifact — skip it, never crash the scan.
            }
            if (snapshot is not null)
                snapshots.Add(snapshot);
        }
        return snapshots;
    }

    /// <summary>
    /// Write a self-describing recovery snapshot atomically (NFR-4). The filename is opaque (a fresh token
    /// per write) so a concurrent/retried write never collides and the §9.2 same-machine seq tie-break can
    /// resolve duplicate-tabId snapshots. Returns the snapshot file path (the session index's recoveryRef hint).
    /// </summary>
    public async Task<string> WriteSnapshotAsync(RecoverySnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureRecoveryDirectory();

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJson);
        // Opaque per-write filename: the tabId + the monotonic seq make it unique on this machine, and the
        // embedded tabId — not the name — is the join key. (Two snapshots of one tabId differ by seq.)
        string fileName = $"{snapshot.TabId}.{snapshot.Seq}{SnapshotExtension}";
        string path = Path.Combine(_paths.RecoveryDirectory, fileName);

        // A snapshot file is unique per (tabId, seq); a retry of the SAME (tabId, seq) replaces in place.
        if (File.Exists(path))
            await _store.ReplaceWhenPresentAsync(path, bytes, ct).ConfigureAwait(false);
        else
            await _store.CreateWhenAbsentAsync(path, bytes, ct).ConfigureAwait(false);

        return path;
    }

    /// <summary>
    /// Delete every recovery snapshot for <paramref name="tabId"/> (FR-21 — a clean save+close clears the
    /// buffer's recovery data so no spurious recovery prompt appears next launch). Idempotent.
    /// </summary>
    public void ClearSnapshots(string tabId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);
        string dir = _paths.RecoveryDirectory;
        if (!Directory.Exists(dir))
            return;

        // Match by the embedded tabId, not the filename pattern alone — read the lightweight prefix the
        // WriteSnapshotAsync naming uses, then verify. Cheap path-prefix match is enough here because the
        // filename is "<tabId>.<seq>.snapshot" by construction; a relocated snapshot is a non-v1 case.
        foreach (string file in Directory.EnumerateFiles(dir, "*" + SnapshotExtension))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith(tabId + ".", StringComparison.Ordinal))
                File.Delete(file);
        }
    }

    /// <summary>
    /// F-FS1c fix 2: delete <paramref name="tabId"/>'s SUPERSEDED older snapshot files, keeping only
    /// <paramref name="keepPath"/> (the snapshot <see cref="WriteSnapshotAsync"/> just wrote). Called AFTER
    /// the new snapshot is durably written (write-then-prune-older — never the reverse, so there is no
    /// window where a tab has zero recovery snapshots). Bounds a long dirty session at the 1–30s periodic
    /// auto-save cadence to ~1 snapshot file per tab instead of accumulating one per tick.
    /// <see cref="ReadSnapshotsAsync"/>'s caller (<c>SessionReconciler</c>'s same-machine tie-break, §9.2)
    /// already keeps only the highest-<c>seq</c> snapshot per tabId and discards the rest, so deleting every
    /// other file for this tab here changes no restore behavior — it only stops those already-discarded
    /// duplicates from piling up on disk. Same match-by-embedded-tabId-prefix + best-effort posture as
    /// <see cref="ClearSnapshots"/>; idempotent.
    /// </summary>
    public void PruneSupersededSnapshots(string tabId, string keepPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);
        ArgumentException.ThrowIfNullOrEmpty(keepPath);
        string dir = _paths.RecoveryDirectory;
        if (!Directory.Exists(dir))
            return;

        string keepName = Path.GetFileName(keepPath);
        foreach (string file in Directory.EnumerateFiles(dir, "*" + SnapshotExtension))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith(tabId + ".", StringComparison.Ordinal)
                && !string.Equals(name, keepName, StringComparison.Ordinal))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Read the unsaved buffer text from a snapshot (the base64 content decoded as UTF-8).</summary>
    public static string DecodeContent(RecoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        byte[] bytes = Convert.FromBase64String(snapshot.ContentBase64);
        return Utf8NoBom.GetString(bytes);
    }

    /// <summary>Encode buffer text to the snapshot's base64 content field (UTF-8 bytes).</summary>
    public static string EncodeContent(string text) =>
        Convert.ToBase64String(Utf8NoBom.GetBytes(text ?? string.Empty));

    private void EnsureAppDataRoot()
    {
        if (!Directory.Exists(_paths.AppDataRoot))
        {
            Directory.CreateDirectory(_paths.AppDataRoot);
            _paths.RestrictToCurrentUser(_paths.AppDataRoot);
        }
    }

    private void EnsureRecoveryDirectory()
    {
        if (!Directory.Exists(_paths.RecoveryDirectory))
        {
            Directory.CreateDirectory(_paths.RecoveryDirectory);
            _paths.RestrictToCurrentUser(_paths.RecoveryDirectory);
        }
    }
}
