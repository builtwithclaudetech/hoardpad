// IAiTurnGrantComposer.cs — the session-scoped, per-turn Archetype-B grant composer seam (PANEL-2,
// feature-ai-multidoc-panel §5.4). The AiSessionBinder builds ONE composer per open-folder session (it
// owns the EXACT IPathResolver + raw-root-base leg the session's PathResolverSandboxGate resolves against)
// and hands it to the AI panel at BindSession. At each turn-arm the panel calls ComposeTurn with the
// checked out-of-folder document paths; the composer resolves each through that SAME leg (FR-MD-5.1 — the
// mandatory same-resolver invariant), builds the immutable per-turn FileGrantSet, wraps the session root
// gate in a UnionSandboxGate, and returns a per-turn AiFileOpsService composed over that union gate.
//
// The panel uses the returned service as THE write path for the turn (Archetype B: replay / apply). An
// empty checked set yields FileGrantSet.Empty → the union gate behaves byte-identically to the root gate
// (root-only, fail-closed default FR-MD-10). NO frozen artifact is modified: the composer CONSTRUCTS the
// frozen AiFileOpsService + PathResolverSandboxGate (constructing them is what the binder already does) and
// COMPOSES the new UnionSandboxGate around them — it edits none of them.

using HoardPad.Core.Ai.FileOps;

namespace HoardPad.App.Ai;

/// <summary>
/// Composes the per-turn Archetype-B write path for the AI multi-doc panel (feature-ai-multidoc-panel §5.4).
/// Session-scoped: constructed once per open folder over the session's resolver + root gate + file-ops
/// dependencies. <see cref="ComposeTurn"/> resolves the checked grant paths through the SAME resolver leg the
/// root gate uses (FR-MD-5.1) and returns the union-gated service the turn enforces its writes through.
/// </summary>
public interface IAiTurnGrantComposer
{
    /// <summary>
    /// Compose the write path for ONE turn from the checked out-of-folder document paths (FR-MD-1/2/9).
    /// Resolves each raw path through <c>IPathResolver.ResolveCanonical(path, rawRootBase)</c> — the EXACT
    /// leg + root-base the session's <c>PathResolverSandboxGate</c> uses, so a granted resolved path is
    /// byte-comparable to what the gate returns for that candidate (the same-resolver invariant). An in-root
    /// checked path adds nothing to containment (it is already Allowed by the root gate). A path that fails
    /// to resolve, or an empty set, yields no grant for that entry (fail-closed, FR-MD-10).
    /// </summary>
    /// <param name="checkedGrantablePaths">The raw on-disk paths of the checked, non-scratch documents.</param>
    /// <returns>
    /// The <see cref="TurnGrantComposition"/>: the immutable per-turn <see cref="FileGrantSet"/> and the
    /// per-turn <see cref="AiFileOpsService"/> composed over a <see cref="UnionSandboxGate"/> wrapping the
    /// session root gate. The service is the write path the panel uses for this turn.
    /// </returns>
    TurnGrantComposition ComposeTurn(System.Collections.Generic.IReadOnlyList<string> checkedGrantablePaths);

    /// <summary>
    /// OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): compose the per-op union-gated write path for an
    /// FR-39 REPLAY of a parked out-of-folder op that carries a single already-resolved grant. The grant is
    /// NOT re-resolved (it is already a <see cref="HoardPad.Core.Host.Abstractions.IPathResolver"/>-resolved
    /// string persisted from the turn that produced it) — it feeds a single-element
    /// <see cref="FileGrantSet.FromResolvedPaths"/>, wrapped in a <see cref="UnionSandboxGate"/> over the
    /// session root gate, exactly as <see cref="ComposeTurn"/> composes the per-turn service. The op's
    /// out-of-folder write is thereby admitted by the service gate while an in-root op is untouched. An
    /// empty/whitespace grant yields <see cref="FileGrantSet.Empty"/> ⇒ root-only (fail-closed).
    /// </summary>
    /// <param name="resolvedGrantPath">The single resolved, normcased grant path for the replayed op.</param>
    /// <returns>The per-op union-gated <see cref="AiFileOpsService"/> the replay writes through.</returns>
    AiFileOpsService ComposeReplayFileOps(string resolvedGrantPath);

    /// <summary>
    /// OQ-MD-5 option ii: resolve <paramref name="rawTargetPath"/> through the EXACT session resolver leg and,
    /// if the resolved path is EXACTLY one of <paramref name="turnResolvedGrants"/> (ordinal), return it so
    /// the App can persist it as the parked op's grant. Returns <c>null</c> when the target is in-root or not
    /// among the turn's grants (an in-root op needs no grant; a non-granted out-of-root op is not replayable).
    /// Uses the SAME <c>ResolveCanonical(path, rawRootBase)</c> leg the session gate + <see cref="ComposeTurn"/>
    /// use, so the persisted grant is byte-comparable to what the union reader/gate resolve for that target.
    /// </summary>
    /// <param name="rawTargetPath">The raw target path of the op being parked (as the proposal specified it).</param>
    /// <param name="turnResolvedGrants">The resolved grant strings armed for the turn that produced the op.</param>
    /// <returns>The matching resolved grant string, or <c>null</c> if the target is in-root / ungranted.</returns>
    string? TryMatchGrantForTarget(
        string rawTargetPath,
        System.Collections.Generic.IReadOnlyCollection<string> turnResolvedGrants);
}

/// <summary>
/// The result of a <see cref="IAiTurnGrantComposer.ComposeTurn"/> call — the immutable per-turn grant set
/// and the union-gated file-ops service the turn enforces its Archetype-B writes through (FR-MD-9: both are
/// frozen for the turn; a checkbox toggled mid-turn affects only the NEXT ComposeTurn).
/// </summary>
/// <param name="Grants">The immutable per-file grant set for this turn (root-only when empty, FR-MD-10).</param>
/// <param name="TurnFileOps">
/// The per-turn <see cref="AiFileOpsService"/> composed over a <see cref="UnionSandboxGate"/> (root gate ∪
/// the grants). Byte-identical to the session's root-gated service when the grant set is empty.
/// </param>
/// <param name="ResolvedGrantPaths">
/// PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): the resolved, normcased grant path strings this
/// composition's <see cref="Grants"/> was built from — the SAME strings the Archetype-B
/// <see cref="UnionSandboxGate"/> enforces. The App hands these to the host grants-file writer
/// (<see cref="HoardPad.Core.Host.Abstractions.IFileGrantsWriter"/>) so Archetype A (claude -p, via the
/// HOARDPAD_FILE_GRANTS hook leg) admits the identical set. Empty ⇒ root-only (FR-MD-10).
/// </param>
public sealed record TurnGrantComposition(
    FileGrantSet Grants,
    AiFileOpsService TurnFileOps,
    System.Collections.Generic.IReadOnlyList<string> ResolvedGrantPaths);
