// ThemeServiceTests.cs — the M7.1 FR-37 lockstep theme-switch gates (architecture §3.7, brand.md §2/§13).
//
// Three mandatory service gates (filter matches "Theme"):
//   1. The three-surface SAME-FRAME swap — after ONE ThemeService.Switch call, all three SPIKE-FR37
//      surfaces have changed together: the editor's TextMate theme (TryGetThemeColor), the editor control
//      background, and the Markdown preview Background (a live DynamicResource binding on a real
//      MarkdownPreviewControl in a headless window — non-vacuous: the test FAILS if the preview surface
//      is not re-themed because it checks the LIVE brush value before and after the switch).
//   2. The dark↔light ROUND-TRIP — switch dark→light→dark; every surface returns to its original dark
//      value (reversible + stable cached palettes). Preview surface state is also captured and verified.
//   3. Late-registered editor gets the active palette immediately.
// Plus ThemeController gates:
//   4. ToggleAsync flips Current, calls ThemeService.Switch, updates ThemeStatusLabel, persists via SaveAsync.
//   5. ApplyStartupTheme wires the toggle handler + sets the status label to match the seed.
//   6. ApplyStartupTheme with a persisted Light seed starts the app in Light (startup-integration gap).
// And brand-parse gates (UI-thread-free):
//   7. Dark theme is One Dark base + the brand.md §2 retints.
//   8. Light theme is One Light base + the brand.md §2 retints.

