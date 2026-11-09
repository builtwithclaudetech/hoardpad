using System.Text.Json.Serialization;

namespace HoardPad.Core.Records;

/// <summary>
/// The §9.3/§9.4 <c>paths</c> object: <c>target</c> always present; <c>source</c>+<c>dest</c> present
/// for <c>move</c>/<c>copy</c>. For a clobbering move/copy, <c>dest</c> IS the clobbered destination
/// (so the log/journal records which file was overwritten). Plain path strings — already-resolved
/// host paths the producer supplies; this model does no resolution (MA13).
/// </summary>
public sealed record OpPaths(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("source")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonPropertyName("dest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dest = null);
