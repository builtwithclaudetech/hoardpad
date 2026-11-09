// BrandTextMateTheme.cs — builds the FR-37 brand-matched TextMate editor themes (architecture §3.7,
// brand.md §2). One Dark / One Light is the adopted base scheme (decision 2); a small set of token
// scopes is RETINTED toward the brand jewel/gold palette (brand.md §2 "Retinted syntax slots"),
// and the editor background/foreground are re-pointed to the §13 surface/on-surface roles. Everything
// not retinted inherits the adopted One Dark / One Light value unchanged.
//
// MECHANISM: we read the bundled One Dark / One Light theme JSON straight out of the
// TextMateSharp.Grammars assembly (the SAME embedded resource RegistryOptions.LoadTheme parses), append
// the brand retint entries to its "tokenColors" array (TextMate scope resolution is last-match-wins, so
// the appended retints win over the base) and override the four "colors" editor keys, then parse the
// merged JSON via ThemeReader.ReadThemeSync into a cached IRawTheme. Reading the base out of the
// assembly keeps the full One Dark token set without re-typing it here — only the retints live here.
//
// The resulting IRawTheme is parsed ONCE at startup (cached on the ThemePalette) — never re-parsed per
// switch (architecture §3.7 / SPIKE-FR37 §Q4).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using HoardPad.App.Settings;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Themes;

namespace HoardPad.App.Theme;

/// <summary>
/// Builds the brand-retinted TextMate editor themes (brand.md §2): the adopted One Dark / One Light base
/// with the jewel/gold retint slots overlaid and the editor background/foreground re-pointed to the §13
/// surface/on-surface roles. Each <see cref="IRawTheme"/> is parsed once and cached on a
/// <see cref="ThemePalette"/>.
/// </summary>
internal static class BrandTextMateTheme
{
    // The bundled base themes (the SAME embedded resources RegistryOptions.LoadTheme reads). Matched by
    // suffix so a packaging-side resource-name change does not silently break the overlay.
    private const string OneDarkResourceSuffix = "Resources.Themes.onedark-color-theme.json";
    private const string OneLightResourceSuffix = "Resources.Themes.atom-one-light-color-theme.json";

    /// <summary>Build the brand-retinted dark editor theme (One Dark base + brand.md §2 dark retints).</summary>
    public static IRawTheme BuildDark() => BuildOverlay(OneDarkResourceSuffix, DarkRetints, DarkEditorColors);

    /// <summary>Build the brand-retinted light editor theme (One Light base + brand.md §2 light retints).</summary>
    public static IRawTheme BuildLight() => BuildOverlay(OneLightResourceSuffix, LightRetints, LightEditorColors);

    // ----- brand.md §2 "Retinted syntax slots" (the ONLY slots re-pointed; everything else inherits). -----
    // Each entry is (TextMate scope selector, retint hex). Appended AFTER the base tokenColors so the
    // last-match-wins TextMate resolution gives the retint precedence over the One Dark / One Light value.

    private static readonly (string Scope, string Color)[] DarkRetints =
    [
        ("keyword, storage.type", "#b79ad6"),                                  // keywords = amethyst (primary)
        ("entity.name.function, support.function", "#6ea8da"),                 // functions = sapphire (accent)
        ("entity.name.type, entity.name.class, support.type", "#5cc0cf"),      // types/classes = teal
        ("string, string.quoted", "#45c98a"),                                  // strings = positive-green (success)
        ("constant.numeric, constant.language", "#f6b73e"),                    // numbers/literals = gold-fire (the one gold touch)
        ("comment", "#6b6680"),                                                // comments = muted-soft (≥3:1 floor)
    ];

    private static readonly (string Scope, string Color)[] LightRetints =
    [
        ("keyword, storage.type", "#5a3f78"),                                  // keywords = amethyst (primary)
        ("entity.name.function, support.function", "#2f5d8f"),                 // functions = sapphire (accent)
        ("entity.name.type, entity.name.class, support.type", "#2f7d8c"),      // types/classes = teal
        ("string, string.quoted", "#14794a"),                                  // strings = positive-green (success)
        ("constant.numeric, constant.language", "#9a5a00"),                    // numbers/literals = gold-ink
        ("comment", "#8c8899"),                                                // comments = muted-soft darkened (3.30:1)
    ];

