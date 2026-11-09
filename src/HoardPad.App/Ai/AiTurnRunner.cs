// AiTurnRunner.cs — the M6.6 production turn runner (architecture §3.4 / §4.2), M11 live-write wired.
//
// The OS-facing implementation of IAiTurnRunner: it composes the protected Archetype-A safety spine
// and runs the launch + stream pump + process-tree kill OFF the UI thread (NFR-8). It does NOT
// re-implement any safety logic — it CALLS it:
//   - ArchetypeAOrchestration.Begin composes the FR-24 §4.1 launch contract (ClaudeLaunchContract)
//     under the self-test-gated agency posture; the turn request is constructed via the guarded
//     AgentTurnRequest ctor (never hand-assembled args — the disarm-token guard is by construction);
//   - IAgentBackend.StartTurnAsync launches the process; IAgentTurn.StreamAsync pumps stream-json;
//   - StreamJsonTaxonomy parses each line for write tool-uses; ArchetypeAOrchestration folds them for
//     the MA14 liveness verdict, degrading fail-closed on the first unmatched write;
//   - IAgentTurn.KillTreeAsync is the cancellation primitive (Stop / no-reply kill).
//
// M11 (docs/escalation-ai-live-write-path.md §4, docs/m11-channel-protocol.md §6) — the live-write
// path's App CONSUMPTION half. Per armed turn this runner ALSO:
//   - opens the per-turn IHookObservationChannel endpoint (the OS IPC primitive the host owns + the
//     hook connects to) and delivers its address into the launch via the EXISTING launch-env seam
//     (HookConfig.ObserveChannelAddress + ObserveMode → BuildHookSettings → HookEnvironment), merged
//     ON TOP of the session sandbox/cap launchEnv passed to StartTurnAsync;
//   - consumes endpoint.ObserveAsync OFF the UI thread, CONCURRENTLY with the stdout pump, folding
//     each observed HookOpEvent into a file-op chip (LiveOpProjection.ToChip — blocked on a hook-deny),
//     the MA14 observed-callback feed (LiveOpProjection.ToHookCallback → OnHookCallback), and the live
//     cap-meter READ (LiveCapMeter.Read of the O_EXCL cap-state via ICapStateStore.ReadAsync — never a
//     parallel counter, spec §5 invariant 4);
//   - disposes the endpoint at turn end (disposal is the fail-closed trigger — a still-blocked hook
//     reads a closed channel and denies, protocol §4).
// This runner adds NO safety/decision logic: the deny-by-default floor, AND-narrowing, and
// fail-closed-on-channel-failure all live in the hook + HookObservationChannel; it consumes them.
//
// The view-model consumes the parsed TurnEvent sequence and marshals each onto the dispatcher; this
// runner stays off the UI thread. The cap snapshots it emits come from the SAME conversation-keyed
// cap-state the spine uses — so the meters the view-model renders are the one shared state.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;

namespace HoardPad.App.Ai;

/// <summary>
/// Production <see cref="IAiTurnRunner"/> over the Archetype-A spine (architecture §4.2). Launches
/// <c>claude -p</c>, pumps <c>stream-json</c> off the UI thread, folds the MA14 liveness verdict, and
/// kills the process tree on Stop (NFR-8). Session-scoped: constructed per open-folder turn with the
/// resolved sandbox root + the host-built <c>--settings</c> hook JSON (M6.3 supplies these). M11 adds
/// the per-turn hook-observation channel (live chips / cap meter / approve-each / MA14 feed).
/// </summary>
/// <remarks>
/// <para>
/// Safety-by-construction: the turn request is built via <see cref="AgentTurnRequest"/>'s guarded ctor,
/// which calls <see cref="LaunchArgsSafety.AssertSafe"/> — a launch arg list carrying <c>--cwd</c> or a
/// <c>bypassPermissions</c>/<c>--dangerously-skip-permissions</c> token cannot be instantiated.
/// </para>
/// <para>
/// M11 live-write (docs/m11-channel-protocol.md §6): the observe loop runs CONCURRENTLY with the stdout
/// pump and both funnel into a single merge channel, so the MA14 orchestration state is folded by ONE
/// reader (no concurrent mutation of the immutable turn state). The observe channel is opened only when
/// the host supplied one AND write agency is armed for the turn; a read-only or no-channel turn behaves
/// exactly as before. The cap meter is a READ of the O_EXCL cap-state (invariant 4), never a counter.
/// </para>
/// </remarks>
public sealed class AiTurnRunner : IAiTurnRunner
{
    private readonly IAgentBackend _backend;
    private readonly NormcasedPath _workingRoot;
    private readonly string _settingsJsonPath;
    private readonly decimal _maxBudgetUsd;
    private readonly IReadOnlyDictionary<string, string> _launchEnv;

