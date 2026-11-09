// RecoveryFileSystem.cs — the production IRecoveryFileSystem (PRD §9.2 dimension-3).
//
// Reads the real filesystem OFF the UI thread for the §9.2 reconciliation: it hashes the on-disk file
// at a tab path (hash is authoritative — never skipped on mtime alone for the restore-conflict
// decision). For a `kind: hoard` tab the hash is over the CANONICAL-parsed content (couples to MA2) so
// a whitespace/escaping-only reserialization by another build does not flip the baseline and raise a
// spurious conflict. For a `kind: file` tab the hash is over raw bytes (§9.2).
//
// It owns no atomic-write syscalls (that is IAtomicFileStore); it only reads. SHA-256 hex is the hash
// form (the snapshot's baselineContentHash uses the same form, written by SessionStore).

using System.Security.Cryptography;
using System.Text;
using HoardPad.Core.Checklist;
using HoardPad.Core.Recovery;

namespace HoardPad.App.Session;

/// <summary>
/// Production <see cref="IRecoveryFileSystem"/>: the real off-UI on-disk probe. Hash is authoritative;
/// <c>.hoard</c> hashing is canonical.
/// </summary>
internal sealed class RecoveryFileSystem : IRecoveryFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Compute the SHA-256 hex hash of a content byte span (the snapshot baseline + dimension-3 form).</summary>
    public static string HashHex(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    /// Canonical-parse a <c>.hoard</c> JSON string and re-serialize it to its canonical form, then hash
    /// (couples to MA2). A parse failure falls back to raw-bytes hashing — a corrupt file is "changed"
    /// either way, and a non-canonicalizable file must not crash the restore reconcile.
    /// </summary>
    public static string CanonicalHoardHashHex(byte[] rawBytes)
    {
        string json = Utf8NoBom.GetString(StripBom(rawBytes));
        HoardLoadResult result = HoardLoader.Load(json);
        if (result.Document is not { } document)
            return HashHex(rawBytes); // rejected/corrupt → compare raw (it's "changed" regardless).

        string canonical = HoardSerializer.Serialize(document);
        return HashHex(Utf8NoBom.GetBytes(canonical));
    }

    public async Task<CurrentDiskFile> ProbeAsync(string path, TabKind kind, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new CurrentDiskFile(Exists: false, ContentHash: null, Mtime: null);

        DateTimeOffset mtime = File.GetLastWriteTimeUtc(path);
        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        // Hash is authoritative for the restore-conflict decision (§9.2 / FR-28(ii)) — always compute it
        // (no mtime-skip here; the skip optimization is for the steady-state file watcher, not the
        // crash-restart reconcile where a false-unchanged verdict would arm a clobbering Save).
        string hash = kind == TabKind.Hoard
            ? CanonicalHoardHashHex(bytes)   // canonical-parsed — no spurious whitespace conflicts.
            : HashHex(bytes);                // raw bytes (§9.2 — file tabs are byte-compared).

        return new CurrentDiskFile(Exists: true, ContentHash: hash, Mtime: mtime);
    }

    private static ReadOnlySpan<byte> StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsSpan(3)
            : bytes;
}
