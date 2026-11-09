// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// Atomic, durable file writes per NFR-4. All OS syscalls live here; the domain
/// calls these and never touches fsync / MoveFileEx / ReplaceFile / rename(2).
public interface IAtomicFileStore
{
    /// First-ever write of a NEW file (new .hoard via FR-34, first recovery snapshot
    /// of a never-saved scratch buffer, first activity-log / undo-journal record).
    /// Sequence: same-directory temp -> fsync(temp data) -> atomic create-rename ->
    /// Linux: fsync(containing dir).
    /// Windows: MoveFileEx(temp, dest, MOVEFILE_WRITE_THROUGH) (fails if dest exists -
    /// correct for create-when-absent). SPIKE-5 compile-confirmed; runtime power-cut DEFERRED (§6).
    /// Throws AtomicWriteUnavailable (caller surfaces the NFR-4 best-effort warning) on EXDEV /
    /// cross-device / synced-target where atomic rename is unavailable.
    Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// Replace an EXISTING file atomically with write-through.
    /// Sequence: same-directory temp -> fsync(temp data) -> atomic rename-over -> Linux dir fsync.
    /// Windows: P/Invoke ReplaceFile with REPLACEFILE_WRITE_THROUGH (NOT System.IO.File.Replace,
    /// which throws FileNotFoundException when dest is absent and does NOT pass WRITE_THROUGH).
    /// Never delete-then-move (crash window with no valid file).
    Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// Append one durable record to an append-log (activity log §9.3, undo journal §9.4).
    /// Writes [4B uint32 LE length][N B payload][4B uint32 CRC-32/ISO-HDLC], then fsync,
    /// BEFORE returning (per-record fsync-before-next-append, §3.3). Returns the new end offset.
    Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// On open, truncate-repair an append-log to the last validated record offset (§3.3).
    /// Returns the recovered records and whether a torn tail was truncated.
    Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct);

    /// Whether destPath's directory supports atomic rename (local same-FS) vs requires the
    /// fsync'd-in-place + warning fallback (EXDEV / SMB / NFS / Syncthing / Dropbox). A-4 / NFR-4.
    Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct);
}

public enum AppendReadMode { StopAtFirstBad, ResyncAndContinue } // §3.3: default StopAtFirstBad on local SSD
public enum AtomicWriteCapability { Atomic, BestEffortWithWarning }
public sealed record AppendLogRepairResult(IReadOnlyList<ReadOnlyMemory<byte>> Records, long ValidEndOffset, bool WasTruncated);

// CA1710 (type should end in 'Exception') is suppressed deliberately: TD §2.1 names this type
// verbatim 'AtomicWriteUnavailable' as the binding contract M2.1 throws and the file-ops service
// catches. The brief permits modulating only the project rename, so the spec name is preserved.
#pragma warning disable CA1710
public sealed class AtomicWriteUnavailable : Exception { /* carries the path + reason for the NFR-4 warning */ }
#pragma warning restore CA1710
