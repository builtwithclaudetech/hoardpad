// AiSessionBinder.cs — folder-open → AI session binding (carry-forward C; PRD §9.2, FR-26).
//
// On folder-open (live, restored at startup, or the QA2-18 lazy first-write request), this binds the AI
// panel to the session using ONLY the EXISTING Core.Ai / Host seams + the panel's BindSession /
// SetTurnRunner / ArmAgencyAsync surface — it does NOT modify the protected safety spine.
//
// The lazy first-write path is the load-bearing safety rule here: write agency must never arm silently —
// RequestWriteFolderAsync re-prompts for the root via IOpenFolderPrompt, and ONLY a user-confirmed root
// binds + arms. If the user declines, the panel stays read/chat only (no silent stale sandbox root).
//
// When a root is confirmed, it constructs the session-scoped AiFileOpsService + the PathResolverSandboxGate
// (canonicalize-then-evaluate), writes the host-built hook --settings JSON to a per-session file, builds
// the session-scoped AiTurnRunner, then calls panel.BindSession + SetTurnRunner + ArmAgencyAsync (the
// self-test-before-arm gate the panel already owns runs inside ArmAgencyAsync).
//
// All I/O (settings write, launcher resolve, self-test) runs off the UI thread inside ArmAgencyAsync /
// the binder; the panel-state mutations marshal back via the VM.

using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Session;

/// <summary>
/// The result of an <see cref="IAiSessionBinder.BindLiveFolderAsync"/> (or another <c>Bind*</c>) call.
/// Carries both the raw root (the user-facing path as supplied / picked) and the canonical root
/// (realpath-normalised). Both are null when no root was armed (declined re-prompt or no folder).
/// </summary>
internal sealed record BindResult(string? RawRoot, string? CanonicalRoot);

/// <summary>
/// Seam for folder-open AI session binding (carry-forward C). Extracted so <see cref="WorkspaceService"/>
/// can be integration-tested with a null-binder that does not touch the real AI spine.
/// </summary>
internal interface IAiSessionBinder
{
    /// <summary>Bind the panel to a freshly-opened live folder (the user just picked it — no staleness to re-prompt).</summary>
    Task<BindResult> BindLiveFolderAsync(
        AiPanelViewModel panel,
        string root,
        string conversationId,
        string sessionId,
        CancellationToken ct);

    /// <summary>
    /// FS-1 (Decision A, FINAL): bind the panel to the root <see cref="FolderRootResolver"/> derived for
    /// THIS launch (tiers i/ii/iii — always a real, resolvable folder, never a stale-root re-prompt). Unlike
    /// <see cref="BindLiveFolderAsync"/>, this does NOT arm write agency immediately for a write-capable
    /// configured mode — arming defers to the first write-mode AI action (the panel rests bound-but-
    /// <c>NotYetArmed</c>, root visible, read/chat available). A read-only configured mode still arms
    /// immediately (no self-test needed). Returns null roots if the resolved root does not actually exist
    /// on disk (defensive — the same no-usable-root guard the other bind paths apply).
    /// </summary>
    Task<BindResult> BindDerivedRootAsync(
        AiPanelViewModel panel,
        string root,
        string conversationId,
        string sessionId,
        CancellationToken ct);

    /// <summary>
    /// QA2-18 LAZY folder request: fired on the FIRST write-mode AI action when no folder is bound yet
    /// (never at startup). Shows the approved first-write explainer on the panel, opens the OS folder
    /// picker, and — on a confirmed root — binds + arms the session. Returns the confirmed root's
    /// <see cref="BindResult"/>, or null roots if the user declined (the panel stays read-only).
    /// </summary>
    Task<BindResult> RequestWriteFolderAsync(
        AiPanelViewModel panel,
        string? priorRoot,
        string conversationId,
        string sessionId,
        CancellationToken ct);
}

