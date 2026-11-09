// BenchEchoChannel.cs — the M9.1 SC-1/SC-2 stop-instant emitter (architecture §3.5; SPIKE-1 MA15).
//
// PERF INSTRUMENTATION ONLY. This is the SOLE non-TimeProvider clock in the product (architecture §8.1):
// it uses Stopwatch (monotonic, high-resolution) because it is the measurement clock itself — it is not a
// product timer/debounce/timeout. It is INERT in every normal run and every test run: nothing constructs
// it unless the HOARDPAD_BENCH_PIPE environment variable is set (the bench launcher sets it; see
// App.OnFrameworkInitializationCompleted). It NEVER touches the AI spine (no cap-state, no hook, no
// channel) and adds zero work to a non-bench startup — the only writer is the editor's TextChanged, and
// the emitter is null in a non-bench run.
//
// When the synthetic startup keystroke has rendered (the editor's TextChanged → next layout pass),
// HoardPad writes ONE 24-byte stop record to a named pipe. The external HoardPad.Bench launcher owns the
// SC-1 START instant (immediately before Process.Start) and reads this STOP record; the SC-1 interval is
// stop − processStart and the SC-2 interval is stop − keystroke (the keystroke instant travels in the
// record, so SC-2 is exact in-process). Exactly one in-process emission (MA15); first-paint / window-shown
// are explicitly NOT the stop instant.
//
// Wire protocol (per connection, binary little-endian, 24 bytes):
//   8 bytes — Stopwatch.GetTimestamp() at the synthetic keystroke instant (the SC-2 start, in-process)
//   8 bytes — Stopwatch.GetTimestamp() at the editor's TextChanged (the SC-1/SC-2 STOP instant)
//   8 bytes — Stopwatch.Frequency
// The launcher already owns the SC-1 START (its own Stopwatch.GetTimestamp() immediately before
// Process.Start — same monotonic tick domain on this host, so the cross-process subtraction is valid).
// It computes SC-1 = textChanged − processStart and SC-2 = textChanged − keystroke (the latter fully
// in-process, so it is exact and immune to any process-launch / focus jitter).

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace HoardPad.App.Bench;

/// <summary>
/// The named-pipe echo channel — the SC-1/SC-2 stop instant (architecture §3.5, SPIKE-1 MA15). Active
/// ONLY when the bench launcher supplies a pipe name via <c>HOARDPAD_BENCH_PIPE</c>; otherwise nothing
/// constructs it and the product runs unchanged. Uses <see cref="Stopwatch"/> — the single justified
/// non-<see cref="TimeProvider"/> clock in the product (it is the measurement clock, not a product timer).
/// </summary>
internal sealed class BenchEchoChannel : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _server;
    private volatile bool _connected;
    private volatile bool _emitted;
    private long _keystrokeTs;
    private bool _disposed;

    public BenchEchoChannel(string pipeName) => _pipeName = pipeName;

    /// <summary>
    /// Record the synthetic keystroke instant (the SC-2 start, in-process Stopwatch domain) immediately
    /// before injecting the synthetic character into the editor. Captured here so the SC-2 interval is the
    /// exact in-process keystroke→TextChanged delta, independent of any cross-process launch jitter.
    /// </summary>
    public void MarkKeystroke() => _keystrokeTs = Stopwatch.GetTimestamp();

    /// <summary>Start listening for the launcher's read connection. Returns immediately; the accept runs on a pool thread.</summary>
    public void StartListening()
    {
        _server = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        if (_server is null)
            return;
        try
        {
            await _server.WaitForConnectionAsync().ConfigureAwait(false);
            _connected = true;
        }
        catch (ObjectDisposedException) { /* disposed before a client connected — bench not running */ }
        catch (Exception ex)
        {
            // Instrumentation only: a failed accept must never take the app down.
            Console.Error.WriteLine($"[BenchEchoChannel] accept error: {ex.Message}");
        }
    }

    /// <summary>
    /// Emit the 24-byte stop record (keystroke instant + the SC-1/SC-2 stop instant + frequency). Called
    /// from the editor's <c>TextChanged</c> on the UI thread — a single 24-byte pipe write, negligible
    /// (architecture §3.5). MA15: exactly one emission per process — repeated calls after the first are
    /// no-ops. Non-blocking and fully swallowing: if no client is connected (or the bench closed the read
    /// end) it silently skips, so a non-bench run or a torn-down bench never affects the app.
    /// </summary>
    public void EmitStopTimestamp()
    {
        if (_emitted || !_connected || _server is null || !_server.IsConnected)
            return;
        try
        {
            long ts = Stopwatch.GetTimestamp();
            long freq = Stopwatch.Frequency;

            Span<byte> buf = stackalloc byte[24];
            BinaryPrimitives.WriteInt64LittleEndian(buf[..8], _keystrokeTs);
            BinaryPrimitives.WriteInt64LittleEndian(buf[8..16], ts);
            BinaryPrimitives.WriteInt64LittleEndian(buf[16..], freq);
            _server.Write(buf);
            _server.Flush();
            _emitted = true; // MA15: exactly one in-process emission.
        }
        catch
        {
            // Swallow — the bench may have closed the read end; instrumentation must not crash the app.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _server?.Dispose();
    }
}
