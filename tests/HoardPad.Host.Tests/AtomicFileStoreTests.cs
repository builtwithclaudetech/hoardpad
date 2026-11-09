// AtomicFileStoreTests.cs — temp-FS durability tests for the M2.1 seam.
//
// Productionizes the SPIKE-5 crash fixtures as deterministic in-process tests:
//   - atomic create / replace round-trip;
//   - append-record round-trip (frame + returned end offset);
//   - truncate-repair of a torn trailing record (SPIKE-5 fixture 1);
//   - interior-bad-record resync vs stop-at-first-bad (SPIKE-5 fixture 2);
//   - the EXDEV/synced-target capability probe (local dir => Atomic).
//
// The Windows first-create power-cut fixture is AUTHORED here but DEFERRED to real
// Windows hardware (SPIKE-5 non-confirmation 1): it is gated off with [Fact(Skip=...)]
// so it compiles for win-x64 but never runs/asserts on this Linux box.

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class AtomicFileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly AtomicFileStore _store = new();

    public AtomicFileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hoardpad-m21-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string InDir(string name) => Path.Combine(_dir, name);

    // ---- atomic whole-file writes ----

    [Fact]
    public async Task CreateWhenAbsentWritesExactBytes()
    {
        string dest = InDir("new.hoard");
        byte[] content = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");

        await _store.CreateWhenAbsentAsync(dest, content, CancellationToken.None);

        Assert.True(File.Exists(dest));
        Assert.Equal(content, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task CreateWhenAbsentFailsWhenDestinationExists()
    {
        string dest = InDir("exists.hoard");
        await File.WriteAllBytesAsync(dest, new byte[] { 1, 2, 3 });

        // POSIX rename(overwrite:false) onto an existing path throws IOException.
        await Assert.ThrowsAnyAsync<IOException>(
            () => _store.CreateWhenAbsentAsync(dest, new byte[] { 9 }, CancellationToken.None));

        // The pre-existing file is left intact (never delete-then-move).
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task ReplaceWhenPresentOverwritesAtomicallyWithExactBytes()
    {
        string dest = InDir("doc.hoard");
        await File.WriteAllBytesAsync(dest, Encoding.UTF8.GetBytes("OLD"));
        byte[] updated = Encoding.UTF8.GetBytes("UPDATED-CONTENT");

        await _store.ReplaceWhenPresentAsync(dest, updated, CancellationToken.None);

        Assert.Equal(updated, await File.ReadAllBytesAsync(dest));
        // No temp files left behind in the directory.
        Assert.Empty(Directory.GetFiles(_dir, ".hoard-tmp-*"));
    }

    // ---- append-record round-trip ----

    [Fact]
    public async Task AppendRecordReturnsGrowingEndOffsetAndRoundTrips()
    {
        string log = InDir("activity.log");
        byte[] r0 = Encoding.UTF8.GetBytes("{\"seq\":0}");
        byte[] r1 = Encoding.UTF8.GetBytes("{\"seq\":1,\"op\":\"write\"}");

        long off0 = await _store.AppendRecordAsync(log, r0, CancellationToken.None);
        long off1 = await _store.AppendRecordAsync(log, r1, CancellationToken.None);

        Assert.Equal(FrameSize(r0.Length), off0);
        Assert.Equal(FrameSize(r0.Length) + FrameSize(r1.Length), off1);

        var result = await _store.OpenAndRepairAsync(log, AppendReadMode.StopAtFirstBad, CancellationToken.None);
        Assert.False(result.WasTruncated);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(r0, result.Records[0].ToArray());
        Assert.Equal(r1, result.Records[1].ToArray());
        Assert.Equal(off1, result.ValidEndOffset);
    }

    [Fact]
    public async Task OpenAndRepairOnEmptyOrAbsentLogIsClean()
    {
        string log = InDir("empty.log");
        var absent = await _store.OpenAndRepairAsync(log, AppendReadMode.StopAtFirstBad, CancellationToken.None);
        Assert.Empty(absent.Records);
        Assert.Equal(0, absent.ValidEndOffset);
        Assert.False(absent.WasTruncated);

        await File.WriteAllBytesAsync(log, Array.Empty<byte>());
        var empty = await _store.OpenAndRepairAsync(log, AppendReadMode.StopAtFirstBad, CancellationToken.None);
        Assert.Empty(empty.Records);
        Assert.Equal(0, empty.ValidEndOffset);
        Assert.False(empty.WasTruncated);   // nothing to truncate (SPIKE-5 B5a)
    }

    // ---- truncate-repair of a torn trailing record (SPIKE-5 fixture 1) ----

    [Fact]
    public async Task OpenAndRepairTruncatesTornTrailingRecord()
    {
        string log = InDir("torn.log");
        // 3 complete records, then a torn 4th (length prefix only, no payload/CRC).
        long validEnd = 0;
        for (int i = 0; i < 3; i++)
            validEnd = await _store.AppendRecordAsync(log, RecordBytes(i), CancellationToken.None);

        // Append a bare length prefix for a record that never completed.
        byte[] tornPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tornPrefix, 99u);
        await using (var fs = new FileStream(log, FileMode.Append))
            await fs.WriteAsync(tornPrefix);

        long lenBeforeRepair = new FileInfo(log).Length;
        Assert.True(lenBeforeRepair > validEnd);   // the torn tail is on disk

        var result = await _store.OpenAndRepairAsync(log, AppendReadMode.StopAtFirstBad, CancellationToken.None);

        Assert.True(result.WasTruncated);
        Assert.Equal(3, result.Records.Count);
        Assert.Equal(validEnd, result.ValidEndOffset);
        for (int i = 0; i < 3; i++)
            Assert.Equal(RecordBytes(i), result.Records[i].ToArray());

        // The file was physically truncated to the last validated offset.
        Assert.Equal(validEnd, new FileInfo(log).Length);
    }

    // ---- interior-bad-record fixture (SPIKE-5 fixture 2) ----

    [Fact]
    public async Task InteriorBadRecordStopAtFirstBadDropsTheTail()
    {
        string log = await BuildLogWithCorruptInteriorRecordAsync(recordCount: 7, corruptIndex: 2);

        var result = await _store.OpenAndRepairAsync(log, AppendReadMode.StopAtFirstBad, CancellationToken.None);

        // Naive reader stops at the corrupt record #2 — recovers only #0 and #1
        // (the data-loss bug §9.3(c) exists to characterise).
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(RecordBytes(0), result.Records[0].ToArray());
        Assert.Equal(RecordBytes(1), result.Records[1].ToArray());
    }

    [Fact]
    public async Task InteriorBadRecordResyncAndContinueRecoversIntactTail()
    {
        // Rebuild a fresh log (StopAtFirstBad above truncated its copy on repair).
        string log = await BuildLogWithCorruptInteriorRecordAsync(recordCount: 7, corruptIndex: 2);

        var result = await _store.OpenAndRepairAsync(log, AppendReadMode.ResyncAndContinue, CancellationToken.None);

        // R4-mi4 fallback: skip corrupt #2, recover the 6 intact records (#0,#1,#3..#6).
        Assert.Equal(6, result.Records.Count);
        var recovered = result.Records.Select(r => Encoding.UTF8.GetString(r.Span)).ToList();
        Assert.Contains(Encoding.UTF8.GetString(RecordBytes(3)), recovered);
        Assert.Contains(Encoding.UTF8.GetString(RecordBytes(6)), recovered);
        Assert.DoesNotContain(Encoding.UTF8.GetString(RecordBytes(2)), recovered);
    }

    // ---- EXDEV / synced-target capability probe ----

    [Fact]
    public async Task ProbeAtomicCapabilityReportsAtomicForLocalSameFsDirectory()
    {
        // The temp dir is a local same-FS path: an in-directory rename is atomic.
        var capability = await _store.ProbeAtomicCapabilityAsync(InDir("anything.hoard"), CancellationToken.None);
        Assert.Equal(AtomicWriteCapability.Atomic, capability);
        // The probe leaves no temp files behind.
        Assert.Empty(Directory.GetFiles(_dir, ".hoard-probe-*"));
    }

    // ---- W1: no temp-file leak on any failure path (TC1) ----

    [Fact]
    public async Task TC1TempFileNotLeakedWhenRenameFails()
    {
        // Exercise the W1 fix: the temp is written + fsync'd successfully, then the
        // rename step fails. The finally block must clean it up — no orphan temp.
        //
        // Injection method: the destination path is a DIRECTORY that already exists,
        // so File.Move throws IOException ("cannot overwrite directory with file").
        // This means: FileMode.CreateNew for the temp succeeds → Write+Flush succeed
        // → File.Move throws → finally must delete the temp (the W1 path).
        //
        // Alternative (ENOSPC / EIO during Flush) is not deterministically injectable
        // on a running test system without kernel-level fault injection; the directory-
        // as-dest method is the closest reliable in-process approximation.
        string destIsADir = InDir("dest-is-a-dir");
        Directory.CreateDirectory(destIsADir);
        byte[] content = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");

        await Assert.ThrowsAnyAsync<Exception>(
            () => _store.CreateWhenAbsentAsync(destIsADir, content, CancellationToken.None));

        // The temp was created on disk (write+fsync succeeded) but the rename failed —
        // the finally block must have removed it. No orphan .hoard-tmp-* file anywhere.
        Assert.Empty(Directory.GetFiles(_dir, ".hoard-tmp-*"));
    }

    // ---- W2: probe never throws on non-EXDEV failure (TC2) ----

    [Fact]
    public async Task TC2ProbeNeverThrowsForNonAtomicDirectory()
    {
        // Point the probe at a path whose parent directory does not exist — the
        // FileStream CreateNew for probeA throws DirectoryNotFoundException (not an
        // EXDEV). The broad catch must swallow it and return BestEffortWithWarning
        // rather than propagating the exception to the caller (W2).
        string nonExistentParent = InDir(Path.Combine("ghost-dir", "file.hoard"));

        // Must not throw; must degrade gracefully.
        var capability = await _store.ProbeAtomicCapabilityAsync(nonExistentParent, CancellationToken.None);
        Assert.Equal(AtomicWriteCapability.BestEffortWithWarning, capability);
    }

    // ---- TC3: all five methods honour a pre-cancelled CancellationToken ----

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("append")]
    [InlineData("repair")]
    [InlineData("probe")]
    public async Task TC3CancelledTokenThrowsOperationCanceledException(string method)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ct = cts.Token;
        byte[] content = Encoding.UTF8.GetBytes("{}");
        string path = InDir($"cancel-{method}.log");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(method switch
        {
            "create" => () => _store.CreateWhenAbsentAsync(path, content, ct),
            "replace" => () => _store.ReplaceWhenPresentAsync(path, content, ct),
            "append" => () => _store.AppendRecordAsync(path, content, ct),
            "repair" => () => _store.OpenAndRepairAsync(path, AppendReadMode.StopAtFirstBad, ct),
            "probe" => () => _store.ProbeAtomicCapabilityAsync(path, ct),
            _ => throw new InvalidOperationException(method),
        });
    }

    // ---- DEFERRED: Windows first-create power-cut fixture (SPIKE-5 non-confirmation 1) ----
    //
    // AUTHORED so it compiles for win-x64; GATED OFF on Linux. The MoveFileEx
    // MOVEFILE_WRITE_THROUGH "absent-or-complete, never torn" assertion requires a
    // real power-cut on Windows hardware and is NOT runtime-testable on this box.
    // Skip keeps it out of the Met count while keeping the intent in the codebase.
    [Fact(Skip = "win-x64 runtime Deferred — MoveFileEx power-cut fixture authored, not run on Linux (SPIKE-5 non-confirmation 1)")]
    public async Task WindowsFirstCreatePowerCutIsAbsentOrCompleteNeverTorn()
    {
        // On real Windows hardware this would: kill the process mid-MoveFileEx of a
        // never-before-written file and assert the destination is either absent or
        // the complete content, never a torn partial. Deferred here.
        string dest = InDir("win-create.hoard");
        byte[] content = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");
        await _store.CreateWhenAbsentAsync(dest, content, CancellationToken.None);
        bool absentOrComplete = !File.Exists(dest)
            || (await File.ReadAllBytesAsync(dest)).AsSpan().SequenceEqual(content);
        Assert.True(absentOrComplete);
    }

    // ---- fixture helpers ----

    private const int LengthPrefixSize = 4;
    private const int ChecksumSize = 4;

    private static int FrameSize(int payloadLen) => LengthPrefixSize + payloadLen + ChecksumSize;

    private static byte[] RecordBytes(int seq) => Encoding.UTF8.GetBytes($"{{\"seq\":{seq}}}");

    /// <summary>Frame one record exactly as the store does, for fixture construction.</summary>
    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[FrameSize(payload.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, LengthPrefixSize), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(LengthPrefixSize));
        Crc32.Hash(payload, frame.AsSpan(LengthPrefixSize + payload.Length, ChecksumSize));
        return frame;
    }

    /// <summary>
    /// Build a log of <paramref name="recordCount"/> records where the record at
    /// <paramref name="corruptIndex"/> has its payload bytes flipped (CRC now fails)
    /// while every other record is intact — SPIKE-5 fixture 2 in-process.
    /// </summary>
    private async Task<string> BuildLogWithCorruptInteriorRecordAsync(int recordCount, int corruptIndex)
    {
        string log = InDir($"interior-{Guid.NewGuid():N}.log");
        await using var fs = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        for (int i = 0; i < recordCount; i++)
        {
            byte[] payload = RecordBytes(i);
            byte[] frame = Frame(payload);
            if (i == corruptIndex)
            {
                // Flip a payload byte so the stored CRC no longer matches. The
                // length prefix stays valid, so the reader still knows the record
                // boundary — exactly the interior-corruption shape fixture 2 needs.
                frame[LengthPrefixSize] ^= 0xFF;
            }
            await fs.WriteAsync(frame);
        }
        return log;
    }
}
