// HoardPadSettings.cs — the FR-35 settings surface model + documented defaults (M6.8).
//
// THE single human-readable settings record persisted to IAppDataPaths.SettingsFilePath (FR-36,
// architecture §8.2). It carries the whole FR-35 surface: active backend + per-backend config,
// agency mode, AI-panel hotkey, auto-save interval, active theme, default new-doc language, and the
// AI-safety tunables (the FR-10(a) large-root threshold, the four caps, the FR-24 no-reply timeout).
//
// SINGLE SOURCE OF TRUTH for defaults: HoardPadSettings.Default. The four cap defaults are NOT
// re-typed here — they come from HoardPad.Core.Ai's CapConfig.Pinned (the SPIKE-3-measured 25/10/20/80,
// the same values the AI panel + spine already consume). A missing/corrupt settings file falls back to
// this Default without crashing (FR-36, handled by SettingsStore). The caps are stored as the Core.Ai
// CapConfig so the FR-35 clamp (CapClamp) validates the EXACT type the spine enforces — no parallel
// cap representation.

using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Records;

namespace HoardPad.App.Settings;

/// <summary>
/// The persisted FR-35 settings surface (architecture §8.2). One human-readable record under
/// <see cref="HoardPad.Core.Host.Abstractions.IAppDataPaths.SettingsFilePath"/> (FR-36). A
/// missing/corrupt file falls back to <see cref="Default"/> (the documented defaults — the
/// SINGLE source of truth) without crashing. The four AI caps are the Core.Ai <see cref="CapConfig"/>
/// so the FR-35 clamp (<see cref="CapClamp"/>) validates the exact type the spine enforces.
/// </summary>
/// <param name="ActiveBackend">FR-23 active AI backend (default <c>claude -p</c>).</param>
/// <param name="ClaudeCliPath">claude -p (Archetype A) CLI path override; <c>null</c> = resolve via the FR-24 shim order.</param>
/// <param name="AnthropicApiKey">Anthropic API (Archetype B) key; <c>null</c> until configured (NFR-7 — never logged).</param>
/// <param name="AnthropicModel">Anthropic API model name; <c>null</c> = backend default.</param>
/// <param name="LocalEndpoint">Local LLM (Ollama/LiteLLM) endpoint URL; <c>null</c> until configured.</param>
/// <param name="LocalModel">Local LLM model name; <c>null</c> = backend default.</param>
/// <param name="AgencyMode">FR-27 AI agency mode (default <c>auto-apply</c>).</param>
/// <param name="AiPanelHotkey">FR-22 app-scoped AI-panel hotkey (default <c>Ctrl+Shift+A</c>).</param>
/// <param name="AutoSaveSeconds">FR-20 auto-save idle interval, seconds (default 5, hard ceiling 30).</param>
/// <param name="Theme">FR-37 active theme (default <c>Dark</c>).</param>
/// <param name="DefaultLanguage">FR-8 default new-document language (default <c>Plain Text</c>).</param>
/// <param name="LargeRootThreshold">FR-10(a) large-root file-count threshold (default 2,000).</param>
/// <param name="Caps">The FR-11/FR-38/FR-40 + FR-38 X% caps (default <see cref="CapConfig.Pinned"/>).</param>
/// <param name="NoReplySeconds">FR-24 approve-each no-reply timeout, seconds (default 60).</param>
/// <param name="ConfirmLargeHomeFolder">
/// FS-5 (docs/decisions.md DR-1): user override for FR-10(a) — <c>true</c> (default) keeps the
/// home/large-root auto-downgrade-to-approve-each; <c>false</c> auto-applies everywhere in-bounds
/// (every other FR-10/FR-26/cap/undo/log control stays unaffected either way).
/// </param>
/// <param name="ModelId">BUG-5: AI model ID (e.g. <c>claude-opus-4-8</c>); <c>null</c> = backend default.</param>
/// <param name="EffortLevel">BUG-5: AI reasoning effort (<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>); <c>null</c> = backend default.</param>
/// <param name="EditorFontFamily">FEAT-1: editor code font family override; <c>null</c> = brand.md §5 fallback chain.</param>
/// <param name="EditorFontSize">FEAT-1: editor code font size (default <see cref="DefaultEditorFontSize"/>).</param>
/// <param name="UiFontFamily">FEAT-1: UI chrome font family override; <c>null</c> = brand.md §5 fallback chain.</param>
/// <param name="UiFontSize">FEAT-1: UI chrome font size (default <see cref="DefaultUiFontSize"/>).</param>
/// <param name="AiInputFontFamily">FEAT-15: AI prompt-input font family override; <c>null</c> = brand.md §5 fallback chain.</param>
/// <param name="AiInputFontSize">FEAT-15: AI prompt-input font size (default <see cref="DefaultAiFontSize"/>).</param>
/// <param name="AiResponseFontFamily">FEAT-15: AI assistant-message font family override; <c>null</c> = brand.md §5 fallback chain.</param>
/// <param name="AiResponseFontSize">FEAT-15: AI assistant-message font size (default <see cref="DefaultAiFontSize"/>).</param>
/// <param name="AiResponseColor">FEAT-15: AI assistant-message text color override, a hex string (e.g. <c>#E5B93D</c>); <c>null</c> = on-surface token.</param>
/// <param name="AiUserFontFamily">FEAT-15: AI user-message font family override; <c>null</c> = brand.md §5 fallback chain.</param>
/// <param name="AiUserFontSize">FEAT-15: AI user-message font size (default <see cref="DefaultAiFontSize"/>).</param>
/// <param name="AiUserColor">FEAT-15: AI user-message text color override, a hex string (e.g. <c>#E5B93D</c>); <c>null</c> = on-surface token.</param>
public sealed record HoardPadSettings(
    AiBackend ActiveBackend,
    string? ClaudeCliPath,
    string? AnthropicApiKey,
    string? AnthropicModel,
    string? LocalEndpoint,
    string? LocalModel,
    AgencyMode AgencyMode,
    string AiPanelHotkey,
    int AutoSaveSeconds,
    AppTheme Theme,
    string DefaultLanguage,
    int LargeRootThreshold,
    CapConfig Caps,
    int NoReplySeconds,
    bool ConfirmLargeHomeFolder = true,
    string? ModelId = null,
    string? EffortLevel = null,
    string? EditorFontFamily = null,
    int EditorFontSize = 13,
    string? UiFontFamily = null,
    int UiFontSize = 12,
    string? AiInputFontFamily = null,
    int AiInputFontSize = 12,
    string? AiResponseFontFamily = null,
    int AiResponseFontSize = 12,
    string? AiResponseColor = null,
    string? AiUserFontFamily = null,
    int AiUserFontSize = 12,
    string? AiUserColor = null)
{
    // ---- The documented defaults (FR-35/§7.5/§8.2) — the SINGLE source of truth. ----

    /// <summary>FR-20 default auto-save idle interval (5 s, design §7.5).</summary>
    public const int DefaultAutoSaveSeconds = 5;

    /// <summary>FR-20 hard ceiling for the auto-save interval (30 s, design §7.5).</summary>
    public const int MaxAutoSaveSeconds = 30;

    /// <summary>
    /// FR-20 hard floor for the auto-save interval (1 s). The single source of truth both the
    /// Settings-UI clamp (<see cref="SettingsChangeApplier"/>) and the periodic-timer clamp
    /// (<see cref="HoardPad.App.Session.WorkspaceService.StartAutoSaveTimer"/>, F-FS1c fix 1) share, so a
    /// zero/negative/missing-field value from a hand-edited or partially-deserialized settings.json can
    /// never become a hot-spinning or throwing <c>DispatcherTimer</c> interval.
    /// </summary>
    public const int MinAutoSaveSeconds = 1;

    /// <summary>FR-22 default app-scoped AI-panel hotkey (design §7.1).</summary>
    public const string DefaultAiPanelHotkey = "Ctrl+Shift+A";

    /// <summary>FR-8 default new-document language (design §7.3).</summary>
    public const string DefaultLanguageLabel = "Plain Text";

    /// <summary>FR-10(a) default large-root file-count threshold (design §7.5).</summary>
    public const int DefaultLargeRootThreshold = 2_000;

    /// <summary>FR-24 default approve-each no-reply timeout in seconds (design §7.5).</summary>
    public const int DefaultNoReplySeconds = 60;

    /// <summary>FEAT-1 default editor code font size.</summary>
    public const int DefaultEditorFontSize = 13;

    /// <summary>FEAT-1 default UI chrome font size.</summary>
    public const int DefaultUiFontSize = 12;

    /// <summary>FEAT-1 minimum allowed editor font size.</summary>
    public const int MinEditorFontSize = 8;

    /// <summary>FEAT-1 maximum allowed editor font size.</summary>
    public const int MaxEditorFontSize = 36;

    /// <summary>FEAT-1 minimum allowed UI chrome font size.</summary>
    public const int MinUiFontSize = 8;

    /// <summary>FEAT-1 maximum allowed UI chrome font size.</summary>
    public const int MaxUiFontSize = 24;

    /// <summary>
    /// FEAT-15 default AI-panel font size (input / response / user message). Shares the FEAT-1
    /// <see cref="MinUiFontSize"/>/<see cref="MaxUiFontSize"/> clamp bounds — the AI panel's text is
    /// UI-chrome-scale prose, not the monospace editor buffer.
    /// </summary>
    public const int DefaultAiFontSize = 12;

    /// <summary>
    /// FEAT-1 default editor code font fallback chain (brand.md §5 cross-platform monospace stack
    /// minus the unbundled JetBrains Mono). Mirrored in <c>BrandTheme.axaml</c> as the
    /// <c>Brand.Font.Code</c> resource — edit both when bundling JetBrains Mono.
    /// </summary>
    public const string DefaultCodeFontFallback = "Cascadia Mono,Consolas,DejaVu Sans Mono,monospace";

    /// <summary>
    /// FEAT-1 default UI chrome font fallback chain (brand.md §5 Inter stack). Mirrored in
    /// <c>BrandTheme.axaml</c> as the <c>Brand.Font.Ui</c> resource.
    /// </summary>
    public const string DefaultUiFontFallback = "Inter, system-ui, Segoe UI, Roboto, Noto Sans, sans-serif";

    /// <summary>
    /// The documented FR-35 defaults (design §7.5, architecture §8.2) — the single source of truth a
    /// missing/corrupt settings file falls back to (FR-36). The four caps are <see cref="CapConfig.Pinned"/>
    /// (the SPIKE-3-measured 25/10/20/80), referenced rather than re-typed.
    /// </summary>
    public static HoardPadSettings Default { get; } = new(
        ActiveBackend: AiBackend.ClaudeCli,
        ClaudeCliPath: null,
        AnthropicApiKey: null,
        AnthropicModel: null,
        LocalEndpoint: null,
        LocalModel: null,
        AgencyMode: AgencyMode.AutoApply,
        AiPanelHotkey: DefaultAiPanelHotkey,
        AutoSaveSeconds: DefaultAutoSaveSeconds,
        Theme: AppTheme.Dark,
        DefaultLanguage: DefaultLanguageLabel,
        LargeRootThreshold: DefaultLargeRootThreshold,
        Caps: CapConfig.Pinned,
        NoReplySeconds: DefaultNoReplySeconds);
}

/// <summary>
/// FR-37 active theme. Dark is the first-run default (FR-37). The live theme SWITCH is the cut-tail
/// M7.1; M6.8 only persists/exposes the selection (build-context cut-tail boundary).
/// </summary>
public enum AppTheme
{
    /// <summary>The dark theme — the FR-37 first-run default.</summary>
    Dark,

    /// <summary>The documented light alternate (FR-37, design §7.4).</summary>
    Light,
}
