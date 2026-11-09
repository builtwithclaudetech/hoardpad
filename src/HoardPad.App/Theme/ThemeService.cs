// ThemeService.cs — the FR-37 lockstep theme switch (architecture §3.7, SPIKE-FR37, brand.md §13).
//
// Switch(palette) performs ALL FOUR mutations in ONE synchronous UI-thread call with NO yield, so the
// dispatcher's next render frame never observes a partial state (architecture §3.7):
//   1. TextMate SetTheme on every registered editor installation (the brand-retinted IRawTheme, brand.md §2),
//   2. the editor control background swap (AvaloniaEdit.TextMate does NOT auto-apply editor.background),
//   3. the Markdown preview-palette swap — driven by the variant change in (4): the preview binds the §13
//      brushes via DynamicResource against the ThemeDictionaries, so flipping the variant re-points them,
//   4. Application.Current.RequestedThemeVariant (the Fluent chrome + the §13 ThemeDictionaries).
// The fourth surface (the Fluent chrome) may lag one frame on heavy resource trees — the acknowledged,
// non-blocking residual (architecture §3.7); the three SPIKE-FR37-captured surfaces are same-frame.
//
// The two palettes (each with its cached IRawTheme) are built once at construction — never re-parsed per
// switch (architecture §3.7 / SPIKE-FR37 §Q4). Editor surfaces register their installation on TextMate
// install and unregister on detach; a newly-registered editor is brought to the active palette immediately
// so a tab opened after a switch is themed correctly.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using HoardPad.App.Settings;

namespace HoardPad.App.Theme;

/// <summary>
/// The FR-37 lockstep theme switch (architecture §3.7). One synchronous UI-thread <see cref="Switch"/>
/// re-themes the editor (TextMate + background), the Markdown preview, and the Fluent chrome together.
/// Singleton; the cached palettes (each with its brand-retinted <see cref="TextMateSharp.Themes.IRawTheme"/>)
/// are built once at construction.
/// </summary>
internal sealed class ThemeService
{
    /// <summary>
    /// The app-singleton instance, set by the composition root at startup. Editor surfaces (created by XAML,
    /// not DI) self-register against this on TextMate install; null in the design-time / no-app path.
    /// </summary>
    public static ThemeService? Current { get; internal set; }

    private readonly ThemePalette _dark;
    private readonly ThemePalette _light;

    // The registered editor surfaces (installation + control). A set keyed by the installation so a
    // re-install or duplicate registration does not double-apply. Editor surfaces add/remove themselves.
    private readonly Dictionary<TextMate.Installation, TextEditor> _editors = new();

    public ThemeService()
    {
        _dark = ThemePalette.Dark();
        _light = ThemePalette.Light();
        ActivePalette = _dark; // FR-37 first-run default is Dark (brand.md §0); Switch overrides at startup.
    }

    /// <summary>The currently-applied palette (FR-37 default Dark until <see cref="Switch"/> is called).</summary>
    public ThemePalette ActivePalette { get; private set; }

    /// <summary>The palette for a persisted <see cref="AppTheme"/> selection (the two cached instances).</summary>
    public ThemePalette PaletteFor(AppTheme theme) => theme == AppTheme.Light ? _light : _dark;

    /// <summary>
    /// Register an editor surface so it follows theme switches (FR-37). Called when the
    /// <see cref="TextMate.Installation"/> is installed; the editor is brought to the active palette
    /// immediately so a tab opened after a switch is correctly themed. UI-thread only.
    /// </summary>
    public void RegisterEditor(TextMate.Installation installation, TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(editor);
        _editors[installation] = editor;
        ApplyToEditor(installation, editor, ActivePalette);
    }

    /// <summary>Unregister an editor surface on detach so a disposed installation is not re-themed (no leak).</summary>
    public void UnregisterEditor(TextMate.Installation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        _editors.Remove(installation);
    }

    /// <summary>
    /// The ONE synchronous, no-yield FR-37 switch (architecture §3.7). Applies <paramref name="palette"/>'s
    /// TextMate theme + editor background to every registered editor and sets
    /// <see cref="Application.RequestedThemeVariant"/> (chrome + preview lockstep) — all in this call, with
    /// no <c>await</c> / <c>Dispatcher.Post</c> between the steps. MUST run on the UI thread.
    /// </summary>
    public void Switch(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ActivePalette = palette;

        // (1) + (2): every registered editor — TextMate theme then control background, no yield.
        foreach (KeyValuePair<TextMate.Installation, TextEditor> entry in _editors)
            ApplyToEditor(entry.Key, entry.Value, palette);

        // (3) + (4): the chrome + the Markdown preview both bind the §13 ThemeDictionaries via
        // DynamicResource, so this single variant assignment swaps both in lockstep (architecture §3.7).
        if (Application.Current is { } app)
            app.RequestedThemeVariant = palette.Variant;
    }

    /// <summary>Switch to the palette for a persisted <see cref="AppTheme"/> (the menu / startup entry point).</summary>
    public void Switch(AppTheme theme) => Switch(PaletteFor(theme));

    private static void ApplyToEditor(TextMate.Installation installation, TextEditor editor, ThemePalette palette)
    {
        installation.SetTheme(palette.EditorTheme);   // (1) syntax theme (brand.md §2 retints).
        editor.Background = palette.EditorBackground; // (2) the §13 surface (TextMate does not apply it).
    }
}
