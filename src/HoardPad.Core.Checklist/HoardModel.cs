using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoardPad.Core.Checklist;

/// <summary>
/// The root <c>.hoard</c> document (PRD §9.1 / architecture §5.1, schemaVersion 2). A FLAT model:
/// nesting is derived from <see cref="HoardItem.ParentId"/>, never a nested-tree JSON (§9.1 D1).
/// Property order is the fixed canonical order (TD §3.4): <c>schemaVersion</c>, <c>title</c>,
/// <c>settings</c>, <c>items</c> — pinned with <see cref="JsonPropertyOrderAttribute"/> so the
/// serialized form is byte-stable regardless of compiler/runtime member ordering. The
/// <see cref="Extra"/> root-level <c>[JsonExtensionData]</c> overflow bag captures unknown keys a
/// newer build wrote and re-emits them on save (§9.1 "the overflow bag is required") — without it
/// every mutation silently drops cross-version keys (a real data-loss path).
/// </summary>
public sealed record HoardDocument
{
    /// <summary>Schema version. Canonical current = <c>2</c>. A file with a HIGHER version is REJECTED
    /// by <see cref="HoardLoader"/> (never silently downgraded); a <c>1</c> file is migrated to v2.</summary>
    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; init; } = HoardSchema.CurrentVersion;

    /// <summary>Display title (§9.1). May be empty string but the key is always present.</summary>
    [JsonPropertyName("title")]
    [JsonPropertyOrder(1)]
    public string Title { get; init; } = string.Empty;

    /// <summary>The §9.1 <c>settings</c> object.</summary>
    [JsonPropertyName("settings")]
    [JsonPropertyOrder(2)]
    public HoardSettings Settings { get; init; } = new();

    /// <summary>The flat item array (§9.1). May be empty; nesting is derived from <c>parentId</c>.</summary>
    [JsonPropertyName("items")]
    [JsonPropertyOrder(3)]
    public IReadOnlyList<HoardItem> Items { get; init; } = [];

    /// <summary>Root-level <c>[JsonExtensionData]</c> overflow bag (§9.1). Unknown root keys a newer
    /// build wrote survive a load → mutate → save round-trip. STJ emits the bag AFTER all known
    /// properties in preserved within-bag insertion order (the accepted one-time relocation, TD §3.2(b)).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// The §9.1 <c>settings</c> object. <c>checkedItemMode</c> is kept as a plain string (not a typed enum)
/// so an unknown/foreign mode value round-trips deterministically rather than throwing on load; the
/// three valid modes are a UI concern, not a serialization gate. The <see cref="Extra"/> bag lets
/// unknown settings keys survive (§9.1 overflow-bag-at-both-levels is about root+item, but a settings
/// bag costs nothing and keeps a foreign-authored settings block lossless).
/// </summary>
public sealed record HoardSettings
{
    /// <summary>One of <c>strikethrough-in-place</c> (default), <c>move-to-bottom</c>,
    /// <c>hide-when-checked</c> (FR-33). Stored as a string for lossless round-trip.</summary>
    [JsonPropertyName("checkedItemMode")]
    [JsonPropertyOrder(0)]
    public string CheckedItemMode { get; init; } = HoardSchema.DefaultCheckedItemMode;

    /// <summary>Settings-level overflow bag — unknown settings keys survive a round-trip.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// One checklist item (§9.1, schemaVersion 2). Fixed canonical property order (TD §3.4):
/// <c>id</c>, <c>text</c>, <c>checked</c>, <c>parentId</c>, <c>order</c>, <c>collapsed</c> — pinned with
/// <see cref="JsonPropertyOrderAttribute"/>. The <see cref="Extra"/> per-item <c>[JsonExtensionData]</c>
/// overflow bag is the SECOND of the two required bag levels (§9.1): unknown per-item keys survive a
/// mutation (e.g. toggling <see cref="Checked"/>) and re-emit intact.
/// </summary>
public sealed record HoardItem
{
    /// <summary>Stable unique id (ULID for new items; GUID accepted on read). Never reused/reassigned.</summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(0)]
    public string Id { get; init; } = string.Empty;

    /// <summary>Item label. May be empty string (transient while editing); persisted as-is.</summary>
    [JsonPropertyName("text")]
    [JsonPropertyOrder(1)]
    public string Text { get; init; } = string.Empty;

    /// <summary>Check state. <c>checked</c> = "completed" uniformly (§9.1 D2); on a parent it also
    /// locks the subtree (FR-41), but the schema stores a single bool with this one meaning.</summary>
    [JsonPropertyName("checked")]
    [JsonPropertyOrder(2)]
    public bool Checked { get; init; }

    /// <summary><c>null</c> = tier-0 (top) item; otherwise the <c>id</c> of the parent (§9.1, v2).
    /// Must reference an existing item, no cycles, no dangling — <see cref="HoardLoader"/> repairs a
    /// dangling/cyclic value to <c>null</c> on load. Default on absence (v1/foreign) = <c>null</c>.</summary>
    [JsonPropertyName("parentId")]
    [JsonPropertyOrder(3)]
    public string? ParentId { get; init; }

    /// <summary>0-based position within the item's sibling group (same <c>parentId</c>), contiguous
    /// <c>0..n-1</c> per group. <see cref="HoardLoader"/> renormalizes after repair/clamp (§9.1).</summary>
    [JsonPropertyName("order")]
    [JsonPropertyOrder(4)]
    public int Order { get; init; }

    /// <summary>View state of a parent's subtree (▸ collapsed / ▾ expanded), v2. Meaningful only for
    /// items with children; preserved-but-ignored for leaves. Default on absence = <c>false</c>.</summary>
    [JsonPropertyName("collapsed")]
    [JsonPropertyOrder(5)]
    public bool Collapsed { get; init; }

    /// <summary>Per-item <c>[JsonExtensionData]</c> overflow bag (§9.1). Unknown item keys survive a
    /// mutation round-trip — the exact cross-version data-loss the bag exists to prevent (PRD §9.1).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }
}
