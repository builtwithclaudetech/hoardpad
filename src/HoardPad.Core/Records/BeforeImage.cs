using System.Text.Json.Serialization;

namespace HoardPad.Core.Records;

/// <summary>
/// The §9.4 before-image of a file (the <c>priorContent</c> / <c>destPriorContent</c> shape):
/// either inline bytes or a pointer to a stored before-image blob. <c>kind</c> ∈ {<c>blob</c>,
/// <c>inline</c>}; <c>ref</c> is the blob path (e.g. <c>before/turn-7f3a-03.bin</c>) for a blob.
/// The whole field is <c>null</c> for <c>create</c> (nothing existed before) — modelled as a null
/// <see cref="BeforeImage"/> reference on the entry, not a sentinel here.
/// </summary>
public sealed record BeforeImage(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ref")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref = null);
