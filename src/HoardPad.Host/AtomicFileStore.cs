// AtomicFileStore.cs — the NFR-4 atomic-durable-write seam (M2.1).
//
// This is the productionized form of the SPIKE-5 durability harness
// (spikes/spike-5/Spike5.DurabilityLib). It is the ONLY place in HoardPad that
// touches fsync / rename(2) / MoveFileEx / ReplaceFile (C-9 / architecture §1.3).
//
// Frame layout per append-log record (all integers little-endian) — TD §3.3, SPIKE-5:
//   [4 bytes]  uint32 payload length (LE)
//   [N bytes]  payload (UTF-8 JSON, no trailing newline)
//   [4 bytes]  uint32 CRC-32/ISO-HDLC of the payload bytes only (LE)
//   => 8 bytes per-record overhead.
//
// Durability rules (§9.3 a/b/c):
//   (a) per-record fsync before the next append (Flush(flushToDisk: true));
//   (b) on-open truncate-repair to the last validated record's end offset;
//   (c) trailing-record-only recovery (StopAtFirstBad) is correct on a
//       write-ordering FS; ResyncAndContinue is the R4-mi4 non-ordering-FS fallback.
//
// Atomic-write sequence (TD §2.1, SPIKE-5 §(b)):
//   Linux:   same-dir temp -> fsync(temp data) -> rename(2) -> fsync(containing dir).
//   Windows: same-dir temp -> fsync(temp data) ->
//              create : MoveFileEx(temp, dest, MOVEFILE_WRITE_THROUGH)   [fails if dest exists]
//              replace: ReplaceFile(dest, temp, REPLACEFILE_WRITE_THROUGH).
//   The Windows path COMPILES for win-x64; its runtime power-cut assertion is
//   DEFERRED to real Windows hardware (TD §6 / SPIKE-5 non-confirmation 1) and is
//   never executed on this Linux box.

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// Atomic, durable file writes per NFR-4. Implements <see cref="IAtomicFileStore"/>.
/// Linux uses temp -&gt; fsync -&gt; rename -&gt; dir-fsync; Windows uses
/// MoveFileEx/ReplaceFile with WRITE_THROUGH (compiled, runtime deferred).
/// </summary>
public sealed class AtomicFileStore : IAtomicFileStore
{
    // ---- Frame constants (SPIKE-5 / TD §3.3) ----
    private const int LengthPrefixSize = 4;   // uint32 LE
    private const int ChecksumSize = 4;       // uint32 CRC-32/ISO-HDLC LE
    private const int RecordOverhead = LengthPrefixSize + ChecksumSize;

    // Sanity cap on a single record's payload length. A torn length field could
    // otherwise ask us to allocate gigabytes; cap at 64 MB (SPIKE-5 B1).
    private const uint MaxPayloadLen = 64 * 1024 * 1024;

    // ---- Atomic whole-file writes ----

