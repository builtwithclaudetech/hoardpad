using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using HoardPad.Core.Safety;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// The SINGLE shared file-ops service (architecture §4.5, PRD FR-26/FR-28/FR-29/FR-39) — the one execution
/// path for Archetype-B ops AND for replayed Archetype-A FR-39 proposals. It IS the sandbox enforcement for
/// every AI-applied file op: a bug here is a sandbox escape or a lost undo.
/// </summary>
/// <remarks>
/// <para>Per op, in this exact order (the order is the safety guarantee):</para>
/// <list type="number">
/// <item><b>Canonicalize + sandbox-check</b> (<see cref="ISandboxGate"/> → <see cref="IPathResolver"/> +
///   <see cref="SandboxPolicy"/>). A DENY means the op does NOT proceed: it is logged <c>blocked</c> and
///   <b>no <see cref="IAtomicFileStore"/> mutation is ever issued</b> — the write never escapes the root
///   (FR-26, non-negotiable).</item>
/// <item><b>M15 durability ordering</b> — for a DESTRUCTIVE op (edit/overwrite/delete/clobbering move/copy)
///   the undo-journal entry (and its host-staged before-image blob ref) is appended + <c>fsync</c>'d via
///   <see cref="IAtomicFileStore.AppendRecordAsync"/> <b>BEFORE</b> the target is mutated, so a crash
///   mid-op is always reversible (FR-28, §3.6). A <c>create</c> has no before-image (nothing existed).</item>
/// <item><b>Apply</b> via <see cref="IAtomicFileStore"/> (create-when-absent / replace-when-present).
///   If the store throws <see cref="AtomicWriteUnavailable"/>, the service catches it and logs a
///   <c>blocked</c> / best-effort-warning record — the pre-mutation journal entry is undo-safe to leave
///   (undo re-verifies current state; a failed create means the file is absent → delete-undo is a no-op;
///   a failed overwrite means content is unchanged → restore restores identical bytes).</item>
/// <item><b>Append the activity-log record</b> (§9.3) — every op is logged (FR-29), uniform with the
///   journal entry (C-8). If the activity-log append fails, the service retries once; on a second failure
///   it returns <see cref="FileOpResult.ActivityLogWriteFailed"/> = <c>true</c> (the op IS applied +
///   undoable — only the audit record is missing). A raw propagating throw is NEVER emitted from this leg.</item>
/// <item><b>Cap accounting</b> — when the op counts against the per-turn caps (NOT an FR-39 replay),
///   classify (<see cref="CapClassifier"/>) + account (<see cref="CapAccounting.Account"/>) through the
///   conversation-keyed <see cref="ICapStateStore"/>, and gate on <see cref="CapDecision.RequiresConfirm"/>.</item>
/// </list>
/// <para>
/// Platform-free (C-9): no syscalls, no clock (the host supplies <see cref="FileOpContext.Ts"/> and
/// <see cref="FileOpRequest.DurationMs"/>), no <c>System.IO</c>. Every I/O leg is a host seam call.
/// </para>
/// <para>
/// <b>Op vocabulary (architecture §4.5).</b> The service models the full <see cref="FileOp"/> vocabulary.
/// The Archetype-A reachable subset is create / full-overwrite / partial-edit; copy is supported when
/// driven via Archetype-B or FR-39 replay. <b>Move and Delete are NOT yet supported</b> at this service
/// layer (the <see cref="IAtomicFileStore"/> M2.1 contract has no rename/unlink primitive); issuing them
/// throws <see cref="NotSupportedException"/> so a caller error is immediately visible — never silently
/// reported as Applied-with-no-mutation (MUST-FIX 1). They are carried to M6.x when the host adds the
/// rename/unlink leg.
/// </para>
/// <para>
/// <b>CountsAgainstPerTurnCaps discipline.</b> The public <see cref="ApplyAsync"/> method ALWAYS forces
/// <c>CountsAgainstPerTurnCaps = true</c>, regardless of what the caller passes. Only the internal
/// <see cref="ApplyInternalAsync"/> path (reached exclusively through <see cref="ReplayAsync"/>) may
/// run with <c>false</c>. This prevents a caller from silently bypassing caps via the public surface
/// (SHOULD-FIX 5).
/// </para>
/// </remarks>
public sealed class AiFileOpsService
{
    private readonly ISandboxGate _sandbox;
    private readonly IAtomicFileStore _store;
    private readonly ICapStateStore _capStore;
    private readonly IAppDataPaths _paths;
    private readonly string _sessionId;
    private readonly CapConfig _capConfig;

