// UnionBeforeImageReaderTests.cs — OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5) coverage for the
// additive union-aware FR-39 replay before-image reader (UnionBeforeImageReader).
//
// Drives the REAL UnionBeforeImageReader against a controlled temp filesystem to verify:
//
//   1. In-root PRESENT / ABSENT → Found / NotFound (unchanged from the root-only reader — grants add nothing).
//   2. Out-of-root path IN the grant set → admitted: Found (bytes + correct SHA-256 + cap behavior), or
//      NotFound (granted create case).
//   3. Out-of-root path NOT in the grant set → Refused. NON-VACUOUS: the SAME out-of-root file, once its
//      resolved path is the grant, is Found — so removing the grant-membership admit makes the granted case
//      Refused (proves the admit is load-bearing).
//   4. Prefix/sibling non-widening: granting /a/b/c.txt does NOT admit /a/b/c.txt2 (a prefix-sharing sibling)
//      — widening the admit to a prefix check would fail this.
//   5. Empty grant set → byte-identical to the root-only reader (out-of-root Refused; in-root Found).
//   6. Hash-format parity: for the SAME in-root file, the union reader's ContentHash EQUALS the frozen
//      BeforeImageReader's — the FR-28(ii) undo re-verify depends on this.
//
// The REAL PathResolver is used throughout so containment goes through the exact production normalization —
// a mocked resolver would degrade the safety property (containment via the seam) to an algorithm unit test.

