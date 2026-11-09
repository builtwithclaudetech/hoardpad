// PerTabCaretTests.cs — QA5-1: caret position (and, per the hostile-review's test-first probe below,
// selection + scroll too) was reset to offset 0 on every tab switch away-and-back because one shared
// AvaloniaEdit TextEditor is reused across all tabs (see PerTabUndoTests.cs's doc comment for the same
// shared-control shape). See docs/qa5-1-diagnosis-review.md for the full hostile-review this fix answers.
//
// Root cause (isolated by the first probe test below, against the REAL AvaloniaEdit 11.4.1 framework
// classes, no HoardPad wiring involved): reassigning TextArea.Document to a DIFFERENT TextDocument instance
// unconditionally clobbers all three, regardless of the new document's content or whether it was previously
// visited:
//   - AvaloniaEdit.Editing.TextArea.OnDocumentChanged(TextDocument, TextDocument) executes
//     `Caret.Location = new TextLocation(1, 1); ClearSelection();` unconditionally.
//   - AvaloniaEdit.Rendering.TextView.OnDocumentChanged(TextDocument, TextDocument) executes
//     `ClearScrollData(); ClearVisualLines();` unconditionally.
// Both run synchronously inside the SAME call chain the ONE clobbering statement in production
// (EditorView.axaml.cs SwitchToTabDocument, `_editor.Document = document`) triggers: TextEditor.Document's
// property-changed callback sets TextArea.Document, whose property-changed callback (in turn) sets
// TextView.Document (-> the scroll clear) THEN sets Caret.Location + clears selection. The prior hostile-review
// (docs/qa5-1-diagnosis-review.md) decompiled TextArea.OnDocumentChanged and TextView.OnDocumentChanged
// looking for a Caret-touching statement matching "Position"/"Offset"/"_position" and found none — the actual
// statement uses the DIFFERENTLY-NAMED `Caret.Location` property, which is why it evaded that grep-shaped
// search. This file's first test proves the mechanism empirically (not just by re-reading the decompile) by
// driving the real AvaloniaEdit TextArea/TextDocument/Caret/Selection classes directly, with no view or VM
// involved at all.
//
// Because the SAME statement clears selection and scroll too, the hostile-review's objection 2 (whether
// selection/scroll are "in scope" or already survive) resolves in favor of ALL THREE being in scope — the
// round-trip test below (SwitchingAwayAndBackRestoresTheCaretAndSelectionOnThePreviouslyVisitedTab) confirms
// selection is restored through the full EditorView chain, and the scroll-preservation tests confirm scroll
// is too.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class PerTabCaretTests
{
    // ---- Probe (hard requirement, docs/qa5-1-diagnosis-review.md objection 1): isolate the TRUE clobbering
    // statement BEFORE writing any production fix. Drives bare AvaloniaEdit framework classes directly — no
    // EditorView, no EditorViewModel, no HoardPad wiring at all — so the result can only be explained by
    // AvaloniaEdit's own TextArea.Document setter, never by anything HoardPad-specific. ----

    [AvaloniaFact]
    public void ReassigningTextAreaDocumentToADifferentInstanceResetsCaretAndClearsSelectionRegardlessOfContent()
    {
        var docA = new TextDocument("hello world");
        // docB has the IDENTICAL text to docA — proves the reset is driven by DOCUMENT IDENTITY changing,
        // not by the new document's content differing from the old one.
        var docB = new TextDocument("hello world");
        var textArea = new TextArea { Document = docA };

        textArea.Caret.Offset = 5;
        textArea.Selection = Selection.Create(textArea, 2, 7);
        Assert.Equal(5, textArea.Caret.Offset); // precondition.
        Assert.False(textArea.Selection.IsEmpty); // precondition.

        // The isolated statement under test: TextArea.OnDocumentChanged(TextDocument, TextDocument) —
        // triggered purely by this property reassignment, with no HoardPad code anywhere in the call stack.
        textArea.Document = docB;

        Assert.Equal(0, textArea.Caret.Offset); // TRUE clobber: Caret.Location = new TextLocation(1, 1).
        Assert.True(textArea.Selection.IsEmpty); // TRUE clobber: ClearSelection().
    }

    // ---- The Done-when: caret + selection restored across a genuine A -> B -> A round trip. This is also
    // the end-to-end confirmation (through the REAL production chain: EditorView + a genuine DataContext
    // swap) that the isolated framework mechanism probed above is exactly what fires when a previously-
    // visited tab (isNewDocument == false) is revisited, and that RestoreViewState's synchronous caret/
    // selection restore (see its doc comment) undoes the clobber. ----

    [AvaloniaFact]
    public async Task SwitchingAwayAndBackRestoresTheCaretAndSelectionOnThePreviouslyVisitedTab()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        string pathA = await WriteTempFileAsync("tab A content for the round trip").ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("tab B content").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);

            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.Select(4, 3); // "A co..." selection.
            editor.CaretOffset = 6; // "tab A |content..." — decoupled from the selection end (7).

            view.DataContext = vmB;
            Assert.Equal("tab B content", editor.Text); // precondition: genuinely a different tab now.

            view.DataContext = vmA;

            Assert.Equal(6, editor.CaretOffset); // caret restored.
            Assert.Equal(4, editor.SelectionStart); // selection restored.
            Assert.Equal(3, editor.SelectionLength);

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
        }
    }

    // ---- Scroll restore, on a document tall enough to actually scroll. ----

    [AvaloniaFact]
    public async Task SwitchingAwayAndBackRestoresTheScrollOffsetOnThePreviouslyVisitedTab()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        string longText = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i}"));
        string pathA = await WriteTempFileAsync(longText).ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("tab B content").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);

            // Short viewport so 200 lines genuinely overflows it (mirrors ChecklistScrollPreserveTests.Attach).
            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 150 };
            window.Show();
            PumpUntilSettled(window);

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            // TextEditor.ScrollToVerticalOffset is a no-op in the pinned AvaloniaEdit 11.4.1 build (decompiled:
            // its body is just `ApplyTemplate();`, the offset parameter is unused) — set the internal
            // ScrollViewer's Offset directly via the visual tree, exactly as the production fix now does.
            ScrollViewer editorScroller = editor.GetVisualDescendants().OfType<ScrollViewer>().Single();
            editorScroller.Offset = new Vector(0, 300);
            PumpUntilSettled(window);
            double scrolledY = editor.VerticalOffset;
            Assert.True(scrolledY > 0, "precondition: the document must actually be scrollable and scrolled.");

            view.DataContext = vmB;
            PumpUntilSettled(window);

            view.DataContext = vmA;
            PumpUntilSettled(window); // the scroll restore is deferred (DispatcherPriority.Loaded) — must drain.

            Assert.Equal(scrolledY, editor.VerticalOffset, precision: 1);

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
        }
    }

    // ---- Hostile-review-mandated: a brand-new tab is unaffected — it must never inherit a stale ViewState
    // from whatever the shared EditorViewModel default happens to be (each VM starts with EditorViewState.Initial). ----

    [AvaloniaFact]
    public async Task ABrandNewTabAlwaysOpensWithCaretAtZeroNotSomeStaleValue()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        string pathA = await WriteTempFileAsync("tab A content").ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("tab B content, never visited before").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);

            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.CaretOffset = 6;

            view.DataContext = vmB; // vmB has never been bound to this view before.

            Assert.Equal(0, editor.CaretOffset); // opens at the top, not inheriting vmA's caret.

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
        }
    }

    // ---- Hostile-review CRITICAL fix shape (objection 5): a rapid A -> B -> C -> B fast-switch chain must
    // restore B's OWN caret, never C's, and never a stale queued Post meant for a vm no longer live. ----

    [AvaloniaFact]
    public async Task FastSwitchingAToBToCToBRestoresBsOwnCaretNeverCsOrAStaleQueuedValue()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        EditorViewModel vmC = NewEditorVm();
        string pathA = await WriteTempFileAsync("tab A content").ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("tab B content, distinct").ConfigureAwait(true);
        string pathC = await WriteTempFileAsync("tab C content, also distinct").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);
            await vmC.OpenAsync(pathC, CancellationToken.None).ConfigureAwait(true);

            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            // Visit B once and give it its own distinct caret, settling any pending restore first.
            view.DataContext = vmB;
            Dispatcher.UIThread.RunJobs();
            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.CaretOffset = 4; // "tab |B content..." — B's own position, distinct from A's and C's.

            // Visit C once and give it its own distinct caret.
            view.DataContext = vmC;
            Dispatcher.UIThread.RunJobs();
            editor.CaretOffset = 9; // "tab C con|tent..." — C's own position.

            // Rapid A -> B -> C -> B with NO settling in between — every deferred restore-Post from every hop
            // stays queued until we explicitly drain them below.
            view.DataContext = vmA;
            view.DataContext = vmB;
            view.DataContext = vmC;
            view.DataContext = vmB;

            Dispatcher.UIThread.RunJobs(); // drain every stacked Post now.

            Assert.Equal(4, editor.CaretOffset); // B's OWN caret — never C's (9), never A's, never 0.

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
            DeleteQuietly(pathC);
        }
    }

    // ---- Review-mandated (the gap that hid the CRITICAL scroll-capture bug): the caret probe above proves
    // fast-switch caret/selection are safe because their restore is SYNCHRONOUS, but scroll restore is
    // DEFERRED (DispatcherPriority.Loaded) — a fast hop away from a tab BEFORE its own deferred scroll-restore
    // Post has fired must never capture the transient (post-Document-reassignment) live offset and clobber
    // that tab's real, previously-settled scroll with it. Every hop below is settled (via PumpUntilSettled)
    // EXCEPT the critical B -> C hop, which fires with B's own restore-Post still queued — exactly the fast
    // Ctrl+Tab / rapid-click window the CRITICAL fix (EditorView._scrollSettledVm) targets. ----

    [AvaloniaFact]
    public async Task FastSwitchingBToCToBPreservesBsOwnScrollNeverCsOrATransientZero()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        EditorViewModel vmC = NewEditorVm();
        string longText = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i}"));
        string pathA = await WriteTempFileAsync(longText).ConfigureAwait(true);
        string pathB = await WriteTempFileAsync(longText).ConfigureAwait(true);
        string pathC = await WriteTempFileAsync(longText).ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);
            await vmC.OpenAsync(pathC, CancellationToken.None).ConfigureAwait(true);

            // Short viewport so 200 lines genuinely overflows it (mirrors the scroll round-trip test above).
            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 150 };
            window.Show();
            PumpUntilSettled(window);

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            ScrollViewer editorScroller = editor.GetVisualDescendants().OfType<ScrollViewer>().Single();

            // Visit B once, let its restore-Post settle, then give it its own DISTINCT, SETTLED scroll offset.
            view.DataContext = vmB;
            PumpUntilSettled(window);
            editorScroller.Offset = new Vector(0, 300); // B's own scroll.
            PumpUntilSettled(window);

            // Visit C once, let its restore-Post settle, then give it its own DISTINCT, SETTLED scroll offset.
            view.DataContext = vmC;
            PumpUntilSettled(window);
            editorScroller.Offset = new Vector(0, 700); // C's own scroll, distinct from B's.
            PumpUntilSettled(window);

            // The critical fast hop: switch INTO B (schedules B's deferred scroll-restore-Post, NOT yet run)
            // then immediately AWAY again to C, with NO intervening pump — B's own restore-Post never got a
            // chance to fire, so the live offset right now is the transient post-reassignment value, not a
            // real reading for B.
            view.DataContext = vmB;
            view.DataContext = vmC;

            // Drain every stacked Post now, then hop back to B one final time and let it genuinely settle.
            Dispatcher.UIThread.RunJobs();
            view.DataContext = vmB;
            PumpUntilSettled(window);

            Assert.Equal(300, editor.VerticalOffset, precision: 1); // B's OWN scroll — never C's (700), never 0.

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
            DeleteQuietly(pathC);
        }
    }

    private static EditorViewModel NewEditorVm()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }

    private static async Task<string> WriteTempFileAsync(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "hoardpad-tabcaret-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Pump the dispatcher + force render/layout passes until deferred work settles (mirrors
    /// ChecklistScrollPreserveTests.PumpUntilSettled).</summary>
    private static void PumpUntilSettled(Window window)
    {
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }
}
