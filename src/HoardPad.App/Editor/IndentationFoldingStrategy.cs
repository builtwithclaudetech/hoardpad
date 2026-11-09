// IndentationFoldingStrategy.cs — the FR-9 indentation folding strategy (architecture §3.3, §6 FR-9 row;
// tech-design §5.3 SPIKE-FR9). The indentation half of the FoldingStrategySelector pair (the other half is
// the existing BraceFoldingStrategy).
//
// SCOPE: indentation-structured languages (Python / YAML / TOML / Makefile / Dockerfile / plain text), and
// the lazy-grammar scope-null FALLBACK (FoldingStrategySelector picks this when the TextMate scope has not
// resolved yet — architecture §3.3). Pure over an immutable FoldSnapshot (captured on the UI thread; this
// strategy touches no UI-thread Avalonia primitive — SPIKE-FR9), display-only (NEVER mutates the document —
// EC-10), and returns an offset-sorted List<NewFolding> ready for FoldingManager.UpdateFoldings(result, -1).
// O(n) in lines.
//
// A region opens on a line whose first following non-blank line is MORE indented; it spans to the last line
// belonging to that deeper block (the run of lines at-or-deeper than the child indent, blanks absorbed).
// Tabs expand to TabWidth columns before depth comparison so a tab-indented Makefile folds like a space file.

using System;
using System.Collections.Generic;
using AvaloniaEdit.Folding;

namespace HoardPad.App.Editor;

/// <summary>The FR-9 indentation folding strategy — pure over an immutable <see cref="FoldSnapshot"/>.</summary>
internal static class IndentationFoldingStrategy
{
    /// <summary>Columns a leading TAB advances when measuring indentation depth (no per-language setting in v1).</summary>
    private const int TabWidth = 4;

    /// <summary>
    /// Compute indentation fold regions: a line folds when the next non-blank line is more indented; the
    /// fold spans from that header line to the last line of the deeper block (trailing blank lines are not
    /// included). Single-line "blocks" and blank/whitespace-only lines never open a fold. Returns regions
    /// sorted by start offset, ready for <see cref="FoldingManager.UpdateFoldings"/>.
    /// </summary>
    public static List<NewFolding> ComputeFoldings(FoldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var foldings = new List<NewFolding>();
        int lineCount = snapshot.LineCount;

        // Pre-measure each 1-based line: its indent depth (in expanded columns) and whether it is blank.
        // Blank lines carry depth -1 so they never act as a block boundary by indent alone.
        var depth = new int[lineCount + 1];
        for (int n = 1; n <= lineCount; n++)
            depth[n] = IndentDepth(snapshot, n);

        for (int n = 1; n <= lineCount; n++)
        {
            if (depth[n] < 0)
                continue; // a blank header opens nothing.

            int childLine = NextNonBlankLine(depth, n + 1, lineCount);
            if (childLine < 0 || depth[childLine] <= depth[n])
                continue; // no deeper block follows — nothing to fold under this line.

            int childDepth = depth[childLine];
            int endLine = LastLineOfBlock(depth, childLine, childDepth, lineCount);
            if (endLine <= n)
                continue;

            // Fold from the END of the header line (so the header text stays visible when collapsed) to the
            // end of the block's last line.
            foldings.Add(new NewFolding(snapshot.LineEndOffset(n), snapshot.LineEndOffset(endLine))
            {
                Name = "…",
                DefaultClosed = false,
            });
        }

        foldings.Sort(static (a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    /// <summary>The expanded-column indent depth of the 1-based line, or -1 for a blank/whitespace-only line.</summary>
    private static int IndentDepth(FoldSnapshot snapshot, int lineNumber)
    {
        int columns = 0;
        int endOffset = snapshot.LineEndOffset(lineNumber);
        for (int offset = snapshot.LineOffset(lineNumber); offset < endOffset; offset++)
        {
            char c = snapshot.CharAt(offset);
            if (c == ' ')
                columns++;
            else if (c == '\t')
                columns += TabWidth - (columns % TabWidth);
            else
                return columns; // first non-whitespace char — this is the indent.
        }

        return -1; // no non-whitespace char on the line.
    }

    /// <summary>The first non-blank line at or after <paramref name="from"/> (1-based), or -1 if none.</summary>
    private static int NextNonBlankLine(int[] depth, int from, int lineCount)
    {
        for (int n = from; n <= lineCount; n++)
        {
            if (depth[n] >= 0)
                return n;
        }

        return -1;
    }

    /// <summary>
    /// The last line (1-based) belonging to the block that starts at <paramref name="childLine"/> with
    /// indent <paramref name="childDepth"/>: the run of non-blank lines at depth ≥ childDepth, with any
    /// trailing blank lines excluded (a blank after the block belongs to neither the block nor the header).
    /// </summary>
    private static int LastLineOfBlock(int[] depth, int childLine, int childDepth, int lineCount)
    {
        int last = childLine;
        for (int n = childLine + 1; n <= lineCount; n++)
        {
            if (depth[n] < 0)
                continue; // a blank line inside the block does not end it (its membership is decided by the
                          // next non-blank line).
            if (depth[n] < childDepth)
                break; // dedented out of the block.
            last = n;
        }

        return last;
    }
}
