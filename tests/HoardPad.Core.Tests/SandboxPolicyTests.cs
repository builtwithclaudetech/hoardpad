using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Safety;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M1.2 tests for the SAFETY-CRITICAL FR-26 containment decision <see cref="SandboxPolicy.Evaluate"/>.
/// Every trap named in the M1.2 brief is asserted: in-root allow, out-of-root deny, root-itself allow,
/// the prefix-trap (both directions), trailing-separator equivalence, a Windows normcased pair, the
/// pre-resolved symlink pair, the defensive <c>..</c> deny, and the <c>default(NormcasedPath)</c> guard.
/// The policy has ZERO I/O — these tests stand up no host.
/// </summary>
public sealed class SandboxPolicyTests
{
    private static NormcasedPath P(string resolvedNormcased) => NormcasedPath.FromResolved(resolvedNormcased);

    // ── Core allow / deny ─────────────────────────────────────────────────────

    [Theory]
    // In-root descendant (POSIX).
    [InlineData("/project", "/project/file.txt", SandboxDecision.Allow)]
    [InlineData("/project", "/project/sub/dir/file.txt", SandboxDecision.Allow)]
    // The root itself is in-sandbox.
    [InlineData("/project", "/project", SandboxDecision.Allow)]
    // Out-of-root deny — sibling and ancestor.
    [InlineData("/project", "/other/file.txt", SandboxDecision.Deny)]
    [InlineData("/project", "/", SandboxDecision.Deny)]
    [InlineData("/project/sub", "/project/file.txt", SandboxDecision.Deny)]
    public void EvaluateBasicContainment(string root, string candidate, SandboxDecision expected) =>
        Assert.Equal(expected, SandboxPolicy.Evaluate(P(root), P(candidate)));

    // ── Prefix-trap (BOTH directions): a separator boundary, not StartsWith ───

