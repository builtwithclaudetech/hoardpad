// SystemTimeProvider.cs — the concrete wall clock (M2.2).
//
// The TimeProviderConvention (HoardPad.Core) is deliberately NOT an IClock wrapper:
// the BCL's System.TimeProvider IS the seam (TD §2.5, architecture §1.2 / C-9). The
// domain takes an injected TimeProvider for every timestamp; this is the named
// concrete type the composition root (HoardPad.App) resolves for the real run. Tests
// inject a FakeTimeProvider instead, so the §9.2 reconciliation table and §9.4 undo
// logic stay deterministic without a real wall-clock dependency.

using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// The production wall-clock <see cref="TimeProvider"/> the composition root injects
/// (architecture §1.3, M2.2; the <see cref="TimeProviderConvention"/> seam). Delegates to
/// <see cref="TimeProvider.System"/> — the simplest correct subclass — so domain code that
/// takes an injected <see cref="TimeProvider"/> gets the real clock at runtime while tests
/// substitute a fake. Provided as a named type because the convention references it by name.
/// </summary>
public sealed class SystemTimeProvider : TimeProvider
{
    private readonly TimeProvider _inner = System;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);
}
