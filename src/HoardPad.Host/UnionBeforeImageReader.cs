// UnionBeforeImageReader.cs — the OQ-MD-5 option ii Host concrete for IUnionBeforeImageReader.
//
// The additive, union-aware FR-39 replay before-image reader (feature-ai-multidoc-panel §5.5). It reads the
// on-disk before-image through the SAME two Core containment primitives the frozen BeforeImageReader and the
// live write path use (IPathResolver → SandboxPolicy.Evaluate), so normalization is byte-for-byte identical.
// The ONLY difference from the frozen reader is the admit rule after the gate: an out-of-root target is
// admitted iff its RESOLVED path is EXACTLY one of the per-file grants (ordinal) — reproducing the
// UnionSandboxGate's file-as-root equality arm. Everything downstream (existence/read/hash/cap) is byte-
// identical to the frozen reader.
//
// This class DUPLICATES the frozen reader's read logic (the small portion after the containment gate). It
// cannot call into BeforeImageReader's private members, and BeforeImageReader is FROZEN (must not be edited
// to expose a shared helper). The duplication is deliberate and load-bearing: the two readers MUST stay hash-
// format / cap / single-handle identical, verified by UnionBeforeImageReaderTests.HashMatchesFrozenReader.
//
// Self-enforcing containment (the Run-9 lesson): the seam re-resolves the raw target and re-checks membership
// ITSELF — it never trusts the caller's claim about which path is granted. A tampered target that does not
// equal a granted resolved path is Refused. An empty grant set ⇒ byte-identical to the root-only reader.
//
// C-9: this is a Host class; all OS/System.IO calls live here, not in the domain. It holds only the stateless
// IPathResolver seam (identical singleton posture to BeforeImageReader).

using System.Security.Cryptography;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Safety;

namespace HoardPad.Host;

/// <summary>
/// The OQ-MD-5 option ii union-aware FR-39 replay before-image reader (feature-ai-multidoc-panel §5.5).
/// Implements <see cref="IUnionBeforeImageReader"/> by routing every read through
/// <see cref="IPathResolver"/>/<see cref="SandboxPolicy.Evaluate"/> before touching the filesystem, admitting
/// an out-of-root target only when its resolved path is EXACTLY one of the supplied grants — containment-safe
/// by construction (FR-26 / FR-MD-1). The frozen <see cref="BeforeImageReader"/> is UNMODIFIED; this is an
/// additive type composed at the call site.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hash / cap / single-handle parity with the frozen reader.</b> The <see cref="BeforeImageCapture"/> is
/// produced identically to <see cref="BeforeImageReader"/>: SHA-256 hex via <see cref="Convert.ToHexString"/>,
/// the same 10&#160;MB <c>SnapshotSizeCap</c>, and the same single-<see cref="FileShare.Read"/>-handle read
/// where <c>stream.Length</c> drives the cap decision and mtime is captured adjacent to the open. A divergence
/// would corrupt the FR-28(ii) undo re-verify.
/// </para>
/// <para>
/// <b>Fail-closed.</b> An empty <c>resolvedGrants</c> set makes the Deny arm admit nothing — behavior is then
/// byte-identical to the root-only <see cref="BeforeImageReader"/>.
/// </para>
/// </remarks>
public sealed class UnionBeforeImageReader : IUnionBeforeImageReader
{
    // FR-28(iii) snapshot size cap — MUST equal BeforeImageReader.SnapshotSizeCap. That constant is `internal`
    // to the Host assembly; this reader lives in the SAME assembly, so it references it directly rather than
    // re-declaring the magic number (single source of truth — no drift between the two readers).
    private const long SnapshotSizeCap = BeforeImageReader.SnapshotSizeCap;

    private readonly IPathResolver _resolver;

    /// <param name="resolver">The host's canonicalizing path resolver (M2.2) — the ONLY NormcasedPath producer.</param>
    public UnionBeforeImageReader(IPathResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async Task<BeforeImageResult> ReadAsync(
        string targetPath,
        string openFolderRoot,
        IReadOnlySet<string> resolvedGrants,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(targetPath))
            throw new ArgumentException("A non-empty target path is required.", nameof(targetPath));
        if (string.IsNullOrEmpty(openFolderRoot))
            throw new ArgumentException("A non-empty open-folder root is required.", nameof(openFolderRoot));
        ArgumentNullException.ThrowIfNull(resolvedGrants);

        // Step 1: canonicalize both paths — the SAME leg BeforeImageReader + the write path use.
        NormcasedPath resolvedRoot = _resolver.ResolveCanonical(openFolderRoot, openFolderRoot);
        NormcasedPath resolvedCandidate = _resolver.ResolveCanonical(targetPath, openFolderRoot);

        // Step 2: sandbox containment gate — NO I/O before this decision. This is the ONLY branch that
        // differs from the frozen reader.
        //   Allow  → in-root: proceed to the existence/read logic exactly like the frozen reader.
        //   Deny   → out-of-root: admit ONLY IF the resolved candidate is EXACTLY one of the grants
        //            (ordinal). This reproduces UnionSandboxGate's file-as-root equality arm WITHOUT
        //            calling SandboxPolicy.Evaluate on a file-as-root — the exact same admit rule the
        //            write gate uses, so read-side and write-side containment cannot diverge. An empty
        //            grant set admits nothing (fail-closed). A tampered target that does not equal a
        //            granted resolved path Refuses — the seam self-enforces; it never trusts a caller hint.
        SandboxDecision decision = SandboxPolicy.Evaluate(resolvedRoot, resolvedCandidate);
        if (decision == SandboxDecision.Deny && !resolvedGrants.Contains(resolvedCandidate.Value))
            return new BeforeImageResult.Refused(resolvedCandidate.Value);

        // Admitted (in-root OR out-of-root-but-granted). From here the logic is byte-identical to
        // BeforeImageReader — existence check + single-handle size/mtime/content read + SHA-256 hex.
        string resolvedPath = resolvedCandidate.Value;
        if (!File.Exists(resolvedPath))
            return new BeforeImageResult.NotFound();

        using FileStream stream = new(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long fileLength = stream.Length;
        DateTimeOffset mtime = File.GetLastWriteTimeUtc(resolvedPath);

        if (fileLength > SnapshotSizeCap)
        {
            return new BeforeImageResult.Found(
                new BeforeImageCapture(
                    Content: ReadOnlyMemory<byte>.Empty,
                    ContentHash: string.Empty,
                    Mtime: mtime,
                    ByteCount: fileLength,
                    SnapshotSkipped: true));
        }

        byte[] bytes = await ReadFullyAsync(stream, (int)fileLength, ct).ConfigureAwait(false);
        string contentHash = Convert.ToHexString(SHA256.HashData(bytes));

        return new BeforeImageResult.Found(
            new BeforeImageCapture(
                Content: new ReadOnlyMemory<byte>(bytes),
                ContentHash: contentHash,
                Mtime: mtime,
                ByteCount: bytes.Length));
    }

    // Read exactly `length` bytes from a stream positioned at the start — identical to
    // BeforeImageReader.ReadFullyAsync (the short-read/truncation handling must match so the two readers
    // produce the same bytes for the same file).
    private static async Task<byte[]> ReadFullyAsync(FileStream stream, int length, CancellationToken ct)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            offset += read;
        }
        return offset == length ? buffer : buffer[..offset];
    }
}
