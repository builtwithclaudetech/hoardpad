namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// Canonical and foreign <c>.hoard</c> JSON fixtures shared across the serialization, round-trip, and
/// migration/repair tests. Every CANONICAL fixture is byte-for-byte what <see cref="HoardSerializer.Serialize"/>
/// emits (2-space indent, literal non-ASCII via <c>UnsafeRelaxedJsonEscaping</c>, fixed property order,
/// overflow-bag keys after known properties in within-bag insertion order). Raw string literals are
/// de-indented to column 0 by the closing delimiter, so the content carries no test-file indentation.
/// </summary>
internal static class HoardFixtures
{
    /// <summary>
    /// CANONICAL v2 corpus — covers, at BOTH object levels (root <c>x-root</c> bag + item <c>x-item</c> bag):
    /// 64-bit integer, decimal with trailing zero, scientific notation, negative zero, null, nested object,
    /// and literal unicode (<c>é</c>, an emoji, and a literal <c>&lt;</c>). Nested items with a parent chain
    /// and per-sibling-group contiguous order. This is the byte-stable canonical form.
    /// </summary>
    public const string CanonicalCorpus =
"""
{
  "schemaVersion": 2,
  "title": "Voyage café ☕ <tag>",
  "settings": {
    "checkedItemMode": "move-to-bottom"
  },
  "items": [
    {
      "id": "01ROOT",
      "text": "Trip planning é",
      "checked": true,
      "parentId": null,
      "order": 0,
      "collapsed": false,
      "x-item": {
        "big": 9223372036854775807,
        "dec": 1.50,
        "sci": 1E10,
        "negzero": -0.0,
        "nil": null
      }
    },
    {
      "id": "02CHILD",
      "text": "Flights ✈",
      "checked": false,
      "parentId": "01ROOT",
      "order": 0,
      "collapsed": false
    },
    {
      "id": "03CHILD",
      "text": "Hotel",
      "checked": false,
      "parentId": "01ROOT",
      "order": 1,
      "collapsed": false
    }
  ],
  "x-root": {
    "nested": {
      "a": 1,
      "b": [
        true,
        false,
        null
      ]
    },
    "unicode": "naïve ☃"
  }
}
""";

    /// <summary>
    /// A NON-CANONICAL foreign v2 file: an unknown root key is INTERLEAVED between known root keys, and an
    /// unknown item key is interleaved between known item keys. The first re-save relocates both bags to the
    /// tail of their object (the accepted one-time canonicalizing diff, TD §3.2(b)).
    /// </summary>
    public const string ForeignInterleavedV2 =
"""
{
  "schemaVersion": 2,
  "title": "Café",
  "x-unknown-root": {"big": 9223372036854775807, "dec": 1.50, "n": null},
  "settings": {"checkedItemMode": "move-to-bottom", "x-foo": 42},
  "items": [
    {
      "id": "01J",
      "x-item-extra": "keep me",
      "text": "A <b>",
      "checked": false,
      "parentId": null,
      "order": 0,
      "collapsed": false
    }
  ]
}
""";

    /// <summary>The canonical form the foreign interleaved file re-saves to (bags relocated to the tail).</summary>
    public const string ForeignInterleavedV2Canonical =
"""
{
  "schemaVersion": 2,
  "title": "Café",
  "settings": {
    "checkedItemMode": "move-to-bottom",
    "x-foo": 42
  },
  "items": [
    {
      "id": "01J",
      "text": "A <b>",
      "checked": false,
      "parentId": null,
      "order": 0,
      "collapsed": false,
      "x-item-extra": "keep me"
    }
  ],
  "x-unknown-root": {
    "big": 9223372036854775807,
    "dec": 1.50,
    "n": null
  }
}
""";

    /// <summary>A v1 file — no <c>parentId</c>/<c>collapsed</c> on its items, <c>schemaVersion: 1</c>.</summary>
    public const string V1File =
"""
{
  "schemaVersion": 1,
  "title": "Old list",
  "settings": {
    "checkedItemMode": "strikethrough-in-place"
  },
  "items": [
    {
      "id": "A1",
      "text": "First",
      "checked": false,
      "order": 0
    },
    {
      "id": "A2",
      "text": "Second",
      "checked": true,
      "order": 1
    }
  ]
}
""";
}