using System.Collections.Generic;
using System.Security.Cryptography;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class UnionBeforeImageReaderTests : IDisposable
{
    private readonly string _root;                 // realpath'd sandbox root
    private readonly string _outsideDir;           // a sibling dir, outside the root
    private readonly PathResolver _resolver = new();
    private readonly UnionBeforeImageReader _reader;
    private readonly BeforeImageReader _frozenReader;

    public UnionBeforeImageReaderTests()
    {
        string raw = Path.Combine(Path.GetTempPath(), $"hoardpad-mdii-{Guid.NewGuid():N}");
        Directory.CreateDirectory(raw);
        _root = new PathResolver().ResolveCanonical(raw, raw).Value;
        if (!Directory.Exists(_root))
            Directory.CreateDirectory(_root);

        string rawOutside = Path.Combine(Path.GetTempPath(), $"hoardpad-mdii-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rawOutside);
        _outsideDir = new PathResolver().ResolveCanonical(rawOutside, rawOutside).Value;
        if (!Directory.Exists(_outsideDir))
            Directory.CreateDirectory(_outsideDir);

        _reader = new UnionBeforeImageReader(_resolver);
        _frozenReader = new BeforeImageReader(_resolver);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_outsideDir, recursive: true); } catch { /* best-effort */ }
    }

    private string InRoot(string name) => Path.Combine(_root, name);
    private string Outside(string name) => Path.Combine(_outsideDir, name);

    private static HashSet<string> Grants(params string[] resolved) =>
        new(resolved, StringComparer.Ordinal);

    // Resolve a raw path the SAME way the reader/gate resolve it — for building the exact grant string.
    private string Resolve(string rawPath) => _resolver.ResolveCanonical(rawPath, _root).Value;

    // ---- 1. In-root: grants add nothing (unchanged behavior) ----

    [Fact]
    public async Task InRootPresentReturnsFoundRegardlessOfGrants()
    {
        byte[] content = System.Text.Encoding.UTF8.GetBytes("in-root union\n");
        string target = InRoot("target.txt");
        await File.WriteAllBytesAsync(target, content);

        // Even with an unrelated grant present, an in-root file is Found via the root Allow arm.
        BeforeImageResult result =
            await _reader.ReadAsync(target, _root, Grants(Resolve(Outside("unrelated.txt"))), CancellationToken.None);

        var found = Assert.IsType<BeforeImageResult.Found>(result);
        Assert.Equal((long)content.Length, found.Capture.ByteCount);
        Assert.Equal(content, found.Capture.Content.ToArray());
    }

    [Fact]
    public async Task InRootAbsentReturnsNotFound()
    {
        string missing = InRoot("missing.txt");
        BeforeImageResult result =
            await _reader.ReadAsync(missing, _root, Grants(), CancellationToken.None);
        Assert.IsType<BeforeImageResult.NotFound>(result);
    }

    // ---- 2. Out-of-root, granted → admitted ----

    [Fact]
    public async Task OutOfRootPresentAndGrantedReturnsFoundWithCorrectHash()
    {
        byte[] content = System.Text.Encoding.UTF8.GetBytes("granted out-of-folder doc\n");
        string outFile = Outside("granted.txt");
        await File.WriteAllBytesAsync(outFile, content);
        string grant = Resolve(outFile);

        BeforeImageResult result =
            await _reader.ReadAsync(outFile, _root, Grants(grant), CancellationToken.None);

        var found = Assert.IsType<BeforeImageResult.Found>(result);
        Assert.Equal((long)content.Length, found.Capture.ByteCount);
        Assert.Equal(content, found.Capture.Content.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), found.Capture.ContentHash);
        Assert.False(found.Capture.SnapshotSkipped);
    }

    [Fact]
    public async Task OutOfRootAbsentButGrantedReturnsNotFound()
    {
        // A granted create target that does not yet exist → NotFound (the granted create case).
        string outFile = Outside("granted-create.txt");
        Assert.False(File.Exists(outFile));
        string grant = Resolve(outFile);

        BeforeImageResult result =
            await _reader.ReadAsync(outFile, _root, Grants(grant), CancellationToken.None);

        Assert.IsType<BeforeImageResult.NotFound>(result);
    }

    [Fact]
    public async Task OutOfRootGrantedOverCapReturnsFoundWithSnapshotSkipped()
    {
        long overCap = BeforeImageReader.SnapshotSizeCap + 1;
        string outFile = Outside("granted-big.bin");
        using (FileStream fs = new(outFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Seek(overCap - 1, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
        }
        string grant = Resolve(outFile);

        BeforeImageResult result =
            await _reader.ReadAsync(outFile, _root, Grants(grant), CancellationToken.None);

        var found = Assert.IsType<BeforeImageResult.Found>(result);
        Assert.True(found.Capture.SnapshotSkipped);
        Assert.Equal(0, found.Capture.Content.Length);
        Assert.Equal(string.Empty, found.Capture.ContentHash);
        Assert.Equal(overCap, found.Capture.ByteCount);
    }

    // ---- 3. Out-of-root, NOT granted → Refused (NON-VACUOUS pairing) ----

    [Fact]
    public async Task OutOfRootNotGrantedReturnsRefused()
    {
        byte[] content = System.Text.Encoding.UTF8.GetBytes("secret");
        string outFile = Outside("ungranted.txt");
        await File.WriteAllBytesAsync(outFile, content);

        // Empty grant set — the out-of-root file is NOT admitted.
        BeforeImageResult result =
            await _reader.ReadAsync(outFile, _root, Grants(), CancellationToken.None);

        var refused = Assert.IsType<BeforeImageResult.Refused>(result);
        Assert.Equal(Resolve(outFile), refused.ResolvedPath);
        // No mutation of the refused file.
        Assert.Equal("secret", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(outFile)));
    }

    [Fact]
    public async Task GrantMembershipIsLoadBearingSameFileFoundWithGrantRefusedWithout()
    {
        // NON-VACUITY: the SAME out-of-root file is Found WITH its grant and Refused WITHOUT it. If the
        // grant-membership admit were removed (always Refuse on Deny), the WITH-grant case would fail here.
        byte[] content = System.Text.Encoding.UTF8.GetBytes("load-bearing");
        string outFile = Outside("member.txt");
        await File.WriteAllBytesAsync(outFile, content);
        string grant = Resolve(outFile);

        BeforeImageResult withGrant =
            await _reader.ReadAsync(outFile, _root, Grants(grant), CancellationToken.None);
        BeforeImageResult withoutGrant =
            await _reader.ReadAsync(outFile, _root, Grants(), CancellationToken.None);

        Assert.IsType<BeforeImageResult.Found>(withGrant);
        Assert.IsType<BeforeImageResult.Refused>(withoutGrant);
    }

    // ---- 4. Prefix/sibling non-widening (AC-MD-5 at the read level) ----

    [Fact]
    public async Task GrantingOneFileDoesNotAdmitPrefixSharingSibling()
    {
        // Grant /out/c.txt; a sibling /out/c.txt2 shares the prefix but is a DIFFERENT resolved string.
        // Exact ordinal equality must NOT admit it. Widening the admit to a prefix check fails this.
        byte[] siblingContent = System.Text.Encoding.UTF8.GetBytes("sibling");
        string grantedFile = Outside("c.txt");
        string siblingFile = Outside("c.txt2");
        await File.WriteAllBytesAsync(grantedFile, System.Text.Encoding.UTF8.GetBytes("granted"));
        await File.WriteAllBytesAsync(siblingFile, siblingContent);

        IReadOnlySet<string> grants = Grants(Resolve(grantedFile));

        // The granted file is Found.
        Assert.IsType<BeforeImageResult.Found>(
            await _reader.ReadAsync(grantedFile, _root, grants, CancellationToken.None));

        // The prefix-sharing sibling is Refused (not admitted by the c.txt grant).
        Assert.IsType<BeforeImageResult.Refused>(
            await _reader.ReadAsync(siblingFile, _root, grants, CancellationToken.None));
    }

    // ---- 5. Empty grant set ≡ root-only reader (fail-closed) ----

    [Fact]
    public async Task EmptyGrantSetBehavesIdenticallyToRootOnlyReader()
    {
        // In-root present: both Found with the same bytes.
        byte[] inContent = System.Text.Encoding.UTF8.GetBytes("in-root");
        string inFile = InRoot("both.txt");
        await File.WriteAllBytesAsync(inFile, inContent);

        var unionIn = Assert.IsType<BeforeImageResult.Found>(
            await _reader.ReadAsync(inFile, _root, Grants(), CancellationToken.None));
        var frozenIn = Assert.IsType<BeforeImageResult.Found>(
            await _frozenReader.ReadAsync(inFile, _root, CancellationToken.None));
        Assert.Equal(frozenIn.Capture.ContentHash, unionIn.Capture.ContentHash);

        // Out-of-root: both Refused with the same resolved path.
        string outFile = Outside("out.txt");
        await File.WriteAllBytesAsync(outFile, System.Text.Encoding.UTF8.GetBytes("x"));
        var unionOut = Assert.IsType<BeforeImageResult.Refused>(
            await _reader.ReadAsync(outFile, _root, Grants(), CancellationToken.None));
        var frozenOut = Assert.IsType<BeforeImageResult.Refused>(
            await _frozenReader.ReadAsync(outFile, _root, CancellationToken.None));
        Assert.Equal(frozenOut.ResolvedPath, unionOut.ResolvedPath);
    }

    // ---- 6. Hash-format parity with the frozen reader ----

    [Fact]
    public async Task HashMatchesFrozenReaderForSameInRootFile()
    {
        // The FR-28(ii) undo re-verify compares the captured hash against a fresh one; the two readers MUST
        // produce byte-identical hashes for the same file or a granted-replay undo would silently corrupt.
        byte[] content = System.Text.Encoding.UTF8.GetBytes("parity check payload 12345");
        string target = InRoot("parity.txt");
        await File.WriteAllBytesAsync(target, content);

        var unionFound = Assert.IsType<BeforeImageResult.Found>(
            await _reader.ReadAsync(target, _root, Grants(), CancellationToken.None));
        var frozenFound = Assert.IsType<BeforeImageResult.Found>(
            await _frozenReader.ReadAsync(target, _root, CancellationToken.None));

        Assert.Equal(frozenFound.Capture.ContentHash, unionFound.Capture.ContentHash);
        Assert.Equal(64, unionFound.Capture.ContentHash.Length); // SHA-256 hex
    }

    // ---- Cancellation propagation (parity with the frozen reader) ----

    [Fact]
    public async Task CancelledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _reader.ReadAsync(InRoot("any.txt"), _root, Grants(), cts.Token));
    }
}
