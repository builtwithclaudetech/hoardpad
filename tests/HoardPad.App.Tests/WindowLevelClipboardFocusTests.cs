// WindowLevelClipboardFocusTests.cs — QA4-14/W1: generalizing the AI-panel paste-focus fix.
//
// AiPanelPasteFocusTests.cs fixed Ctrl+X/C/V hijacking ONLY for the AI panel's own PromptBox (a local
// KeyBindings fix scoped to that one control). The identical hijack was still live wherever ANY other
// plain TextBox in the shell had focus while a document tab was open — the Find/Replace FindBox/ReplaceBox,
// checklist item-rename/AddItemBox, and Settings text fields all sit on the SAME MainWindow.KeyBindings
// ancestor-walk (Avalonia.Input.KeyboardDevice.ProcessRawEvent) with no closer-scoped binding of their own.
//
// The central fix (MainWindowViewModel.WindowLevelCutCommand/WindowLevelCopyCommand/WindowLevelPasteCommand,
// MainWindow.axaml.cs's IsNonEditorTextInputFocused) covers every one of these surfaces at once, without a
// per-control retrofit. This file proves it end to end against the Find box — the same pattern generalizes
// to every other TextBox the central fix protects.
//
// Class name matches the verify filter "FullyQualifiedName~WindowLevelClipboardFocus".

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class WindowLevelClipboardFocusTests
{
    [AvaloniaFact]
    public async Task CtrlVWithFindBoxFocusedPastesIntoTheFindBoxNotTheEditorDocument()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorVm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        string path = Path.Combine(Path.GetTempPath(), "w1-findbox-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "original document text").ConfigureAwait(true);
        try
        {
            await editorVm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var shell = new MainWindowViewModel(editorVm);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var frVm = new FindReplaceViewModel(new FakeFindReplaceWorkspace(), ScriptedReviewPrompt.ConfirmAll());
        shell.AttachFindReplace(frVm);
        shell.ShowFindCommand.Execute(null); // reveals the real, docked FindReplaceView (design §3.5).
        window.UpdateLayout();

        try
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("pasted-into-findbox");

            TextBox findBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FindBox");
            findBox.Focus();
            window.UpdateLayout();
            Assert.True(findBox.IsFocused);

            window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("pasted-into-findbox", findBox.Text);
            Assert.Equal("original document text", editorVm.Document.Text); // the editor is untouched.
        }
        finally
        {
            window.Close();
        }
    }
}
