using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Safety;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// The FR-26 canonicalize-then-evaluate gate the shared file-ops service (M4.3) consults for EVERY op
/// before it can mutate the filesystem (architecture §4.5 step 1). Given a raw, possibly-relative,
/// possibly-symlinked candidate path, it canonicalizes via <see cref="IPathResolver"/> and applies
/// <see cref="SandboxPolicy.Evaluate"/> against the resolved open-folder root — returning
/// <see cref="SandboxDecision.Deny"/> when the resolved candidate is outside the root.
/// </summary>
/// <remarks>
/// <para>
/// This is a thin domain seam over <see cref="IPathResolver"/> + <see cref="SandboxPolicy"/>, extracted
/// for one reason: <see cref="NormcasedPath"/> is constructed ONLY by the host resolver (its factory is
/// <c>internal</c> to <c>HoardPad.Core</c>, visible only to <c>HoardPad.Host</c> and
/// <c>HoardPad.Core.Tests</c> — pinned by <c>ArchitectureBoundaryTests</c>). The file-ops service must
/// never need to mint a <see cref="NormcasedPath"/>, so the resolve+evaluate step is behind this gate;
/// the production <see cref="PathResolverSandboxGate"/> does the real canonicalize→evaluate, and a test
/// fakes the gate to drive the deny path without producing a <see cref="NormcasedPath"/>.
/// </para>
/// <para>
/// <b>Fail-closed corners the production gate inherits from <see cref="SandboxPolicy"/> (documented, not
/// modified — that would be an out-of-scope Core change):</b> a candidate that resolves with a surviving
/// <c>..</c> segment, or a Linux filename literally containing the substring <c>\..\</c> (the policy
/// splits on BOTH <c>/</c> and <c>\</c> as segment boundaries, so <c>\..\</c> reads as a parent segment),
/// is DENIED. The <c>\..\</c>-literal case is a safe over-deny: such a filename is exotic and denying a
/// write to it is the conservative side of the FR-26 invariant.
/// </para>
/// </remarks>
public interface ISandboxGate
{
    /// <summary>
    /// Resolve <paramref name="rawCandidatePath"/> against the open-folder root and decide containment.
    /// </summary>
    /// <returns><see cref="SandboxDecision.Allow"/> iff the resolved candidate is at-or-under the root.</returns>
    SandboxResolution Resolve(string rawCandidatePath);
}

/// <summary>
/// The result of an <see cref="ISandboxGate"/> resolution: the containment verdict plus the resolved
/// canonical path string (so the service can log the path it actually checked, and feed it to the FR-40
/// cap accounting which keys on the canonical path).
/// </summary>
/// <param name="Decision">The FR-26 containment verdict.</param>
/// <param name="ResolvedPath">
/// The canonical, symlink-resolved, normcased path string the verdict was computed over (the
/// <see cref="NormcasedPath.Value"/>). This is what the activity log records and what the FR-40
/// distinct-files set keys on — never the raw, unresolved input.
/// </param>
public readonly record struct SandboxResolution(SandboxDecision Decision, string ResolvedPath);

/// <summary>
/// The production <see cref="ISandboxGate"/>: the real architecture §4.5 step-1 seam —
/// <see cref="IPathResolver.ResolveCanonical"/> then <see cref="SandboxPolicy.Evaluate"/> against the
/// pre-resolved open-folder <see cref="NormcasedPath"/> root the host supplies at folder-open.
/// Platform-free: it owns no syscalls — the resolver is the host's realpath leg, the policy is pure.
/// </summary>
public sealed class PathResolverSandboxGate : ISandboxGate
{
    private readonly IPathResolver _resolver;
    private readonly NormcasedPath _root;
    private readonly string _rootRawForRelativeResolution;

    /// <summary>
    /// Construct the gate for one open-folder session.
    /// </summary>
    /// <param name="resolver">The host's canonicalizing resolver (M2.2).</param>
    /// <param name="root">
    /// The resolved, normcased open-folder sandbox root the host produced at folder-open (architecture
    /// §5.2 — a moved/renamed/deleted root re-prompts before write agency arms, so this is a live,
    /// validated root). Must be non-default.
    /// </param>
    /// <param name="rootRawForRelativeResolution">
    /// The raw root path string used as the base for resolving a relative candidate
    /// (<see cref="IPathResolver.ResolveCanonical"/>'s second arg). Required non-empty.
    /// </param>
    public PathResolverSandboxGate(IPathResolver resolver, NormcasedPath root, string rootRawForRelativeResolution)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (root.IsDefault)
            throw new ArgumentException("The sandbox root must be a resolved NormcasedPath.", nameof(root));
        if (string.IsNullOrEmpty(rootRawForRelativeResolution))
            throw new ArgumentException("A non-empty raw root is required for relative resolution.", nameof(rootRawForRelativeResolution));

        _resolver = resolver;
        _root = root;
        _rootRawForRelativeResolution = rootRawForRelativeResolution;
    }

    /// <inheritdoc />
    public SandboxResolution Resolve(string rawCandidatePath)
    {
        // Canonicalize first (the host's realpath/symlink/normcase leg — the only NormcasedPath producer),
        // THEN apply the pure containment policy. This is the FR-26 "the write NEVER escapes the root"
        // chokepoint: a candidate that resolves outside the root Denies here and never reaches the apply.
        NormcasedPath candidate = _resolver.ResolveCanonical(rawCandidatePath, _rootRawForRelativeResolution);
        SandboxDecision decision = SandboxPolicy.Evaluate(_root, candidate);
        return new SandboxResolution(decision, candidate.Value);
    }
}