    // M11 live-write seams (all OPTIONAL — null ⇒ the runner behaves exactly as the pre-M11 path).
    private readonly IHookObservationChannel? _channel;
    private readonly IToolPermissionHookHost? _hookHost;
    private readonly ICapStateStore? _capStore;
    private readonly string? _conversationKey;
    // The per-turn channel ADDRESS the host injects into the hook env (HOARDPAD_OBSERVE_CHANNEL). It is
    // deterministic for the (workingRoot, conversationKey) pair, so the binder resolves it ONCE (via the
    // channel's own ResolveChannelAddress) and hands it here — it is the EXACT address every OpenForTurnAsync
    // on this (root, key) binds, so the injected env value and the live socket/pipe cannot drift. Null when
    // no live channel is wired.
    private readonly string? _channelAddress;

    // PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): the host grants-file writer. Per armed turn carrying
    // non-empty grant paths, the runner writes them to an app-controlled JSON file via this seam and injects
    // HOARDPAD_FILE_GRANTS onto the launch so the hook's grant leg admits exactly those out-of-folder paths.
    // Null ⇒ no Archetype-A grant delivery (byte-identical to today — the env var is never set).
    private readonly IFileGrantsWriter? _grantsWriter;

    // Volatile because _turn is written on the UI continuation (after StartTurnAsync) and read from a
    // ThreadPool timer thread in OnNoReply → StopAsync (the NFR-8 no-reply kill path). Without volatile
    // a stale null read would skip KillTreeAsync, leaving the process tree alive.
    private volatile IAgentTurn? _turn;

