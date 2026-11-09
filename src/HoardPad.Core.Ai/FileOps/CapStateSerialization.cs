using System.Collections.Immutable;
using System.Text.Json;
using HoardPad.Core.Ai.Caps;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// Serializes the M4.1 <see cref="CapState"/> to/from the PascalCase JSON the conversation-keyed
/// <see cref="HoardPad.Core.Host.Abstractions.ICapStateStore"/> persists. <b>PascalCase is binding</b>:
/// M5.1's Python deny-by-default hook reads this same cap-state file as PascalCase JSON (architecture
/// §4.4 / the build-context cap-state shape), so the property names MUST stay PascalCase
/// (<c>CreateCount</c>, <c>FilesTouched</c>, …) — the default <see cref="System.Text.Json"/> naming
/// policy (no camelCase) gives exactly that.
/// </summary>
/// <remarks>
/// On deserialize, the <see cref="CapState.FilesTouched"/> set is rebuilt with
/// <see cref="StringComparer.Ordinal"/> — the comparer is not carried in JSON, and the FR-40 set MUST use
/// ordinal comparison (the host supplies already-canonicalized paths; case-folding is the resolver's job,
/// not the set's, or a Windows case variant would double-count). A null/empty payload yields
/// <see cref="CapState.Empty"/> (a never-persisted conversation).
/// </remarks>
public static class CapStateSerialization
{
    // Default options: PascalCase property names (no camelCase policy) — the M5.1 Python-hook contract.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Serialize a <see cref="CapState"/> to its PascalCase JSON payload.</summary>
    public static string Serialize(CapState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(state, Options);
    }

    /// <summary>
    /// Deserialize a cap-state payload, rebuilding the FR-40 set with <see cref="StringComparer.Ordinal"/>.
    /// A <c>null</c> payload (never-persisted conversation) yields <see cref="CapState.Empty"/>.
    /// </summary>
    public static CapState Deserialize(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return CapState.Empty;

        CapState parsed = JsonSerializer.Deserialize<CapState>(payloadJson, Options)
            ?? throw new JsonException("Cap-state payload deserialized to null.");

        // The ImmutableHashSet round-trips its members but not its comparer; rebuild it ordinal so the FR-40
        // distinct-files semantics match CapState.Empty exactly (a path touched twice counts once).
        ImmutableHashSet<string> filesTouched = parsed.FilesTouched is null
            ? ImmutableHashSet.Create<string>(StringComparer.Ordinal)
            : ImmutableHashSet.CreateRange(StringComparer.Ordinal, parsed.FilesTouched);

        return parsed with { FilesTouched = filesTouched };
    }
}
