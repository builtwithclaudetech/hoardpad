// CapStateStore.cs — the O_EXCL-locked, conversation-keyed cap-state store (M5.2).
//
// This is the host half of architecture §4.4: the durable persistence of the pure
// HoardPad.Core.Ai CapState (M4.1) under a conversation id, behind the ICapStateStore
// seam. The cap-state SHAPE + the increment/no-re-arm/trip logic are pure Core.Ai; THIS
// owns ONLY the locked file I/O. It is the production port of the SPIKE-4 O_EXCL SpinLock
// (the SAME lock M5.1's deny_by_default.py uses), with a compare-and-swap on a version
// token so a continuation turn and a fresh invocation cannot drop an increment (the
// R5-BL1 re-arm failure the conversation-keyed lifetime exists to prevent).
//
// LOCK (SPIKE-4 / TD §4.8, MIRRORS M5.1's SpinLock EXACTLY):
//   atomic O_CREAT|O_EXCL (POSIX) / CREATE_NEW (Win32) on "<payload>.lock"; 5 ms spin;
//   10 s hard timeout; unlinked in finally. NEVER fcntl (advisory + no Windows equivalent
//   — the spike rejected it). FileMode.CreateNew gives O_CREAT|O_EXCL / CREATE_NEW on both
//   OSes, so one managed primitive covers both RIDs with no P/Invoke.
//
// PAYLOAD PERSISTED VERBATIM (the M5.1 hook contract):
//   The M4.3 payload string is the PascalCase System.Text.Json of CapState; the M5.1
//   Python hook reads THIS file as PascalCase JSON (CreateCount / DestructiveCount /
//   FilesTouched / *CapTripped). We persist that string BYTE-FOR-BYTE — no re-serialize,
//   no transform. The optimistic-concurrency Version is kept in a SEPARATE sidecar file
//   so the payload file stays exactly the bytes the hook expects (a {version,payload}
//   wrapper would change the payload bytes and break the cross-language contract).
//
// CAS (the no-lost-update guarantee):
//   ReadAsync -> (payloadJson, version). WriteAsync(payload, expectedVersion) takes the
//   lock, re-reads the CURRENT version, and writes ONLY if it still equals expectedVersion;
//   a mismatch returns Conflict(currentVersion) and the caller re-reads. Because the
//   read-of-version, the compare, AND the durable writes ALL happen UNDER THE SAME LOCK
//   ACQUISITION, two writers racing the same conversation serialize perfectly: the loser
//   sees the winner's bumped version and gets a Conflict instead of clobbering the increment.
//   There is no gap between the compare and the write — they share one lock hold.
//
// STABLE FILENAMES (cross-process safety):
//   The per-conversation file name is derived from a SHA-256 of the UTF-8 conversation-id
//   bytes (first 8 hex chars). .NET's string.GetHashCode() is per-process randomized; using
//   it would produce different filenames across HoardPad restarts, preventing a continuation
//   turn from finding prior cap-state (which would silently re-arm a tripped cap). SHA-256 is
//   stable across processes, OS reboots, and .NET versions.

using System.Security.Cryptography;
using System.Text;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// The <c>O_EXCL</c>-locked, conversation-keyed cap-state store (architecture §4.4).
/// Implements <see cref="ICapStateStore"/>. Persists the M4.3 PascalCase payload string
/// VERBATIM (the M5.1 hook reads it); the optimistic-concurrency version is a separate
/// sidecar so the payload bytes are never transformed. <see cref="WriteAsync"/> is a
/// compare-and-swap under the lock — a stale <c>expectedVersion</c> yields a conflict, never
/// a lost update. File names are SHA-256 stable across process restarts.
/// </summary>
public sealed class CapStateStore : ICapStateStore
{
    private const int RetrySleepMs = 5;          // 5 ms spin between lock retries (SPIKE-4).
    private const int HardTimeoutMs = 10_000;    // 10 s hard timeout (the CLI kills the hook long before).

    private readonly string _baseDirectory;
    private readonly IAtomicFileStore _atomicStore;

    /// <summary>
    /// Construct a store rooted at <paramref name="baseDirectory"/> (in production, a subdir
    /// of <c>IAppDataPaths.AppDataRoot</c>; in tests, a temp dir). Each conversation's payload
    /// + version live in two files keyed by the conversation id under this directory.
    /// </summary>
    /// <param name="atomicStore">The M2.1 atomic-durable write seam used for both files.</param>
    public CapStateStore(string baseDirectory, IAtomicFileStore atomicStore)
    {
        if (string.IsNullOrEmpty(baseDirectory))
            throw new ArgumentException("A cap-state base directory is required.", nameof(baseDirectory));
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _atomicStore = atomicStore ?? throw new ArgumentNullException(nameof(atomicStore));
        Directory.CreateDirectory(_baseDirectory);
    }