    // The "colors" editor keys overridden per brand.md §2: the editor background is HoardPad surface (§13),
    // NOT One Dark's #282c34; foreground is on-surface; selection + active-line band bind the §13 roles.
    private static readonly (string Key, string Color)[] DarkEditorColors =
    [
        ("editor.background", "#16131f"),              // §13 surface (the contrast basis for the retints)
        ("editor.foreground", "#f2effa"),             // §13 on-surface
        ("editor.selectionBackground", "#3a3358"),    // §13 selection
        ("editor.lineHighlightBackground", "#1f1b2e"), // active-line band (one step off surface)
    ];

    private static readonly (string Key, string Color)[] LightEditorColors =
    [
        ("editor.background", "#fbfaf7"),              // §13 surface
        ("editor.foreground", "#1b1a22"),             // §13 on-surface
        ("editor.selectionBackground", "#d8d0ea"),    // §13 selection
        ("editor.lineHighlightBackground", "#f1eef7"), // active-line band
    ];

    private static IRawTheme BuildOverlay(
        string baseResourceSuffix,
        (string Scope, string Color)[] retints,
        (string Key, string Color)[] editorColors)
    {
        // Parse the merged theme into an IRawTheme via TextMateSharp's reader (the same path
        // RegistryOptions.LoadTheme uses — it wants a StreamReader). Parsed once at startup; cached on the
        // ThemePalette.
        byte[] json = System.Text.Encoding.UTF8.GetBytes(BuildOverlayJson(baseResourceSuffix, retints, editorColors));
        using var stream = new MemoryStream(json);
        using var reader = new StreamReader(stream);
        return ThemeReader.ReadThemeSync(reader);
    }

    /// <summary>
    /// Build the merged brand-theme JSON (One Dark / One Light base + the brand.md §2 retints appended +
    /// the editor "colors" overrides). Exposed internally so the M7.1 tests assert the retint slots are
    /// present without depending on the fragile <see cref="IRawTheme"/> introspection API.
    /// </summary>
    internal static string BuildDarkJson() => BuildOverlayJson(OneDarkResourceSuffix, DarkRetints, DarkEditorColors);

    /// <summary>The light merged brand-theme JSON (see <see cref="BuildDarkJson"/>).</summary>
    internal static string BuildLightJson() => BuildOverlayJson(OneLightResourceSuffix, LightRetints, LightEditorColors);

    private static string BuildOverlayJson(
        string baseResourceSuffix,
        (string Scope, string Color)[] retints,
        (string Key, string Color)[] editorColors)
    {
        JsonObject root = ReadBaseTheme(baseResourceSuffix);

        // Override the editor "colors" keys (surface/on-surface/selection/line-highlight, brand.md §2).
        var colors = root["colors"] as JsonObject ?? new JsonObject();
        foreach ((string key, string color) in editorColors)
            colors[key] = color;
        root["colors"] = colors;

        // Append the retint tokenColors AFTER the base set (last-match-wins overlay, brand.md §2).
        var tokenColors = root["tokenColors"] as JsonArray ?? new JsonArray();
        foreach ((string scope, string color) in retints)
            tokenColors.Add(new JsonObject
            {
                ["scope"] = scope,
                ["settings"] = new JsonObject { ["foreground"] = color },
            });
        root["tokenColors"] = tokenColors;

        return root.ToJsonString();
    }

    private static JsonObject ReadBaseTheme(string resourceSuffix)
    {
        Assembly grammars = typeof(RegistryOptions).Assembly;
        string resourceName = grammars.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceSuffix, System.StringComparison.Ordinal))
            ?? throw new FileNotFoundException(
                $"Bundled base TextMate theme resource ending '{resourceSuffix}' was not found.");

        using Stream stream = grammars.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Could not open base theme resource '{resourceName}'.");

        JsonNode? node = JsonNode.Parse(stream);
        return node as JsonObject
            ?? throw new InvalidDataException($"Base theme resource '{resourceName}' is not a JSON object.");
    }

    /// <summary>The §13 surface hex per <see cref="AppTheme"/> (the editor background brush + the assert basis).</summary>
    public static IReadOnlyDictionary<AppTheme, string> EditorBackgroundHex { get; } =
        new Dictionary<AppTheme, string>
        {
            [AppTheme.Dark] = "#16131f",   // §13 surface (dark)
            [AppTheme.Light] = "#fbfaf7",  // §13 surface (light)
        };
}
