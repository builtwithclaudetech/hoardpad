// HoardWriteQueue.cs — the serialized per-document `.hoard` write queue (FR-31, architecture §3.6).
//
// One queue per open `.hoard` document. Two mutation classes (FR-31 / MA7):
//   - TERMINAL mutations (check, reorder, indent/outdent, collapse-toggle, delete, mode change, add) —
//     ZERO debounce: each is persisted immediately via the full atomic temp+fsync+rename sequence before
//     the next write begins (IAtomicFileStore).
//   - Item-TEXT keystrokes — coalesced ≤ 150 ms (the window is TimeProvider-driven, C-9), then written.
//
// Last-write-wins coalescing: a write captures the CURRENT document via the injected snapshot callback at
// write time, so a burst of mutations collapses to one write of the final state — never out-of-order or
// partial. The queue is a single serialized async loop (no interleaved writes corrupting order). The VM
// enqueues from the UI thread; the write completes off the dispatcher. A depth-cap NO-OP enqueues nothing
// (the VM only calls Enqueue* on a non-null mutation result), so an over-cap indent triggers ZERO fsync.
//
// DURABILITY-FAULT POLICY: if _writeAsync throws a non-cancellation exception (e.g. IAtomicFileStore
// throws IOException on a full disk), the pump does NOT die — it catches, fires WriteError so the VM can
// surface a "save failed" status banner, and continues draining the queue. The next terminal mutation will
// try again. This ensures a transient I/O failure does not permanently silence the queue (a silently dead
// queue is a durability breach: any subsequent mutation would be lost).
//
// QA3-7 (Save-As relocate / File-Save on a checklist tab): QuiesceAsync enqueues a BARRIER signal carrying
// a TaskCompletionSource. Because the channel is single-reader FIFO, once the pump dequeues the barrier
// (after every signal enqueued ahead of it has been written), the caller knows the queue has drained
// everything up to that point — no write is racing behind it. The barrier only carries ForceWrite=true
// (an actual extra write) when a coalesced item-text edit was still pending its ≤150 ms window — flushing
// that immediately instead of waiting it out. A quiesce with NOTHING pending is a pure drain: it does NOT
// force a redundant write of already-durable, unchanged state (that would fabricate a file for a document
// that was never actually persisted, e.g. a brand-new checklist Save-As'd before its first edit). Used to
// (a) make File ▸ Save on a checklist tab a meaningful action instead of a silent no-op (it was previously
// swallowed before ever reaching the VM), since `.hoard` already auto-saves zero-debounce, and (b) the
// write-quiesce step of the safe Save-As relocate order, so the caller can safely delete the OLD file
// afterward without a background write still targeting it.

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Checklist;

/// <summary>
/// A single serialized async write queue for one open <c>.hoard</c> document (FR-31). Terminal mutations
/// persist with zero debounce; item-text edits coalesce ≤ 150 ms (<see cref="TimeProvider"/>-driven).
/// Write failures fire <see cref="WriteError"/> and the pump continues (no silent death — durability fault
/// policy).
/// </summary>
internal sealed class HoardWriteQueue : IAsyncDisposable
{
    /// <summary>The item-text coalesce window (FR-31 default ≤ 150 ms, measured affordable in SPIKE-1).</summary>
    public static readonly TimeSpan TextCoalesceWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// One channel entry. An ordinary write-now signal (terminal, or the coalesce timer firing) carries
    /// <c>Barrier: null, ForceWrite: true</c>. A <see cref="QuiesceAsync"/> barrier carries a completion
    /// source; <c>ForceWrite</c> is true only when a pending coalesced edit needed flushing — a quiesce
    /// with nothing pending drains without performing a redundant write.
    /// </summary>
    private readonly record struct Signal(TaskCompletionSource<bool>? Barrier, bool ForceWrite);

    private readonly Func<CancellationToken, Task> _writeAsync;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<Signal> _signals;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    private ITimer? _textTimer;
    private long _completedWrites;
    private int _disposed; // 0 = live, 1 = disposed; CAS'd in DisposeAsync to make it idempotent.

    /// <summary>
    /// Raised (on the pump thread) when a write attempt throws a non-cancellation exception. The handler
    /// can surface a "save failed" status to the user. The queue remains alive after this event fires.
    /// </summary>
    public event Action<Exception>? WriteError;

