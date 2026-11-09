// DragonScrubCapabilityProbeTests.cs — DS-2 (docs/decisions.md DR-DS-1, docs/nightly-schedule.md).
//
// Pre-rewire capability-confirmation probes: DragonScrub 0.2.0's `BlockScanner.RedactBlock` — NOT
// `TokenScanner.Redact` and NOT `Engine.ScrubOutput` — is the primitive DS-3 will wire in to replace
// `SettingsChangeProposalParser.StripMarker`. Neither of the other two candidates is exercised here
// because `docs/dragonscrub-capability-gap.md` already proved both insufficient for this job:
//   - `TokenScanner.Redact` is token-granular — it removes only the matched `` ```hoardpad-settings ``
//     open-fence line and leaves the JSON payload (and the closing fence) sitting in the transcript.
//   - `Engine.ScrubOutput` only FLAGS a caller marker (`Finding.Action == "flag"`); `ApplyActions` never
//     rewrites text for a flag, so the payload survives untouched.
// `BlockScanner.RedactBlock` is new in 0.2.0 specifically to close that gap (gap-report verdict
// MEETS-OR-EXCEEDS, addendum 2026-07-17) by composing the same obfuscation-tolerant open-token match
// with a content-independent close-delimiter line walk. These probes are a CONFIRMATION against the
// published package — they are expected to pass. Per DS-2's contract, an unexpected failure here is
// NOT patched around in HoardPad: it goes back to the DragonScrub repo (rule/catalog + vectors + both
// engines green, republish, re-pin CPM) before this file may be marked done.
//
// Every probe calls the exact production shape DS-3 will use:
//   BlockScanner.RedactBlock(text, "```hoardpad-settings", "```")
// (SettingsChangeProposal.cs's private MarkerOpenToken/FenceCloseLine constants, reproduced literally
// here since DS-2 is pre-rewire and does not yet touch that file.)

using DragonScrub;
using Xunit;

namespace HoardPad.App.Tests.Ai;

public sealed class DragonScrubCapabilityProbeTests
{
    private const string OpenToken = "```hoardpad-settings";
    private const string CloseDelimiter = "```";

    [Fact]
    public void RedactBlockRemovesACanonicalWellFormedMarkerBlockEntirely()
    {
        string text =
            "Sure, I'll make the font bigger.\n"
            + "```hoardpad-settings\n"
            + "{\"field\": \"EditorFontSize\", \"value\": \"16\"}\n"
            + "```\n"
            + "Let me know if that looks right.";

        BlockScrubResult result = BlockScanner.RedactBlock(text, OpenToken, CloseDelimiter);

        Assert.True(result.BlocksRemoved >= 1);
        Assert.DoesNotContain("EditorFontSize", result.RedactedText);
        Assert.DoesNotContain("hoardpad-settings", result.RedactedText);
        Assert.Contains("Sure, I'll make the font bigger.", result.RedactedText);
        Assert.Contains("Let me know if that looks right.", result.RedactedText);
    }

    [Fact]
    public void RedactBlockLeavesTextWithoutAnyMarkerByteIdentical()
    {
        string text =
            "No settings change here — just a normal reply about your document,\n"
            + "with a stray code fence that has nothing to do with settings:\n"
            + "```csharp\n"
            + "Console.WriteLine(\"hi\");\n"
            + "```\n"
            + "Hope that helps!";

        BlockScrubResult result = BlockScanner.RedactBlock(text, OpenToken, CloseDelimiter);

        Assert.Equal(0, result.BlocksRemoved);
        Assert.Equal(text, result.RedactedText);
    }

    [Fact]
    public void RedactBlockRemovesTheWholeBlockWhenALeadingCharPrecedesTheFenceOnTheSameLine()
    {
        // F8-a: a leading non-whitespace char glued directly to the opening fence — the exact
        // encoding evasion that defeated StripMarker's line-trim-then-StartsWith check.
        string text =
            "Before.\n"
            + "X```hoardpad-settings\n"
            + "{\"field\": \"AnthropicApiKey\", \"value\": \"sk-ant-LEADCHAR456\"}\n"
            + "```\n"
            + "After.";

        BlockScrubResult result = BlockScanner.RedactBlock(text, OpenToken, CloseDelimiter);

        Assert.True(result.BlocksRemoved >= 1);
        Assert.DoesNotContain("sk-ant-LEADCHAR456", result.RedactedText);
        Assert.DoesNotContain("hoardpad-settings", result.RedactedText);
        Assert.Contains("Before.", result.RedactedText);
        Assert.Contains("After.", result.RedactedText);
    }

    [Fact]
    public void RedactBlockRemovesTheWholeBlockWhenTheOpenTokenIsSplitAcrossTwoLines()
    {
        // F8-b: the opening token's own characters interrupted by a newline — StripMarker's
        // single-line StartsWith check never sees the full token on either line.
        string text =
            "Before.\n"
            + "```hoardpad-\n"
            + "settings\n"
            + "{\"field\": \"AnthropicApiKey\", \"value\": \"sk-ant-SPLIT789\"}\n"
            + "```\n"
            + "After.";

        BlockScrubResult result = BlockScanner.RedactBlock(text, OpenToken, CloseDelimiter);

        Assert.True(result.BlocksRemoved >= 1);
        Assert.DoesNotContain("sk-ant-SPLIT789", result.RedactedText);
        Assert.DoesNotContain("hoardpad-", result.RedactedText);
        Assert.DoesNotContain("settings", result.RedactedText);
        Assert.Contains("Before.", result.RedactedText);
        Assert.Contains("After.", result.RedactedText);
    }

    [Fact]
    public void RedactBlockRemovesTheWholeBlockWhenAZeroWidthCodepointSitsInsideTheToken()
    {
        // F8-c: U+200B (ZERO WIDTH SPACE) spliced inside the literal open token — invisible to a
        // human reading the transcript, but enough to defeat a literal StartsWith comparison.
        string text =
            "Before.\n"
            + "```hoardpad​-settings\n"
            + "{\"field\": \"AnthropicApiKey\", \"value\": \"sk-ant-ZEROWIDTH012\"}\n"
            + "```\n"
            + "After.";

        BlockScrubResult result = BlockScanner.RedactBlock(text, OpenToken, CloseDelimiter);

        Assert.True(result.BlocksRemoved >= 1);
        Assert.DoesNotContain("sk-ant-ZEROWIDTH012", result.RedactedText);
        // The char-collection overload compares each System.Char via EqualityComparer<char>.Default
        // (ordinal), unlike the string-substring overload above, which defaults to a culture-aware
        // comparison that treats a lone Format-category codepoint like U+200B as collation-ignorable
        // and reports a false "match" at position 0 even when the character is not actually present.
        Assert.DoesNotContain('​', result.RedactedText);
        Assert.Equal("Before.\nAfter.", result.RedactedText);
        Assert.Contains("Before.", result.RedactedText);
        Assert.Contains("After.", result.RedactedText);
    }
}
