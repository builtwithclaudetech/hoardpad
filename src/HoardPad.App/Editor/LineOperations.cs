// LineOperations.cs — the FR-6 custom move/duplicate-line TextDocument commands (architecture §6 FR-6,
// design §3.3, TD §5.5). AvaloniaEdit has no built-in move-line or duplicate-line; these are the
// straightforward document-mutation commands TD §5.5 calls for, with multi-line-selection handling.
//
// Pure over a TextDocument + a caret/selection span: every method takes the current line range and
// returns the new caret/selection span, mutating the document in place. No AvaloniaEdit UI control is
// touched, so these are directly unit-testable against a bare TextDocument (no headless UI required).
// All editing is on the UI thread (architecture §3.1) — these are synchronous in-memory document edits.

using AvaloniaEdit.Document;

namespace HoardPad.App.Editor;

/// <summary>
/// The span of lines a line-op acts on, in 1-based line numbers (inclusive). For a caret with no
/// selection, <see cref="FirstLine"/> == <see cref="LastLine"/>. After an op, the new span is returned
/// so the caller can restore the selection/caret to follow the moved/duplicated text.
/// </summary>
internal readonly record struct LineSpan(int FirstLine, int LastLine);

/// <summary>The FR-6 move/duplicate-line commands as pure <see cref="TextDocument"/> mutations.</summary>
internal static class LineOperations
{
    /// <summary>
    /// Move the line span up by one line (swap it with the preceding line). No-op when the span is
    /// already at the top of the document. Returns the span's new position (shifted up by one).
    /// </summary>
    public static LineSpan MoveLinesUp(TextDocument document, LineSpan span)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (span.FirstLine <= 1)
            return span; // already at the top — nothing above to swap with.

        DocumentLine above = document.GetLineByNumber(span.FirstLine - 1);
        DocumentLine first = document.GetLineByNumber(span.FirstLine);
        DocumentLine last = document.GetLineByNumber(span.LastLine);

        // The text of the line above (without its delimiter) and the block being moved (with its
        // internal structure preserved). Swapping = remove the above-line + the block's preceding
        // delimiter, then re-insert the above-line after the block.
        string aboveText = document.GetText(above.Offset, above.Length);
        string blockText = document.GetText(first.Offset, last.EndOffset - first.Offset);

        // Region spans from the start of the above line to the end (incl. delimiter) of the last line,
        // so the rewrite keeps the document's trailing-newline shape intact.
        int regionStart = above.Offset;
        int regionEnd = last.EndOffset;
        string movedDelimiter = NewLineFor(document);

        document.Replace(regionStart, regionEnd - regionStart, blockText + movedDelimiter + aboveText);

        return new LineSpan(span.FirstLine - 1, span.LastLine - 1);
    }

    /// <summary>
    /// Move the line span down by one line (swap it with the following line). No-op when the span is
    /// already at the bottom of the document. Returns the span's new position (shifted down by one).
    /// </summary>
    public static LineSpan MoveLinesDown(TextDocument document, LineSpan span)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (span.LastLine >= document.LineCount)
            return span; // already at the bottom — nothing below to swap with.

        // Moving [first..last] down past the next line == moving the next line up past the block.
        DocumentLine first = document.GetLineByNumber(span.FirstLine);
        DocumentLine last = document.GetLineByNumber(span.LastLine);
        DocumentLine below = document.GetLineByNumber(span.LastLine + 1);

        string blockText = document.GetText(first.Offset, last.EndOffset - first.Offset);
        string belowText = document.GetText(below.Offset, below.Length);

        int regionStart = first.Offset;
        int regionEnd = below.EndOffset;
        string movedDelimiter = NewLineFor(document);

        document.Replace(regionStart, regionEnd - regionStart, belowText + movedDelimiter + blockText);

        return new LineSpan(span.FirstLine + 1, span.LastLine + 1);
    }

    /// <summary>
    /// Duplicate the line span, inserting the copy immediately below the original. The caret/selection
    /// follows to the duplicated copy. Returns the span covering the inserted duplicate.
    /// </summary>
    public static LineSpan DuplicateLines(TextDocument document, LineSpan span)
    {
        ArgumentNullException.ThrowIfNull(document);

        DocumentLine first = document.GetLineByNumber(span.FirstLine);
        DocumentLine last = document.GetLineByNumber(span.LastLine);
        string blockText = document.GetText(first.Offset, last.EndOffset - first.Offset);

        string newLine = NewLineFor(document);
        bool lastHasDelimiter = last.DelimiterLength > 0;

        if (lastHasDelimiter)
        {
            // Insert the copy + a delimiter immediately AFTER the last line's delimiter (TotalLength
            // includes the delimiter), so the duplicate becomes whole new line(s) below the block and
            // the originals keep their delimiters intact.
            document.Insert(last.Offset + last.TotalLength, blockText + newLine);
        }
        else
        {
            // The final line has no trailing delimiter: append a delimiter + the copy at document end.
            document.Insert(last.EndOffset, newLine + blockText);
        }

        int duplicateFirst = span.LastLine + 1;
        int duplicateLast = duplicateFirst + (span.LastLine - span.FirstLine);
        return new LineSpan(duplicateFirst, duplicateLast);
    }

    /// <summary>
    /// Delete the line span entirely (FR-6 delete-line, <c>Ctrl+Shift+K</c>) — the lines and one
    /// delimiter are removed, so the surrounding lines close up. Returns the caret line where the deletion
    /// resolved (clamped into the now-shorter document), as a zero-width span. Deleting the only line leaves
    /// an empty document with the caret on line 1.
    /// </summary>
    public static LineSpan DeleteLines(TextDocument document, LineSpan span)
    {
        ArgumentNullException.ThrowIfNull(document);

        DocumentLine first = document.GetLineByNumber(span.FirstLine);
        DocumentLine last = document.GetLineByNumber(span.LastLine);

        // Remove the block PLUS its trailing delimiter (TotalLength includes the delimiter), so the next
        // line moves up. When the block is the last line(s) with no trailing delimiter, also pull in the
        // PRECEDING delimiter so we do not leave a dangling empty final line.
        int removeStart = first.Offset;
        int removeEnd = last.Offset + last.TotalLength;
        if (last.DelimiterLength == 0 && span.FirstLine > 1)
        {
            DocumentLine above = document.GetLineByNumber(span.FirstLine - 1);
            removeStart = above.Offset + above.Length; // start at the preceding delimiter.
        }

        document.Remove(removeStart, removeEnd - removeStart);

        int caretLine = Math.Clamp(span.FirstLine, 1, Math.Max(1, document.LineCount));
        return new LineSpan(caretLine, caretLine);
    }

    /// <summary>The document's prevailing newline (CRLF/LF/CR), so line ops preserve the buffer's EOL style.</summary>
    private static string NewLineFor(TextDocument document)
    {
        // AvaloniaEdit tracks the delimiter per line; use the first line's delimiter when present,
        // else fall back to LF (HoardPad's new-buffer default, NFR-9).
        for (int n = 1; n <= document.LineCount; n++)
        {
            DocumentLine line = document.GetLineByNumber(n);
            if (line.DelimiterLength > 0)
                return document.GetText(line.Offset + line.Length, line.DelimiterLength);
        }
        return "\n";
    }
}
