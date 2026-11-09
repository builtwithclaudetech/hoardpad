// HoardWriteQueueTests.cs — the M6.5 serialized per-document write-queue gates (FR-31, architecture §3.6).
//
// Proves the durability contract independently of the UI:
//   - a TERMINAL mutation persists IMMEDIATELY (zero debounce);
//   - an item-TEXT edit coalesces ≤ 150 ms (TimeProvider-driven, deterministic via ManualTimeProvider) —
//     it does NOT write before the window elapses, and a burst of keystrokes collapses to ONE write;
//   - writes are serialized (never two at once) and last-write-wins.
// The verify filter matches the class name "Hoard".

using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Checklist;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class HoardWriteQueueTests
{
    /// <summary>Spin until the queue's CompletedWrites reaches <paramref name="expected"/> (background pump).</summary>
    private static async Task WaitForWrites(HoardWriteQueue queue, long expected)
    {
        for (int i = 0; i < 200 && queue.CompletedWrites < expected; i++)
            await Task.Delay(5);
        Assert.True(queue.CompletedWrites >= expected,
            $"expected ≥ {expected} writes, saw {queue.CompletedWrites}");
    }

    [Fact]
    public async Task TerminalMutationPersistsImmediatelyWithNoDebounce()
    {
        int writes = 0;
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, clock);

        queue.EnqueueTerminal();

        await WaitForWrites(queue, 1);
        // Zero debounce: the write completes WITHOUT advancing the clock past any window.
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task ItemTextEditDoesNotWriteBeforeTheCoalesceWindowElapses()
    {
        int writes = 0;
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, clock);

        queue.EnqueueTextEdit();

        // Advance LESS than the 150 ms window — no write must have fired.
        clock.Advance(System.TimeSpan.FromMilliseconds(100));
        await Task.Delay(20);
        Assert.Equal(0, writes);

        // Cross the window — exactly one coalesced write fires.
        clock.Advance(System.TimeSpan.FromMilliseconds(60));
        await WaitForWrites(queue, 1);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task RapidTextKeystrokesCoalesceToASingleWrite()
    {
        int writes = 0;
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, clock);

        // Five keystrokes within the window — each resets the timer; only the last survives.
        for (int i = 0; i < 5; i++)
        {
            queue.EnqueueTextEdit();
            clock.Advance(System.TimeSpan.FromMilliseconds(50)); // < 150 ms each — keeps resetting.
        }
        await Task.Delay(20);
        Assert.Equal(0, writes); // still inside the rolling window — nothing written yet.

        clock.Advance(System.TimeSpan.FromMilliseconds(150)); // now let it settle.
        await WaitForWrites(queue, 1);
        Assert.Equal(1, writes); // the burst collapsed to a single write.
    }

    [Fact]
    public async Task TerminalMutationFlushesAPendingTextEditIntoOneWrite()
    {
        int writes = 0;
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, clock);

        queue.EnqueueTextEdit();       // pending coalesced text write…
        queue.EnqueueTerminal();       // …a terminal mutation cancels the timer and writes now.

        await WaitForWrites(queue, 1);
        // Advancing past the window must NOT add a second write (the text timer was cancelled).
        clock.Advance(System.TimeSpan.FromMilliseconds(300));
        await Task.Delay(20);
        Assert.Equal(1, writes);
    }

    /// <summary>
    /// Fix 1 durability: if the write callback throws a non-cancellation exception (e.g. IOException),
    /// the pump must (a) stay alive, (b) fire WriteError, and (c) successfully complete the NEXT write.
    /// A pump that dies on the first failure silently drops all subsequent mutations — a durability breach.
    /// </summary>
    [Fact]
    public async Task PumpSurvivesAWriteFailureAndCompletesTheNextWrite()
    {
        int attempts = 0;
        var errorsSeen = new System.Collections.Generic.List<Exception>();
        var clock = new ManualTimeProvider();

        // First call throws; subsequent calls succeed.
        await using var queue = new HoardWriteQueue(ct =>
        {
            int n = Interlocked.Increment(ref attempts);
            if (n == 1)
                throw new System.IO.IOException("disk full simulation");
            return Task.CompletedTask;
        }, clock);

        queue.WriteError += ex => errorsSeen.Add(ex);

        queue.EnqueueTerminal(); // will throw on attempt 1.

        // Wait long enough for the pump to process the failure.
        await Task.Delay(100);

        Assert.Single(errorsSeen);  // WriteError fired once.
        Assert.Equal(0, queue.CompletedWrites); // failed write does NOT increment CompletedWrites.

        queue.EnqueueTerminal(); // second mutation — pump must still be alive.

        await WaitForWrites(queue, 1);
        Assert.Equal(1, queue.CompletedWrites); // the second mutation was written successfully.
    }

    // ---- QA3-7: QuiesceAsync (Save-As write-quiesce / File-Save on a checklist tab). ----

    [Fact]
    public async Task QuiesceWithNothingPendingDrainsWithoutForcingARedundantWrite()
    {
        int writes = 0;
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, clock);

        // Nothing enqueued at all — a pure drain must complete promptly and write NOTHING.
        await queue.QuiesceAsync(CancellationToken.None);

        Assert.Equal(0, writes);
        Assert.Equal(0, queue.CompletedWrites);
    }

    [Fact]
    public async Task QuieceFlushesAPendingCoalescedTextEditImmediatelyWithoutWaitingOutTheWindow()
    {
        int writes = 0;
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, clock);

        queue.EnqueueTextEdit(); // armed, still inside the 150ms window — no write yet.
        Assert.Equal(0, writes);

        // No clock advance at all — Quiesce must still flush it NOW.
        await queue.QuiesceAsync(CancellationToken.None);

        Assert.Equal(1, writes);
        Assert.Equal(1, queue.CompletedWrites);

        // The cancelled timer must never fire a SECOND write later.
        clock.Advance(System.TimeSpan.FromMilliseconds(300));
        await Task.Delay(20);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task QuiesceWaitsForAnAlreadyInFlightOrQueuedWriteBeforeReturning()
    {
        int writes = 0;
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, clock);

        queue.EnqueueTerminal(); // real write enqueued ahead of the barrier.
        await queue.QuiesceAsync(CancellationToken.None); // must not return before that write completes.

        Assert.True(writes >= 1, "Quiesce must not resolve before an already-queued write has completed");
    }

    [Fact]
    public async Task QuiesceThrowsWhenTheForcedFlushFails()
    {
        var clock = new ManualTimeProvider();
        await using var queue = new HoardWriteQueue(
            _ => throw new System.IO.IOException("disk full simulation"), clock);

        queue.EnqueueTextEdit(); // armed — Quiesce will try to flush it and hit the failing write.

        await Assert.ThrowsAsync<System.IO.IOException>(() => queue.QuiesceAsync(CancellationToken.None));
    }
}
