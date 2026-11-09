using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoardPad.Core.Records;

/// <summary>
/// A <see cref="JsonStringEnumConverter"/> pre-seeded with <see cref="JsonNamingPolicy.KebabCaseLower"/>,
/// so PascalCase enum members serialize to their §9.3/§9.4 kebab-lower wire tokens
/// (e.g. <c>ClaudeCli</c> → <c>claude-cli</c>, <c>HomeRoot</c> → <c>home-root</c>, <c>Move</c> → <c>move</c>).
/// Applied via <c>[JsonConverter(typeof(KebabLowerEnumConverter))]</c> on each vocabulary enum so the
/// persisted form is pinned by the type, independent of any consumer's serializer options.
/// </summary>
public sealed class KebabLowerEnumConverter() : JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower);
