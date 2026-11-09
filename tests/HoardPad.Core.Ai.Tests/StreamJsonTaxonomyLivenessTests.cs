using HoardPad.Core.Ai.Provider;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.2 — the §4.2 stream-json event taxonomy that feeds the MA14 Liveness match: parse WRITE tool-use events
/// (with <c>tool_use_id</c>) out of a stream-json line (TD §4.7, SPIKE-3 I). Only Write/Edit are surfaced — a
/// Read tool-use is not gated for liveness. Pure — JSON over a string, no I/O. (FQN carries "Liveness" so it runs
/// under the MA14 verify filter alongside the correlation tests.)
/// </summary>
public sealed class StreamJsonTaxonomyLivenessTests
{
    private const string AssistantWriteLine =
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01ABC","name":"Write","input":{"file_path":"x.txt","content":"hi"}}]}}""";

    [Fact]
    public void ParseWriteToolUsesExtractsTheWriteEventAndItsToolUseId()
    {
        var events = StreamJsonTaxonomy.ParseWriteToolUses(AssistantWriteLine);

        var ev = Assert.Single(events);
        Assert.Equal("toolu_01ABC", ev.ToolUseId);
        Assert.Equal("Write", ev.ToolName);
    }

    [Fact]
    public void ParseWriteToolUsesSurfacesEditButNotRead()
    {
        const string line =
            """{"type":"assistant","message":{"content":[{"type":"text","text":"ok"},{"type":"tool_use","id":"t_read","name":"Read","input":{}},{"type":"tool_use","id":"t_edit","name":"Edit","input":{}}]}}""";

        var events = StreamJsonTaxonomy.ParseWriteToolUses(line);

        var ev = Assert.Single(events); // Read is excluded; only the Edit is a gated write.
        Assert.Equal("t_edit", ev.ToolUseId);
        Assert.Equal("Edit", ev.ToolName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"type":"system","subtype":"init"}""")]                              // non-assistant event
    [InlineData("""{"type":"assistant","message":{"content":[]}}""")]                   // no tool_use blocks
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""")] // text only
    public void ParseWriteToolUsesBenignLinesYieldNoEvents(string line)
    {
        // Malformed / non-write lines contribute no events — MA14 degrades on an UNMATCHED write, never on a
        // parse miss (the parser must not invent or suppress a liveness signal).
        Assert.Empty(StreamJsonTaxonomy.ParseWriteToolUses(line));
    }

    [Fact]
    public void ParseWriteToolUsesFeedsTheLivenessMatchEndToEnd()
    {
        // The two M4.2 pieces compose: parse a write event, then liveness-match it against a hook callback.
        var writes = StreamJsonTaxonomy.ParseWriteToolUses(AssistantWriteLine);

        var matchedVerdict = LivenessMatch.Evaluate(
            writes, new[] { new HookCallbackObservation("toolu_01ABC") });
        Assert.False(matchedVerdict.ShouldDegrade);

        var unmatchedVerdict = LivenessMatch.Evaluate(
            writes, Array.Empty<HookCallbackObservation>());
        Assert.True(unmatchedVerdict.ShouldDegrade);
    }
}
