// FoldingService.cs — the FR-9 debounced background folding recompute (architecture §3.3, the single most
// important threading-correctness rule lives here). Owns: the 300 ms debounce, the off-UI background
// recompute, cancellation on document replace, and the UI-thread UpdateFoldings() commit — guarded by the
// snapshot-version check that drops + reschedules a result computed against a stale document version.
//
// THREADING MODEL (architecture §3.3, binding):
//   - The debounce timer is TimeProvider-driven (C-9, §8.1) so the 300 ms window is deterministic under a
//     FakeTimeProvider in tests — matching the M6.5 HoardWriteQueue idiom. The timer callback NEVER touches
//     an AvaloniaEdit primitive off the UI thread: it only marshals a "begin recompute" hop back to the UI
//     thread (via the injected post-to-UI delegate), preserving the architecture's "no timer-thread callback
//     touches FoldingManager" intent (the DispatcherTimer pin's purpose).
//   - On the UI thread an IMMUTABLE snapshot is captured — the document's full text + its authoritative
//     per-line offset table (FoldSnapshot.Capture) + the ITextSourceVersion + scope — then Task.Run computes
//     the fold list off the UI thread over that snapshot ONLY. No live, mutable TextDocument is read off the
//     UI thread (a concurrent keystroke can therefore neither tear the read nor throw).
//   - The commit hops back to the UI thread; the version guard runs THERE, immediately before UpdateFoldings.
//
// THE SNAPSHOT-VERSION GUARD (load-bearing): capture document.Version when the recompute is scheduled; on
// the UI-thread commit, if document.Version has advanced past the captured version (CompareAge != 0), DROP
// the stale fold result and RESCHEDULE — never commit fold offsets computed against an older snapshot
// (committing stale offsets ships fold-offset corruption on typing). Cancellation on document replace
// supersedes the guard (a replaced document is a fresh schedule, not a reschedule of the old one).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace HoardPad.App.Editor;

/// <summary>
/// The FR-9 folding service: debounces text changes (300 ms, <see cref="TimeProvider"/>-driven), recomputes
/// fold regions off the UI thread via the <see cref="FoldingStrategySelector"/>, and commits them on the UI
/// thread under the snapshot-version guard. One instance per editor view; <see cref="SetDocument"/> on a
/// document replace cancels any in-flight recompute (a tab switch / new open). The owning view calls
/// <see cref="Stop"/> on detach (the view manages this lifecycle manually, like its TextMate installation —
/// it is not itself an <see cref="IDisposable"/>).
/// </summary>
internal sealed class FoldingService
{
    /// <summary>The OQ-TD-3 default debounce window: 300 ms after the last change (architecture §3.3).</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly TimeProvider _timeProvider;
    private readonly Func<TextDocument?> _getDocument;
    private readonly Func<string?> _getScope;
    private readonly Action<List<NewFolding>> _commit; // runs on the UI thread (UpdateFoldings).
    private readonly Action<Action> _postToUi;         // marshals an action onto the UI thread.
    private readonly Action<Action> _runBackground;    // runs the pure compute off the UI thread (Task.Run).

    private ITimer? _debounceTimer;
    private bool _disposed;

    // The recompute "generation": bumped on every SetDocument (document replace). A background recompute
    // started before the bump checks its captured generation on commit and is discarded if it is stale — the
    // "cancelled on document replace" rule (the version guard handles in-place edits within one generation).
    private int _generation;

