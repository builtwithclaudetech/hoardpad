// AiPanelViewModel.cs — the M6.6 FR-22 AI-panel view-model (design §6, architecture §4).
//
// The protected AI-host surface. It does NOT re-implement any safety logic — it WIRES the
// Core.Ai + Host safety spine and renders its state:
//   - arms write agency ONLY after IToolPermissionHookHost.RunFailClosedSelfTestAsync passes,
//     resolving the posture via AgencyMapping.Resolve (fail-closed to read-only + downgrade badge);
//   - drives ONE turn off the UI thread through IAiTurnRunner (the runner owns the process + kill);
//   - binds the three cap meters to ONE conversation-keyed cap snapshot — NO reset on backend switch;
//   - shows the three FR-10 confirms with their exact default-focus + wording (via IAiConfirmService);
//   - Stop kills the process tree (IAiTurnRunner.StopAsync -> IAgentTurn.KillTreeAsync, NFR-8);
//   - a TimeProvider-driven no-reply timer fails closed (deny + park in Pending, FR-39);
//   - handles the no-folder-open state gracefully (write agency unavailable until M6.3 opens a folder).
//
// THREADING (architecture §3.4): the view-model methods run on the UI thread; the turn runner runs the
// process + stream pump off-UI and the view-model marshals each yielded event back onto the dispatcher.
// The Stop path is responsive — StopAsync cancels the turn token and kills the tree off-UI; the UI
// reflects the stop without waiting for the process to die.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DragonScrub;
using HoardPad.App.Ai;
using HoardPad.App.Settings;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;

namespace HoardPad.App.ViewModels;

/// <summary>
/// The FR-22 AI panel view-model (design §6). The signature surface over the AI-safety spine — it
/// renders the spine's state and routes the user's controls (Stop / confirm / agency / backend /
/// pending apply), never duplicating the spine's safety decisions.
/// </summary>
public sealed partial class AiPanelViewModel : ObservableObject
{
    private IAiTurnRunner _runner;
    private IAiConfirmService _confirm;
    private readonly IToolPermissionHookHost _hookHost;
    private readonly IAgentBackend _backend;
    private readonly TimeProvider _time;
    private readonly CapConfig _capConfig;

    // STF-3 (docs/stf-diagnosis.md, Option 1): the bundled HoardPad.SelfTestStandIn executable path,
    // used ONLY as the `launcher` for the two fail-closed self-test gates — never for a real turn
    // (AgentBackend.StartTurnAsync resolves the real `claude` CLI independently and is untouched).
    // The real CLI has no knowledge of the self-test descriptor protocols and can never satisfy
    // either gate (docs/stf-diagnosis.md §(a)), so arming write agency against it always failed
    // closed. Defaults to null-safe resolution via ResolveSelfTestStandInPath when not supplied
    // (existing tests that construct this view-model directly are unaffected — they inject their
    // own IToolPermissionHookHost fakes and never reach the real launcher-resolve path).
    private readonly string _selfTestStandInPath;

    // The no-reply fail-closed deadline (FR-24/FR-39 default ~60 s, architecture §3.4). TimeProvider-driven
    // so a test drives it deterministically. The view also shows the countdown derived from it.
    private readonly TimeSpan _noReplyTimeout;

    // FS-5 / FR-10(a) (docs/decisions.md DR-1): the home/large-root trigger detector, invoked from
    // ArmAgencyAsync off the UI thread. Defaults to the real live implementation; tests inject a
    // scripted delegate so the ViewModel-level behavior is provable without touching real disk.
    private readonly Func<string, int, CancellationToken, DowngradeReason> _largeHomeRootDetector;

    // The in-flight turn's cancellation source (a Stop / no-reply kill cancels it). Null when idle.
    private CancellationTokenSource? _turnCts;

    // The write-agency armed flag from the LAST resolved AgencyResolution (set by ApplyResolution /
    // ArmAgencyAsync). Drives TurnRequest.WriteAgencyArmed — safe-by-construction from the resolved
    // posture rather than from a derived Sandbox state, which could return true for read-only-with-folder.
    private bool _writeAgencyArmed;

    // F-FS1d: the bind-generation epoch. Bumped by every BindSession/ClearSession call (each one
    // supersedes whatever bind came before it). ArmAgencyAsync captures the value in effect when IT
    // was called; if a newer bind/clear runs while its self-test/arm work is still in flight, the
    // captured generation goes stale and the eventual ApplyResolution is skipped — the newest bind's
    // panel state always wins. Purely cosmetic arm-state hygiene: the real sandbox/cap/hook enforcement
    // is scoped to _fileOps/the resolved root, never to this flag or to Sandbox/EffectiveMode.
    private int _bindGeneration;

    // BUG-4 diagnostic flags: _noReplyFired is set by the timer callback and checked in SendAsync's
    // finally block to distinguish a no-reply timeout from a user-initiated Stop.  _isTesting gates the
    // self-test button so it cannot overlap with a live turn (both gate on each other's flags).
    private bool _noReplyFired;
    private bool _isTesting;

    // The session-scoped file-ops service for FR-39 replay (the SAME shared service every AI op uses).
    // Null until a folder is open (M6.3 supplies it); a pending apply is disabled while it is null.
    //
    // PANEL-2 (feature-ai-multidoc-panel §5.4): _fileOps is the ARCHETYPE-B write path. Between turns it is
    // the session ROOT-gated service (bound at BindSession). At turn-arm, when a grant composer + checked
    // out-of-folder documents are present, SendAsync swaps in a per-turn UnionSandboxGate-composed service
    // for the turn's duration, then restores _sessionRootFileOps afterward — so the enforced grant set is
    // recomputed per turn (FR-MD-9) and a mid-turn checkbox toggle affects only the NEXT turn (FR-MD-6(e)).
    private AiFileOpsService? _fileOps;
    private AiFileOpsService? _sessionRootFileOps; // the root-gated service to restore after a turn (PANEL-2).
    private string? _conversationId;

    // PANEL-2: the session-scoped per-turn grant composer (feature-ai-multidoc-panel §5.4). Non-null once a
    // folder is bound WITH the composer (production wiring); null in the no-folder state and in tests that do
    // not exercise the multi-doc path (the panel then behaves exactly as before — root-only).
    private IAiTurnGrantComposer? _grantComposer;

    // FR-39 replay seams (MF1.4): the before-image reader + the atomic store for blob staging + the
    // app-data paths for UndoJournalRoot. Ctor-injected by CompositionRoot (app-scoped stateless singletons
    // that serve across all BindSession/ClearSession cycles). Null only in tests that don't need replay
    // (they pass null and get CanApplyPending=false, which is the correct disabled-replay behavior). The
    // apply command gates on _beforeImageReader != null to make a missing wire immediately visible.
    private readonly IBeforeImageReader? _beforeImageReader;
    private readonly IAtomicFileStore? _replayStore;
    private readonly IAppDataPaths? _replayPaths;

    // OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): the additive union-aware before-image reader. Used
    // ONLY for replaying a parked out-of-folder op that carries a GrantedResolvedPath — it admits that ONE
    // resolved grant (self-enforcing containment; empty grant ⇒ root-only). An in-root op uses the frozen
    // root-only _beforeImageReader unchanged. Null in tests that don't exercise the granted-replay path (the
    // panel then behaves exactly as before for in-root ops). CompositionRoot injects the real reader.
    private readonly IUnionBeforeImageReader? _unionBeforeImageReader;

    // OQ-MD-5 option ii: the resolved grant paths armed for the CURRENT turn (set at turn-arm alongside
    // _fileOps swap, cleared when the root service is restored). At park time a parked op's raw target is
    // matched against this set to persist its single GrantedResolvedPath on the proposal, so a granted
    // out-of-folder write survives to the pending/replay surface. Empty between turns / when no multi-doc arm.
    private IReadOnlyList<string> _currentTurnGrantPaths = System.Array.Empty<string>();

    // FR-39 replay: cache tool_use_id → raw input JSON captured from stream-json before the hook ask
    // arrives. The hook channel's _ask_message deliberately omits content; the stream-json tool_use
    // block is the only in-process source of the proposed bytes. Populated by the stream event handler;
    // consumed (and cleared) at park time (ConfirmHookDecisionAsync). Bounded naturally by turn lifetime.
    private readonly ConcurrentDictionary<string, string> _toolInputCache =
        new(StringComparer.Ordinal);

    // PANEL-4 (feature-ai-multidoc-panel FR-MD-8, AC-MD-3): the ordered pre-turn event log recorded by
    // FlushCheckedDirtyDocsAndCaptureAsync — a flush(path) event is appended BEFORE the before-image(path)
    // event for the same doc, and this whole sequence is produced BEFORE _runner.RunAsync (the first backend
    // call). The AC-MD-3 acceptance reads PreTurnOrderLog to assert flush-strictly-before-before-image; the
    // captured hash (below) is asserted equal to the flushed content's hash (proving capture-after-flush).
    private readonly List<PreTurnEvent> _preTurnOrderLog = [];

    // PANEL-4 (FR-MD-8 step 2): the last turn's captured before-image content hash per flushed target path.
    // AC-MD-3 asserts this equals the hash of the FLUSHED bytes (not the pre-flush bytes) — non-vacuously.
    private readonly Dictionary<string, string> _lastBeforeImageHashByPath = new(StringComparer.Ordinal);

    /// <summary>
    /// Construct the panel view-model. The no-folder-open default (no <paramref name="fileOps"/>) is the
    /// graceful state design §6.8 requires: write agency is unavailable until M6.3 opens a folder and
    /// constructs the session-scoped service + sandbox root.
    /// </summary>
    /// <param name="beforeImageReader">
    /// The MF1.1 before-image seam — required for FR-39 replay (MF1.4). When <c>null</c>, the
    /// <c>ApplyPendingAsync</c> command is disabled; existing tests that don't need replay pass
    /// <c>null</c> and are unaffected. Production wiring supplies this from the DI container.
    /// </param>
    /// <param name="replayStore">
    /// The atomic file store used to stage before-image blobs before a replay mutation (M15 ordering).
    /// Must not be <c>null</c> when <paramref name="beforeImageReader"/> is supplied; otherwise <c>null</c>.
    /// </param>
    /// <param name="replayPaths">
    /// The app-data paths supplying <c>UndoJournalRoot</c> for blob path construction. Must not be
    /// <c>null</c> when <paramref name="beforeImageReader"/> is supplied; otherwise <c>null</c>.
    /// </param>
    /// <param name="selfTestStandInPath">
    /// STF-3: absolute path to the bundled <c>HoardPad.SelfTestStandIn</c> executable, used as the
    /// <c>launcher</c> for the fail-closed self-test gates instead of the real <c>claude</c> CLI.
    /// Defaults to <see cref="ResolveDefaultSelfTestStandInPath"/> (resolved next to the running app,
    /// mirroring the bundled-hook resolution pattern) when not supplied.
    /// </param>
    /// <param name="largeHomeRootDetector">
    /// FS-5 / FR-10(a) (docs/decisions.md DR-1): the home/large-root trigger detector. Defaults to
    /// <see cref="LargeHomeRootDetector.DetectLive"/>; tests inject a scripted delegate.
    /// </param>
    public AiPanelViewModel(
        IAiTurnRunner runner,
        IAiConfirmService confirm,
        IToolPermissionHookHost hookHost,
        IAgentBackend backend,
        TimeProvider time,
        CapConfig? capConfig = null,
        TimeSpan? noReplyTimeout = null,
        IBeforeImageReader? beforeImageReader = null,
        IAtomicFileStore? replayStore = null,
        IAppDataPaths? replayPaths = null,
        IUnionBeforeImageReader? unionBeforeImageReader = null,
        string? selfTestStandInPath = null,
        Func<string, int, CancellationToken, DowngradeReason>? largeHomeRootDetector = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(hookHost);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);

        _runner = runner;
        _confirm = confirm;
        _hookHost = hookHost;
        _backend = backend;
        _time = time;
        _capConfig = capConfig ?? CapConfig.Pinned;
        _selfTestStandInPath = selfTestStandInPath is { Length: > 0 }
            ? selfTestStandInPath
            : ResolveDefaultSelfTestStandInPath();
        _noReplyTimeout = noReplyTimeout ?? TimeSpan.FromSeconds(60);
        _beforeImageReader = beforeImageReader;
        _replayStore = replayStore;
        _replayPaths = replayPaths;
        _unionBeforeImageReader = unionBeforeImageReader;
        _largeHomeRootDetector = largeHomeRootDetector ?? LargeHomeRootDetector.DetectLive;

        Transcript = new ObservableCollection<TranscriptLine>();
        Pending = new ObservableCollection<PendingProposalViewModel>();
        CreatedMeter = new CapMeterViewModel { Label = "created", Cap = _capConfig.CreateCap };
        DestructiveMeter = new CapMeterViewModel { Label = "destructive", Cap = _capConfig.DestructiveCap };
        TouchedMeter = new CapMeterViewModel { Label = "touched", Cap = _capConfig.FilesTouchedCap };

