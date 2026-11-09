// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// Clock-injection convention marker (TD §2.5, architecture §1.2 / C-9). NOT a wrapper type —
/// HoardPad deliberately introduces no <c>IClock</c>: the BCL's <see cref="System.TimeProvider"/>
/// (available in net8.0) IS the seam.
/// </summary>
/// <remarks>
/// Every domain timestamp (<c>savedAt</c>, the <c>seq</c>-paired clock, activity-log <c>ts</c>,
/// undo-journal time reads) and every enforcement timer (no-reply, the SpinLock, debounce/coalesce)
/// is driven off an <b>injected</b> <see cref="System.TimeProvider"/> passed in by the composition
/// root. Domain code NEVER calls <c>DateTime.UtcNow</c>/<c>DateTime.Now</c> (C-9 —
/// <c>Microsoft.CodeAnalysis.BannedApiAnalyzers</c> fails the build if it does); tests inject a
/// fake provider so the §9.2 reconciliation table and the §9.4 undo logic stay deterministic.
/// The concrete wall clock (<c>SystemTimeProvider : TimeProvider</c>) is a HoardPad.Host artifact
/// (architecture §1.3, M2.2). There is no member to declare here — the convention is enforced by
/// constructor signatures taking <see cref="System.TimeProvider"/> plus the banned-API analyzer.
/// </remarks>
internal static class TimeProviderConvention;
