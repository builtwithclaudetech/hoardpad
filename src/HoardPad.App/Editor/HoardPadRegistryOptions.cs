// HoardPadRegistryOptions.cs — the FR-7 custom IRegistryOptions overlay (architecture §6 FR-7, TD §5.6).
//
// TextMateSharp.Grammars 2.0.4 ships ~64 stock grammars covering all 20 v1 languages EXCEPT TOML
// (empirically: GetLanguageByExtension(".toml") returns null; "toml" is not a stock language id).
// Rust IS in the stock set (id "rust", scope "source.rust", ext ".rs") — so the only genuinely
// missing supplemental grammar is TOML (Rule 2/3: add only what is actually missing).
//
// This wrapper delegates every IRegistryOptions member to an inner stock RegistryOptions, but:
//   - GetGrammar("source.toml") returns the bundled supplemental grammar (lazily parsed on first
//     request — FR-7 lazy per-language registration, the SC-1 enabling mechanism, arch §3.3/§7.2),
//   - ResolveScopeForExtension(".toml") returns "source.toml" even though the stock options do not
//     know that extension.
// All other scopes/extensions fall through to the stock options unchanged.

using System.Collections.Generic;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace HoardPad.App.Editor;

/// <summary>
/// The FR-7 registry overlay. Wraps a stock <see cref="RegistryOptions"/> and adds the one
/// supplemental grammar the stock set lacks (TOML), parsed lazily on first use. The editor view
/// installs this against AvaloniaEdit; <see cref="ResolveScopeForExtension"/> drives the lazy
/// per-language grammar selection (the stock <c>GetLanguageByExtension</c> cannot see TOML).
/// </summary>
internal sealed class HoardPadRegistryOptions : IRegistryOptions
{
    /// <summary>The TextMate scope of the supplemental TOML grammar (matches the .tmLanguage.json scopeName).</summary>
    internal const string TomlScope = "source.toml";

    private readonly RegistryOptions _stock;
    private readonly object _gate = new();

    // The supplemental grammar is parsed on first GetGrammar("source.toml") — never at construction
    // (FR-7 lazy registration). Cached after the first parse so repeated tokenize passes are free.
    private IRawGrammar? _tomlGrammar;

    public HoardPadRegistryOptions(ThemeName initialTheme)
    {
        _stock = new RegistryOptions(initialTheme);
    }

    /// <summary>Load a stock bundled TextMate theme (delegates to the inner stock options).</summary>
    public IRawTheme LoadTheme(ThemeName name) => _stock.LoadTheme(name);

    /// <summary>
    /// Resolve a file extension (leading dot, e.g. ".toml") to its TextMate scope, or null if no
    /// grammar covers it. The supplemental TOML extension is handled here; everything else delegates
    /// to the stock options. This is the lazy-registration entry point the editor calls per open file.
    /// </summary>
    public string? ResolveScopeForExtension(string extensionWithDot)
    {
        if (string.Equals(extensionWithDot, ".toml", System.StringComparison.OrdinalIgnoreCase))
            return TomlScope;

        Language? language = _stock.GetLanguageByExtension(extensionWithDot);
        return language is null ? null : _stock.GetScopeByLanguageId(language.Id);
    }

    // ----- IRegistryOptions (the 4-member contract AvaloniaEdit's TextMate installation consumes). -----

    public IRawGrammar GetGrammar(string scopeName)
    {
        if (scopeName == TomlScope)
            return GetOrLoadTomlGrammar();

        return _stock.GetGrammar(scopeName);
    }

    public ICollection<string> GetInjections(string scopeName) => _stock.GetInjections(scopeName);

    public IRawTheme GetTheme(string scopeName) => _stock.GetTheme(scopeName);

    public IRawTheme GetDefaultTheme() => _stock.GetDefaultTheme();

    private IRawGrammar GetOrLoadTomlGrammar()
    {
        lock (_gate)
        {
            // Double-checked: the first request parses the embedded .tmLanguage.json; later
            // requests reuse the cached IRawGrammar (the SC-2 warm path pays nothing).
            _tomlGrammar ??= SupplementalGrammars.LoadToml();
            return _tomlGrammar;
        }
    }
}
