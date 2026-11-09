// SettingsFontApplier.cs — the SINGLE writer of the FEAT-1 font resource keys (QA3-1).
//
// Before this fix, only SettingsView.axaml.cs.ApplyLiveFonts wrote Brand.Font.Code/CodeSize/Ui/UiSize
// into Application.Resources — it ran on every live edit in the settings tab, but nothing re-applied the
// PERSISTED font settings at the next launch, so a relaunch silently fell back to the BrandTheme.axaml
// compiled defaults (QA3-1 root cause). This helper is now the one place that writes those four resource
// keys; both the live-edit path (SettingsView.ApplyLiveFonts) and the startup path (App.axaml.cs, after
// ThemeController.ApplyStartupTheme()) call it, so the two can never drift apart.
//
// Clamps every font size to the documented HoardPadSettings bounds before writing (Min/MaxEditorFontSize,
// Min/MaxUiFontSize) — the VM's property setters already clamp on user edit, but a hand-edited or future
// AI-written settings.json is NOT validated on load (HoardPadSettings is a raw record), so this is the
// backstop that keeps an out-of-range persisted value from ever reaching Application.Resources.
//
// QA4-18: also derives Brand.Font.UiSizeSection (clampedUiSize * SectionHeaderScale) — the Settings-tab
// group-header size. There is no separate "header font size" setting; the header simply scales off the
// same clamped UI font size everything else in the chrome uses, at a fixed 1.25x so it stays visually
// distinct from body/label text.
//
// QA3-1 defensive hardening (hostile-review escalation): ReassertWindowFont re-asserts the CURRENT chrome
// font as LOCAL values on a Window, in ADDITION to the DynamicResource binding declared in each window's
// XAML. A live process-boundary hostile review reported the DynamicResource losing to later theme
// machinery in a real windowed process on 5/6 relaunch cycles; an exhaustive live re-trace by the fix
// author (19 relaunch cycles spanning pre-seeded and real Settings-UI-driven flows, Dark and Light startup
// themes, fresh-default and repeated-edit histories) could not reproduce it. Root cause is therefore NOT
// CONFIRMED here — this reassert is insurance against the theorized mechanism, not a proven fix for it.
// Apply(...) calls it for every currently-open window (the live-edit path — MainWindow plus any open
// dialog); App.axaml.cs calls it again at Window.Opened for the startup path (no window is open yet when
// the startup Apply(...) runs, so Apply's own reassert loop is a no-op at that point).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

namespace HoardPad.App.Settings;

/// <summary>
/// Writes the FEAT-1 font resource keys (<c>Brand.Font.Code</c>/<c>CodeSize</c>/<c>Ui</c>/<c>UiSize</c>,
/// plus the QA4-18-derived <c>Brand.Font.UiSizeSection</c>) into <see cref="Application.Resources"/>. The
/// single writer shared by the live-edit path (<c>SettingsView.ApplyLiveFonts</c>) and the startup
/// re-apply path (<c>App.axaml.cs</c>, QA3-1).
/// </summary>
internal static class SettingsFontApplier
{
    /// <summary>QA4-18: the Settings-tab group-header size is this fixed multiple of the clamped UI font size.</summary>
    private const double SectionHeaderScale = 1.25;

    /// <summary>Apply the font fields of a persisted <see cref="HoardPadSettings"/> (startup path, QA3-1).</summary>
    public static void Apply(Application app, HoardPadSettings settings) =>
        Apply(app, settings.EditorFontFamily, settings.EditorFontSize, settings.UiFontFamily, settings.UiFontSize);

    /// <summary>
    /// Apply explicit font values (the live-edit path, where the VM's already-clamped properties are the
    /// source). Clamps sizes to the documented bounds regardless of the caller, so neither call site can
    /// push an out-of-range value into <see cref="Application.Resources"/>.
    /// </summary>
    public static void Apply(
        Application app,
        string? editorFontFamily,
        int editorFontSize,
        string? uiFontFamily,
        int uiFontSize)
    {
        if (app.Resources is null) return;

        int clampedEditorSize = Math.Clamp(
            editorFontSize, HoardPadSettings.MinEditorFontSize, HoardPadSettings.MaxEditorFontSize);
        int clampedUiSize = Math.Clamp(
            uiFontSize, HoardPadSettings.MinUiFontSize, HoardPadSettings.MaxUiFontSize);

        app.Resources["Brand.Font.Code"] = new FontFamily(
            string.IsNullOrWhiteSpace(editorFontFamily) ? HoardPadSettings.DefaultCodeFontFallback : editorFontFamily);
        app.Resources["Brand.Font.CodeSize"] = (double)clampedEditorSize;

        app.Resources["Brand.Font.Ui"] = new FontFamily(
            string.IsNullOrWhiteSpace(uiFontFamily) ? HoardPadSettings.DefaultUiFontFallback : uiFontFamily);
        app.Resources["Brand.Font.UiSize"] = (double)clampedUiSize;
        app.Resources["Brand.Font.UiSizeSection"] = clampedUiSize * SectionHeaderScale;

        // Defensive hardening (see class remarks): reassert on every currently-open window, not just via
        // the DynamicResource. A no-op at startup (no window is open yet); covers the live-edit path.
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Window openWindow in desktop.Windows)
                ReassertWindowFont(openWindow);
        }
    }

    /// <summary>
    /// Re-assert the current <c>Brand.Font.Ui</c>/<c>Brand.Font.UiSize</c> resource values as LOCAL
    /// FontFamily/FontSize values on <paramref name="window"/> (see class remarks — defensive hardening,
    /// not a proven fix). A no-op if either resource is missing or not yet the expected type.
    /// </summary>
    public static void ReassertWindowFont(Window window)
    {
        Application? app = Application.Current;
        if (app?.Resources is null) return;

        if (app.Resources.TryGetValue("Brand.Font.Ui", out object? family) && family is FontFamily ff)
            window.FontFamily = ff;
        if (app.Resources.TryGetValue("Brand.Font.UiSize", out object? size) && size is double d)
            window.FontSize = d;
    }
}
