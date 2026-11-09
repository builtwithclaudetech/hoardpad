// FindReplaceUndoTests.cs — QA3-5 (DATA-SAFETY): after a Find & Replace, Ctrl+Z must revert the
// replacement — a bulk Replace-All must be recoverable via ONE undo.
//
// Root cause (fixed here): both single Replace and Replace-All routed through
// EditorViewModel.ReplaceBufferText -> EditorView.MirrorDocumentIntoEditor -> `_editor.Text = ...`.
// AvaloniaEdit 11.4.1's TextEditor.Text setter pushes the change to the UndoStack and then IMMEDIATELY
// calls UndoStack.ClearAll() — so the replace was never undoable. The fix routes the find/replace content
// mutation through a NEW EditorViewModel.BufferMutationRequested signal that the view handles via
// `Document.Replace(0, TextLength, newText)` (one undo entry, no ClearAll), while a genuine new-document
// load (tab switch / open / reinterpret) still clears undo via the ORIGINAL `TextEditor.Text` path — a
// Ctrl+Z must never bleed one document's content into another (gap 2).
//
// These tests drive the REAL production chain end to end: FindReplaceViewModel -> EditorViewModelBuffer
// (the production IFindReplaceBuffer adapter, HoardPad.App.Editor) -> EditorViewModel.ReplaceBufferText ->
// EditorView's real AvaloniaEdit TextEditor / TextDocument / UndoStack (headless, [AvaloniaFact]) — NOT the
// FakeBuffer the rest of FindReplaceTests.cs uses, because a FakeBuffer is a plain string field and cannot
// exercise (or catch a regression in) the real AvaloniaEdit undo stack.
//
// Class name matches BOTH verify filters: "FullyQualifiedName~FindReplace" and "FullyQualifiedName~Undo".

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

/// <summary>
/// The production current-file scope (<see cref="IFindReplaceWorkspace"/>) over a REAL, view-bound
/// <see cref="EditorViewModel"/> — wraps it with the production <see cref="EditorViewModelBuffer"/> adapter
/// rather than the in-memory <c>FakeBuffer</c>, so a replace actually flows through the real AvaloniaEdit
/// <c>TextDocument</c>/<c>UndoStack</c>. The other two FR-17 scopes are unused by these tests.
/// </summary>
internal sealed class SingleBufferFindReplaceWorkspace : IFindReplaceWorkspace
{
    private readonly EditorViewModelBuffer _buffer;

    public SingleBufferFindReplaceWorkspace(EditorViewModel vm) => _buffer = new EditorViewModelBuffer(vm);

    public IFindReplaceBuffer? CurrentBuffer => _buffer;

    public IReadOnlyList<IFindReplaceBuffer> OpenBuffers => new IFindReplaceBuffer[] { _buffer };

    public string? OpenFolderRoot => null;

    public Task<string?> ReadFileAsync(string path, CancellationToken ct) => Task.FromResult<string?>(null);

    public IReadOnlyList<string> EnumerateFolderFiles() => Array.Empty<string>();

    public Task WriteFileAsync(string path, string text, CancellationToken ct) => Task.CompletedTask;
}