    public Task<CapStateSnapshot> ReadAsync(string conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateConversationId(conversationId);

        string payloadPath = PayloadPath(conversationId);
        string versionPath = VersionPath(conversationId);
        string lockPath = LockPath(conversationId);

        // The read is taken UNDER THE LOCK too: a concurrent WriteAsync replaces the payload
        // and version files in two steps, and reading mid-replacement could pair a new payload
        // with a stale version (or vice-versa). Holding the lock makes the read see a single
        // consistent (payload, version) pair.
        return Task.FromResult(WithLock(lockPath, () =>
        {
            string? payload = File.Exists(payloadPath) ? File.ReadAllText(payloadPath, Utf8NoBom) : null;
            long version = ReadVersion(versionPath);
            return new CapStateSnapshot(payload, version);
        }, ct));
    }

    public async Task<CapStateWriteResult> WriteAsync(
        string conversationId, string payloadJson, long expectedVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateConversationId(conversationId);
        ArgumentNullException.ThrowIfNull(payloadJson);

        string payloadPath = PayloadPath(conversationId);
        string versionPath = VersionPath(conversationId);
        string lockPath = LockPath(conversationId);

        // The compare-and-swap. The version-read, the compare, AND the durable writes ALL happen
        // under a SINGLE lock acquisition — there is no gap between compare and write. A racing
        // writer cannot slip between our compare and our write: it will block on the lock and when
        // it acquires it will read the already-advanced version and receive a Conflict.
        CapStateWriteResult result = CapStateWriteResult.Conflict(0);
        await WithLockAsync(lockPath, async () =>
        {
            long current = ReadVersion(versionPath);
            if (current != expectedVersion)
            {
                // Stale expectedVersion: another writer already advanced the store. Refuse
                // and report the current version so the caller re-reads and re-applies — the
                // increment is never silently clobbered (no lost update).
                result = CapStateWriteResult.Conflict(current);
                return;
            }

            long next = current + 1;
            // The payload is persisted VERBATIM — exactly the M4.3 PascalCase bytes the M5.1
            // hook reads. The version is a separate sidecar so the payload file is untouched.
            ReadOnlyMemory<byte> payloadBytes = Utf8NoBom.GetBytes(payloadJson);
            ReadOnlyMemory<byte> versionBytes =
                Utf8NoBom.GetBytes(next.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Write the payload first, then the version. A crash BETWEEN the two leaves the
            // payload advanced but the version stale; the next writer's compare then sees the
            // stale version and re-writes (idempotent — the payload is the source of truth and
            // is re-derived from CapState each turn). The version monotonically advancing is the
            // CAS token, not a durability anchor for the payload itself.
            await WriteAtomicAsync(payloadPath, payloadBytes, ct).ConfigureAwait(false);
            await WriteAtomicAsync(versionPath, versionBytes, ct).ConfigureAwait(false);

            result = CapStateWriteResult.Ok(next);
        }, ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// The EXACT payload-file path this store reads/writes for <paramref name="conversationId"/> — the
    /// SAME path <see cref="ReadAsync"/> / <see cref="WriteAsync"/> use (it just exposes the existing
    /// <see cref="PayloadPath"/>). Pure + read-only: it computes the leaf, opens nothing, takes no lock,
    /// and the O_EXCL CAS write path is byte-untouched. The M11 launch-env seam injects this as the hook's
    /// <c>HOARDPAD_CAP_STATE_FILE</c>; the hook locks the <c>&lt;path&gt;.lock</c> sibling — which is
    /// exactly this store's <see cref="LockPath"/> (<c>.capstate.lock</c>) — so the hook reads and locks
    /// the SAME file the spine writes (invariant 4).
    /// </summary>
    public string ResolveCapStateFilePath(string conversationId)
    {
        ValidateConversationId(conversationId);
        return PayloadPath(conversationId);
    }

    // ---- atomic write (create-when-absent vs replace-when-present) ----

    private async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (File.Exists(path))
            await _atomicStore.ReplaceWhenPresentAsync(path, content, ct).ConfigureAwait(false);
        else
            await _atomicStore.CreateWhenAbsentAsync(path, content, ct).ConfigureAwait(false);
    }

    // ---- the SPIKE-4 O_EXCL SpinLock (mirrors M5.1's SpinLock; never fcntl) ----

    private static T WithLock<T>(string lockPath, Func<T> body, CancellationToken ct)
    {
        AcquireLock(lockPath, ct);
        try { return body(); }
        finally { ReleaseLock(lockPath); }
    }

    private static async Task WithLockAsync(string lockPath, Func<Task> body, CancellationToken ct)
    {
        AcquireLock(lockPath, ct);
        try { await body().ConfigureAwait(false); }
        finally { ReleaseLock(lockPath); }
    }

    private static void AcquireLock(string lockPath, CancellationToken ct)
    {
        // FileMode.CreateNew == O_CREAT|O_EXCL (POSIX) / CREATE_NEW (Win32): an atomic
        // exclusive create that throws IOException if the lock file already exists. We open,
        // immediately close (the file's EXISTENCE is the lock, not an open handle — matching
        // M5.1's os.open(...).close-on-release shape), and unlink it on release in finally.
        long deadline = Environment.TickCount64 + HardTimeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using FileStream fs = new(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return;   // acquired — the lock file now exists; ReleaseLock unlinks it.
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                if (Environment.TickCount64 > deadline)
                    throw new TimeoutException(
                        $"SpinLock timeout acquiring '{lockPath}' after {HardTimeoutMs} ms (the cap-state lock is "
                        + "held; the CLI kills the hook long before this floor — a timeout here means a stuck lock).");
                Thread.Sleep(RetrySleepMs);
            }
        }
    }

    private static void ReleaseLock(string lockPath)
    {
        try { if (File.Exists(lockPath)) File.Delete(lockPath); }
        catch { /* best-effort unlink, mirroring M5.1's FileNotFoundError pass */ }
    }

    // ---- helpers ----

    /// <summary>
    /// Read the version sidecar. Returns 0 for a missing file (new conversation). Throws for a
    /// present-but-unparseable file (torn write) — the caller MUST NOT commit a write with
    /// expectedVersion:0 against a non-empty payload because the file may have been corrupted.
    /// </summary>
    private static long ReadVersion(string versionPath)
    {
        if (!File.Exists(versionPath))
            return 0L;   // never-persisted conversation — valid new.

        // PRESENT-BUT-UNPARSEABLE: treat as a torn write, fail-closed.
        string raw = File.ReadAllText(versionPath, Utf8NoBom).Trim();
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long v))
            return v;

