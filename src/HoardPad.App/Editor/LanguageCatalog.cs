// LanguageCatalog.cs — extension -> (TextMate scope, status-bar label) for FR-7/FR-8. Wraps the custom
// HoardPadRegistryOptions (which knows the supplemental TOML grammar the stock set lacks). The grammar is
// still registered lazily by the editor on first use; this only RESOLVES which scope a file maps to.

using System.Collections.Generic;

namespace HoardPad.App.Editor;

/// <summary>Resolves a file path to its FR-7 grammar scope + FR-8 status-bar language label.</summary>
internal sealed class LanguageCatalog
{
    /// <summary>
    /// QA2-13: one entry per manually-selectable language — the canonical (label, TextMate scope) pairs
    /// the Language menu lists, "Plain Text" first. This is the SAME 20 FR-7 languages + label spelling
    /// as <c>SettingsViewModel.LanguageOptions</c> (QA2-12's default-new-doc-language dropdown) and the
    /// SC-3 fixtures (<c>Sc3Fixtures.cs</c>) — one canonical label/scope source both surfaces read from,
    /// per the QA2-12 note ("same language catalog should feed QA2-12's dropdown"). A null scope means
    /// Plain Text (no grammar). QA3-4 Fix 1: <see cref="Resolve"/> now looks a recognized scope up in
    /// this table FIRST, so the label auto-detected on open/restore matches the Language menu's checkmark
    /// exactly; <see cref="LabelFor"/> is only a fallback for a scope outside this 20-language catalog
    /// (the stock TextMate set recognizes far more extensions than the manually-selectable menu lists).
    /// </summary>
    public static IReadOnlyList<(string Label, string? Scope)> AvailableLanguages { get; } =
    [
        ("Plain Text", null),
        ("Markdown", "text.html.markdown"),
        ("HTML", "text.html.derivative"),
        ("JavaScript", "source.js"),
        ("CSS", "source.css"),
        ("Python", "source.python"),
        ("C#", "source.cs"),
        ("XML", "text.xml"),
        ("JSON", "source.json"),
        ("SQL", "source.sql"),
        ("Rust", "source.rust"),
        ("TypeScript", "source.ts"),
        ("YAML", "source.yaml"),
        ("TOML", "source.toml"),
        ("Shell/Bash", "source.shell"),
        ("PowerShell", "source.powershell"),
        ("Dockerfile", "source.dockerfile"),
        ("C", "source.c"),
        ("C++", "source.cpp"),
        ("Java", "source.java"),
        ("Go", "source.go"),
    ];

    /// <summary>
    /// FEAT-2/FEAT-8: the default Save-As extension (no leading dot) for each <see cref="AvailableLanguages"/>
    /// label — the reverse direction of <see cref="Resolve"/> (extension -> label). Any label absent here
    /// (including "Plain Text") falls back to "txt" in <see cref="DefaultExtensionFor"/>. Dockerfile has no
    /// conventional extension, so it maps to the empty string — the Save dialog then forces no suffix,
    /// letting the user name the file "Dockerfile" verbatim.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultExtensionByLabel =
        new(System.StringComparer.Ordinal)
        {
            ["Markdown"] = "md",
            ["HTML"] = "html",
            ["JavaScript"] = "js",
            ["CSS"] = "css",
            ["Python"] = "py",
            ["C#"] = "cs",
            ["XML"] = "xml",
            ["JSON"] = "json",
            ["SQL"] = "sql",
            ["Rust"] = "rs",
            ["TypeScript"] = "ts",
            ["YAML"] = "yaml",
            ["TOML"] = "toml",
            ["Shell/Bash"] = "sh",
            ["PowerShell"] = "ps1",
            ["Dockerfile"] = "",
            ["C"] = "c",
            ["C++"] = "cpp",
            ["Java"] = "java",
            ["Go"] = "go",
        };

    /// <summary>
    /// FEAT-2: the default Save-As picker extension (no dot) for a status-bar language label — e.g.
    /// "Python" -> "py". Falls back to "txt" for "Plain Text" or any label outside
    /// <see cref="AvailableLanguages"/>, never leaving a Save-As dialog with no sane default.
    /// </summary>
    public static string DefaultExtensionFor(string label) =>
        DefaultExtensionByLabel.TryGetValue(label, out string? extension) ? extension : "txt";

    private readonly HoardPadRegistryOptions _registry;

    public LanguageCatalog(HoardPadRegistryOptions registry) => _registry = registry;

    /// <summary>
    /// Resolve a file path's language: the TextMate scope (null = Plain Text) and a display label. QA3-4
    /// Fix 1: the label is the canonical <see cref="AvailableLanguages"/> spelling when the scope is one
    /// of the 20 menu languages (e.g. "text.html.markdown" -> "Markdown", not the scope-derived "Html"),
    /// so <c>LanguageMode</c> reads the same regardless of whether the language was set via the Language
    /// menu or auto-detected on open/restore — the Language menu's checkmark depends on this match. Falls
    /// back to <see cref="LabelFor"/> for a recognized-but-uncataloged scope, or "Plain Text" when no
    /// grammar covers the extension. FR-8 keeps a scratch buffer Plain Text until saved/recognized.
    /// </summary>
    public (string? Scope, string Label) Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return (null, "Plain Text");

        string extension = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return (null, "Plain Text");

        string? scope = _registry.ResolveScopeForExtension(extension);
        return scope is null ? (null, "Plain Text") : (scope, LabelForScope(scope));
    }

    /// <summary>The canonical <see cref="AvailableLanguages"/> label for <paramref name="scope"/>, or the
    /// scope-derived <see cref="LabelFor"/> fallback when the scope is not one of the 20 menu languages.</summary>
    private static string LabelForScope(string scope)
    {
        foreach ((string label, string? entryScope) in AvailableLanguages)
        {
            if (string.Equals(entryScope, scope, System.StringComparison.Ordinal))
                return label;
        }
        return LabelFor(scope);
    }

    /// <summary>A human-readable label from a TextMate scope ("source.toml" -> "Toml", "text.html.basic" -> "Html").</summary>
    private static string LabelFor(string scope)
    {
        // Scopes are dotted (source.cs, source.rust, source.toml, text.html.markdown). The most
        // informative segment for a label is the second ("source.<lang>"); fall back to the last.
        string[] parts = scope.Split('.');
        string token = parts.Length >= 2 ? parts[1] : parts[^1];
        return token.Length == 0 ? scope : char.ToUpperInvariant(token[0]) + token[1..];
    }
}