public sealed class FindReplaceUndoTests
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

    /// <summary>
    /// A window carrying the SAME Window-level Ctrl+Z <see cref="KeyBinding"/> QA3-5 added to
    /// <c>MainWindow.axaml</c> (<c>UndoCommand</c>), plus a plain, inert <see cref="Button"/> standing in
    /// for the docked Find/Replace panel's Replace button — so a test can move REAL keyboard focus off the
    /// editor's <see cref="AvaloniaEdit.Editing.TextArea"/> onto a non-text control, mirroring the exact
    /// repro (focus stays on the panel's button after a click).
    /// </summary>
    private static (EditorViewModel Vm, EditorView View, Window Window, TextEditor Editor, Button OtherControl)
        NewBoundEditorWithWindowLevelUndoBinding()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var vm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        var view = new EditorView { DataContext = vm };
        var otherControl = new Button { Content = "Replace" };
        var root = new StackPanel { Children = { otherControl, view } };
        var window = new Window { Content = root, Width = 400, Height = 300 };
        window.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Z, KeyModifiers.Control),
            Command = vm.UndoCommand,
        });
        window.Show();

        TextEditor editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
        return (vm, view, window, editor, otherControl);
    }

    /// <summary>A real mouse click at the editor's midpoint — proven pattern (BootstrapTests) for giving the
    /// AvaloniaEdit <c>TextArea</c> genuine keyboard focus (a direct <c>.Focus()</c> call on the composite,
    /// templated <see cref="TextEditor"/> is not the same as the real input path a user's click drives).</summary>
    private static void ClickToFocus(Window window, Visual target)
    {
        Point offset = default;
        Visual? current = target;
        while (current is not null && !ReferenceEquals(current, window))
        {
            if (current is Control control)
                offset += control.Bounds.Position;
            current = current.GetVisualParent();
        }
        Point midpoint = offset + new Point(((Control)target).Bounds.Width / 2, ((Control)target).Bounds.Height / 2);
        window.MouseDown(midpoint, MouseButton.Left);
        window.MouseUp(midpoint, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static async Task<string> WriteTempFileAsync(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "hoardpad-undo-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Single Replace must be undoable (proves the root cause + the fix). ----

    [AvaloniaFact]
    public async Task SingleReplaceIsUndoneByCtrlZRestoringTheOriginalText()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("cat cat cat").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();
            Assert.Equal("cat cat cat", editor.Text);

            var frVm = new FindReplaceViewModel(new SingleBufferFindReplaceWorkspace(vm), ScriptedReviewPrompt.ConfirmAll());
            frVm.FindText = "cat";
            frVm.ReplaceText = "dog";

            frVm.ReplaceCommand.Execute(null); // single Replace — the first match only.
            Assert.Equal("dog cat cat", editor.Text);

            vm.UndoCommand.Execute(null); // the Edit -> Undo / Ctrl+Z path (BUG-9 wiring).

            Assert.Equal("cat cat cat", editor.Text); // the ORIGINAL text is restored.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- Replace-All (multiple matches) reverts in ONE undo. ----

    [AvaloniaFact]
    public async Task ReplaceAllIsUndoneByOneCtrlZRestoringTheFullOriginalText()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        const string original = "cat sat on the cat mat with another cat";
        string path = await WriteTempFileAsync(original).ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();
            Assert.Equal(original, editor.Text);

            var frVm = new FindReplaceViewModel(new SingleBufferFindReplaceWorkspace(vm), ScriptedReviewPrompt.ConfirmAll());
            frVm.FindText = "cat";
            frVm.ReplaceText = "dog";
            frVm.Scope = FindReplaceScope.CurrentFile;

            await frVm.ReplaceAllCommand.ExecuteAsync(null).ConfigureAwait(true);
            const string replaced = "dog sat on the dog mat with another dog";
            Assert.Equal(replaced, editor.Text); // 3 matches replaced.

            vm.UndoCommand.Execute(null); // ONE undo.

            Assert.Equal(original, editor.Text); // the WHOLE replace-all reverts in a single Ctrl+Z.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- Caret behavior after a replace (QA3-5 decision: snap to the start, matching prior behavior). ----

    [AvaloniaFact]
    public async Task ReplaceSnapsTheCaretToTheStartOfTheBuffer()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("cat cat cat").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();
            editor.CaretOffset = 5; // move the caret away from 0 before the replace.

            var frVm = new FindReplaceViewModel(new SingleBufferFindReplaceWorkspace(vm), ScriptedReviewPrompt.ConfirmAll());
            frVm.FindText = "cat";
            frVm.ReplaceText = "dog";

            frVm.ReplaceCommand.Execute(null);

            Assert.Equal(0, editor.CaretOffset); // the decided caret behavior: snap to 0, as before the fix.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- GAP 2: a new-document load must still clear undo — a replace's undo entry must not bleed across opens. ----

    [AvaloniaFact]
    public async Task OpeningADifferentFileClearsUndoSoAPriorReplaceCannotBleedAcrossTheOpen()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string pathA = await WriteTempFileAsync("document A original text").ConfigureAwait(true);
        string pathB = await WriteTempFileAsync("document B content").ConfigureAwait(true);
        try
        {
            // Open document A and push an undoable find/replace onto the stack.
            await vm.OpenAsync(pathA, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            var frVm = new FindReplaceViewModel(new SingleBufferFindReplaceWorkspace(vm), ScriptedReviewPrompt.ConfirmAll());
            frVm.FindText = "original";
            frVm.ReplaceText = "REPLACED";
            frVm.ReplaceCommand.Execute(null);
            Assert.Equal("document A REPLACED text", editor.Text);

            // Open document B into the SAME view/vm (a new-document load, not a find/replace mutation).
            await vm.OpenAsync(pathB, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();
            Assert.Equal("document B content", editor.Text);

            // Undo must NOT bring back document A's (pre- or post-replace) content.
            vm.UndoCommand.Execute(null);

            Assert.Equal("document B content", editor.Text); // unchanged — no bleed-through from document A.
        }
        finally
        {
            window.Close();
            DeleteQuietly(pathA);
            DeleteQuietly(pathB);
        }
    }

    // ---- GAP 1: a pure grammar change (QA2-13 Language menu) must not touch text or wipe undo. ----

    [AvaloniaFact]
    public async Task LanguageMenuGrammarChangeDoesNotWipeAPendingReplaceUndoEntry()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor) = NewBoundEditor();
        string path = await WriteTempFileAsync("cat cat cat").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            var frVm = new FindReplaceViewModel(new SingleBufferFindReplaceWorkspace(vm), ScriptedReviewPrompt.ConfirmAll());
            frVm.FindText = "cat";
            frVm.ReplaceText = "dog";
            frVm.ReplaceCommand.Execute(null);
            Assert.Equal("dog cat cat", editor.Text);

            // A pure grammar-scope switch — text is unchanged; this must not clear the undo stack.
            vm.SetLanguageCommand.Execute("Python");

            vm.UndoCommand.Execute(null);

            Assert.Equal("cat cat cat", editor.Text); // the replace is still undoable after a language switch.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    // ---- Hostile-review fix: Ctrl+Z was a dead key whenever focus ended up outside the editor's TextArea
    // (e.g. right after clicking Replace in the docked Find/Replace panel) because only a display-only
    // MenuItem InputGesture existed — no functional Window-level KeyBinding. QA3-5's acceptance criterion is
    // literally "Ctrl+Z reverts a replace", so this must work no matter where focus ends up after the
    // replace, not just via Edit > Undo or with the editor already focused. ----

    [AvaloniaFact]
    public async Task WindowLevelCtrlZUndoesAReplaceWhenFocusIsOnANonEditorControl()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor, Button otherControl) =
            NewBoundEditorWithWindowLevelUndoBinding();
        string path = await WriteTempFileAsync("cat cat cat").ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            var frVm = new FindReplaceViewModel(new SingleBufferFindReplaceWorkspace(vm), ScriptedReviewPrompt.ConfirmAll());
            frVm.FindText = "cat";
            frVm.ReplaceText = "dog";
            frVm.ReplaceCommand.Execute(null);
            Assert.Equal("dog cat cat", editor.Text);

            // Move focus OFF the editor onto a plain, non-text control — the real repro: after clicking
            // Replace in the docked Find/Replace panel, focus stays on that button, not the TextArea.
            otherControl.Focus();
            window.UpdateLayout();

            // A real Ctrl+Z KEY PRESS (not a direct command call) — the focused control (a Button) has no
            // Ctrl+Z handling of its own, so this only reverts if the Window-level KeyBinding added for
            // QA3-5 actually fires.
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

            Assert.Equal("cat cat cat", editor.Text); // the Window-level accelerator fired Undo.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }

    [AvaloniaFact]
    public async Task EditorFocusedCtrlZUndoesExactlyOneEditNotTwo()
    {
        (EditorViewModel vm, EditorView view, Window window, TextEditor editor, Button _) =
            NewBoundEditorWithWindowLevelUndoBinding();
        string path = await WriteTempFileAsync(string.Empty).ConfigureAwait(true);
        try
        {
            await vm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            view.MirrorActiveDocument();

            ClickToFocus(window, editor);
            Assert.True(editor.TextArea.IsFocused);

            // Two discrete edits, each its own undo entry (mirrors two separate real keystrokes/edits).
            AvaloniaEdit.Document.TextDocument document = editor.Document!;
            document.UndoStack.StartUndoGroup();
            document.Insert(0, "A");
            document.UndoStack.EndUndoGroup();
            document.UndoStack.StartUndoGroup();
            document.Insert(document.TextLength, "B");
            document.UndoStack.EndUndoGroup();
            Assert.Equal("AB", editor.Text);

            // ONE Ctrl+Z key press with the editor's TextArea focused. AvaloniaEdit's own TextArea input
            // handler must consume it first (marks the routed KeyDown event Handled) so the Window-level
            // KeyBinding added for QA3-5 never re-fires on the SAME key press — otherwise BOTH edits would
            // disappear (a double-undo).
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

            Assert.Equal("A", editor.Text); // exactly ONE edit undone, not two.
        }
        finally
        {
            window.Close();
            DeleteQuietly(path);
        }
    }
}
