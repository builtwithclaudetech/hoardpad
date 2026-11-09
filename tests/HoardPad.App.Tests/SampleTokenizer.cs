// SampleTokenizer.cs — SC-3 read-back plumbing (M9.2). Tokenizes a multi-line sample through a loaded
// TextMate IGrammar and resolves a marked region (a substring occurrence) to the scope stack of the token
// covering the START of that substring.
//
// TextMateSharp tokenizes ONE LINE at a time and returns per-line token spans (StartIndex/EndIndex are
// column offsets WITHIN that line, plus a Scopes stack). To resolve a substring-marked region we: split the
// source on '\n' (matching how the editor feeds lines), find the substring's absolute offset, translate it
// to (lineIndex, columnInLine), then pick the token on that line whose [StartIndex, EndIndex) covers the
// column. The region is marked at its FIRST character, which always falls inside exactly one token.

using System;
using System.Collections.Generic;
using System.Linq;
using TextMateSharp.Grammars;

namespace HoardPad.App.Tests;

/// <summary>The per-line tokenization of a fixture sample, with region -> scope-stack resolution.</summary>
internal sealed class TokenizedSample
{
    // One entry per source line: the line text + its tokens (column-relative spans + scope stacks).
    private readonly IReadOnlyList<(string Line, IToken[] Tokens)> _lines;

    public TokenizedSample(IReadOnlyList<(string Line, IToken[] Tokens)> lines) => _lines = lines;

    /// <summary>Every token scope stack across every line (used for the no-plaintext-fallback check).</summary>
    public IEnumerable<IReadOnlyList<string>> AllTokenStacks =>
        _lines.SelectMany(l => l.Tokens).Select(t => (IReadOnlyList<string>)t.Scopes);

    /// <summary>
    /// The scope stack of the token covering the start of a marked region, located by substring occurrence.
    /// Throws (a test failure) if the needle/occurrence is not present — a fixture-authoring mistake.
    /// </summary>
    public IReadOnlyList<string> ScopeStackAt(string source, MarkedRegion region)
    {
        int absOffset = NthIndexOf(source, region.Needle, region.Occurrence);
        if (absOffset < 0)
            throw new InvalidOperationException(
                $"Fixture region not found: occurrence #{region.Occurrence} of '{region.Needle}'.");

        (int lineIndex, int columnInLine) = ToLineColumn(source, absOffset);
        if (lineIndex >= _lines.Count)
            throw new InvalidOperationException($"Region offset {absOffset} fell past the tokenized lines.");

        IToken[] tokens = _lines[lineIndex].Tokens;
        IToken? covering = tokens.FirstOrDefault(t => columnInLine >= t.StartIndex && columnInLine < t.EndIndex);
        if (covering is null)
            throw new InvalidOperationException(
                $"No token covers column {columnInLine} on line {lineIndex} for region '{region.Needle}'.");

        return covering.Scopes;
    }

    private static int NthIndexOf(string haystack, string needle, int occurrence)
    {
        int idx = -1;
        for (int n = 0; n < occurrence; n++)
        {
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
            if (idx < 0)
                return -1;
        }
        return idx;
    }

    private static (int LineIndex, int Column) ToLineColumn(string source, int absOffset)
    {
        int lineStart = 0;
        int lineIndex = 0;
        for (int i = 0; i < absOffset; i++)
        {
            if (source[i] == '\n')
            {
                lineIndex++;
                lineStart = i + 1;
            }
        }
        return (lineIndex, absOffset - lineStart);
    }
}

/// <summary>Tokenizes a fixture sample line-by-line through a loaded grammar (the SC-3 headless read-back).</summary>
internal static class SampleTokenizer
{
    public static TokenizedSample Tokenize(IGrammar grammar, string source)
    {
        // Split on '\n' and strip a trailing '\r' so the column math matches the editor's per-line feed.
        // A trailing '\n' yields a final empty line; harmless (it tokenizes to zero/one empty token).
        string[] rawLines = source.Split('\n');
        var lines = new List<(string Line, IToken[] Tokens)>(rawLines.Length);
        foreach (string raw in rawLines)
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            ITokenizeLineResult result = grammar.TokenizeLine(line);
            lines.Add((line, result.Tokens));
        }
        return new TokenizedSample(lines);
    }
}
