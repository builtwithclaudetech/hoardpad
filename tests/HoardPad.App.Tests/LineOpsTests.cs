// LineOpsTests.cs — the FR-6 move/duplicate-line commands (design §3.3). These are pure TextDocument
// mutations (no AvaloniaEdit UI control), so they run as ordinary [Fact]s against a bare TextDocument —
// no Skia-headless UI thread needed. The verify filter matches the class name "LineOps".

using AvaloniaEdit.Document;
using HoardPad.App.Editor;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class LineOpsTests
{
    private static LineSpan One(int line) => new(line, line);

    [Fact]
    public void MoveLinesDownSwapsTheLineWithTheOneBelow()
    {
        var doc = new TextDocument("alpha\nbeta\ngamma\n");

        LineOperations.MoveLinesDown(doc, One(1));

        Assert.Equal("beta\nalpha\ngamma\n", doc.Text);
    }

    [Fact]
    public void MoveLinesUpSwapsTheLineWithTheOneAbove()
    {
        var doc = new TextDocument("alpha\nbeta\ngamma\n");

        LineOperations.MoveLinesUp(doc, One(3));

        Assert.Equal("alpha\ngamma\nbeta\n", doc.Text);
    }

    [Fact]
    public void MoveLinesUpAtTopIsANoOp()
    {
        var doc = new TextDocument("alpha\nbeta\n");

        LineSpan result = LineOperations.MoveLinesUp(doc, One(1));

        Assert.Equal("alpha\nbeta\n", doc.Text);
        Assert.Equal(One(1), result);
    }

    [Fact]
    public void MoveLinesDownAtBottomIsANoOp()
    {
        var doc = new TextDocument("alpha\nbeta");

        LineSpan result = LineOperations.MoveLinesDown(doc, One(2));

        Assert.Equal("alpha\nbeta", doc.Text);
        Assert.Equal(One(2), result);
    }

    [Fact]
    public void MoveLinesPreservesCrlfEol()
    {
        var doc = new TextDocument("alpha\r\nbeta\r\ngamma\r\n");

        LineOperations.MoveLinesDown(doc, One(1));

        // The CRLF style is preserved (no \n-only delimiter introduced by the swap).
        Assert.Equal("beta\r\nalpha\r\ngamma\r\n", doc.Text);
    }

    [Fact]
    public void MoveMultiLineSelectionDownMovesTheWholeBlock()
    {
        var doc = new TextDocument("one\ntwo\nthree\nfour\n");

        LineSpan result = LineOperations.MoveLinesDown(doc, new LineSpan(1, 2));

        Assert.Equal("three\none\ntwo\nfour\n", doc.Text);
        Assert.Equal(new LineSpan(2, 3), result);
    }

    [Fact]
    public void DuplicateLinesInsertsACopyBelow()
    {
        var doc = new TextDocument("alpha\nbeta\n");

        LineSpan result = LineOperations.DuplicateLines(doc, One(1));

        Assert.Equal("alpha\nalpha\nbeta\n", doc.Text);
        Assert.Equal(One(2), result);
    }

    [Fact]
    public void DuplicateLastLineWithoutTrailingNewlineAppendsACopy()
    {
        var doc = new TextDocument("alpha\nbeta");

        LineSpan result = LineOperations.DuplicateLines(doc, One(2));

        Assert.Equal("alpha\nbeta\nbeta", doc.Text);
        Assert.Equal(One(3), result);
    }

    [Fact]
    public void DuplicateMultiLineSelectionDuplicatesTheWholeBlock()
    {
        var doc = new TextDocument("one\ntwo\nthree\n");

        LineSpan result = LineOperations.DuplicateLines(doc, new LineSpan(1, 2));

        Assert.Equal("one\ntwo\none\ntwo\nthree\n", doc.Text);
        Assert.Equal(new LineSpan(3, 4), result);
    }

    // ---- FR-6 delete-line (Ctrl+Shift+K) — carry-forward B (the FR-6 op without a home until M6.3). ----

    [Fact]
    public void DeleteLinesRemovesAMiddleLineAndClosesUp()
    {
        var doc = new TextDocument("alpha\nbeta\ngamma\n");

        LineSpan result = LineOperations.DeleteLines(doc, One(2));

        Assert.Equal("alpha\ngamma\n", doc.Text);
        Assert.Equal(One(2), result); // the caret stays on the line the deletion left behind.
    }

    [Fact]
    public void DeleteLastLineWithoutTrailingNewlineDoesNotLeaveADanglingEmptyLine()
    {
        var doc = new TextDocument("alpha\nbeta");

        LineOperations.DeleteLines(doc, One(2));

        Assert.Equal("alpha", doc.Text); // the preceding delimiter is pulled in — no trailing empty line.
    }

    [Fact]
    public void DeleteTheOnlyLineLeavesAnEmptyDocument()
    {
        var doc = new TextDocument("only line");

        LineSpan result = LineOperations.DeleteLines(doc, One(1));

        Assert.Equal(string.Empty, doc.Text);
        Assert.Equal(One(1), result);
    }

    [Fact]
    public void DeleteMultiLineSelectionRemovesTheWholeBlock()
    {
        var doc = new TextDocument("one\ntwo\nthree\nfour\n");

        LineOperations.DeleteLines(doc, new LineSpan(2, 3));

        Assert.Equal("one\nfour\n", doc.Text);
    }
}