    public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AtomicWrite(destPath, content.Span, overwrite: false);
        return Task.CompletedTask;
    }

    public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AtomicWrite(destPath, content.Span, overwrite: true);
        return Task.CompletedTask;
    }

    private static void AtomicWrite(string destPath, ReadOnlySpan<byte> content, bool overwrite)
    {
        string fullDest = Path.GetFullPath(destPath);
        string dir = Path.GetDirectoryName(fullDest)
            ?? throw new ArgumentException("Could not determine directory.", nameof(destPath));
        string temp = Path.Combine(dir, $".hoard-tmp-{Guid.NewGuid():N}");

        // The entire post-CreateNew sequence (write, fsync, rename) is inside one try
        // so that ANY failure — including ENOSPC/EIO during Write or Flush — triggers
        // BestEffortDeleteTemp. Without this, a failed Flush disposes the stream and
        // propagates but never reaches cleanup, orphaning the temp in the user's folder
        // (the NFR-4 failure mode we must never hit — W1).
        bool renamed = false;
        try
        {
            // Step 1+2: write the temp file in the SAME directory, then fsync its data.
            // FileMode.CreateNew => O_CREAT|O_EXCL (exclusive create) for the temp name.
            using (FileStream fs = new(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 65536, FileOptions.None))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);   // fsync(temp data)
            }

            // Step 3: atomic rename onto the destination.
            if (OperatingSystem.IsWindows())
            {
                WindowsAtomicRename(temp, fullDest, overwrite);
            }
            else
            {
                // POSIX rename(2): atomic same-FS create or replace.
                // overwrite:false fails (IOException) when dest already exists —
                // the create-when-absent contract; overwrite:true is rename-over.
                File.Move(temp, fullDest, overwrite);
                FsyncDirectory(dir);   // make the new/replaced dir entry durable
            }
            renamed = true;
        }
        catch (IOException ex) when (CrossDeviceRename(ex))
        {
            // TODO(carry-forward): throw new AtomicWriteUnavailable(destPath, ex.Message, ex)
            // once a later Core-touching milestone gives AtomicWriteUnavailable a
            // (path, reason, inner) ctor — the Core type currently has no such members.
            throw new AtomicWriteUnavailable();   // caller surfaces the NFR-4 warning
        }
        finally
        {
            // Delete the temp only if the rename didn't consume it. Covers every
            // failure path: write error, fsync error, rename error, cross-device.
            if (!renamed)
                BestEffortDeleteTemp(temp);
        }
    }

    // ---- Append-log: durable per-record append ----

    public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Write-side cap guard: a record whose length exceeds MaxPayloadLen would be
        // written, fsync'd, and assigned a valid offset, then silently dropped by
        // OpenAndRepairAsync's read-side cap check — committed data, silent drop.
        // Rejecting here closes the asymmetry before the write ever lands on disk.
        if ((uint)payload.Length > MaxPayloadLen)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Payload length {payload.Length} exceeds the {MaxPayloadLen}-byte per-record cap (SPIKE-5 B1).");

        ReadOnlySpan<byte> payloadSpan = payload.Span;
        byte[] frame = new byte[RecordOverhead + payloadSpan.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, LengthPrefixSize), (uint)payloadSpan.Length);
        payloadSpan.CopyTo(frame.AsSpan(LengthPrefixSize, payloadSpan.Length));
        // Crc32.Hash emits the CRC-32/ISO-HDLC bytes in little-endian order, which
        // is exactly the LE checksum the frame stores — copy them in verbatim.
        Crc32.Hash(payloadSpan, frame.AsSpan(LengthPrefixSize + payloadSpan.Length, ChecksumSize));

        using FileStream fs = new(logPath, FileMode.Append, FileAccess.Write,
            FileShare.None, bufferSize: 65536, FileOptions.None);
        fs.Write(frame);
        fs.Flush(flushToDisk: true);   // per-record fsync BEFORE returning (§9.3-a)
        // fs.Length is the true new end offset only because FileShare.None guarantees
        // exclusive access — no concurrent writer can have extended the file between
        // our Write and this Length read. Any future FileShare loosening must revisit this.
        return Task.FromResult(fs.Length);
    }

    // ---- Append-log: on-open truncate-repair ----

    public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var (records, validEnd) = ReadAll(logPath, mode);

        long fileLen = File.Exists(logPath) ? new FileInfo(logPath).Length : 0L;
        bool wasTruncated = validEnd < fileLen;

        if (wasTruncated)
        {
            // Physically truncate to the last validated record (§9.3-b), then fsync
            // so the repaired length is itself durable before any new append.
            // No directory fsync is needed here: SetLength is an in-place size change
            // on the existing file — the directory entry (name, inode link) is not
            // modified, only the inode's size metadata, which the file fsync covers.
            // Dir fsync is only required when a directory entry is created or replaced
            // (i.e. after rename/unlink), which the create/replace paths do above.
            using FileStream fs = new(logPath, FileMode.Open, FileAccess.Write,
                FileShare.None, bufferSize: 4096, FileOptions.None);
            fs.SetLength(validEnd);
            fs.Flush(flushToDisk: true);
        }

        return Task.FromResult<AppendLogRepairResult>(
            new AppendLogRepairResult(records, validEnd, wasTruncated));
    }

    /// <summary>
    /// Read every framed record, validating length + CRC. Returns the recovered
    /// payloads and the offset just past the last valid record. In
    /// <see cref="AppendReadMode.StopAtFirstBad"/> the scan stops at the first
    /// invalid record (correct on a write-ordering FS — the only invalid record is
    /// the torn tail). In <see cref="AppendReadMode.ResyncAndContinue"/> it advances
    /// one byte past a bad record and re-frames on the next valid record.
    /// </summary>
    private static (IReadOnlyList<ReadOnlyMemory<byte>> Records, long ValidEndOffset) ReadAll(
        string logPath, AppendReadMode mode)
    {
        if (!File.Exists(logPath))
            return (Array.Empty<ReadOnlyMemory<byte>>(), 0L);

        using FileStream fs = new(logPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 65536, FileOptions.SequentialScan);

        var records = new List<ReadOnlyMemory<byte>>();
        long validEnd = 0L;
        long pos = 0L;
        long len = fs.Length;

        // Hoisted out of the loop (CA2014: no stackalloc inside a loop body).
        Span<byte> lenBuf = stackalloc byte[LengthPrefixSize];
        Span<byte> crcBuf = stackalloc byte[ChecksumSize];

        bool Recover(ref long position)
        {
            if (mode == AppendReadMode.ResyncAndContinue) { position++; return true; }
            return false;   // StopAtFirstBad
        }

        while (pos < len)
        {
            if (len - pos < LengthPrefixSize)
                break;   // not enough bytes for even a length prefix — torn tail

            fs.Position = pos;
            if (!ReadExact(fs, lenBuf))
                break;
            uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

            if (payloadLen > MaxPayloadLen)
            {
                if (Recover(ref pos)) continue;
                break;
            }

            long fullRecordSize = (long)LengthPrefixSize + payloadLen + ChecksumSize;
            if (pos + fullRecordSize > len)
            {
                // Record runs past EOF — torn trailing record.
                if (Recover(ref pos)) continue;
                break;
            }

            byte[] payload = new byte[payloadLen];
            if (!ReadExact(fs, payload))
            {
                if (Recover(ref pos)) continue;
                break;
            }

            if (!ReadExact(fs, crcBuf))
            {
                if (Recover(ref pos)) continue;
                break;
            }

            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);
            uint computedCrc = Crc32.HashToUInt32(payload);   // LE-equivalent of Crc32.Hash bytes
            if (storedCrc != computedCrc)
            {
                if (Recover(ref pos)) continue;
                break;
            }

            records.Add(payload);
            pos += fullRecordSize;
            validEnd = pos;
        }

        return (records, validEnd);
    }

    // ---- Atomic-capability probe (EXDEV / synced-target best-effort warning) ----

    public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string dir = Path.GetDirectoryName(Path.GetFullPath(destPath))
            ?? throw new ArgumentException("Could not determine directory.", nameof(destPath));

        // Best-effort probe: create a zero-length temp in the destination directory
        // and rename it within the same directory. A same-FS local directory yields
        // an atomic rename; any failure (EXDEV cross-device, permission-denied,
        // read-only mount, ENOSPC on the probe create, etc.) degrades to
        // BestEffortWithWarning — the probe must NEVER throw to the caller (W2).
        string probeA = Path.Combine(dir, $".hoard-probe-{Guid.NewGuid():N}");
        string probeB = Path.Combine(dir, $".hoard-probe-{Guid.NewGuid():N}");
        var capability = AtomicWriteCapability.Atomic;
        try
        {
            using (FileStream fs = new(probeA, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows())
                WindowsAtomicRename(probeA, probeB, overwrite: false);
            else
                File.Move(probeA, probeB, overwrite: false);
        }
        catch
        {
            // Any failure — EXDEV, UnauthorizedAccessException, DirectoryNotFoundException,
            // IOException, etc. — is treated as "atomic rename not confirmed here".
            // Degrading to BestEffortWithWarning is always the safe answer; false-negative
            // for a transient error costs one warning surfaced to the user, not data loss.
            capability = AtomicWriteCapability.BestEffortWithWarning;
        }
        finally
        {
            BestEffortDeleteTemp(probeA);
            BestEffortDeleteTemp(probeB);
        }

        return Task.FromResult(capability);
    }

    // ---- Helpers ----

    private static bool ReadExact(FileStream fs, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = fs.Read(buf[total..]);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    private static void BestEffortDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup; never mask the real failure */ }
    }

    /// <summary>
    /// True when a rename/move threw because src and dest are on different devices
    /// (Linux errno EXDEV=18, Windows ERROR_NOT_SAME_DEVICE=0x11). This is the
    /// only IOException from rename(2)/MoveFileEx that signals "atomic rename is
    /// structurally unavailable here" — the caller should throw AtomicWriteUnavailable
    /// so the domain can surface the NFR-4 best-effort warning.
    /// </summary>
    private static bool CrossDeviceRename(IOException ex)
    {
        // Linux: rename(2) across mounts sets errno EXDEV (18); the BCL maps errno
        // into the low 16 bits of HResult on Unix.
        if (!OperatingSystem.IsWindows())
            return (ex.HResult & 0xFFFF) == 18;   // 18 = EXDEV
        // Windows: ERROR_NOT_SAME_DEVICE (0x11) surfaces as HResult 0x80070011.
        const int ERROR_NOT_SAME_DEVICE = unchecked((int)0x80070011);
        return ex.HResult == ERROR_NOT_SAME_DEVICE;
    }

    // ---- Linux directory fsync (durable rename) ----

    // CA2101 wants Unicode (LPWStr) marshaling for P/Invoke strings, but the libc
    // open(2) path argument is a byte string; LPUTF8Str is the correct marshaling
    // and Unicode would be wrong. Suppressed narrowly with the right marshaling
    // pinned (same deliberate-suppression posture as Core's CA1710 on
    // AtomicWriteUnavailable). This import only ever runs on the Linux path.
