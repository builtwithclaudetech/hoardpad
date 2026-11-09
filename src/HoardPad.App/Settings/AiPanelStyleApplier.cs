// AiPanelStyleApplier.cs — the SINGLE writer of the FEAT-15 AI-panel style resource keys.
//
// Mirrors SettingsFontApplier (QA3-1's fix) exactly: both the live-edit path (SettingsView.ApplyLiveFonts)
// and the startup path (App.axaml.cs, after ApplyStartupThemeAndFonts) call THIS single helper, so a
// relaunch can never silently drop a saved AI font/color choice the way QA3-1 did for the FEAT-1 fields.
//
// Six FontFamily/size resource keys are ALWAYS written (Brand.Font.AiInput/AiInputSize/AiResponse/
// AiResponseSize/AiUser/AiUserSize), clamped to HoardPadSettings.Min/MaxUiFontSize — the AI panel's text
// is UI-chrome-scale prose, not the monospace editor buffer, so it shares the FEAT-1 UI-font bounds rather
// than introducing new ones. Brand.Font.AiInput drives the always-visible prompt TextBox directly via
// DynamicResource (AiPanelView.axaml); Brand.Font.AiResponse/AiUser are read by TranscriptBubbleConverter
// per transcript-line Kind (a single shared TextBlock template, not duplicated per sender — the FEAT-12
// posture).
//
// The two color keys (Brand.Color.AiResponse/AiUser) are written ONLY when the user has set a valid hex
// override — otherwise the key is REMOVED so TranscriptForegroundConverter falls through to the live
// Brand.OnSurface.Brush theme token (staying theme-reactive for the common no-override case, rather than
// freezing a snapshot of whichever theme was active at Apply time).
using Avalonia;
using Avalonia.Media;

namespace HoardPad.App.Settings;

/// <summary>
/// Writes the FEAT-15 AI-panel style resource keys (<c>Brand.Font.AiInput</c>/<c>AiInputSize</c>/
/// <c>AiResponse</c>/<c>AiResponseSize</c>/<c>AiUser</c>/<c>AiUserSize</c> + <c>Brand.Color.AiResponse</c>/
/// <c>AiUser</c>) into <see cref="Application.Resources"/>. The single writer shared by the live-edit path
/// (<c>SettingsView.ApplyLiveFonts</c>) and the startup re-apply path (<c>App.axaml.cs</c>).
/// </summary>
internal static class AiPanelStyleApplier
{
    /// <summary>Apply the AI style fields of a persisted <see cref="HoardPadSettings"/> (startup path).</summary>
    public static void Apply(Application app, HoardPadSettings settings) => Apply(
        app,
        settings.AiInputFontFamily, settings.AiInputFontSize,
        settings.AiResponseFontFamily, settings.AiResponseFontSize, settings.AiResponseColor,
        settings.AiUserFontFamily, settings.AiUserFontSize, settings.AiUserColor);

    /// <summary>
    /// Apply explicit AI style values (the live-edit path, where the VM's already-clamped properties are
    /// the source). Clamps sizes to <see cref="HoardPadSettings.MinUiFontSize"/>/<see cref="HoardPadSettings.MaxUiFontSize"/>
    /// regardless of the caller, so neither call site can push an out-of-range value into
    /// <see cref="Application.Resources"/>.
    /// </summary>
    public static void Apply(
        Application app,
        string? inputFontFamily,
        int inputFontSize,
        string? responseFontFamily,
        int responseFontSize,
        string? responseColor,
        string? userFontFamily,
        int userFontSize,
        string? userColor)
    {
        if (app.Resources is null) return;

        int clampedInputSize = Math.Clamp(inputFontSize, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize);
        int clampedResponseSize = Math.Clamp(responseFontSize, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize);
        int clampedUserSize = Math.Clamp(userFontSize, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize);

        app.Resources["Brand.Font.AiInput"] = new FontFamily(
            string.IsNullOrWhiteSpace(inputFontFamily) ? HoardPadSettings.DefaultUiFontFallback : inputFontFamily);
        app.Resources["Brand.Font.AiInputSize"] = (double)clampedInputSize;

        app.Resources["Brand.Font.AiResponse"] = new FontFamily(
            string.IsNullOrWhiteSpace(responseFontFamily) ? HoardPadSettings.DefaultUiFontFallback : responseFontFamily);
        app.Resources["Brand.Font.AiResponseSize"] = (double)clampedResponseSize;
        SetOrRemoveColor(app, "Brand.Color.AiResponse", responseColor);

        app.Resources["Brand.Font.AiUser"] = new FontFamily(
            string.IsNullOrWhiteSpace(userFontFamily) ? HoardPadSettings.DefaultUiFontFallback : userFontFamily);
        app.Resources["Brand.Font.AiUserSize"] = (double)clampedUserSize;
        SetOrRemoveColor(app, "Brand.Color.AiUser", userColor);
    }

    /// <summary>
    /// Writes a fixed <see cref="SolidColorBrush"/> when <paramref name="hex"/> parses; otherwise removes
    /// the key so a reader falls through to the live theme token (never a stale/invalid frozen color).
    /// </summary>
    private static void SetOrRemoveColor(Application app, string key, string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out Color parsed))
            app.Resources![key] = new SolidColorBrush(parsed);
        else
            app.Resources!.Remove(key);
    }
}
