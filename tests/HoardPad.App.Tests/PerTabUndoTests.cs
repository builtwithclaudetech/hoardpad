// PerTabUndoTests.cs — QA3-22 (DATA-SAFETY): editor undo history was WIPED on every tab switch because a
// single AvaloniaEdit TextEditor/TextDocument was shared across all tabs — MirrorDocumentIntoEditor's
// `_editor.Text = ...` (via the TextEditor.Text setter) clears the ONE shared UndoStack on every switch, so
// typing in tab A, switching to tab B and back to A lost A's entire undo history.
//
// Fix (EditorView.axaml.cs, SwitchToTabDocument): each EditorViewModel (one per open tab) now gets its OWN
// AvaloniaEdit TextDocument/UndoStack, cached in a ConditionalWeakTable keyed by the VM instance. A genuine
// DataContext swap (an actual tab switch) reattaches that tab's own document instead of mutating the
// previously-attached one. A brand-new tab (never visited) gets a fresh TextDocument, which starts with an
// empty UndoStack by construction — no explicit clear needed, and no bleed from whichever tab was active
// before it. The original whole-buffer clear-undo path (MirrorDocumentIntoEditor, reached via
// MirrorActiveDocument()) is UNCHANGED and still used for a genuine new-document load on the CURRENTLY-
// ATTACHED tab (QA3-5 gap 2 — see FindReplaceUndoTests.cs, which must stay green after this fix).
//
// These tests drive the REAL production chain: EditorView's real AvaloniaEdit TextEditor / TextDocument /
// UndoStack (headless, [AvaloniaFact]) across an ACTUAL DataContext swap between two distinct EditorViewModel
// instances — not a mocked view or a FakeBuffer — because only the real AvaloniaEdit undo stack can prove
// (or catch a regression in) per-tab undo survival.
//
// Class name contains "Undo" so --filter "FullyQualifiedName~Undo" resolves it (matches FindReplaceUndoTests
// convention, and the schedule's --filter "FullyQualifiedName~Undo" QA3-5 baseline check).

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class PerTabUndoTests
{
    private static EditorViewModel NewEditorVm()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }

    private static async Task<string> WriteTempFileAsync(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "hoardpad-tabundo-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- The core Done-when: type in A -> switch to B -> switch back to A -> Ctrl+Z still reverts A's edit. ----

    [AvaloniaFact]
    public async Task TypingInTabAThenRoundTrippingThroughTabBPreservesTabAsUndoHistory()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        string pathA = await WriteTempFileAsync("tab A original").ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("tab B original").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);

            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            Assert.Equal("tab A original", editor.Text);

            // A real, undoable edit on tab A (mirrors a keystroke reaching the live TextDocument).
            editor.Document!.Insert(editor.Document.TextLength, " EDITED");
            Assert.Equal("tab A original EDITED", editor.Text);

            // Switch to tab B (a genuinely different DocumentTab's EditorViewModel)...
            view.DataContext = vmB;
            Assert.Equal("tab B original", editor.Text);

            // ...and back to tab A.
            view.DataContext = vmA;
            Assert.Equal("tab A original EDITED", editor.Text); // the round-trip did not lose A's edit.

            vmA.UndoCommand.Execute(null);

            Assert.Equal("tab A original", editor.Text); // the round-trip did NOT wipe A's undo history.

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
        }
    }

    // ---- Data-safety guard: a brand-new tab (never visited) starts with a clean undo stack, independent of
    // whatever tab was active immediately before it — no bleed-through from the previous tab's edits. ----

    [AvaloniaFact]
    public async Task SwitchingToABrandNewTabStartsWithAnEmptyUndoStack()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        string pathA = await WriteTempFileAsync("tab A original").ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("tab B original").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);

            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.Document!.Insert(editor.Document.TextLength, " EDITED");
            Assert.True(editor.Document.UndoStack.CanUndo);

            // vmB has never been bound to this view before — a brand-new tab.
            view.DataContext = vmB;

            Assert.Equal("tab B original", editor.Text);
            Assert.False(editor.Document!.UndoStack.CanUndo); // no bleed from tab A's pending undo entry.

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
        }
    }

    // ---- QA3-22 survivor: a NULL-intermediate DataContext transition (A -> null -> A, exactly what
    // MainWindowViewModel.ActivateTab drives when a checklist/Settings tab sits between two visits to the
    // same text tab — Editor = tab?.Editor is null for those tab kinds) must NOT wipe A's undo history. Before
    // the fix, OnDataContextChanged's null branch returned before updating _subscribedViewModel, so the final
    // re-bind to A was mistaken for a same-VM re-mirror (MirrorDocumentIntoEditor, which clears undo) instead
    // of a genuine tab switch (SwitchToTabDocument, which preserves it). ----

    [AvaloniaFact]
    public async Task TypingInTabAThenRoundTrippingThroughANullDataContextPreservesTabAsUndoHistory()
    {
        EditorViewModel vmA = NewEditorVm();
        string pathA = await WriteTempFileAsync("tab A original").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);

            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            Assert.Equal("tab A original", editor.Text);

            // A real, undoable edit on tab A.
            editor.Document!.Insert(editor.Document.TextLength, " EDITED");
            Assert.Equal("tab A original EDITED", editor.Text);

            // A -> null -> A: the null step is what a checklist/Settings tab activation drives (Editor = null).
            view.DataContext = null;
            view.DataContext = vmA;

            Assert.Equal("tab A original EDITED", editor.Text); // the round-trip did not lose A's edit.

            for (int i = 0; i < 5; i++)
            {
                view.DataContext = null;
                view.DataContext = vmA;
            }

            Assert.Equal("tab A original EDITED", editor.Text); // repeated null round-trips are stable too.

            vmA.UndoCommand.Execute(null);

            Assert.Equal("tab A original", editor.Text); // the null round-trip did NOT wipe A's undo history.

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
        }
    }

    // ---- Background-mutation safety: if a tab's content changed while it was INACTIVE (e.g. a Find/Replace
    // "All Open Files" pass, which writes EditorViewModel.Document directly with no view subscriber to react
    // while that tab isn't the bound DataContext), switching back to it must show the NEW content (not stale
    // cached text) and the change must land as one undoable edit — not silently unobservable, and not a full
    // undo-stack wipe of whatever that tab had already accumulated. ----

    [AvaloniaFact]
    public async Task ReactivatingATabWhoseContentChangedWhileInactiveShowsTheNewTextAsOneUndoableEdit()
    {
        EditorViewModel vmA = NewEditorVm();
        EditorViewModel vmB = NewEditorVm();
        string pathA = await WriteTempFileAsync("tab A original").ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("tab B original").ConfigureAwait(true);
        try
        {
            await vmA.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            await vmB.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);

            var view = new EditorView { DataContext = vmA };
            var window = new Window { Content = view, Width = 400, Height = 300 };
            window.Show();

            // Visit tab A once (creates + caches its TextDocument), then switch away to tab B.
            view.DataContext = vmB;

            // Tab A is now inactive. Mutate it directly, exactly as a background Find/Replace "All Open
            // Files" pass would (EditorViewModelBuffer.SetText -> EditorViewModel.ReplaceBufferText).
            vmA.ReplaceBufferText("tab A REPLACED while inactive");

            // Switch back to tab A.
            view.DataContext = vmA;

            TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            Assert.Equal("tab A REPLACED while inactive", editor.Text); // the background change is visible.

            vmA.UndoCommand.Execute(null);

            Assert.Equal("tab A original", editor.Text); // ONE undo reverts the whole background replace.

            window.Close();
        }
        finally
        {
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
        }
    }
}