#pragma warning disable CA2101
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);
#pragma warning restore CA2101

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    private const int O_RDONLY = 0;

    /// <summary>
    /// fsync the containing directory so a freshly created/replaced directory
    /// entry is durable after a power cut (SPIKE-5 §(b) step 4). Best-effort: a
    /// failure here does not invalidate the already-fsync'd file data.
    /// </summary>
    private static void FsyncDirectory(string dir)
    {
        int fd = open(dir, O_RDONLY);
        if (fd < 0) return;
        try { _ = fsync(fd); }
        finally { _ = close(fd); }
    }

    // ---- Windows atomic rename — COMPILE-ONLY on this box; runtime DEFERRED ----
    //
    // SPIKE-5 non-confirmation 1: MoveFileEx/ReplaceFile with WRITE_THROUGH compile
    // clean for win-x64; the power-cut absent-or-complete assertion needs real
    // Windows hardware. Guarded by OperatingSystem.IsWindows() so it never runs on
    // Linux. [SupportedOSPlatform] satisfies the platform-compatibility analyzer.

    [SupportedOSPlatform("windows")]
    private static void WindowsAtomicRename(string temp, string dest, bool overwrite)
    {
        if (overwrite)
        {
            // ReplaceFile requires the destination to already exist; if absent fall
            // back to MoveFileEx (create-rename) which is correct for that case too.
            if (File.Exists(dest))
            {
                if (!ReplaceFileW(dest, temp, null, ReplaceFileFlags.WriteThrough, IntPtr.Zero, IntPtr.Zero))
                    throw new IOException($"ReplaceFile failed with Win32 error {Marshal.GetLastWin32Error()}");
                return;
            }
            if (!MoveFileExW(temp, dest, MoveFileFlags.WriteThrough | MoveFileFlags.ReplaceExisting))
                throw new IOException($"MoveFileEx (replace) failed with Win32 error {Marshal.GetLastWin32Error()}");
            return;
        }

        // Create-when-absent: MoveFileEx WITHOUT ReplaceExisting fails if dest exists.
        if (!MoveFileExW(temp, dest, MoveFileFlags.WriteThrough))
            throw new IOException($"MoveFileEx (create) failed with Win32 error {Marshal.GetLastWin32Error()}");
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x00000001,
        WriteThrough = 0x00000008,   // MOVEFILE_WRITE_THROUGH
    }

    [Flags]
    private enum ReplaceFileFlags : uint
    {
        WriteThrough = 0x00000001,   // REPLACEFILE_WRITE_THROUGH
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, MoveFileFlags dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReplaceFileW(
        string lpReplacedFileName,
        string lpReplacementFileName,
        string? lpBackupFileName,
        ReplaceFileFlags dwReplaceFlags,
        IntPtr lpExclude,
        IntPtr lpReserved);
}
