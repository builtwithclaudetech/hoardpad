// SessionReconciler.cs — drives the PURE Core §9.2 reconciliation (architecture §5.2).
//
// This is the App's reconcile ORCHESTRATOR. It gathers the already-read facts (the session.json index,
// the recovery-directory scan, the per-tab on-disk probes) and DRIVES the Core decision functions — it
// never re-implements any reconciliation arithmetic:
//   - SnapshotTieBreak.Resolve     — dedupe duplicate-tabId snapshots (same-machine crash-mid-write).
//   - PathAuthority.Resolve        — which of {snapshot, session-entry} owns the path (Save-As stale-path).
//   - OnDiskStateClassifier.Classify — dimension 3 (hash-authoritative present/changed/deleted).
//   - SessionReconciliation.Decide — the 3-D table cell (action + conflict/deleted/scratch modifiers).
//
// The reconcile is async only because the on-disk probe (dimension 3) reads the filesystem; the DECISION
// itself is pure Core. The result is a flat list of RestoredTab the workspace materializes.

using HoardPad.Core.Recovery;

namespace HoardPad.App.Session;

/// <summary>One reconciled tab the workspace should materialize, with the §9.2 modifiers attached.</summary>
/// <param name="TabId">The durable tab id.</param>
/// <param name="Action">The §9.2 action (recover-orphan / join / keep-reopen / drop).</param>
/// <param name="Kind">The authoritative kind (the snapshot's kind wins when present, §9.2).</param>
/// <param name="Path">The authoritative path (PathAuthority-resolved), or null for a scratch buffer.</param>
/// <param name="Language">The language id to restore.</param>
/// <param name="Cursor">The caret position to restore.</param>
/// <param name="RecoveredText">The unsaved buffer text from the snapshot, or null when there is no snapshot.</param>
/// <param name="SurfaceConflict">
/// <c>true</c> when disk changed externally vs the recovered buffer — the workspace shows the restore-conflict
/// dialog (keep-recovered / keep-disk / show-diff, FR-19) before arming a clobbering Save.
/// </param>
/// <param name="OnDiskFileDeleted"><c>true</c> when the on-disk file is gone (next Save is a prompted re-create).</param>
/// <param name="IsScratch"><c>true</c> for a scratch (path-less) buffer.</param>
/// <param name="SurfaceUnrecoverableLoss"><c>true</c> when dirty content for a now-deleted file could not be recovered.</param>
public sealed record RestoredTab(
    string TabId,
    ReconciliationAction Action,
    TabKind Kind,
    string? Path,
    string Language,
    CursorPosition? Cursor,
    string? RecoveredText,
    bool SurfaceConflict = false,
    bool OnDiskFileDeleted = false,
    bool IsScratch = false,
    bool SurfaceUnrecoverableLoss = false);

/// <summary>The full reconcile result: the active tab id and the restored tabs.</summary>
/// <param name="ActiveTabId">The tab to focus on restore (null if none survived).</param>
/// <param name="Tabs">The restored tabs (drops are excluded; the caller materializes these in order).</param>
public sealed record SessionReconcileResult(
    string? ActiveTabId,
    IReadOnlyList<RestoredTab> Tabs);

/// <summary>Drives the pure Core §9.2 reconciliation from the already-gathered session/recovery facts.</summary>
internal sealed class SessionReconciler
{
    private readonly IRecoveryFileSystem _fs;

    public SessionReconciler(IRecoveryFileSystem fs) => _fs = fs;