    /// <param name="timeProvider">The injected clock driving the 300 ms debounce (C-9, deterministic in tests).</param>
    /// <param name="getDocument">Reads the live <see cref="TextDocument"/> on the UI thread (null = no document).</param>
    /// <param name="getScope">Reads the active TextMate scope on the UI thread (null/empty = indentation fallback).</param>
    /// <param name="commit">Applies the computed folds — invoked on the UI thread (wraps <see cref="FoldingManager.UpdateFoldings"/>).</param>
    /// <param name="postToUi">Marshals an action onto the UI thread (wraps <c>Dispatcher.UIThread.Post</c>).</param>
    /// <param name="runBackground">
    /// Runs the pure fold compute off the UI thread (production passes <see cref="Task.Run(Action)"/>); a
    /// test passes a synchronous runner so the snapshot→compute→commit hops are deterministic.
    /// </param>
    public FoldingService(
        TimeProvider timeProvider,
        Func<TextDocument?> getDocument,
        Func<string?> getScope,
        Action<List<NewFolding>> commit,
        Action<Action> postToUi,
        Action<Action>? runBackground = null)
    {
        _timeProvider = timeProvider;
        _getDocument = getDocument;
        _getScope = getScope;
        _commit = commit;
        _postToUi = postToUi;
        _runBackground = runBackground ?? (work => _ = Task.Run(work));
    }

    /// <summary>
    /// Schedule a debounced recompute (call on the UI thread from the editor's <c>TextChanged</c>). Restarts
    /// the 300 ms window on each call so a burst of keystrokes collapses to one recompute after the last one.
    /// </summary>
    public void Schedule()
    {
        if (_disposed)
            return;

        CancelTimer();
        _debounceTimer = _timeProvider.CreateTimer(
            _ => _postToUi(BeginRecompute),
            state: null,
            dueTime: Debounce,
            period: Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Notify the service that the bound document was replaced (a tab switch / new open / reinterpret): cancel
    /// any pending debounce and invalidate any in-flight background recompute (the "cancelled on document
    /// replace" rule), then schedule a fresh recompute for the new document. Call on the UI thread.
    /// </summary>
    public void SetDocument()
    {
        if (_disposed)
            return;

        Interlocked.Increment(ref _generation);
        Schedule();
    }

    // Runs on the UI thread (posted from the debounce timer). Captures an IMMUTABLE snapshot — the document
    // text + its authoritative line-offset table (FoldSnapshot), its ITextSourceVersion, and the scope — then
    // kicks the pure compute off the UI thread over that snapshot ONLY. The live document is captured too, but
    // solely so the UI-thread Commit guard can compare identity/version; the background compute never reads it.
    private void BeginRecompute()
    {
        if (_disposed)
            return;

        TextDocument? document = _getDocument();
        if (document is null)
            return;

        int generation = Volatile.Read(ref _generation);
        ITextSourceVersion? scheduledVersion = document.Version;
        string? scope = _getScope();
        FoldSnapshot snapshot = FoldSnapshot.Capture(document);

        _runBackground(() =>
        {
            List<NewFolding> result = FoldingStrategySelector.ComputeFoldings(snapshot, scope);
            _postToUi(() => Commit(document, generation, scheduledVersion, result));
        });
    }

    // Runs on the UI thread. THE SNAPSHOT-VERSION GUARD: if the document version advanced since the snapshot
    // was taken (an in-place edit between snapshot and commit), the offsets are stale — drop them and
    // reschedule rather than apply fold offsets against an older snapshot. A generation bump (document
    // replace) discards the result outright (no reschedule — SetDocument already scheduled the new document).
    private void Commit(TextDocument document, int generation, ITextSourceVersion? scheduledVersion, List<NewFolding> result)
    {
        if (_disposed)
            return;

        // Document was replaced after this recompute started — discard; the replace already rescheduled.
        if (generation != Volatile.Read(ref _generation))
            return;

        // The live document is no longer the one this result was computed for, or the version advanced past
        // the captured snapshot: the offsets are stale. Drop + reschedule (NEVER commit stale offsets).
        TextDocument? live = _getDocument();
        if (!ReferenceEquals(live, document)
            || scheduledVersion is null
            || live.Version is null
            || scheduledVersion.CompareAge(live.Version) != 0)
        {
            Schedule();
            return;
        }

        _commit(result);
    }

    private void CancelTimer()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    /// <summary>Cancel the pending debounce and stop accepting schedules (call on the UI thread, on detach).</summary>
    public void Stop()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelTimer();
    }
}
