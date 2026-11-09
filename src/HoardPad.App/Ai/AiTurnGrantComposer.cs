// AiTurnGrantComposer.cs — the production per-turn Archetype-B grant composer (PANEL-2,
// feature-ai-multidoc-panel §5.4 / FR-MD-1/2/5.1/9/10). Constructed once per open-folder session by
// AiSessionBinder over the SAME dependencies the session's write path uses, so a grant it resolves is
// byte-comparable to what the session gate returns.
//
// THE SOUNDNESS-CRITICAL INVARIANT (FR-MD-5.1): each checked out-of-folder path is resolved through the
// EXACT leg the session's PathResolverSandboxGate resolves candidates through —
//   _resolver.ResolveCanonical(rawPath, _rawRootBase)
// where _rawRootBase is the SAME raw open-folder-root string passed as PathResolverSandboxGate's
// `rootRawForRelativeResolution` at construction. PANEL-1's FileGrantSet compares by ordinal string
// equality against exactly SandboxResolution.ResolvedPath (== NormcasedPath.Value from that same call), so
// any divergence in resolver instance OR root-base would silently fail a legitimate grant (fail-closed, but
// a real usability bug). The composer therefore takes the resolver + the raw root base from the binder and
// uses NOTHING else to resolve a grant path.

using System.Collections.Generic;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Ai;

/// <summary>
/// The production <see cref="IAiTurnGrantComposer"/> (feature-ai-multidoc-panel §5.4). Session-scoped over
/// the resolver + root gate + file-ops dependencies; composes the per-turn union gate + file-ops service.
/// </summary>
internal sealed class AiTurnGrantComposer : IAiTurnGrantComposer
{
    private readonly IPathResolver _resolver;
    private readonly string _rawRootBase;
    private readonly ISandboxGate _rootGate;
    private readonly IAtomicFileStore _store;
    private readonly ICapStateStore _capStore;
    private readonly IAppDataPaths _paths;
    private readonly string _sessionId;
    private readonly CapConfig _capConfig;

    /// <summary>Construct the session-scoped composer.</summary>
    /// <param name="resolver">The host resolver — the ONLY <c>NormcasedPath</c> producer (FR-MD-5.1 leg).</param>
    /// <param name="rawRootBase">
    /// The SAME raw open-folder-root string passed to the session's
    /// <see cref="PathResolverSandboxGate"/> as its relative-resolution base. A grant path is resolved with
    /// exactly this base so the resulting resolved string is byte-comparable to what the gate returns.
    /// </param>
    /// <param name="rootGate">The session root gate (the frozen <see cref="PathResolverSandboxGate"/>) — the inner union gate.</param>
    /// <param name="store">The host atomic store (the per-turn service's writer).</param>
    /// <param name="capStore">The conversation-keyed cap-state store.</param>
    /// <param name="paths">The per-user data paths (activity log + undo journal).</param>
    /// <param name="sessionId">The session id keying the activity log.</param>
    /// <param name="capConfig">The cap config in force.</param>
    public AiTurnGrantComposer(
        IPathResolver resolver,
        string rawRootBase,
        ISandboxGate rootGate,
        IAtomicFileStore store,
        ICapStateStore capStore,
        IAppDataPaths paths,
        string sessionId,
        CapConfig capConfig)
    {
        System.ArgumentNullException.ThrowIfNull(resolver);
        System.ArgumentException.ThrowIfNullOrEmpty(rawRootBase);
        System.ArgumentNullException.ThrowIfNull(rootGate);
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(capStore);
        System.ArgumentNullException.ThrowIfNull(paths);
        System.ArgumentException.ThrowIfNullOrEmpty(sessionId);
        System.ArgumentNullException.ThrowIfNull(capConfig);

        _resolver = resolver;
        _rawRootBase = rawRootBase;
        _rootGate = rootGate;
        _store = store;
        _capStore = capStore;
        _paths = paths;
        _sessionId = sessionId;
        _capConfig = capConfig;
    }

    /// <inheritdoc />
    public TurnGrantComposition ComposeTurn(IReadOnlyList<string> checkedGrantablePaths)
    {
        System.ArgumentNullException.ThrowIfNull(checkedGrantablePaths);

        // Resolve the checked paths ONCE (FR-MD-5.1) into a de-duped ordinal list. The SAME list feeds both
        // the FileGrantSet (Archetype-B UnionSandboxGate enforcement) AND the composition's ResolvedGrantPaths
        // (the strings PANEL-A2's host grants-file writer hands the Archetype-A hook), so the two archetypes
        // enforce byte-identical grant sets — never a resolve-twice divergence.
        IReadOnlyList<string> resolvedGrantPaths = ResolveGrants(checkedGrantablePaths);
        FileGrantSet grants = FileGrantSet.FromResolvedPaths(resolvedGrantPaths);

        // Compose the per-turn union gate over the session root gate (root ∪ grants). An empty grant set
        // makes this byte-identical to the bare root gate (FR-MD-10). The grant set is immutable for the
        // turn (FR-MD-9) — the panel discards this composition at turn end and composes a fresh one next arm.
        var turnGate = new UnionSandboxGate(_rootGate, grants);

        // Build the per-turn file-ops service over the union gate. Every arg other than the gate is
        // session-stable, so this is dependency composition at the call site — the frozen AiFileOpsService
        // is CONSTRUCTED (exactly as the binder constructs the session service), never modified.
        var turnFileOps = new AiFileOpsService(turnGate, _store, _capStore, _paths, _sessionId, _capConfig);

        return new TurnGrantComposition(grants, turnFileOps, resolvedGrantPaths);
    }