    /// <summary>Construct the runner for one open-folder session.</summary>
    /// <param name="backend">The host's <c>claude -p</c> launcher + process-tree killer (M5.2).</param>
    /// <param name="workingRoot">The resolved open-folder root the child runs in (WorkingDirectory; no --cwd).</param>
    /// <param name="settingsJsonPath">The host-built <c>--settings</c> hook JSON path (M5.2 BuildHookSettings).</param>
    /// <param name="maxBudgetUsd">The FR-24 <c>--max-budget-usd</c> ceiling (must be positive).</param>
    /// <param name="launchEnv">
    /// The M11 launch-env (docs/escalation-m11-launch-env-seam.md Option A): the hook env the host
    /// applies on the spawned turn — HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE (the fail-closed
    /// prerequisite). The optional per-turn HOARDPAD_OBSERVE_* channel vars are MERGED on top of this
    /// per turn (they are mode-dependent), so this base map carries only the session-stable sandbox/cap
    /// env. Passed (with the merge) to <see cref="IAgentBackend.StartTurnAsync"/>; this runner adds no
    /// safety logic of its own — it is pure env-plumbing (the env is assembled by <c>AiSessionBinder</c>).
    /// </param>
    /// <param name="channel">
    /// The M11 per-turn <see cref="IHookObservationChannel"/> (live chips / cap meter / approve-each /
    /// MA14 feed). <c>null</c> ⇒ no live observation (the pre-M11 behavior). Required together with
    /// <paramref name="hookHost"/>, <paramref name="capStore"/>, and <paramref name="conversationKey"/>.
    /// </param>
    /// <param name="hookHost">The host hook host — used ONLY to build the per-turn observe env via the seam.</param>
    /// <param name="capStore">The O_EXCL cap-state store — the live meter is a READ of it (invariant 4).</param>
    /// <param name="conversationKey">The conversation-keyed cap-state key the channel + the cap read use.</param>
    /// <param name="channelAddress">
    /// The pre-resolved per-turn channel address (the binder resolves it via the channel's own
    /// ResolveChannelAddress for this (workingRoot, conversationKey)). It is the EXACT address every
    /// OpenForTurnAsync on this pair binds, so the env value and the live primitive cannot drift.
    /// </param>
    /// <param name="grantsWriter">
    /// PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): the host grants-file writer. When present together
    /// with a non-null <paramref name="conversationKey"/>, an armed turn carrying non-empty
    /// <see cref="TurnRequest.FileGrantPaths"/> gets a host-written grants file and a HOARDPAD_FILE_GRANTS
    /// launch-env var. <c>null</c> ⇒ no Archetype-A grant delivery (byte-identical to the pre-PANEL-A2 path).
    /// </param>
    public AiTurnRunner(
        IAgentBackend backend, NormcasedPath workingRoot, string settingsJsonPath, decimal maxBudgetUsd,
        IReadOnlyDictionary<string, string> launchEnv,
        IHookObservationChannel? channel = null,
        IToolPermissionHookHost? hookHost = null,
        ICapStateStore? capStore = null,
        string? conversationKey = null,
        string? channelAddress = null,
        IFileGrantsWriter? grantsWriter = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (workingRoot.IsDefault)
            throw new ArgumentException("The working root must be a resolved NormcasedPath.", nameof(workingRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJsonPath);
        if (maxBudgetUsd <= 0m)
            throw new ArgumentOutOfRangeException(nameof(maxBudgetUsd), maxBudgetUsd, "The --max-budget-usd ceiling must be positive.");
        ArgumentNullException.ThrowIfNull(launchEnv);

        _backend = backend;
        _workingRoot = workingRoot;
        _settingsJsonPath = settingsJsonPath;
        _maxBudgetUsd = maxBudgetUsd;
        _launchEnv = launchEnv;
        _channel = channel;
        _hookHost = hookHost;
        _capStore = capStore;
        _conversationKey = conversationKey;
        _channelAddress = channelAddress;
        _grantsWriter = grantsWriter;
    }

    // True when every M11 live-observation dependency is present (the channel is wired). When false the
    // runner takes the pre-M11 path (stdout pump only) — a no-channel/read-only turn is unchanged.
    private bool LiveChannelWired =>
        _channel is not null && _hookHost is not null && _capStore is not null &&
        !string.IsNullOrEmpty(_conversationKey) && !string.IsNullOrEmpty(_channelAddress);

    /// <inheritdoc />
    public async IAsyncEnumerable<TurnEvent> RunAsync(
        TurnRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Compose the FR-24 §4.1 launch contract under the resolved agency posture (the self-test gate ran
        // in the view-model via ArmAgencyAsync). The orchestration owns the flag set; we never author flags.
        // BUG-5: thread the per-turn model/effort overrides (null = backend default) from TurnRequest.
        AgencyMode mode = request.EffectiveMode;
        SelfTestResult? selfTest = request.WriteAgencyArmed ? new SelfTestResult(Passed: true, Detail: null) : null;
        (ArchetypeATurnState state, var launchArgs) = ArchetypeAOrchestration.Begin(
            mode, selfTest, _maxBudgetUsd, _settingsJsonPath, request.ModelId, request.EffortLevel);

        // Safety-by-construction: the guarded AgentTurnRequest ctor asserts the FR-24 §4.1 invariants — a
        // disarm token cannot be smuggled in (LaunchArgsSafety.AssertSafe runs in the ctor).
        var turnRequest = new AgentTurnRequest(request.Prompt, _workingRoot, launchArgs);

        // M11 live-write: open the per-turn observation endpoint (when armed) BEFORE the launch so the
        // hook can connect, and deliver its address into the launch env via the seam. A read-only / no-
        // channel turn opens nothing — the observe env is empty and the runner is the pre-M11 path.
        IHookObservationEndpoint? endpoint = null;
        IReadOnlyDictionary<string, string> launchEnv = _launchEnv;
        if (request.WriteAgencyArmed && LiveChannelWired)
        {
            endpoint = await _channel!.OpenForTurnAsync(_workingRoot, _conversationKey!, ct).ConfigureAwait(false);
            launchEnv = MergeObserveEnv(_launchEnv, request.EffectiveMode);
        }

        // PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): write the turn's per-file grants and inject
        // HOARDPAD_FILE_GRANTS BEFORE launch, mirroring the observe-endpoint lifecycle (write → launch →
        // delete). Fires only for an armed turn carrying non-empty grant paths with a wired writer + key;
        // otherwise the env var is never set and behavior is byte-identical to today (root-only, FR-MD-10).
        // Composed ON TOP of launchEnv so the observe merge (if any) is preserved. Fail-closed: a write
        // fault sets NO env var and does NOT abort/widen the turn — the out-of-folder writes are simply
        // denied by the hook (FR-MD-10 / AC-MD-6). The written path is threaded to RunTurnAsync's finally
        // for deletion (the same disposal path the endpoint uses).
        string? grantsFilePath = null;
        if (request.WriteAgencyArmed
            && _grantsWriter is not null
            && !string.IsNullOrEmpty(_conversationKey)
            && request.FileGrantPaths is { Count: > 0 } grantPaths)
        {
            grantsFilePath = await TryWriteGrantsFileAsync(grantPaths, ct).ConfigureAwait(false);
            if (grantsFilePath is not null)
            {
                var withGrants = new Dictionary<string, string>(launchEnv, StringComparer.Ordinal)
                {
                    ["HOARDPAD_FILE_GRANTS"] = grantsFilePath,
                };
                launchEnv = withGrants;
            }
        }

        await foreach (TurnEvent ev in RunTurnAsync(turnRequest, state, launchEnv, endpoint, grantsFilePath, ct).ConfigureAwait(false))
            yield return ev;
    }

    // Write the turn's grants file via the seam, returning its path — or null on ANY fault (fail-closed,
    // FR-MD-10 / AC-MD-6). A null return means the caller never sets HOARDPAD_FILE_GRANTS, so the hook grant
    // leg stays inert and the turn proceeds ROOT-ONLY — the out-of-folder writes are denied by the hook, the
    // narrower safe set. We never launch pointing at a stale/failed grants file.
    private async Task<string?> TryWriteGrantsFileAsync(
        IReadOnlyList<string> grantPaths, CancellationToken ct)
    {
        try
        {
            return await _grantsWriter!.WriteAsync(_conversationKey!, grantPaths, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A grants-file write fault falls back to root-only — never widens (FR-MD-10).
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    // The turn body factored out so the endpoint can be disposed via a finally without an `await` inside a
    // try with a `yield` in the same method (an iterator cannot yield inside a try-with-catch/finally that
    // wraps an await over the yielded sequence). The endpoint disposal IS the fail-closed trigger (§4).
    private async IAsyncEnumerable<TurnEvent> RunTurnAsync(
        AgentTurnRequest turnRequest, ArchetypeATurnState state,
        IReadOnlyDictionary<string, string> launchEnv, IHookObservationEndpoint? endpoint,
        string? grantsFilePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            LaunchDescriptor launcher = _backend.ResolveLauncher(configuredPath: null);
            // M11 launch-env seam: deliver the host-built sandbox/cap (+ optional observe-channel) env onto
            // the spawned turn. Without it the hook reads an unset sandbox root and fail-closed-denies every
            // write — the prerequisite the seam exists to satisfy (escalation §2).
            IAgentTurn turn = await _backend.StartTurnAsync(launcher, turnRequest, launchEnv, ct).ConfigureAwait(false);
            _turn = turn;

            try
            {
                await foreach (TurnEvent ev in MergeStreamAndObserveAsync(turn, state, endpoint, ct).ConfigureAwait(false))
                    yield return ev;
            }
            finally
            {
                if (ReferenceEquals(_turn, turn))
                    _turn = null;
                await turn.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // Dispose the observation endpoint at turn end — disposal tears down the socket/pipe, so any
            // still-blocked hook reads a closed channel and DENIES (protocol §4 fail-closed). Disposed
            // AFTER the turn so a late ask still resolves to the (already-cancelled ⇒ deny) confirm.
            if (endpoint is not null)
                await endpoint.DisposeAsync().ConfigureAwait(false);

            // PANEL-A2: delete the per-turn grants file (the grant is per-turn, FR-MD-9). Best-effort inside
            // the seam — a missing/failed delete is a no-op (the file is overwritten next turn and is never
            // read outside an armed turn). Deleted AFTER the turn so the hook could read it for the whole turn.
            if (grantsFilePath is not null)
                _grantsWriter?.Delete(grantsFilePath);
        }
    }

    // Run the stdout pump and (when live) the observe loop CONCURRENTLY, merging both into one ordered
    // TurnEvent sequence. Both producers funnel SIGNALS into a single-reader merge channel so the MA14
    // orchestration `state` is folded by exactly one reader (the immutable turn state is never mutated by
    // two threads). The reader yields the resulting TurnEvents; it never touches a process or the cap-state
    // store directly beyond the projection reads. (docs/m11-channel-protocol.md §6.)
    private async IAsyncEnumerable<TurnEvent> MergeStreamAndObserveAsync(
        IAgentTurn turn, ArchetypeATurnState state, IHookObservationEndpoint? endpoint,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var merge = Channel.CreateUnbounded<MergeItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = endpoint is null,   // one producer (stdout) when no observe loop runs.
        });

        // A merge-scoped CTS linked to the turn token. The STDOUT stream is the turn's authoritative end —
        // when it completes the writer, the merge reader exits, and we cancel this CTS to unwind the observe
        // loop (which otherwise idles waiting for more reports until the endpoint is disposed). Cancelling it
        // here keeps the observe loop from outliving the turn (and lets us await both producers without a
        // deadlock against the endpoint disposal that happens in the OUTER finally).
        using var mergeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Producer 1: the stdout pump. Forwards each raw stream line as a signal; the reader folds it for
        // MA14 + extracts assistant text. Does NOT complete the writer (the coordinator does, after BOTH
        // producers drain — otherwise an observe event in flight when the stream ends would be dropped).
        Task stdoutPump = PumpStdoutAsync(turn, merge.Writer, mergeCts.Token);

        // Producer 2 (live only): the observe loop. Forwards each HookOpEvent as a signal; the reader
        // projects it into a chip + MA14 callback + a cap-meter read. Runs OFF the UI thread, concurrently.
        Task observePump = endpoint is null
            ? Task.CompletedTask
            : PumpObserveAsync(endpoint, merge.Writer, mergeCts.Token);

        // Coordinator: the STDOUT stream is the turn's authoritative end. When it finishes, cancel the merge
        // CTS to unwind the (idle-waiting) observe loop, let any already-available observe writes drain, then
        // complete the writer so the reader stops. Completing only AFTER both producers finish is what makes
        // the merge deterministic — no enqueued observe event is lost to a premature Complete.
        Task coordinator = CompleteWhenBothDrainAsync(merge.Writer, stdoutPump, observePump, mergeCts);

        try
        {
            // The single reader: fold each signal into `state` and yield the projected TurnEvents. Because
            // ONE reader owns `state`, the immutable turn state is never mutated by two threads (MA14 correct).
            await foreach (MergeItem item in merge.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item.Kind == MergeKind.StreamLine)
                {
                    (state, IReadOnlyList<TurnEvent> streamEvents) = FoldStreamLine(item.RawJsonLine!, state);
                    foreach (TurnEvent ev in streamEvents)
                        yield return ev;
                    if (state.IsDegraded)
                        yield break;   // the runner stops the stream on a degrade (the finally kills the tree).
                }
                else // MergeKind.HookEvent
                {
                    (state, TurnEvent? chip) = await FoldHookEventAsync(item.HookEvent!, state, mergeCts.Token).ConfigureAwait(false);
                    if (chip is not null)
                        yield return chip;
                }
            }
        }
        finally
        {
            // The merge reader has finished (the stdout stream ended, a degrade broke out, or the turn was
            // cancelled). Cancel the merge CTS so the observe loop unwinds (it ends WITHOUT needing the
            // endpoint disposed first — no deadlock against the outer finally's DisposeAsync, the hook-facing
            // fail-closed teardown), then await the coordinator (which awaits both producers) so a
            // non-cancellation fault surfaces. Cancellation is benign (Stop / no-reply kill).
            if (!mergeCts.IsCancellationRequested)
                mergeCts.Cancel();
            await AwaitQuietlyAsync(coordinator).ConfigureAwait(false);
        }

        // The stream ended (the writer completed) and both producers were awaited in the finally above.
        // Resolve the MA14 verdict — any write still pending a hook callback (no observed callback) degrades
        // the turn fail-closed (TD §4.7).
        (state, LivenessVerdict verdict) = ArchetypeAOrchestration.ResolveLiveness(state);
        if (verdict.ShouldDegrade)
            yield return TurnEvent.Degrade("Backend degrade detected at turn end — unmatched write tool-use.");
    }

    // The stdout pump producer: read parsed stream-json events and forward each raw line into the merge
    // channel. Does NOT complete the writer — the coordinator does, after BOTH producers drain (so an
    // observe event in flight when the stream ends is never lost). A non-cancellation stream fault is
    // re-thrown so the coordinator surfaces it as the writer's completion fault.
    private static async Task PumpStdoutAsync(
        IAgentTurn turn, ChannelWriter<MergeItem> writer, CancellationToken ct)
    {
        try
        {
            await foreach (AgentStreamEvent raw in turn.StreamAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteAsync(MergeItem.Stream(raw.RawJsonLine), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop / no-reply kill — benign; the reader unwinds on the same token.
        }
        catch (ChannelClosedException)
        {
            // The coordinator already completed the writer; the stream end is then moot.
        }
    }

    // The coordinator: complete the merge writer only AFTER both producers drain. The stdout stream is the
    // turn's authoritative end — when it finishes, cancel the merge CTS to stop the observe loop from
    // producing new events (ObserveAsync uses the token), then await the observe pump to let any
    // already-dequeued event finish its WriteAsync (which writes with CancellationToken.None so it is not
    // interrupted by the cancel), then complete the writer. Completing only after both finish ensures that
    // an observe event dequeued at the same instant stdout ends is not lost: it finishes its write to
    // CancellationToken.None, the observe pump exits, then the writer completes. A non-cancellation
    // producer fault becomes the writer's completion fault (surfaced to the reader).
    private static async Task CompleteWhenBothDrainAsync(
        ChannelWriter<MergeItem> writer, Task stdoutPump, Task observePump, CancellationTokenSource mergeCts)
    {
        Exception? fault = null;
        try
        {
            await stdoutPump.ConfigureAwait(false);   // the stream end is the turn's authoritative end.
        }
#pragma warning disable CA1031 // A stream fault is surfaced via the writer completion, not propagated raw.
        catch (Exception ex)
        {
            fault = ex is OperationCanceledException ? null : ex;
        }
#pragma warning restore CA1031

        // Stop the observe loop from PRODUCING new events (ObserveAsync uses the token), then await the
        // observe pump — in-flight events that already dequeued finish their write to CancellationToken.None,
        // so no dequeued-but-not-yet-written event is dropped at the cancel instant.
        if (!mergeCts.IsCancellationRequested)
            mergeCts.Cancel();
        await AwaitQuietlyAsync(observePump).ConfigureAwait(false);

        writer.TryComplete(fault);
    }

    // The observe producer (live only): yield each HookOpEvent into the merge channel. The PRODUCTION gate
    // (ObserveAsync) uses the merge-CTS token so the loop stops producing when the turn's stdout ends.
    // The WRITE to the merge channel uses CancellationToken.None so an event that was already dequeued
    // from ObserveAsync at the cancel instant finishes its write instead of throwing OCE and being dropped.
    // Without this split an observe event dequeued-but-not-yet-written at the cancel instant would be
    // lost, producing a spurious end-of-turn MA14 degrade on a write that the hook already resolved.
    // The approve-each ask round is handled INLINE by the channel's own ObserveAsync (it drives the App
    // confirm + the decision write-back); this loop only consumes the resulting report events.
    private static async Task PumpObserveAsync(
        IHookObservationEndpoint endpoint, ChannelWriter<MergeItem> writer, CancellationToken ct)
    {
        try
        {
            await foreach (HookOpEvent ev in endpoint.ObserveAsync(ct).ConfigureAwait(false))
                await writer.WriteAsync(MergeItem.Hook(ev), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Turn ended / Stop / no-reply kill — the observe loop stops producing (ObserveAsync threw).
        }
        catch (ChannelClosedException)
        {
            // The merge writer completed; a trailing write is moot (turn ended).
        }
    }

    // Fold one stdout stream line into the MA14 state and project assistant text. Mirrors the pre-M11 fold:
    // parse write tool-uses, degrade fail-closed on a degrade, then extract assistant text. Pure over the
    // state-in / (state-out, events) — the single reader owns the threading.
    //
    // MF1.4 addition: also emit a WriteToolInput event for each parsed write tool-use so the panel can
    // pre-populate its _toolInputCache (tool_use_id → raw input JSON) BEFORE the hook ask arrives. The
    // panel consumes these events silently; existing tests that drive only the scripted runner emit no
    // WriteToolInput events, so the cache stays empty and replay remains disabled — zero impact.
    private static (ArchetypeATurnState State, IReadOnlyList<TurnEvent> Events) FoldStreamLine(
        string rawJsonLine, ArchetypeATurnState state)
    {
        ImmutableArray<WriteToolUseEvent> writes =
            StreamJsonTaxonomy.ParseWriteToolUses(rawJsonLine);
        foreach (WriteToolUseEvent w in writes)
            state = ArchetypeAOrchestration.OnWriteToolUse(state, w);

        if (state.IsDegraded)
            return (state, [TurnEvent.Degrade("Backend degrade detected — agency set to approve-each for this session.")]);

        var events = new List<TurnEvent>(writes.Length + 1);

        // Emit one WriteToolInput event per parsed write tool-use (MF1.4 replay cache feed).
        foreach (WriteToolUseEvent w in writes)
            events.Add(TurnEvent.WriteInput(w.ToolUseId, w.RawInputJson));

        string? text = ExtractAssistantText(rawJsonLine);
        if (text is { Length: > 0 })
            events.Add(TurnEvent.Assistant(text));

        return (state, events);
    }

    // Fold one observed HookOpEvent (M11 §6 step 2): feed the MA14 observed-callback, project the chip, and
    // read the live cap-meter from the O_EXCL cap-state. The chip's WasApplied reflects the hook's verdict
    // (a hook-deny renders a Blocked chip with NO cap advance — the cap-state read is the authority). Adds
    // NO safety decision: the hook already decided; this re-shapes its report for the panel.
    private async Task<(ArchetypeATurnState State, TurnEvent? Chip)> FoldHookEventAsync(
        HookOpEvent ev, ArchetypeATurnState state, CancellationToken ct)
    {
        // MA14 observed-callback feed (the existing M4.2 fold — reused, not re-implemented).
        HookCallbackObservation? cb = LiveOpProjection.ToHookCallback(ev);
        if (cb is { } callback)
            state = ArchetypeAOrchestration.OnHookCallback(state, callback);

        // A Read is reported but renders no chip (protocol §2): ToChip maps it to an edit op, but reads are
        // not file-op chips. We render a chip only for a write tool (mirrors ToHookCallback's write gate).
        if (!StreamJsonTaxonomy.IsWriteTool(ev.ToolName))
            return (state, null);

        LiveOpChip chip = LiveOpProjection.ToChip(ev);

        // The live cap meter is a READ of the O_EXCL cap-state — never a parallel counter (invariant 4).
        CapStateSnapshot snapshot = await _capStore!.ReadAsync(_conversationKey!, ct).ConfigureAwait(false);
        LiveCapMeterReading reading = LiveCapMeter.Read(snapshot);
        var caps = new CapSnapshot(reading.Created, reading.Destructive, reading.Touched);

        OpOutcomeBadge badge = chip.WasApplied ? OpOutcomeBadge.Applied : OpOutcomeBadge.Blocked;
        return (state, TurnEvent.FileOp(chip.Op, chip.TargetPath, badge, caps));
    }

    // Build the per-turn observe env via the EXISTING seam (HookConfig.ObserveChannelAddress + ObserveMode
    // → BuildHookSettings → BuildObserveEnvironment), then MERGE it on top of the session sandbox/cap env.
    // The App never derives the HOARDPAD_OBSERVE_* var NAMES itself — they come from the host seam, so the
    // env value and the bound primitive cannot drift. Approve mode for approve-each; report otherwise.
    private Dictionary<string, string> MergeObserveEnv(
        IReadOnlyDictionary<string, string> sessionEnv, AgencyMode mode)
    {
        HookObserveMode observeMode = mode == AgencyMode.ApproveEach ? HookObserveMode.Approve : HookObserveMode.Report;

        var hookConfig = new HookConfig(_workingRoot, NoReplyTimeout: TimeSpan.FromSeconds(600))
        {
            ObserveChannelAddress = _channelAddress,
            ObserveMode = observeMode,
        };
        HookSettings settings = _hookHost!.BuildHookSettings(hookConfig);

        var merged = new Dictionary<string, string>(sessionEnv, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in settings.HookEnvironment)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        IAgentTurn? turn = _turn;
        if (turn is null)
            return;
        await turn.KillTreeAsync().ConfigureAwait(false);
    }

    // Await a task, swallowing benign cancellation (a Stop / no-reply kill). A real fault still propagates.
    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Benign — a Stop / no-reply kill cancelled the producer / coordinator.
        }
    }

    // Pull the assistant text out of a stream-json line. A non-text / partial / non-JSON line yields null
    // (benign — it simply contributes no transcript text). The full taxonomy lives in StreamJsonTaxonomy;
    // the text extraction here is the minimal projection the transcript needs.
    private static string? ExtractAssistantText(string rawJsonLine)
    {
        if (string.IsNullOrWhiteSpace(rawJsonLine))
            return null;

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(rawJsonLine);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        using (doc)
        {
            System.Text.Json.JsonElement root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            // assistant message event: { "type":"assistant", "message": { "content":[ { "type":"text", "text":"…" } ] } }
            if (!root.TryGetProperty("message", out System.Text.Json.JsonElement message) ||
                message.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !message.TryGetProperty("content", out System.Text.Json.JsonElement content) ||
                content.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            System.Text.StringBuilder? sb = null;
            foreach (System.Text.Json.JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                if (!block.TryGetProperty("type", out System.Text.Json.JsonElement type) ||
                    type.ValueKind != System.Text.Json.JsonValueKind.String ||
                    type.GetString() != "text")
                {
                    continue;
                }
                if (block.TryGetProperty("text", out System.Text.Json.JsonElement t) &&
                    t.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    (sb ??= new System.Text.StringBuilder()).Append(t.GetString());
                }
            }

            return sb?.ToString();
        }
    }

    // ---- the merge-channel signal (one reader folds both producers into the MA14 state) ----

    private enum MergeKind { StreamLine, HookEvent }

    private readonly record struct MergeItem(MergeKind Kind, string? RawJsonLine, HookOpEvent? HookEvent)
    {
        public static MergeItem Stream(string rawJsonLine) => new(MergeKind.StreamLine, rawJsonLine, null);
        public static MergeItem Hook(HookOpEvent ev) => new(MergeKind.HookEvent, null, ev);
    }
}
