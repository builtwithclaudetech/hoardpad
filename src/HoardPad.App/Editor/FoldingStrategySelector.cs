// FoldingStrategySelector.cs — the FR-9 strategy selector (architecture §3.3, §6 FR-9 row). Chooses
// brace-folding vs indentation-folding BY TextMate scope, with the binding scope-null FALLBACK to
// indentation folding (architecture §3.3: FR-7 registers grammars lazily on first use, so a recompute can
// run before the grammar's first async tokenize resolves a scope — the selector must then fall back to
// indentation folding, never mis-fold or throw).
//
// Brace languages (curly-brace block structure): C / C++ / C# / Java / JS / TS / Rust / Go / JSON / PHP /
// CSS / SCSS / LESS — their TextMate scopes are matched by prefix. Everything else — including Python,
// YAML, TOML, Makefile, Dockerfile, Markdown, plain text, and ANY null/unresolved scope — uses indentation
// folding. This is a pure, table-driven decision over the scope string; the chosen strategy is then run by
// the FoldingService over the immutable FoldSnapshot.

using System;
using System.Collections.Generic;
using AvaloniaEdit.Folding;

namespace HoardPad.App.Editor;

/// <summary>Whether brace or indentation folding applies to the active document's TextMate scope (FR-9).</summary>
internal enum FoldingKind
{
    /// <summary>Indentation-structured (Python/YAML/TOML/…) OR an unresolved/null scope (the §3.3 fallback).</summary>
    Indentation,

    /// <summary>Curly-brace-structured (C-family / JS / TS / Rust / Go / JSON / …).</summary>
    Brace,
}

/// <summary>
/// The FR-9 <c>FoldingStrategySelector</c>: maps a TextMate scope to its <see cref="FoldingKind"/> and runs
/// the chosen strategy over an immutable <see cref="FoldSnapshot"/>. A null/empty/unrecognized scope falls
/// back to <see cref="FoldingKind.Indentation"/> (architecture §3.3 lazy-grammar fallback).
/// </summary>
internal static class FoldingStrategySelector
{
    // Brace-structured TextMate scope prefixes. Matched by prefix so language variants (e.g. "source.ts",
    // "source.tsx", "source.css.scss") resolve to brace without enumerating every leaf scope.
    private static readonly string[] BraceScopePrefixes =
    [
        "source.c",      // c, cpp (source.cpp), c# (source.cs), css (source.css), scss (source.css.scss)
        "source.objc",   // objective-c / objective-c++
        "source.java",   // java (source.java), javascript (source.js)
        "source.js",
        "source.ts",
        "source.rust",
        "source.go",
        "source.json",
        "source.php",
        "source.less",
    ];

    /// <summary>
    /// Choose the folding kind for a TextMate scope. A null/empty scope, or any scope not recognized as
    /// brace-structured, yields <see cref="FoldingKind.Indentation"/> (the §3.3 scope-null fallback).
    /// </summary>
    public static FoldingKind KindFor(string? scope)
    {
        if (string.IsNullOrEmpty(scope))
            return FoldingKind.Indentation;

        foreach (string prefix in BraceScopePrefixes)
        {
            if (scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return FoldingKind.Brace;
        }

        return FoldingKind.Indentation;
    }

    /// <summary>
    /// Compute fold regions for <paramref name="snapshot"/> using the strategy chosen for
    /// <paramref name="scope"/> (<see cref="KindFor"/>). Pure over the immutable snapshot — safe to run off
    /// the UI thread (architecture §3.3).
    /// </summary>
    public static List<NewFolding> ComputeFoldings(FoldSnapshot snapshot, string? scope) =>
        KindFor(scope) == FoldingKind.Brace
            ? BraceFoldingStrategy.ComputeFoldings(snapshot)
            : IndentationFoldingStrategy.ComputeFoldings(snapshot);
}
