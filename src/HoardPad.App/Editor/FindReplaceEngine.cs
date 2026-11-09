// FindReplaceEngine.cs — the pure find/replace engine (FR-17 / FR-18). NO UI dependency, fully unit-
// testable in isolation. Compiles a user pattern into a .NET Regex honouring the three FR-18 toggles
// (literal-vs-regex, case-sensitivity, whole-word), enumerates matches over a string (the live match
// count + next/prev navigation), and applies replace / replace-all returning the new text + a count.
//
// PCRE parity (FR-17 Done-when): .NET regex supports capture-group BACKREFERENCES in the pattern
// (\1 / \k<name>) and $1-style group refs in the replacement — the two things the FR-17 backreference
// gate exercises. Literal mode wraps the pattern in Regex.Escape; whole-word wraps it in \b…\b.
//
// INVALID REGEX (graceful, no crash): a bad pattern (e.g. an unbalanced "(") is caught ONCE at compile
// time — the single legitimate try boundary, since the input is untrusted user text. A failed compile
// yields an engine in the IsValid=false state that returns zero matches and performs no replacement, so
// every caller treats "bad pattern" as "no matches" with no exception to handle (house FP error style).

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HoardPad.App.Editor;

/// <summary>The user's find/replace inputs + the three FR-18 toggles (an immutable value).</summary>
internal readonly record struct FindReplaceQuery(
    string Pattern,
    string Replacement,
    bool UseRegex,
    bool CaseSensitive,
    bool WholeWord);

/// <summary>One match's location in a buffer (for the navigation cursor + the live count).</summary>
internal readonly record struct FindMatch(int Index, int Length);

/// <summary>The outcome of a replace-all over one buffer: the new text + how many replacements occurred.</summary>
internal readonly record struct ReplaceResult(string NewText, int ReplacedCount);

/// <summary>
/// A compiled find/replace engine for one <see cref="FindReplaceQuery"/>. Construct via
/// <see cref="Compile"/> — an invalid pattern returns an engine in the <see cref="IsValid"/>=false
/// state (zero matches, no replacement) rather than throwing, so callers never wrap it in try/catch.
/// </summary>
internal sealed class FindReplaceEngine
{
    private readonly Regex? _regex;
    private readonly string _replacement;

    private FindReplaceEngine(Regex? regex, string replacement)
    {
        _regex = regex;
        _replacement = replacement;
    }

    /// <summary>Whether the pattern compiled to a usable regex. An empty pattern is treated as invalid (no matches).</summary>
    public bool IsValid => _regex is not null;

    /// <summary>
    /// Compile a query into an engine. The pattern is escaped (literal mode), word-bounded (whole-word),
    /// and the case toggle maps to <see cref="RegexOptions.IgnoreCase"/>. An empty pattern or a malformed
    /// regex (the one untrusted-input try boundary) yields the IsValid=false engine — never a throw.
    /// </summary>
    public static FindReplaceEngine Compile(FindReplaceQuery query)
    {
        if (string.IsNullOrEmpty(query.Pattern))
            return new FindReplaceEngine(regex: null, query.Replacement);

        string body = query.UseRegex ? query.Pattern : Regex.Escape(query.Pattern);
        if (query.WholeWord)
            body = $@"\b(?:{body})\b";

        RegexOptions options = query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

        try
        {
            // A 1-second match timeout guards a pathological backtracking pattern from hanging the UI
            // (NFR-1 responsiveness); a timeout surfaces as "no further matches", never a crash.
            var regex = new Regex(body, options, System.TimeSpan.FromSeconds(1));
            return new FindReplaceEngine(regex, query.Replacement);
        }
        catch (System.ArgumentException)
        {
            // Malformed pattern (e.g. unbalanced "(") — the untrusted-input boundary. Degrade to the
            // no-match engine so the panel shows "bad pattern / 0 matches" with nothing to catch upstream.
            return new FindReplaceEngine(regex: null, query.Replacement);
        }
    }

    /// <summary>Enumerate every match in <paramref name="text"/> (left-to-right). Empty when the pattern is invalid.</summary>
    public IReadOnlyList<FindMatch> FindAll(string text)
    {
        if (_regex is null || text.Length == 0)
            return System.Array.Empty<FindMatch>();

        var matches = new List<FindMatch>();
        // A bad-pattern engine never reaches here; a match timeout on a pathological pattern degrades to
        // "the matches found so far" rather than throwing into the UI.
        try
        {
            foreach (Match m in _regex.Matches(text))
            {
                // Skip zero-width matches so an empty-match pattern (e.g. "a*") does not loop forever in
                // navigation; only substantive matches drive the count + the next/prev cursor.
                if (m.Length > 0)
                    matches.Add(new FindMatch(m.Index, m.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Keep whatever was found before the timeout — the UI shows a (partial) count, never crashes.
        }

        return matches;
    }

    /// <summary>
    /// Replace ALL substantive (non-zero-width) matches in <paramref name="text"/> with the $1-aware
    /// replacement (FR-17 / FR-18). Operates on the SAME match set as <see cref="FindAll"/> — zero-width
    /// matches are excluded from both, so the Find count and Replace-All mutation count always agree.
    ///
    /// Applies replacements right-to-left so earlier match offsets stay valid after each splice.
    /// </summary>
    public ReplaceResult ReplaceAll(string text)
    {
        if (_regex is null)
            return new ReplaceResult(text, 0);

        // Collect the same substantive (non-zero-width) match set as FindAll so count == mutation count.
        IReadOnlyList<FindMatch> matches = FindAll(text);
        if (matches.Count == 0)
            return new ReplaceResult(text, 0);

        // Apply right-to-left so earlier offsets remain valid after each splice.
        // Re-match each segment so the Regex engine substitutes $1/$2/... group refs per capture.
        var chars = new System.Text.StringBuilder(text);
        int replaced = 0;
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            FindMatch m = matches[i];
            string segment = text.Substring(m.Index, m.Length);
            try
            {
                // ReplaceAll on the isolated single-match segment performs the $-ref substitution
                // for exactly one match (the only occurrence in the segment is the full capture).
                string substituted = _regex.Replace(segment, _replacement);
                chars.Remove(m.Index, m.Length);
                chars.Insert(m.Index, substituted);
                replaced++;
            }
            catch (RegexMatchTimeoutException)
            {
                // Timeout on one segment: skip that replacement, keep accumulating the others.
            }
        }

        return new ReplaceResult(chars.ToString(), replaced);
    }

    /// <summary>Count substantive (non-zero-width) matches in <paramref name="text"/> (the live "M" count).</summary>
    public int CountMatches(string text) => FindAll(text).Count;
}
