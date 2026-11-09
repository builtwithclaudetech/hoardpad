using System.Text.Json;
using Xunit;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// Binding assertion 1 (TD §3.5 / PRD §9.1) — the <b>canonical round-trip is byte-identical</b> over a
/// corpus covering integers, large/64-bit integers, decimals, nulls, nested objects, and unicode strings at
/// BOTH object levels (root + per-item overflow bags), plus nested items with a parent chain and per-sibling
/// contiguous order. <c>serialize → deserialize → serialize</c> yields byte-identical output.
/// </summary>
public sealed class HoardRoundTripTests
{
    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void CanonicalCorpusIsAByteIdenticalFixedPoint()
    {
        var canonical = Normalize(HoardFixtures.CanonicalCorpus);

        // Load applies migration/repair (none needed here) then we re-serialize.
        var reSaved = Normalize(HoardSerializer.Serialize(HoardLoader.Load(canonical).Document!));

        Assert.Equal(canonical, reSaved);
    }

    [Fact]
    public void SerializeDeserializeSerializeIsByteIdentical()
    {
        var canonical = Normalize(HoardFixtures.CanonicalCorpus);

        var doc = HoardSerializer.Deserialize(canonical)!;
        var once = Normalize(HoardSerializer.Serialize(doc));
        var twice = Normalize(HoardSerializer.Serialize(HoardSerializer.Deserialize(once)!));

        Assert.Equal(canonical, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void SixtyFourBitAndDecimalAndScientificBagValuesRoundTripExact()
    {
        var doc = HoardSerializer.Deserialize(Normalize(HoardFixtures.CanonicalCorpus))!;
        var itemBag = doc.Items[0].Extra!["x-item"];

        Assert.Equal(9223372036854775807L, itemBag.GetProperty("big").GetInt64());
        // Raw text of the decimal is preserved verbatim (trailing zero kept) — the MA2 numbers leg.
        Assert.Equal("1.50", itemBag.GetProperty("dec").GetRawText());
        Assert.Equal("1E10", itemBag.GetProperty("sci").GetRawText());
        Assert.Equal("-0.0", itemBag.GetProperty("negzero").GetRawText());
        Assert.Equal(JsonValueKind.Null, itemBag.GetProperty("nil").ValueKind);
    }

    [Fact]
    public void UnicodeIsEmittedLiteralAtBothObjectLevels()
    {
        var canonical = Normalize(HoardFixtures.CanonicalCorpus);
        var reSaved = Normalize(HoardSerializer.Serialize(HoardSerializer.Deserialize(canonical)!));

        // The pinned UnsafeRelaxedJsonEscaping emits literal non-ASCII and literal '<' as the canonical form.
        Assert.Contains("café ☕ <tag>", reSaved, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("naïve ☃", reSaved);
        Assert.Contains("Flights ✈", reSaved);
        Assert.DoesNotContain("\\u", reSaved);
    }
}
