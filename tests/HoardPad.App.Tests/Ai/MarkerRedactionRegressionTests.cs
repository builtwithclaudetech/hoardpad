// MarkerRedactionRegressionTests.cs — DS-4 (docs/decisions.md DR-DS-1, docs/nightly-schedule.md).
//
// Unit-level regression suite against SettingsChangeProposalParser.StripMarker — the production
// wrapper now delegating to DragonScrub's BlockScanner.RedactBlock (DS-3). Where
// DragonScrubCapabilityProbeTests (DS-2) confirmed the RAW library primitive against the exact
// production call shape, this suite drives the PRODUCTION seam itself (StripMarker) with one test per
// entry in the 5-round hostile-review bypass catalog documented on StripMarker's XML doc and in
// docs/tasklist.md's QA4-17/F8 notes, PLUS the 3 F8 encoding evasions. Deliberately does NOT re-plumb
// the full AiPanelViewModel/IAiTurnRunner scaffolding Qa417AiSettingsChangeTests.cs already uses for
// its integration-level proof that StripMarker is called on every terminal path (ReadOnly/cancel/
// fault) — that wiring proof stands. This suite instead proves the PURE-FUNCTION property that makes
// that unconditional wiring safe in the first place: StripMarker's behavior depends only on the text
// it is given, never on which terminal path produced that text shape, so calling it unconditionally
// on every path needs no per-path special-casing.
//
// All green + F8 checked off = docs/tasklist.md F8 CLOSES.

using HoardPad.App.Ai;
using Xunit;

namespace HoardPad.App.Tests.Ai;

public sealed class MarkerRedactionRegressionTests
{
    // -----------------------------------------------------------------------------------------------
    // Catalog entry 1: lazy-JSON premature close (embedded fake "} ``` " sequence inside a value).
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void LazyJsonPrematureCloseNeverSmugglesASecretPastTheStrip()
    {
        // A single payload line containing: a plausible-looking JSON prefix, a FAKE "} ``` " close, the
        // secret, a FAKE re-open "``` {", then a closing brace — followed by the REAL closing fence line.
        string smuggledPayloadLine =
            "{\"field\": \"AnthropicApiKey\", \"value\": \"prefix} ``` sk-ant-SMUGGLED-SECRET ``` {\"suffix\"}";
        string marker = "```hoardpad-settings\n" + smuggledPayloadLine + "\n```";
        string text = "Sure!\n" + marker + "\nDone.";

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("sk-ant-SMUGGLED-SECRET", stripped);
        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.Contains("Sure!", stripped);
        Assert.Contains("Done.", stripped);
    }

