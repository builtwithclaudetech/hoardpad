// BeforeImageReader.cs — the MF1.1 Host concrete for IBeforeImageReader.
//
// Reads the on-disk before-image of a file at FR-39 replay time through the SAME two Core
// containment primitives the live write path uses (IPathResolver → SandboxPolicy.Evaluate),
// so normalization is byte-for-byte identical — a discrepancy would be a security defect.
//
// Algorithm (per the IBeforeImageReader contract):
//   1. Resolve targetPath against openFolderRoot via IPathResolver.ResolveCanonical.
//      Also resolve the root itself so SandboxPolicy.Evaluate gets a NormcasedPath root
//      (the policy requires an already-resolved root — it never resolves raw strings itself).
//   2. SandboxPolicy.Evaluate(resolvedRoot, resolvedCandidate).
//      Deny → BeforeImageResult.Refused(candidate.Value). NO I/O before this check passes.
//   3. File absent → BeforeImageResult.NotFound (in-root, create case).
//   4. File present → open ONE FileStream (FileShare.Read); use stream.Length for the cap
//      decision and mtime (both from the same open, closing the size/mtime/content TOCTOU).
//      Over-cap → Found(SnapshotSkipped=true, Content empty, ByteCount=stream.Length).
//      Under-cap → await File.ReadAllBytesAsync via the open handle for genuine async ct
//      propagation; compute SHA-256 hex hash; return Found(BeforeImageCapture).
//
// Hash format: Convert.ToHexString(SHA256.HashData(bytes)) — uppercase hex, no hyphens.
// Identical to RecoveryFileSystem.HashHex and the write path's PreOpContentHash format.
//
// Size cap constant: SnapshotSizeCap = 10 MB — matches the "10 MB" over-cap example in the
// PRD hostile review (docs/prd-hostile-review-2026-06-14.md §B4) and the tech-design note
// "set high enough that ordinary edits are always journaled; the exact cap is a tech-design
// number" (docs/tech-design.md FR-28(iii)).  This is the FIRST definition in the codebase.
// TODO(MF-future): promote SnapshotSizeCap to a shared constant once the live write path
// also enforces FR-28(iii) (currently the live path captures before-images in the App layer
// where the cap decision should ideally share this value).
//
// C-9: this is the ONLY Host class allowed OS/System.IO calls; all syscalls are here, not
// in the domain. The class holds only the stateless IPathResolver seam.

using System.Security.Cryptography;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Safety;

namespace HoardPad.Host;

/// <summary>
/// The FR-39 replay-time before-image reader. Implements <see cref="IBeforeImageReader"/> by
/// routing every read through <see cref="IPathResolver"/>/<see cref="SandboxPolicy.Evaluate"/>
/// before touching the filesystem — containment-safe by construction (FR-26 / safety invariant 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton posture.</b> This class holds only the stateless <see cref="IPathResolver"/>
/// seam; it carries no per-session root. The open-folder root is supplied per-call via
/// <see cref="ReadAsync"/>'s <paramref name="openFolderRoot"/> parameter and is resolved fresh
/// on each invocation, so a single registered instance serves all open-folder sessions correctly.
/// </para>
/// <para>
/// <b>Hash format.</b> <c>SHA-256</c> hex via <see cref="Convert.ToHexString"/> — identical to
/// <c>RecoveryFileSystem.HashHex</c> and the write path's <c>PreOpContentHash</c> format. Changing
/// this without changing the write path would silently corrupt the undo TOCTOU re-verify (FR-28(ii)).
/// </para>
/// <para>
/// <b>Single-handle consistency.</b> After the containment gate, the file is opened once with
/// <see cref="FileShare.Read"/>; <c>stream.Length</c> drives the cap decision, mtime is captured
/// from that same open, and the bytes (when under-cap) are read from the same handle with a
/// genuinely-async <c>ReadAllBytesAsync</c> call so the <see cref="CancellationToken"/> propagates
/// into the read — not just checked at entry.
/// </para>
/// </remarks>
public sealed class BeforeImageReader : IBeforeImageReader
{
    // FR-28(iii) snapshot size cap: files larger than this are not snapshotted (undo unavailable).
    // PRD example uses 10 MB ("a 9 MB→0-byte truncate…"); tech-design says "set high enough that
    // ordinary edits are always journaled; the exact cap is a tech-design number."
    // This is the first and only definition in the codebase.
    internal const long SnapshotSizeCap = 10L * 1024 * 1024;   // 10 MB

    private readonly IPathResolver _resolver;

