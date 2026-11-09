// AiPanelPasteFocusTests.cs — QA4-14: Ctrl+V (and Ctrl+C/Ctrl+X) while the AI panel's prompt input has
// focus must act on the AI input, never the document editor.
//
// ROOT CAUSE (confirmed against Avalonia 11.3.6's Avalonia.Input.KeyboardDevice.ProcessRawEvent, decompiled):
// a KeyDown's KeyBindings are walked from the FOCUSED element UP the visual tree to the root (the Window)
// BEFORE the routed KeyDownEvent is even raised. The first ancestor whose Gesture matches AND whose
// Command.CanExecute returns true wins outright — marks the event Handled and stops the walk — so no
// descendant (including the focused control's own native key handling) ever runs. MainWindow's Ctrl+X/C/V
// KeyBindings (FEAT-3) forward UNCONDITIONALLY to the active editor's Cut/Copy/PasteCommand, so with no
// closer-scoped binding they always won this walk, even while focus sat in a plain TextBox elsewhere in the
// shell (the AI panel's PromptBox) — exactly the same mechanism FEAT-5's TextArea-level Ctrl+R rebind already
// exploits for Redo (see RedoRebindTests.cs's doc comment).
//
// FIX (AiPanelView.axaml.cs): give PromptBox its OWN local KeyBindings for Ctrl+X/C/V that call ITS OWN
// Cut()/Copy()/Paste(). Since the walk starts at the focused leaf, PromptBox's bindings are found FIRST and
// always win while it has focus — MainWindow's forwarding bindings are never reached.
//
// This file drives the REAL MainWindow (both PromptBox and the editor's TextArea can independently hold
// real Avalonia focus) via the same window.KeyPressQwerty the RedoRebindTests/StandardShortcutsTests
// pattern uses, so the assertions exercise the actual KeyboardDevice routing, not a code-behind shortcut.
//
// Class name matches the verify filter "AiPanelPasteFocus".

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

public sealed class AiPanelPasteFocusTests
{
    private static async Task<(MainWindowViewModel Shell, MainWindow Window, TextBox PromptBox, TextEditor Editor)>
        NewBoundWindowAsync(string documentText)
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorVm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        string path = Path.Combine(Path.GetTempPath(), "qa414-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, documentText).ConfigureAwait(true);
        try
        {
            await editorVm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            DeleteQuietly(path);
        }

        var aiPanelVm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        var shell = new MainWindowViewModel(editorVm, aiPanelVm) { IsAiPanelVisible = true };
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        TextBox promptBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptBox");
        TextEditor editor = window.GetVisualDescendants().OfType<TextEditor>().Single();
        return (shell, window, promptBox, editor);
    }

    /// <summary>The RedoRebindTests click-to-focus helper (a real click routes real Avalonia focus into a
    /// composite templated control like the AvaloniaEdit TextEditor, unlike a bare <c>.Focus()</c> call).</summary>
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

    [AvaloniaFact]
    public async Task CtrlVWithAiInputFocusedPastesIntoTheAiInputNotTheEditor()
    {
        (MainWindowViewModel shell, MainWindow window, TextBox promptBox, TextEditor editor) =
            await NewBoundWindowAsync("original document text").ConfigureAwait(true);
        try
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("pasted-from-clipboard");

            promptBox.Focus();
            window.UpdateLayout();
            Assert.True(promptBox.IsFocused);

            window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // PromptInput starts empty and the expected pasted content is fully known, so tighten to an
            // exact-equality assertion (Assert.Contains would silently pass even on a double-paste).
            Assert.Equal("pasted-from-clipboard", shell.AiPanel!.PromptInput);
            Assert.Equal("original document text", editor.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlVWithEditorFocusedStillPastesIntoTheEditor()
    {
        (MainWindowViewModel shell, MainWindow window, TextBox promptBox, TextEditor editor) =
            await NewBoundWindowAsync("").ConfigureAwait(true);
        try
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("pasted-into-editor");

            ClickToFocus(window, editor);
            Assert.True(editor.TextArea.IsFocused);

            window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // The document started empty and the expected pasted content is fully known, so tighten to an
            // exact-equality assertion (Assert.Contains would silently pass even on a double-paste).
            Assert.Equal("pasted-into-editor", editor.Text);
            Assert.Equal(string.Empty, promptBox.Text ?? string.Empty);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlXWithAiInputFocusedCutsTheAiInputsSelectionNotTheEditors()
    {
        // W2: Ctrl+X was untested — select text in PromptBox, Ctrl+X removes it from PromptBox, places it
        // on the clipboard, and leaves the editor's document untouched.
        (MainWindowViewModel shell, MainWindow window, TextBox promptBox, TextEditor editor) =
            await NewBoundWindowAsync("editor-secret-text").ConfigureAwait(true);
        try
        {
            shell.AiPanel!.PromptInput = "cut-me";
            window.UpdateLayout();
            promptBox.Focus();
            promptBox.SelectAll();
            window.UpdateLayout();
            Assert.True(promptBox.IsFocused);

            window.KeyPressQwerty(PhysicalKey.X, RawInputModifiers.Control);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)!.Clipboard!;
            Assert.Equal("cut-me", await clipboard.GetTextAsync());
            Assert.Equal(string.Empty, shell.AiPanel!.PromptInput); // removed from the AI input.
            Assert.Equal("editor-secret-text", editor.Text); // the editor's document is untouched.
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlCWithAiInputFocusedCopiesTheAiInputsSelectionNotTheEditors()
    {
        (MainWindowViewModel shell, MainWindow window, TextBox promptBox, TextEditor editor) =
            await NewBoundWindowAsync("editor-secret-text").ConfigureAwait(true);
        try
        {
            shell.AiPanel!.PromptInput = "ai-input-text";
            window.UpdateLayout();
            promptBox.Focus();
            promptBox.SelectAll();
            window.UpdateLayout();
            Assert.True(promptBox.IsFocused);

            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)!.Clipboard!;
            Assert.Equal("ai-input-text", await clipboard.GetTextAsync());
        }
        finally
        {
            window.Close();
        }
    }
}
