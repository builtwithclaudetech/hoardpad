// BraceFoldingStrategy.cs — the non-trimmable FR-9 brace folding (architecture §6 FR-9, design §3.1).
//
// SCOPE: brace folding ONLY ({ } pairs). Indentation folding, the FoldingStrategySelector by TextMate
// scope, the debounced background FoldingService, and string/comment fold-suppression are the cut-tail
// M8.2 (OUT OF SCOPE here) — see architecture §3.3. This is the simple, pure { }-pair pass AvaloniaEdit
// does not ship built-in (it ships only XmlFoldingStrategy). It is pure over an immutable FoldSnapshot
// (captured on the UI thread) and returns an offset-sorted List<NewFolding> ready for
// FoldingManager.UpdateFoldings(result, -1).
//
// Folding never mutates buffer content (FR-9). String/comment awareness is deferred (M8.2): a naive
// pass can over-fold braces inside strings, but it can never corrupt text, so it is safe to ship the
// simple version now and refine scope-suppression in M8.2.

using System;
using System.Collections.Generic;
using AvaloniaEdit.Folding;

namespace HoardPad.App.Editor;

/// <summary>The FR-9 brace ({ }) folding strategy — pure over an immutable <see cref="FoldSnapshot"/>.</summary>
internal static class BraceFoldingStrategy
{
    /// <summary>
    /// Compute fold regions for matched <c>{</c>…<c>}</c> pairs. A fold spans from just after the
    /// opening brace to the closing brace; single-line <c>{}</c> pairs produce no fold (nothing to
    /// hide). Returns regions sorted by start offset, ready for
    /// <see cref="FoldingManager.UpdateFoldings"/>.
    /// </summary>
    public static List<NewFolding> ComputeFoldings(FoldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var foldings = new List<NewFolding>();
        var openOffsets = new Stack<int>();
        string text = snapshot.Text;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                openOffsets.Push(i);
            }
            else if (c == '}' && openOffsets.Count > 0)
            {
                int start = openOffsets.Pop();
                // Fold from just after '{' to the '}' offset. Skip same-line pairs (start line ==
                // end line) — a one-line block has nothing foldable.
                int startLine = snapshot.LineNumberByOffset(start);
                int endLine = snapshot.LineNumberByOffset(i);
                if (endLine > startLine)
                {
                    foldings.Add(new NewFolding(start + 1, i)
                    {
                        Name = "{ … }",
                        DefaultClosed = false,
                    });
                }
            }
        }

        foldings.Sort(static (a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }
}
