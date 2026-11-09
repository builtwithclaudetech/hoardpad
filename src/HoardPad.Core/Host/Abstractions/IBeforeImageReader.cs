// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
using HoardPad.Core.Safety;

namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// HOST [host]. Reads the target file's current on-disk state at FR-39 replay time, returning a
/// typed <see cref="BeforeImageResult"/> that distinguishes three outcomes: <c>Found</c> (file is
/// present and in-root), <c>NotFound</c> (file is absent and in-root — the <c>create</c> replay
/// case), or <c>Refused</c> (out-of-root — containment violation, replay must be aborted).
/// </summary>
/// <remarks>
/// <para>
/// <b>Containment-safe by construction (FR-26 / safety invariant 3).</b> The implementation MUST
/// accept the <em>raw, unresolved</em> target path (as the parked AI proposal specified it) and
/// route the read through the two Core containment primitives in order, reusing them without
/// modification so the normalization is byte-for-byte identical to the write path:
/// <list type="number">
/// <item><see cref="IPathResolver.ResolveCanonical"/> — symlink-resolve, make absolute, normcase
/// (expands Win32 8.3 short names to prevent evasion, per TD-1).</item>
/// <item><see cref="SandboxPolicy.Evaluate"/> — check that the resolved path is at-or-under the
/// resolved open-folder root. Returns <see cref="SandboxDecision.Deny"/> when out-of-root.</item>
/// </list>
/// When <see cref="SandboxPolicy.Evaluate"/> returns <see cref="SandboxDecision.Deny"/>, the
/// implementation MUST return <see cref="BeforeImageResult.Refused"/> — never throw, never return
/// <see cref="BeforeImageResult.NotFound"/> (<c>NotFound</c> is strictly reserved for an in-root
/// target that does not exist on disk). This structural guarantee is what lets <c>Host.Tests</c>
/// verify out-of-root refusal through the seam itself.
/// </para>
/// <para>
/// <b>Note on <c>ISandboxGate</c>.</b> The file-ops service (<c>HoardPad.Core.Ai</c>) wraps the
/// same two primitives in <c>ISandboxGate</c> / <c>PathResolverSandboxGate</c>.
/// <c>ISandboxGate</c> lives in a higher assembly (<c>HoardPad.Core.Ai</c>) that
/// <c>HoardPad.Core</c> must not reference. The Host concrete implements <em>this</em> seam using
/// <see cref="IPathResolver"/> + <see cref="SandboxPolicy"/> directly — the same two primitives,
/// the same order, zero normalization discrepancy with the write path.
/// </para>
/// <para>
/// <b>No <c>System.IO</c> in Core.</b> This interface declaration carries no <c>System.IO</c>
/// reference. The Host concrete owns all file-system I/O.
/// </para>
/// </remarks>
public interface IBeforeImageReader
{
    /// <summary>
    /// Resolve <paramref name="targetPath"/> via <see cref="IPathResolver.ResolveCanonical"/>,
    /// evaluate containment via <see cref="SandboxPolicy.Evaluate"/>, then read the file's current
    /// on-disk state.
    /// </summary>
    /// <param name="targetPath">
    /// The raw, unresolved target path from the parked <c>tool_input</c> — as the AI proposal
    /// specified it (may be relative, may contain symlinks). The implementation resolves it; callers
    /// must NOT pre-resolve this path.
    /// </param>
    /// <param name="openFolderRoot">
    /// The raw open-folder root string — passed to <see cref="IPathResolver.ResolveCanonical"/> as
    /// the base for relative-candidate resolution (the same second argument
    /// <c>PathResolverSandboxGate</c> uses). The Host concrete also holds the resolved
    /// <see cref="NormcasedPath"/> root for <see cref="SandboxPolicy.Evaluate"/>; this string
    /// provides the relative-resolution anchor.
    /// </param>
    /// <param name="ct">Cancellation token; the Host concrete respects it at the read boundary.</param>
    /// <returns>
    /// <list type="bullet">
    /// <item><see cref="BeforeImageResult.Found"/> — target in-root and file present on disk;
    /// carries a <see cref="BeforeImageCapture"/>.</item>
    /// <item><see cref="BeforeImageResult.NotFound"/> — target in-root but file does not exist;
    /// replay applies as a <c>create</c> (<c>PriorContent = null</c>).</item>
    /// <item><see cref="BeforeImageResult.Refused"/> — <see cref="SandboxPolicy.Evaluate"/>
    /// returned <see cref="SandboxDecision.Deny"/>; replay must be aborted; carries the resolved
    /// path for audit logging.</item>
    /// </list>
    /// </returns>
    Task<BeforeImageResult> ReadAsync(string targetPath, string openFolderRoot, CancellationToken ct);
}

