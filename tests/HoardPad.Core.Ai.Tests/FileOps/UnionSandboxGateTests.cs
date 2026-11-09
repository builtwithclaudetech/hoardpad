using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Safety;
using Xunit;

namespace HoardPad.Core.Ai.Tests.FileOps;

/// <summary>
/// feature-ai-multidoc-panel §5.4 / FR-MD-1 / FR-MD-2 / FR-MD-10 — the additive Archetype-B
/// <see cref="UnionSandboxGate"/>. Every test names the mutation it kills so the suite is non-vacuous
/// (§6 AC-MD-5). No test mints a <see cref="HoardPad.Core.Host.Abstractions.NormcasedPath"/>; the inner
/// gate is faked to prove the UNION admits only the exact grant, independent of the root leg.
/// </summary>
public sealed class UnionSandboxGateTests
{
    /// <summary>
    /// An inner root gate that DENIES every candidate and echoes the raw path back as the resolved path
    /// (the tests supply already-clean paths). Using a deny-everything root proves that any Allow the
    /// <see cref="UnionSandboxGate"/> returns comes SOLELY from an exact grant match, not from the root leg.
    /// </summary>
    private sealed class DenyAllGate : ISandboxGate
    {
        public SandboxResolution Resolve(string rawCandidatePath) =>
            new(SandboxDecision.Deny, rawCandidatePath);
    }

    /// <summary>
    /// An inner root gate that ALLOWS every candidate (echoing the raw path) — used only to prove the
    /// root leg is returned unchanged regardless of grants (FR-MD-1).
    /// </summary>
    private sealed class AllowAllGate : ISandboxGate
    {
        public SandboxResolution Resolve(string rawCandidatePath) =>
            new(SandboxDecision.Allow, rawCandidatePath);
    }

    private const string Grant = "/a/b/c.txt";

    private static readonly string[] BlankGrants = { "", "   " };

    private static UnionSandboxGate WithGrant(ISandboxGate root, params string[] grants) =>
        new(root, FileGrantSet.FromResolvedPaths(grants));

    // ---- AC-MD-5 granularity (non-vacuous) ------------------------------------------------------------

    // Non-vacuity: this Allow is the positive leg. If the membership check were removed the gate would just
    // return the root's Deny, and this assertion would fail — so it proves the grant leg is live.
    [Fact]
    public void ResolveExactGrantedPathIsAllowed()
    {
        UnionSandboxGate gate = WithGrant(new DenyAllGate(), Grant);

        SandboxResolution r = gate.Resolve(Grant);

        Assert.Equal(SandboxDecision.Allow, r.Decision);
        Assert.Equal(Grant, r.ResolvedPath); // the resolved path is preserved for the activity log
    }

    // Non-vacuity: a sibling in the SAME directory. Widening the grant match to StartsWith(dir) or granting
    // the parent folder would Allow this — the Deny assertion kills that mutation (FR-MD-2 no-folder-scope).
    [Fact]
    public void ResolveSiblingInGrantedDirIsRefused()
    {
        UnionSandboxGate gate = WithGrant(new DenyAllGate(), Grant);

        Assert.Equal(SandboxDecision.Deny, gate.Resolve("/a/b/other.txt").Decision);
    }

    // Non-vacuity: the prefix trap. "/a/b/c.txt.bak" and "/a/b/c.txt2" both share the grant string as a
    // prefix. Only an EXACT ordinal equality refuses them; a StartsWith(grant) mutation would Allow them.
    [Theory]
    [InlineData("/a/b/c.txt.bak")]
    [InlineData("/a/b/c.txt2")]
    public void ResolvePrefixSharingNeighborIsRefused(string candidate)
    {
        UnionSandboxGate gate = WithGrant(new DenyAllGate(), Grant);

        Assert.Equal(SandboxDecision.Deny, gate.Resolve(candidate).Decision);
    }

    // Non-vacuity: the granted document's directory itself (a directory-create target). A folder-prefix
    // grant would Allow this; exact-equality refuses it (FR-MD-2 — never the parent folder).
    [Fact]
    public void ResolveGrantedDirAsDirectoryCreateIsRefused()
    {
        UnionSandboxGate gate = WithGrant(new DenyAllGate(), Grant);

        Assert.Equal(SandboxDecision.Deny, gate.Resolve("/a/b/").Decision);
    }

    // ---- FR-MD-1 root leg unchanged -------------------------------------------------------------------