    // -----------------------------------------------------------------------------------------------
    // Catalog entry 2: non-canonical case fence.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void NonCanonicalCaseFenceIsStillStrippedInFull()
    {
        string marker = "```HoardPad-Settings\n{\"field\": \"AnthropicApiKey\", \"value\": \"sk-case-variant-leak\"}\n```";
        string text = "Sure!\n" + marker + "\nDone.";

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("sk-case-variant-leak", stripped);
        Assert.DoesNotContain("hoardpad-settings", stripped, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sure!", stripped);
        Assert.Contains("Done.", stripped);
    }

    // -----------------------------------------------------------------------------------------------
    // Catalog entry 3: marker in a NON-first block — proves all-blocks coverage, not first-block-only.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void MarkerInASecondFencedBlockIsStrippedEvenThoughAnUnrelatedFencePrecedesIt()
    {
        string text =
            "Here's the snippet you asked about:\n"
            + "```csharp\n"
            + "Console.WriteLine(\"hi\");\n"
            + "```\n"
            + "And here's the settings change:\n"
            + "```hoardpad-settings\n"
            + "{\"field\": \"AnthropicApiKey\", \"value\": \"sk-second-block-leak\"}\n"
            + "```\n"
            + "Done.";

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("sk-second-block-leak", stripped);
        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.Contains("Console.WriteLine(\"hi\");", stripped); // the unrelated FIRST block is untouched.
        Assert.Contains("Here's the snippet you asked about:", stripped);
        Assert.Contains("Done.", stripped);
    }

    // -----------------------------------------------------------------------------------------------
    // Catalog entry 4: malformed/unparseable JSON block still stripped — parse-independent.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void MalformedUnparseableJsonBlockIsStrippedRegardlessOfTryParseFailing()
    {
        // Unescaped quote inside the JSON string value — invalid JSON (TryParse would decline), but the
        // fenced block itself is still a well-formed open-fence/standalone-close-line pair.
        string malformedMarker =
            "```hoardpad-settings\n{\"field\": \"AnthropicApiKey\", \"value\": \"sk-\"broken\"-secret\"}\n```";
        string text = "Sure!\n" + malformedMarker + "\nDone.";

        // Confirm the malformed payload really is parse-independent-relevant: TryParse must decline it.
        Assert.False(SettingsChangeProposalParser.TryParse(text, out _, out _));

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.DoesNotContain("broken", stripped);
        Assert.DoesNotContain("sk-", stripped);
        Assert.Contains("Sure!", stripped);
        Assert.Contains("Done.", stripped);
    }

    // -----------------------------------------------------------------------------------------------
    // Catalog entry 5: stripping is content-independent at the unit level — StripMarker's behavior does
    // not depend on which terminal path (ReadOnly / cancel / fault) produced the text shape it receives,
    // which is exactly the property that makes calling it unconditionally on every path safe. Each test
    // below feeds StripMarker the TEXT SHAPE that terminal path would hand it — the function itself is
    // never told which path it came from, and needs no such awareness to strip correctly.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void ContentIndependenceReadOnlyShapedCompleteMarkerTextIsStripped()
    {
        // ReadOnly never reaches the apply seam, but the assistant text it streams looks identical to
        // any other completed turn's text — a complete, well-formed marker.
        string marker = "```hoardpad-settings\n{\"field\": \"AnthropicApiKey\", \"value\": \"sk-readonly-leak-attempt\"}\n```";
        string text = "Sure!\n" + marker + "\nDone.";

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.DoesNotContain("sk-readonly-leak-attempt", stripped);
        Assert.Contains("Sure!", stripped);
        Assert.Contains("Done.", stripped);
    }

    [Fact]
    public void ContentIndependenceCancelledShapedUnclosedFenceTextIsDroppedToEndOfText()
    {
        // A Stop mid-stream leaves only an UNCLOSED opening fence in the transcript — no closing ```
        // ever arrived because the chunk that would carry it was never emitted.
        string partialChunk = "Sure!\n```hoardpad-settings\n{\"field\": \"AnthropicApiKey\", \"value\": \"sk-partial-leak";

        string stripped = SettingsChangeProposalParser.StripMarker(partialChunk);

        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.DoesNotContain("sk-partial-leak", stripped);
        Assert.Equal("Sure!", stripped.TrimEnd('\n')); // prose BEFORE the dangling fence survives; nothing after it exists.
    }

    [Fact]
    public void ContentIndependenceFaultedShapedPartialStreamTextIsStripped()
    {
        // A crash mid-stream still leaves whatever text DID stream before the fault — here a complete
        // marker that streamed in full before the backend threw.
        string marker = "```hoardpad-settings\n{\"field\": \"AnthropicApiKey\", \"value\": \"sk-crash-leak-attempt\"}\n```";
        string text = "Sure!\n" + marker;

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.DoesNotContain("sk-crash-leak-attempt", stripped);
        Assert.Contains("Sure!", stripped);
    }

    // -----------------------------------------------------------------------------------------------
    // Catalog entry 6: unclosed-fence handling PRESERVES legitimate trailing prose after a REAL close —
    // a closed-but-braceless fence must not be confused with a genuinely unclosed one, which would
    // over-truncate everything after it.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void ClosedBracelessFenceIsStrippedButTrailingProseSurvives()
    {
        string braceless = "```hoardpad-settings\nnot json at all\n```";
        string trailingProse = "This is legitimate follow-up prose that must survive.";
        string text = "Sure!\n" + braceless + "\n" + trailingProse;

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.DoesNotContain("not json at all", stripped);
        Assert.Contains(trailingProse, stripped); // must NOT be over-truncated away.
        Assert.Contains("Sure!", stripped);
    }

    // -----------------------------------------------------------------------------------------------
    // F8 encoding evasions (3) — proven at the raw DragonScrub.BlockScanner level by
    // DragonScrubCapabilityProbeTests (DS-2); reproduced here through the PRODUCTION StripMarker seam
    // to confirm the end-to-end call path (not just the library primitive) closes them. All green here
    // is the evidence pointer for docs/tasklist.md F8's close.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void F8aLeadingCharGluedToTheOpenFenceIsStillStripped()
    {
        string text =
            "Before.\n"
            + "X```hoardpad-settings\n"
            + "{\"field\": \"AnthropicApiKey\", \"value\": \"sk-ant-LEADCHAR456\"}\n"
            + "```\n"
            + "After.";

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("sk-ant-LEADCHAR456", stripped);
        Assert.DoesNotContain("hoardpad-settings", stripped);
        Assert.Contains("Before.", stripped);
        Assert.Contains("After.", stripped);
    }

    [Fact]
    public void F8bOpenTokenSplitAcrossTwoLinesIsStillStripped()
    {
        string text =
            "Before.\n"
            + "```hoardpad-\n"
            + "settings\n"
            + "{\"field\": \"AnthropicApiKey\", \"value\": \"sk-ant-SPLIT789\"}\n"
            + "```\n"
            + "After.";

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("sk-ant-SPLIT789", stripped);
        Assert.DoesNotContain("hoardpad-", stripped);
        Assert.DoesNotContain("settings", stripped);
        Assert.Contains("Before.", stripped);
        Assert.Contains("After.", stripped);
    }

    [Fact]
    public void F8cZeroWidthCodepointInsideTheTokenIsStillStripped()
    {
        // U+200B (ZERO WIDTH SPACE) spliced inside the literal open token.
        string text =
            "Before.\n"
            + "```hoardpad​-settings\n"
            + "{\"field\": \"AnthropicApiKey\", \"value\": \"sk-ant-ZEROWIDTH012\"}\n"
            + "```\n"
            + "After.";

        string stripped = SettingsChangeProposalParser.StripMarker(text);

        Assert.DoesNotContain("sk-ant-ZEROWIDTH012", stripped);
        Assert.DoesNotContain('​', stripped);
        Assert.Equal("Before.\nAfter.", stripped);
    }
}