/// <summary>
/// The three-outcome result of <see cref="IBeforeImageReader.ReadAsync"/>. Modelled as a sealed
/// record hierarchy so each arm carries its own payload without boxing or sentinel fields.
/// </summary>
/// <remarks>
/// Caller pattern:
/// <code>
/// switch (await reader.ReadAsync(targetPath, root, ct))
/// {
///     case BeforeImageResult.Found f:   // use f.Capture …
///     case BeforeImageResult.NotFound:  // treat as create …
///     case BeforeImageResult.Refused r: // log r.ResolvedPath, abort replay …
/// }
/// </code>
/// </remarks>
public abstract record BeforeImageResult
{
    private BeforeImageResult() { } // sealed hierarchy — only the three nested arms below

    /// <summary>
    /// The target resolved inside the open-folder root and the file was present on disk.
    /// <see cref="Capture"/> carries the bytes, hash, mtime, and byte count needed to populate
    /// the before-image fields of a <c>FileOpRequest</c> (<c>HoardPad.Core.Ai.FileOps</c>).
    /// </summary>
    public sealed record Found(BeforeImageCapture Capture) : BeforeImageResult;

    /// <summary>
    /// The target resolved inside the open-folder root but the file does not exist on disk.
    /// The replay caller treats this as a <c>create</c>: <c>PriorContent = null</c>,
    /// <c>PreOpContentHash = null</c>, <c>PreOpMtime = null</c>, <c>BytesBefore = 0</c>.
    /// </summary>
    public sealed record NotFound : BeforeImageResult;

    /// <summary>
    /// <see cref="SandboxPolicy.Evaluate"/> returned <see cref="SandboxDecision.Deny"/> for the
    /// resolved target — the path is outside the open-folder root. The replay MUST be aborted.
    /// <see cref="ResolvedPath"/> is the canonical path the policy evaluated, for audit logging
    /// (mirrors <c>SandboxResolution.ResolvedPath</c> that the write path logs on a deny).
    /// </summary>
    public sealed record Refused(string ResolvedPath) : BeforeImageResult;
}

/// <summary>
/// The on-disk stat of a file at FR-39 replay time. Carried by <see cref="BeforeImageResult.Found"/>.
/// Provides the data needed to populate the before-image fields of a <c>FileOpRequest</c>
/// (<c>HoardPad.Core.Ai.FileOps</c>) — the same fields the live write path captures before a
/// destructive op: <c>PriorContent</c>, <c>PreOpContentHash</c>, <c>PreOpMtime</c>,
/// <c>BytesBefore</c>. Content bytes are included so the replay caller can stage the before-image
/// blob (M15 durable-before-mutate ordering applies to replay as it does to live ops).
/// </summary>
/// <param name="Content">
/// The raw file bytes at read time. Used by the replay caller to stage the before-image blob.
/// Empty when <see cref="SnapshotSkipped"/> is <see langword="true"/> (file exceeded the FR-28(iii)
/// size cap — undo will be Unavailable for this replay op, matching the live write path).
/// </param>
/// <param name="ContentHash">
/// The pre-op content hash (FR-28(ii)). Populates <c>FileOpRequest.PreOpContentHash</c>. The Host
/// concrete computes this using the same hash algorithm as the live write path (SHA-256 hex).
/// </param>
/// <param name="Mtime">
/// The file's last-modified timestamp at read time. Populates <c>FileOpRequest.PreOpMtime</c>.
/// </param>
/// <param name="ByteCount">
/// The file size in bytes. Populates <c>FileOpRequest.BytesBefore</c>.
/// </param>
/// <param name="SnapshotSkipped">
/// <see langword="true"/> when the file exceeded the FR-28(iii) snapshot size cap. The replay
/// caller sets <c>SnapshotSkipped = true</c> on the <c>FileOpRequest</c> so the undo decision
/// reports it Unavailable — exactly as the live write path does.
/// </param>
public sealed record BeforeImageCapture(
    ReadOnlyMemory<byte> Content,
    string ContentHash,
    DateTimeOffset Mtime,
    long ByteCount,
    bool SnapshotSkipped = false);
