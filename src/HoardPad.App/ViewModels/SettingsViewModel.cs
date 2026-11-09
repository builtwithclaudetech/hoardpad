// SettingsViewModel.cs — the M6.8 settings-as-tab view-model (FR-35/FR-36, ODQ-12, design §7).
//
// The settings surface is a DOCUMENT TAB (ODQ-12 — settings-as-tab, NOT a separate OS window),
// consistent with the editor/checklist tabs. This VM exposes the whole FR-35 surface as bound
// properties, loads/saves through SettingsStore (atomic settings.json, FR-36), and routes any cap
// change through the Core.Ai FR-35 clamp (CapClamp) — a cap set to 0/unbounded is REJECTED, the prior
// safe value is kept, and a validation message is surfaced (FR-35 Done-when). It does NOT reimplement
// the clamp; CapClamp.Validate is the sole authority.
//
// THREADING: VM methods run on the UI thread; SaveAsync/LoadAsync do the file I/O off the UI thread
// through the atomic store. Time comes from the injected TimeProvider (none needed directly here yet,
// but it is threaded so the tab is consistent with the other surfaces and ready if a timer is added).

using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoardPad.App.Settings;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Records;

namespace HoardPad.App.ViewModels;

/// <summary>
/// The FR-35 settings surface (design §7), hosted as a document tab (ODQ-12). Round-trips the whole
/// FR-35 field set through <see cref="SettingsStore"/> (atomic <c>settings.json</c>, FR-36) and routes
/// every cap change through the Core.Ai <see cref="CapClamp"/> (the FR-35 backstop rule) — an unsafe
/// cap is rejected and the prior safe value retained.
/// </summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    /// <summary>The last-known-safe caps — the value the four cap meters fall back to on a rejected edit.</summary>
    private CapConfig _caps;

    private SettingsViewModel(SettingsStore store, HoardPadSettings initial)
    {
        _store = store;
        _caps = initial.Caps;
        Apply(initial);
    }

    /// <summary>
    /// BUG-5: set <see cref="SelectedModelOption"/>/<see cref="CustomModelId"/>/<see cref="EffortLevel"/>
    /// from <paramref name="s"/> — if the saved <c>ModelId</c> matches a known preset, select that preset
    /// in the combo; otherwise select <see cref="CustomModelOption"/> and populate the custom text field.
    /// Called by <see cref="Apply"/> at open-time only (QA4-21 removed the F6 live-sync push that used to
    /// also call this on every incoming <see cref="SettingsStore.SettingsChanged"/> — the AI panel no
    /// longer writes Model/Effort back to settings, so there is nothing left to sync mid-session, and
    /// syncing live would otherwise clobber an in-progress unsaved edit on this tab whenever an unrelated
    /// setting elsewhere saved).
    /// </summary>
    private void ApplyModelEffort(HoardPadSettings s)
    {
        if (!string.IsNullOrEmpty(s.ModelId) && ModelOptions.Take(ModelOptions.Count - 1).Contains(s.ModelId))
        {
            SelectedModelOption = s.ModelId;
            CustomModelId = string.Empty;
        }
        else if (!string.IsNullOrEmpty(s.ModelId))
        {
            SelectedModelOption = CustomModelOption;
            CustomModelId = s.ModelId;
        }
        else
        {
            SelectedModelOption = string.Empty;
            CustomModelId = string.Empty;
        }
        EffortLevel = s.EffortLevel ?? string.Empty;
    }

    /// <summary>
    /// Build the settings tab VM, loading the current settings (or the documented defaults if the file is
    /// missing/corrupt, FR-36 — <see cref="SettingsStore.LoadAsync"/> never throws on a bad file).
    /// </summary>
    public static async Task<SettingsViewModel> LoadAsync(SettingsStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        HoardPadSettings loaded = await store.LoadAsync(ct).ConfigureAwait(true);
        return new SettingsViewModel(store, loaded);
    }

    /// <summary>The settings tab's display name (shown in the tab strip).</summary>
    public string DisplayName { get; } = "Settings";

    /// <summary>The FR-23 backend choices bound by the backend ComboBox.</summary>
    public static IReadOnlyList<AiBackend> BackendOptions { get; } =
        [AiBackend.ClaudeCli, AiBackend.AnthropicApi, AiBackend.Local];

    /// <summary>The FR-27 agency-mode choices bound by the agency ComboBox.</summary>
    public static IReadOnlyList<AgencyMode> AgencyOptions { get; } =
        [AgencyMode.AutoApply, AgencyMode.ApproveEach, AgencyMode.ReadOnly];

    /// <summary>The FR-37 theme choices bound by the theme ComboBox.</summary>
    public static IReadOnlyList<AppTheme> ThemeOptions { get; } = [AppTheme.Dark, AppTheme.Light];

    /// <summary>
    /// QA2-12: the FR-8 default new-document language choices bound by the language ComboBox — "Plain
    /// Text" (the FR-8 default, preselected on first use) followed by the FR-7 20-language set in the
    /// order the PRD lists them.
    /// </summary>
    public static IReadOnlyList<string> LanguageOptions { get; } =
    [
        HoardPadSettings.DefaultLanguageLabel,
        "Markdown", "HTML", "JavaScript", "CSS", "Python", "C#", "XML", "JSON", "SQL", "Rust",
        "TypeScript", "YAML", "TOML", "Shell/Bash", "PowerShell", "Dockerfile", "C", "C++", "Java", "Go",
    ];

    // ---- BUG-5: AI model + effort (FR-35 surface extension). ----

    /// <summary>BUG-5: the "custom model" sentinel — the exact <see cref="ModelOptions"/> last entry.</summary>
    private const string CustomModelOption = "Custom/Other...";

    /// <summary>BUG-5: known Claude model IDs plus a custom/other entry for the Model ComboBox.</summary>
    public static IReadOnlyList<string> ModelOptions { get; } =
        ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001", CustomModelOption];

    /// <summary>BUG-5: reasoning effort levels bound by the Effort ComboBox.</summary>
    public static IReadOnlyList<string> EffortOptions { get; } =
        ["low", "medium", "high", "xhigh"];

    // ---- FEAT-1: font family options. ----

    /// <summary>FEAT-1: editor code font family options (monospace faces). Empty = brand.md §5 default.</summary>
    public static IReadOnlyList<string> EditorFontOptions { get; } =
        ["Cascadia Mono", "Consolas", "DejaVu Sans Mono", "JetBrains Mono", "Fira Code", "monospace"];

    /// <summary>FEAT-1: UI chrome font family options. Empty = brand.md §5 default.</summary>
    public static IReadOnlyList<string> UiFontOptions { get; } =
        ["Inter", "system-ui", "Segoe UI", "Roboto", "Noto Sans", "sans-serif"];

    // ---- FR-35 surface: AI backend + per-backend config (design §7.2). ----

    /// <summary>FR-23 active AI backend.</summary>
    [ObservableProperty]
    private AiBackend _activeBackend;

    /// <summary>claude -p CLI path override; empty = resolve via the FR-24 shim order.</summary>
    [ObservableProperty]
    private string _claudeCliPath = string.Empty;

    /// <summary>Anthropic API key (masked in the view; NFR-7 — never logged).</summary>
    [ObservableProperty]
    private string _anthropicApiKey = string.Empty;

    /// <summary>Anthropic API model name; empty = backend default.</summary>
    [ObservableProperty]
    private string _anthropicModel = string.Empty;

    /// <summary>Local LLM endpoint URL.</summary>
    [ObservableProperty]
    private string _localEndpoint = string.Empty;

    /// <summary>Local LLM model name; empty = backend default.</summary>
    [ObservableProperty]
    private string _localModel = string.Empty;

    /// <summary>FR-27 AI agency mode.</summary>
    [ObservableProperty]
    private AgencyMode _agencyMode;

    /// <summary>FR-22 app-scoped AI-panel hotkey.</summary>
    [ObservableProperty]
    private string _aiPanelHotkey = HoardPadSettings.DefaultAiPanelHotkey;

    // ---- BUG-5: AI model + effort (FR-35 surface extension). ----

    /// <summary>BUG-5: the item selected in the model ComboBox (preset or "Custom/Other...").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomModel))]
    private string _selectedModelOption = string.Empty;

    /// <summary>BUG-5: custom model ID text entry, visible when <see cref="IsCustomModel"/> is true.</summary>
    [ObservableProperty]
    private string _customModelId = string.Empty;

    /// <summary>BUG-5: the selected effort level.</summary>
    [ObservableProperty]
    private string _effortLevel = string.Empty;

    // ---- FEAT-1: font settings. ----

    /// <summary>FEAT-1: editor code font family override; empty = brand.md §5 default.</summary>
    [ObservableProperty]
    private string _editorFontFamily = string.Empty;

    /// <summary>FEAT-1: UI chrome font family override; empty = brand.md §5 default.</summary>
    [ObservableProperty]
    private string _uiFontFamily = string.Empty;

    /// <summary>
    /// Raised when any font setting property changes, so the view layer can update Application.Resources
    /// and apply the change live to the editor and chrome (FEAT-1).
    /// </summary>
    public event Action? FontSettingsChanged;

    partial void OnEditorFontFamilyChanged(string value) => FontSettingsChanged?.Invoke();
    partial void OnUiFontFamilyChanged(string value) => FontSettingsChanged?.Invoke();

    /// <summary>FEAT-1: editor code font size, clamped to 8..36. Fires <see cref="FontSettingsChanged"/> on change.</summary>
    private int _editorFontSize = HoardPadSettings.DefaultEditorFontSize;

    public int EditorFontSize
    {
        get => _editorFontSize;
        set
        {
            if (SetProperty(ref _editorFontSize, Math.Clamp(value, HoardPadSettings.MinEditorFontSize, HoardPadSettings.MaxEditorFontSize)))
                FontSettingsChanged?.Invoke();
        }
    }

    /// <summary>FEAT-1: UI chrome font size, clamped to 8..24. Fires <see cref="FontSettingsChanged"/> on change.</summary>
    private int _uiFontSize = HoardPadSettings.DefaultUiFontSize;

    public int UiFontSize
    {
        get => _uiFontSize;
        set
        {
            if (SetProperty(ref _uiFontSize, Math.Clamp(value, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize)))
                FontSettingsChanged?.Invoke();
        }
    }

    // ---- FEAT-15: AI-panel style settings (input font, response font/color, user-message font/color). ----
    // Reuses the FEAT-1 UiFontOptions family picker list (AI text is UI-chrome-scale prose, not the
    // monospace editor buffer) and the FEAT-1 8..24 size clamp. Fires the same FontSettingsChanged event
    // consumed by SettingsView.ApplyLiveFonts, so an AI style edit applies live exactly like a font edit.

    /// <summary>FEAT-15: AI prompt-input font family override; empty = brand.md §5 default.</summary>
    [ObservableProperty]
    private string _aiInputFontFamily = string.Empty;

    /// <summary>FEAT-15: AI assistant-message font family override; empty = brand.md §5 default.</summary>
    [ObservableProperty]
    private string _aiResponseFontFamily = string.Empty;

    /// <summary>FEAT-15: AI user-message font family override; empty = brand.md §5 default.</summary>
    [ObservableProperty]
    private string _aiUserFontFamily = string.Empty;

    /// <summary>FEAT-15: AI assistant-message text color override, a hex string (e.g. "#E5B93D"); empty = on-surface token.</summary>
    [ObservableProperty]
    private string _aiResponseColor = string.Empty;

    /// <summary>FEAT-15: AI user-message text color override, a hex string (e.g. "#E5B93D"); empty = on-surface token.</summary>
    [ObservableProperty]
    private string _aiUserColor = string.Empty;

    partial void OnAiInputFontFamilyChanged(string value) => FontSettingsChanged?.Invoke();
    partial void OnAiResponseFontFamilyChanged(string value) => FontSettingsChanged?.Invoke();
    partial void OnAiUserFontFamilyChanged(string value) => FontSettingsChanged?.Invoke();
    partial void OnAiResponseColorChanged(string value) => FontSettingsChanged?.Invoke();
    partial void OnAiUserColorChanged(string value) => FontSettingsChanged?.Invoke();

    /// <summary>FEAT-15: AI prompt-input font size, clamped to 8..24. Fires <see cref="FontSettingsChanged"/> on change.</summary>
    private int _aiInputFontSize = HoardPadSettings.DefaultAiFontSize;

    public int AiInputFontSize
    {
        get => _aiInputFontSize;
        set
        {
            if (SetProperty(ref _aiInputFontSize, Math.Clamp(value, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize)))
                FontSettingsChanged?.Invoke();
        }
    }

    /// <summary>FEAT-15: AI assistant-message font size, clamped to 8..24. Fires <see cref="FontSettingsChanged"/> on change.</summary>
    private int _aiResponseFontSize = HoardPadSettings.DefaultAiFontSize;

    public int AiResponseFontSize
    {
        get => _aiResponseFontSize;
        set
        {
            if (SetProperty(ref _aiResponseFontSize, Math.Clamp(value, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize)))
                FontSettingsChanged?.Invoke();
        }
    }

    /// <summary>FEAT-15: AI user-message font size, clamped to 8..24. Fires <see cref="FontSettingsChanged"/> on change.</summary>
    private int _aiUserFontSize = HoardPadSettings.DefaultAiFontSize;

    public int AiUserFontSize
    {
        get => _aiUserFontSize;
        set
        {
            if (SetProperty(ref _aiUserFontSize, Math.Clamp(value, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize)))
                FontSettingsChanged?.Invoke();
        }
    }

    /// <summary>BUG-5: <c>true</c> when "Custom/Other..." is selected in the model ComboBox.</summary>
    public bool IsCustomModel => SelectedModelOption == CustomModelOption;

    /// <summary>
    /// BUG-5: the effective model ID for persistence. When <see cref="IsCustomModel"/> is true this is
    /// <see cref="CustomModelId"/>; otherwise it is <see cref="SelectedModelOption"/>.
    /// </summary>
    public string ModelId => IsCustomModel ? CustomModelId : SelectedModelOption;

    partial void OnSelectedModelOptionChanged(string value)
    {
        // When a known preset is selected, clear the custom field so stale text does not linger.
        if (!IsCustomModel)
            CustomModelId = string.Empty;
        OnPropertyChanged(nameof(ModelId));
    }

    partial void OnCustomModelIdChanged(string value)
    {
        // Only propagate to ModelId when the custom option is active.
        if (IsCustomModel)
            OnPropertyChanged(nameof(ModelId));
    }

    // ---- FR-35 surface: appearance + editor + files (design §7.3/§7.4/§7.5). ----

    /// <summary>FR-37 active theme. Persisted/exposed only — the live switch is the cut-tail M7.1.</summary>
    [ObservableProperty]
    private AppTheme _theme;

    /// <summary>FR-8 default new-document language.</summary>
    [ObservableProperty]
    private string _defaultLanguage = HoardPadSettings.DefaultLanguageLabel;

    /// <summary>FR-20 auto-save idle interval (seconds). Clamped to 1..30 on set (design §7.5).</summary>
    private int _autoSaveSeconds = HoardPadSettings.DefaultAutoSaveSeconds;

    public int AutoSaveSeconds
    {
        get => _autoSaveSeconds;
        set
        {
            // FR-20 ceiling: clamp to 1..30 s (design §7.5). A non-positive interval would disable
            // auto-save; the hard ceiling bounds the data-loss window. This is a value clamp, not the
            // FR-35 backstop clamp (that is CapClamp, below).
            int clamped = Math.Clamp(value, 1, HoardPadSettings.MaxAutoSaveSeconds);
            SetProperty(ref _autoSaveSeconds, clamped);
        }
    }

    // ---- FR-35 surface: AI-safety tunables (design §7.5). ----

    /// <summary>FR-10(a) large-root file-count threshold. Clamped to ≥ 1 on set.</summary>
    private int _largeRootThreshold = HoardPadSettings.DefaultLargeRootThreshold;

    public int LargeRootThreshold
    {
        get => _largeRootThreshold;
        set => SetProperty(ref _largeRootThreshold, Math.Max(1, value));
    }

    /// <summary>
    /// FS-5 (docs/decisions.md DR-1): user override for FR-10(a) — ON (default) keeps the home/large-root
    /// auto-downgrade-to-approve-each; OFF auto-applies everywhere in-bounds.
    /// </summary>
    [ObservableProperty]
    private bool _confirmLargeHomeFolder = true;

    // The four caps each route through CapClamp on set: a candidate CapConfig (this cap changed, the
    // others held at their last-safe value) is validated; a rejection keeps the prior safe value and
    // surfaces a message. CapClamp.Validate is the SOLE clamp authority (no reimplementation).

    /// <summary>FR-11 per-turn create cap. Set routes through the FR-35 clamp (rejects 0/unbounded).</summary>
    public int CreateCap
    {
        get => _caps.CreateCap;
        set => TrySetCap(_caps with { CreateCap = value }, CapField.CreateCap, nameof(CreateCap));
    }

    /// <summary>FR-38 per-turn destructive-op cap. Set routes through the FR-35 clamp.</summary>
    public int DestructiveCap
    {
        get => _caps.DestructiveCap;
        set => TrySetCap(_caps with { DestructiveCap = value }, CapField.DestructiveCap, nameof(DestructiveCap));
    }

    /// <summary>FR-40 per-turn distinct-files-touched cap. Set routes through the FR-35 clamp.</summary>
    public int FilesTouchedCap
    {
        get => _caps.FilesTouchedCap;
        set => TrySetCap(_caps with { FilesTouchedCap = value }, CapField.FilesTouchedCap, nameof(FilesTouchedCap));
    }

    /// <summary>FR-38 X% magnitude firing-threshold (whole percent). Set routes through the FR-35 clamp (1..100).</summary>
    public int DestructiveMagnitudePercent
    {
        get => _caps.DestructiveMagnitudePercent;
        set => TrySetCap(
            _caps with { DestructiveMagnitudePercent = value },
            CapField.DestructiveMagnitudePercent,
            nameof(DestructiveMagnitudePercent));
    }

    /// <summary>FR-24 approve-each no-reply timeout (seconds). Clamped to ≥ 1 on set.</summary>
    private int _noReplySeconds = HoardPadSettings.DefaultNoReplySeconds;

    public int NoReplySeconds
    {
        get => _noReplySeconds;
        set => SetProperty(ref _noReplySeconds, Math.Max(1, value));
    }

    /// <summary>
    /// The last cap-validation message (empty when the last cap edit was accepted). The view binds this to
    /// surface the FR-35 "invalid values are rejected with a clear message" requirement.
    /// </summary>
    [ObservableProperty]
    private string _capValidationMessage = string.Empty;

    /// <summary>The current caps as the Core.Ai <see cref="CapConfig"/> (always a clamp-valid value).</summary>
    public CapConfig Caps => _caps;

    /// <summary>
    /// Validate <paramref name="candidate"/> against the FR-35 clamp. On pass, commit it and raise the four
    /// cap properties; on rejection, keep the prior safe value and surface the message (FR-35 Done-when).
    /// </summary>
    private void TrySetCap(CapConfig candidate, CapField changed, string propertyName)
    {
        IReadOnlyList<CapClampError> errors = CapClamp.Validate(candidate);
        CapClampError? rejection = errors.FirstOrDefault(e => e.Field == changed);
        if (rejection is not null)
        {
            // Reject: do NOT mutate _caps. Re-raise the property so a bound control snaps back to the
            // retained safe value, and surface the reason (FR-35 — rejected with a clear message).
            CapValidationMessage = DescribeRejection(rejection);
            OnPropertyChanged(propertyName);
            return;
        }

        _caps = candidate;
        CapValidationMessage = string.Empty;
        OnPropertyChanged(propertyName);
    }

    private static string DescribeRejection(CapClampError error)
    {
        string field = error.Field switch
        {
            CapField.CreateCap => "Create cap",
            CapField.DestructiveCap => "Destructive cap",
            CapField.FilesTouchedCap => "Files-touched cap",
            CapField.DestructiveMagnitudePercent => "Destructive magnitude %",
            _ => "Cap",
        };
        string reason = error.Reason switch
        {
            CapClampReason.NonPositive => "cannot be 0 or negative — that disables the AI safety backstop.",
            CapClampReason.Unbounded => "cannot be unbounded — that disables the AI safety backstop.",
            CapClampReason.MagnitudeOutOfRange => "must be between 1 and 100 percent.",
            _ => "is not a valid value.",
        };
        return $"{field} {reason}";
    }

    /// <summary>Project the current bound state into a persistable <see cref="HoardPadSettings"/> record.</summary>
    public HoardPadSettings ToSettings() => new(
        ActiveBackend: ActiveBackend,
        ClaudeCliPath: NullIfBlank(ClaudeCliPath),
        AnthropicApiKey: NullIfBlank(AnthropicApiKey),
        AnthropicModel: NullIfBlank(AnthropicModel),
        LocalEndpoint: NullIfBlank(LocalEndpoint),
        LocalModel: NullIfBlank(LocalModel),
        AgencyMode: AgencyMode,
        AiPanelHotkey: AiPanelHotkey,
        AutoSaveSeconds: AutoSaveSeconds,
        Theme: Theme,
        DefaultLanguage: DefaultLanguage,
        LargeRootThreshold: LargeRootThreshold,
        Caps: _caps,
        NoReplySeconds: NoReplySeconds,
        ConfirmLargeHomeFolder: ConfirmLargeHomeFolder,
        ModelId: NullIfBlank(ModelId),
        EffortLevel: NullIfBlank(EffortLevel),
        EditorFontFamily: NullIfBlank(EditorFontFamily),
        EditorFontSize: EditorFontSize,
        UiFontFamily: NullIfBlank(UiFontFamily),
        UiFontSize: UiFontSize,
        AiInputFontFamily: NullIfBlank(AiInputFontFamily),
        AiInputFontSize: AiInputFontSize,
        AiResponseFontFamily: NullIfBlank(AiResponseFontFamily),
        AiResponseFontSize: AiResponseFontSize,
        AiResponseColor: NullIfBlank(AiResponseColor),
        AiUserFontFamily: NullIfBlank(AiUserFontFamily),
        AiUserFontSize: AiUserFontSize,
        AiUserColor: NullIfBlank(AiUserColor));

    /// <summary>
    /// QA2-9: confirms the last Save completed, since persistence is explicit (not live). Cleared on the
    /// next edit to any bound field via <see cref="OnPropertyChanged(string?)"/> below, so a stale
    /// "Settings saved." message never survives an unsaved edit.
    /// </summary>
    [ObservableProperty]
    private string _saveStatusMessage = string.Empty;

    /// <summary>Persist the current settings atomically (FR-36). The save runs through the atomic store.</summary>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        await _store.SaveAsync(ToSettings(), ct).ConfigureAwait(true);
        SaveStatusMessage = "Settings saved.";
    }

    /// <summary>
    /// QA2-9: any bound-field change after a save invalidates the "Settings saved." confirmation, so the
    /// message never lingers once the on-screen values have diverged from the persisted file again.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(SaveStatusMessage) && SaveStatusMessage.Length > 0)
            SaveStatusMessage = string.Empty;
    }

    private void Apply(HoardPadSettings s)
    {
        ActiveBackend = s.ActiveBackend;
        ClaudeCliPath = s.ClaudeCliPath ?? string.Empty;
        AnthropicApiKey = s.AnthropicApiKey ?? string.Empty;
        AnthropicModel = s.AnthropicModel ?? string.Empty;
        LocalEndpoint = s.LocalEndpoint ?? string.Empty;
        LocalModel = s.LocalModel ?? string.Empty;
        AgencyMode = s.AgencyMode;
        AiPanelHotkey = s.AiPanelHotkey;
        AutoSaveSeconds = s.AutoSaveSeconds;
        Theme = s.Theme;
        DefaultLanguage = s.DefaultLanguage;
        LargeRootThreshold = s.LargeRootThreshold;
        NoReplySeconds = s.NoReplySeconds;
        ConfirmLargeHomeFolder = s.ConfirmLargeHomeFolder;

        // BUG-5: load model id + effort level (extracted to ApplyModelEffort — open-time snapshot only,
        // QA4-21).
        ApplyModelEffort(s);

        // FEAT-1: load font settings.
        EditorFontFamily = s.EditorFontFamily ?? string.Empty;
        EditorFontSize = s.EditorFontSize;
        UiFontFamily = s.UiFontFamily ?? string.Empty;
        UiFontSize = s.UiFontSize;

        // FEAT-15: load AI-panel style settings.
        AiInputFontFamily = s.AiInputFontFamily ?? string.Empty;
        AiInputFontSize = s.AiInputFontSize;
        AiResponseFontFamily = s.AiResponseFontFamily ?? string.Empty;
        AiResponseFontSize = s.AiResponseFontSize;
        AiResponseColor = s.AiResponseColor ?? string.Empty;
        AiUserFontFamily = s.AiUserFontFamily ?? string.Empty;
        AiUserFontSize = s.AiUserFontSize;
        AiUserColor = s.AiUserColor ?? string.Empty;

        // _caps was set from the loaded value in the ctor; raise the four cap properties so a bound view
        // reflects the loaded caps.
        OnPropertyChanged(nameof(CreateCap));
        OnPropertyChanged(nameof(DestructiveCap));
        OnPropertyChanged(nameof(FilesTouchedCap));
        OnPropertyChanged(nameof(DestructiveMagnitudePercent));
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