    /// <summary>
    /// Reconcile a session index + the recovery-directory scan into the tabs to materialize (§9.2). The
    /// recovery directory scan is authoritative; the session index is the rebuildable hint. Every tabId in
    /// EITHER source is reconciled; duplicate-tabId snapshots are deduped via the Core tie-break first.
    /// </summary>
    public async Task<SessionReconcileResult> ReconcileAsync(
        SessionState session,
        IReadOnlyList<RecoverySnapshot> snapshots,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(snapshots);

        // Dedupe duplicate-tabId snapshots via the §9.2 same-machine tie-break (keep the survivor; a
        // genuinely-unorderable duplicate surfaces a conflict, never a silent guess).
        (IReadOnlyDictionary<string, RecoverySnapshot> snapshotByTab, ISet<string> ambiguous) =
            DedupeSnapshots(snapshots);

        var sessionByTab = session.Tabs.ToDictionary(t => t.Id);

        // FS-2 (Decision A dependency, hostile-review CRITICAL fix): restore order MUST exactly equal the
        // SAVED session.Tabs array order — never re-derive an order via set-hashing. The prior
        // `HashSet<string>(snapshotByTab.Keys).UnionWith(sessionByTab.Keys)` seeded from snapshot ids
        // FIRST, so any DIRTY tab (which has a recovery snapshot) could enumerate ahead of an
        // earlier-saved CLEAN tab, silently reordering restore vs save. FS-1 tier (ii) derives the AI
        // write root from "the first path-bearing tab in restore order", so a silent reorder here
        // silently moves the AI write boundary onto the wrong tab's folder — this is the safety-critical
        // reason the order must be exact, not merely cosmetic.
        var orderedTabIds = new List<string>(session.Tabs.Count);
        var seenTabIds = new HashSet<string>();
        foreach (SessionTab sessionTab in session.Tabs)
        {
            if (seenTabIds.Add(sessionTab.Id))
                orderedTabIds.Add(sessionTab.Id);
        }

        // Snapshot-only orphans (a tabId with a recovery snapshot but NO session.json entry — e.g. a
        // crash before the very first persist ever ran) have no genuine "save order" to honor. Append
        // them AFTER every session-ordered tab, in a DETERMINISTIC order (sorted by id) — never the
        // recovery-directory's Directory.EnumerateFiles order, which is filesystem/OS-dependent.
        foreach (string orphanId in snapshotByTab.Keys
            .Where(id => !seenTabIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal))
        {
            seenTabIds.Add(orphanId);
            orderedTabIds.Add(orphanId);
        }

        var restored = new List<RestoredTab>();
        foreach (string tabId in orderedTabIds)
        {
            ct.ThrowIfCancellationRequested();
            snapshotByTab.TryGetValue(tabId, out RecoverySnapshot? snapshot);
            sessionByTab.TryGetValue(tabId, out SessionTab? sessionTab);

            RestoredTab? tab = await ReconcileOneAsync(
                tabId, snapshot, sessionTab, ambiguous.Contains(tabId), ct).ConfigureAwait(false);
            if (tab is not null)
                restored.Add(tab);
        }

        // Only keep the prior active id if its tab actually survived reconciliation.
        string? activeId = restored.Any(t => t.TabId == session.ActiveTabId)
            ? session.ActiveTabId
            : restored.FirstOrDefault()?.TabId;

        return new SessionReconcileResult(activeId, restored);
    }

