// SettingsChangeApplier.cs — FEAT-14/QA4-17 pure allow-list + value computation for an AI-requested
// settings change (architecture AI file-op safety + FR boundaries; §8.2 settings/config).
//
// QA4-17 broadens this from the original FEAT-14 twelve font/color fields to the FULL HoardPadSettings
// record EXCEPT SIX fields, which are OUT OF SCOPE BY CONSTRUCTION (no switch case for them — TryCompute
// always returns null): ModelId, EffortLevel (user-only, BUG-5/QA4-21 — the AI panel's own Model/Effort
// pickers are an ephemeral per-session override, never AI-writable); the three FS-5 safety fields
// ConfirmLargeHomeFolder, AgencyMode, LargeRootThreshold (docs/decisions.md DR-1 — the AI must never be
// able to weaken its own agency gate or the large/home-root downgrade trigger by proposing a settings
// change); and AnthropicApiKey (QA4-17 hostile-round CRITICAL finding — a credential is categorically
// different from a font size: (a) the proposed value MUST be embedded in the assistant's own
// ```hoardpad-settings``` marker text to work at all, and that text is rendered verbatim in the chat
// transcript AND re-injected into every later turn's prompt by AiConversationContextBuilder (QA4-12), so
// a "masked display" is no defense — the PLAINTEXT key would still leak to the backend on every
// subsequent Send; (b) even ignoring the leak, the WRITE itself lets a prompt-injected AI plant an
// attacker-controlled key + (separately, legitimately) flip ActiveBackend to route conversation/document
// content through the attacker's account. AnthropicApiKey is therefore excluded exactly like the other
// five safety-critical fields — no masking helper can make a credential write safe here).
//
// Every other field — the AI backend selection, the non-secret per-backend config (ClaudeCliPath/
// AnthropicModel/LocalEndpoint/LocalModel), the AI-panel hotkey, the auto-save interval, the active
// theme, the default new-document language, the four FR-11/FR-38/FR-40 caps (routed through the SAME
// Core.Ai CapClamp the Settings tab uses — an unsafe cap value is REJECTED, never clamped, exactly like
// SettingsViewModel.TrySetCap), the FR-24 no-reply timeout, and the original twelve font/color fields —
// is AI-writable.
//
// FIELD-SCOPED PATCH (QA4-17 hostile-round CRITICAL #2, concurrency): every computation carries not just
// the fully-updated `Updated` record (handy for display/tests) but a `Patch`/`RevertPatch` delegate that
// mutates ONLY the one field this proposal actually changed, against WHATEVER base record it is applied
// to later — never a snapshot frozen at TryCompute time. The caller (App.ApplyAiSettingsChangeAsync) runs
// Patch/RevertPatch inside SettingsStore.UpdateAsync's own critical section, against the record loaded
// FRESH at that moment — so a concurrent writer of any OTHER field (a theme toggle, a Settings-tab Save,
// another AI-applied change) can never be silently reverted by a stale multi-field re-assertion. This is
// the exact same posture ThemeController.PersistAsync already has (`s => s with { Theme = _current }`),
// generalized to every allowed field.
//
// No I/O — the caller (AiPanelViewModel) owns loading the current snapshot and persisting + live-applying
// the result through the SAME agency/confirm gate as a file write.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Records;

namespace HoardPad.App.Settings;

/// <summary>
/// One computed settings change: the field's human-readable label (for the confirm dialog + the audit
/// chip), the old/new display strings, the fully-updated <see cref="HoardPadSettings"/> record (handy for
/// display/tests — computed as <c>Patch(current)</c>), and the FIELD-SCOPED <see cref="Patch"/>/
/// <see cref="RevertPatch"/> delegates the caller actually persists through (never a frozen multi-field
/// snapshot — see class remarks). Returned only for an ALLOWED field whose value is valid AND actually
/// different from the current value (a no-op request yields no computation — nothing to confirm or apply).
/// </summary>
internal sealed record SettingsChangeComputation(
    string FieldLabel,
    string OldDisplay,
    string NewDisplay,
    HoardPadSettings Updated,
    Func<HoardPadSettings, HoardPadSettings> Patch,
    Func<HoardPadSettings, HoardPadSettings> RevertPatch);

