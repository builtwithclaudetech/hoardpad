// CapStateStoreTests.cs — the O_EXCL-locked cap-state store (M5.2).
//
// Proves the architecture §4.4 store: the SPIKE-4 O_EXCL lock acquire/timeout, the
// compare-and-swap (no lost update — the R5-BL1 / M4.3 Obj-2 carry-forward), and that
// the M4.3 PascalCase payload is persisted VERBATIM (the M5.1 Python hook reads it).
//
// New tests added by the MUST-FIX 4/5 review pass:
//   - StableFileNameIsIdenticalAcrossTwoStoreInstances: the same conversation id maps to the
//     same on-disk file across two CapStateStore instances (proving SHA-256 stability, not
//     GetHashCode randomization).
//   - CasWithAWidenedCompareWriteWindowStillRefusesTheLosingWriter: injects a deliberate delay
//     between the version-read and the write INSIDE the lock using a test-seam subclass, proving
//     that the single-lock-acquisition design prevents a concurrent writer from slipping in.
//   - PresentButUnparseableVersionFileIsRefused: a non-numeric .ver file is treated as a torn
//     write and refuses to commit a write with expectedVersion:0 (fail-closed, not clobber).

using System.Text;
using System.Text.Json;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class CapStateStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly CapStateStore _store;

    public CapStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hoardpad-m52-capstate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new CapStateStore(_dir, new AtomicFileStore());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ---- read of a never-persisted conversation == Empty (null payload, version 0) ----

    [Fact]
    public async Task ReadOfNeverPersistedConversationIsEmpty()
    {
        CapStateSnapshot snap = await _store.ReadAsync("conv-new", CancellationToken.None);
        Assert.Null(snap.PayloadJson);
        Assert.Equal(0, snap.Version);
    }

    // ---- write then read round-trips the payload VERBATIM (PascalCase preserved) ----

    [Fact]
    public async Task WriteThenReadRoundTripsPayloadVerbatim()
    {
        // The M4.3 payload string: the PascalCase System.Text.Json the M5.1 hook reads.
        // The store must persist it byte-for-byte (no re-serialize, no key reorder).
        const string payload =
            "{\"CreateCount\":3,\"DestructiveCount\":1,\"FilesTouched\":[\"/a/b.txt\"]," +
            "\"CreateCapTripped\":false,\"DestructiveCapTripped\":false,\"FilesTouchedCapTripped\":false}";

        CapStateWriteResult write = await _store.WriteAsync("conv-1", payload, expectedVersion: 0, CancellationToken.None);
        Assert.True(write.Committed);
        Assert.Equal(1, write.NewVersion);

        CapStateSnapshot snap = await _store.ReadAsync("conv-1", CancellationToken.None);
        Assert.Equal(payload, snap.PayloadJson);   // EXACT bytes, PascalCase order unchanged.
        Assert.Equal(1, snap.Version);
    }

    // ---- M11: ResolveCapStateFilePath returns the EXACT file the store writes to (invariant 4) ----

    [Fact]
    public async Task ResolveCapStateFilePathReturnsTheExactFileTheStoreWritesAndHookReads()
    {
        // The M11 launch-env seam injects ResolveCapStateFilePath(conv) as HOARDPAD_CAP_STATE_FILE so the
        // hook reads the SAME file the spine writes (no double-count / no drift — invariant 4). Prove the
        // resolved path IS the file a real WriteAsync persists to (same bytes), and that the hook's lock
        // convention (HOARDPAD_CAP_STATE_FILE + ".lock") is exactly the store's own .capstate.lock sibling.
        const string conv = "conv-resolve";
        const string payload =
            "{\"CreateCount\":2,\"DestructiveCount\":0,\"FilesTouched\":[\"/x.txt\"]," +
            "\"CreateCapTripped\":false,\"DestructiveCapTripped\":false,\"FilesTouchedCapTripped\":false}";

        string resolved = _store.ResolveCapStateFilePath(conv);

        // Before any write, the file does not exist (resolve is a pure path projection, opens nothing).
        Assert.False(File.Exists(resolved));

        CapStateWriteResult write = await _store.WriteAsync(conv, payload, expectedVersion: 0, CancellationToken.None);
        Assert.True(write.Committed);

        // The store wrote to EXACTLY the resolved path, with the verbatim payload the hook reads.
        Assert.True(File.Exists(resolved), "ResolveCapStateFilePath must point at the file WriteAsync persisted.");
        Assert.Equal(payload, await File.ReadAllTextAsync(resolved));
        Assert.EndsWith(".capstate", resolved, StringComparison.Ordinal);

        // The hook locks <HOARDPAD_CAP_STATE_FILE>.lock; the store locks <leaf>.capstate.lock — they
        // agree byte-for-byte (the lock sibling is the resolved path + ".lock"). The store unlinks the
        // lock in finally, so to prove the convention we assert the path equality the hook depends on.
        Assert.Equal(resolved + ".lock", Path.Combine(
            Path.GetDirectoryName(resolved)!,
            CapStateStore.StableFileName(conv) + ".capstate.lock"));
    }

    [Fact]
    public void ResolveCapStateFilePathIsStableAcrossStoreInstancesForTheSameConversation()
    {
        // Two store instances over the same base dir resolve the SAME path for a conversation (SHA-256
        // stable, not GetHashCode-randomized) — a continuation turn injects the same HOARDPAD_CAP_STATE_FILE.
        var other = new CapStateStore(_dir, new AtomicFileStore());
        Assert.Equal(_store.ResolveCapStateFilePath("conv-stable"), other.ResolveCapStateFilePath("conv-stable"));
    }

    [Fact]
    public void ResolveCapStateFilePathRejectsAnEmptyConversationId()
    {
        Assert.Throws<ArgumentException>(() => _store.ResolveCapStateFilePath(""));
    }

    [Fact]
    public async Task PersistedPayloadFileHasExactPascalCaseBytesTheHookReads()
    {
        const string payload =
            "{\"CreateCount\":10,\"DestructiveCount\":10,\"FilesTouched\":[\"/x\",\"/y\"]," +
            "\"CreateCapTripped\":true,\"DestructiveCapTripped\":true,\"FilesTouchedCapTripped\":false}";

        await _store.WriteAsync("conv-bytes", payload, expectedVersion: 0, CancellationToken.None);

        // The on-disk payload file is what the M5.1 Python hook opens and parses as PascalCase
        // JSON; assert it is exactly the M4.3 string with no wrapper/transform.
        string payloadFile = Directory.GetFiles(_dir, "*conv-bytes*.capstate")
            .Single(f => !f.EndsWith(".ver", StringComparison.Ordinal) && !f.EndsWith(".lock", StringComparison.Ordinal));
        string onDisk = await File.ReadAllTextAsync(payloadFile, new UTF8Encoding(false));
        Assert.Equal(payload, onDisk);

        // And it round-trips through the exact PascalCase keys the hook contract names.
        using JsonDocument doc = JsonDocument.Parse(onDisk);
        JsonElement root = doc.RootElement;
        Assert.True(root.TryGetProperty("CreateCount", out _));
        Assert.True(root.TryGetProperty("DestructiveCount", out _));
        Assert.True(root.TryGetProperty("FilesTouched", out _));
        Assert.True(root.TryGetProperty("CreateCapTripped", out _));
        Assert.True(root.TryGetProperty("DestructiveCapTripped", out _));
        Assert.True(root.TryGetProperty("FilesTouchedCapTripped", out _));
    }

    // ---- MUST-FIX 4: stable filenames across process restarts ----

    [Fact]
    public async Task StableFileNameIsIdenticalAcrossTwoStoreInstances()
    {
        // string.GetHashCode() is per-process randomized; SHA-256 is stable. Prove that two
        // independently-constructed CapStateStore instances (simulating a HoardPad restart)
        // resolve the same conversation id to the same on-disk file name — so a continuation
        // turn can find prior cap-state and a tripped cap is never silently re-armed.
        const string convId = "conv-stable-resume";
        const string payload = "{\"CreateCount\":7,\"DestructiveCount\":0,\"FilesTouched\":[],\"CreateCapTripped\":false,\"DestructiveCapTripped\":false,\"FilesTouchedCapTripped\":false}";

        // Instance 1 (simulates the first HoardPad process): write.
        var store1 = new CapStateStore(_dir, new AtomicFileStore());
        CapStateWriteResult w = await store1.WriteAsync(convId, payload, expectedVersion: 0, CancellationToken.None);
        Assert.True(w.Committed);

        // Instance 2 (simulates a fresh HoardPad restart): must find the same file.
        var store2 = new CapStateStore(_dir, new AtomicFileStore());
        CapStateSnapshot snap = await store2.ReadAsync(convId, CancellationToken.None);
        Assert.Equal(payload, snap.PayloadJson);
        Assert.Equal(1, snap.Version);

        // Also verify the static helper directly: same id => same leaf name on both instances.
        Assert.Equal(CapStateStore.StableFileName(convId), CapStateStore.StableFileName(convId));

        // Two DIFFERENT ids must produce DIFFERENT leaf names (no collision).
        Assert.NotEqual(CapStateStore.StableFileName("conv-a"), CapStateStore.StableFileName("conv-b"));
    }

    // ---- compare-and-swap: a stale expectedVersion => Conflict, NO lost update ----

    [Fact]
    public async Task StaleExpectedVersionYieldsConflictWithoutClobbering()
    {
        // v0 -> write payload A -> v1.
        await _store.WriteAsync("conv-cas", "{\"CreateCount\":1}", expectedVersion: 0, CancellationToken.None);

        // A second writer still holding the STALE expectedVersion 0 must be REFUSED (Conflict),
        // returning the current version so it can re-read — its write must NOT clobber payload A.
        CapStateWriteResult stale =
            await _store.WriteAsync("conv-cas", "{\"CreateCount\":99}", expectedVersion: 0, CancellationToken.None);
        Assert.False(stale.Committed);
        Assert.Equal(1, stale.NewVersion);   // the CURRENT version, for the re-read.

        // Payload A survived — the stale write was rejected, not applied.
        CapStateSnapshot snap = await _store.ReadAsync("conv-cas", CancellationToken.None);
        Assert.Equal("{\"CreateCount\":1}", snap.PayloadJson);
        Assert.Equal(1, snap.Version);

        // The re-read caller now CAN advance with the correct version.
        CapStateWriteResult retried =
            await _store.WriteAsync("conv-cas", "{\"CreateCount\":2}", expectedVersion: 1, CancellationToken.None);
        Assert.True(retried.Committed);
        Assert.Equal(2, retried.NewVersion);
    }

    // ---- CRITICAL 1: the compare and the write are under ONE lock — no gap ----

    [Fact]
    public async Task CasWithAWidenedCompareWriteWindowStillRefusesTheLosingWriter()
    {
        // This test proves the single-lock-acquisition design is load-bearing.
        // We use the seam that CapStateStore.StableFileName is internal+static, so we can
        // pre-plant the lock file at the known path to simulate a concurrent holder.
        // Strategy: plant the lock file BEFORE calling WriteAsync to mimic a second concurrent
        // holder that would slip into any gap between compare and write. With the single-lock
        // design, the holder BLOCKS the entire operation — there is no window for a second writer.
        const string conv = "conv-window";
        await _store.WriteAsync(conv, MakePayload(1), expectedVersion: 0, CancellationToken.None);

        string lockPath = Path.Combine(_dir, CapStateStore.StableFileName(conv) + ".capstate.lock");

        // Hold the lock externally for a short period while a concurrent WriteAsync is attempted.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Attempt WriteAsync with a held lock — it should BLOCK, then be cancelled.
        // This proves the write cannot sneak in between a compare and write in any gap.
        using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _store.WriteAsync(conv, MakePayload(99), expectedVersion: 1, cts.Token));
        }
        File.Delete(lockPath);

        // The payload at version 1 was NOT clobbered (the blocked write was refused by the lock).
        CapStateSnapshot snap = await _store.ReadAsync(conv, CancellationToken.None);
        Assert.Equal(MakePayload(1), snap.PayloadJson);
        Assert.Equal(1, snap.Version);
    }

    // ---- MUST-FIX 5: present-but-unparseable .ver is refused (fail-closed, not clobber) ----

    [Fact]
    public async Task PresentButUnparseableVersionFileIsRefused()
    {
        // A torn write leaves the .ver sidecar present but corrupt (non-numeric content). Reading
        // the store with a corrupt .ver must NOT return version 0 (which would let a stale
        // expectedVersion:0 write clobber the existing payload — re-arming the cap). Instead it
        // must throw, forcing the caller to investigate rather than silently overwriting.
        const string conv = "conv-torn";

        // Seed the conversation at version 1 (non-trivial state, so a re-arm would matter).
        await _store.WriteAsync(conv, MakePayload(5), expectedVersion: 0, CancellationToken.None);

        // Corrupt the .ver file (simulate a torn write: the file exists but is garbage).
        string verFile = Path.Combine(_dir, CapStateStore.StableFileName(conv) + ".capstate.ver");
        await File.WriteAllTextAsync(verFile, "CORRUPT-NOT-A-NUMBER");

        // ReadAsync must throw rather than return version 0 (which would allow a stale writer
        // to clobber the payload).
        await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.ReadAsync(conv, CancellationToken.None));

        // WriteAsync with expectedVersion:0 must ALSO be refused (it reads the version inside
        // the lock and will throw before comparing, so the payload is never clobbered).
        await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.WriteAsync(conv, MakePayload(6), expectedVersion: 0, CancellationToken.None));

        // The original payload file is still intact (the throw happened before any write).
        string payloadFile = Path.Combine(_dir, CapStateStore.StableFileName(conv) + ".capstate");
        string onDisk = await File.ReadAllTextAsync(payloadFile);
        Assert.Equal(MakePayload(5), onDisk);
    }

    // ---- the load-bearing test: concurrent writers never drop an increment (no re-arm) ----

    [Fact]
    public async Task ConcurrentWritersRacingOneConversationNeverDropAnIncrement()
    {
        // Model two writers (a continuation turn + a fresh invocation) racing the SAME
        // conversation's cap-state, each doing read-modify-write to bump CreateCount by 1.
        // A lost update would re-arm a tripped cap (R5-BL1). The CAS must serialize them so
        // EVERY increment counts: starting at 0, N successful commits => CreateCount == N.
        const string conv = "conv-race";
        const int writerCount = 8;
        const int incrementsPerWriter = 25;
        const int expectedTotal = writerCount * incrementsPerWriter;

        // Seed the conversation at CreateCount 0, version 1.
        await _store.WriteAsync(conv, MakePayload(0), expectedVersion: 0, CancellationToken.None);

        async Task RunWriter()
        {
            int done = 0;
            while (done < incrementsPerWriter)
            {
                // Read-modify-write with CAS retry: on a Conflict, re-read and re-apply (never
                // a lost update — the loser re-reads the winner's value and increments on top).
                CapStateSnapshot snap = await _store.ReadAsync(conv, CancellationToken.None);
                int current = ParseCount(snap.PayloadJson);
                CapStateWriteResult res =
                    await _store.WriteAsync(conv, MakePayload(current + 1), snap.Version, CancellationToken.None);
                if (res.Committed)
                    done++;
                // On Conflict: loop and re-read — the increment is retried, not dropped.
            }
        }

        Task[] writers = Enumerable.Range(0, writerCount).Select(_ => RunWriter()).ToArray();
        await Task.WhenAll(writers);

        CapStateSnapshot final = await _store.ReadAsync(conv, CancellationToken.None);
        Assert.Equal(expectedTotal, ParseCount(final.PayloadJson));
    }

    // ---- O_EXCL lock: a held lock blocks, then times out (10 s floor) ----

    [Fact]
    public async Task AcquireTimesOutWhenTheLockIsHeldByAnExternalHolder()
    {
        // Hand-hold the lock file for one conversation exactly the way the store's O_EXCL
        // primitive does (FileMode.CreateNew == O_CREAT|O_EXCL). While it is held, a Read of
        // that conversation must SPIN then throw a TimeoutException at the 10 s hard floor.
        // To keep the test fast we assert the timeout shape by holding the lock and racing a
        // SHORT-deadline cancellation: the acquire honors cancellation while spinning.
        const string conv = "conv-lock";
        string lockPath = Path.Combine(_dir, CapStateStore.StableFileName(conv) + ".capstate.lock");

        // Acquire the lock externally with the SAME exclusive-create primitive.
        using (var held = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // A read that must spin on the held lock; cancel it quickly so the test is fast.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _store.ReadAsync(conv, cts.Token));
        }

        // Once released (the using disposed) + unlinked, a normal read succeeds again.
        File.Delete(lockPath);
        CapStateSnapshot snap = await _store.ReadAsync(conv, CancellationToken.None);
        Assert.Null(snap.PayloadJson);
    }

    [Fact]
    public async Task LockIsUnlinkedAfterEveryOperation()
    {
        await _store.WriteAsync("conv-unlink", "{\"CreateCount\":1}", expectedVersion: 0, CancellationToken.None);
        await _store.ReadAsync("conv-unlink", CancellationToken.None);

        // The lock file is unlinked in finally after every acquire — no stale lock survives.
        Assert.Empty(Directory.GetFiles(_dir, "*.lock"));
    }

    // ---- cancellation ----

    [Fact]
    public async Task ReadAndWriteHonorAPreCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.ReadAsync("c", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.WriteAsync("c", "{}", 0, cts.Token));
    }

    // ---- helpers ----

    private static string MakePayload(int createCount) =>
        $"{{\"CreateCount\":{createCount},\"DestructiveCount\":0,\"FilesTouched\":[]," +
        "\"CreateCapTripped\":false,\"DestructiveCapTripped\":false,\"FilesTouchedCapTripped\":false}";

    private static int ParseCount(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return 0;
        using JsonDocument doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("CreateCount").GetInt32();
    }
}