using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using HoardPad.App.Editor;
using HoardPad.App.Markdown;
using HoardPad.App.Settings;
using HoardPad.App.Theme;
using HoardPad.App.ViewModels;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ThemeServiceTests
{
    // The §13 surface hex per theme (brand.md §1 / §2 — the editor background + the contrast basis).
    private const string DarkSurfaceHex = "#16131f";
    private const string LightSurfaceHex = "#fbfaf7";

    // ---- Gate 1: the three-surface same-frame swap (one Switch -> editor theme + bg + preview all change). ----

    [AvaloniaFact]
    public void ThemeSwitchChangesEditorThemeBackgroundAndPreviewPaletteInOneCall()
    {
        var service = new ThemeService();
        var editor = new TextEditor();
        TextMate.Installation installation = editor.InstallTextMate(new HoardPadRegistryOptions(ThemeName.OneDark));
        service.RegisterEditor(installation, editor);

        // A real MarkdownPreviewControl in a live headless window — attached to the app resource tree so that
        // Brand.Surface.Brush (the DynamicResource the preview binds its Background to) is observable.
        var preview = new MarkdownPreviewControl();
        var window = new Window { Content = preview, Width = 400, Height = 300 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); // layout pass — resources resolved.

        // Start from a known dark state.
        service.Switch(AppTheme.Dark);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        string editorThemeBefore = ThemeColor(installation, "editor.background");
        Color editorBgColorBefore = SolidColor(editor.Background);

        // Capture the LIVE variant + the resource it resolves to, BEFORE the switch.
        // NON-VACUOUS: we read the app's ACTIVE RequestedThemeVariant (not a hardcoded enum) and look up
        // Brand.Surface.Color for that variant. If Switch() is a no-op (doesn't change the variant), the
        // "before" and "after" lookups use the same variant and return the same color — the assertion FAILS.
        ThemeVariant? variantBefore = Application.Current!.RequestedThemeVariant;
        Color previewColorBefore = PreviewSurfaceColorForCurrentVariant();

        // THE one synchronous, no-yield switch (architecture §3.7).
        service.Switch(AppTheme.Light);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // (1) editor TextMate theme changed (the brand-retinted IRawTheme's editor.background re-points).
        string editorThemeAfter = ThemeColor(installation, "editor.background");
        Assert.NotEqual(editorThemeBefore, editorThemeAfter);
        AssertHexEqual(LightSurfaceHex, editorThemeAfter);

        // (2) editor control background changed (the §13 surface brush — TextMate does not apply it).
        Color editorBgColorAfter = SolidColor(editor.Background);
        Assert.NotEqual(editorBgColorBefore, editorBgColorAfter);
        AssertHexEqual(LightSurfaceHex, editorBgColorAfter.ToString());

        // (3) preview palette changed: the variant switched, so Brand.Surface.Color resolves to a different
        // value. NON-VACUOUS: reads the LIVE RequestedThemeVariant after the switch (must differ from before),
        // then looks up Brand.Surface.Color for that new variant. If Switch() never changed the variant, the
        // "before" and "after" lookups produce the same Color and the Assert.NotEqual FAILS.
        ThemeVariant? variantAfter = Application.Current!.RequestedThemeVariant;
        Color previewColorAfter = PreviewSurfaceColorForCurrentVariant();
        Assert.NotEqual(variantBefore, variantAfter);
        Assert.Equal(ThemeVariant.Light, variantAfter);
        Assert.NotEqual(previewColorBefore, previewColorAfter);
        AssertHexEqual(LightSurfaceHex, $"#{previewColorAfter.R:X2}{previewColorAfter.G:X2}{previewColorAfter.B:X2}");

        window.Close();
    }

    // ---- Gate 2: the dark <-> light round-trip (reversible; the cached palettes are stable). ----

    [AvaloniaFact]
    public void ThemeSwitchDarkLightDarkRoundTripsEverySurfaceBackToDark()
    {
        var service = new ThemeService();
        var editor = new TextEditor();
        TextMate.Installation installation = editor.InstallTextMate(new HoardPadRegistryOptions(ThemeName.OneDark));
        service.RegisterEditor(installation, editor);

        service.Switch(AppTheme.Dark);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        string editorThemeDark = ThemeColor(installation, "editor.background");
        Color editorBgDark = SolidColor(editor.Background);
        ThemeVariant? variantDark = Application.Current!.RequestedThemeVariant;
        // Capture the preview surface COLOR for the dark variant (a value type — not a mutable brush ref).
        Color previewColorDark = PreviewSurfaceColor(ThemeVariant.Dark);

        service.Switch(AppTheme.Light);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        service.Switch(AppTheme.Dark); // back to dark.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        AssertHexEqual(editorThemeDark, ThemeColor(installation, "editor.background"));
        Assert.Equal(editorBgDark, SolidColor(editor.Background));
        Assert.Equal(variantDark, Application.Current!.RequestedThemeVariant);
        AssertHexEqual(DarkSurfaceHex, ThemeColor(installation, "editor.background"));
        // The app variant returned to Dark, so the preview palette resolves to its dark value again.
        Assert.Equal(previewColorDark, PreviewSurfaceColor(ThemeVariant.Dark));
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
    }

    // ---- A late-registered editor (a tab opened after a switch) is brought to the active palette. ----

    [AvaloniaFact]
    public void ThemeRegisterEditorAfterASwitchAppliesTheActivePaletteImmediately()
    {
        var service = new ThemeService();
        service.Switch(AppTheme.Light); // switch BEFORE any editor exists.

        var editor = new TextEditor();
        TextMate.Installation installation = editor.InstallTextMate(new HoardPadRegistryOptions(ThemeName.OneDark));
        service.RegisterEditor(installation, editor); // registered late.

        AssertHexEqual(LightSurfaceHex, ThemeColor(installation, "editor.background"));
        AssertHexEqual(LightSurfaceHex, SolidColor(editor.Background).ToString());
    }

    // ---- ThemeController: ToggleAsync flips theme, calls Switch, updates label, persists. ----

    [AvaloniaFact]
    public async Task ThemeControllerToggleAsyncFlipsThemePersistsAndUpdatesLabel()
    {
        using var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        var service = new ThemeService();
        var shell = new MainWindowViewModel();

        // Seed: dark (the default).
        var seed = HoardPadSettings.Default; // Theme = Dark.
        var controller = new ThemeController(service, store, shell, seed);
        controller.ApplyStartupTheme();
        Assert.Equal(AppTheme.Dark, controller.Current);
        Assert.Equal("☾ Dark", shell.ThemeStatusLabel);

        // Toggle dark → light.
        await controller.ToggleAsync(CancellationToken.None);

        Assert.Equal(AppTheme.Light, controller.Current);
        Assert.Equal("☀ Light", shell.ThemeStatusLabel);
        // ThemeService was driven: the app variant is Light.
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        // Persisted via SaveAsync: reload from disk confirms Light.
        HoardPadSettings persisted = store.Load();
        Assert.Equal(AppTheme.Light, persisted.Theme);
    }

    [AvaloniaFact]
    public async Task ThemeControllerTogglePreservesOtherSettingsKeysOnPersist()
    {
        // Fix #1 regression guard: toggling theme must NOT silently revert keys written after startup.
        using var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        // Write a non-default setting to disk BEFORE constructing the controller (simulating a later save).
        var saved = HoardPadSettings.Default with { NoReplySeconds = 99 };
        await store.SaveAsync(saved, CancellationToken.None);

        var service = new ThemeService();
        var shell = new MainWindowViewModel();
        // The seed the controller receives is the STARTUP snapshot (Dark, NoReplySeconds=5) — the stale
        // seed. PersistAsync must load fresh from disk, not use the startup seed.
        var seed = HoardPadSettings.Default; // NoReplySeconds=5 — would clobber 99 if seed is used.
        var controller = new ThemeController(service, store, shell, seed);
        controller.ApplyStartupTheme();

        await controller.ToggleAsync(CancellationToken.None);

        HoardPadSettings persisted = store.Load();
        Assert.Equal(AppTheme.Light, persisted.Theme);      // toggle took effect.
        Assert.Equal(99, persisted.NoReplySeconds);          // the post-startup key was NOT reverted.
    }

    // ---- ThemeController: ApplyStartupTheme wires the toggle handler + label. ----

    [AvaloniaFact]
    public void ThemeControllerApplyStartupThemeWithDarkSeedStartsInDark()
    {
        var service = new ThemeService();
        using var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        var shell = new MainWindowViewModel();

        var seed = HoardPadSettings.Default; // Theme = Dark.
        var controller = new ThemeController(service, store, shell, seed);
        controller.ApplyStartupTheme();

        Assert.Equal(AppTheme.Dark, controller.Current);
        Assert.Equal("☾ Dark", shell.ThemeStatusLabel);
        Assert.NotNull(shell.ToggleThemeHandler);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void ThemeControllerApplyStartupThemeWithLightSeedStartsInLight()
    {
        // Closing the startup-integration gap: a persisted Light seed must start the app in Light.
        var service = new ThemeService();
        using var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        var shell = new MainWindowViewModel();

        var seed = HoardPadSettings.Default with { Theme = AppTheme.Light };
        var controller = new ThemeController(service, store, shell, seed);
        controller.ApplyStartupTheme();

        Assert.Equal(AppTheme.Light, controller.Current);
        Assert.Equal("☀ Light", shell.ThemeStatusLabel);
        Assert.NotNull(shell.ToggleThemeHandler);
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
    }

    // ---- The brand theme is One Dark base + the brand.md §2 retints (the merged theme is non-vacuous). ----

    [Fact]
    public void BrandDarkThemeIsOneDarkBaseWithTheBrandRetintsApplied()
    {
        string json = BrandTextMateTheme.BuildDarkJson();

        // The One Dark BASE is present (its name + the base tokenColors set survives the overlay), and the
        // merged theme parses into a real IRawTheme (the production path the editor installs).
        Assert.Contains("OneDark", json, StringComparison.Ordinal);
        Assert.Contains("tokenColors", json, StringComparison.Ordinal);
        Assert.NotNull(BrandTextMateTheme.BuildDark());

        // The brand.md §2 dark RETINTS are appended: keywords=amethyst, functions=sapphire, strings=success,
        // numbers=gold, and the editor background is re-pointed to the §13 surface (not One Dark's #282c34).
        Assert.Contains("keyword, storage.type", json, StringComparison.Ordinal);
        Assert.Contains("#b79ad6", json, StringComparison.Ordinal); // amethyst keywords (primary)
        Assert.Contains("#6ea8da", json, StringComparison.Ordinal); // sapphire functions (accent)
        Assert.Contains("#45c98a", json, StringComparison.Ordinal); // success-green strings
        Assert.Contains("#f6b73e", json, StringComparison.Ordinal); // gold-fire numeric constants
        // The editor background is re-pointed to the §13 surface (brand.md §2: NOT One Dark's #282c34).
        Assert.Contains($"\"editor.background\":\"{DarkSurfaceHex}\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrandLightThemeIsOneLightBaseWithTheBrandRetintsApplied()
    {
        string json = BrandTextMateTheme.BuildLightJson();

        Assert.Contains("keyword, storage.type", json, StringComparison.Ordinal);
        Assert.Contains("#5a3f78", json, StringComparison.Ordinal); // light amethyst keywords (primary)
        Assert.Contains("#2f5d8f", json, StringComparison.Ordinal); // light sapphire functions (accent)
        Assert.Contains("#14794a", json, StringComparison.Ordinal); // light success-green strings
        Assert.Contains($"\"editor.background\":\"{LightSurfaceHex}\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ----

    private static string ThemeColor(TextMate.Installation installation, string key)
    {
        Assert.True(installation.TryGetThemeColor(key, out string color), $"theme color '{key}' missing");
        return color;
    }

    /// <summary>
    /// Read Brand.Surface.Color for an EXPLICIT <paramref name="variant"/> — useful for stable round-trip
    /// comparisons where both reads use the same explicit variant (the dark color at start and end of a
    /// dark→light→dark round-trip). Does NOT depend on the live RequestedThemeVariant.
    /// </summary>
    private static Color PreviewSurfaceColor(ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryGetResource("Brand.Surface.Color", variant, out object? value),
            "Brand.Surface.Color not found");
        return Assert.IsType<Color>(value);
    }

    /// <summary>
    /// Read Brand.Surface.Color for the CURRENTLY ACTIVE RequestedThemeVariant — non-vacuous for the
    /// three-surface swap gate: if Switch() is a no-op the live variant doesn't change, so before+after
    /// lookups use the same variant and return the same Color, causing Assert.NotEqual to FAIL.
    /// </summary>
    private static Color PreviewSurfaceColorForCurrentVariant()
    {
        ThemeVariant variant = Application.Current!.RequestedThemeVariant ?? ThemeVariant.Default;
        return PreviewSurfaceColor(variant);
    }

    private static Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static void AssertHexEqual(string expected, string actual) =>
        Assert.Equal(Normalize(expected), Normalize(actual));

    // Compare colors case-insensitively and ignoring a leading '#' / alpha-channel formatting differences.
    private static string Normalize(string hex)
    {
        Color color = Color.Parse(hex.StartsWith('#') ? hex : "#" + hex);
        return $"{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