/// <summary>
/// QA4-17's pure allow-list (reopens FEAT-14). <see cref="TryCompute"/> validates a proposed
/// <c>(field, rawValue)</c> pair against <paramref name="current"/> and, if valid and allowed, returns the
/// updated record + display strings + a field-scoped patch; otherwise <c>null</c> (the caller declines
/// silently or logs a "could not be applied" note — its choice, not this class's).
/// </summary>
internal static class SettingsChangeApplier
{
    /// <summary>
    /// The FR-8 default new-document language choices — MUST stay in lockstep with
    /// <c>HoardPad.App.ViewModels.SettingsViewModel.LanguageOptions</c> (duplicated rather than
    /// cross-referenced to avoid a Settings → ViewModels dependency; the same "must stay in lockstep"
    /// posture this class's own <see cref="SettingsChangeApplier"/> switch already has with
    /// <c>SettingsChangeProposalParser.CapabilityInstruction</c>).
    /// </summary>
    private static readonly string[] KnownLanguages =
    [
        HoardPadSettings.DefaultLanguageLabel,
        "Markdown",
        "HTML",
        "JavaScript",
        "CSS",
        "Python",
        "C#",
        "XML",
        "JSON",
        "SQL",
        "Rust",
        "TypeScript",
        "YAML",
        "TOML",
        "Shell/Bash",
        "PowerShell",
        "Dockerfile",
        "C",
        "C++",
        "Java",
        "Go",
    ];

    /// <summary>
    /// Validate + compute a proposed settings change. Returns <c>null</c> when: <paramref name="field"/>
    /// (leading/trailing whitespace ignored — a UX nicety only; an untrimmed unrecognized name already
    /// failed closed to <c>null</c> before this, so trimming only WIDENS what's accepted among the
    /// genuinely-allowed names, never narrows the exclusion set) is not one of the allowed fields (see
    /// class remarks — six fields are excluded by construction); <paramref name="rawValue"/> does not
    /// parse/validate for that field's type; or the (clamped/normalized) new value equals the current one.
    /// </summary>
    public static SettingsChangeComputation? TryCompute(HoardPadSettings current, string field, string rawValue)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(field);
        string raw = (rawValue ?? string.Empty).Trim();
        string fieldTrimmed = field.Trim();

