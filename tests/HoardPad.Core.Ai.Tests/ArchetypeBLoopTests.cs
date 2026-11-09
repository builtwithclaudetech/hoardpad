using HoardPad.Core.Ai.Provider;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.2 — the Archetype-B (driven-completion) loop where HoardPad OWNS the loop, defines the tools, and parses
/// tool-calls (architecture §4.1, PRD FR-23). The full op vocabulary is reachable here (R3-BL1), unlike
/// Archetype A. Pure — tool-name mapping + JSON-shape parse, no I/O, no clock.
/// </summary>
public sealed class ArchetypeBLoopTests
{
    [Theory]
    [InlineData("read", BackendTool.Read)]
    [InlineData("create", BackendTool.Create)]
    [InlineData("copy", BackendTool.Copy)]
    [InlineData("move", BackendTool.Move)]
    [InlineData("rename", BackendTool.Move)] // rename canonicalizes to move (MA11)
    [InlineData("edit", BackendTool.Edit)]
    [InlineData("delete", BackendTool.Delete)]
    public void MapToolMapsTheOfferedRoster(string name, BackendTool expected)
    {
        Assert.Equal(expected, ArchetypeBLoop.MapTool(name));
    }

    [Fact]
    public void MapToolUnknownToolIsNotOffered()
    {
        Assert.Null(ArchetypeBLoop.MapTool("Bash"));
        Assert.Null(ArchetypeBLoop.MapTool("mcp__github__create_issue"));
    }

    [Fact]
    public void ParseStepNoToolCallsIsComplete()
    {
        var step = ArchetypeBLoop.ParseStep(Array.Empty<(string, string)>());

        Assert.True(step.IsComplete);
        Assert.Empty(step.ToolCalls);
    }

    [Fact]
    public void ParseStepRecognizedToolCallsAreParsedAndNotComplete()
    {
        var step = ArchetypeBLoop.ParseStep(new[]
        {
            ("create", """{"path":"a.txt","content":"hi"}"""),
            ("delete", """{"path":"b.txt"}"""),
        });

        Assert.False(step.IsComplete);
        Assert.Equal(2, step.ToolCalls.Length);
        Assert.Equal(BackendTool.Create, step.ToolCalls[0].Tool);
        Assert.Equal(BackendTool.Delete, step.ToolCalls[1].Tool);
    }

    [Fact]
    public void ParseStepDropsUnrecognizedToolsAndMalformedArgs()
    {
        var step = ArchetypeBLoop.ParseStep(new[]
        {
            ("Bash", """{"cmd":"rm -rf /"}"""),   // not offered → dropped
            ("edit", "not-json"),                  // malformed args → dropped
            ("move", """{"source":"a","dest":"b"}"""),
        });

        var call = Assert.Single(step.ToolCalls);
        Assert.Equal(BackendTool.Move, call.Tool);
        Assert.False(step.IsComplete);
    }

    [Fact]
    public void ParseStepAllCallsDroppedIsComplete()
    {
        // If every requested call is unrecognized/malformed, the step has nothing to execute → complete.
        var step = ArchetypeBLoop.ParseStep(new[]
        {
            ("Bash", "{}"),
            ("edit", "{"),
        });

        Assert.True(step.IsComplete);
        Assert.Empty(step.ToolCalls);
    }
}