/// <summary>
/// Binds the AI panel to an open-folder session (carry-forward C). Composes the session-scoped
/// <see cref="AiFileOpsService"/> + <see cref="PathResolverSandboxGate"/> + <see cref="AiTurnRunner"/>
/// over the existing host seams; the lazy first-write path re-prompts for a stale root before arming;
/// never touches the spine.
/// </summary>
internal sealed class AiSessionBinder : IAiSessionBinder
{
    // The FR-24 --max-budget-usd ceiling default (a positive ceiling; tunable via FR-35 settings later).
    private const decimal DefaultMaxBudgetUsd = 5.00m;

    private readonly IPathResolver _pathResolver;
    private readonly IAtomicFileStore _store;
    private readonly ICapStateStore _capStore;
    private readonly IAppDataPaths _paths;
    private readonly IAgentBackend _backend;
    private readonly IToolPermissionHookHost _hookHost;
    private readonly IOpenFolderPrompt _rootPrompt;
    private readonly TimeProvider _time;
    // PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): the host grants-file writer wired into the runner so
    // an armed multi-doc turn delivers its per-file grants to the claude -p hook via HOARDPAD_FILE_GRANTS.
    private readonly IFileGrantsWriter _grantsWriter;
    // The M11 per-turn hook-observation channel (the Host concrete — needed for ResolveChannelAddress, a
    // Host implementation detail not on the IHookObservationChannel seam). Composed with the App's confirm
    // delegate in the composition root; the runner consumes it as the IHookObservationChannel interface.
    private readonly HoardPad.Host.HookObservationChannel _observationChannel;

    public AiSessionBinder(
        IPathResolver pathResolver,
        IAtomicFileStore store,
        ICapStateStore capStore,
        IAppDataPaths paths,
        IAgentBackend backend,
        IToolPermissionHookHost hookHost,
        IOpenFolderPrompt rootPrompt,
        TimeProvider time,
        HoardPad.Host.HookObservationChannel observationChannel,
        IFileGrantsWriter grantsWriter)
    {
        _pathResolver = pathResolver;
        _store = store;
        _capStore = capStore;
        _paths = paths;
        _backend = backend;
        _hookHost = hookHost;
        _rootPrompt = rootPrompt;
        _time = time;
        _observationChannel = observationChannel;
        _grantsWriter = grantsWriter ?? throw new ArgumentNullException(nameof(grantsWriter));
    }

    /// <summary>
    /// Bind the panel to a freshly-opened folder (the live File ▸ Open Folder path). The root is already
    /// the user's choice (no staleness to re-prompt), so it binds + arms directly.
    /// </summary>
    public Task<BindResult> BindLiveFolderAsync(
        AiPanelViewModel panel, string root, string conversationId, string sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentException.ThrowIfNullOrEmpty(root);
        return BindRootAsync(panel, root, conversationId, sessionId, armImmediately: true, ct);
    }

    /// <summary>
    /// FS-1 (Decision A, FINAL): bind the panel to the root <see cref="FolderRootResolver"/> derived for
    /// this launch. Unlike every other bind path, this does NOT arm a write-capable configured mode
    /// immediately — arming defers to the first write-mode AI action (AiPanelViewModel.SendAsync). A
    /// read-only configured mode still arms immediately (cheap — no self-test to defer). With no usable
    /// root (defensive: the derived folder no longer exists) the panel stays read/chat only.
    /// </summary>
    public Task<BindResult> BindDerivedRootAsync(
        AiPanelViewModel panel, string root, string conversationId, string sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        if (!Directory.Exists(root))
        {
            panel.ClearSession();
            return Task.FromResult(new BindResult(RawRoot: null, CanonicalRoot: null));
        }

        return BindRootAsync(panel, root, conversationId, sessionId, armImmediately: false, ct);
    }

