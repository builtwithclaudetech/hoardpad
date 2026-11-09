namespace HoardPad.Core.Recovery;

/// <summary>The kind of a session tab / recovery snapshot (mirrors §9.2 <c>tabs[].kind</c>).</summary>
public enum TabKind
{
    /// <summary>A file-backed tab (has a <c>path</c>); dimension 3 applies.</summary>
    File,

    /// <summary>A <c>.hoard</c> checklist tab (has a <c>path</c>); dimension 3 applies (canonical-parsed, MA5).</summary>
    Hoard,

    /// <summary>A scratch (path-less) buffer; dimension 3 is N/A.</summary>
    Scratch,
}

/// <summary>
/// The reconciliation action the §9.2 table prescribes for one <c>tabId</c>. Each value is exactly one
/// of the table's "Action" outcomes; the orthogonal modifiers (surface-conflict, on-disk-deleted) ride
/// on <see cref="ReconciliationResult"/>.
/// </summary>
public enum ReconciliationAction
{
    /// <summary>
    /// Recover as an <b>orphan tab</b> from snapshot metadata; buffer = snapshot bytes. Used for the
    /// snapshot-present / session-tab-absent rows (file/hoard and scratch).
    /// </summary>
    RecoverOrphan,

    /// <summary>
    /// <b>Join</b> the session tab and its snapshot on <c>tabId</c>; snapshot supplies buffer bytes +
    /// cursor. Used for the snapshot-present / session-tab-present rows (file/hoard and scratch).
    /// </summary>
    Join,

    /// <summary>
    /// <b>Keep</b> the session tab and reopen from disk — there is no snapshot, so no unsaved content to
    /// recover (snapshot-absent / session-tab-present / on-disk present).
    /// </summary>
    KeepReopenFromDisk,

    /// <summary>
    /// <b>Drop</b> the tab — nothing recoverable (snapshot-absent / tab-present / file deleted, or a
    /// scratch tab with no snapshot). May carry <see cref="ReconciliationResult.SurfaceUnrecoverableLoss"/>.
    /// </summary>
    DropTab,

    /// <summary>Nothing to do — neither a snapshot nor a session tab exists for this <c>tabId</c>.</summary>
    Nothing,
}

/// <summary>
/// The outcome of reconciling one <c>tabId</c> per §9.2: the prescribed <see cref="Action"/> plus the
/// orthogonal modifiers the table's prose attaches to specific cells.
/// </summary>
/// <param name="Action">The §9.2 "Action" column value.</param>
/// <param name="SurfaceConflict">
/// <c>true</c> when the recovered buffer conflicts with newer disk content (present-changed-externally
/// rows) — keep-recovered / keep-disk / diff, per FR-19. Never silently arm a clobbering Save (B7).
/// </param>
/// <param name="OnDiskFileDeleted">
/// <c>true</c> when the tab is restored but its on-disk file is gone — the tab is treated as
/// unsaved-to-a-missing-path; the next Save is a prompted re-create, not a silent resurrection.
/// </param>
/// <param name="IsScratch"><c>true</c> when this is a scratch (path-less) buffer.</param>
/// <param name="SurfaceUnrecoverableLoss">
/// <c>true</c> only for the snapshot-absent / tab-present / file-deleted row when the tab recorded
/// <c>isDirty: true</c> but no snapshot survives — surface "unsaved content for a now-deleted file
/// could not be recovered" rather than dropping silently.
/// </param>
public sealed record ReconciliationResult(
    ReconciliationAction Action,
    bool SurfaceConflict = false,
    bool OnDiskFileDeleted = false,
    bool IsScratch = false,
    bool SurfaceUnrecoverableLoss = false);

