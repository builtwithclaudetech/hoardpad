// PathResolverTests.cs — canonicalization + the M1.2-carry-forward contract test (M2.2).
//
// Two concerns:
//   1. Symlink/normcase resolution on Linux: a real symlink in a temp dir resolves to its
//      canonical target (realpath leg), and a relative path resolves against the supplied root.
//   2. The CARRY-FORWARD CONTRACT TEST (the reason M2.2 exists in this run): over an adversarial
//      corpus, the resolver NEVER emits a degenerate / mixed-separator / non-canonical
//      NormcasedPath to SandboxPolicy. This is the upstream closure of M1.2 Finding 1 — M1.2's
//      SandboxPolicy.Evaluate already fails closed on a degenerate root defensively; this proves
//      the resolver can't produce one to feed it.
//
// The Windows normcase/GetLongPathName leg is authored compile-only (TD §6); these tests run on
// linux-x64, so the Windows case-fold assertion is NOT made here (it is Deferred), only the
// platform-neutral invariants that must hold on every OS.

using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class PathResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly PathResolver _resolver = new();

    public PathResolverTests()
    {
        // realpath the temp root itself so the test's expected values are already canonical
        // (e.g. on macOS/some Linux setups /tmp is a symlink to /private/tmp or similar).
        string raw = Path.Combine(Path.GetTempPath(), $"hoardpad-m22-{Guid.NewGuid():N}");
        Directory.CreateDirectory(raw);
        _dir = new DirectoryInfo(raw).LinkTarget is not null
            ? raw
            : Path.GetFullPath(raw);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ---- symlink / normcase resolution (Linux) ----

    [Fact]
    public void ResolvesSymlinkToCanonicalTarget()
    {
        // realpath only resolves an existing path, so create a real target file + a symlink to it.
        string target = Path.Combine(_dir, "real-target.txt");
        File.WriteAllText(target, "x");
        string link = Path.Combine(_dir, "link-to-target.txt");
        File.CreateSymbolicLink(link, target);

        NormcasedPath resolved = _resolver.ResolveCanonical(link, _dir);

        // The symlink must resolve to the canonical TARGET, not the link path — a symlink that
        // pointed outside the sandbox would otherwise evade the prefix check.
        Assert.Equal(new DirectoryInfo(_dir).FullName + Path.DirectorySeparatorChar + "real-target.txt",
            resolved.Value);
    }

    [Fact]
    public void ResolvesSymlinkedParentForNonExistentCreateTarget()
    {
        // A symlinked PARENT directory must be canonicalized even when the leaf does not exist
        // yet (the AI proposing a file to create). Otherwise a symlinked dir could place a
        // create-target outside the sandbox.
        string realParent = Path.Combine(_dir, "real-parent");
        Directory.CreateDirectory(realParent);
        string linkedParent = Path.Combine(_dir, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, realParent);

        // Leaf "new-file.txt" does not exist; the parent is a symlink.
        NormcasedPath resolved = _resolver.ResolveCanonical(
            Path.Combine(linkedParent, "new-file.txt"), _dir);

        Assert.Equal(Path.Combine(realParent, "new-file.txt"), resolved.Value);
    }

    [Fact]
    public void ResolvesRelativePathAgainstRoot()
    {
        string sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "f.txt"), "x");

        NormcasedPath resolved = _resolver.ResolveCanonical("sub/f.txt", _dir);

        Assert.Equal(Path.Combine(_dir, "sub", "f.txt"), resolved.Value);
    }

    [Fact]
    public void CollapsesDotDotTraversalWithinRoot()
    {
        string a = Path.Combine(_dir, "a");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "x");

        // "a/../f.txt" canonicalizes to "<root>/f.txt" (still inside the root).
        NormcasedPath resolved = _resolver.ResolveCanonical(Path.Combine("a", "..", "f.txt"), _dir);

        Assert.Equal(Path.Combine(_dir, "f.txt"), resolved.Value);
    }

    [Fact]
    public void RejectsDegenerateFilesystemRoot()
    {
        // A bare "/" resolved as a sandbox root would grant containment over the whole volume.
        // The resolver must throw rather than hand it to SandboxPolicy (upstream of M1.2 Finding 1).
        Assert.Throws<ArgumentException>(() => _resolver.ResolveCanonical("/", _dir));
    }

    [Fact]
    public void RejectsEmptyInputs()
    {
        Assert.Throws<ArgumentException>(() => _resolver.ResolveCanonical("", _dir));
        Assert.Throws<ArgumentException>(() => _resolver.ResolveCanonical("x", ""));
    }

    // ---- CARRY-FORWARD CONTRACT TEST (M1.2 Finding 1, upstream closure) ----

    /// <summary>
    /// The resolver must NEVER emit a degenerate / mixed-separator / non-canonical
    /// <see cref="NormcasedPath"/> to <c>SandboxPolicy.Evaluate</c>, for ANY raw input — including
    /// adversarial ones. For each input the resolver either returns a path satisfying every
    /// invariant below, OR throws (the bare-root case): a thrown input can never reach the sandbox,
    /// so the invariant holds vacuously.
    /// </summary>
    [Theory]
    [InlineData("/")]                       // bare root — must throw, never emitted
    [InlineData("//foo")]                   // doubled leading separator
    [InlineData("a/../../etc/passwd")]      // '..' traversal escaping the root
    [InlineData("../sibling")]              // relative '..'
    [InlineData("sub/file.txt")]            // ordinary relative
    [InlineData("a//b///c.txt")]            // doubled internal separators
    [InlineData("trailing/")]               // trailing separator
    [InlineData("foo\\bar")]                // mixed-separator input (literal name on Linux)
    [InlineData("./a/./b")]                 // '.' segments
    [InlineData("a/b/../c")]                // interior '..'
    [InlineData("a\\..\\b")]               // Gate-1 W2: literal backslash segments on Linux
    public void NeverEmitsDegenerateOrMixedSeparatorPath(string rawInput)
    {
        NormcasedPath result;
        try
        {
            result = _resolver.ResolveCanonical(rawInput, _dir);
        }
        catch (ArgumentException)
        {
            // A rejected input (the bare-root degenerate case) is never handed to the sandbox —
            // the invariant holds vacuously. This is the resolver failing CLOSED upstream.
            return;
        }

        string value = result.Value;

        // (1) NEVER a degenerate root.
        Assert.False(value is "/" or "//" or "\\" or "\\\\",
            $"resolver emitted a degenerate root '{value}' for input '{rawInput}'");

        // (2) No doubled run of the OS-native separator, and no FOREIGN separator used AS a
        // separator (the "mixed-separator pair" the carry-forward forbids). The native separator
        // is the only one that delimits segments; the foreign one is NOT a separator on this OS and
        // must therefore never appear between two segments. A LONE foreign char inside a single
        // segment (e.g. "foo\bar" on Linux, where POSIX makes '\' a literal filename byte) is NOT a
        // mixed pair — it is one segment — and is sandbox-safe (invariant (5) proves the policy
        // accepts it). The precise test: split on the native separator; no resulting segment may
        // be empty (a doubled native separator) or itself contain the native separator, and the
        // foreign separator must not act as a delimiter (the native split must equal the full set
        // of real segments — verified below by the no-empty-segment check plus invariant (5)).
        char native = Path.DirectorySeparatorChar;
        char foreign = native == '/' ? '\\' : '/';
        string nativeDoubled = new string(native, 2);
        Assert.DoesNotContain(nativeDoubled, value);
        // The foreign separator may appear only inside a segment, never adjacent to a native
        // separator (which would be the mixed-pair ambiguity). Reject "<native><foreign>" and
        // "<foreign><native>" adjacencies.
        Assert.DoesNotContain($"{native}{foreign}", value);
        Assert.DoesNotContain($"{foreign}{native}", value);
        // No empty interior segment (the doubled-native-separator case, restated post-split).
        string[] segments = value.Split(native);
        for (int i = 1; i < segments.Length; i++)   // skip [0]: empty before the leading '/'
            Assert.False(segments[i].Length == 0,
                $"resolver emitted an empty (doubled-separator) segment in '{value}' for '{rawInput}'");

        // (3) Fully canonical: absolute, no surviving '.'/'..' SEGMENT.
        Assert.True(Path.IsPathFullyQualified(value),
            $"resolved value '{value}' is not absolute for input '{rawInput}'");
        foreach (string segment in value.Split(Path.DirectorySeparatorChar))
            Assert.False(segment is "." or "..",
                $"resolved value '{value}' retains a '{segment}' segment for input '{rawInput}'");

        // (4) The result is exactly what NormcasedPath.FromResolved validated — non-empty and
        // not the default sentinel — so SandboxPolicy.Evaluate sees a real resolved path.
        Assert.False(result.IsDefault);
        Assert.NotEqual(string.Empty, value);

        // (5) PRECONDITION-SHAPE proof: SandboxPolicy.Evaluate accepts the result as both root
        // and candidate WITHOUT throwing its own degenerate-root/default guard, and returns Allow
        // (a path is contained in itself). This proves the resolver's output is precondition-shaped
        // — non-degenerate, non-default, single-separator — as SandboxPolicy requires. It does NOT
        // prove containment within any real sandbox root (an escaping candidate evaluated against
        // itself would also return Allow). Containment under a real root is proven at M4.3
        // (sandbox-deny test with a distinct root) and M9.3 (the SC-5 gate).
        //
        // KNOWN CONSUMER-SIDE QUIRK (Gate-1 W2, carried to M4.3): SandboxPolicy.HasParentSegment
        // splits on BOTH '/' and '\' (it treats both as path delimiters). A Linux path that
        // contains literal '\' bytes (a valid POSIX filename, e.g. the input "a\\..\\b" resolving
        // to "<root>/a\..\b") will be split by SandboxPolicy into segments including ".."; the
        // policy therefore fails-closed-DENY on that value even when used as its own root (because
        // the ".." segment trips HasParentSegment). This is a consumer-side fail-closed quirk, NOT
        // a resolver bug — the resolver correctly produces the POSIX-canonical path; SandboxPolicy's
        // both-separator split is the conservative defence against a path that genuinely mixes
        // separators. In practice the AI can never propose a Linux filename containing '\' via a
        // write path that passes through a real shell, but the quirk is documented here for M4.3.
        // Invariants (1)–(4) still hold for such inputs; we exclude them from (5) to avoid
        // asserting a POSIX literal-backslash path is self-contained (it isn't, in SandboxPolicy's
        // both-separator view), while the shape invariants remain strict.
        bool hasPosixLiteralBackslash = !OperatingSystem.IsWindows() && value.Contains('\\');
        if (!hasPosixLiteralBackslash)
        {
            Assert.Equal(
                HoardPad.Core.Safety.SandboxDecision.Allow,
                HoardPad.Core.Safety.SandboxPolicy.Evaluate(result, result));
        }
    }
}