        // PANEL-3 (FR-MD-6): the multi-doc section visibility follows the row count. Rows are rebuilt in place
        // by MultiDocSelection.Rearm (Clear + Add), so a collection change is the trigger to re-evaluate.
        MultiDoc.Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultiDocRows));
    }

    // ----- The rendered surface (design §6.1 regions) -----

    /// <summary>The three agency-mode choices for the dropdown (BUG-6: ItemsSource instead of static XAML items).</summary>
    public IReadOnlyList<AgencyMode> AgencyOptions { get; } =
        [AgencyMode.AutoApply, AgencyMode.ApproveEach, AgencyMode.ReadOnly];

    /// <summary>The streaming conversation transcript (region 3 — messages + file-op chips + safety chips).</summary>
    public ObservableCollection<TranscriptLine> Transcript { get; }

    /// <summary>The FR-39 pending-proposals (region 5 Pending tab) — each applied individually, no Apply-All.</summary>
    public ObservableCollection<PendingProposalViewModel> Pending { get; }

    /// <summary>Cap meter: created N/25 (region 4) — bound to the single conversation-keyed cap snapshot.</summary>
    public CapMeterViewModel CreatedMeter { get; }

    /// <summary>Cap meter: destructive N/10 (region 4).</summary>
    public CapMeterViewModel DestructiveMeter { get; }

    /// <summary>Cap meter: touched N/20 (region 4).</summary>
    public CapMeterViewModel TouchedMeter { get; }

    /// <summary>The FR-38 X% magnitude firing-threshold context label (design §6.5 — a parameter, not a meter).</summary>
    public string CapThresholdLabel => $"X% threshold {_capConfig.DestructiveMagnitudePercent}%";

    /// <summary>The selected backend (FR-23). Switching does NOT reset the caps (§6.3) — only the §9.3 backend label.</summary>
    [ObservableProperty]
    private AiBackend _selectedBackend = AiBackend.ClaudeCli;

    /// <summary>The user-configured agency mode (FR-27). Setting it (re)arms write agency via the self-test gate.</summary>
    [ObservableProperty]
    private AgencyMode _configuredMode = AgencyMode.AutoApply;

    /// <summary>
    /// FS-5 / FR-10(a) (docs/decisions.md DR-1): ON (default, matches <c>HoardPadSettings.
    /// ConfirmLargeHomeFolder</c>) keeps the home/large-root auto-downgrade-to-approve-each; OFF
    /// auto-applies everywhere in-bounds. Pushed live from settings (CompositionRoot, BUG-5 pattern);
    /// consulted only by <see cref="ArmAgencyAsync"/> — takes effect on the next arm.
    /// </summary>
    [ObservableProperty]
    private bool _confirmLargeHomeFolder = true;

    /// <summary>
    /// FS-5 / FR-10(a): the FR-35 large-root file-count threshold (design §7.5), consulted by
    /// <see cref="ArmAgencyAsync"/>. Pushed live from settings the same way BUG-5 pushes
    /// <see cref="CurrentModelId"/>/<see cref="CurrentEffortLevel"/> (CompositionRoot's SettingsChanged
    /// subscription). Not an <c>[ObservableProperty]</c> — it drives no bound UI, only the next arm.
    /// </summary>
    public int LargeRootThreshold { get; set; } = HoardPadSettings.DefaultLargeRootThreshold;

    /// <summary>The effective agency mode (post self-test gate / mid-turn degrade, MA13) — shown in the header.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgencyHeader))]
    private AgencyMode _effectiveMode = AgencyMode.ReadOnly;

    /// <summary>The downgrade reason badge text (design §6.2), or empty when effective == configured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgencyHeader))]
    [NotifyPropertyChangedFor(nameof(HasDowngrade))]
    private string _downgradeBadge = string.Empty;

    /// <summary><c>true</c> when a downgrade badge is showing (the effective mode differs from configured).</summary>
    public bool HasDowngrade => DowngradeBadge.Length > 0;

    /// <summary>The header agency text — "auto-apply (↓ approve-each: large root)" style (design §6.2).</summary>
    public string AgencyHeader => HasDowngrade
        ? $"{Label(ConfiguredMode)} (↓ {Label(EffectiveMode)}: {DowngradeBadge})"
        : Label(EffectiveMode);

    /// <summary>The sandbox arming state (design §6.7). No folder open until M6.3 → NoFolder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SandboxLine))]
    [NotifyPropertyChangedFor(nameof(IsFolderOpen))]
    private SandboxState _sandbox = SandboxState.NoFolder;

    /// <summary>The resolved sandbox root path (design §6.7), or empty when no folder is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SandboxLine))]
    private string _sandboxRoot = string.Empty;

    // QA4-21 (hostile-review Warning 1 fix): CurrentModelId/CurrentEffortLevel are hand-written
    // properties, NOT [ObservableProperty], because the construction-captured-default capture below
    // must run UNCONDITIONALLY on the FIRST-EVER set call. CommunityToolkit.Mvvm's generated setter
    // wraps its whole body (including the OnXxxChanged partial-hook call) in an equality check against
    // the backing field's CURRENT value — on a fresh install CompositionRoot seeds
    // `CurrentModelId = settings.ModelId ?? string.Empty`, and `HoardPadSettings.Default`'s null ModelId
    // makes that assignment `string.Empty`, which already equals the backing field's compiler default
    // ("") — so the generated setter (and therefore a `??=`-latching OnChanged hook) never fires, and
    // the construction-captured default stays uncaptured. The FIRST value that ever DOES trip the
    // generated setter's equality check (i.e. the first later panel override) would then get wrongly
    // latched as "the default". `_modelIdDefaultCaptured`/`_effortLevelDefaultCaptured` are set BEFORE
    // the equality-gated `SetProperty` call below, independent of whether the value actually changes.
    private bool _modelIdDefaultCaptured;
    private bool _effortLevelDefaultCaptured;

    private string _currentModelId = string.Empty;

    /// <summary>
    /// BUG-5 / QA4-2 / QA4-21: the current AI model ID — bound live to the header ModelSelector ComboBox.
    /// QA4-21 (revises QA4-2): seeded from settings at construction, but a panel change is a SESSION-LOCAL
    /// override — it is never persisted back to <c>settings.json</c> (see <see cref="ResetModelEffortToAppDefaultAsync"/>
    /// for the only way to re-seed it on demand). It survives panel close/reopen because this view-model is
    /// a DI singleton; only a full app relaunch reverts it to the saved default.
    /// </summary>
    public string CurrentModelId
    {
        get => _currentModelId;
        set
        {
            if (!_modelIdDefaultCaptured)
            {
                _defaultModelId = value;
                _modelIdDefaultCaptured = true;
            }

            SetProperty(ref _currentModelId, value);
        }
    }

    private string _currentEffortLevel = string.Empty;

    /// <summary>BUG-5 / QA4-2 / QA4-21: the current AI effort level — bound live to the header EffortSelector ComboBox. Same ephemeral-override posture as <see cref="CurrentModelId"/>.</summary>
    public string CurrentEffortLevel
    {
        get => _currentEffortLevel;
        set
        {
            if (!_effortLevelDefaultCaptured)
            {
                _defaultEffortLevel = value;
                _effortLevelDefaultCaptured = true;
            }

            SetProperty(ref _currentEffortLevel, value);
        }
    }

    /// <summary>
    /// QA4-2 (reopens QA3-11/BUG-5): the known Claude model presets for the header ModelSelector ComboBox —
    /// <see cref="SettingsViewModel.ModelOptions"/> minus its "Custom/Other..." sentinel (the SAME
    /// <c>Take(Count-1)</c> slice <see cref="SettingsViewModel"/>'s own settings-load path uses), so both
    /// surfaces share ONE list — no second copy of the model set to drift out of sync. Avalonia's
    /// <see cref="Avalonia.Controls.ComboBox"/> has no editable/free-text mode, so a custom model ID is
    /// still set from the Settings tab's preset+custom-textbox pair — this ComboBox covers the common case
    /// directly in the panel.
    /// </summary>
    public static IReadOnlyList<string> ModelOptions { get; } =
        SettingsViewModel.ModelOptions.Take(SettingsViewModel.ModelOptions.Count - 1).ToList();

    /// <summary>QA4-2: the closed effort-level set for the header EffortSelector ComboBox — reuses <see cref="SettingsViewModel.EffortOptions"/> directly (one list, no drift).</summary>
    public static IReadOnlyList<string> EffortOptions => SettingsViewModel.EffortOptions;

    // QA4-21: the FIRST value ever assigned to CurrentModelId/CurrentEffortLevel — i.e. the seed
    // CompositionRoot sets right after construction — captured unconditionally by the hand-written
    // setters above. This is the "construction-captured default" fallback ResetModelEffortToAppDefaultAsync
    // uses when no CurrentSettingsProvider is wired (e.g. a test that constructs the view-model directly).
    // It never changes after that first assignment; it does NOT track later panel picks or Settings-tab saves.
    private string? _defaultModelId;
    private string? _defaultEffortLevel;

    /// <summary>
    /// QA4-21: re-seed <see cref="CurrentModelId"/>/<see cref="CurrentEffortLevel"/> from the CURRENT saved
    /// settings on demand (the panel header's small "↺ reset to app default" control) — discarding whatever
    /// session-local override is in effect. Prefers a fresh async read through <see cref="CurrentSettingsProvider"/>
    /// (the live on-disk value, which may differ from the value this session started with if the Settings tab
    /// saved a change since launch); falls back to the construction-captured <see cref="_defaultModelId"/>/
    /// <see cref="_defaultEffortLevel"/> when that seam is unwired (e.g. a direct-construction test) OR when
    /// the provider read itself fails (hostile-review Warning 2 fix: a corrupted settings.json or a transient
    /// I/O fault must not leave the reset silently inert — it degrades to the same fallback rather than letting
    /// the exception propagate unobserved out of the AsyncRelayCommand).
    /// </summary>
    [RelayCommand]
    private async Task ResetModelEffortToAppDefaultAsync()
    {
        if (CurrentSettingsProvider is { } provider)
        {
            HoardPadSettings? current;
            try
            {
                current = await provider(CancellationToken.None).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is first-class — never swallowed.
            }
            catch (Exception)
            {
                current = null; // fall through to the construction-captured fallback below.
            }

            if (current is not null)
            {
                CurrentModelId = current.ModelId ?? string.Empty;
                CurrentEffortLevel = current.EffortLevel ?? string.Empty;
                return;
            }
        }

        CurrentModelId = _defaultModelId ?? string.Empty;
        CurrentEffortLevel = _defaultEffortLevel ?? string.Empty;
    }

    /// <summary>
    /// The always-visible document-folder scope line (design §6.7) — state + root. FS-3 Decision B
    /// (FINAL, 2026-07-10): user-facing "Sandbox" wording is retired in favor of "Document folder:" — the
    /// safety boundary itself (FR-26) is unchanged, only its display wording.
    ///
    /// QA2-18 (the maintainer's LAZY-picker decision): at startup the panel RESTS in the no-folder / read-only state
    /// and NEVER fires the OS folder picker unprompted. The <see cref="SandboxState.NoFolder"/> line is the
    /// approved resting note that explains WHY AI edits are folder-scoped (CLAUDE.md §4 sandbox boundary).
    /// The picker fires only on the FIRST write-mode AI action that needs write access
    /// (<see cref="RequestWriteFolderAsync"/>, invoked from <see cref="SendAsync"/>), after the approved
    /// first-write explainer — never at startup.
    /// </summary>
    public string SandboxLine => Sandbox switch
    {
        SandboxState.Armed => $"Document folder: {SandboxRoot}  [ scoped ✓ ]",
        // FS-1: bound to a resolved root but write agency has not armed yet (deferred to the first
        // write-mode AI action) — shows the root, same as Armed, so the safety boundary stays legible.
        SandboxState.NotYetArmed => $"Document folder: {SandboxRoot}  [ not yet armed ]",
        SandboxState.SelfTestFailed => "Document folder could not be armed ⚠",
        SandboxState.Degraded => "Degraded — approve-each forced",
        // QA2-18 approved RESTING note (startup, no folder / read-only) — verbatim, do not paraphrase.
        _ => "AI file access is off. For safety, AI edits are limited to a single folder you choose — open one to enable them.",
    };

    /// <summary>
    /// <c>true</c> when a folder is open (design §6.8) — i.e. a session has been bound via
    /// <see cref="BindSession"/>. The write-agency selector is enabled when a folder is open; the panel
    /// shows the self-test-failed or degraded state while the folder is open but the self-test has not
    /// passed. Distinct from "write agency is currently armed" — that is <c>_writeAgencyArmed</c>.
    /// </summary>
    public bool IsFolderOpen =>
        Sandbox is SandboxState.Armed or SandboxState.NotYetArmed or SandboxState.SelfTestFailed or SandboxState.Degraded;

    // ----- BUG-4 diagnostic status line (backend health) -----

    /// <summary>
    /// Backend connection state for the BUG-4 diagnostic status line. Updated by <see cref="SendAsync"/>,
    /// <see cref="TestConnectionAsync"/>, and the no-reply timer. Drives the status-line foreground color
    /// via <see cref="StatusBrushConverter"/>.
    /// </summary>
    [ObservableProperty]
    private BackendConnectionState _backendStatus = BackendConnectionState.Disconnected;

    /// <summary>
    /// The human-readable status-line message (e.g. "Ready", "Connecting...", "Error: Claude CLI not found").
    /// Updated together with <see cref="BackendStatus"/>.
    /// </summary>
    [ObservableProperty]
    private string _backendStatusMessage = "No folder open";

    /// <summary>
    /// <c>true</c> when the MF1.4 replay seams are wired (ctor-injected by the production
    /// <c>CompositionRoot</c>). <c>false</c> only in tests that do not need replay (they pass <c>null</c>)
    /// or in a misconfigured production build. The <c>ApplyPendingAsync</c> command is disabled when this is
    /// <c>false</c> (<see cref="CanApplyPending"/> gates on it), making a mis-wiring immediately visible.
    /// Exposed <c>internal</c> so <c>CompositionRootTests</c> can assert it true on the production panel.
    /// </summary>
    internal bool ReplayDependenciesWired =>
        _beforeImageReader is not null && _replayStore is not null && _replayPaths is not null;

    /// <summary>
    /// OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): <c>true</c> when the union-aware before-image reader
    /// is wired (ctor-injected by the production <c>CompositionRoot</c>). Without it, a parked out-of-folder
    /// granted op falls back to the root-only reader and is Refused — the granted-replay feature is silently
    /// inert. Exposed <c>internal</c> so <c>CompositionRootTests</c> can assert the production panel injects a
    /// real <c>UnionBeforeImageReader</c> (the dead-feature-trap guard).
    /// </summary>
    internal bool UnionReplayReaderWired => _unionBeforeImageReader is not null;

    /// <summary>
    /// The number of entries currently in the FR-39 tool-input cache (MF1.4). Zero between turns
    /// (the <c>SendAsync</c> finally block drains it). Exposed <c>internal</c> for the cache-drain test.
    /// </summary>
    internal int ToolInputCacheCount => _toolInputCache.Count;

    /// <summary>
    /// PANEL-4 (feature-ai-multidoc-panel FR-MD-8, AC-MD-3): the ordered pre-turn flush/before-image event
    /// log from the most recent turn-arm. A <c>Flush</c> event for a doc precedes its <c>BeforeImage</c>
    /// event, and the whole sequence is produced before the first backend call. Exposed <c>internal</c> so
    /// the AC-MD-3 acceptance can assert the strict ordering non-vacuously.
    /// </summary>
    internal IReadOnlyList<PreTurnEvent> PreTurnOrderLog => _preTurnOrderLog;

    /// <summary>
    /// PANEL-4 (FR-MD-8 step 2): the before-image content hash captured from the FLUSHED on-disk content of
    /// <paramref name="path"/> during the most recent turn-arm, or <c>null</c> if not captured. AC-MD-3
    /// asserts this equals the hash of the flushed bytes (proving capture happened after the flush).
    /// </summary>
    internal string? LastBeforeImageHash(string path) =>
        _lastBeforeImageHashByPath.TryGetValue(path, out string? h) ? h : null;

    /// <summary>
    /// The bound session's conversation id (§4.4 — the cap-state's on-disk key), or <c>null</c> in the
    /// no-folder state. Exposed <c>internal</c> so a test can assert the FEAT-9 Clear-Conversation command
    /// leaves it byte-identical (clearing history must NOT mint a new conversation / re-arm caps).
    /// </summary>
    internal string? ConversationId => _conversationId;

    /// <summary>Whether a turn is in flight (drives the persistent [Stop] visibility + input lockout, §6.4).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearConversationCommand))]
    private bool _isTurnInFlight;

    /// <summary>The prompt input text (region 6, FR-25).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _promptInput = string.Empty;

    /// <summary>The Pending-tab badge count (design §6.6.4) — surfaces parked work.</summary>
    public int PendingCount => Pending.Count;

    // ----- The session wiring (M6.3 connects this; M6.6 builds it + the no-folder default) -----

    /// <summary>
    /// QA2-18 approved FIRST-WRITE explainer — shown verbatim right before the OS folder picker opens on
    /// the first AI action that needs write access. Do NOT paraphrase further (maintainer-approved copy),
    /// relabeled by FS-3 Decision B (FINAL, 2026-07-10) to drop "sandbox" wording.
    /// </summary>
    public const string FirstWriteFolderExplainer =
        "For your safety, HoardPad confines all AI file changes to one folder and can never write outside it. "
        + "Choose that folder to continue.";

    /// <summary>
    /// QA2-18 LAZY folder-request seam. When a WRITE-capable turn is sent while no folder is bound yet
    /// (<c>_fileOps is null</c>), <see cref="SendAsync"/> invokes this callback FIRST: the wired handler
    /// (composition root) shows the <see cref="FirstWriteFolderExplainer"/> and opens the OS folder picker,
    /// then binds + arms the session on a confirmed root. It returns <c>true</c> once a folder is bound and
    /// write agency armed, <c>false</c> if the user declined (the turn then proceeds read-only). The picker
    /// is NEVER fired at startup — only on this first write interaction (the maintainer's lazy decision). Null in tests
    /// that don't exercise the lazy path (the send then proceeds against whatever runner is bound).
    /// </summary>
    public Func<Task<bool>>? RequestWriteFolderAsync { get; set; }

    /// <summary>
    /// QA3-8: the up-front unsaved-document save-prompt seam. When a write-capable turn is about to be sent
    /// and the FOCUSED open document has never been saved (no on-disk path — a scratch buffer),
    /// <see cref="SendAsync"/> invokes this IMMEDIATELY — before the <see cref="RequestWriteFolderAsync"/>
    /// check, the multi-doc grant arm, and the first backend call — so the user is asked to save right away,
    /// rather than letting the AI silently fail to write to a path-less buffer and only mention it several
    /// turns later. The wired handler (composition root, over the SAME Save-As dialog
    /// <see cref="RequestSaveAsForScratchAsync"/> uses via OQ-MD-3) returns the new on-disk path on success,
    /// or <c>null</c> if the user cancelled — either way <see cref="SendAsync"/> proceeds afterward exactly
    /// as before this fix (a cancelled save just means the document stays non-grantable, NG-5). Null seam
    /// (tests / no wiring) ⇒ no prompt fires — the pre-fix behavior.
    /// </summary>
    public Func<OpenDocumentInfo, Task<string?>>? RequestSaveForUnsavedFocusedDocAsync { get; set; }

    // ----- PANEL-2: the AI multi-doc checked-set state surface (feature-ai-multidoc-panel FR-MD-6) -----

    /// <summary>
    /// The AI multi-doc panel's per-open-document "checked for AI write" state (PANEL-2). PANEL-3 binds its
    /// visual checklist to <see cref="MultiDocSelection.Rows"/>; PANEL-2 reads the checked set at turn-arm to
    /// assemble the grant set (FR-MD-1/2) and the FR-MD-3 context descriptor. The focused document is
    /// pre-checked by default on the first arm (FR-MD-6(a)); the checked-set persists across turns while the
    /// session is open (FR-MD-9), but the ENFORCED grant set is recomputed per turn at arm time.
    /// </summary>
    public MultiDocSelection MultiDoc { get; } = new();

    /// <summary>
    /// PANEL-3 (FR-MD-6): whether the multi-doc checklist has any rows to show. Drives the section's
    /// visibility so it collapses cleanly when no documents are open. Raised whenever
    /// <see cref="MultiDocSelection.Rows"/> changes (subscribed in the ctor).
    /// </summary>
    public bool HasMultiDocRows => MultiDoc.Rows.Count > 0;

    /// <summary>
    /// The open-document provider (PANEL-2). The composition root wires this over the shell's open tabs +
    /// active tab (<c>MainWindowViewModel.Tabs</c> / <c>ActiveTab</c>); the panel calls it at turn-arm to
    /// snapshot the currently-open documents (path + focused/dirty/read-only flags, NO contents). Null in
    /// tests / no-folder paths that do not exercise the multi-doc surface — the panel then arms with an empty
    /// checked set (root-only, fail-closed FR-MD-10) exactly as before.
    /// </summary>
    public Func<IReadOnlyList<OpenDocumentInfo>>? OpenDocumentsProvider { get; set; }

    /// <summary>
    /// PANEL-3 (FR-MD-6(c)): the APPROVED, ship-verbatim out-of-folder note (OQ-MD-2), relabeled by FS-3
    /// Decision B (FINAL, 2026-07-10) to drop "your open folder" in favor of the canonical "document
    /// folder" term. This exact string is the maintainer-approved copy — do not paraphrase further or add a
    /// placeholder.
    /// </summary>
    public const string OutOfFolderNote =
        "This document is outside the document folder. Checking it grants the AI write access to this one file only — not its folder.";

    /// <summary>
    /// PANEL-3 (FR-MD-6(d), OQ-MD-3): the Save-As seam for a checked SCRATCH buffer. When the user checks a
    /// never-saved scratch row, the panel invokes this to force a Save-As so the buffer resolves to a real
    /// on-disk path before the turn (a path-less buffer cannot be granted, NG-5). The handler (composition
    /// root, over the workspace Save-As dialog) returns the new on-disk path on success, or <c>null</c> if the
    /// user cancelled — the panel then REVERTS the check. Null seam (tests / no wiring) ⇒ a scratch check is
    /// reverted immediately (it can never be granted), which is the correct fail-closed behavior.
    /// The argument is the scratch row's current <see cref="OpenDocumentInfo"/> identity (display name).
    /// </summary>
    public Func<MultiDocRowViewModel, Task<string?>>? RequestSaveAsForScratchAsync { get; set; }

    /// <summary>
    /// PANEL-4 (feature-ai-multidoc-panel FR-MD-7/8): the pre-turn flush seam. At turn-arm the panel invokes
    /// this once per CHECKED, DIRTY, non-scratch document (keyed by its on-disk path) to auto-save the buffer
    /// to disk through the durable save path and seal an editor undo point — BEFORE the before-image is
    /// captured and BEFORE the first backend call (the FR-MD-8 ordering). The handler (composition root, over
    /// <c>WorkspaceService.FlushDocumentByPathAsync</c>) returns <c>true</c> on a durable flush; <c>false</c>
    /// when no matching editor tab is found or the save failed — the panel then FAILS SAFE and aborts the
    /// turn-arm rather than launch the agent against stale on-disk bytes (FR-MD-7 durability posture). Null
    /// seam (tests / no wiring that exercises the flush) ⇒ the flush step is skipped entirely (root-only,
    /// no-op), and the panel behaves exactly as before PANEL-4.
    /// </summary>
    public Func<string, Task<bool>>? FlushDocumentAsync { get; set; }

    /// <summary>
    /// QA3-9: invoked whenever this panel APPLIES a write to a target path — a live
    /// <see cref="TurnEventKind.FileOp"/> chip whose outcome is <see cref="OpOutcomeBadge.Applied"/> (the M11
    /// auto-apply live-write path), or an FR-39 replay whose outcome is <see cref="OpOutcomeBadge.Approved"/>
    /// (<see cref="DispatchReplayAsync"/>) — so a matching OPEN editor tab can refresh its buffer from disk
    /// (the handler, wired from <c>App.axaml.cs</c> over <c>WorkspaceService.RefreshOpenDocumentByPathAsync</c>,
    /// is a no-op when no open tab's path matches). Protects against a later manual Save silently clobbering
    /// the AI's on-disk edit with a stale in-memory buffer. NOT invoked for a blocked/denied op (nothing was
    /// written). Null in tests / no-wiring paths ⇒ the refresh step is skipped entirely (the panel behaves
    /// exactly as before QA3-9).
    /// </summary>
    public Func<string, Task>? RefreshOpenDocumentAsync { get; set; }

    // ----- FEAT-14: the AI-requested-settings-change seams -----

    /// <summary>
    /// FEAT-14: reads the CURRENT persisted settings snapshot, awaited (never the composition-time-only
    /// synchronous <c>SettingsStore.Load()</c>) so this never blocks the UI thread on file I/O. Called
    /// once per completed turn — only when a settings-change marker was found in the assistant's response
    /// AND the turn was write-capable. Wired from <c>App.axaml.cs</c> over <c>SettingsStore.LoadAsync</c>.
    /// Null in tests / no-wiring paths ⇒ a settings-change proposal is declined (never applied) — the
    /// same fail-closed posture as every other unwired seam in this view-model.
    /// </summary>
    public Func<CancellationToken, Task<HoardPadSettings>>? CurrentSettingsProvider { get; set; }

    /// <summary>
    /// FEAT-14/QA4-17: persists a FIELD-SCOPED patch (<c>Func&lt;HoardPadSettings, HoardPadSettings&gt;</c>
    /// — see <c>SettingsChangeApplier.SettingsChangeComputation.Patch</c>/<c>RevertPatch</c>) through
    /// <c>SettingsStore.UpdateAsync</c>'s own critical section AND live-applies whichever of the FEAT-1/
    /// FEAT-15 font/style resources or the M7.1 theme actually changed — the SAME single-writer seams
    /// QA3-1/FEAT-15/M7.1 already own, so an AI-applied change is visible immediately, not only after a
    /// relaunch. Wired from <c>App.axaml.cs</c> (it needs the live <c>Application</c> instance). QA4-17
    /// hostile-round fix: this is a PATCH DELEGATE, not a precomputed full record — the caller re-reads the
    /// base fresh at persist time and mutates only the one field the patch targets, so a concurrent writer
    /// of any OTHER field (a theme toggle, a Settings-tab Save, another AI-applied change) between when
    /// this delegate was built and when it runs can never be silently reverted. Called both for a NEWLY
    /// approved/auto-applied change and for <see cref="RevertLastSettingsChangeCommand"/>. Null in tests /
    /// no-wiring paths ⇒ a settings-change proposal is declined (nothing is ever silently applied).
    /// </summary>
    public Func<Func<HoardPadSettings, HoardPadSettings>, CancellationToken, Task>? ApplySettingsChangeAsync { get; set; }

    // The field-scoped REVERT patch for the last AI-applied change — the exact restore target for
    // RevertLastSettingsChangeCommand ("undoable to the extent settings changes can be", FEAT-14
    // done-when). QA4-17: a patch delegate, not a frozen full snapshot (concurrency-safety — see
    // ApplySettingsChangeAsync remarks). Null when there is nothing to revert (nothing applied yet, or the
    // last applied change was already reverted).
    private Func<HoardPadSettings, HoardPadSettings>? _revertPatch;

    /// <summary>
    /// FEAT-14: the label the Revert button shows (e.g. "↺ Revert: Editor font size"), or empty when
    /// there is nothing to revert. Set alongside <see cref="_revertPatch"/> so the two never drift.
    /// </summary>
    [ObservableProperty]
    private string _revertSettingsChangeLabel = string.Empty;

    /// <summary>FEAT-14: whether <see cref="RevertLastSettingsChangeCommand"/> has anything to restore.</summary>
    public bool CanRevertSettingsChange => _revertPatch is not null;

    // PANEL-3: guards a re-entrant refresh while a Save-As dialog is in flight (the Save-As success re-arms
    // the rows, which would otherwise re-subscribe mid-flight). Also gates reverting a check we are actively
    // resolving via Save-As so the revert-on-cancel does not re-trigger the seam.
    private bool _multiDocSaveAsInFlight;

    /// <summary>
    /// PANEL-3 (FR-MD-6): rebuild the bindable checklist <see cref="MultiDocSelection.Rows"/> for DISPLAY from
    /// the current open-document snapshot — WITHOUT composing any grant set (that stays a turn-arm concern,
    /// FR-MD-9). Called when the panel becomes visible and when the open-tab set / focus changes, so the
    /// checklist is populated and interactive before the user sends a turn. A checkbox the user toggles here
    /// affects only the next turn (FR-MD-6(e)); this display refresh never touches an in-flight turn's grants.
    /// No-op when there is no provider (no-folder / test paths) — the rows simply stay empty.
    /// </summary>
    public void RefreshMultiDocRows()
    {
        if (_multiDocSaveAsInFlight)
            return; // a Save-As is resolving; the success path re-arms once, avoid a re-entrant rebuild.
        if (OpenDocumentsProvider is not { } docsProvider)
            return;

        MultiDoc.Rearm(docsProvider(), SandboxRoot);
        WireScratchSaveAsHooks();
    }

    // PANEL-3: subscribe each SCRATCH row so checking it forces a Save-As (OQ-MD-3). A non-scratch row needs
    // no hook here — its grant assembly is the turn-arm concern. Called after every Rearm/RefreshMultiDocRows.
    private void WireScratchSaveAsHooks()
    {
        foreach (MultiDocRowViewModel row in MultiDoc.Rows)
        {
            if (!row.IsScratch)
                continue;

            MultiDocRowViewModel captured = row;
            captured.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MultiDocRowViewModel.IsChecked)
                    && captured.IsChecked
                    && !_multiDocSaveAsInFlight)
                {
                    _ = OnScratchRowCheckedAsync(captured);
                }
            };
        }
    }

    /// <summary>
    /// PANEL-3 (FR-MD-6(d), OQ-MD-3): a scratch buffer was checked. Force a Save-As so it resolves to a real
    /// path; on success re-arm the rows (the row now has a path and participates as a normal grant); on cancel
    /// revert the check (a path-less buffer cannot be granted). Guarded so the re-arm/revert does not re-fire
    /// the seam. Exposed internal so an interaction test can drive it deterministically without a real dialog.
    /// </summary>
    internal async Task OnScratchRowCheckedAsync(MultiDocRowViewModel scratchRow)
    {
        if (RequestSaveAsForScratchAsync is not { } saveAs)
        {
            // No Save-As wiring (tests / no dialog): a path-less buffer cannot be granted — revert the check.
            scratchRow.IsChecked = false;
            return;
        }

        string? savedPath;
        _multiDocSaveAsInFlight = true;
        try
        {
            savedPath = await saveAs(scratchRow).ConfigureAwait(true);
        }
        finally
        {
            _multiDocSaveAsInFlight = false;
        }

        if (string.IsNullOrEmpty(savedPath))
        {
            // Cancelled Save-As: a path-less buffer cannot be granted (NG-5) — revert the check. Guarded so
            // this revert (IsChecked → false) does not itself re-enter the seam.
            _multiDocSaveAsInFlight = true;
            try { scratchRow.IsChecked = false; }
            finally { _multiDocSaveAsInFlight = false; }
            return;
        }

        // Save-As succeeded: the tab now has a real path. Re-arm from the fresh snapshot so this row is
        // rebuilt as a normal (non-scratch) path-bearing row, then pre-check the row now bearing the SAVED
        // PATH so it participates as a normal grant (matched by the returned path, not the stale name).
        MultiDoc.Rearm(OpenDocumentsProvider?.Invoke() ?? System.Array.Empty<OpenDocumentInfo>(), SandboxRoot);
        WireScratchSaveAsHooks();
        foreach (MultiDocRowViewModel row in MultiDoc.Rows)
        {
            if (string.Equals(row.Path, savedPath, StringComparison.Ordinal))
            {
                row.IsChecked = true; // the just-saved buffer stays checked as a write target.
                break;
            }
        }
    }

    /// <summary>
    /// QA2-18: show the approved <see cref="FirstWriteFolderExplainer"/> as a transcript system note, RIGHT
    /// BEFORE the OS folder picker opens on the first write-mode AI action. The lazy folder-request handler
    /// calls this immediately ahead of awaiting the picker so the user reads WHY a sandbox folder is required
    /// (CLAUDE.md §4) before the OS dialog appears.
    /// </summary>
    public void ShowFirstWriteExplainer() =>
        AddLine(new TranscriptLine { Kind = TranscriptKind.SystemNote, Text = FirstWriteFolderExplainer });

    /// <summary>
    /// Bind an open-folder session: the resolved sandbox root, the shared file-ops service (for FR-39
    /// replay), and the conversation id keying the cap-state. Called by M6.3 when a folder opens; until
    /// then the panel is in the no-folder state (design §6.8). Setting a session does NOT itself arm
    /// write agency — <see cref="ArmAgencyAsync"/> runs the self-test gate (§6.7 Armed requires the pass).
    /// </summary>
    /// <param name="grantComposer">
    /// PANEL-2 (feature-ai-multidoc-panel §5.4): the session-scoped per-turn grant composer the binder builds
    /// over the SAME resolver + root-base the session gate uses. When supplied, a write-capable turn with
    /// checked out-of-folder documents is enforced through a per-turn <c>UnionSandboxGate</c>-composed service
    /// (FR-MD-1). When <c>null</c> (tests / no-multidoc), the panel enforces root-only exactly as before.
    /// </param>
    public void BindSession(
        string sandboxRoot,
        AiFileOpsService fileOps,
        string conversationId,
        IAiTurnGrantComposer? grantComposer = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sandboxRoot);
        ArgumentNullException.ThrowIfNull(fileOps);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        // Fail-closed hardening (hostile-review Warning 2, comment corrected per Warning #5a): a live
        // re-bind mid-session (FS-4's ONLY change-folder path, File ▸ Open Folder) resets the flag HERE —
        // the ONLY writer besides ApplyResolution/ClearSession — so SendAsync's lazy-arm trigger (FS-1)
        // forces a fresh self-test on the very next write-capable turn instead of trusting the prior
        // root's arm. This closes the READ-side of the re-bind race (what a NEW turn's TurnRequest.
        // WriteAgencyArmed reads). It does NOT, by itself, close a cross-root WRITE-scope leak — but no
        // such leak is possible regardless: the self-test itself is root-agnostic (RunFailClosedSelfTestAsync
        // launches the bundled stand-in, never touches _fileOps/the target root), and the actual write
        // path (_fileOps / PathResolverSandboxGate) is bound to THIS call's root synchronously, above,
        // before any await — so an in-flight turn from the PRIOR root keeps writing through the PRIOR
        // root's already-captured _fileOps reference, never this new one. The residual risk this reset
        // ALONE left open — a self-test that was ALREADY in flight for the prior root when this rebind
        // runs could still resolve afterward and cosmetically flip Sandbox back to Armed via
        // ApplyResolution, even though _writeAgencyArmed is what actually gates a turn (a stale COSMETIC
        // state, not a stale WRITE-capability grant) — is now closed by F-FS1d: bumping _bindGeneration
        // HERE invalidates any ArmAgencyAsync call already in flight for the prior bind, so its eventual
        // ApplyResolution is skipped (see ArmAgencyAsync's generation check) instead of applying stale
        // arm-state onto this newer session.
        _bindGeneration++;
        _writeAgencyArmed = false;
        _fileOps = fileOps;
        _sessionRootFileOps = fileOps; // PANEL-2: the root-gated service to restore after a per-turn swap.
        _grantComposer = grantComposer;
        MultiDoc.Reset(); // PANEL-2: a fresh root re-arms the checked set from scratch (FR-MD-9).
        _conversationId = conversationId;
        SandboxRoot = sandboxRoot;
        // The root is open but write agency is not armed until ArmAgencyAsync resolves it (either right
        // after this call, or — FS-1 — deferred to the first write-mode AI action). NotYetArmed keeps the
        // header honest AND shows the root (unlike SelfTestFailed, which reads as a genuine failure).
        Sandbox = SandboxState.NotYetArmed;
        EffectiveMode = AgencyMode.ReadOnly;
        DowngradeBadge = "document folder not yet armed";
    }

    /// <summary>
    /// Clear the open-folder session — back to the no-folder-open state (design §6.8). M6.3 calls this on
    /// folder-close. Write agency becomes unavailable; the cap-state is NOT reset here (only a new
    /// user-initiated conversation resets caps, §4.4 — that is a M6.3 concern keyed by conversation id).
    /// </summary>
    public void ClearSession()
    {
        _fileOps = null;
        _sessionRootFileOps = null;
        _grantComposer = null;
        MultiDoc.Reset(); // PANEL-2: folder close revokes all grants (FR-MD-9).
        _conversationId = null;
        // F-FS1d: invalidate any ArmAgencyAsync already in flight for the just-closed session (same
        // generation guard as BindSession) so a late self-test cannot re-arm a folder the user closed.
        _bindGeneration++;
        _writeAgencyArmed = false;
        SandboxRoot = string.Empty;
        Sandbox = SandboxState.NoFolder;
        EffectiveMode = AgencyMode.ReadOnly;
        DowngradeBadge = string.Empty;
    }

    /// <summary>
    /// Arm the configured agency mode through the FAIL-CLOSED self-test gate (FR-24 §3, TD §4.4, design §6.7).
    /// A write-capable mode runs <see cref="IToolPermissionHookHost.RunFailClosedSelfTestAsync"/>; the
    /// posture is resolved by <see cref="AgencyMapping.Resolve"/>. If the self-test does not pass, write
    /// agency is NEVER armed — the panel downgrades to read-only and shows the downgrade badge. A read-only
    /// configured mode needs no self-test. Returns the resolved posture.
    /// </summary>
    public async Task<AgencyResolution> ArmAgencyAsync(CancellationToken ct = default)
    {
        // F-FS1d: snapshot the generation in effect for THIS call. Only the write-capable branch below
        // has an async gap wide enough for a newer BindSession/ClearSession to supersede it — the
        // no-folder / read-only branches resolve synchronously, in the same UI-thread turn as the bind
        // call that preceded them, so nothing else can run in between and the snapshot cannot go stale.
        int myBindGeneration = _bindGeneration;

        // No folder open → write agency cannot be armed regardless of the configured mode (design §6.8).
        if (_fileOps is null)
        {
            _writeAgencyArmed = false;
            EffectiveMode = AgencyMode.ReadOnly;
            DowngradeBadge = ConfiguredMode == AgencyMode.ReadOnly ? string.Empty : "no folder open";
            Sandbox = SandboxState.NoFolder;
            return new AgencyResolution(PermissionPosture.NoWriteTools, WriteAgencyArmed: false, GateDenied: false);
        }

        // A read-only configured mode needs no self-test (AgencyMapping.Resolve short-circuits it).
        if (!AgencyMapping.IsWriteCapable(ConfiguredMode))
        {
            AgencyResolution ro = AgencyMapping.Resolve(ConfiguredMode, selfTest: null);
            ApplyResolution(ro);
            return ro;
        }

        // Write-capable: run the fail-closed self-test FIRST. It is the SOLE guard against a silently-
        // disarmed sandbox (claude -p ignores an invalid hook config — SPIKE-3 D3). The launcher resolve
        // + the self-test are OS legs; run them off the UI thread so a slow self-test never freezes the panel.
        //
        // M11 invariant 5 (docs/escalation-ai-live-write-path.md §5): when the LIVE approve-each channel is
        // in play, the killed-channel deny self-test (RunChannelKillSelfTestAsync) is ALSO a precondition
        // for arming write agency. A real spawn whose channel is killed mid-ask must deny + leave no file;
        // a failing channel-kill self-test fails closed to read-only + the downgrade badge, exactly like
        // the existing gate. The impl already exists + is gated; the App only RUNS it through the seam (no
        // decision logic added).
        //
        // FS-5 / FR-10(a) (docs/decisions.md DR-1, cross-platform-review REVISE fix): "approve-each is the
        // only mode that uses an `approve` channel" is NO LONGER TRUE — the FR-10(a) home/large-root
        // downgrade (below) can drive EffectiveMode=ApproveEach for a user-configured AutoApply session too,
        // and AiTurnRunner.MergeObserveEnv keys HOARDPAD_OBSERVE_MODE=approve off EffectiveMode, not
        // ConfiguredMode. Arming an AutoApply+ConfirmLargeHomeFolder session without the channel-kill gate
        // would let a live approve-each turn run on weaker arm-time proof than the mode it can degrade
        // into. The condition below is therefore a SUPERSET: it also runs the gate whenever an arm COULD
        // downgrade to approve-each (ConfirmLargeHomeFolder ON under AutoApply), not only when the exact
        // trigger fires — running it on a root that turns out small is harmless over-caution (fail-safe),
        // and it avoids re-ordering the self-test ahead of the (real, bounded-I/O) home/large-root walk.
        bool requiresChannelKillGate = ConfiguredMode == AgencyMode.ApproveEach
            || (ConfiguredMode == AgencyMode.AutoApply && ConfirmLargeHomeFolder);
        SelfTestResult selfTest;
        try
        {
            selfTest = await Task.Run(async () =>
            {
                // STF-3: the self-test gates launch the bundled stand-in, NEVER the real `claude` CLI
                // (_backend.ResolveLauncher). The real CLI has no knowledge of the descriptor protocol
                // the gates drive and can never satisfy them (docs/stf-diagnosis.md §(a)) — the actual
                // turn launch below (IAiTurnRunner / AgentBackend.StartTurnAsync) is a completely
                // separate call path and still resolves + launches the real CLI, untouched by this.
                var launcher = new LaunchDescriptor(
                    _selfTestStandInPath, IsCmd: false, FromPath: false, ResolvedVia: "self-test-standin");
                SelfTestResult failClosed = await _hookHost.RunFailClosedSelfTestAsync(launcher, ct).ConfigureAwait(false);
                if (!failClosed.Passed || !requiresChannelKillGate)
                    return failClosed;

                // Both gates must pass for the live approve-each path. A failing channel-kill self-test is
                // surfaced as the overall self-test failure (the same fail-closed downgrade posture).
                SelfTestResult channelKill = await _hookHost.RunChannelKillSelfTestAsync(launcher, ct).ConfigureAwait(false);
                return channelKill.Passed
                    ? failClosed
                    : new SelfTestResult(Passed: false, Detail: "channel-kill self-test failed: " + channelKill.Detail);
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any failure to RUN the self-test is treated as a failed self-test (absent proof of arming ⇒
            // no write agency, TD §4.4). Fail closed to read-only + the downgrade badge.
            selfTest = new SelfTestResult(Passed: false, Detail: ex.Message);
        }

        AgencyResolution resolution = AgencyMapping.Resolve(ConfiguredMode, selfTest);

        // FS-5 / FR-10(a) (docs/decisions.md DR-1): once write agency has armed for an auto-apply
        // session, check the home/large-root trigger — but only when the setting is ON, since the walk
        // is real (bounded) I/O. Off the UI thread, mirroring the self-test's own Task.Run above. This
        // is an App-layer override of the DISPLAY/effective mode only; the already-computed `resolution`
        // (the spine's self-test-gated posture/armed-flag) is passed through to ApplyResolution/the
        // caller UNCHANGED — see DR-1 "Mechanism".
        DowngradeReason homeOrLargeReason = DowngradeReason.None;
        if (resolution.WriteAgencyArmed && ConfirmLargeHomeFolder && ConfiguredMode == AgencyMode.AutoApply)
        {
            string root = SandboxRoot;
            int threshold = LargeRootThreshold;
            try
            {
                homeOrLargeReason = await Task.Run(
                    () => _largeHomeRootDetector(root, threshold, ct), ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is first-class — never swallowed.
            }
            catch (Exception)
            {
                // FS-5 / FR-10(a) (docs/decisions.md DR-1, cross-platform-review nit fix): an UNEXPECTED
                // detector fault (the walk is only best-effort-safe against the exceptions it explicitly
                // catches — see LargeHomeRootDetector's doc) must fail TOWARD CONFIRM, never toward silent
                // auto-apply — the safe direction for a trigger whose whole job is deciding confirm-vs-
                // silent. Treated as LargeRoot (the generic "the walk couldn't prove this root is small"
                // outcome). FR-26/caps still bound the write either way; this only changes whether it
                // confirms first.
                homeOrLargeReason = DowngradeReason.LargeRoot;
            }
        }

        // F-FS1d (bind-generation epoch guard, defense-in-depth / confirmed non-escape — see BindSession):
        // a newer BindSession/ClearSession may have superseded this arm attempt while the self-test
        // and/or the home/large-root probe above was in flight (e.g. the user opened a different folder
        // before this one's self-test resolved). Applying THIS resolution onto the newer session's panel
        // state would be stale cosmetic arm-state, so the newest bind must win — skip ApplyResolution and
        // hand the caller the resolution as computed, unapplied. The real sandbox/cap/hook enforcement is
        // unaffected either way (it is scoped to the CURRENT _fileOps/root, never to this flag), so
        // skipping here is UI/flag hygiene only, not a safety change.
        if (myBindGeneration != _bindGeneration)
            return resolution;

        ApplyResolution(resolution, homeOrLargeReason);
        return resolution;
    }

    /// <summary>
    /// FS-1 (Decision A, FINAL): called right after a startup <c>BindSession</c> instead of
    /// <see cref="ArmAgencyAsync"/> directly. A write-capable <see cref="ConfiguredMode"/> is left
    /// <see cref="SandboxState.NotYetArmed"/> — arming (and its self-test) defers to the first write-mode AI
    /// action (<see cref="SendAsync"/>'s lazy-arm trigger), never at startup. A read-only configured mode
    /// arms immediately regardless — <see cref="ArmAgencyAsync"/>'s read-only branch is a cheap, no-self-test
    /// resolution, so there is nothing to defer and staying <c>NotYetArmed</c> forever would be misleading.
    /// </summary>
    public Task ArmIfReadOnlyElseDeferAsync(CancellationToken ct = default) =>
        AgencyMapping.IsWriteCapable(ConfiguredMode) ? Task.CompletedTask : ArmAgencyAsync(ct);

    private void ApplyResolution(AgencyResolution resolution, DowngradeReason homeOrLargeReason = DowngradeReason.None)
    {
        // Store the resolved write-agency flag directly from the resolution (safe-by-construction). This is
        // the ONLY writer of _writeAgencyArmed; TurnRequest.WriteAgencyArmed reads it from here rather than
        // deriving from Sandbox state, which would be true for read-only-with-open-folder (safe-by-coincidence
        // via the spine's mode foreclosure, not by construction — W-1 fix).
        _writeAgencyArmed = resolution.WriteAgencyArmed;

        if (resolution.WriteAgencyArmed)
        {
            if (homeOrLargeReason != DowngradeReason.None)
            {
                // FS-5 / FR-10(a) (docs/decisions.md DR-1): downgrade an auto-apply op to a one-time
                // approve-each confirm on a home/large root. This REUSES the existing approve-each
                // machinery (AiTurnRunner.MergeObserveEnv keys HOARDPAD_OBSERVE_MODE off EffectiveMode,
                // and the header/badge rendering already expects exactly this shape) — never a separate
                // code path. WriteAgencyArmed/Sandbox are unaffected: the op still applies, just after a
                // confirm, so caps/undo/log/FR-26 are all untouched (DR-1's exact boundary).
                EffectiveMode = AgencyMode.ApproveEach;
                DowngradeBadge = homeOrLargeReason == DowngradeReason.HomeRoot ? "home root" : "large root";
            }
            else
            {
                EffectiveMode = ConfiguredMode;
                DowngradeBadge = string.Empty;
            }
            Sandbox = SandboxState.Armed;
        }
        else if (resolution.GateDenied)
        {
            // A write-capable mode that failed the self-test gate (FR-24 Done-when "sandbox could not be armed").
            EffectiveMode = AgencyMode.ReadOnly;
            DowngradeBadge = "self-test failed";
            Sandbox = SandboxState.SelfTestFailed;
        }
        else
        {
            // A read-only configured mode — no downgrade, just read-only.
            EffectiveMode = AgencyMode.ReadOnly;
            DowngradeBadge = string.Empty;
            // Keep an open-folder sandbox state honest: a read-only mode with a folder open is still
            // "armed" as a sandbox (the root is resolved); it just offers no write tools.
            Sandbox = _fileOps is null ? SandboxState.NoFolder : SandboxState.Armed;
        }
    }

    // ----- Send a turn (region 6) -----

    private bool CanSend() => !IsTurnInFlight && !_isTesting && PromptInput.Trim().Length > 0;

    /// <summary>
    /// Send the prompt as one turn (FR-25). Runs the turn off the UI thread through <see cref="IAiTurnRunner"/>;
    /// each yielded event marshals back to render the transcript + advance the caps. A <see cref="TimeProvider"/>-
    /// driven no-reply timer fails closed if no event arrives within the timeout. Stop / cancel ends it mid-stream.
    ///
    /// BUG-4: also tracks backend health — updates <see cref="BackendStatus"/> and renders a diagnostic error
    /// chip when a non-cancellation exception occurs or the no-reply timer fires.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        string prompt = PromptInput.Trim();
        if (prompt.Length == 0 || IsTurnInFlight)
            return;

        // QA3-8: a write-capable turn against a never-saved (scratch, no on-disk path) FOCUSED document must
        // prompt the user to save FIRST — immediately, before the QA2-18 folder request, the multi-doc grant
        // arm, and the first backend call — so the AI never has to discover mid-turn that it cannot write and
        // only mention it several rounds later. Independent of whether a sandbox folder is bound yet (saving
        // the buffer is a prerequisite regardless of the AI sandbox state). A read-only configured mode never
        // prompts (there is nothing to write). Declining the save just leaves the document non-grantable
        // (NG-5) — the turn still proceeds afterward, unchanged from the pre-fix behavior.
        if (AgencyMapping.IsWriteCapable(ConfiguredMode)
            && OpenDocumentsProvider is { } focusedDocsProvider
            && RequestSaveForUnsavedFocusedDocAsync is { } requestSave)
        {
            OpenDocumentInfo? focusedDoc = null;
            foreach (OpenDocumentInfo doc in focusedDocsProvider())
            {
                if (doc.IsFocused)
                {
                    focusedDoc = doc;
                    break;
                }
            }

            if (focusedDoc is { IsScratch: true })
                await requestSave(focusedDoc).ConfigureAwait(true);
        }

        // QA2-18 LAZY picker / FS-1 LAZY arm: on the first WRITE-capable turn, resolve write agency NOW —
        // never at startup. A read-only configured mode NEVER prompts or arms — it stays read-only (the maintainer's
        // decision).
        //   - No folder bound at all (_fileOps is null — the pre-FS-1 "genuinely nothing known" case, still
        //     reachable e.g. after a declined BindDerivedRootAsync): fire the OS folder picker via the wired
        //     handler (shows FirstWriteFolderExplainer first), which binds + arms a confirmed root.
        //   - FS-1: a folder IS already bound (RestoreAsync always derives one) but write agency has not
        //     armed yet (NotYetArmed) — run the self-test-gated arm directly, no picker (the root is already
        //     known and shown on the scope line).
        if (AgencyMapping.IsWriteCapable(ConfiguredMode) && !_writeAgencyArmed)
        {
            if (_fileOps is null && RequestWriteFolderAsync is { } requestFolder)
                await requestFolder().ConfigureAwait(true);
            else if (_fileOps is not null)
                await ArmAgencyAsync().ConfigureAwait(true);
        }

        // PANEL-2 (feature-ai-multidoc-panel FR-MD-1/3/6/9): arm the multi-doc checked set for THIS turn and
        // compose the per-turn Archetype-B write path. Recomputed per turn at arm time (FR-MD-9): snapshot the
        // open documents, rearm the selection (pre-checking the focused doc by default on first arm, FR-MD-6(a)),
        // resolve the checked out-of-folder paths into an immutable grant set through the SAME resolver leg the
        // gate uses, and swap _fileOps to the UnionSandboxGate-composed per-turn service. Restored in the finally.
        // The context descriptor (FR-MD-3) is assembled from the same rows and prepended to the runner prompt.
        // Only a write-capable, folder-bound turn with a wired composer arms grants; otherwise this is a no-op
        // and the panel behaves exactly as before (root-only).
        string turnPrompt = prompt;
        // PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): the resolved grant paths for THIS turn, threaded
        // into TurnRequest.FileGrantPaths so the runner delivers them to the Archetype-A hook via
        // HOARDPAD_FILE_GRANTS. This is the SAME set that composed the Archetype-B UnionSandboxGate below —
        // both archetypes enforce byte-identical grants. Empty when no multi-doc arm ran (root-only, FR-MD-10).
        IReadOnlyList<string>? turnGrantPaths = null;
        if (_grantComposer is { } composer
            && AgencyMapping.IsWriteCapable(EffectiveMode)
            && _writeAgencyArmed
            && OpenDocumentsProvider is { } docsProvider)
        {
            IReadOnlyList<OpenDocumentInfo> openDocs = docsProvider();
            MultiDoc.Rearm(openDocs, SandboxRoot);

            TurnGrantComposition composition = composer.ComposeTurn(MultiDoc.CheckedGrantablePaths());
            _fileOps = composition.TurnFileOps; // the per-turn UnionSandboxGate-composed write path (FR-MD-9).
            turnGrantPaths = composition.ResolvedGrantPaths; // the SAME set for the Archetype-A grants file.
            // OQ-MD-5 option ii: publish the turn's grants so a parked op's raw target can be matched to its
            // single resolved grant at park time (persisted as GrantedResolvedPath → union-gated replay).
            _currentTurnGrantPaths = composition.ResolvedGrantPaths;

            string descriptor = MultiDocContextDescriptor.Build(MultiDoc.CurrentRows, SandboxRoot);
            if (descriptor.Length > 0)
                turnPrompt = descriptor + "\n" + prompt;

            // PANEL-4 (feature-ai-multidoc-panel FR-MD-7/8, AC-MD-3): the strict pre-turn ordering
            //   1. FLUSH every checked dirty doc to disk (each sealing an editor undo point), then
            //   2. CAPTURE the before-image from the FLUSHED on-disk content,
            // both BEFORE the first backend call below (_runner.RunAsync). The before-image reflects the
            // bytes the agent actually reads/mutates (rationale FR-MD-8) — capturing before the flush would
            // restore stale pre-flush bytes on undo/replay. On a flush FAILURE this fails safe: the session
            // root write path is restored and the turn is NOT launched against stale on-disk bytes.
            if (!await FlushCheckedDirtyDocsAndCaptureAsync(composition.ResolvedGrantPaths).ConfigureAwait(true))
            {
                // A checked dirty doc failed to flush — abort the turn-arm (fail-closed durability posture).
                if (_sessionRootFileOps is not null)
                    _fileOps = _sessionRootFileOps;
                _currentTurnGrantPaths = System.Array.Empty<string>();
                AddLine(new TranscriptLine
                {
                    Kind = TranscriptKind.SafetyEventChip,
                    Text = "Turn aborted — a selected document could not be saved to disk. "
                        + "Save it manually, then retry so the AI acts on current content.",
                    IsDanger = true,
                });
                return;
            }
        }

        // QA4-12 (DR-QA4-12, docs/decisions.md): prepend the prior-turns context block — read from
        // Transcript BEFORE this Send's own UserMessage line is appended below, so it carries only EARLIER
        // turns. Wraps whatever turnPrompt currently holds (the bare prompt, or the PANEL-2 descriptor-
        // prefixed prompt above) rather than the raw `prompt`, so a multi-doc turn's descriptor is never
        // silently discarded. Empty on the conversation's first Send — turnPrompt is then unchanged.
        // DS-5 (DR-DS-2/DR-DS-3): each folded prior line is scrubbed (Engine.ScrubInput, Level.Standard)
        // before it enters Block; Findings/WasModified are carried on the result for the DS-6 badge+log
        // surface to consume — not yet rendered here.
        ConversationContextResult conversationContext = AiConversationContextBuilder.Build(Transcript);
        if (conversationContext.Block.Length > 0)
            turnPrompt = conversationContext.Block + turnPrompt;

        // DS-6 (DR-DS-3): surface the DS-5 inbound-scrub outcome — a non-blocking chip + log entry, only
        // when at least one span was actually redacted (WasModified). A clean prior conversation (the
        // common case) gets neither.
        if (conversationContext.WasModified)
            await ReportScrubFindingsAsync("prior conversation", conversationContext.Findings.Count, conversationContext.Findings)
                .ConfigureAwait(true);

        // FEAT-14 (architecture AI file-op safety + FR boundaries): offer the settings-change capability
        // under the EXACT SAME agency posture as a file write (architecture §4.2 step 2) — a read-only or
        // self-test-failed-downgraded turn never sees the capability offered, so it can never propose (or
        // have applied) a settings mutation either. Independent of the PANEL-2 multi-doc block above
        // (a settings change is not a file op — it needs no grant composer, no sandbox root).
        if (AgencyMapping.IsWriteCapable(EffectiveMode))
            turnPrompt = SettingsChangeProposalParser.CapabilityInstruction + "\n\n" + turnPrompt;

        AddLine(new TranscriptLine { Kind = TranscriptKind.UserMessage, Text = prompt });
        PromptInput = string.Empty;

        var turnCts = new CancellationTokenSource();
        _turnCts = turnCts;
        IsTurnInFlight = true;
        BackendStatus = BackendConnectionState.Connecting;
        BackendStatusMessage = "Connecting...";

        var assistantLine = new TranscriptLine { Kind = TranscriptKind.AssistantMessage, Text = string.Empty };
        AddLine(assistantLine);

        // The no-reply fail-closed timer (TimeProvider-driven, architecture §3.4). It fires when the turn
        // produces NO event within the timeout; each received event resets the deadline. On expiry it
        // cancels the turn (Stop path) + parks the pending op (FR-39).
        ITimer? noReplyTimer = null;
        // WriteAgencyArmed is derived from the stored AgencyResolution flag (safe-by-construction — W-1 fix).
        // Do NOT derive from Sandbox state; Sandbox == Armed is true for a read-only-with-open-folder turn.
        // BUG-5: thread model/effort overrides from settings (empty string → null → backend default).
        // PANEL-2: turnPrompt carries the FR-MD-3 checked-set descriptor prepended to the user's prompt (or
        // is the bare prompt when no multi-doc arm ran). The transcript shows the bare prompt (added above);
        // the runner/backend receives the descriptor-augmented text.
        var request = new TurnRequest(
            turnPrompt,
            EffectiveMode,
            WriteAgencyArmed: _writeAgencyArmed,
            ModelId: string.IsNullOrWhiteSpace(CurrentModelId) ? null : CurrentModelId,
            EffortLevel: string.IsNullOrWhiteSpace(CurrentEffortLevel) ? null : CurrentEffortLevel,
            FileGrantPaths: turnGrantPaths);

        try
        {
            noReplyTimer = _time.CreateTimer(_ => OnNoReply(turnCts), state: null, _noReplyTimeout, Timeout.InfiniteTimeSpan);

            await foreach (TurnEvent ev in _runner.RunAsync(request, turnCts.Token).ConfigureAwait(true))
            {
                // Reset the no-reply deadline — an event arrived, so the turn is live.
                noReplyTimer.Change(_noReplyTimeout, Timeout.InfiniteTimeSpan);
                await HandleTurnEventAsync(ev, assistantLine, turnCts).ConfigureAwait(true);
            }

            // FEAT-14: only after a CLEAN turn completion (never on cancellation/error below — a partial or
            // garbled response must not trigger a settings mutation) and only when this turn was
            // write-capable (the same posture the capability was offered under, above). This gates APPLYING
            // a proposal only — QA4-17 hostile-round (3rd round): the separate HYGIENE strip (removing the
            // marker text from the transcript regardless of outcome) runs unconditionally in `finally`
            // below, in every agency mode and on every terminal path, not just this one.
            if (AgencyMapping.IsWriteCapable(EffectiveMode))
                await HandleSettingsChangeProposalAsync(assistantLine, turnCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A Stop / no-reply kill cancelled the turn — handled in finally.
        }
        catch (Exception ex)
        {
            // BUG-4: render a diagnostic error chip for non-cancellation failures (backend launch failed,
            // process crashed, etc.). The user sees why the turn produced no response.
            string errorText = MapTurnException(ex);
            BackendStatus = BackendConnectionState.Error;
            BackendStatusMessage = $"Error: {errorText}";
            AddLine(new TranscriptLine
            {
                Kind = TranscriptKind.SafetyEventChip,
                Text = errorText,
                IsDanger = true,
            });
        }
        finally
        {
            // QA4-17 hostile-round (3rd round CRITICAL, both spec-hostile + security): the marker-hygiene
            // strip is MODE- and OUTCOME-INDEPENDENT — it runs here, unconditionally, on EVERY terminal path
            // (clean completion in ANY agency mode including ReadOnly, cancellation, and fault), never
            // gated on IsWriteCapable/clean-completion the way the APPLY logic above is. A prior version
            // ran the strip only inside HandleSettingsChangeProposalAsync, which is itself gated on
            // write-capable + reaching this point without throwing — so it silently never stripped in
            // ReadOnly (a first-class user-selectable mode AND the downgrade target of several FR-10/
            // self-test paths — the AI still READS sandboxed files there) nor on a Stop/no-reply-timeout/
            // backend-crash mid-stream (assistantLine was already mutated live by HandleTurnEventAsync
            // before either exception path is reached). Idempotent + safe to call even when nothing
            // streamed at all (StripMarker no-ops on text with no marker-shaped fence).
            assistantLine.Text = SettingsChangeProposalParser.StripMarker(assistantLine.Text, out int blocksRemoved);

            // DS-6 (DR-DS-3): surface the DS-3 outbound-scrub outcome — a non-blocking chip + log entry,
            // only when at least one `hoardpad-settings` block was actually redacted. RedactBlock is a
            // block-granular primitive (no per-span category/rule-id/confidence — see BlockScrubResult),
            // so this carries only the block count; the inbound side above carries full Finding detail.
            if (blocksRemoved > 0)
                await ReportScrubFindingsAsync("assistant reply", blocksRemoved, Array.Empty<Finding>())
                    .ConfigureAwait(true);

            noReplyTimer?.Dispose();

            // BUG-4: detect no-reply timeout (the timer fired and set _noReplyFired before the OCE was
            // caught). Override any intermediate status with the no-reply error.
            if (_noReplyFired)
            {
                BackendStatus = BackendConnectionState.Error;
                BackendStatusMessage = "Error: no response from backend";
                AddLine(new TranscriptLine
                {
                    Kind = TranscriptKind.SafetyEventChip,
                    Text = "Backend not responding — check connectivity",
                    IsDanger = true,
                });
                _noReplyFired = false;
            }
            else if (BackendStatus == BackendConnectionState.Connecting)
            {
                if (_runner is NoSessionTurnRunner)
                {
                    // No folder is open — the canned note was emitted but no real backend was contacted.
                    BackendStatus = BackendConnectionState.Error;
                    BackendStatusMessage = "No folder open — open a folder to enable AI";
                }
                else
                {
                    // Turn completed without exception and no no-reply trigger — the backend is responsive.
                    BackendStatus = BackendConnectionState.Connected;
                    BackendStatusMessage = "Ready";
                }
            }
            IsTurnInFlight = false;
            if (ReferenceEquals(_turnCts, turnCts))
                _turnCts = null;
            turnCts.Dispose();
            // PANEL-2: restore the session ROOT-gated write path (FR-MD-9). A per-turn UnionSandboxGate-composed
            // service is valid ONLY for the turn it was armed for; restoring here means a mid-turn checkbox
            // toggle affects the NEXT turn (recomputed at its arm), and a stale grant can never outlive its turn.
            if (_sessionRootFileOps is not null)
                _fileOps = _sessionRootFileOps;
            // OQ-MD-5 option ii: clear the turn's published grants — a grant is valid only for the turn it was
            // armed for (FR-MD-9). Any op parked during the turn has ALREADY captured its GrantedResolvedPath
            // on its proposal (park happens mid-turn, from the observe loop, before this finally runs), so the
            // parked op's replay grant survives here; only the live matching window closes.
            _currentTurnGrantPaths = System.Array.Empty<string>();
            // Drain the tool-input cache (MF1.4, FR-39 replay). Entries are only valid within the turn
            // that produced them: the hook ask always arrives before the turn stream ends, so every
            // cached entry was either consumed at park time (TryRemove in ConfirmHookDecisionAsync) or
            // belongs to an op that was auto-applied (never parked, never consumed). Clearing here
            // makes "bounded by turn lifetime" true — without this, auto-apply mode leaks one entry
            // per write-op indefinitely over the app-scoped VM lifetime.
            _toolInputCache.Clear();
        }
    }

    /// <summary>
    /// FEAT-14: parse the completed assistant response for a settings-change marker and, if one is found
    /// and it validates against the allow-list, route it through the SAME agency/confirm safety model a
    /// file write uses (architecture §4.2 step 2) — auto-apply applies immediately, approve-each shows the
    /// Deny-focused confirm — then persists + live-applies it and leaves an auditable transcript entry.
    /// Never applies anything when <see cref="CurrentSettingsProvider"/>/<see cref="ApplySettingsChangeAsync"/>
    /// are unwired (tests / no-op paths) — the proposal is silently declined, the same fail-closed posture
    /// every other unwired seam in this view-model has.
    ///
    /// QA4-17 hostile-round (3rd round): this method is ONLY the "parse → allow-list → apply" half — it is
    /// deliberately called from ONE spot, inside the try block, gated on write-capable + a CLEAN turn
    /// completion (never on a partial/garbled/cancelled response, and never in ReadOnly — an unsolicited
    /// marker must never be APPLIED there). It parses <paramref name="assistantLine"/>.Text AS-IS (still
    /// unstripped at this point in the flow). The HYGIENE half — stripping the fenced marker block from
    /// what stays displayed/persisted — is a SEPARATE, MODE- and OUTCOME-INDEPENDENT concern now handled by
    /// <see cref="SendAsync"/>'s own <c>finally</c> block (via <c>StripSettingsChangeMarkerHygieneOnly</c>),
    /// which runs on EVERY terminal path of a turn (clean completion in ANY agency mode including
    /// ReadOnly, cancellation, and fault) — not just the write-capable-and-clean path this method gates on.
    /// A 2nd-round fix had stripping live INSIDE this method, which meant it silently never ran in ReadOnly
    /// and never ran on a cancelled/faulted turn (both real leak paths: ReadOnly is a first-class mode AND
    /// the downgrade target of several FR-10/self-test paths, and a Stop/no-reply/crash leaves whatever was
    /// streamed — marker included — in the transcript). Splitting the two concerns is what closes both.
    /// </summary>
    private async Task HandleSettingsChangeProposalAsync(TranscriptLine assistantLine, CancellationToken ct)
    {
        if (!SettingsChangeProposalParser.TryParse(assistantLine.Text, out string? field, out string? rawValue))
            return; // no marker in this response — the common case (or a malformed one — hygiene strips it later).

        if (CurrentSettingsProvider is not { } loadCurrent || ApplySettingsChangeAsync is not { } apply)
            return; // seam not wired — declined, never applied.

        HoardPadSettings current = await loadCurrent(ct).ConfigureAwait(true);
        SettingsChangeComputation? computation = SettingsChangeApplier.TryCompute(current, field!, rawValue!);
        if (computation is null)
        {
            AddLine(new TranscriptLine
            {
                Kind = TranscriptKind.SystemNote,
                Text = $"AI proposed a settings change that could not be applied ({field}) — ignored.",
            });
            return;
        }

        // Auto-apply applies immediately (same posture as an auto-apply file write); approve-each shows
        // the Deny-focused confirm first (EffectiveMode is one of these two — HandleSettingsChangeProposalAsync
        // is only reached when AgencyMapping.IsWriteCapable(EffectiveMode), i.e. never ReadOnly).
        bool approved = EffectiveMode == AgencyMode.AutoApply
            || await _confirm.ConfirmSettingsChangeAsync(
                new SettingsChangeConfirmRequest(computation.FieldLabel, computation.OldDisplay, computation.NewDisplay),
                ct).ConfigureAwait(true);

        if (!approved)
        {
            AddLine(new TranscriptLine
            {
                Kind = TranscriptKind.SystemNote,
                Text = $"Settings change declined: {computation.FieldLabel} ({computation.OldDisplay} -> {computation.NewDisplay}).",
            });
            return;
        }

        // QA4-17 hostile-round concurrency fix: persist the FIELD-SCOPED patch, never a frozen multi-field
        // snapshot — `apply` re-reads the base fresh inside SettingsStore.UpdateAsync's own critical
        // section (App.ApplyAiSettingsChangeAsync) and mutates ONLY the field this proposal computed, so a
        // concurrent writer of any OTHER field (a theme toggle, a Settings-tab Save, …) between this
        // confirm await and now can never be silently reverted.
        await apply(computation.Patch, ct).ConfigureAwait(true);

        // Undoable to the extent settings changes can be (FEAT-14 done-when): remember a field-scoped
        // REVERT patch (not a full snapshot — same concurrency-safety reasoning as the forward patch above).
        _revertPatch = computation.RevertPatch;
        RevertSettingsChangeLabel = $"↺ Revert: {computation.FieldLabel}";
        OnPropertyChanged(nameof(CanRevertSettingsChange));
        RevertLastSettingsChangeCommand.NotifyCanExecuteChanged();

        // Auditable (FEAT-14 done-when): a visible transcript entry naming the exact old → new change.
        AddLine(new TranscriptLine
        {
            Kind = TranscriptKind.SafetyEventChip,
            Text = $"Setting changed — {computation.FieldLabel}: {computation.OldDisplay} -> {computation.NewDisplay}",
        });
    }

    private bool CanRevertLastSettingsChange() => _revertPatch is not null;

    /// <summary>
    /// FEAT-14: restore the field this VM last AI-applied to its pre-change value (the "undoable to the
    /// extent settings changes can be" done-when) via a field-scoped patch — same concurrency-safety
    /// posture as the forward apply. A no-op when there is nothing to revert or the seam is unwired.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevertLastSettingsChange))]
    private async Task RevertLastSettingsChangeAsync()
    {
        if (_revertPatch is not { } revertPatch || ApplySettingsChangeAsync is not { } apply)
            return;

        await apply(revertPatch, CancellationToken.None).ConfigureAwait(true);

        AddLine(new TranscriptLine { Kind = TranscriptKind.SystemNote, Text = "Reverted the last AI-applied settings change." });

        _revertPatch = null;
        RevertSettingsChangeLabel = string.Empty;
        OnPropertyChanged(nameof(CanRevertSettingsChange));
        RevertLastSettingsChangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// PANEL-4 (feature-ai-multidoc-panel FR-MD-7/8, AC-MD-3): the strict pre-turn flush → before-image
    /// ordering, run BEFORE the first backend call. For every CHECKED, DIRTY, non-scratch row (keyed by its
    /// on-disk path): (1) FLUSH the buffer to disk via <see cref="FlushDocumentAsync"/> (which saves through
    /// the durable path and seals an editor undo point); then (2) CAPTURE the before-image from the FLUSHED
    /// on-disk content via the wired before-image reader — the captured hash reflects the flushed bytes (not
    /// the pre-flush bytes). Records the ordered flush/before-image events into
    /// <see cref="PreTurnOrderLog"/> so the AC-MD-3 acceptance can assert flush-strictly-before-before-image.
    ///
    /// Returns <c>true</c> to proceed with the turn; <c>false</c> if any checked dirty doc FAILED to flush —
    /// the caller then aborts the turn-arm (fail-safe: never launch the agent against stale on-disk bytes).
    /// When the flush seam is unwired (tests / no-op paths), a dirty checked doc cannot be flushed, so this
    /// returns <c>false</c> only when a flush was actually attempted and failed; with no seam AND no dirty
    /// checked docs it returns <c>true</c> (nothing to flush). A checked doc that is NOT dirty skips the
    /// flush (nothing to write) but its before-image is still captured before the turn (step 2 before 3).
    /// </summary>
    private async Task<bool> FlushCheckedDirtyDocsAndCaptureAsync(IReadOnlyList<string> resolvedGrantPaths)
    {
        _preTurnOrderLog.Clear();
        _lastBeforeImageHashByPath.Clear();

        foreach (MultiDocRowViewModel row in MultiDoc.CurrentRows)
        {
            if (!row.IsChecked || row.IsScratch || row.Path is not { Length: > 0 } path)
                continue;

            // Step 1 — FLUSH (only a DIRTY checked doc; a clean one has nothing to write, FR-MD-7).
            if (row.IsDirty)
            {
                if (FlushDocumentAsync is not { } flush)
                    return false; // a dirty checked doc with no flush seam cannot be made current — fail safe.

                bool flushed = await flush(path).ConfigureAwait(true);
                if (!flushed)
                    return false; // save failed / no matching tab — do NOT launch against stale bytes.

                _preTurnOrderLog.Add(new PreTurnEvent(PreTurnStep.Flush, path));
            }

            // Step 2 — CAPTURE the before-image from the FLUSHED on-disk content (AC-MD-3 hash equality).
            // Routes through the SAME resolver + SandboxPolicy leg the write path uses (FR-26 by
            // construction). A checked out-of-folder doc is read through the union reader with THIS turn's
            // grants; an in-root doc through either reader. Absent readers (tests not exercising capture)
            // skip the recorded capture — the flush ordering is still enforced by the await sequence.
            string? hash = await CaptureBeforeImageHashAsync(path, resolvedGrantPaths).ConfigureAwait(true);
            if (hash is not null)
            {
                _lastBeforeImageHashByPath[path] = hash;
                _preTurnOrderLog.Add(new PreTurnEvent(PreTurnStep.BeforeImage, path));
            }
        }

        return true;
    }

    /// <summary>
    /// PANEL-4 (FR-MD-8 step 2): read the turn-level before-image of a flushed target from its on-disk
    /// content and return the content hash, or <c>null</c> when no reader is wired or the read is Refused
    /// (containment) / NotFound. Uses the union reader (with the turn's grants) when available so a checked
    /// out-of-folder target is readable; otherwise the root reader. This is a turn-level capture of the
    /// flushed state — the per-op M15 before-image inside the frozen spine is unchanged and orthogonal.
    /// </summary>
    private async Task<string?> CaptureBeforeImageHashAsync(string rawTargetPath, IReadOnlyList<string> resolvedGrantPaths)
    {
        if (string.IsNullOrEmpty(SandboxRoot))
            return null;

        BeforeImageResult result;
        if (_unionBeforeImageReader is { } unionReader && resolvedGrantPaths.Count > 0)
        {
            var grants = new HashSet<string>(resolvedGrantPaths, StringComparer.Ordinal);
            result = await unionReader.ReadAsync(rawTargetPath, SandboxRoot, grants, CancellationToken.None)
                .ConfigureAwait(true);
        }
        else if (_beforeImageReader is { } reader)
        {
            result = await reader.ReadAsync(rawTargetPath, SandboxRoot, CancellationToken.None)
                .ConfigureAwait(true);
        }
        else
        {
            return null; // no reader wired — the flush ordering still holds via the await sequence.
        }

        return result is BeforeImageResult.Found found ? found.Capture.ContentHash : null;
    }

    private async Task HandleTurnEventAsync(TurnEvent ev, TranscriptLine assistantLine, CancellationTokenSource turnCts)
    {
        switch (ev.Kind)
        {
            case TurnEventKind.AssistantText:
                assistantLine.Text += ev.Text ?? string.Empty;
                break;

            case TurnEventKind.FileOp:
                AddLine(new TranscriptLine
                {
                    Kind = TranscriptKind.FileOpChip,
                    Op = ev.Op,
                    TargetPath = ev.TargetPath,
                    Badge = ev.Outcome,
                    Text = $"{ev.Op} {ev.TargetPath}",
                    IsDanger = ev.Outcome == OpOutcomeBadge.Blocked,
                });
                if (ev.Caps is { } caps)
                    ApplyCapSnapshot(caps);
                // QA3-9: an applied write may target an OPEN editor tab's file — refresh it from disk so a
                // later Save cannot silently clobber the AI's edit. No-op (no matching open tab) or unwired
                // (RefreshOpenDocumentAsync is null) both resolve harmlessly.
                if (ev.Outcome == OpOutcomeBadge.Applied && ev.TargetPath is { Length: > 0 } appliedPath)
                    _ = RefreshOpenDocumentAsync?.Invoke(appliedPath);
                break;

            case TurnEventKind.SafetyEvent:
                await HandleSafetyEventAsync(ev, turnCts).ConfigureAwait(true);
                break;

            case TurnEventKind.WriteToolInput:
                // Pre-cache tool_use.input JSON for FR-39 replay (MF1.4). The hook ask arrives after this
                // stream event; the cache bridges the gap so ConfirmHookDecisionAsync can park the input JSON
                // alongside the target path + op class it already receives. If RawToolInputJson is null (the
                // stream block was absent or malformed), we skip caching — the park path handles the null case
                // gracefully by leaving CapturedToolInputJson null, and replay declines.
                if (ev.ToolUseId is { Length: > 0 } toolUseId && ev.RawToolInputJson is { } rawJson)
                    _toolInputCache[toolUseId] = rawJson;
                break;
        }
    }

    private async Task HandleSafetyEventAsync(TurnEvent ev, CancellationTokenSource turnCts)
    {
        switch (ev.SafetyKind)
        {
            case SafetyEventKind.SandboxBlocked:
                // The block is per-op; the turn continues — the visible block is the proof the boundary held
                // (design §6.7 SC-5(d)). No kill, no confirm.
                AddLine(new TranscriptLine { Kind = TranscriptKind.SafetyEventChip, Text = ev.Text ?? "blocked — write outside the document folder", IsDanger = true });
                break;

            case SafetyEventKind.Degraded:
                // Mid-turn degrade (FR-10(c), design §6.7): the cap meters freeze, the agency forces
                // approve-each for the session, and the remaining ops are halted by the runner.
                AddLine(new TranscriptLine { Kind = TranscriptKind.SafetyEventChip, Text = ev.Text ?? "Backend degrade detected — agency set to approve-each.", IsDanger = true });
                Sandbox = SandboxState.Degraded;
                EffectiveMode = AgencyMode.ApproveEach;
                DowngradeBadge = "degraded backend";
                break;

            case SafetyEventKind.CapReached:
                if (ev.Caps is { } caps)
                    ApplyCapSnapshot(caps);
                await PauseForCapConfirmAsync(ev.TrippedCap, turnCts).ConfigureAwait(true);
                break;
        }
    }

    private async Task PauseForCapConfirmAsync(TrippedCap cap, CancellationTokenSource turnCts)
    {
        int capValue = cap switch
        {
            TrippedCap.Created => _capConfig.CreateCap,
            TrippedCap.Destructive => _capConfig.DestructiveCap,
            TrippedCap.Touched => _capConfig.FilesTouchedCap,
            _ => 0,
        };

        AddLine(new TranscriptLine
        {
            Kind = TranscriptKind.SafetyEventChip,
            Text = $"Per-turn cap reached ({cap}). This can be a sign of a runaway or injected instruction.",
            IsDanger = true,
        });

        bool allowNext = await _confirm.ConfirmCapReachedAsync(new CapReachedRequest(cap, capValue), turnCts.Token).ConfigureAwait(true);
        if (!allowNext)
        {
            // [Stop] — end the turn; already-applied ops stay, logged + undoable (design §6.5).
            await StopAsync().ConfigureAwait(true);
        }
        // Allow-next: one more op proceeds; the cap does NOT re-arm, so the next op of that class confirms
        // again (the runner drives the next op + emits the next CapReached event — no re-arm here, §6.5).
    }

    /// <summary>
    /// The M11 approve-each channel confirm (docs/m11-channel-protocol.md §3) — the App's narrowing answer
    /// to a hook-ALLOWED op for which the channel raised a <see cref="HookDecisionRequest"/>. It shows the
    /// EXISTING Deny-focused approve-each confirm (<see cref="IAiConfirmService.ConfirmApproveEachAsync"/>)
    /// and returns the user's allow/deny as a <see cref="HookDecisionResponse"/>. The CHANNEL already
    /// AND-narrows and fail-closes (a hook-deny never reaches here; a cancelled <paramref name="ct"/> — the
    /// no-reply deadline, a Stop, or a channel fault — resolves to Deny in the channel) — this method adds NO
    /// safety decision. On a deny / no-reply / fault the op is PARKED to the FR-39 pending surface so the user
    /// can review + apply it later (spec §5 invariant 6). Marshals onto the UI thread (the confirm is a dialog).
    /// </summary>
    /// <remarks>
    /// Invoked by the <c>HookObservationChannel</c> confirm delegate from its OFF-UI observe loop. The channel
    /// guarantees fail-closed-on-fault regardless of this method's outcome; the parking is the only App-side
    /// effect. A cancelled <paramref name="ct"/> yields a Deny answer (no allow can escape a non-answer).
    /// </remarks>
    public async Task<HookDecisionResponse> ConfirmHookDecisionAsync(HookDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool approve;
        try
        {
            // Marshal the confirm onto the UI thread (the production confirm is an in-panel dialog). The
            // existing approve-each confirm is Deny-focused — an accidental Enter never applies an AI write.
            // The Func<Task<bool>> overload of InvokeAsync unwraps the inner task; await yields the answer.
            // NFR-8 note: this method is called off the UI thread from the observe loop. Avalonia's
            // DispatcherOperation does not expose ConfigureAwait — its custom awaiter does not route
            // through SynchronizationContext (the UI dispatch is the InvokeAsync itself, not the await),
            // so the continuation always runs on the current thread (the off-UI observe thread). No
            // ConfigureAwait(false) is needed here; the Avalonia awaiter provides the equivalent guarantee.
            approve = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => _confirm.ConfirmApproveEachAsync(
                    new ApproveEachRequest(
                        OpVerb: VerbFor(request.OpClass),
                        TargetPath: request.TargetPath,
                        DiffPreview: string.Empty,
                        SandboxRoot: SandboxRoot),
                    ct));
        }
        catch (OperationCanceledException)
        {
            // No-reply deadline / Stop / channel fault → deny. The channel also fail-closes; we park here.
            approve = false;
        }
#pragma warning disable CA1031 // A confirm fault must fail closed (deny + park), never surface as an allow.
        catch
        {
            approve = false;
        }
#pragma warning restore CA1031

        if (approve)
            return HookDecisionResponse.Allowed(request.ToolUseId);

        // Deny / no-reply / fault: park the op to the FR-39 pending-proposals surface (the existing park
        // path) so the user can review + apply it later. Marshal the collection mutation onto the UI thread.
        // NFR-8 note: same off-UI discipline as the confirm marshal above — Avalonia DispatcherOperation
        // has no ConfigureAwait; the awaiter does not route through SynchronizationContext (see above).
        //
        // MF1.4: consume the tool-input cache entry (if any) that FoldStreamLine pre-populated BEFORE the
        // hook ask arrived. A cache hit populates CapturedToolInputJson so ApplyPendingAsync can rebuild the
        // FileOpRequest; a cache miss leaves it null and ApplyPendingAsync declines gracefully.
        // TryRemove (not TryGetValue): the id is single-use — the stream event fires once per tool-use.
        _toolInputCache.TryRemove(request.ToolUseId, out string? capturedInputJson);

        // OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): if this op targets a checked out-of-folder
        // document, match its target against THIS turn's armed grants and persist the single resolved grant on
        // the proposal, so the replay can read the before-image (union reader) + write (union-gated service)
        // for that one path. null for an in-root op (unchanged replay path) or a non-granted target (Refused
        // at replay). Matched here, while the turn's grants are still live (the park runs mid-turn).
        string? grantedResolvedPath =
            _grantComposer is { } composer && _currentTurnGrantPaths.Count > 0
                ? composer.TryMatchGrantForTarget(request.TargetPath, _currentTurnGrantPaths)
                : null;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ParseAndPark(new PendingProposalViewModel
        {
            ProposalId = request.ToolUseId,
            Op = OpFor(request.OpClass),
            TargetPath = request.TargetPath,
            Detail = "parked — approve-each not granted",
            CapturedToolInputJson = capturedInputJson,
            GrantedResolvedPath = grantedResolvedPath,
        }));

        return HookDecisionResponse.Deny(request.ToolUseId);
    }

    private static string VerbFor(HookOpClass opClass) => opClass switch
    {
        HookOpClass.Create => "create",
        HookOpClass.Overwrite => "overwrite",
        _ => "edit",
    };

    private static ActivityOp OpFor(HookOpClass opClass) => opClass switch
    {
        HookOpClass.Create => ActivityOp.Create,
        _ => ActivityOp.Edit,
    };

    private void ApplyCapSnapshot(CapSnapshot caps)
    {
        // One shared conversation-keyed cap snapshot drives all three meters (design §6.5). The snapshot
        // is produced by the spine's CapAccounting through the conversation-keyed store — switching backend
        // mid-conversation does NOT reset it (the runner keys it on the conversation id, not the backend).
        CreatedMeter.Count = caps.Created;
        DestructiveMeter.Count = caps.Destructive;
        TouchedMeter.Count = caps.Touched;
    }

    // ----- The persistent Stop (region 6 — NFR-8) -----

    private bool CanStop() => IsTurnInFlight;

    /// <summary>
    /// Instant-stop (design §6.4, NFR-8): kill the process tree via the runner
    /// (<see cref="IAiTurnRunner.StopAsync"/> → <see cref="IAgentTurn.KillTreeAsync"/>) and cancel the turn
    /// token. The UI reflects the stop WITHOUT waiting for the process to die (the kill is off-UI). Any ops
    /// already applied stay on disk + in the activity log/undo journal (the transcript says so).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        CancellationTokenSource? cts = _turnCts;
        if (cts is null)
            return;

        // Cancel first so the await-foreach unwinds promptly; then kill the tree off-UI.
        if (!cts.IsCancellationRequested)
            cts.Cancel();

        await _runner.StopAsync().ConfigureAwait(true);

        AddLine(new TranscriptLine { Kind = TranscriptKind.SystemNote, Text = "— stopped —" });
        AddLine(new TranscriptLine
        {
            Kind = TranscriptKind.SystemNote,
            Text = $"Stopped. {CreatedMeter.Count + DestructiveMeter.Count} file operations were applied and can be undone.",
        });
    }

    // ----- FEAT-9: AI ▸ Clear Conversation -----

    private bool CanClearConversation() => !IsTurnInFlight;

    /// <summary>
    /// FEAT-9: empty the visible transcript for a fresh conversation. This is a UI-level history clear
    /// ONLY — it does NOT touch the conversation id, the bound session (<c>_fileOps</c>/<c>_sessionRootFileOps</c>/
    /// <c>_grantComposer</c>), the sandbox/agency state, or the cap meters. The spine's cap-state is keyed to
    /// the conversation id (§4.4) and MUST NOT re-arm on a history clear — only a new user-initiated folder
    /// bind (<see cref="BindSession"/>) mints a new one. Disabled while a turn is in flight (§6.4) so a
    /// streaming assistant line is never cleared out from under an active render.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearConversation))]
    private void ClearConversation() => Transcript.Clear();

    // The no-reply deadline fired: fail closed (deny + kill). The captured op (if an approve-each confirm
    // was pending) is parked in the Pending surface by the runner/confirm path; here we set the BUG-4
    // no-reply flag so the SendAsync finally block renders a diagnostic error chip, then kill + note it.
    // This is async void (timer callback) — exceptions must not escape; we catch + swallow after ensuring
    // the turn is treated as killed (the cancel already happened; a StopAsync fault cannot be allowed to
    // leave the process tree alive or the turn state ambiguous).
    private async void OnNoReply(CancellationTokenSource turnCts)
    {
        if (turnCts.IsCancellationRequested)
            return;
        // BUG-4: flag the no-reply so SendAsync's finally block renders a diagnostic chip.
        _noReplyFired = true;
        // Cancel the turn (the fail-closed deny).
        turnCts.Cancel();
        try
        {
            await _runner.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // StopAsync faulted — the cancel already happened; treat the turn as killed regardless.
            // A KillTree fault (e.g. the process already exited) is not a safety regression; the
            // cancel token stops the await-foreach from the turn pump's side.
        }
    }

    // ----- BUG-4 self-test command + error mapping -----

    private bool CanTestConnection() => !IsTurnInFlight && !_isTesting;

    /// <summary>
    /// Test the backend connection with a minimal read-only prompt ("reply OK") and report success/failure
    /// with timing. Posts the result as a system-note chip in the transcript and updates the status line.
    /// Read-only: no file writes are performed. Gated so it cannot overlap with a live turn.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        _isTesting = true;
        BackendStatus = BackendConnectionState.Connecting;
        BackendStatusMessage = "Testing...";

        var request = new TurnRequest("reply OK", AgencyMode.ReadOnly, WriteAgencyArmed: false);
        long startTicks = _time.GetTimestamp();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await foreach (TurnEvent ev in _runner.RunAsync(request, cts.Token).ConfigureAwait(true))
            {
                if (ev.Kind == TurnEventKind.AssistantText && !string.IsNullOrEmpty(ev.Text))
                    break;
            }

            // QA2-17: a NoSessionTurnRunner completes without exception too (it yields one canned
            // "no folder open" note and returns) — the same case SendAsync's finally block already
            // treats as a failure. Without this check the Test button reports false-positive success
            // while no real backend was ever contacted. Mirror SendAsync's honest outcome here.
            if (_runner is NoSessionTurnRunner)
            {
                BackendStatus = BackendConnectionState.Error;
                BackendStatusMessage = "No folder open — open a folder to enable AI";
                AddLine(new TranscriptLine
                {
                    Kind = TranscriptKind.SafetyEventChip,
                    Text = "Connection test skipped — no folder open",
                    IsDanger = true,
                });
                return;
            }

            // Turn completed without exception and a real backend was contacted — the runner responded.
            long elapsedMs = _time.GetElapsedTime(startTicks).Milliseconds;
            BackendStatus = BackendConnectionState.Connected;
            BackendStatusMessage = $"Ready ({elapsedMs}ms)";
            AddLine(new TranscriptLine
            {
                Kind = TranscriptKind.SystemNote,
                Text = $"✓ Connection OK ({elapsedMs}ms)",
            });
        }
        catch (OperationCanceledException)
        {
            BackendStatus = BackendConnectionState.Error;
            BackendStatusMessage = "Error: timed out";
            AddLine(new TranscriptLine
            {
                Kind = TranscriptKind.SafetyEventChip,
                Text = "Connection test timed out (30s) — check connectivity",
                IsDanger = true,
            });
        }
        catch (Exception ex)
        {
            string errorText = MapTurnException(ex);
            BackendStatus = BackendConnectionState.Error;
            BackendStatusMessage = $"Error: {errorText}";
            AddLine(new TranscriptLine
            {
                Kind = TranscriptKind.SafetyEventChip,
                Text = errorText,
                IsDanger = true,
            });
        }
        finally
        {
            _isTesting = false;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Map a turn-runner exception to a user-friendly one-line error message for the diagnostic status
    /// line and error chip. The messages are concrete so the user knows what to fix (install claude,
    /// open a folder, check connectivity, etc.).
    /// </summary>
    private static string MapTurnException(Exception ex)
    {
        return ex switch
        {
            InvalidOperationException ioEx
                when ioEx.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                  || ioEx.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                  || ioEx.Message.Contains("cannot start", StringComparison.OrdinalIgnoreCase) =>
                "Claude CLI not found — install claude or switch to Anthropic API",
            System.IO.FileNotFoundException => "Claude CLI not found — install claude or switch to Anthropic API",
            UnauthorizedAccessException => "Backend permission denied — check Claude CLI permissions",
            TimeoutException => "Backend timed out — check connectivity",
            _ => $"Backend error: {ex.Message}",
        };
    }

    // ----- The FR-39 Pending tab (region 5 — NO Apply-All) -----

    /// <summary>
    /// Swap in the production inline-confirm service once the panel view's overlay host exists (the
    /// production <see cref="AiConfirmService"/> needs the view's <c>ConfirmOverlay</c> Panel, which does
    /// not exist at composition time). The view calls this on attach; tests inject the confirm service via
    /// the ctor and never call this. The confirms are shown in-panel (FR-22), never a separate OS window.
    /// </summary>
    public void SetConfirmService(IAiConfirmService confirmService)
    {
        ArgumentNullException.ThrowIfNull(confirmService);
        _confirm = confirmService;
    }

    /// <summary>
    /// Swap in the session-scoped turn runner when M6.3 opens a folder (replacing the no-folder
    /// <see cref="NoSessionTurnRunner"/>). Carried forward — M6.6 builds the seam; M6.3 connects the real
    /// session runner + sandbox root.
    /// </summary>
    public void SetTurnRunner(IAiTurnRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>Park a fail-closed proposal in the Pending surface (design §6.6.4, FR-39).</summary>
    public void ParseAndPark(PendingProposalViewModel proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Pending.Add(proposal);
        OnPropertyChanged(nameof(PendingCount));
    }

    // ApplyPendingAsync requires: no live turn (racing a hook-gated write is unsafe), a session-scoped
    // file-ops service, AND the MF1.1 before-image seam wired (without it, FileOpRequest cannot be built).
    private bool CanApplyPending() => !IsTurnInFlight && _fileOps is not null && _beforeImageReader is not null;

    /// <summary>
    /// Apply ONE parked proposal via FR-39 replay (design §6.6.4, MF1.4). It runs through the SAME shared
    /// file-ops service as an Archetype-B op (<see cref="AiFileOpsService.ReplayAsync"/>) — governed by
    /// the per-op confirm, NOT counted against the per-turn caps (architecture §4.6). The before-image is
    /// read at replay time via the MF1.1 <see cref="IBeforeImageReader"/> seam; the sandbox containment
    /// check inside that seam enforces FR-26 (an out-of-root parked path renders a Blocked chip with no
    /// mutation). There is deliberately NO Apply-All (llm F5). Disabled while a turn is in flight.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyPending))]
    private async Task ApplyPendingAsync(PendingProposalViewModel? proposal)
    {
        if (proposal is null || _fileOps is null || _conversationId is null || _beforeImageReader is null)
            return;

        // Without CapturedToolInputJson the proposed content bytes cannot be recovered (the hook channel
        // deliberately omits them). Decline silently — the user can discard the stale proposal instead.
        if (string.IsNullOrEmpty(proposal.CapturedToolInputJson))
            return;

        // Parse the raw tool_input JSON → target path + proposed content bytes.
        // Write tool: { "file_path": "...", "content": "..." }
        // Edit tool:  { "file_path": "...", "new_string": "..." }
        // A parse failure (unexpected shape) silently declines — never throw from a user command.
        if (!TryParseToolInput(proposal.CapturedToolInputJson, out string? rawTargetPath, out byte[]? proposedBytes)
            || string.IsNullOrEmpty(rawTargetPath) || proposedBytes is null)
            return;

        // OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): select the replay write path + before-image
        // reader from the proposal's persisted grant.
        //   - GrantedResolvedPath present  → out-of-folder op authorized by a per-file grant. Read the
        //     before-image through the UNION reader with the single-element grant set {GrantedResolvedPath},
        //     and compose a per-op union-gated AiFileOpsService so ReplayAsync actually writes the out-of-
        //     folder file. The union reader self-enforces containment (a tampered target that does not equal
        //     the grant Refuses); the empty-grant case is impossible here (guarded non-empty).
        //   - No grant (in-root op)        → the EXISTING frozen root-only reader + the session root-gated
        //     _fileOps — path byte-unchanged.
        // Fail-closed: a granted op with the union reader wired unavailable falls back to the root-only reader
        // ⇒ Refused ⇒ Blocked (never a widened read). ReplayAsync semantics are unchanged either way
        // (not-counted / no-re-arm / undoable).
        bool hasGrant = !string.IsNullOrWhiteSpace(proposal.GrantedResolvedPath);
        AiFileOpsService replayFileOps =
            hasGrant && _grantComposer is { } grantComposer
                ? grantComposer.ComposeReplayFileOps(proposal.GrantedResolvedPath!)
                : _fileOps;

        // Step 1: read the before-image (routes through IPathResolver + SandboxPolicy with the SAME
        // normalization as the live write path — FR-26 by construction). Must precede any confirm (the
        // Refused verdict renders a Blocked chip with NO user prompt).
        CancellationToken ct = CancellationToken.None; // user-reviewed; no deadline appropriate
        BeforeImageResult beforeImage =
            hasGrant && _unionBeforeImageReader is { } unionReader
                ? await unionReader
                    .ReadAsync(
                        rawTargetPath,
                        SandboxRoot,
                        new HashSet<string>(StringComparer.Ordinal) { proposal.GrantedResolvedPath! },
                        ct)
                    .ConfigureAwait(true)
                : await _beforeImageReader
                    .ReadAsync(rawTargetPath, SandboxRoot, ct).ConfigureAwait(true);

        if (beforeImage is BeforeImageResult.Refused refused)
        {
            // Out-of-root path — the sandbox blocked the replay, just as the live path would (FR-26).
            // Remove the proposal and render a Blocked chip so the user sees the boundary held.
            Pending.Remove(proposal);
            OnPropertyChanged(nameof(PendingCount));
            AddLine(new TranscriptLine
            {
                Kind = TranscriptKind.FileOpChip,
                Op = proposal.Op,
                TargetPath = rawTargetPath,
                Badge = OpOutcomeBadge.Blocked,
                Text = $"blocked — replay refused (outside document folder): {refused.ResolvedPath}",
                IsDanger = true,
            });
            return;
        }

        // Step 2: build the before-image fields from the seam result. Determine op (Create vs Edit) from
        // whether the target currently exists on disk (NotFound → create; Found → edit). Stage the blob
        // BEFORE the mutation (M15 durable ordering) when not SnapshotSkipped.
        bool isCreate = beforeImage is BeforeImageResult.NotFound;
        bool snapshotSkipped = false;
        BeforeImage? priorContent = null;
        string? preOpContentHash = null;
        DateTimeOffset? preOpMtime = null;
        long bytesBefore = 0;

        if (beforeImage is BeforeImageResult.Found found)
        {
            BeforeImageCapture capture = found.Capture;
            preOpContentHash = capture.ContentHash;
            preOpMtime = capture.Mtime;
            bytesBefore = capture.ByteCount;
            snapshotSkipped = capture.SnapshotSkipped;

            if (!snapshotSkipped && _replayStore is not null && _replayPaths is not null)
            {
                // Blob-staged path (M15 durable ordering): mint ONE undoRef shared by BOTH the blob
                // filename AND the FileOpRequest.UndoRef — the undo machinery locates the blob by
                // UndoJournalRoot/before/<undoRef>.bin, so they MUST match. Stage the blob first
                // (CreateWhenAbsent), then dispatch — the mutation cannot precede the journal record.
                string undoRefForBlob = Guid.NewGuid().ToString("N");
                string blobPath = Path.Combine(_replayPaths.UndoJournalRoot, "before", undoRefForBlob + ".bin");
                await _replayStore.CreateWhenAbsentAsync(blobPath, capture.Content, ct).ConfigureAwait(true);
                priorContent = new BeforeImage("blob", blobPath);

                // Pass the same undoRef so DispatchReplayAsync uses it in FileOpRequest.UndoRef.
                await DispatchReplayAsync(proposal, rawTargetPath, proposedBytes,
                    isCreate, snapshotSkipped, priorContent, preOpContentHash, preOpMtime,
                    bytesBefore, undoRefForBlob, replayFileOps, ct).ConfigureAwait(true);
                return;
            }
        }

        // Create case OR over-cap (SnapshotSkipped) — no blob to stage; mint a fresh undoRef here.
        // This undoRef is independent of any blob path (there is no before-image blob for creates or
        // snapshot-skipped ops), so a separate mint is correct and causes no confusion with the staged path.
        await DispatchReplayAsync(proposal, rawTargetPath, proposedBytes,
            isCreate, snapshotSkipped, priorContent, preOpContentHash, preOpMtime,
            bytesBefore, undoRef: Guid.NewGuid().ToString("N"), replayFileOps, ct).ConfigureAwait(true);
    }

    // Show the per-op confirm, then call ReplayAsync and render the chip. Extracted to avoid duplicating
    // the confirm + chip logic across the blob-staged and non-staged branches above.
    private async Task DispatchReplayAsync(
        PendingProposalViewModel proposal,
        string rawTargetPath,
        byte[] proposedBytes,
        bool isCreate,
        bool snapshotSkipped,
        BeforeImage? priorContent,
        string? preOpContentHash,
        DateTimeOffset? preOpMtime,
        long bytesBefore,
        string undoRef,
        AiFileOpsService replayFileOps,
        CancellationToken ct)
    {
        // Show the per-op confirm (FR-10(b)). The IRREVERSIBLE variant is shown when SnapshotSkipped so
        // the user understands undo will be unavailable (the before-image was over cap). Default focus = Deny
        // (web F4 / llm F4 — an accidental Enter must not approve a replay write).
        string verb = isCreate ? "create" : "overwrite";
        bool approve = await _confirm.ConfirmApproveEachAsync(
            new ApproveEachRequest(verb, rawTargetPath, DiffPreview: string.Empty, SandboxRoot, IsIrreversible: snapshotSkipped),
            ct).ConfigureAwait(true);

        // Terminal outcome either way: remove the proposal from Pending once the user has decided.
        Pending.Remove(proposal);
        OnPropertyChanged(nameof(PendingCount));

        if (!approve)
            return; // User declined — the proposal is removed; no mutation.

        // Build the FileOpRequest and replay through the SAME shared service (FR-39 §4.6).
        // Classification is null for a replay (not per-turn-cap-counted; FR-10(b) governs this op).
        // DurationMs is 0 — the replay is App-timed; a future pass could carry the elapsed ms if needed.
        var operation = new FileOperation(
            RequestId: $"replay-{undoRef}",
            Op: isCreate ? FileOp.Create : FileOp.Edit,
            Paths: new OpPaths(rawTargetPath));

        var request = new FileOpRequest(
            Operation: operation,
            Content: proposedBytes,
            Classification: null,
            BufferJournalId: Guid.NewGuid().ToString("N"),
            UndoRef: undoRef,
            PriorContent: priorContent,
            PreOpContentHash: preOpContentHash,
            PreOpMtime: preOpMtime,
            BytesBefore: bytesBefore,
            BytesAfter: proposedBytes.Length,
            SnapshotSkipped: snapshotSkipped);

        var context = new FileOpContext(
            Ts: _time.GetUtcNow(), // honor the TimeProvider seam (test-injectable; audit trail consistent)
            Backend: SelectedBackend,
            ConfiguredMode: ConfiguredMode,
            AgencyMode: EffectiveMode,
            DowngradeReason: DowngradeReason.None,
            CountsAgainstPerTurnCaps: false); // replay: governed by FR-10(b), NOT per-turn caps (§4.6)

        // OQ-MD-5 option ii: replay through the SELECTED service — the per-op union-gated service for a granted
        // out-of-folder op, or the session root-gated _fileOps for an in-root op. ReplayAsync semantics are
        // identical (not-counted / no-re-arm / undoable); only the sandbox gate widens to admit the one grant.
        FileOpResult result = await replayFileOps.ReplayAsync(_conversationId!, request, context, ct)
            .ConfigureAwait(true);

        // Render the chip from the actual result outcome (Applied / Blocked / …).
        ActivityOp renderedOp = isCreate ? ActivityOp.Create : ActivityOp.Edit;
        OpOutcomeBadge badge = result.Outcome == ActivityOutcome.Applied
            ? OpOutcomeBadge.Approved
            : OpOutcomeBadge.Blocked;
        AddLine(new TranscriptLine
        {
            Kind = TranscriptKind.FileOpChip,
            Op = renderedOp,
            TargetPath = rawTargetPath,
            Badge = badge,
            Text = $"{renderedOp} {rawTargetPath}",
            IsDanger = badge == OpOutcomeBadge.Blocked,
        });

        // QA3-9: a replay-applied write may target an OPEN editor tab's file — refresh it from disk (same
        // reasoning as the live-turn FileOp fold in HandleTurnEventAsync).
        if (badge == OpOutcomeBadge.Approved)
            _ = RefreshOpenDocumentAsync?.Invoke(rawTargetPath);
    }

    // Pure parser: tool_use.input JSON → rawTargetPath + proposed bytes.
    // Mirrors the Python deny_by_default.py helpers _target_path / _proposed_content (read-only — no side effects).
    // Write tool: { "file_path": "...", "content": "..." }  → bytes = UTF-8 of content
    // Edit tool:  { "file_path": "...", "new_string": "..." } → bytes = UTF-8 of new_string
    // A missing / malformed field returns false (the call site declines silently).
    private static bool TryParseToolInput(
        string rawInputJson,
        out string? rawTargetPath,
        out byte[]? proposedBytes)
    {
        rawTargetPath = null;
        proposedBytes = null;

        if (string.IsNullOrWhiteSpace(rawInputJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawInputJson);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("file_path", out JsonElement filePath) ||
                filePath.ValueKind != JsonValueKind.String)
                return false;

            rawTargetPath = filePath.GetString();
            if (string.IsNullOrEmpty(rawTargetPath))
                return false;

            // Write tool carries "content"; Edit tool carries "new_string".
            string? proposedText = null;
            if (root.TryGetProperty("content", out JsonElement content) &&
                content.ValueKind == JsonValueKind.String)
            {
                proposedText = content.GetString();
            }
            else if (root.TryGetProperty("new_string", out JsonElement newString) &&
                     newString.ValueKind == JsonValueKind.String)
            {
                proposedText = newString.GetString();
            }

            if (proposedText is null)
                return false;

            proposedBytes = Encoding.UTF8.GetBytes(proposedText);
            return true;
        }
        catch (JsonException)
        {
            return false; // malformed JSON — decline silently.
        }
    }

    /// <summary>Discard one parked proposal (design §6.6.4) — removes it with a transcript note.</summary>
    [RelayCommand]
    private void DiscardPending(PendingProposalViewModel? proposal)
    {
        if (proposal is null)
            return;
        Pending.Remove(proposal);
        OnPropertyChanged(nameof(PendingCount));
    }

    // ----- Backend + agency selectors (header) -----

    // async void: the CommunityToolkit partial hook cannot be async Task; exceptions must not escape.
    // Fail-closed: if ArmAgencyAsync throws (not cancellation — those re-throw cleanly), we force
    // the posture to read-only + badge so a stale possibly-armed state cannot persist silently.
    partial void OnConfiguredModeChanged(AgencyMode value) => _ = ArmAgencyFailClosedAsync();

    private async Task ArmAgencyFailClosedAsync()
    {
        // Changing the agency mode (re)runs the self-test gate so write agency is never armed without a pass.
        // Mid-turn the change takes effect at the NEXT turn (design §6.4 turn-in-flight rule) — the view
        // disables the selector while a turn is in flight, so this only fires between turns.
        try
        {
            await ArmAgencyAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled (e.g. the view is being torn down) — not a safety regression.
        }
        catch
        {
            // An unexpected fault in ArmAgencyAsync. Fail closed: force read-only + downgrade badge so
            // we never leave a stale Armed state from a previous successful arm.
            EffectiveMode = AgencyMode.ReadOnly;
            DowngradeBadge = "agency arm error";
            Sandbox = SandboxState.SelfTestFailed;
        }
    }

    partial void OnSelectedBackendChanged(AiBackend value)
    {
        // Backend switch preserves the conversation-keyed cap-state (design §6.3 / FR-40 cap-state-lifetime):
        // the cap meters are NOT touched here. Only the §9.3 backend label changes; the next turn routes
        // through the selected backend (the runner keys caps on the conversation id, not the backend).
    }

    // ----- helpers -----

    /// <summary>
    /// DS-6 (docs/decisions.md DR-DS-3): renders the fixed "clean + badge + log" behavior for a scrub
    /// that actually redacted at least one span — a non-blocking <see cref="TranscriptKind.SafetyEventChip"/>
    /// (rendered in the neutral on-surface tone, <see cref="TranscriptLine.IsDanger"/> left <c>false</c> —
    /// this is informational, never a block) plus a <see cref="ScrubFindingsLogWriter"/> append. A no-op
    /// (no chip, no log write) when <paramref name="spanCount"/> is zero, so a clean turn never gets a
    /// chip — the caller gates this on the underlying scrub's own <c>WasModified</c>/block-count signal
    /// before calling in. <paramref name="findings"/> may be empty (DS-3's <c>BlockScanner.RedactBlock</c>
    /// reports only a block count, not per-span category/rule-id/confidence — only DS-5's
    /// <c>Engine.ScrubInput</c> findings carry that detail); the chip and log entry both degrade
    /// gracefully to a bare count in that case. Never touches disk beyond the log append — the scrubbed
    /// text itself was already sent onward by the caller before this runs (DR-DS-3: "send CleanedText").
    /// </summary>
    private async Task ReportScrubFindingsAsync(string source, int spanCount, IReadOnlyList<Finding> findings)
    {
        if (spanCount <= 0)
            return;

        string plural = spanCount == 1 ? string.Empty : "s";
        string detail = findings.Count > 0
            ? " — " + string.Join("; ", findings.Select(f => $"{f.Category} ({f.RuleId}, {f.Confidence})"))
            : string.Empty;

        AddLine(new TranscriptLine
        {
            Kind = TranscriptKind.SafetyEventChip,
            Text = $"⚠ {spanCount} suspicious span{plural} neutralized in {source}{detail}",
        });

        // Reuse the same IAtomicFileStore/IAppDataPaths handles the FR-39 replay path already wires
        // (_replayStore/_replayPaths) — null in the unwired test-only construction paths, matching the
        // fail-safe posture the replay blob-staging code above already applies. The chip above is the
        // guaranteed visible copy either way (Drift/Assumptions).
        if (_replayStore is not null && _replayPaths is not null)
        {
            await ScrubFindingsLogWriter.AppendAsync(
                _replayStore, _replayPaths, _time.GetUtcNow(), source, spanCount, findings, CancellationToken.None)
                .ConfigureAwait(true);
        }
    }

    private void AddLine(TranscriptLine line) => Transcript.Add(line);

    private static string Label(AgencyMode mode) => mode switch
    {
        AgencyMode.AutoApply => "auto-apply",
        AgencyMode.ApproveEach => "approve-each",
        AgencyMode.ReadOnly => "read-only",
        _ => "read-only",
    };

    /// <summary>
    /// STF-3: absolute path to the bundled <c>HoardPad.SelfTestStandIn</c> executable, resolved next
    /// to the running app (mirrors <c>CompositionRoot.BundledHookScriptPath</c> — correct for both a
    /// published self-contained app and a <c>dotnet run</c> dev launch). The App's
    /// <c>ProjectReference</c> to <c>HoardPad.SelfTestStandIn.csproj</c> bundles its per-RID apphost
    /// into this exact output tree at publish time.
    /// </summary>
    private static string ResolveDefaultSelfTestStandInPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "HoardPad.SelfTestStandIn.exe" : "HoardPad.SelfTestStandIn";
        return Path.Combine(AppContext.BaseDirectory, exeName);
    }
}

/// <summary>
/// PANEL-4 (feature-ai-multidoc-panel FR-MD-8): one step in the strict pre-turn ordering. A
/// <see cref="Flush"/> event is recorded when a checked dirty doc is saved to disk; a
/// <see cref="BeforeImage"/> event when its before-image is captured from the flushed content. AC-MD-3
/// asserts the flush precedes the before-image for each doc, and both precede the first backend call.
/// </summary>
internal enum PreTurnStep
{
    /// <summary>The checked dirty document was flushed (auto-saved) to its on-disk path.</summary>
    Flush,

    /// <summary>The document's before-image was captured from the flushed on-disk content.</summary>
    BeforeImage,
}

/// <summary>PANEL-4 (FR-MD-8, AC-MD-3): one ordered pre-turn event (a step + the target path it applied to).</summary>
internal readonly record struct PreTurnEvent(PreTurnStep Step, string Path);