    /// <summary>
    /// QA2-18 LAZY folder request (fired on the FIRST write-mode AI action, never at startup). Shows the
    /// approved first-write explainer on the panel, THEN opens the OS folder picker via
    /// <see cref="IOpenFolderPrompt"/>. On a confirmed, existing root it binds + arms the session; a declined
    /// pick (null) leaves the panel read-only with no stale sandbox root (MA5). The explainer is shown before
    /// the picker so the user understands the CLAUDE.md §4 sandbox boundary before the OS dialog appears.
    /// </summary>
    public async Task<BindResult> RequestWriteFolderAsync(
        AiPanelViewModel panel, string? priorRoot, string conversationId, string sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        // Show the approved first-write explainer BEFORE the picker opens (QA2-18) so the user reads why a
        // sandbox folder is required, then open the OS folder picker.
        panel.ShowFirstWriteExplainer();
        string? root = await _rootPrompt.RePromptForRootAsync(priorRoot, ct).ConfigureAwait(true);

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            // Declined / no usable root → read/chat only, no stale sandbox root (MA5). Leave the existing
            // no-folder state untouched (ClearSession would also fire, but there is nothing to clear yet).
            return new BindResult(RawRoot: null, CanonicalRoot: null);
        }

        return await BindRootAsync(panel, root, conversationId, sessionId, armImmediately: true, ct).ConfigureAwait(true);
    }

    private async Task<BindResult> BindRootAsync(
        AiPanelViewModel panel, string root, string conversationId, string sessionId, bool armImmediately, CancellationToken ct)
    {
        // Canonicalize the root via the host resolver (the ONLY NormcasedPath producer); this is the
        // resolved, validated sandbox root the gate + runner run against (architecture §5.2).
        NormcasedPath canonicalRoot = _pathResolver.ResolveCanonical(root, root);

        // Session-scoped sandbox gate (canonicalize-then-evaluate, FR-26) + the shared file-ops service —
        // the SAME service every AI op (and the FR-39 replay) goes through. Constructed per session.
        var gate = new PathResolverSandboxGate(_pathResolver, canonicalRoot, root);
        var fileOps = new AiFileOpsService(gate, _store, _capStore, _paths, sessionId, CapConfig.Pinned);

        // PANEL-2 (feature-ai-multidoc-panel §5.4): the session-scoped per-turn grant composer. It resolves
        // checked out-of-folder documents through the EXACT same resolver leg + raw root-base this gate uses
        // (_pathResolver + `root` — FR-MD-5.1 same-resolver invariant), so a grant string is byte-comparable
        // to what the gate returns for that candidate. The panel calls it at each turn-arm to compose a per-turn
        // UnionSandboxGate over THIS root gate; an empty checked set is root-only (fail-closed, FR-MD-10).
        var grantComposer = new HoardPad.App.Ai.AiTurnGrantComposer(
            _pathResolver, root, gate, _store, _capStore, _paths, sessionId, CapConfig.Pinned);

        // Build the deny-by-default hook --settings JSON for this root and persist it to a per-session
        // file (the launcher passes the PATH as --settings). Off-UI (write through the atomic store).
        var hookConfig = new HookConfig(canonicalRoot, NoReplyTimeout: TimeSpan.FromSeconds(600));
        HookSettings hookSettings = _hookHost.BuildHookSettings(hookConfig);
        string settingsPath = await WriteHookSettingsAsync(sessionId, hookSettings.SettingsJson, ct).ConfigureAwait(false);

        // M11 launch-env seam (docs/escalation-m11-launch-env-seam.md Option A): assemble the SESSION-STABLE
        // env the host applies on every spawned turn — the sandbox/cap env the hook reads to enforce
        // containment. Without HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE the hook reads an unset root
        // and fail-closed-denies every write (escalation §2 — the inert state this seam fixes). The cap-state
        // path comes from the ONE authority — ICapStateStore — so it is the EXACT file the spine writes (and
        // whose .lock sibling the hook locks); the App never re-derives it. The per-turn M11 observe vars
        // (HOARDPAD_OBSERVE_CHANNEL / HOARDPAD_OBSERVE_MODE) are mode-dependent, so the RUNNER merges them on
        // top of this base per armed turn (the mode is approve for approve-each, report otherwise).
        var launchEnv = new Dictionary<string, string>(hookSettings.HookEnvironment, StringComparer.Ordinal)
        {
            ["HOARDPAD_SANDBOX_ROOT"] = canonicalRoot.Value,
            ["HOARDPAD_CAP_STATE_FILE"] = _capStore.ResolveCapStateFilePath(conversationId),
        };

        // M11 live-write (docs/m11-channel-protocol.md §6): resolve the per-turn observation channel ADDRESS
        // ONCE for this (root, conversation) — it is deterministic, so it is the EXACT address every
        // OpenForTurnAsync on this pair binds (the env value and the live socket/pipe cannot drift). The
        // channel is consumed by the runner as the IHookObservationChannel seam; the cap store + conversation
        // key drive the live cap-meter READ (invariant 4) and the per-turn endpoint.
        string channelAddress = _observationChannel.ResolveChannelAddress(canonicalRoot, conversationId);

        // The session-scoped production turn runner (claude -p in the resolved root; no --cwd by
        // construction). Replaces the no-folder NoSessionTurnRunner. The M11 channel/hookHost/capStore/key/
        // address wire the live-write path (chips / cap meter / approve-each / MA14 feed).
        var runner = new AiTurnRunner(
            _backend, canonicalRoot, settingsPath, DefaultMaxBudgetUsd, launchEnv,
            channel: _observationChannel,
            hookHost: _hookHost,
            capStore: _capStore,
            conversationKey: conversationId,
            channelAddress: channelAddress,
            grantsWriter: _grantsWriter);

        // Bind the panel session: sandbox root + shared file-ops service + conversation id. This sets the
        // sandbox-scope line to the real root; it does NOT itself arm write agency.
        panel.BindSession(canonicalRoot.Value, fileOps, conversationId, grantComposer);
        panel.SetTurnRunner(runner);

        // Arm via the panel's self-test-before-arm gate (the SOLE guard against a silently-disarmed
        // sandbox). A failing self-test fails closed to read-only + a downgrade badge — the panel owns this.
        // FS-1: armImmediately=false (the startup derived-root bind) defers a write-capable arm to the
        // first write-mode AI action instead — ArmIfReadOnlyElseDeferAsync still arms a read-only
        // configured mode right away (no self-test needed).
        if (armImmediately)
            await panel.ArmAgencyAsync(ct).ConfigureAwait(true);
        else
            await panel.ArmIfReadOnlyElseDeferAsync(ct).ConfigureAwait(true);

        // Return both the raw root (the path as the user chose it — the display path) and the canonical
        // root (the realpath-normalised form used to arm the sandbox for this session; FS-1 retired the
        // persisted open-folder baseline, so the root is derived fresh every launch, not stored).
        return new BindResult(RawRoot: root, CanonicalRoot: canonicalRoot.Value);
    }

    /// <summary>Write the hook --settings JSON to a per-session file under app-data and return its path.</summary>
    private async Task<string> WriteHookSettingsAsync(string sessionId, string settingsJson, CancellationToken ct)
    {
        string dir = Path.Combine(_paths.AppDataRoot, "ai-sessions");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _paths.RestrictToCurrentUser(dir);
        }

        string path = Path.Combine(dir, $"{sessionId}.settings.json");
        ReadOnlyMemory<byte> bytes = System.Text.Encoding.UTF8.GetBytes(settingsJson);
        if (File.Exists(path))
            await _store.ReplaceWhenPresentAsync(path, bytes, ct).ConfigureAwait(false);
        else
            await _store.CreateWhenAbsentAsync(path, bytes, ct).ConfigureAwait(false);
        return path;
    }
}
