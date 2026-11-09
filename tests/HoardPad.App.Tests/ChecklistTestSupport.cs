// ChecklistTestSupport.cs — local fakes for the M6.5 checklist gates.
//
//   - CountingAtomicFileStore: a faithful in-memory IAtomicFileStore that records every create/replace
//     write (count + the last bytes per path) so the durability tests can assert a terminal mutation wrote
//     immediately and a no-op wrote NOTHING.
//   - ManualTimeProvider: a TimeProvider whose CreateTimer registers a one-shot callback fired only by
//     Advance(), so the ≤150 ms item-text coalesce window is deterministic (no wall clock, no Sleep).

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Tests;

/// <summary>An in-memory <see cref="IAtomicFileStore"/> that counts writes and keeps the last bytes per path.</summary>
internal sealed class CountingAtomicFileStore : IAtomicFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private int _writeCount;

    /// <summary>Total create-or-replace writes performed (the durability / zero-write-no-op assertion).</summary>
    public int WriteCount => Volatile.Read(ref _writeCount);

    /// <summary>The last bytes written to <paramref name="path"/> (or null if never written).</summary>
    public byte[]? BytesAt(string path) => _files.TryGetValue(path, out var b) ? b : null;

    public bool Exists(string path) => _files.ContainsKey(path);

    /// <summary>Seed a file as already-present so the next save uses the replace path.</summary>
    public void Seed(string path, byte[] content) => _files[path] = content;

    public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        _files[destPath] = content.ToArray();
        Interlocked.Increment(ref _writeCount);
        return Task.CompletedTask;
    }

    public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        _files[destPath] = content.ToArray();
        Interlocked.Increment(ref _writeCount);
        return Task.CompletedTask;
    }

    public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        Task.FromResult(0L);

    public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct) =>
        Task.FromResult(new AppendLogRepairResult([], 0, false));

    public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct) =>
        Task.FromResult(AtomicWriteCapability.Atomic);
}

/// <summary>
/// A <see cref="TimeProvider"/> whose timers are one-shot callbacks fired only by <see cref="Advance"/>.
/// Deterministic for the ≤ 150 ms item-text coalesce window — no wall clock, no Sleep, no flake.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>True when at least one armed (not-yet-fired) timer is registered — i.e. a (re)schedule is pending.</summary>
    public bool HasPendingTimer
    {
        get
        {
            lock (_gate)
                return _timers.Exists(t => !t.Fired);
        }
    }

    /// <summary>Advance the clock; fire any timer whose due instant is now at or before the new time.</summary>
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = _timers.FindAll(t => !t.Fired && t.DueAt <= _now).ToArray();
            foreach (var t in due)
                t.Fired = true;
        }
        foreach (var t in due)
            t.Callback(t.State);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, _now + dueTime);
        lock (_gate)
            _timers.Add(timer);
        return timer;
    }

    private void Reschedule(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_gate)
        {
            timer.Fired = false;
            timer.DueAt = _now + dueTime;
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueAt)
        : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset DueAt { get; set; } = dueAt;
        public bool Fired { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Reschedule(this, dueTime);
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