        return fieldTrimmed switch
        {
            // ---- FEAT-1/FEAT-15: the original twelve font/color fields (unchanged surface, field-scoped
            // patch since QA4-17). ----
            "EditorFontFamily" => FamilyChange(raw, "Editor font family", current, current.EditorFontFamily,
                (b, v) => b with { EditorFontFamily = v }),
            "UiFontFamily" => FamilyChange(raw, "UI font family", current, current.UiFontFamily,
                (b, v) => b with { UiFontFamily = v }),
            "AiInputFontFamily" => FamilyChange(raw, "AI input font family", current, current.AiInputFontFamily,
                (b, v) => b with { AiInputFontFamily = v }),
            "AiResponseFontFamily" => FamilyChange(raw, "AI response font family", current, current.AiResponseFontFamily,
                (b, v) => b with { AiResponseFontFamily = v }),
            "AiUserFontFamily" => FamilyChange(raw, "AI user font family", current, current.AiUserFontFamily,
                (b, v) => b with { AiUserFontFamily = v }),

            "EditorFontSize" => SizeChange(raw, "Editor font size", current, current.EditorFontSize,
                HoardPadSettings.MinEditorFontSize, HoardPadSettings.MaxEditorFontSize,
                (b, v) => b with { EditorFontSize = v }),
            "UiFontSize" => SizeChange(raw, "UI font size", current, current.UiFontSize,
                HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize,
                (b, v) => b with { UiFontSize = v }),
            "AiInputFontSize" => SizeChange(raw, "AI input font size", current, current.AiInputFontSize,
                HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize,
                (b, v) => b with { AiInputFontSize = v }),
            "AiResponseFontSize" => SizeChange(raw, "AI response font size", current, current.AiResponseFontSize,
                HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize,
                (b, v) => b with { AiResponseFontSize = v }),
            "AiUserFontSize" => SizeChange(raw, "AI user font size", current, current.AiUserFontSize,
                HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize,
                (b, v) => b with { AiUserFontSize = v }),

            "AiResponseColor" => ColorChange(raw, "AI response color", current, current.AiResponseColor,
                (b, v) => b with { AiResponseColor = v }),
            "AiUserColor" => ColorChange(raw, "AI user color", current, current.AiUserColor,
                (b, v) => b with { AiUserColor = v }),

            // ---- QA4-17: the broadened surface — everything else in HoardPadSettings except the six
            // excluded fields (see class remarks). ----
            "ActiveBackend" => EnumChange<AiBackend>(raw, "Active AI backend", current, current.ActiveBackend,
                (b, v) => b with { ActiveBackend = v }),
            "Theme" => EnumChange<AppTheme>(raw, "Theme", current, current.Theme,
                (b, v) => b with { Theme = v }),
            "DefaultLanguage" => LanguageChange(raw, current),

            "AutoSaveSeconds" => SizeChange(raw, "Auto-save interval (seconds)", current, current.AutoSaveSeconds,
                HoardPadSettings.MinAutoSaveSeconds, HoardPadSettings.MaxAutoSaveSeconds, (b, v) => b with { AutoSaveSeconds = v }),
            "NoReplySeconds" => SizeChange(raw, "No-reply timeout (seconds)", current, current.NoReplySeconds,
                1, int.MaxValue, (b, v) => b with { NoReplySeconds = v }),

            // AiPanelHotkey is a required (non-nullable) string — reuses FamilyChange's "non-empty text
            // required" validation, not because it is a font, but because that is exactly the shape this
            // field needs (no narrower validation exists on the Settings tab either — SettingsViewModel
            // binds it as a plain free-text field). The null-coalesce in the patch lambda is defensive only
            // (FamilyChange never actually produces a null new/old value here — AiPanelHotkey's own field
            // type is non-nullable) so this never needs a null-forgiving suppression.
            "AiPanelHotkey" => FamilyChange(raw, "AI panel hotkey", current, current.AiPanelHotkey,
                (b, v) => b with { AiPanelHotkey = v ?? HoardPadSettings.DefaultAiPanelHotkey }),

            "ClaudeCliPath" => OptionalStringChange(raw, "Claude CLI path", current, current.ClaudeCliPath,
                (b, v) => b with { ClaudeCliPath = v }),
            "AnthropicModel" => OptionalStringChange(raw, "Anthropic API model", current, current.AnthropicModel,
                (b, v) => b with { AnthropicModel = v }),
            "LocalEndpoint" => OptionalStringChange(raw, "Local LLM endpoint", current, current.LocalEndpoint,
                (b, v) => b with { LocalEndpoint = v }),
            "LocalModel" => OptionalStringChange(raw, "Local LLM model", current, current.LocalModel,
                (b, v) => b with { LocalModel = v }),

            "CreateCap" => CapFieldChange(raw, CapField.CreateCap, "Create cap", current,
                (b, v) => b with { Caps = b.Caps with { CreateCap = v } }),
            "DestructiveCap" => CapFieldChange(raw, CapField.DestructiveCap, "Destructive cap", current,
                (b, v) => b with { Caps = b.Caps with { DestructiveCap = v } }),
            "FilesTouchedCap" => CapFieldChange(raw, CapField.FilesTouchedCap, "Files-touched cap", current,
                (b, v) => b with { Caps = b.Caps with { FilesTouchedCap = v } }),
            "DestructiveMagnitudePercent" => CapFieldChange(
                raw, CapField.DestructiveMagnitudePercent, "Destructive magnitude %", current,
                (b, v) => b with { Caps = b.Caps with { DestructiveMagnitudePercent = v } }),

            // ModelId, EffortLevel, ConfirmLargeHomeFolder, AgencyMode, LargeRootThreshold, AnthropicApiKey
            // (QA4-17 hostile-round CRITICAL — see class remarks), and any unrecognized field name —
            // declined, fail closed by construction.
            _ => null,
        };
    }

    /// <summary>
    /// Build a <see cref="SettingsChangeComputation"/> whose <see cref="SettingsChangeComputation.Patch"/>/
    /// <see cref="SettingsChangeComputation.RevertPatch"/> apply <paramref name="applyToBase"/> against
    /// WHATEVER base record they are later invoked with — never a value baked in from
    /// <paramref name="current"/> at this call site (the QA4-17 hostile-round concurrency fix; see class
    /// remarks). <see cref="SettingsChangeComputation.Updated"/> is simply <c>Patch(current)</c>, kept for
    /// display/test convenience only — production code applies <c>Patch</c>, never <c>Updated</c> directly.
    /// </summary>
    private static SettingsChangeComputation Build<T>(
        string label, string oldDisplay, string newDisplay,
        HoardPadSettings current, T oldValue, T newValue,
        Func<HoardPadSettings, T, HoardPadSettings> applyToBase)
    {
        HoardPadSettings Patch(HoardPadSettings b) => applyToBase(b, newValue);
        HoardPadSettings RevertPatch(HoardPadSettings b) => applyToBase(b, oldValue);
        return new SettingsChangeComputation(label, oldDisplay, newDisplay, Patch(current), Patch, RevertPatch);
    }

    // A font-family (or other required non-empty text, e.g. AiPanelHotkey) change requires a non-empty
    // value and accepts any non-empty text (declining a reset-to-default request is a deliberate scope
    // limit for the font-family fields — see the class remarks; AiPanelHotkey has no default-reset
    // concept at all, it is simply required).
    private static SettingsChangeComputation? FamilyChange(
        string raw, string label, HoardPadSettings current, string? oldValue,
        Func<HoardPadSettings, string?, HoardPadSettings> applyToBase)
    {
        if (raw.Length == 0)
            return null;
        if (string.Equals(raw, oldValue, StringComparison.Ordinal))
            return null; // no-op request.

        return Build(label, oldValue is { Length: > 0 } ? oldValue : "(default)", raw, current, oldValue, (string?)raw, applyToBase);
    }

    // A font-size (or other clamped integer, e.g. AutoSaveSeconds/NoReplySeconds) change must parse as an
    // integer; the CLAMPED result mirrors SettingsFontApplier / AiPanelStyleApplier / SettingsViewModel's
    // own backstop clamp (an out-of-range AI request is capped, not rejected).
    private static SettingsChangeComputation? SizeChange(
        string raw, string label, HoardPadSettings current, int oldValue, int min, int max,
        Func<HoardPadSettings, int, HoardPadSettings> applyToBase)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return null;

        int clamped = Math.Clamp(parsed, min, max);
        if (clamped == oldValue)
            return null; // no-op after clamping.

        return Build(
            label, oldValue.ToString(CultureInfo.InvariantCulture), clamped.ToString(CultureInfo.InvariantCulture),
            current, oldValue, clamped, applyToBase);
    }

    // A color change accepts a parseable hex color (Avalonia's own Color.TryParse — the SAME validator
    // AiPanelStyleApplier.SetOrRemoveColor uses) or "default"/empty to clear the override back to the
    // live theme token.
    private static SettingsChangeComputation? ColorChange(
        string raw, string label, HoardPadSettings current, string? oldValue,
        Func<HoardPadSettings, string?, HoardPadSettings> applyToBase)
    {
        string? newValue;
        if (raw.Length == 0 || string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase))
            newValue = null;
        else if (Color.TryParse(raw, out _))
            newValue = raw;
        else
            return null; // unparseable color — declined.

        if (string.Equals(newValue, oldValue, StringComparison.OrdinalIgnoreCase))
            return null; // no-op request.

        string oldDisplay = oldValue is { Length: > 0 } ? oldValue : "(theme default)";
        string newDisplay = newValue is { Length: > 0 } ? newValue : "(theme default)";
        return Build(label, oldDisplay, newDisplay, current, oldValue, newValue, applyToBase);
    }

    // QA4-17: an optional (nullable) free-text field — ClaudeCliPath/AnthropicModel/LocalEndpoint/
    // LocalModel. Empty or "default" clears the override back to null (the backend-default posture),
    // same clear semantics as ColorChange, but with no color-format validation (these are paths/URLs/
    // model names, not colors). AnthropicApiKey is NOT here — it is excluded entirely (see class remarks).
    private static SettingsChangeComputation? OptionalStringChange(
        string raw, string label, HoardPadSettings current, string? oldValue,
        Func<HoardPadSettings, string?, HoardPadSettings> applyToBase)
    {
        string? newValue = raw.Length == 0 || string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase)
            ? null
            : raw;

        if (string.Equals(newValue, oldValue, StringComparison.Ordinal))
            return null; // no-op request.

        string oldDisplay = oldValue is { Length: > 0 } ? oldValue : "(not set)";
        string newDisplay = newValue is { Length: > 0 } ? newValue : "(not set)";
        return Build(label, oldDisplay, newDisplay, current, oldValue, newValue, applyToBase);
    }

    // QA4-17: ActiveBackend/Theme — parse the raw text as the C# enum member name (case-insensitive),
    // e.g. "AnthropicApi" or "Light". An unrecognized token is declined, never guessed/coerced.
    private static SettingsChangeComputation? EnumChange<T>(
        string raw, string label, HoardPadSettings current, T oldValue,
        Func<HoardPadSettings, T, HoardPadSettings> applyToBase)
        where T : struct, Enum
    {
        if (!Enum.TryParse(raw, ignoreCase: true, out T parsed) || !Enum.IsDefined(parsed))
            return null;
        if (EqualityComparer<T>.Default.Equals(parsed, oldValue))
            return null; // no-op request.

        return Build(label, oldValue.ToString()!, parsed.ToString()!, current, oldValue, parsed, applyToBase);
    }

    // QA4-17: DefaultLanguage — validated against the FR-7/FR-8 recognized language set (case-insensitive
    // match, canonical casing from KnownLanguages persisted) rather than accepted as free text; an
    // unrecognized language name is declined, matching the Settings tab's own ComboBox-only selection.
    private static SettingsChangeComputation? LanguageChange(string raw, HoardPadSettings current)
    {
        if (raw.Length == 0)
            return null;

        string? canonical = KnownLanguages.FirstOrDefault(l => string.Equals(l, raw, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return null; // not a recognized FR-7/FR-8 language — declined.
        if (string.Equals(canonical, current.DefaultLanguage, StringComparison.Ordinal))
            return null; // no-op request.

        return Build(
            "Default language", current.DefaultLanguage, canonical, current, current.DefaultLanguage, canonical,
            (b, v) => b with { DefaultLanguage = v });
    }

    // QA4-17: the four FR-11/FR-38/FR-40/FR-38(X%) caps — routed through the SAME Core.Ai CapClamp the
    // Settings tab's SettingsViewModel.TrySetCap uses. Unlike SizeChange, an unsafe value is REJECTED
    // (never clamped) — clamping a cap toward safety would still let an AI request silently pick ANY
    // in-bounds value including one right at the edge; the FR-35 backstop rule is specifically "0/
    // unbounded is refused", not a numeric band, so rejection (not clamping) is the correct posture here.
    // applyToBase patches ONLY the one CapConfig sub-field being changed (via `b.Caps with { X = v }`),
    // never re-asserting the other three sub-fields from a stale snapshot.
    private static SettingsChangeComputation? CapFieldChange(
        string raw, CapField capField, string label, HoardPadSettings current,
        Func<HoardPadSettings, int, HoardPadSettings> applyToBase)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return null;

        CapConfig candidate = applyToBase(current, parsed).Caps;
        if (CapClamp.Validate(candidate).Any(e => e.Field == capField))
            return null; // FR-35 backstop rejection — never reaches disk.

        int oldValue = CapFieldValue(current.Caps, capField);
        if (parsed == oldValue)
            return null; // no-op request.

        return Build(
            label, oldValue.ToString(CultureInfo.InvariantCulture), parsed.ToString(CultureInfo.InvariantCulture),
            current, oldValue, parsed, applyToBase);
    }

    private static int CapFieldValue(CapConfig caps, CapField field) => field switch
    {
        CapField.CreateCap => caps.CreateCap,
        CapField.DestructiveCap => caps.DestructiveCap,
        CapField.FilesTouchedCap => caps.FilesTouchedCap,
        CapField.DestructiveMagnitudePercent => caps.DestructiveMagnitudePercent,
        _ => 0,
    };
}
