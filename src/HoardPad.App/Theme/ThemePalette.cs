// ThemePalette.cs — the FR-37 theme palette record (architecture §3.7, brand.md §13).
//
// One immutable bundle per theme: the persisted AppTheme it maps to, the cached brand-retinted TextMate
// IRawTheme for the editor (parsed once at startup — never re-parsed per switch), the Avalonia
// ThemeVariant that drives the chrome + Markdown-preview palette (both bound via ThemeDictionaries /
// DynamicResource, so flipping the variant swaps them in lockstep), and the §13 surface brush the editor
// background is set to (AvaloniaEdit.TextMate does NOT auto-apply the theme's editor.background, so
// ThemeService sets it explicitly — architecture §3.7's "editor background" mutation).

using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using HoardPad.App.Settings;
using TextMateSharp.Themes;

namespace HoardPad.App.Theme;

/// <summary>
/// An immutable FR-37 theme palette (architecture §3.7): the editor TextMate <see cref="IRawTheme"/>
/// (brand.md §2, cached at startup), the Avalonia <see cref="ThemeVariant"/> that drives the chrome +
/// preview-palette lockstep, and the §13 surface brush the editor background is set to.
/// </summary>
/// <param name="Theme">The persisted <see cref="AppTheme"/> this palette maps to.</param>
/// <param name="EditorTheme">The brand-retinted TextMate editor theme (parsed once at startup).</param>
/// <param name="Variant">The Avalonia theme variant (drives chrome + preview via ThemeDictionaries).</param>
/// <param name="EditorBackground">The §13 surface brush applied to the editor control's background.</param>
internal sealed record ThemePalette(
    AppTheme Theme,
    IRawTheme EditorTheme,
    ThemeVariant Variant,
    IBrush EditorBackground)
{
    /// <summary>
    /// Build the brand-bound dark palette (the FR-37 first-run default) — One Dark base + brand.md §2 dark
    /// retints, the Dark theme variant, and the §13 dark surface brush. The TextMate theme is parsed once.
    /// </summary>
    public static ThemePalette Dark() => new(
        AppTheme.Dark,
        BrandTextMateTheme.BuildDark(),
        ThemeVariant.Dark,
        SurfaceBrush(AppTheme.Dark));

    /// <summary>
    /// Build the brand-bound light palette — One Light base + brand.md §2 light retints, the Light theme
    /// variant, and the §13 light surface brush. The TextMate theme is parsed once.
    /// </summary>
    public static ThemePalette Light() => new(
        AppTheme.Light,
        BrandTextMateTheme.BuildLight(),
        ThemeVariant.Light,
        SurfaceBrush(AppTheme.Light));

    private static ImmutableSolidColorBrush SurfaceBrush(AppTheme theme) =>
        new(Color.Parse(BrandTextMateTheme.EditorBackgroundHex[theme]));
}
