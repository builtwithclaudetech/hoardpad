// RedoRebindTests.cs — FEAT-5: Redo Ctrl+Y -> Ctrl+R (unblocked by QA3-22's per-tab undo).
//
// Root fact this file protects against regressing: AvaloniaEdit's TextAreaDefaultInputHandler wires
// ApplicationCommands.Redo to Avalonia's default hotkey gesture (Ctrl+Y) via its OWN RoutedCommand /
// CommandBindings mechanism — entirely independent of any Window-level KeyBinding. Before this change,
// MainWindow.axaml never bound Ctrl+Y at the Window level at all, yet Ctrl+Y still redid whenever the
// editor's TextArea had real focus, purely through that built-in AvaloniaEdit handler. Confirmed
// empirically (throwaway probe, not committed) that a TextArea-level KeyBinding for a gesture pre-empts
// that built-in RoutedCommand handler before it fires — the same mechanism WireLineOperationKeyBindings's
// existing entries already rely on. The fix (EditorView.axaml.cs's WireRedoRebindKeyBindings):
//   - Ctrl+R -> _editor.Redo() at the TextArea level (fires when the editor has focus).
//   - Ctrl+Y -> a no-op at the TextArea level (disarms AvaloniaEdit's built-in Redo binding).
//   - Ctrl+R -> RedoCommand at the Window level (MainWindow.axaml, mirrors QA3-5's Ctrl+Z entry — fires
//     when focus is OFF the editor, e.g. right after a Find/Replace panel action).
//
// Class name matches the verify filter "FullyQualifiedName~Redo".

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

public sealed class RedoRebindTests
{
    private static async Task<(MainWindowViewModel Shell, EditorViewModel Editor, MainWindow Window, TextEditor TextEditor)>
        NewBoundMainWindowAsync(string initialText)
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorVm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        string path = Path.Combine(Path.GetTempPath(), "redorebind-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, initialText).ConfigureAwait(true);
        try
        {
            await editorVm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            DeleteQuietly(path);
        }

        var shell = new MainWindowViewModel(editorVm);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        // The FIRST DataContextChanged on EditorView fires here (window.Show/UpdateLayout realizes the visual
        // tree), with editorVm already carrying the opened text — SwitchToTabDocument seeds the fresh
        // TextDocument straight from vm.Document.Text, so no separate MirrorActiveDocument() call is needed
        // (unlike FindReplaceUndoTests, which binds the EditorView BEFORE calling OpenAsync).
        TextEditor editor = window.GetVisualDescendants().OfType<TextEditor>().Single();
        return (shell, editorVm, window, editor);
    }

    private static void ClickToFocus(Window window, Avalonia.Visual target)
    {
        Avalonia.Point offset = default;
        Avalonia.Visual? current = target;
        while (current is not null && !ReferenceEquals(current, window))
        {
            if (current is Control control)
                offset += control.Bounds.Position;
            current = current.GetVisualParent();
        }
        Avalonia.Point midpoint = offset + new Avalonia.Point(((Control)target).Bounds.Width / 2, ((Control)target).Bounds.Height / 2);
        window.MouseDown(midpoint, MouseButton.Left);
        window.MouseUp(midpoint, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Structural: the real, XAML-loaded MainWindow.KeyBindings collection. ----

    [AvaloniaFact]
    public async Task WindowLevelRedoKeyBindingIsCtrlR()
    {
        (MainWindowViewModel shell, EditorViewModel editorVm, MainWindow window, TextEditor _) =
            await NewBoundMainWindowAsync("").ConfigureAwait(true);
        try
        {
            KeyBinding redoBinding = window.KeyBindings.Single(kb => ReferenceEquals(kb.Command, editorVm.RedoCommand));

            Assert.Equal(Key.R, redoBinding.Gesture.Key);
            Assert.Equal(KeyModifiers.Control, redoBinding.Gesture.KeyModifiers);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task NoWindowLevelKeyBindingUsesTheOldCtrlYGesture()
    {
        (MainWindowViewModel _, EditorViewModel _, MainWindow window, TextEditor _) =
            await NewBoundMainWindowAsync("").ConfigureAwait(true);
        try
        {
            Assert.DoesNotContain(window.KeyBindings, kb => kb.Gesture.Key == Key.Y && kb.Gesture.KeyModifiers == KeyModifiers.Control);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- Behavioral, editor focused: TextArea-level rebind (EditorView.axaml.cs). ----

    [AvaloniaFact]
    public async Task EditorFocusedCtrlRRedoesTheLastUndoneEdit()
    {
        (MainWindowViewModel _, EditorViewModel _, MainWindow window, TextEditor editor) =
            await NewBoundMainWindowAsync("").ConfigureAwait(true);
        try
        {
            ClickToFocus(window, editor);
            Assert.True(editor.TextArea.IsFocused);

            AvaloniaEdit.Document.TextDocument document = editor.Document!;
            document.UndoStack.StartUndoGroup();
            document.Insert(0, "A");
            document.UndoStack.EndUndoGroup();
            Assert.Equal("A", editor.Text);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); // undo
            Assert.Equal(string.Empty, editor.Text);

            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control); // the NEW redo accelerator
            Assert.Equal("A", editor.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EditorFocusedCtrlYNoLongerRedoes()
    {
        (MainWindowViewModel _, EditorViewModel _, MainWindow window, TextEditor editor) =
            await NewBoundMainWindowAsync("").ConfigureAwait(true);
        try
        {
            ClickToFocus(window, editor);
            Assert.True(editor.TextArea.IsFocused);

            AvaloniaEdit.Document.TextDocument document = editor.Document!;
            document.UndoStack.StartUndoGroup();
            document.Insert(0, "A");
            document.UndoStack.EndUndoGroup();
            Assert.Equal("A", editor.Text);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); // undo
            Assert.Equal(string.Empty, editor.Text);

            window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control); // the OLD accelerator — now inert
            Assert.Equal(string.Empty, editor.Text); // NOT redone.
        }
        finally
        {
            window.Close();
        }
    }

    // ---- Behavioral, focus OFF the editor: the Window-level accelerator (mirrors QA3-5's Ctrl+Z coverage). ----

    [AvaloniaFact]
    public async Task WindowLevelCtrlRRedoesWhenFocusIsOnANonEditorControl()
    {
        (MainWindowViewModel _, EditorViewModel _, MainWindow window, TextEditor editor) =
            await NewBoundMainWindowAsync("").ConfigureAwait(true);
        try
        {
            ClickToFocus(window, editor);
            AvaloniaEdit.Document.TextDocument document = editor.Document!;
            document.UndoStack.StartUndoGroup();
            document.Insert(0, "A");
            document.UndoStack.EndUndoGroup();
            Assert.Equal("A", editor.Text);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); // undo, editor still focused.
            Assert.Equal(string.Empty, editor.Text);

            // Move focus OFF the editor's TextArea onto the toolbar's "New" button — a plain, non-text control
            // already in the real MainWindow tree (depth-first traversal reaches the toolbar well before the
            // content row's editor), the same non-editor-control role FindReplaceUndoTests's stand-in Button
            // played for Ctrl+Z.
            Button toolbarButton = window.GetVisualDescendants().OfType<Button>().First();
            toolbarButton.Focus();
            window.UpdateLayout();
            Assert.False(editor.TextArea.IsFocused);

            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);

            Assert.Equal("A", editor.Text); // the Window-level Ctrl+R accelerator fired Redo.
        }
        finally
        {
            window.Close();
        }
    }
}