/// <summary>
/// The host's per-<c>tabId</c> inputs to <see cref="SessionReconciliation"/>. The directory enumeration
/// (which snapshots/session entries exist) and the on-disk read are host concerns (<c>IAppDataPaths</c>,
/// C-9); this struct is the already-gathered facts the pure decision consumes.
/// </summary>
/// <param name="SnapshotPresent">Dimension 1: a self-describing recovery snapshot for this <c>tabId</c> exists.</param>
/// <param name="SessionTabPresent">Dimension 2: an entry in <c>session.json</c> for this <c>tabId</c> exists.</param>
/// <param name="OnDiskState">
/// Dimension 3: the on-disk state at <c>tab.path</c> (use <see cref="OnDiskStateClassifier"/> to derive
/// it for file/hoard tabs; <see cref="OnDiskFileState.NotApplicable"/> for scratch).
/// </param>
/// <param name="Kind">
/// The authoritative kind — the <b>snapshot's</b> <c>kind</c> wins when a snapshot is present (§9.2:
/// "the snapshot's <c>kind</c> is authoritative"); otherwise the session entry's kind.
/// </param>
/// <param name="SessionTabIsDirty">
/// Whether the session entry recorded <c>isDirty: true</c> — only consulted for the snapshot-absent /
/// tab-present / file-deleted row (the unrecoverable-loss surface).
/// </param>
public readonly record struct ReconciliationInputs(
    bool SnapshotPresent,
    bool SessionTabPresent,
    OnDiskFileState OnDiskState,
    TabKind Kind,
    bool SessionTabIsDirty = false);

/// <summary>
/// The pure §9.2 3-dimensional session/recovery reconciliation decision (architecture §5.2, binding).
/// </summary>
/// <remarks>
/// Three independent dimensions — snapshot-present? × session-tab-present? × on-disk file state — map to
/// the §9.2 table's Action column, with the prose modifiers (conflict, on-disk-deleted, scratch,
/// unrecoverable-loss) on <see cref="ReconciliationResult"/>. The directory enumeration and the on-disk
/// read are the host's job; this decision is pure and every cell is unit-tested with injected hashes.
/// Content <b>hash</b> drives dimension 3 (via <see cref="OnDiskStateClassifier"/>); <c>mtime</c> is only
/// a re-hash-skip hint.
/// </remarks>
public static class SessionReconciliation
{
    /// <summary>Decide the §9.2 reconciliation action for a single <c>tabId</c>.</summary>
    public static ReconciliationResult Decide(ReconciliationInputs inputs)
    {
        var scratch = inputs.Kind == TabKind.Scratch;

        // Dimension 1 absent: no snapshot — nothing unsaved to recover.
        if (!inputs.SnapshotPresent)
        {
            if (!inputs.SessionTabPresent)
                return new ReconciliationResult(ReconciliationAction.Nothing);

            // Session tab present, no snapshot.
            if (scratch)
                // absent | present (scratch) | N/A → Drop (a scratch buffer with no recoverable content).
                return new ReconciliationResult(ReconciliationAction.DropTab, IsScratch: true);

            return inputs.OnDiskState switch
            {
                // absent | present | present(file/hoard) → Keep; reopen from disk.
                OnDiskFileState.PresentUnchanged or OnDiskFileState.PresentChangedExternally =>
                    new ReconciliationResult(ReconciliationAction.KeepReopenFromDisk),

                // absent | present | deleted → Drop; surface unrecoverable loss iff isDirty was recorded.
                OnDiskFileState.Deleted =>
                    new ReconciliationResult(
                        ReconciliationAction.DropTab,
                        SurfaceUnrecoverableLoss: inputs.SessionTabIsDirty),

                _ => new ReconciliationResult(ReconciliationAction.DropTab),
            };
        }

        // Dimension 1 present: a snapshot exists. Dimension 2 picks Join vs RecoverOrphan; dimension 3
        // attaches the conflict / on-disk-deleted modifiers (scratch tabs skip dimension 3).
        var action = inputs.SessionTabPresent
            ? ReconciliationAction.Join
            : ReconciliationAction.RecoverOrphan;

        if (scratch)
            // present | (present|absent) (scratch) | N/A → Join / RecoverOrphan the scratch buffer.
            return new ReconciliationResult(action, IsScratch: true);

        return inputs.OnDiskState switch
        {
            // …| present-unchanged → recover/join from snapshot, no conflict (disk matches baseline).
            OnDiskFileState.PresentUnchanged =>
                new ReconciliationResult(action),

            // …| present-changed-externally → recover/join but surface a conflict (FR-19, B7).
            OnDiskFileState.PresentChangedExternally =>
                new ReconciliationResult(action, SurfaceConflict: true),

            // …| deleted → recover/join, mark on-disk-deleted (next Save is a prompted re-create).
            OnDiskFileState.Deleted =>
                new ReconciliationResult(action, OnDiskFileDeleted: true),

            _ => new ReconciliationResult(action),
        };
    }
}
