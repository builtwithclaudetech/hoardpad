// SandboxGateE2ETests.cs — the production PathResolverSandboxGate end-to-end gate (M9.3, carried
// forward from M4.3 Obj 4).
//
// The M4.3 service tests could only FAKE the ISandboxGate (NormcasedPath's factory is internal to
// Core, mintable only by Host). This gate drives the REAL canonicalize->evaluate chain on real
// filesystem inputs — the production PathResolver.ResolveCanonical (libc realpath + normcase) feeding
// the production SandboxPolicy.Evaluate — and asserts the FR-26 containment property:
//
//   - a SYMLINK-escape candidate (an in-root symlink pointing OUTSIDE the root) is DENIED — realpath
//     resolves the link to its outside target, which the policy rejects;
//   - a `..`-TRAVERSAL candidate that climbs out of the root is DENIED;
//   - a WRONG-ROOT candidate (a real sibling directory of the root) is DENIED;
//   - an in-root DESCENDANT is ALLOWED, and the canonical resolved value is returned (not the raw input).
//
// This is the chokepoint the whole sandbox rests on; faking it would not prove FR-26.

using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Safety;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.AcceptanceHarness;

public sealed class SandboxGateE2ETests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly PathResolver _resolver = new();
    private readonly PathResolverSandboxGate _gate;

    public SandboxGateE2ETests()
    {
        _base = Path.Combine(Path.GetTempPath(), $"hoardpad-gate-{Guid.NewGuid():N}");
        _root = Path.Combine(_base, "open-folder");
        Directory.CreateDirectory(_root);

        NormcasedPath resolvedRoot = _resolver.ResolveCanonical(_root, _root);
        _gate = new PathResolverSandboxGate(_resolver, resolvedRoot, _root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void InRootDescendantIsAllowedAndReturnsTheCanonicalResolvedValue()
    {
        string descendant = Path.Combine(_root, "sub", "file.txt");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        SandboxResolution res = _gate.Resolve(descendant);

        Assert.Equal(SandboxDecision.Allow, res.Decision);
        // The resolved value is the canonical realpath form (under the real, symlink-resolved root) —
        // not the raw input. On Linux that means the path under realpath(_root).
        string canonicalRoot = _resolver.ResolveCanonical(_root, _root).Value;
        Assert.StartsWith(canonicalRoot, res.ResolvedPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("sub", "file.txt"), res.ResolvedPath, StringComparison.Ordinal);
    }

    [Fact]
    public void RootItselfIsAllowed()
    {
        SandboxResolution res = _gate.Resolve(_root);
        Assert.Equal(SandboxDecision.Allow, res.Decision);
    }

    [Fact]
    public void ParentTraversalClimbingOutOfTheRootIsDenied()
    {
        // A candidate that traverses OUT of the root with `..`. GetFullPath collapses it to a path
        // OUTSIDE the root, which the policy denies.
        string traversal = Path.Combine(_root, "..", "escape.txt");
        SandboxResolution res = _gate.Resolve(traversal);
        Assert.Equal(SandboxDecision.Deny, res.Decision);
    }

    [Fact]
    public void WrongRootARealSiblingDirectoryIsDenied()
    {
        // A real sibling of the open-folder root (e.g. _base/other/secret.txt). It is a valid, existing
        // path — just OUTSIDE the sandbox root — so containment denies it.
        string sibling = Path.Combine(_base, "other");
        Directory.CreateDirectory(sibling);
        string target = Path.Combine(sibling, "secret.txt");

        SandboxResolution res = _gate.Resolve(target);
        Assert.Equal(SandboxDecision.Deny, res.Decision);
    }

    [Fact]
    public void SymlinkEscapeAnInRootLinkPointingOutsideTheRootIsDenied()
    {
        // The load-bearing FR-26 case: an in-root symlink whose target is OUTSIDE the root. realpath
        // resolves the link to its outside target, so a write THROUGH the link escapes the sandbox and
        // must be denied. A naive (non-realpath) prefix check would WRONGLY allow it (the raw path starts
        // with the root). This proves the production resolver actually resolves symlinks.
        string outsideDir = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outsideDir);

        // Create an in-root symlink "link" -> the outside directory.
        string linkPath = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(linkPath, outsideDir);

        // A write through the in-root link to a file in the OUTSIDE directory.
        string throughLink = Path.Combine(linkPath, "escaped-via-symlink.txt");

        SandboxResolution res = _gate.Resolve(throughLink);
        Assert.Equal(SandboxDecision.Deny, res.Decision);
        // The resolved value points OUTSIDE the root (realpath followed the link).
        string canonicalRoot = _resolver.ResolveCanonical(_root, _root).Value;
        Assert.False(res.ResolvedPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "the resolved symlink target must point OUTSIDE the root (realpath followed the link).");
    }

    [Fact]
    public void InRootSymlinkPointingToAnInRootTargetIsAllowed()
    {
        // The counter-case proving the symlink-deny above is real resolution, not a deny-everything bug:
        // an in-root symlink whose target is ALSO in-root resolves to an in-root path and is ALLOWED.
        string inRootTargetDir = Path.Combine(_root, "real-sub");
        Directory.CreateDirectory(inRootTargetDir);
        string linkPath = Path.Combine(_root, "in-link");
        Directory.CreateSymbolicLink(linkPath, inRootTargetDir);

        string throughLink = Path.Combine(linkPath, "ok.txt");
        SandboxResolution res = _gate.Resolve(throughLink);
        Assert.Equal(SandboxDecision.Allow, res.Decision);
    }
}