    private async Task<RestoredTab?> ReconcileOneAsync(
        string tabId,
        RecoverySnapshot? snapshot,
        SessionTab? sessionTab,
        bool ambiguousDuplicate,
        CancellationToken ct)
    {
        // The snapshot's kind is authoritative when present (§9.2); else the session entry's kind.
        TabKind kind = snapshot?.Kind ?? sessionTab?.Kind ?? TabKind.Scratch;

        // PathAuthority (§9.2): which of {snapshot, session-entry} owns the path. Save-As updates the
        // session entry's seq above the snapshot's, so a Save-As-then-crash restores at the NEW path.
        string? path = ResolvePath(snapshot, sessionTab);

        // Dimension 3: classify the on-disk file (hash-authoritative). Scratch → N/A.
        OnDiskFileState onDisk = OnDiskFileState.NotApplicable;
        if (kind != TabKind.Scratch && path is not null && snapshot is not null && snapshot.BaselineContentHash is not null)
        {
            CurrentDiskFile current = await _fs.ProbeAsync(path, kind, ct).ConfigureAwait(false);
            onDisk = OnDiskStateClassifier.Classify(snapshot.BaselineContentHash, snapshot.BaselineMtime, current);
        }
        else if (kind != TabKind.Scratch && path is not null)
        {
            // No snapshot (or no baseline): the session entry alone. Probe only to tell present vs deleted —
            // there is no baseline to compare against, so a present file is "keep + reopen from disk".
            CurrentDiskFile current = await _fs.ProbeAsync(path, kind, ct).ConfigureAwait(false);
            onDisk = current.Exists ? OnDiskFileState.PresentUnchanged : OnDiskFileState.Deleted;
        }

        var inputs = new ReconciliationInputs(
            SnapshotPresent: snapshot is not null,
            SessionTabPresent: sessionTab is not null,
            OnDiskState: onDisk,
            Kind: kind,
            SessionTabIsDirty: sessionTab?.IsDirty ?? false);

        // Drive the pure §9.2 table.
        ReconciliationResult result = SessionReconciliation.Decide(inputs);

        if (result.Action == ReconciliationAction.Nothing || result.Action == ReconciliationAction.DropTab)
        {
            // Dropped tabs are not materialized; an unrecoverable-loss drop is surfaced via a NOTE the
            // caller can show, but it produces no tab. (We carry the flag on a materialized-but-empty tab
            // only when there is genuinely something to show — here, nothing to restore.)
            return result.SurfaceUnrecoverableLoss
                ? new RestoredTab(tabId, result.Action, kind, path, sessionTab?.Language ?? "plaintext",
                    Cursor: null, RecoveredText: null, SurfaceUnrecoverableLoss: true)
                : null;
        }

        // A duplicate-tabId snapshot that could not be ordered is a genuine ambiguity → force a conflict
        // surface (§9.2 seq-missing-or-tied case), even if dimension 3 said unchanged.
        bool surfaceConflict = result.SurfaceConflict || ambiguousDuplicate;

        string? recoveredText = snapshot is not null && result.Action != ReconciliationAction.KeepReopenFromDisk
            ? SessionStore.DecodeContent(snapshot)
            : null;

        CursorPosition? cursor = snapshot?.Cursor ?? sessionTab?.Cursor;
        string language = snapshot?.Language ?? sessionTab?.Language ?? "plaintext";

        return new RestoredTab(
            tabId,
            result.Action,
            kind,
            path,
            language,
            cursor,
            recoveredText,
            SurfaceConflict: surfaceConflict,
            OnDiskFileDeleted: result.OnDiskFileDeleted,
            IsScratch: result.IsScratch,
            SurfaceUnrecoverableLoss: result.SurfaceUnrecoverableLoss);
    }

    private static string? ResolvePath(RecoverySnapshot? snapshot, SessionTab? sessionTab)
    {
        if (snapshot is null)
            return sessionTab?.Path;
        if (sessionTab is null)
            return snapshot.Path;

        // Both present: drive PathAuthority (§9.2) — the session entry wins only when strictly more recent.
        PathSource source = PathAuthority.Resolve(snapshot.Seq, sessionTab.Seq);
        return source == PathSource.SessionEntry ? sessionTab.Path : snapshot.Path;
    }

    /// <summary>
    /// Group snapshots by tabId and resolve duplicates via the §9.2 same-machine tie-break. Returns the
    /// survivor per tabId plus the set of tabIds whose duplicate was UNORDERABLE (forces a conflict surface).
    /// </summary>
    private static (IReadOnlyDictionary<string, RecoverySnapshot> Winners, ISet<string> Ambiguous)
        DedupeSnapshots(IReadOnlyList<RecoverySnapshot> snapshots)
    {
        var winners = new Dictionary<string, RecoverySnapshot>();
        var ambiguous = new HashSet<string>();

        foreach (RecoverySnapshot snapshot in snapshots)
        {
            if (!winners.TryGetValue(snapshot.TabId, out RecoverySnapshot? incumbent))
            {
                winners[snapshot.TabId] = snapshot;
                continue;
            }

            // Two snapshots of one tabId — drive the Core tie-break (keep higher seq; fall back to savedAt;
            // surface a conflict when neither orders them).
            TieBreakOutcome outcome = SnapshotTieBreak.Resolve(incumbent.Order, snapshot.Order);
            switch (outcome)
            {
                case TieBreakOutcome.KeepA:
                    // incumbent wins — nothing to change.
                    break;
                case TieBreakOutcome.KeepB:
                    winners[snapshot.TabId] = snapshot;
                    break;
                case TieBreakOutcome.SurfaceConflict:
                default:
                    ambiguous.Add(snapshot.TabId);
                    // Keep the incumbent as the buffer to show alongside the conflict prompt.
                    break;
            }
        }

        return (winners, ambiguous);
    }
}
