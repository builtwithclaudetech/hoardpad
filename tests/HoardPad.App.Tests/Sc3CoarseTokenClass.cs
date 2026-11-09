// Sc3CoarseTokenClass.cs — the SC-3 (PRD §5 SC-3 / FR-7 / A-6) coarse scope-stack -> token-class mapper.
//
// SC-3's read-back mechanism (B8) is DELIBERATELY coarse: it reduces a TextMate scope stack to one of six
// token classes rather than matching exhaustive per-character scopes. That keeps the 20 fixtures authorable
// and stable across grammar-version bumps (a grammar can rename a leaf scope but the prefix family is stable).
//
// THE MAPPING (the single documented place, per the A-6 brief). A TextMate scope stack is ordered
// root-first, most-specific-last (verified against the live tokenizer, M9.2). We scan the stack from the
// MOST SPECIFIC scope toward the root and return the FIRST scope whose dotted prefix family matches one of
// these standard TextMate conventions:
//
//   keyword     <- keyword.*  | storage.*            (control words, operators-as-keywords, type/modifier storage)
//   string      <- string.*                          (quoted / unquoted string bodies and delimiters within)
//   comment     <- comment.*                          (line + block comments)
//   number      <- constant.numeric.*                 (numeric literals — checked BEFORE the generic identifier family)
//   identifier  <- variable.* | entity.name.* | entity.other.* | support.*   (names: vars, funcs, types, tags, attrs, builtins)
//   punctuation <- punctuation.*                      (brackets, separators, terminators, delimiters)
//
// Most-specific-first is what makes "pick the most specific meaningful scope on the stack" work: e.g. a
// string DELIMITER token carries `... / string.quoted.double / punctuation.definition.string.begin` — the
// most-specific scope is `punctuation.*`, so the delimiter classifies as punctuation while the string BODY
// (whose leaf is `string.quoted.double`) classifies as string. `meta.*` scopes carry no coarse class and are
// skipped (they only ever appear as non-leaf grouping scopes here). A stack with no matching family yields
// null = "no coarse class" (plain text / un-tokenized fallback) — SC-3's no-fallback assertion keys on that.

using System;
using System.Collections.Generic;

namespace HoardPad.App.Tests;

/// <summary>The six coarse SC-3 token classes a TextMate scope stack reduces to (FR-7 / A-6).</summary>
internal enum CoarseClass
{
    Keyword,
    String,
    Comment,
    Identifier,
    Number,
    Punctuation,
}

/// <summary>Maps a TextMate scope stack to its coarse SC-3 token class (see file header for the mapping).</summary>
internal static class Sc3CoarseTokenClass
{
    /// <summary>
    /// Reduce a scope stack (root-first, most-specific-last — the order the tokenizer returns) to its coarse
    /// class, or null when no scope in the stack belongs to a classable family ("no coarse class" / fallback).
    /// Scans most-specific-first so the most specific meaningful scope wins.
    /// </summary>
    public static CoarseClass? Classify(IReadOnlyList<string> scopeStack)
    {
        for (int i = scopeStack.Count - 1; i >= 0; i--)
        {
            CoarseClass? cls = ClassifyScope(scopeStack[i]);
            if (cls is not null)
                return cls;
        }
        return null;
    }

    /// <summary>Classify a single dotted scope by its prefix family, or null if it belongs to no family.</summary>
    private static CoarseClass? ClassifyScope(string scope)
    {
        // number is a SUBSET of constant.* — test it before the generic identifier/constant families.
        if (HasFamily(scope, "constant.numeric"))
            return CoarseClass.Number;
        if (HasFamily(scope, "keyword") || HasFamily(scope, "storage"))
            return CoarseClass.Keyword;
        if (HasFamily(scope, "string"))
            return CoarseClass.String;
        if (HasFamily(scope, "comment"))
            return CoarseClass.Comment;
        if (HasFamily(scope, "punctuation"))
            return CoarseClass.Punctuation;
        if (HasFamily(scope, "variable")
            || HasFamily(scope, "entity.name")
            || HasFamily(scope, "entity.other")
            || HasFamily(scope, "support"))
            return CoarseClass.Identifier;
        return null;
    }

    /// <summary>True when <paramref name="scope"/> equals <paramref name="family"/> or begins "<paramref name="family"/>.".</summary>
    private static bool HasFamily(string scope, string family) =>
        scope.Equals(family, StringComparison.Ordinal)
        || scope.StartsWith(family + ".", StringComparison.Ordinal);
}