    /// <param name="resolver">The host's canonicalizing path resolver (M2.2).</param>
    public BeforeImageReader(IPathResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async Task<BeforeImageResult> ReadAsync(string targetPath, string openFolderRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(targetPath))
            throw new ArgumentException("A non-empty target path is required.", nameof(targetPath));
        if (string.IsNullOrEmpty(openFolderRoot))
            throw new ArgumentException("A non-empty open-folder root is required.", nameof(openFolderRoot));

        // Step 1: canonicalize both paths — the resolver is the ONLY NormcasedPath producer.
        // The root resolves against itself (it is already an absolute directory path).
        // The candidate resolves against the raw root (the IPathResolver contract for relative paths).
        NormcasedPath resolvedRoot = _resolver.ResolveCanonical(openFolderRoot, openFolderRoot);
        NormcasedPath resolvedCandidate = _resolver.ResolveCanonical(targetPath, openFolderRoot);

        // Step 2: sandbox containment check — NO I/O before this gate passes (FR-26).
        // SandboxPolicy.Evaluate is pure (no syscalls); a Deny returns Refused immediately.
        // The resolved candidate path is carried in Refused for audit logging, matching the
        // SandboxResolution.ResolvedPath the write path logs on a deny (AiFileOpsService.DenyAsync).
        SandboxDecision decision = SandboxPolicy.Evaluate(resolvedRoot, resolvedCandidate);
        if (decision == SandboxDecision.Deny)
            return new BeforeImageResult.Refused(resolvedCandidate.Value);

        // Step 3: containment passed — check existence. A missing target is the create case.
        string resolvedPath = resolvedCandidate.Value;
        if (!File.Exists(resolvedPath))
            return new BeforeImageResult.NotFound();

        // Step 4: open ONE handle (FileShare.Read) so that the size, mtime, and content are
        // all captured in a single consistent filesystem view, closing the TOCTOU window where
        // a concurrent writer could extend the file past SnapshotSizeCap between the size check
        // and the read, or produce a mtime that predates the bytes we actually read.
        //
        // SequentialScan hint because we read the whole file linearly (same posture as
        // AtomicFileStore.ReadAll's SequentialScan for the append-log reader).
        //
        // mtime is taken from File.GetLastWriteTimeUtc immediately after the stream is opened —
        // the narrowest window achievable without P/Invoke (matches RecoveryFileSystem.ProbeAsync
        // which also takes mtime adjacent to the open, before the read). FileStream does not
        // expose LastWriteTimeUtc directly; FileInfo.LastWriteTimeUtc and File.GetLastWriteTimeUtc
        // are equivalent single-syscall reads (stat(2) on Linux) and are the established pattern.
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
            // Over-cap: do NOT read the bytes (expensive + undo will be Unavailable anyway).
            // Content = empty, ContentHash = empty string, ByteCount = actual file length.
            return new BeforeImageResult.Found(
                new BeforeImageCapture(
                    Content: ReadOnlyMemory<byte>.Empty,
                    ContentHash: string.Empty,
                    Mtime: mtime,
                    ByteCount: fileLength,
                    SnapshotSkipped: true));
        }

        // Under-cap: read the full bytes from the same open handle so the content is consistent
        // with the size and mtime captured above. The read is genuinely async so ct propagates
        // into the I/O syscall — not just checked at entry.
        //
        // SHA-256 hex — identical format to RecoveryFileSystem.HashHex:
        //   Convert.ToHexString(SHA256.HashData(content))
        // Uppercase hex, no separators, no prefix. Changing this silently breaks FR-28(ii)
        // TOCTOU re-verify because the undo path compares PreOpContentHash against a fresh hash.
        byte[] bytes = await ReadFullyAsync(stream, (int)fileLength, ct).ConfigureAwait(false);
        string contentHash = Convert.ToHexString(SHA256.HashData(bytes));

        return new BeforeImageResult.Found(
            new BeforeImageCapture(
                Content: new ReadOnlyMemory<byte>(bytes),
                ContentHash: contentHash,
                Mtime: mtime,
                ByteCount: bytes.Length));
    }

    // Read exactly `length` bytes from a stream that is already positioned at the start.
    // Under-cap files are ≤ 10 MB, so a single ReadAsync is nearly always sufficient; the
    // loop handles the (rare) short-read case without requiring a second allocating call.
    private static async Task<byte[]> ReadFullyAsync(FileStream stream, int length, CancellationToken ct)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0)
                break;   // EOF before expected length — truncation between open and read; return what we have.
            offset += read;
        }
        // If the file shrank between open and read, trim the buffer to the bytes actually read.
        return offset == length ? buffer : buffer[..offset];
    }
}