    [Fact]
    public void EvaluatePrefixTrapLongerSiblingOutsideShorterRootDenies()
    {
        // "/project" must NOT be inside "/proj" — raw StartsWith("/proj") would wrongly Allow it.
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P("/proj"), P("/project")));
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P("/proj"), P("/project/file.txt")));
    }

    [Fact]
    public void EvaluatePrefixTrapShorterSiblingOutsideLongerRootDenies()
    {
        // The reverse direction: "/proj" is not inside "/project".
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P("/project"), P("/proj")));
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P("/project"), P("/proj/file.txt")));
    }

    // ── Trailing-separator equivalence on the root ────────────────────────────

    [Theory]
    [InlineData("/project/", "/project", SandboxDecision.Allow)]          // root==candidate, root has trailing /
    [InlineData("/project/", "/project/file.txt", SandboxDecision.Allow)] // descendant, root has trailing /
    [InlineData("/project/", "/project2/file.txt", SandboxDecision.Deny)] // trailing / must not mask the prefix-trap
    [InlineData("/project", "/project/file.txt", SandboxDecision.Allow)]  // same outcome without trailing /
    public void EvaluateTrailingSeparatorOnRootBehavesIdentically(
        string root, string candidate, SandboxDecision expected) =>
        Assert.Equal(expected, SandboxPolicy.Evaluate(P(root), P(candidate)));

    // ── Windows-style normcased pair (separator '\', case already folded by host) ──

    [Theory]
    [InlineData(@"c:\proj", @"c:\proj\file.txt", SandboxDecision.Allow)]
    [InlineData(@"c:\proj", @"c:\proj", SandboxDecision.Allow)]
    [InlineData(@"c:\proj\", @"c:\proj\file.txt", SandboxDecision.Allow)] // trailing backslash on root
    // Windows prefix-trap: "c:\project" is not inside "c:\proj".
    [InlineData(@"c:\proj", @"c:\project", SandboxDecision.Deny)]
    [InlineData(@"c:\proj", @"c:\project\file.txt", SandboxDecision.Deny)]
    [InlineData(@"c:\proj", @"c:\other\file.txt", SandboxDecision.Deny)]
    public void EvaluateWindowsNormcasedPair(string root, string candidate, SandboxDecision expected) =>
        Assert.Equal(expected, SandboxPolicy.Evaluate(P(root), P(candidate)));

    [Fact]
    public void EvaluateNormcasedCaseFoldedByHostNotReFoldedInDomain()
    {
        // The host already case-folded on Windows, so a normcased pair is lowercase. The domain
        // compares ordinally and must NOT mistake a differently-cased value for the same path:
        // an upper-case "C:\PROJ" candidate that the host did NOT fold is, to the domain, a
        // different ordinal string and is correctly out-of-sandbox of normcased root "c:\proj".
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P(@"c:\proj"), P(@"C:\PROJ\file.txt")));
    }

    // ── Pre-resolved symlink pair (host already resolved; policy just prefix-checks) ──

    [Fact]
    public void EvaluatePreResolvedSymlinkInsideRootAllows()
    {
        // Host resolved a symlink "/project/link" → its real target "/project/real/file.txt"
        // (still under root). The policy sees only the resolved candidate and Allows it.
        Assert.Equal(SandboxDecision.Allow,
            SandboxPolicy.Evaluate(P("/project"), P("/project/real/file.txt")));
    }

    [Fact]
    public void EvaluatePreResolvedSymlinkEscapingRootDenies()
    {
        // Host resolved a symlink "/project/escape" → real target "/etc/passwd" (outside root).
        // The policy sees the resolved out-of-root candidate and Denies it.
        Assert.Equal(SandboxDecision.Deny,
            SandboxPolicy.Evaluate(P("/project"), P("/etc/passwd")));
    }

    // ── Defensive: a non-canonical candidate with a '..' segment fails closed ──

    [Theory]
    [InlineData("/project", "/project/../etc/passwd")]   // escapes via parent
    [InlineData("/project", "/project/sub/../../other")] // escapes via chained parent
    [InlineData("/project", "/project/..")]              // parent of root
    [InlineData("/project", "/project/sub/..")]          // would resolve back to root, still denied
    public void EvaluateNonCanonicalCandidateWithParentSegmentDenies(string root, string candidate) =>
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P(root), P(candidate)));

    [Fact]
    public void EvaluateDotDotAsSubstringButNotASegmentIsNotTreatedAsParent()
    {
        // "my..file" is a legitimate filename containing ".." as a substring, NOT a parent segment.
        Assert.Equal(SandboxDecision.Allow,
            SandboxPolicy.Evaluate(P("/project"), P("/project/my..file")));
    }

    // ── default(NormcasedPath) guard ──────────────────────────────────────────

    [Fact]
    public void EvaluateDefaultRootThrowsArgumentException()
    {
        // An unresolved root is a host contract violation (resolver never ran), not an op to deny.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SandboxPolicy.Evaluate(default, P("/project/file.txt")));
        Assert.Equal("root", ex.ParamName);
    }

    [Fact]
    public void EvaluateDefaultCandidateDeniesNotAllows()
    {
        // A default candidate must fail closed — never silently Allow into the sandbox.
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P("/project"), default));
    }

    // ── Degenerate root guard: bare-separator roots must never Allow the whole volume ──

    [Theory]
    [InlineData("/")]     // POSIX filesystem root — no-trim (length 1), degenerate
    [InlineData("//")]    // double-slash root — trims to "/" (length 1), degenerate
    [InlineData("\\")]    // Windows bare separator — degenerate
    [InlineData("\\\\")]  // Windows double-separator — trims to "\" (length 1), degenerate
    public void EvaluateDegenerateRootThrowsArgumentException(string degenerateRoot)
    {
        // A root that normalizes to a bare separator would contain every absolute path on the
        // volume. The policy must reject it rather than silently Allow-the-world.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SandboxPolicy.Evaluate(P(degenerateRoot), P("/etc/passwd")));
        Assert.Equal("root", ex.ParamName);
    }

    // ── UNC root paths (Windows network shares) ───────────────────────────────

    [Theory]
    [InlineData(@"\\server\share", @"\\server\share\file.txt", SandboxDecision.Allow)]
    [InlineData(@"\\server\share", @"\\server\share", SandboxDecision.Allow)]        // root itself
    [InlineData(@"\\server\share\", @"\\server\share\file.txt", SandboxDecision.Allow)] // trailing backslash on root
    // UNC prefix-trap: "\\server\shareEVIL" is not inside "\\server\share".
    [InlineData(@"\\server\share", @"\\server\shareEVIL", SandboxDecision.Deny)]
    [InlineData(@"\\server\share", @"\\server\shareEVIL\file.txt", SandboxDecision.Deny)]
    [InlineData(@"\\server\share", @"\\server\other\file.txt", SandboxDecision.Deny)]
    public void EvaluateUncRootPair(string root, string candidate, SandboxDecision expected) =>
        Assert.Equal(expected, SandboxPolicy.Evaluate(P(root), P(candidate)));

    // ── Mixed-separator candidate against a single-style root — fails closed ──

    [Fact]
    public void EvaluateMixedSeparatorCandidateDenies()
    {
        // The host guarantees a single, consistent separator style (POSIX / or Windows \).
        // A cross-style pair is therefore a host contract deviation and is never a legitimate
        // in-sandbox path; the ordinal comparison correctly Denies because the string values
        // do not share a prefix. Pinning this as fail-closed-by-design, not fail-closed-by-accident.
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P(@"\a\b"), P("/a/b/x")));
        Assert.Equal(SandboxDecision.Deny, SandboxPolicy.Evaluate(P("/a/b"), P(@"\a\b\x")));
    }

    // ── Benign candidate non-canonicality: '.' segments and doubled separators ──

    [Theory]
    // A '.' segment in the candidate is benign — it resolves inside the sandbox and cannot escape.
    [InlineData("/project", "/project/./x")]
    // A doubled separator in the candidate is benign — still clearly inside root.
    [InlineData("/project", "/project//x")]
    public void EvaluateBenignCandidateNonCanonicalityAllows(string root, string candidate)
    {
        // Candidate canonicality is tolerated: a '.' segment or doubled separator cannot push the
        // path outside the sandbox root (no parent traversal). ROOT canonicality is load-bearing
        // (the degenerate-root guard above enforces it); candidate canonicality is best-effort
        // (the host should deliver canonical candidates, but a minor deviation stays in-sandbox).
        Assert.Equal(SandboxDecision.Allow, SandboxPolicy.Evaluate(P(root), P(candidate)));
    }
}
