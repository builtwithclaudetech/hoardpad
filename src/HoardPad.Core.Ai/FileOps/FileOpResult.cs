using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// The outcome of applying one op through the shared file-ops service (M4.3): whether it was applied or
/// fail-closed (denied / parked), the uniform records it emitted (always — even a deny is logged), and —
/// for an applied & counted op — the cap decision (so the host knows whether a confirm is now required).
/// </summary>
/// <remarks>
/// The activity-log record is ALWAYS present (every op is logged — the FR-26/FR-29 audit invariant). The
/// undo-journal entry is present only when the op produced an undoable mutation (a denied op never enters
/// the undo journal — §9.3 mi5: <c>blocked</c> has no before-image).
/// </remarks>
/// <param name="Outcome">The §9.3 outcome (<c>applied</c> / <c>blocked</c> / <c>queued</c>).</param>
/// <param name="ActivityRecord">The uniform §9.3 activity-log record (always emitted, C-8).</param>
/// <param name="JournalEntry">The §9.4 undo-journal entry, or <c>null</c> when the op was not undoably applied.</param>
/// <param name="CapDecision">
/// The cap decision for a counted applied op (so the host gates on <see cref="CapDecision.RequiresConfirm"/>),
/// or <c>null</c> when the op was denied/parked or was an FR-39 replay (not counted, architecture §4.6).
/// </param>
/// <param name="ResolvedTargetPath">The canonical target path the sandbox check resolved (logged, not the raw input).</param>
/// <param name="ActivityLogWriteFailed">
/// <c>true</c> when the op was applied and journaled durably, but the activity-log append failed even after
/// one retry. The op IS applied and undoable; however, the §9.3 audit record is missing from the log. The
/// host should surface a best-effort-warning (NFR-4) and note the gap in the audit trail. This is distinct
/// from a raw propagating throw — the service NEVER propagates a raw activity-log I/O failure (MUST-FIX 2b).
/// </param>
public sealed record FileOpResult(
    ActivityOutcome Outcome,
    ActivityLogRecord ActivityRecord,
    UndoJournalEntry? JournalEntry,
    CapDecision? CapDecision,
    string? ResolvedTargetPath,
    bool ActivityLogWriteFailed = false)
{
    /// <summary><c>true</c> when the op mutated the filesystem (an undoable apply produced a journal entry).</summary>
    public bool WasApplied => Outcome == ActivityOutcome.Applied;
}