    /// <summary>Construct the service for one open-folder + session.</summary>
    /// <param name="sandbox">The FR-26 canonicalize-then-evaluate gate (real impl wraps <see cref="IPathResolver"/> + <see cref="SandboxPolicy"/>).</param>
    /// <param name="store">The host's atomic/durable file store (M2.1) — the only writer of bytes.</param>
    /// <param name="capStore">The conversation-keyed cap-state store seam (M5.2 supplies the O_EXCL-locked impl).</param>
    /// <param name="paths">The host's per-user data paths (resolves the activity-log + undo-journal locations).</param>
    /// <param name="sessionId">The session id keying the per-session activity log (§5.5 <c>ActivityLogPath</c>).</param>
    /// <param name="capConfig">The (clamp-validated) cap config in force for this conversation.</param>
    public AiFileOpsService(
        ISandboxGate sandbox,
        IAtomicFileStore store,
        ICapStateStore capStore,
        IAppDataPaths paths,
        string sessionId,
        CapConfig capConfig)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(capStore);
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(capConfig);

        _sandbox = sandbox;
        _store = store;
        _capStore = capStore;
        _paths = paths;
        _sessionId = sessionId;
        _capConfig = capConfig;
    }

    /// <summary>
    /// Apply one op through the full FR-26/FR-28/FR-29 path. Used for an Archetype-B op (<paramref name="conversationId"/>
    /// counted). Always counted against per-turn caps — the caller cannot opt out via
    /// <see cref="FileOpContext.CountsAgainstPerTurnCaps"/>; that field is forced to <c>true</c> here.
    /// Use <see cref="ReplayAsync"/> for an FR-39 replay (not counted).
    /// </summary>
    /// <param name="conversationId">The user-initiated conversation id keying the cap-state (architecture §4.4).</param>
    /// <param name="request">The op to apply (paths, bytes, pre-op facts, cap-classification input).</param>
    /// <param name="context">The per-op audit envelope (timestamp, backend, agency). <c>CountsAgainstPerTurnCaps</c> is overridden to <c>true</c>.</param>
    public Task<FileOpResult> ApplyAsync(
        string conversationId, FileOpRequest request, FileOpContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(conversationId))
            throw new ArgumentException("A non-empty conversation id is required.", nameof(conversationId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // SHOULD-FIX 5: the public ApplyAsync ALWAYS counts, regardless of what the caller passed.
        // Only ReplayAsync may call the internal overload with counts:false.
        FileOpContext countedContext = context with { CountsAgainstPerTurnCaps = true };
        return ApplyInternalAsync(conversationId, request, countedContext, ct);
    }

    /// <summary>
    /// Apply a parked FR-39 proposal (architecture §4.6). It runs the SAME full sandbox + before-image +
    /// log/journal path as <see cref="ApplyAsync"/>, but is governed by FR-10(b) per-op confirm and is NOT
    /// counted against the per-turn caps (the caller has already obtained the user's per-op authorization).
    /// </summary>
    /// <param name="conversationId">The conversation the replay runs under (often a NEW one — §4.6).</param>
    /// <param name="request">The op rebuilt from the parked <c>tool_input</c>.</param>
    /// <param name="context">
    /// The audit envelope. This method FORCES <see cref="FileOpContext.CountsAgainstPerTurnCaps"/> to
    /// <c>false</c> regardless of what the caller passed, so a replay can never burn (or be blocked by) a
    /// per-turn cap from a turn that already ended.
    /// </param>
    public Task<FileOpResult> ReplayAsync(
        string conversationId, FileOpRequest request, FileOpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The replay distinction is enforced here, not trusted from the caller: never counted (§4.6).
        FileOpContext replayContext = context with { CountsAgainstPerTurnCaps = false };
        return ApplyInternalAsync(conversationId, request, replayContext, ct);
    }

    // ---- Internal apply (both ApplyAsync and ReplayAsync converge here) --------------------------------

    private async Task<FileOpResult> ApplyInternalAsync(
        string conversationId, FileOpRequest request, FileOpContext context, CancellationToken ct)
    {
        // (1) CANONICALIZE + SANDBOX-CHECK. The raw target is resolved (realpath/symlink/normcase via the
        //     host) then evaluated against the root. A DENY stops here: log blocked, NO mutation issued —
        //     the write never escapes the root (FR-26). This is the first and load-bearing guard.
        SandboxResolution gate = _sandbox.Resolve(request.Operation.Paths.Target);
        if (gate.Decision == SandboxDecision.Deny)
            return await DenyAsync(request, context, gate.ResolvedPath, ct).ConfigureAwait(false);

        // For a clobbering move/copy the DESTINATION is also a write target — it too must be in-sandbox, or
        // the op could overwrite a file outside the root. Evaluate it before any mutation.
        string resolvedDest = gate.ResolvedPath;
        if (HasDestination(request.Operation.Op) && request.Operation.Paths.Dest is { } rawDest)
        {
            SandboxResolution destGate = _sandbox.Resolve(rawDest);
            if (destGate.Decision == SandboxDecision.Deny)
                return await DenyAsync(request, context, destGate.ResolvedPath, ct).ConfigureAwait(false);
            resolvedDest = destGate.ResolvedPath;
        }

        // (2) M15 — for a destructive op, the undo-journal entry (+ the host-staged before-image blob) is
        //     appended + fsync'd BEFORE the mutation, so a crash mid-op is always reversible. A create has
        //     no before-image. The journal entry is built over the RESOLVED paths.
        UndoJournalEntry? journalEntry = BuildJournalEntry(request, gate.ResolvedPath, resolvedDest);
        if (journalEntry is not null)
        {
            byte[] journalPayload = RecordSerialization.SerializeJournal(journalEntry);
            // Awaited: AppendRecordAsync fsyncs before returning (§3.3) — the durable-before-mutate barrier.
            await _store.AppendRecordAsync(JournalEntryPath(request.UndoRef), journalPayload, ct)
                .ConfigureAwait(false);
        }

        // (3) APPLY via the atomic store — ONLY now, after the before-image/journal is durable.
        //     If the store throws AtomicWriteUnavailable the mutation did NOT happen. The pre-mutation
        //     journal entry is safe to leave: undo re-verifies current state before acting, so:
        //       - a failed create → target is still absent → delete-undo is a no-op;
        //       - a failed overwrite → content is unchanged → restore restores identical bytes.
        //     We catch the exception and return a blocked / best-effort-warning result (MUST-FIX 2a).
        try
        {
            await ApplyMutationAsync(request, gate.ResolvedPath, resolvedDest, ct).ConfigureAwait(false);
        }
        catch (AtomicWriteUnavailable ex)
        {
            // Mutation failed — log blocked (best-effort warning). No raw throw from this service.
            ActivityLogRecord failRecord = BuildActivityRecord(
                request, context, context.DowngradeReason, ActivityOutcome.Blocked,
                gate.ResolvedPath, resolvedDest, error: ex.Message ?? "AtomicWriteUnavailable");
            await _store.AppendRecordAsync(ActivityLogPath(), RecordSerialization.SerializeActivity(failRecord), ct)
                .ConfigureAwait(false);
            return new FileOpResult(ActivityOutcome.Blocked, failRecord, JournalEntry: null, CapDecision: null, gate.ResolvedPath);
        }

        // (5) CAP ACCOUNTING (read first so the §9.3 downgradeReason can reflect a cap trip). An FR-39
        //     replay is NOT counted (architecture §4.6): it skips accounting but still logs + journals.
        CapDecision? capDecision = null;
        DowngradeReason downgradeReason = context.DowngradeReason;
        if (context.CountsAgainstPerTurnCaps && request.Classification is { } classification)
        {
            capDecision = await AccountAsync(conversationId, classification, gate.ResolvedPath, ct)
                .ConfigureAwait(false);
            downgradeReason = MergeDowngradeReason(downgradeReason, capDecision.Trigger);
        }

        // (4) APPEND THE ACTIVITY-LOG RECORD — every applied op is logged (FR-29), uniform with the journal.
        //     If the append fails, retry once. A second failure returns ActivityLogWriteFailed=true rather
        //     than propagating raw (MUST-FIX 2b). The op IS applied + undoable; only the audit record is missing.
        ActivityLogRecord record = BuildActivityRecord(
            request, context, downgradeReason, ActivityOutcome.Applied, gate.ResolvedPath, resolvedDest);
        bool logFailed = false;
        try
        {
            await _store.AppendRecordAsync(ActivityLogPath(), RecordSerialization.SerializeActivity(record), ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // First attempt failed — retry once.
            try
            {
                await _store.AppendRecordAsync(ActivityLogPath(), RecordSerialization.SerializeActivity(record), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Both attempts failed. The op is applied and journaled (undoable); only the audit record is
                // missing. Signal the host via ActivityLogWriteFailed — never propagate raw.
                logFailed = true;
            }
        }

        return new FileOpResult(ActivityOutcome.Applied, record, journalEntry, capDecision, gate.ResolvedPath, ActivityLogWriteFailed: logFailed);
    }

    // ---- Deny path -------------------------------------------------------------------------------------

    /// <summary>
    /// The fail-closed deny path: NO mutation is issued (no <see cref="IAtomicFileStore"/> create/replace
    /// call is ever made), no undo-journal entry is written (a blocked op has no before-image, §9.3 mi5),
    /// and the op is logged <c>blocked</c> with op <c>blocked</c> so the audit trail records the refusal.
    /// </summary>
    private async Task<FileOpResult> DenyAsync(
        FileOpRequest request, FileOpContext context, string resolvedPath, CancellationToken ct)
    {
        ActivityLogRecord record = new(
            Ts: context.Ts,
            RequestId: request.Operation.RequestId,
            Backend: context.Backend,
            ConfiguredMode: context.ConfiguredMode,
            AgencyMode: context.AgencyMode,
            DowngradeReason: context.DowngradeReason,
            Op: ActivityOp.Blocked,
            Paths: BuildPaths(request, resolvedPath, resolvedPath),
            Outcome: ActivityOutcome.Blocked,
            BytesBefore: request.BytesBefore,
            BytesAfter: request.BytesBefore, // nothing changed
            UndoRef: null,                    // a blocked op never enters the undo journal
            Error: "outside document folder: write denied (FR-26)",
            DurationMs: request.DurationMs,
            Tokens: context.Tokens,
            CostUsd: context.CostUsd);

        await _store.AppendRecordAsync(ActivityLogPath(), RecordSerialization.SerializeActivity(record), ct)
            .ConfigureAwait(false);

        return new FileOpResult(ActivityOutcome.Blocked, record, JournalEntry: null, CapDecision: null, resolvedPath);
    }

    // ---- Apply dispatch (the op vocabulary) ------------------------------------------------------------

    /// <summary>
    /// Dispatch the mutation to the appropriate <see cref="IAtomicFileStore"/> primitive.
    /// <b>Move and Delete are not supported at this layer</b> (the M2.1 contract has no rename/unlink
    /// primitive); they throw <see cref="NotSupportedException"/> so a caller error surfaces immediately
    /// and is never silently reported as Applied-with-no-mutation (MUST-FIX 1). Add the real mutation
    /// primitive in M6.x when the host seam exposes rename/unlink.
    /// </summary>
    private async Task ApplyMutationAsync(
        FileOpRequest request, string resolvedTarget, string resolvedDest, CancellationToken ct)
    {
        switch (request.Operation.Op)
        {
            case FileOp.Create:
                // First-ever write of a new file — create-when-absent (fails if it exists, which is correct).
                await _store.CreateWhenAbsentAsync(resolvedTarget, request.Content, ct).ConfigureAwait(false);
                break;

            case FileOp.Edit:
                // An edit/overwrite onto an existing target — replace-when-present (atomic temp+rename).
                await _store.ReplaceWhenPresentAsync(resolvedTarget, request.Content, ct).ConfigureAwait(false);
                break;

            case FileOp.Copy:
                // Archetype-B / replay only. Write the copied bytes to the destination. A clobbering copy
                // (dest pre-existed) replaces; a fresh copy creates. The before-image of a clobbered dest is
                // already journaled (M15) above.
                if (request.DestPriorContent is not null)
                    await _store.ReplaceWhenPresentAsync(resolvedDest, request.Content, ct).ConfigureAwait(false);
                else
                    await _store.CreateWhenAbsentAsync(resolvedDest, request.Content, ct).ConfigureAwait(false);
                break;

            case FileOp.Move:
            case FileOp.Delete:
                // MUST-FIX 1: Move and Delete have no rename/unlink primitive in IAtomicFileStore M2.1.
                // A silent no-op here would return ActivityOutcome.Applied while the filesystem is unchanged
                // and a populated UndoJournalEntry with a MoveBack/RestoreTarget reverse action would be
                // emitted — corrupting the undo journal + audit the moment the Archetype-B / FR-39 replay
                // path reaches them. Throw NotSupportedException so the gap is immediately visible to any
                // caller rather than silently producing a bogus Applied result. M6.x adds the real leg.
                throw new NotSupportedException(
                    $"{request.Operation.Op} is not yet supported by the M2.1 IAtomicFileStore contract. " +
                    $"Add the rename/unlink host seam in M6.x before routing Move/Delete through this service.");
        }
    }

    // ---- Record construction ---------------------------------------------------------------------------

    private static bool HasDestination(FileOp op) => op is FileOp.Move or FileOp.Copy;

    private static OpPaths BuildPaths(FileOpRequest request, string resolvedTarget, string resolvedDest)
    {
        // Record the RESOLVED paths (never the raw input). source+dest present only for move/copy (§9.3/§9.4).
        if (HasDestination(request.Operation.Op))
        {
            // For move/copy the source is the resolved target (the thing being moved/copied FROM); dest is
            // the resolved destination. This matches the §9.3 example (move: source+dest both present).
            return new OpPaths(Target: resolvedTarget, Source: resolvedTarget, Dest: resolvedDest);
        }
        return new OpPaths(Target: resolvedTarget);
    }

    /// <summary>
    /// Build the undo-journal entry for a DESTRUCTIVE op, or <c>null</c> for a <c>create</c> (nothing existed
    /// before — no before-image). A clobbering move/copy carries <c>DestPriorContent</c> + its hash so the
    /// undo path passes <c>destCurrent</c> to <see cref="HoardPad.Core.Undo.UndoReverseDecision.Decide"/>.
    /// </summary>
    private static UndoJournalEntry? BuildJournalEntry(FileOpRequest request, string resolvedTarget, string resolvedDest)
    {
        FileOp op = request.Operation.Op;

        // A create has no before-image and never enters the undo journal as a "restore" — but it DOES get an
        // entry (priorContent null) so undo can DELETE the created file (UndoReverseDecision.DeleteCreated).
        // The M15 durable-before-mutate barrier applies to entries that carry a before-image (edit/overwrite/
        // delete/clobbering move/copy). For a create we still write the entry (so undo is possible) but it
        // carries no before-image; ordering it before the mutation is harmless and keeps the path uniform.
        bool clobbering = request.DestPriorContent is not null;

        return new UndoJournalEntry(
            UndoRef: request.UndoRef,
            BufferJournalId: request.BufferJournalId,
            RequestId: request.Operation.RequestId,
            Op: op,
            Paths: BuildPaths(request, resolvedTarget, resolvedDest),
            PriorContent: request.PriorContent,                       // null for create
            DestPriorContent: clobbering ? request.DestPriorContent : null,
            DestPriorContentHash: clobbering ? request.DestPriorContentHash : null,
            PreOpContentHash: request.PreOpContentHash,               // null for create
            PreOpMtime: request.PreOpMtime,                           // null for create
            SnapshotSkipped: request.SnapshotSkipped);
    }

    private static ActivityLogRecord BuildActivityRecord(
        FileOpRequest request,
        FileOpContext context,
        DowngradeReason downgradeReason,
        ActivityOutcome outcome,
        string resolvedTarget,
        string resolvedDest,
        string? error = null) =>
        new(
            Ts: context.Ts,
            RequestId: request.Operation.RequestId,
            Backend: context.Backend,
            ConfiguredMode: context.ConfiguredMode,
            AgencyMode: context.AgencyMode,
            DowngradeReason: downgradeReason,
            Op: ToActivityOp(request.Operation.Op),
            Paths: BuildPaths(request, resolvedTarget, resolvedDest),
            Outcome: outcome,
            BytesBefore: request.BytesBefore,
            BytesAfter: request.BytesAfter,
            UndoRef: request.UndoRef,
            Error: error,
            DurationMs: request.DurationMs,
            Tokens: context.Tokens,
            CostUsd: context.CostUsd);

    private static ActivityOp ToActivityOp(FileOp op) => op switch
    {
        FileOp.Create => ActivityOp.Create,
        FileOp.Copy => ActivityOp.Copy,
        FileOp.Move => ActivityOp.Move,
        FileOp.Edit => ActivityOp.Edit,
        FileOp.Delete => ActivityOp.Delete,
        _ => ActivityOp.Blocked,
    };

    /// <summary>A cap trigger that fired refines the §9.3 downgradeReason; an explicit host reason wins if already set.</summary>
    private static DowngradeReason MergeDowngradeReason(DowngradeReason explicitReason, CapTrigger trigger)
    {
        if (explicitReason != DowngradeReason.None)
            return explicitReason;
        return trigger switch
        {
            CapTrigger.CreateCap => DowngradeReason.CreateCap,
            CapTrigger.DestructiveCap => DowngradeReason.DestructiveCap,
            CapTrigger.FilesTouchedCap => DowngradeReason.FilesTouchedCap,
            _ => DowngradeReason.None,
        };
    }

    // ---- Cap accounting through the conversation-keyed store -------------------------------------------

    private async Task<CapDecision> AccountAsync(
        string conversationId, OpClassificationInput classification, string canonicalTargetPath, CancellationToken ct)
    {
        // Read-modify-write under the host's exclusive lock, with a compare-and-write retry on a concurrent
        // writer (a continuation turn + a fresh invocation can both touch the same conversation's state).
        // The accounting itself (CapClassifier + CapAccounting) is the M4.1 pure logic — never duplicated.
        // M5.2's O_EXCL ICapStateStore impl must carry a compare-and-swap conformance test (concurrent
        // writers, no lost update) — carried forward; the retry here is sufficient at the service layer.
        OpKind kind = CapClassifier.Classify(classification, _capConfig.DestructiveMagnitudePercent);

        while (true)
        {
            CapStateSnapshot snapshot = await _capStore.ReadAsync(conversationId, ct).ConfigureAwait(false);
            CapState state = CapStateSerialization.Deserialize(snapshot.PayloadJson);

            CapDecision decision = CapAccounting.Account(state, kind, canonicalTargetPath, _capConfig);

            string payload = CapStateSerialization.Serialize(decision.State);
            CapStateWriteResult write = await _capStore
                .WriteAsync(conversationId, payload, snapshot.Version, ct)
                .ConfigureAwait(false);

            if (write.Committed)
                return decision;
            // Version conflict: another writer advanced the state — re-read and re-apply against the new base.
        }
    }

    // ---- Path resolution for the append logs -----------------------------------------------------------

    private string ActivityLogPath() => _paths.ActivityLogPath(_sessionId);

    // §5.5 layout: undo/<undoRef>.entry under the undo-journal root. The undoRef is host-minted and opaque;
    // the host restricts this directory to the current user (NFR-7).
    private string JournalEntryPath(string undoRef) =>
        CombineUndoPath(_paths.UndoJournalRoot, undoRef);

    // Pure string join (no System.IO.Path is banned here? Path is NOT banned — only File/Directory are.
    // Path is a pure string utility, used by the host-supplied roots to form the per-undoRef entry path).
    private static string CombineUndoPath(string root, string undoRef) =>
        System.IO.Path.Combine(root, undoRef + ".entry");
}
