// StandardShortcutsTests.cs — FEAT-3: standard keyboard shortcuts (Ctrl+N/O/S, plus Ctrl+X/C/V) as real
// Window-level KeyBindings, not just editor-focused InputGestures.
//
// Before this change, Ctrl+N/O/S had NO binding anywhere in the app (not even AvaloniaEdit-native — those
// gestures carry no built-in meaning for a plain text editor), so they simply did nothing regardless of
// focus. Ctrl+X/C/V DID already work, but only through AvaloniaEdit's own built-in TextArea handling while
// the editor's TextArea itself had focus — pressing them anywhere else in the window (a menu, the toolbar,
// the folder tree, a checklist row) was a dead key, exactly the gap QA3-5's Ctrl+Z fix closed for Undo.
//
// This file mirrors two proven patterns already in this suite:
//   - RedoRebindTests's "structural" assertions on the real, XAML-loaded MainWindow.KeyBindings collection
//     (WindowLevelRedoKeyBindingIsCtrlR) — confirms each gesture exists and maps to the correct command.
//   - RedoRebindTests's "focus OFF the editor" behavioral proof (WindowLevelCtrlRRedoesWhenFocusIsOnANonEditorControl)
//     — confirms the accelerator actually fires when the editor's TextArea does NOT have focus.
//
// Class name matches the verify filter "FullyQualifiedName~StandardShortcuts".

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class StandardShortcutsTests
{
    private static async Task<(MainWindowViewModel Shell, MainWindow Window)> NewBoundMainWindowAsync()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorVm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        string path = Path.Combine(Path.GetTempPath(), "stdshortcuts-" + System.Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "").ConfigureAwait(true);
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
        return (shell, window);
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Structural: the real, XAML-loaded MainWindow.KeyBindings collection. ----

    [AvaloniaTheory]
    [InlineData(Key.N, KeyModifiers.Control, nameof(MainWindowViewModel.NewDocumentCommand))]
    [InlineData(Key.O, KeyModifiers.Control, nameof(MainWindowViewModel.OpenDocumentCommand))]
    [InlineData(Key.S, KeyModifiers.Control, nameof(MainWindowViewModel.SaveDocumentCommand))]
    // QA4-14/W1: the Window-level gestures bind to the focus-scoped WindowLevel* wrappers, not the plain
    // CutCommand/CopyCommand/PasteCommand the Edit menu still uses (see MainWindowViewModel's doc comments).
    [InlineData(Key.X, KeyModifiers.Control, nameof(MainWindowViewModel.WindowLevelCutCommand))]
    [InlineData(Key.C, KeyModifiers.Control, nameof(MainWindowViewModel.WindowLevelCopyCommand))]
    [InlineData(Key.V, KeyModifiers.Control, nameof(MainWindowViewModel.WindowLevelPasteCommand))]
    public async Task WindowLevelKeyBindingExistsAndMapsToTheRightCommand(Key key, KeyModifiers modifiers, string commandPropertyName)
    {
        (MainWindowViewModel shell, MainWindow window) = await NewBoundMainWindowAsync().ConfigureAwait(true);
        try
        {
            object? expectedCommand = typeof(MainWindowViewModel)
                .GetProperty(commandPropertyName)!
                .GetValue(shell);
            Assert.NotNull(expectedCommand);

            KeyBinding binding = Assert.Single(window.KeyBindings, kb => ReferenceEquals(kb.Command, expectedCommand));
            Assert.Equal(key, binding.Gesture.Key);
            Assert.Equal(modifiers, binding.Gesture.KeyModifiers);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- Behavioral, focus OFF the editor: Ctrl+N/O/S previously had no binding at all, so this also
    //      proves they now fire regardless of where focus is in the window (FEAT-3's Done-when). ----

    [AvaloniaFact]
    public async Task WindowLevelCtrlNFiresWhenFocusIsOnANonEditorControl()
    {
        (MainWindowViewModel shell, MainWindow window) = await NewBoundMainWindowAsync().ConfigureAwait(true);
        try
        {
            int callCount = 0;
            shell.NewDocumentHandler = () => { callCount++; return Task.CompletedTask; };

            // Any non-editor control works — focus it explicitly so the editor's TextArea does NOT have focus
            // (mirrors RedoRebindTests.WindowLevelCtrlRRedoesWhenFocusIsOnANonEditorControl).
            Button nonEditorControl = window.GetVisualDescendants().OfType<Button>().First();
            nonEditorControl.Focus();
            window.UpdateLayout();

            window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control);
            await Task.Yield();

            Assert.Equal(1, callCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WindowLevelCtrlOFiresWhenFocusIsOnANonEditorControl()
    {
        (MainWindowViewModel shell, MainWindow window) = await NewBoundMainWindowAsync().ConfigureAwait(true);
        try
        {
            int callCount = 0;
            shell.OpenDocumentHandler = () => { callCount++; return Task.CompletedTask; };

            Button nonEditorControl = window.GetVisualDescendants().OfType<Button>().First();
            nonEditorControl.Focus();
            window.UpdateLayout();

            window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
            await Task.Yield();

            Assert.Equal(1, callCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WindowLevelCtrlSFiresWhenFocusIsOnANonEditorControl()
    {
        (MainWindowViewModel shell, MainWindow window) = await NewBoundMainWindowAsync().ConfigureAwait(true);
        try
        {
            int callCount = 0;
            shell.SaveDocumentHandler = () => { callCount++; return Task.CompletedTask; };

            Button nonEditorControl = window.GetVisualDescendants().OfType<Button>().First();
            nonEditorControl.Focus();
            window.UpdateLayout();

            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await Task.Yield();

            Assert.Equal(1, callCount);
        }
        finally
        {
            window.Close();
        }
    }
}
