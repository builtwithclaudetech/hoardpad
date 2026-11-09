namespace HoardPad.Core.Ai.FileOps;

using HoardPad.Core.Safety;

/// <summary>
/// The additive Archetype-B enforcement seam for the multi-doc panel (feature-ai-multidoc-panel §5.4,
/// FR-MD-1). It implements the frozen <see cref="ISandboxGate"/> by composing (a) an inner root gate
/// (the frozen <see cref="PathResolverSandboxGate"/> in production — depended on only via the interface)
/// with (b) an immutable per-turn <see cref="FileGrantSet"/>. The per-turn write sandbox becomes
/// <c>(root ∪ descendants)</c> ∪ <c>(exactly the checked out-of-folder file grants)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No frozen artifact is modified.</b> <see cref="AiFileOpsService"/> already consumes
/// <see cref="ISandboxGate"/> as an injected abstraction, so composing this gate at the call site
/// (PANEL-2) needs no service change; and <see cref="SandboxPolicy"/> / <see cref="PathResolverSandboxGate"/>
/// are consulted unchanged (the root leg IS the frozen gate).
/// </para>
/// <para>
/// <b>Why equality-on-resolved-strings defeats the prefix trap.</b> The inner root gate canonicalizes +
/// normcases the candidate via the same <see cref="HoardPad.Core.Host.Abstractions.IPathResolver"/> leg the
/// App used to build the grant strings. Comparing the already-resolved candidate string for EXACT ordinal
/// equality against a granted resolved string is the §5.2 "degenerate file-as-root equality arm" WITHOUT
/// calling <see cref="SandboxPolicy.Evaluate"/> on a file-as-root — sidestepping OQ-MD-1 entirely and
/// keeping <see cref="SandboxPolicy"/> untouched. An exact string match cannot admit a prefix-sharing
/// sibling (<c>/a/b/c.txt</c> vs <c>/a/b/c.txt2</c> — the strings simply differ), and a file grant has no
/// descendant arm for a child path to slip through. This is strictly narrower than the root gate's
/// segment-boundary descendant check, which is correct: a file grants only itself.
/// </para>
/// <para>
/// <b>Fail-closed (FR-MD-10).</b> An empty/absent <see cref="FileGrantSet"/> makes this gate behave
/// byte-identically to the bare root gate — a root Deny stays a Deny. A malformed grant string never
/// widens; only an exact resolved-path match admits. The grant leg can only turn a root <c>Deny</c> into
/// an <c>Allow</c> for one exact granted path; it can never narrow a root <c>Allow</c> (FR-MD-1: an
/// in-root path is already writable and grants add nothing).
/// </para>
/// </remarks>
public sealed class UnionSandboxGate : ISandboxGate
{
    private readonly ISandboxGate _rootGate;
    private readonly FileGrantSet _grants;

    /// <summary>Compose the union gate for one AI turn.</summary>
    /// <param name="rootGate">
    /// The inner root gate (the frozen <see cref="PathResolverSandboxGate"/> in production) — depended on
    /// via the interface, not the concrete type. It performs the real FR-26 canonicalize→evaluate.
    /// </param>
    /// <param name="grants">
    /// The immutable per-file grant set for this turn (FR-MD-9). Pass <see cref="FileGrantSet.Empty"/> for
    /// the root-only fail-closed default (FR-MD-10).
    /// </param>
    public UnionSandboxGate(ISandboxGate rootGate, FileGrantSet grants)
    {
        ArgumentNullException.ThrowIfNull(rootGate);
        ArgumentNullException.ThrowIfNull(grants);

        _rootGate = rootGate;
        _grants = grants;
    }

    /// <inheritdoc />
    public SandboxResolution Resolve(string rawCandidatePath)
    {
        // Call the inner root gate ONCE — it canonicalizes + normcases via the host resolver and applies
        // the frozen SandboxPolicy against the root. This is the single resolution the whole decision rests
        // on; the grant comparison reuses its ResolvedPath so no second resolution can diverge.
        SandboxResolution rootResolution = _rootGate.Resolve(rawCandidatePath);

        // In-root candidate: the root gate already Allows it. Grants add nothing (FR-MD-1) — return the
        // root resolution unchanged.
        if (rootResolution.Decision == SandboxDecision.Allow)
            return rootResolution;

        // Out-of-root candidate: admit iff its RESOLVED path is EXACTLY one of the granted resolved paths
        // (ordinal). This is the file-as-root equality arm expressed as a resolved-string compare — no call
        // into SandboxPolicy.Evaluate, so the frozen policy stays untouched (OQ-MD-1 sidestepped).
        if (_grants.Contains(rootResolution.ResolvedPath))
            return new SandboxResolution(SandboxDecision.Allow, rootResolution.ResolvedPath);

        // No grant matches — return the root gate's Deny unchanged, so the activity log records the resolved
        // path exactly as the frozen gate does (fail-closed, FR-MD-10).
        return rootResolution;
    }
}
