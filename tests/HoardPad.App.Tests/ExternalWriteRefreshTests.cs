// ExternalWriteRefreshTests.cs — QA3-9 (safety-adjacent): after the AI edits an open document ON DISK
// (outside the editor — the M11 live-write / FR-39 replay path), the editor buffer must refresh to show
// the change, or a later manual Save silently clobbers the AI's edit with stale in-memory bytes.
//
// Root mechanism (added here): EditorViewModel.RefreshFromDiskAsync re-reads the document's current path
// under its existing encoding and, if the decoded text differs from the buffer, raises a NEW
// ExternalWriteApplied signal — distinct from QA3-5's BufferMutationRequested (find/replace) because the
// view PRESERVES the caret's character offset for this signal (clamped to the new length) instead of
// resetting it to 0, matching the QA3-9 "preserve cursor position where possible" requirement. Both
// signals apply the new text via the SAME undo-preserving `Document.Replace(0, TextLength, newText)`
// mechanism (one undo entry, no UndoStack.ClearAll()), so Ctrl+Z reverts an AI-applied refresh exactly as
// it reverts a find/replace (QA3-20 — verified end to end by these tests, not merely by inspection).
//
// These tests drive the REAL production chain: EditorViewModel.RefreshFromDiskAsync -> EditorView's real
// AvaloniaEdit TextEditor/TextDocument/UndoStack (headless, [AvaloniaFact]) — mirrors FindReplaceUndoTests'
// harness (NewBoundEditor / RealAtomicFileStore / real temp files) so the fix is proven against the same
// undo machinery QA3-5 already hardened, not a fake buffer that could hide a regression.

using System;
using System.IO;
using System.Linq;
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

public sealed class ExternalWriteRefreshTests
{
    private static (EditorViewModel Vm, EditorView View, Window Window, TextEditor Editor) NewBoundEditor()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var vm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        var view = new EditorView { DataContext = vm };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();

        TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
        return (vm, view, window, editor);
    }

    private static async Task<string> WriteTempFileAsync(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "hoardpad-extrefresh-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Core requirement: an on-disk change under an open buffer refreshes the view. ----

    [AvaloniaFact]
    public async Task OnDiskChangeUnderAnOpenBufferRefreshesTheEditorView()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("original content").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();
            Assert.Equal("original content", editor.Text);

            // Simulate the AI writing to the file OUTSIDE the editor (the M11 live-write / FR-39 replay path).
            await File.WriteAllTextAsync(path, "AI-edited content", CancellationToken.None).ConfigureAwait(true);

            await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Equal("AI-edited content", editor.Text); // the view refreshed to show the AI's edit.
            Assert.Equal("AI-edited content", vm.Document.Text);
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- QA3-20: the refresh must be undoable (Ctrl+Z reverts the AI's edit). ----

    [AvaloniaFact]
    public async Task RefreshIsUndoneByCtrlZRestoringThePreRefreshBuffer()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("original content").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            await File.WriteAllTextAsync(path, "AI-edited content", CancellationToken.None).ConfigureAwait(true);
            await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true);
            Assert.Equal("AI-edited content", editor.Text);

            vm.UndoCommand.Execute(null); // Ctrl+Z / Edit ▸ Undo path (BUG-9 wiring).

            Assert.Equal("original content", editor.Text); // the AI's edit is reverted.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- QA3-9 "preserve cursor position where possible": unlike QA3-5's find/replace (snaps to 0), an
    // AI refresh preserves the caret's character offset (clamped to the new length). ----

    [AvaloniaFact]
    public async Task RefreshPreservesTheCaretOffsetInsteadOfSnappingToZero()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("line one\nline two\nline three").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();
            editor.CaretOffset = 10; // somewhere in "line two".

            // A same-length edit elsewhere in the document (the offset 10 position stays valid either way).
            await File.WriteAllTextAsync(path, "line ONE\nline two\nline three", CancellationToken.None)
                .ConfigureAwait(true);
            await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Equal(10, editor.CaretOffset); // preserved, not reset to 0.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    [AvaloniaFact]
    public async Task RefreshClampsThePreservedCaretOffsetWhenTheNewDocumentIsShorter()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("a fairly long original line of text").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();
            editor.CaretOffset = 30; // past the end of the shorter replacement below.

            await File.WriteAllTextAsync(path, "short", CancellationToken.None).ConfigureAwait(true);
            await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Equal(5, editor.CaretOffset); // clamped to the new (shorter) document length.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- The core safety concern: a later manual Save must not clobber the AI's edit with stale bytes. ----

    [AvaloniaFact]
    public async Task SaveAfterARefreshWritesTheAisContentNotTheStaleBuffer()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("original content").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            await File.WriteAllTextAsync(path, "AI-edited content", CancellationToken.None).ConfigureAwait(true);
            await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true);

            // A later manual Save (e.g. a stray Ctrl+S) must round-trip the AI's content, not the stale
            // pre-refresh buffer — that IS the QA3-9 safety concern.
            await vm.SaveAsync(CancellationToken.None).ConfigureAwait(true);

            string onDisk = await File.ReadAllTextAsync(path, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal("AI-edited content", onDisk);
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- The refreshed buffer is not left flagged dirty — it matches disk exactly, mirroring Open. ----

    [AvaloniaFact]
    public async Task RefreshLeavesTheBufferNotDirty()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("original content").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            await File.WriteAllTextAsync(path, "AI-edited content", CancellationToken.None).ConfigureAwait(true);
            await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.False(vm.IsDirty);
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- No-op when the disk content already matches the buffer (nothing to refresh, no spurious undo entry). ----

    [AvaloniaFact]
    public async Task RefreshIsANoOpWhenDiskAlreadyMatchesTheBuffer()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("unchanged content").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Equal("unchanged content", editor.Text);
            Assert.False(editor.CanUndo); // no spurious undo entry was pushed for a no-op refresh.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- A scratch buffer (no on-disk path) can never be the target of an AI file op refresh — no-op. ----

    [AvaloniaFact]
    public async Task RefreshIsANoOpForAScratchBufferWithNoPath()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var vm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        await vm.RefreshFromDiskAsync(CancellationToken.None).ConfigureAwait(true); // must not throw.

        Assert.Equal(string.Empty, vm.Document.Text);
    }
}
