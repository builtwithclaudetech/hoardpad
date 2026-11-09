// CompositionRoot.cs — the M6.1 DI composition root (architecture §8.1).
//
// THE single place the HoardPad.Host platform implementations meet the HoardPad.Core
// abstractions. It wires every concrete Host seam to its Core abstraction (the "seam map")
// plus the interim IEncodingDetector and the shell view-model. Session-scoped services
// (the per-open-folder AiFileOpsService + PathResolverSandboxGate, which need an open-folder
// root / session id / cap config that do not exist until a folder is opened) are NOT
// registered here — they are constructed per session in later milestones (M6.3/M6.6). M6.1's
// done-criterion is only that every SEAM resolves to its Host concrete.

using Avalonia.Threading;
using HoardPad.App.Ai;
using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.Settings;
using HoardPad.App.Theme;
using HoardPad.App.ViewModels;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Microsoft.Extensions.DependencyInjection;
using TextMateSharp.Grammars;

namespace HoardPad.App.Hosting;

/// <summary>
/// Builds the application's <see cref="IServiceProvider"/> (architecture §8.1). Registers the
/// six <see cref="HoardPad.Host"/> concrete seams against their <see cref="HoardPad.Core"/>
/// abstractions, the FR-6 UTF.Unknown-backed <see cref="IEncodingDetector"/> (M8.3), and the shell
/// view-model. Tests substitute fakes for the seams via the same <see cref="ConfigureServices"/> surface.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>Build a fully-wired service provider for the real (Host-backed) application run.</summary>
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Surface a missing/mis-shaped registration at build time rather than first resolve.
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Register the seam map + view-models into <paramref name="services"/>. Factored out so the
    /// headless composition-root test asserts against the exact production registration set.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register the CodePagesEncodingProvider ONCE so Encoding.GetEncoding("windows-1252")
        // resolves to real CP-1252 (not Latin-1). Safe to call multiple times — the BCL
        // deduplicates; we call it here (composition root) so it fires before any TextEncodings.Resolve
        // call, whether that is from the DI-resolved EditorFileService or a test that calls
        // CompositionRoot.ConfigureServices. Static, but idempotent.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // --- The seam map (architecture §8.1): Host concrete -> Core abstraction. ---
        // Stateless / app-data-resolvable seams: singletons.
        services.AddSingleton<IAtomicFileStore, AtomicFileStore>();
        services.AddSingleton<IPathResolver, PathResolver>();
        services.AddSingleton<IAppDataPaths, AppDataPaths>();
        services.AddSingleton<IAgentBackend, AgentBackend>();

        // TimeProvider is the seam (TD §2.5 — the BCL type IS the abstraction); the real run
        // injects the SystemTimeProvider wall clock. Every domain timer consumes THIS (§8.1).
        services.AddSingleton<TimeProvider, SystemTimeProvider>();

        // FR-6 IEncodingDetector (M8.3): the UTF.Unknown-backed byte-sniff/BOM detector in
        // HoardPad.Host. Supersedes the interim M6.1 UtfDefaultEncodingDetector; UTF.Unknown lives
        // behind this seam in Host and NEVER enters Core (§2.1 invariant 1 / OQ-ARCH-4).
        services.AddSingleton<IEncodingDetector, EncodingDetector>();

        // The deny-by-default PreToolUse hook host. Bound to the bundled hook resolved next to
        // the published app (architecture §7.4) — the .csproj copies hooks/deny_by_default.py
        // into the output tree.
        services.AddSingleton<IToolPermissionHookHost>(_ =>
            new ToolPermissionHookHost(BundledHookScriptPath()));

        // The O_EXCL-locked, conversation-keyed cap-state store (M5.2). Its base directory is the
        // §5.5 capstate subtree under the per-user app-data root; it writes through the same
        // atomic store as every other durable write.
        services.AddSingleton<ICapStateStore>(sp =>
        {
            var paths = sp.GetRequiredService<IAppDataPaths>();
            var store = sp.GetRequiredService<IAtomicFileStore>();
            return new CapStateStore(Path.Combine(paths.AppDataRoot, "capstate"), store);
        });

        // MF1.1 — the FR-39 replay-time before-image reader (the new host seam). Stateless singleton:
        // it holds only the IPathResolver; the open-folder root is supplied per-call so a single instance
        // serves all sessions. Must be registered AFTER IPathResolver (constructor dependency).
        services.AddSingleton<IBeforeImageReader, BeforeImageReader>();

        // OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5) — the ADDITIVE union-aware before-image reader for
        // replaying a parked out-of-folder op authorized by a per-file grant. Same stateless singleton posture
        // as BeforeImageReader (holds only IPathResolver; the root + grants are per-call). The frozen
        // IBeforeImageReader/BeforeImageReader are UNMODIFIED; this is a new seam composed at the replay call
        // site. Without it, a granted out-of-folder replay falls back to root-only ⇒ Refused (silently inert).
        services.AddSingleton<IUnionBeforeImageReader, UnionBeforeImageReader>();

        // PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A) — the host grants-file writer. Serializes the
        // per-turn AI file-grants to an app-controlled JSON file the deny_by_default.py hook reads via
        // HOARDPAD_FILE_GRANTS, so Archetype A (claude -p) admits the same out-of-folder paths the
        // Archetype-B UnionSandboxGate does. Consumes the same IAppDataPaths root + IAtomicFileStore the
        // cap-state file uses. Registered here so AiSessionBinder can inject it into the runner.
        services.AddSingleton<IFileGrantsWriter>(sp =>
            new FileGrantsWriter(
                sp.GetRequiredService<IAppDataPaths>(),
                sp.GetRequiredService<IAtomicFileStore>()));

        // --- Settings (M6.8, FR-35/FR-36, architecture §8.2). The atomic settings.json store (over the
        // IAtomicFileStore seam + the IAppDataPaths settings path), and the settings snapshot loaded ONCE
        // at composition — the seed for the AI panel (caps/no-reply/agency/backend) + editor default
        // language below. A missing/corrupt file degrades to HoardPadSettings.Default (FR-36). The live
        // settings TAB (SettingsViewModel) is constructed per-open via SettingsTabFactory, not a singleton. ---
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());
        services.AddSingleton<SettingsTabFactory>();

        // --- Editor surface (M6.2, design §3). The FR-7 registry overlay (One Dark base, brand.md §2)
        // is a singleton so grammar resolution + lazy loading share one instance; the file service
        // consumes the IAtomicFileStore/IPathResolver/IEncodingDetector seams above. ---
        services.AddSingleton(_ => new HoardPadRegistryOptions(ThemeName.OneDark));
        services.AddSingleton<LanguageCatalog>();
        services.AddSingleton<EditorFileService>();

        // --- Theming (M7.1, FR-37, architecture §3.7). The lockstep theme service: it builds + caches the
        // two brand-retinted palettes (One Dark/One Light + brand.md §2 retints) once, and Switch performs
        // the one synchronous no-yield re-theme across the editor, the preview, and the chrome. Editor
        // surfaces (created by XAML) self-register against ThemeService.Current, set in App at startup. ---
        services.AddSingleton<ThemeService>();

        // --- Checklist surface (M6.5, design §5). The .hoard load/atomic-save service is a singleton over
        // the IAtomicFileStore seam; the per-document ChecklistViewModel + its serialized write queue are
        // SESSION-scoped (constructed per open .hoard via ChecklistViewModel.OpenAsync/ForDocument with the
        // injected TimeProvider), not DI singletons — consistent with the M6.1 per-session note. ---
        services.AddSingleton<ChecklistFileService>();

        // --- AI panel (M6.6, design §6 / architecture §4). The FR-22 in-window panel VM. Constructed in
        // the NO-FOLDER default state (design §6.8): the NoSessionTurnRunner streams a read-only note and
        // performs no file ops, and the fail-closed confirm service is the safe default until the panel
        // view binds the production AiConfirmService over its overlay host on attach. The session-scoped
        // AiTurnRunner + sandbox root + cap-state conversation id connect in M6.3 (SetTurnRunner / BindSession).
        // M6.8: the cap config + no-reply timeout are SEEDED from the loaded settings (clamp-valid by
        // construction — settings load via HoardPadSettings.Default/SettingsStore, the caps are CapClamp-safe),
        // and the configured agency + backend are applied from settings. The FR-35 caps still flow through
        // the SAME conversation-keyed spine; settings only choose which (clamp-valid) values are in force. ---
        services.AddSingleton<AiPanelViewModel>(sp =>
        {
            HoardPadSettings settings = sp.GetRequiredService<HoardPadSettings>();
            var panel = new AiPanelViewModel(
                runner: new NoSessionTurnRunner(),
                confirm: new FailClosedConfirmService(),
                hookHost: sp.GetRequiredService<IToolPermissionHookHost>(),
                backend: sp.GetRequiredService<IAgentBackend>(),
                time: sp.GetRequiredService<TimeProvider>(),
                capConfig: settings.Caps,
                noReplyTimeout: TimeSpan.FromSeconds(settings.NoReplySeconds),
                // MF1.4 (FR-39 replay): wire the three replay seams so ApplyPendingAsync is live in
                // production. All three are app-scoped stateless singletons: IBeforeImageReader resolves
                // the open-folder root per-call (not per-construct), so one instance serves all sessions;
                // IAtomicFileStore and IAppDataPaths are already singletons. Without these, CanApplyPending
                // returns false permanently (gates on _beforeImageReader != null) and the Apply button is
                // always disabled — a permanently dead feature despite the tests passing. Registered above:
                //   IBeforeImageReader line 96, IAtomicFileStore line 63, IAppDataPaths line 65.
                beforeImageReader: sp.GetRequiredService<IBeforeImageReader>(),
                replayStore: sp.GetRequiredService<IAtomicFileStore>(),
                replayPaths: sp.GetRequiredService<IAppDataPaths>(),
                // OQ-MD-5 option ii: the union-aware before-image reader for granted out-of-folder replay.
                // Without it, UnionReplayReaderWired is false and a granted replay is silently Refused.
                unionBeforeImageReader: sp.GetRequiredService<IUnionBeforeImageReader>())
            {
                ConfiguredMode = settings.AgencyMode,
                SelectedBackend = settings.ActiveBackend,
                CurrentModelId = settings.ModelId ?? string.Empty,
                CurrentEffortLevel = settings.EffortLevel ?? string.Empty,
                // FS-5 / FR-10(a) (docs/decisions.md DR-1): seed the toggle + threshold from settings at
                // construction, same as every other FR-35 field above.
                ConfirmLargeHomeFolder = settings.ConfirmLargeHomeFolder,
                LargeRootThreshold = settings.LargeRootThreshold,
            };

            // QA4-21 (revises QA4-2): Model/Effort are now a SESSION-LOCAL panel override — seeded above at
            // construction, never persisted back to SettingsStore, and never re-synced from a later
            // Settings-tab save while this session is running (maintainer decision: "keep the panel override").
            // The panel's own "↺ reset to app default" control (ResetModelEffortToAppDefaultCommand) is the
            // only way to re-seed them on demand, via CurrentSettingsProvider.

            // FS-5: live settings push — when the user saves the confirm-large-home-folder toggle/threshold
            // in the settings tab, propagate them to the already-constructed panel so the next arm uses the
            // updated values. SaveAsync fires SettingsChanged off the UI thread (the awaiting task
            // continuation), so we post the property updates to the UI thread via Dispatcher.UIThread.Post.
            sp.GetRequiredService<SettingsStore>().SettingsChanged += s =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // FS-5: live-apply the toggle + threshold — takes effect on the NEXT arm (DR-1).
                    panel.ConfirmLargeHomeFolder = s.ConfirmLargeHomeFolder;
                    panel.LargeRootThreshold = s.LargeRootThreshold;
                });
            };

            return panel;
        });

        // --- Session / recovery (M6.3, PRD §9.2 / FR-19/FR-20/FR-21). The durable store + the pure-Core-
        // driven reconciler + the App-facing service; the per-tab surface factories; the AI-session binder;
        // and the production prompt adapters (the restore-conflict modal + the MA5 re-pick picker). ---
        services.AddSingleton<IRecoveryFileSystem, RecoveryFileSystem>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<SessionReconciler>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<EditorViewModelFactory>();
        services.AddSingleton<ChecklistViewModelFactory>();
        services.AddSingleton<IRestoreConflictPrompt, RestoreConflictPrompt>();
        services.AddSingleton<IOpenFolderPrompt, StorageOpenFolderPrompt>();
        // The M11 per-turn hook-observation channel (docs/m11-channel-protocol.md). The OS IPC primitive
        // HoardPad owns and the deny_by_default.py hook connects to. Composed with the App's approve-each
        // confirm delegate — the panel's Deny-focused ConfirmHookDecisionAsync (it shows the in-panel confirm
        // + parks a denied/no-reply op to FR-39). The channel itself fail-closes (a hook-deny never reaches
        // the confirm; a cancelled deadline / fault resolves to Deny); the App adds NO safety decision here.
        services.AddSingleton(sp =>
        {
            var panel = sp.GetRequiredService<AiPanelViewModel>();
            return new HookObservationChannel(
                confirm: (request, ct) => panel.ConfirmHookDecisionAsync(request, ct));
        });

        // Register AiSessionBinder as both its concrete type (for direct resolution) and IAiSessionBinder
        // (so WorkspaceService receives the interface — the seam that makes integration tests injectable).
        services.AddSingleton(sp => new AiSessionBinder(
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IAtomicFileStore>(),
            sp.GetRequiredService<ICapStateStore>(),
            sp.GetRequiredService<IAppDataPaths>(),
            sp.GetRequiredService<IAgentBackend>(),
            sp.GetRequiredService<IToolPermissionHookHost>(),
            sp.GetRequiredService<IOpenFolderPrompt>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<HookObservationChannel>(),
            sp.GetRequiredService<IFileGrantsWriter>()));
        services.AddSingleton<IAiSessionBinder>(sp => sp.GetRequiredService<AiSessionBinder>());

        // --- View-models (CommunityToolkit.Mvvm). The shell + editor VMs. The settings VM is constructed
        // per-open via SettingsTabFactory (M6.8), not a singleton. The shell VM is built from the live
        // editor + AI-panel VMs (explicit factory so the editor+panel-taking ctor is used, not the
        // design-time parameterless). ---
        services.AddSingleton<EditorViewModel>();
        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<EditorViewModel>(),
            sp.GetRequiredService<AiPanelViewModel>()));

        // The M7.1 theme coordinator (FR-37): applies the persisted theme at startup, owns the View ▸ Toggle
        // Theme command + the status-bar segment, and persists the choice through the M6.8 SettingsStore.
        services.AddSingleton(sp => new ThemeController(
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<MainWindowViewModel>(),
            sp.GetRequiredService<HoardPadSettings>()));

        // The M6.3 workspace coordinator: drives the startup restore (carry-forward A), the restore-conflict
        // dialog (FR-19), and the AI-session binding per MA5 (carry-forward C). Built over the live shell.
        services.AddSingleton(sp => new WorkspaceService(
            sp.GetRequiredService<MainWindowViewModel>(),
            sp.GetRequiredService<AiPanelViewModel>(),
            sp.GetRequiredService<SessionService>(),
            sp.GetRequiredService<SessionReconciler>(),
            sp.GetRequiredService<IAiSessionBinder>(),
            sp.GetRequiredService<IRestoreConflictPrompt>(),
            sp.GetRequiredService<EditorViewModelFactory>(),
            sp.GetRequiredService<ChecklistViewModelFactory>(),
            sp.GetRequiredService<SettingsTabFactory>(),
            sp.GetRequiredService<IOpenFolderPrompt>(),
            MyDocumentsResolver.ResolveLive,
            sp.GetRequiredService<IPathResolver>()));

        // --- Find / Replace (M6.7, FR-17/FR-18, design §3.5). The scope seam over the live shell tab set +
        // the open-folder root + the atomic store; the production review-modal prompt; and the docked panel
        // VM. The VM is attached to the shell post-construction (App.OnFrameworkInitializationCompleted) —
        // the same post-build wiring as OpenSettingsHandler — because the workspace seam needs the shell +
        // workspace, which the shell VM itself does not depend on. The in-folder write is USER-initiated
        // (NOT the AI sandbox path), but still atomic-through-the-store + review-before-write. ---
        services.AddSingleton<IFindReplaceReviewPrompt, FindReplaceReviewPrompt>();
        services.AddSingleton<IFindReplaceWorkspace>(sp => new ShellFindReplaceWorkspace(
            sp.GetRequiredService<MainWindowViewModel>(),
            sp.GetRequiredService<WorkspaceService>(),
            sp.GetRequiredService<IAtomicFileStore>()));
        services.AddSingleton(sp => new FindReplaceViewModel(
            sp.GetRequiredService<IFindReplaceWorkspace>(),
            sp.GetRequiredService<IFindReplaceReviewPrompt>()));
    }

    /// <summary>
    /// Absolute path to the bundled <c>hooks/deny_by_default.py</c> next to the running app
    /// (architecture §7.4). Resolved against <see cref="AppContext.BaseDirectory"/> so it is
    /// correct for both a published self-contained app and an <c>dotnet run</c> dev launch.
    /// </summary>
    private static string BundledHookScriptPath() =>
        Path.Combine(AppContext.BaseDirectory, "hooks", "deny_by_default.py");
}
