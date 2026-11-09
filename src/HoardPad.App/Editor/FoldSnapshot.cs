// FoldSnapshot.cs — the immutable, UI-thread-captured input to the FR-9 fold compute (architecture §3.3).
//
// THREADING (load-bearing): no AvaloniaEdit TextDocument (or any mutable Avalonia primitive) may be read off
// the UI thread. FoldingService.BeginRecompute calls Capture() ON THE UI THREAD — reading the document's own
// authoritative line tree (DocumentLine.Offset/EndOffset) and full text once — into this readonly value. The
// background compute (the strategies, via FoldingStrategySelector) then reads ONLY this snapshot, never the
// live document, so a concurrent keystroke can neither tear the read nor throw. Line semantics are identical
// to the document because the line boundaries come from the line tree itself, not from re-derived scanning.

using System;
using AvaloniaEdit.Document;

namespace HoardPad.App.Editor;

/// <summary>
/// An immutable, thread-safe snapshot of a <see cref="TextDocument"/> for off-UI-thread fold computation: the
/// full text plus the document's authoritative per-line start/end offsets (1-based, mirroring AvaloniaEdit's
/// line API). The FR-9 strategies read from this snapshot only — never the live, mutable document.
/// </summary>
internal sealed class FoldSnapshot
{
    private readonly int[] _lineOffsets;    // 1-based: _lineOffsets[n] == DocumentLine n .Offset.
    private readonly int[] _lineEndOffsets; // 1-based: _lineEndOffsets[n] == DocumentLine n .EndOffset.

    private FoldSnapshot(string text, int[] lineOffsets, int[] lineEndOffsets)
    {
        Text = text;
        _lineOffsets = lineOffsets;
        _lineEndOffsets = lineEndOffsets;
    }

    /// <summary>The full document text at capture time.</summary>
    public string Text { get; }

    /// <summary>The number of lines (matches <see cref="TextDocument.LineCount"/> at capture time).</summary>
    public int LineCount => _lineOffsets.Length - 1; // index 0 is unused (lines are 1-based).

    /// <summary>
    /// Capture an immutable snapshot from <paramref name="document"/>. MUST be called on the UI thread — it
    /// reads the live document's text and line tree, which the background compute then never touches.
    /// </summary>
    public static FoldSnapshot Capture(TextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string text = document.Text;
        int lineCount = document.LineCount;
        var lineOffsets = new int[lineCount + 1];
        var lineEndOffsets = new int[lineCount + 1];
        for (int n = 1; n <= lineCount; n++)
        {
            DocumentLine line = document.GetLineByNumber(n);
            lineOffsets[n] = line.Offset;
            lineEndOffsets[n] = line.EndOffset;
        }

        return new FoldSnapshot(text, lineOffsets, lineEndOffsets);
    }

    /// <summary>The start offset of the 1-based line <paramref name="lineNumber"/> (its <c>Offset</c>).</summary>
    public int LineOffset(int lineNumber) => _lineOffsets[lineNumber];

    /// <summary>The end offset of the 1-based line <paramref name="lineNumber"/> (its <c>EndOffset</c>, before the delimiter).</summary>
    public int LineEndOffset(int lineNumber) => _lineEndOffsets[lineNumber];

    /// <summary>The character at <paramref name="offset"/> in the snapshot text.</summary>
    public char CharAt(int offset) => Text[offset];

    /// <summary>
    /// The 1-based line number containing <paramref name="offset"/> — the snapshot equivalent of
    /// <see cref="TextDocument.GetLineByOffset"/>'s <c>LineNumber</c>. Binary search over the line-start table.
    /// </summary>
    public int LineNumberByOffset(int offset)
    {
        // Find the greatest line n whose start offset is <= offset (lines are 1-based; index 0 is unused).
        int low = 1;
        int high = LineCount;
        int result = 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (_lineOffsets[mid] <= offset)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }
}