    /// <param name="writeAsync">
    /// Persists the document's CURRENT state atomically (the caller captures the live state inside this
    /// callback — last-write-wins). Invoked off the dispatcher, serialized: never two at once.
    /// </param>
    /// <param name="timeProvider">The injected clock driving the item-text coalesce timer (C-9).</param>
    public HoardWriteQueue(Func<CancellationToken, Task> writeAsync, TimeProvider timeProvider)
    {
        _writeAsync = writeAsync;
        _timeProvider = timeProvider;
        _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>The number of atomic writes that have completed (test/telemetry hook — proves zero-write no-ops).</summary>
    public long CompletedWrites => Interlocked.Read(ref _completedWrites);

    /// <summary>
    /// Enqueue a TERMINAL mutation (FR-31): persisted immediately, zero debounce. Any pending coalesced
    /// item-text write is folded into this write (the snapshot callback captures the final state anyway).
    /// </summary>
    public void EnqueueTerminal()
    {
        CancelTextTimer();
        _signals.Writer.TryWrite(new Signal(Barrier: null, ForceWrite: true));
    }

    /// <summary>
    /// Enqueue an item-TEXT mutation (FR-31): starts/extends the ≤ 150 ms coalesce window; the write fires
    /// once the window elapses with no further keystroke (TimeProvider-driven, deterministic under a fake).
    /// </summary>
    public void EnqueueTextEdit()
    {
        CancelTextTimer();
        _textTimer = _timeProvider.CreateTimer(
            _ => _signals.Writer.TryWrite(new Signal(Barrier: null, ForceWrite: true)),
            state: null,
            dueTime: TextCoalesceWindow,
            period: Timeout.InfiniteTimeSpan);
    }

    private void CancelTextTimer()
    {
        _textTimer?.Dispose();
        _textTimer = null;
    }

    /// <summary>
    /// QA3-7: drain the queue and wait for it to fully settle — flushing any pending ≤ 150 ms coalesced
    /// item-text edit immediately (right now, instead of waiting out the window) if one was armed, or
    /// simply draining with no forced write when nothing was pending (a document that is already fully
    /// durable, or was never persisted at all, must not have a redundant/fabricated write forced onto it).
    /// Because the channel is single-reader FIFO, the returned task only completes once every signal
    /// enqueued BEFORE this call has also been processed, so the caller can rely on the queue being fully
    /// drained (no write still in flight or pending) once this returns. Throws if a forced write itself
    /// fails (the same exception <see cref="WriteError"/> already surfaced) — a caller doing a Save-As
    /// relocate must abort rather than proceed on an unconfirmed state.
    /// </summary>
    public Task QuiesceAsync(CancellationToken ct)
    {
        bool hadPendingTextEdit = _textTimer is not null;
        CancelTextTimer();
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = _signals.Writer.TryWrite(new Signal(barrier, ForceWrite: hadPendingTextEdit));
        ObjectDisposedException.ThrowIf(!enqueued, this);
        return barrier.Task.WaitAsync(ct);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        // ReadAllAsync drains signals one at a time — the serialization guarantee. Each iteration
        // captures + writes the document's CURRENT state, so a burst that wrote multiple signals
        // still collapses to "write whatever the final state is" per write turn (last-write-wins).
        await foreach (Signal signal in _signals.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (!signal.ForceWrite)
            {
                // A pure-drain QuiesceAsync barrier with nothing pending — everything enqueued ahead of it
                // has already been written (FIFO), so it is safe to signal drained WITHOUT writing again.
                signal.Barrier?.TrySetResult(true);
                continue;
            }

            try
            {
                await _writeAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _completedWrites);
                signal.Barrier?.TrySetResult(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Disposal-driven shutdown — exit the pump cleanly.
                signal.Barrier?.TrySetCanceled(ct);
                return;
            }
            catch (Exception ex)
            {
                // A write failure (e.g. IOException from IAtomicFileStore, disk full, etc.).
                // Surface the error so the VM can show a "save failed" banner, then CONTINUE the
                // pump so the queue stays alive for the next mutation (a dead queue silently drops
                // all future writes — a durability breach). A QuiesceAsync caller waiting on this
                // signal's barrier sees the failure too, so it can abort a Save-As relocate rather
                // than proceed on an unconfirmed state.
                WriteError?.Invoke(ex);
                signal.Barrier?.TrySetException(ex);
                // continue: the loop drains the next signal.
            }
        }
    }

    /// <summary>
    /// Drain any pending coalesced text write, stop the pump, and await the in-flight write. Idempotent:
    /// a second call returns immediately without throwing, guarded by a CAS on <c>_disposed</c> so
    /// <c>_cts.Cancel()</c> / <c>_cts.Dispose()</c> are never called twice (which would throw
    /// <see cref="ObjectDisposedException"/>).
    /// (UI-thread caller; the await completes off the dispatcher.)
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // CAS: only the first caller proceeds; subsequent calls are no-ops.
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        CancelTextTimer();
        _signals.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected if cancelled mid-write during disposal.
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}
