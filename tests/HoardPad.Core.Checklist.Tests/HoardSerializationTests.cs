using System.Linq;
using Xunit;

namespace HoardPad.Core.Checklist.Tests;

/// <summary>
/// Binding assertions 2 and 3 (TD §3.5 / PRD §9.1):
/// <list type="number">
/// <item><b>First-re-save canonicalization</b> — a non-canonical (interleaved-unknown-key) file canonicalizes
/// with a documented one-time relocation diff, then is byte-stable on every subsequent save.</item>
/// <item><b>Unknown overflow-bag keys survive a MUTATION</b> — load a file with unknown keys at BOTH the root
/// and item levels, toggle an item's <c>checked</c>, re-serialize, and assert the unknown keys re-emit at both
/// levels (the exact cross-version data-loss the <c>[JsonExtensionData]</c> bag exists to prevent).</item>
/// </list>
/// </summary>
public sealed class HoardSerializationTests
{
    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    // ── Binding assertion 2: first-re-save canonicalization, byte-stable thereafter ──────────────────

    [Fact]
    public void FirstReSaveCanonicalizesWithOneTimeDiffThenIsByteStable()
    {
        var foreign = Normalize(HoardFixtures.ForeignInterleavedV2);
        var expectedCanonical = Normalize(HoardFixtures.ForeignInterleavedV2Canonical);

        var firstReSave = Normalize(HoardSerializer.Serialize(HoardLoader.Load(foreign).Document!));

        // The one-time diff: the interleaved unknown keys relocate to the tail of their object.
        Assert.NotEqual(foreign, firstReSave);
        Assert.Equal(expectedCanonical, firstReSave);

        // Byte-stable on every save thereafter.
        var secondReSave = Normalize(HoardSerializer.Serialize(HoardLoader.Load(firstReSave).Document!));
        Assert.Equal(firstReSave, secondReSave);
    }

    // ── Binding assertion 3: unknown overflow-bag keys survive a mutation at BOTH levels ─────────────

    [Fact]
    public void UnknownOverflowBagKeysSurviveAnItemCheckedMutationAtBothLevels()
    {
        var canonical = Normalize(HoardFixtures.CanonicalCorpus);
        var doc = HoardSerializer.Deserialize(canonical)!;

        // Sanity: the bags are present before the mutation.
        Assert.True(doc.Extra!.ContainsKey("x-root"));
        Assert.True(doc.Items[0].Extra!.ContainsKey("x-item"));

        // MUTATE: toggle the first item's checked state (true → false). This is the path a no-mutation
        // round-trip can pass while silently dropping the bag — the exact regression this guards.
        var items = doc.Items.ToList();
        items[0] = items[0] with { Checked = !items[0].Checked };
        var mutated = doc with { Items = items };

        var reSaved = Normalize(HoardSerializer.Serialize(mutated));

        // Both unknown bags re-emit intact after the mutation.
        Assert.Contains("\"x-root\"", reSaved);
        Assert.Contains("\"x-item\"", reSaved);

        // Re-parse and confirm the bag VALUES survived, not just the keys.
        var roundTripped = HoardSerializer.Deserialize(reSaved)!;
        Assert.True(roundTripped.Extra!.ContainsKey("x-root"));
        Assert.True(roundTripped.Items[0].Extra!.ContainsKey("x-item"));
        Assert.Equal(
            9223372036854775807L,
            roundTripped.Items[0].Extra!["x-item"].GetProperty("big").GetInt64());

        // And the mutation actually took effect.
        Assert.False(roundTripped.Items[0].Checked);
    }

    [Fact]
    public void SettingsLevelUnknownKeySurvivesRoundTrip()
    {
        var foreign = Normalize(HoardFixtures.ForeignInterleavedV2);
        var doc = HoardLoader.Load(foreign).Document!;

        Assert.True(doc.Settings.Extra!.ContainsKey("x-foo"));
        Assert.Equal(42, doc.Settings.Extra!["x-foo"].GetInt32());

        var reSaved = Normalize(HoardSerializer.Serialize(doc));
        Assert.Contains("\"x-foo\": 42", reSaved);
    }
}
