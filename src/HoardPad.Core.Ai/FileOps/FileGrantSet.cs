namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// The immutable set of per-file write grants enforced for ONE AI turn (feature-ai-multidoc-panel §5.1,
/// FR-MD-1/FR-MD-2). Each grant is the <b>resolved, normcased absolute path string</b> of a checked
/// out-of-folder document — the exact same string form
/// <see cref="PathResolverSandboxGate.Resolve"/> returns as <see cref="SandboxResolution.ResolvedPath"/>
/// (i.e. <c>NormcasedPath.Value</c>). A grant authorizes writes to that one path only — never its parent
/// directory, a sibling, a descendant, or a prefix-sharing neighbor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why exact-string equality, keyed <see cref="StringComparer.Ordinal"/>:</b> the App produces each
/// grant path through the SAME <see cref="HoardPad.Core.Host.Abstractions.IPathResolver.ResolveCanonical"/>
/// → normcase leg the write path resolves a candidate through, so both sides are already OS-case-folded
/// (no-op on Linux, case-fold on Windows — the host's job, never re-applied by the domain). Equality must
/// therefore be exact-byte ordinal, matching the hook's <c>os.path.normcase</c> + ordinal comparison
/// (§5.2). Ordinal equality is what defeats the prefix trap: an exact match cannot admit a
/// prefix-sharing sibling (<c>/a/b/c.txt</c> vs <c>/a/b/c.txt2</c>), and a file grant has no descendant
/// arm (FR-MD-2).
/// </para>
/// <para>
/// <b>Immutable per turn (FR-MD-9):</b> the set is frozen at construction; the enforced grant set for a
/// turn cannot be widened or narrowed after it is armed. An empty set is the fail-closed default
/// (FR-MD-10) — a <see cref="UnionSandboxGate"/> composed with an empty <see cref="FileGrantSet"/>
/// behaves byte-identically to the bare root gate.
/// </para>
/// </remarks>
public sealed class FileGrantSet
{
    private readonly HashSet<string> _grants;

    private FileGrantSet(HashSet<string> grants) => _grants = grants;

    /// <summary>The fail-closed empty grant set — no out-of-folder path is admitted (FR-MD-10).</summary>
    public static FileGrantSet Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// Build the enforced grant set for a turn from the resolved, normcased grant paths (FR-MD-5.1).
    /// Each element MUST already be a <see cref="SandboxResolution.ResolvedPath"/>-form string (the App
    /// resolves each checked out-of-folder document's raw path through
    /// <see cref="HoardPad.Core.Host.Abstractions.IPathResolver.ResolveCanonical"/> before calling this).
    /// A <c>null</c>/empty/whitespace entry is dropped (it can never match a resolved candidate, so it
    /// silently widens nothing — fail-closed, FR-MD-10). The resulting set is immutable.
    /// </summary>
    /// <param name="resolvedGrantPaths">The resolved, normcased grant path strings for this turn.</param>
    public static FileGrantSet FromResolvedPaths(IEnumerable<string> resolvedGrantPaths)
    {
        ArgumentNullException.ThrowIfNull(resolvedGrantPaths);

        HashSet<string> set = new(StringComparer.Ordinal);
        foreach (string path in resolvedGrantPaths)
            if (!string.IsNullOrWhiteSpace(path))
                set.Add(path);

        return set.Count == 0 ? Empty : new FileGrantSet(set);
    }

    /// <summary><c>true</c> iff there are no grants (the root-only fail-closed default, FR-MD-10).</summary>
    public bool IsEmpty => _grants.Count == 0;

    /// <summary>The number of distinct per-file grants enforced for this turn.</summary>
    public int Count => _grants.Count;

    /// <summary>
    /// <c>true</c> iff <paramref name="resolvedCandidatePath"/> is EXACTLY (ordinal) one of the granted
    /// resolved paths. This is the §5.2 "degenerate file-as-root equality arm" expressed as a direct
    /// resolved-string comparison — no call into <see cref="HoardPad.Core.Safety.SandboxPolicy"/>, so the
    /// frozen policy is untouched and OQ-MD-1 (file-as-root <see cref="ArgumentException"/> risk) is
    /// sidestepped entirely. An exact string match cannot admit a prefix-sharing sibling, and a file
    /// grant has no descendant arm.
    /// </summary>
    public bool Contains(string resolvedCandidatePath) => _grants.Contains(resolvedCandidatePath);
}
