// abstraction — lives in HoardPad.Core, NOT HoardPad.Host.
//
// OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): the ADDITIVE union-aware before-image reader seam,
// ALONGSIDE the frozen IBeforeImageReader. The frozen reader is root-only: at FR-39 replay time it Refuses
// an out-of-folder target — correct for the single-open-folder floor, but a correctness gap for a parked op
// that was authorized by a per-file grant (a checked out-of-folder document). This seam admits EXACTLY the
// paths the union write gate (UnionSandboxGate + FileGrantSet, PANEL-1/2) already enforces — and NOTHING
// wider. The frozen IBeforeImageReader + BeforeImageReader are UNMODIFIED; this is a new type only.
//
// Kept free of any HoardPad.Core.Ai reference (mirroring IBeforeImageReader's note): FileGrantSet lives in
// the higher HoardPad.Core.Ai assembly that HoardPad.Core must not reference. The grant is passed as a plain
// IReadOnlySet<string> of resolved, normcased path strings — the SAME string form NormcasedPath.Value /
// SandboxResolution.ResolvedPath / FileGrantSet membership uses — so the containment comparison is a direct
// ordinal string match with zero cross-assembly coupling.

using HoardPad.Core.Safety;

namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// HOST [host]. The FR-39 replay-time before-image reader for a UNION-gated write path (OQ-MD-5 option ii,
/// feature-ai-multidoc-panel §5.5) — the additive counterpart to the frozen <see cref="IBeforeImageReader"/>.
/// It reads the target's current on-disk state and returns the same three-outcome
/// <see cref="BeforeImageResult"/>, but admits an out-of-root target iff it is EXACTLY one of the resolved
/// per-file grants for the op (ordinal), reproducing the <c>UnionSandboxGate</c>'s file-as-root equality arm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Containment-safe by construction — the seam enforces containment ITSELF (the Run-9 lesson).</b> The
/// implementation MUST route the read through the SAME two Core containment primitives, in the SAME order,
/// as the frozen reader and the write path, so normalization is byte-for-byte identical:
/// <list type="number">
/// <item><see cref="IPathResolver.ResolveCanonical"/> — symlink-resolve, make absolute, normcase.</item>
/// <item><see cref="SandboxPolicy.Evaluate"/> — check the resolved candidate is at-or-under the resolved root.</item>
/// </list>
/// On an <see cref="SandboxDecision.Allow"/> the read proceeds exactly like the frozen reader. On a
/// <see cref="SandboxDecision.Deny"/>, the read proceeds ONLY IF the resolved candidate string is EXACTLY
/// (ordinal) a member of <paramref name="resolvedGrants"/> — the same admit rule
/// <c>UnionSandboxGate.Resolve</c> applies for a write. Otherwise <see cref="BeforeImageResult.Refused"/>.
/// The implementation never trusts the caller's claim about which path is granted: it re-resolves the raw
/// target and re-checks membership itself, so a tampered target that does not equal a granted resolved path
/// is Refused.
/// </para>
/// <para>
/// <b>Fail-closed (FR-MD-10).</b> An empty <paramref name="resolvedGrants"/> makes this seam behave
/// byte-identically to the root-only <see cref="IBeforeImageReader"/> — every out-of-root target Refused.
/// A grant string only ever turns a Deny into an admit for its ONE exact resolved path; it can never widen a
/// prefix, a sibling, a parent directory, or a descendant (exact ordinal equality — no descendant arm).
/// </para>
/// <para>
/// <b>Format parity is mandatory.</b> The <see cref="BeforeImageCapture"/> it produces (SHA-256 hex hash,
/// 10&#160;MB snapshot cap, single-handle size/mtime/content read) MUST be byte-identical to the frozen
/// <c>BeforeImageReader</c>'s for the same file — the FR-28(ii) undo re-verify compares this hash against a
/// fresh one, so any format divergence would silently corrupt undo.
/// </para>
/// </remarks>
public interface IUnionBeforeImageReader
{
    /// <summary>
    /// Resolve <paramref name="targetPath"/> via <see cref="IPathResolver.ResolveCanonical"/>, evaluate
    /// containment via <see cref="SandboxPolicy.Evaluate"/> against <paramref name="openFolderRoot"/>, admit
    /// an out-of-root target only if its resolved string is EXACTLY one of <paramref name="resolvedGrants"/>,
    /// then read the file's current on-disk state.
    /// </summary>
    /// <param name="targetPath">
    /// The raw, unresolved target path from the parked <c>tool_input</c> — as the AI proposal specified it.
    /// The implementation resolves it; callers must NOT pre-resolve it.
    /// </param>
    /// <param name="openFolderRoot">The raw open-folder root — the relative-resolution anchor + containment base.</param>
    /// <param name="resolvedGrants">
    /// The resolved, normcased per-file grant strings authorizing this op's out-of-root writes (the SAME
    /// strings the <c>UnionSandboxGate</c>/<c>FileGrantSet</c> enforce). Empty ⇒ root-only, fail-closed.
    /// </param>
    /// <param name="ct">Cancellation token; respected at the read boundary.</param>
    /// <returns>
    /// <see cref="BeforeImageResult.Found"/> (in-root, or granted-and-present), <see cref="BeforeImageResult.NotFound"/>
    /// (in-root or granted, absent — the create case), or <see cref="BeforeImageResult.Refused"/> (out-of-root
    /// and NOT granted — replay must be aborted).
    /// </returns>
    Task<BeforeImageResult> ReadAsync(
        string targetPath,
        string openFolderRoot,
        IReadOnlySet<string> resolvedGrants,
        CancellationToken ct);
}