    /// <inheritdoc />
    public AiFileOpsService ComposeReplayFileOps(string resolvedGrantPath)
    {
        // OQ-MD-5 option ii: compose the per-op union-gated write path for an FR-39 replay. The grant is
        // ALREADY resolved (persisted from the turn that produced the parked op) — do NOT re-resolve it, or a
        // divergent normalization could silently drop a legitimate grant. Feed it straight into a single-element
        // FileGrantSet, wrap the SAME session root gate in a UnionSandboxGate, and CONSTRUCT the frozen
        // AiFileOpsService over it — identical composition to ComposeTurn, with a one-path grant set. An
        // empty/whitespace grant yields FileGrantSet.Empty (root-only, fail-closed).
        FileGrantSet grants = FileGrantSet.FromResolvedPaths(
            string.IsNullOrWhiteSpace(resolvedGrantPath)
                ? System.Array.Empty<string>()
                : new[] { resolvedGrantPath });

        var turnGate = new UnionSandboxGate(_rootGate, grants);
        return new AiFileOpsService(turnGate, _store, _capStore, _paths, _sessionId, _capConfig);
    }

    /// <inheritdoc />
    public string? TryMatchGrantForTarget(
        string rawTargetPath,
        IReadOnlyCollection<string> turnResolvedGrants)
    {
        if (string.IsNullOrWhiteSpace(rawTargetPath) || turnResolvedGrants.Count == 0)
            return null;

        // Resolve the parked op's raw target through the EXACT session leg (FR-MD-5.1), then check exact
        // ordinal membership in the turn's armed grants. A resolution failure or a non-member returns null —
        // the op is then treated as in-root (root-only replay) or unreplayable-out-of-folder (Refused later).
        NormcasedPath resolved;
        try
        {
            resolved = _resolver.ResolveCanonical(rawTargetPath, _rawRootBase);
        }
#pragma warning disable CA1031 // An unresolvable target simply carries no grant (fail-closed).
        catch (System.Exception)
        {
            return null;
        }
#pragma warning restore CA1031

        if (resolved.IsDefault)
            return null;

        // The turn's grants are ordinal-resolved strings; compare directly.
        foreach (string grant in turnResolvedGrants)
            if (string.Equals(grant, resolved.Value, System.StringComparison.Ordinal))
                return grant;

        return null;
    }

    /// <summary>
    /// Resolve the checked grant paths through the EXACT session resolver leg (FR-MD-5.1) into a de-duped
    /// ordinal list. Each raw path is canonicalized + normcased via <c>ResolveCanonical(path, _rawRootBase)</c>
    /// — the same call the session gate makes — and the resulting <c>NormcasedPath.Value</c> is the grant
    /// string. A path that throws on resolution contributes NO grant (fail-closed, FR-MD-10); an empty input
    /// yields an empty list. De-duped ordinally so the list matches the <see cref="FileGrantSet"/> membership
    /// exactly (an in-root checked path is harmless — the union gate's root-Allow arm already covers it, so
    /// the grant membership is never consulted for it; it widens nothing, FR-MD-1).
    /// </summary>
    private IReadOnlyList<string> ResolveGrants(IReadOnlyList<string> checkedGrantablePaths)
    {
        if (checkedGrantablePaths.Count == 0)
            return System.Array.Empty<string>();

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var resolved = new List<string>(checkedGrantablePaths.Count);
        foreach (string rawPath in checkedGrantablePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            // The SAME resolver + root-base the session gate uses (FR-MD-5.1). No divergent resolver, no
            // divergent base — otherwise the grant string would not match the gate's ResolvedPath.
            NormcasedPath canonical;
            try
            {
                canonical = _resolver.ResolveCanonical(rawPath, _rawRootBase);
            }
#pragma warning disable CA1031 // A path that cannot be resolved simply grants nothing (fail-closed, FR-MD-10).
            catch (System.Exception)
            {
                continue;
            }
#pragma warning restore CA1031

            if (!canonical.IsDefault && seen.Add(canonical.Value))
                resolved.Add(canonical.Value);
        }

        return resolved;
    }
}