    // Non-vacuity: when the root gate Allows, the union must return that Allow verbatim regardless of grants
    // (grants add nothing in-root). A mutation that consulted grants first, or altered the resolved path,
    // would fail this.
    [Fact]
    public void ResolveRootAllowIsReturnedUnchangedRegardlessOfGrants()
    {
        const string inRoot = "/root/inside.txt";
        UnionSandboxGate withGrants = WithGrant(new AllowAllGate(), Grant);
        UnionSandboxGate noGrants = new(new AllowAllGate(), FileGrantSet.Empty);

        SandboxResolution withG = withGrants.Resolve(inRoot);
        SandboxResolution noG = noGrants.Resolve(inRoot);

        Assert.Equal(SandboxDecision.Allow, withG.Decision);
        Assert.Equal(inRoot, withG.ResolvedPath);
        Assert.Equal(SandboxDecision.Allow, noG.Decision);
        Assert.Equal(inRoot, noG.ResolvedPath);
    }

    // ---- Membership check is load-bearing (explicit non-vacuity guard) --------------------------------

    // Non-vacuity: the checked path is Allowed AND the unchecked (not-in-grant) out-of-folder path is
    // Refused, in one test over the SAME gate. Removing the membership check makes every out-of-root path
    // pass-through with the root's Deny (both Deny) — the checked-Allow assertion fails. Removing the Deny
    // fallback makes every out-of-root path Allow — the unchecked-Deny assertion fails. Only the exact
    // membership check satisfies both legs (FR-MD-1 / AC-MD-1).
    [Fact]
    public void ResolveCheckedPathAllowedAndUncheckedPathRefused()
    {
        UnionSandboxGate gate = WithGrant(new DenyAllGate(), Grant);

        Assert.Equal(SandboxDecision.Allow, gate.Resolve(Grant).Decision);
        Assert.Equal(SandboxDecision.Deny, gate.Resolve("/x/y/unchecked.txt").Decision);
    }

    // ---- Fail-closed (FR-MD-10) -----------------------------------------------------------------------

    // Non-vacuity: with an empty grant set the gate must be byte-identical to the bare root gate. Any code
    // path that admitted a non-empty union from an empty set would Allow this out-of-root path and fail.
    [Fact]
    public void ResolveEmptyGrantSetDeniesOutOfRootLikeBareRootGate()
    {
        UnionSandboxGate gate = new(new DenyAllGate(), FileGrantSet.Empty);

        SandboxResolution r = gate.Resolve(Grant);

        Assert.Equal(SandboxDecision.Deny, r.Decision);
        Assert.Equal(Grant, r.ResolvedPath); // resolved path still surfaced for the log, unchanged
    }

    // Non-vacuity: a malformed (empty/whitespace) grant entry is dropped and never widens (FR-MD-10). If the
    // factory kept blank entries, a candidate that resolved to "" or " " could match — this proves it can't,
    // and that a real out-of-root path stays Denied when only blanks were supplied.
    [Fact]
    public void ResolveBlankGrantEntriesAreDroppedAndNeverWiden()
    {
        FileGrantSet grants = FileGrantSet.FromResolvedPaths(BlankGrants);
        Assert.True(grants.IsEmpty);

        UnionSandboxGate gate = new(new DenyAllGate(), grants);

        Assert.Equal(SandboxDecision.Deny, gate.Resolve(Grant).Decision);
    }

    // ---- Ordinal equality (FR-MD-5.1) -----------------------------------------------------------------

    // Non-vacuity: the resolver already normcased both sides (no-op on Linux, case-fold on Windows), so
    // equality must be exact-byte ordinal. A grant differing only by case must NOT match a case-differing
    // candidate. A mutation to StringComparer.OrdinalIgnoreCase (or CurrentCulture) would Allow this and
    // fail the assertion — proving the gate + FileGrantSet use StringComparer.Ordinal.
    [Fact]
    public void ResolveCaseDifferingGrantDoesNotMatchOrdinal()
    {
        // Grant is lower-case; candidate differs only by case. Ordinal equality => no match.
        UnionSandboxGate gate = WithGrant(new DenyAllGate(), "/a/b/c.txt");

        Assert.Equal(SandboxDecision.Deny, gate.Resolve("/a/b/C.TXT").Decision);
    }

    // Companion to the ordinal test: the exact-case grant DOES match, so the ordinal test above is refusing
    // on case, not on some unrelated mismatch.
    [Fact]
    public void ResolveExactCaseGrantMatches()
    {
        UnionSandboxGate gate = WithGrant(new DenyAllGate(), "/a/b/c.txt");

        Assert.Equal(SandboxDecision.Allow, gate.Resolve("/a/b/c.txt").Decision);
    }
}
