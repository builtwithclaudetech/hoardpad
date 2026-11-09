using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Core.Safety;

/// <summary>
/// PURE FR-26 sandbox containment decision (architecture §1.2, TD §2.2). No syscalls, no
/// <c>System.IO</c>, no canonicalization — decides whether an ALREADY-RESOLVED, normcased
/// <paramref name="candidate"/> path is at-or-under an ALREADY-RESOLVED, normcased
/// <paramref name="root"/>.
/// </summary>
/// <remarks>
/// <para>
/// The host (<see cref="IPathResolver"/>, M2.2) resolves symlinks, makes absolute, expands
/// Windows 8.3 short names, and normcases — then passes the resolved pair in. This policy does
/// the segment-boundary prefix check only; it NEVER touches the filesystem (that would defeat
/// the seam the whole design rests on).
/// </para>
/// <para>
/// Comparison is <see cref="StringComparison.Ordinal"/> over the normcased <see cref="NormcasedPath.Value"/>s.
/// Case folding is the host's job (no-op on Linux; case-fold on Windows, SPIKE-4) — the domain
/// must NOT re-apply OS case rules, or it would either double-fold or diverge from the host.
/// </para>
/// </remarks>
public static class SandboxPolicy
{
    // A normcased path uses the OS-native separator, but a candidate could in principle carry
    // either, so both are treated as path-segment boundaries for the containment check.
    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>
    /// Decides whether <paramref name="candidate"/> is the sandbox root itself or a descendant of it.
    /// </summary>
    /// <param name="root">
    /// The resolved, normcased open-folder root. MUST satisfy the full host-resolver precondition:
    /// non-default, non-degenerate (not a bare filesystem root such as <c>/</c>, <c>//</c>,
    /// <c>\</c>, or <c>\\</c>), single consistent separator style (POSIX uses <c>/</c>, Windows
    /// uses <c>\</c>), no <c>..</c> segments, and already case-folded. Violations are
    /// host contract failures — they throw <see cref="ArgumentException"/> rather than silently
    /// denying or allowing, because a bad root affects every subsequent containment decision.
    /// </param>
    /// <param name="candidate">
    /// The resolved, normcased path the AI wants to write. The host should deliver a canonical,
    /// single-separator-style candidate; however, candidate canonicality is tolerated (a benign
    /// <c>.</c> segment or doubled separator can't escape the sandbox). A <c>..</c> segment in
    /// the candidate — which the host should never produce — fails closed to
    /// <see cref="SandboxDecision.Deny"/> defensively. A mixed-separator candidate (e.g.
    /// <c>/</c> separators against a <c>\</c>-style root) also Denies, which is correct: if the
    /// host delivers a consistent separator style the mixed case never occurs legitimately.
    /// </param>
    /// <returns>
    /// <see cref="SandboxDecision.Allow"/> iff <paramref name="candidate"/> equals the root or is a
    /// strict descendant of it (segment-boundary–aware); otherwise <see cref="SandboxDecision.Deny"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> is <c>default(NormcasedPath)</c>, or normalizes to an empty string
    /// or a bare path-separator (<c>/</c>, <c>\</c>) — any of these is a degenerate root that
    /// would grant containment to the entire filesystem volume. The host resolver must never
    /// emit such a value as an open-folder sandbox root.
    /// </exception>
    public static SandboxDecision Evaluate(NormcasedPath root, NormcasedPath candidate)
    {
        // Unresolved root = the resolver never produced it. This is a programming error, not a
        // file the AI proposed, so it must not read as "deny this one op" — fail loud.
        if (root.IsDefault)
            throw new ArgumentException(
                "SandboxPolicy.Evaluate requires a resolved root; default(NormcasedPath) means IPathResolver never ran.",
                nameof(root));

        // Unresolved candidate = nothing to contain. Fail closed.
        if (candidate.IsDefault)
            return SandboxDecision.Deny;

        string rootValue = root.Value;
        string candidateValue = candidate.Value;

        // Defensive: inputs are contractually canonical (the host resolved away every '..'), so a
        // surviving '..' segment means the contract was violated upstream. Never trust a prefix
        // check over a non-canonical candidate — fail closed.
        if (HasParentSegment(candidateValue))
            return SandboxDecision.Deny;

        // Trailing-separator normalization: "/project" and "/project/" (and the '\' forms) must
        // behave identically. Strip ONE trailing separator, but never the whole root (e.g. "/").
        string normRoot = TrimTrailingSeparator(rootValue);

        // Degenerate-root guard: a root that normalizes to a single separator (e.g. "/", "\") or
        // to empty represents the entire filesystem volume — the descendant check would Allow
        // every absolute path. A HoardPad sandbox root is always a resolved open-folder directory,
        // never the filesystem root, so this is always a host contract violation. Fail loud here
        // even though the default(NormcasedPath) guard above already handles the zero-value case,
        // because a host could legally produce a non-default NormcasedPath with value "/" and pass
        // it in (the resolver stub still returns Placeholder values). Better to catch it at the
        // policy boundary than discover it as a wrong-Allow in production.
        if (normRoot.Length == 0 || (normRoot.Length == 1 && IsSeparator(normRoot[0])))
            throw new ArgumentException(
                "SandboxPolicy.Evaluate requires a non-degenerate root; a root that normalizes to a bare "
                + "separator (\"/\", \"\\\", \"//\", \"\\\\\") grants containment to the entire filesystem volume. "
                + "IPathResolver must never emit a bare-separator path as the open-folder sandbox root.",
                nameof(root));

        // The root itself is in-sandbox.
        if (string.Equals(candidateValue, normRoot, StringComparison.Ordinal))
            return SandboxDecision.Allow;

        // Descendant: candidate must start with the root AND the very next character must be a
        // path separator. The separator boundary is what defeats the prefix-trap — "/project" is
        // NOT inside "/proj" because the char after "/proj" in "/project" is 'e', not a separator.
        if (candidateValue.Length > normRoot.Length
            && candidateValue.StartsWith(normRoot, StringComparison.Ordinal)
            && IsSeparator(candidateValue[normRoot.Length]))
            return SandboxDecision.Allow;

        return SandboxDecision.Deny;
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static string TrimTrailingSeparator(string path) =>
        path.Length > 1 && IsSeparator(path[^1]) ? path[..^1] : path;

    // True if any path segment is exactly "..". A bare-substring "contains '..'" would wrongly
    // flag legitimate names like "my..file"; the segment must be bounded by separators / ends.
    private static bool HasParentSegment(string path)
    {
        foreach (string segment in path.Split(Separators))
            if (segment == "..")
                return true;
        return false;
    }
}

/// <summary>The FR-26 containment verdict (architecture §1.2, TD §2.2).</summary>
public enum SandboxDecision
{
    /// <summary>The candidate is the sandbox root or a descendant of it.</summary>
    Allow,

    /// <summary>The candidate is outside the sandbox root (the safe, fail-closed default).</summary>
    Deny,
}