        throw new InvalidDataException(
            $"The cap-state version file '{versionPath}' exists but could not be parsed as a version number "
            + $"(content: '{raw}'). This indicates a torn write. The store fails closed to prevent committing "
            + "a stale CAS write that could re-arm a tripped cap. Delete the corrupt file to reset this conversation.");
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private string PayloadPath(string conversationId) =>
        Path.Combine(_baseDirectory, StableFileName(conversationId) + ".capstate");

    private string VersionPath(string conversationId) =>
        Path.Combine(_baseDirectory, StableFileName(conversationId) + ".capstate.ver");

    private string LockPath(string conversationId) =>
        Path.Combine(_baseDirectory, StableFileName(conversationId) + ".capstate.lock");

    private static void ValidateConversationId(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
            throw new ArgumentException("A conversation id is required.", nameof(conversationId));
    }

    /// <summary>
    /// Derive a stable, filesystem-safe leaf from the conversation id.
    /// Uses a SHA-256 of the UTF-8 bytes (first 8 hex chars = 32 bits of spread) as the
    /// disambiguation suffix — unlike <c>string.GetHashCode()</c>, SHA-256 is deterministic
    /// across processes, OS reboots, and .NET versions, so a HoardPad restart always maps a
    /// conversation id to the same file name (preventing silent cap re-arm on continuation).
    /// </summary>
    internal static string StableFileName(string conversationId)
    {
        var sb = new StringBuilder(conversationId.Length + 9);
        foreach (char c in conversationId)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        sb.Append('-');

        // SHA-256 over the UTF-8 bytes of the conversation id: stable across processes.
        byte[] idBytes = Utf8NoBom.GetBytes(conversationId);
        byte[] hash = SHA256.HashData(idBytes);
        // First 4 bytes (8 hex chars): enough bits to avoid collisions across typical conversation counts.
        sb.Append(Convert.ToHexString(hash, 0, 4).ToLowerInvariant());
        return sb.ToString();
    }
}
