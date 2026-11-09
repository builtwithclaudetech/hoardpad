namespace HoardPad.Core.Tests;

/// <summary>
/// A minimal fixed-instant <see cref="System.TimeProvider"/> for the reconciliation + undo decision tests
/// (M1.4). HoardPad's domain takes an injected <see cref="System.TimeProvider"/> (the C-9 clock seam, TD
/// §2.5) — there is no <c>IClock</c> wrapper. No charset/testing NuGet is on the Core.Tests stack, so this
/// is the hand-rolled fake: <see cref="GetUtcNow"/> returns a caller-supplied instant and never touches the
/// wall clock, keeping the §9.2 reconciliation cells and §9.4 undo branches deterministic.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Advance the fake clock so recorded instants (e.g. <c>savedAt</c>, <c>preOpMtime</c>) differ deterministically.</summary>
    public void Advance(TimeSpan by) => _now += by;
}
