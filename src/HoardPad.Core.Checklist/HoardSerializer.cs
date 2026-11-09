using System.Text.Encodings.Web;
using System.Text.Json;

namespace HoardPad.Core.Checklist;

/// <summary>Pinned schema constants for the <c>.hoard</c> v2 contract (§9.1).</summary>
public static class HoardSchema
{
    /// <summary>The current canonical schema version (§9.1, ODQ-11). Files above this are rejected.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The maximum derived depth (strict-ancestor count): tiers 0/1/2 — three tiers (§9.1 D4).</summary>
    public const int MaxDepth = 2;

    /// <summary>The default <c>settings.checkedItemMode</c> (§9.1, FR-33).</summary>
    public const string DefaultCheckedItemMode = "strikethrough-in-place";
}

/// <summary>
/// PURE deterministic serialization of a <see cref="HoardDocument"/> (§9.1 / architecture §5.1 / TD §3).
/// No I/O, no clock — the caller owns reading/writing the bytes. The contract this pins:
/// <list type="bullet">
/// <item><b>Encoder</b> = <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> — literal non-ASCII
/// (TD §3.2(a)), the smallest, most legible diffs for the maintainer's synced files. "Unsafe" is the HTML-injection
/// context name only; a local <c>.hoard</c> JSON file is never an HTML attribute context.</item>
/// <item><b>Both-level <c>[JsonExtensionData]</c></b> — root + per-item (+ settings) overflow bags re-emit
/// unknown keys; STJ emits each bag after its known properties in preserved within-bag insertion order.</item>
/// <item><b>Fixed property order</b> — the model's <c>[JsonPropertyOrder]</c> pins the canonical order
/// (TD §3.4); combined with the single pinned encoder this makes a canonical-form file byte-identical on
/// re-save (the MA2 invariant).</item>
/// </list>
/// <c>WriteIndented</c> is on (the §9.1 example is pretty-printed) with the default 2-space indent, so the
/// on-disk form is human-legible in a <c>git diff</c>. Typed numeric fields (<c>order</c>,
/// <c>schemaVersion</c>) are normalized by STJ's number writer (fine — HoardPad controls them, TD §3.6);
/// only overflow-bag <c>JsonElement</c> values carry the raw-text guarantee.
/// </summary>
public static class HoardSerializer
{
    /// <summary>
    /// The single pinned options instance every HoardPad build uses, so escaping + ordering never differ
    /// build-to-build (TD §3.2(a)). It is the ONE serializer for both <see cref="Serialize"/> and
    /// <see cref="Deserialize"/>.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Serialize a document to its canonical UTF-8 JSON string (deterministic; byte-stable for a
    /// canonical-form document). No trailing newline is appended (the caller owns framing).</summary>
    public static string Serialize(HoardDocument document) =>
        JsonSerializer.Serialize(document, Options);

    /// <summary>
    /// Deserialize raw JSON to a <see cref="HoardDocument"/> with NO migration/repair applied. This is the
    /// low-level parse; callers that need v1→v2 migration, version rejection, and dangling/cyclic/over-deep
    /// repair use <see cref="HoardLoader.Load"/> instead. Returns <c>null</c> only for a literal JSON
    /// <c>null</c> token; malformed JSON throws <see cref="JsonException"/> (a genuinely exceptional input,
    /// per house error-handling style — not an expected-failure result).
    /// </summary>
    public static HoardDocument? Deserialize(string json) =>
        JsonSerializer.Deserialize<HoardDocument>(json, Options);
}
