// CtrlF4CloseTabTests.cs — QA4-20: Ctrl+F4 closes the active tab (save prompt first).
//
// CloseActiveTabCommand (BUG-3) already routes through ConfirmCloseTab — the real modal unsaved-changes
// save prompt — before closing a dirty tab (see TabCloseTests.cs's TabCloseDirtyConfirmationTests, which
// exhaustively cover that routing via a direct Execute(null) call). QA4-20's only remaining gap is that
// nothing summoned CloseActiveTabCommand from the Ctrl+F4 gesture. This file mirrors StandardShortcutsTests.cs's
// proven two-layer pattern:
//   - Structural: the real, XAML-loaded MainWindow.KeyBindings collection carries a Ctrl+F4 entry mapped to
//     CloseActiveTabCommand.
//   - Behavioral: the gesture actually fires — including through the save prompt — when focus is OFF the
//     active tab's editor (the point of a Window-level KeyBinding at all).
//
// Class name matches the verify filter "FullyQualifiedName~CtrlF4CloseTab".

using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class CtrlF4CloseTabTests
{
    // ---- Structural: the real, XAML-loaded MainWindow.KeyBindings collection. ----

    [AvaloniaFact]
    public void WindowLevelCtrlF4KeyBindingExistsAndMapsToCloseActiveTabCommand()
    {
        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            // Ctrl+W already binds to the same CloseActiveTabCommand instance, so match by GESTURE (not by
            // command reference, which would ambiguously match both entries) and assert the resolved gesture's
            // command is the right one.
            KeyBinding binding = Assert.Single(window.KeyBindings,
                kb => kb.Gesture.Key == Key.F4 && kb.Gesture.KeyModifiers == KeyModifiers.Control);
            Assert.Same(shell.CloseActiveTabCommand, binding.Command);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- Behavioral, focus OFF the editor: proves the gesture reaches CloseActiveTabCommand regardless of
    //      where focus is in the window (mirrors StandardShortcutsTests' focus-off-editor proofs). ----

    [AvaloniaFact]
    public async Task CtrlF4ClosesTheActiveCleanTabWhenFocusIsOnANonEditorControl()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            Button nonEditorControl = window.GetVisualDescendants().OfType<Button>().First();
            nonEditorControl.Focus();
            window.UpdateLayout();

            window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.Control);
            await Task.Yield();

            Assert.DoesNotContain(a, shell.Tabs);
            Assert.Same(b, shell.ActiveTab);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- Behavioral: a dirty tab prompts to save FIRST via ConfirmCloseTab before Ctrl+F4 closes it — the
    //      same delegate BUG-3/Ctrl+W already wires to a real modal ShowDialog(owner) in MainWindow.axaml.cs. ----

    [AvaloniaFact]
    public async Task CtrlF4OnADirtyTabInvokesConfirmCloseTabBeforeClosing()
    {
        var shell = new MainWindowViewModel();
        DocumentTab dirty = MakeDirtyTab("dirty");
        shell.Tabs.Add(dirty);
        shell.ActivateTab(dirty);

        // Attach the window FIRST — MainWindow.axaml.cs's OnDataContextChanged wires its own real modal
        // ConfirmCloseTab delegate as soon as DataContext is assigned, so the test delegate below must be
        // set AFTER attaching the window or it gets silently overwritten by the real dialog wiring.
        var window = new MainWindow { DataContext = shell };
        window.Show();

        bool wasCalled = false;
        shell.ConfirmCloseTab = _ =>
        {
            wasCalled = true;
            return Task.FromResult(true);
        };
        try
        {
            Button nonEditorControl = window.GetVisualDescendants().OfType<Button>().First();
            nonEditorControl.Focus();
            window.UpdateLayout();

            window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.Control);
            await Task.Yield();

            Assert.True(wasCalled);
            Assert.DoesNotContain(dirty, shell.Tabs);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlF4OnADirtyTabCancelsWhenConfirmReturnsFalse()
    {
        var shell = new MainWindowViewModel();
        DocumentTab dirty = MakeDirtyTab("dirty");
        shell.Tabs.Add(dirty);
        shell.ActivateTab(dirty);

        // See the ordering note in CtrlF4OnADirtyTabInvokesConfirmCloseTabBeforeClosing above — the test
        // delegate must be assigned AFTER the window attaches, not before.
        var window = new MainWindow { DataContext = shell };
        window.Show();

        shell.ConfirmCloseTab = _ => Task.FromResult(false); // user cancels the save prompt.
        try
        {
            Button nonEditorControl = window.GetVisualDescendants().OfType<Button>().First();
            nonEditorControl.Focus();
            window.UpdateLayout();

            window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.Control);
            await Task.Yield();

            Assert.Contains(dirty, shell.Tabs); // not closed.
            Assert.Same(dirty, shell.ActiveTab);
        }
        finally
        {
            window.Close();
        }
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);

    private static DocumentTab MakeDirtyTab(string id)
    {
        DocumentTab tab = TabFactory.New(id);
        if (tab.Editor is { } editor)
            editor.IsDirty = true;
        return tab;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared factory — mirrors TabFactory in TabCloseTests.cs / TabReorderTests.cs (file-scoped, so it can't
// be reused directly across files).
// ─────────────────────────────────────────────────────────────────────────────

file static class TabFactory
{
    public static DocumentTab New(string id) =>
        DocumentTab.ForEditor(id, TabKind.Scratch, MakeEditorVm());

    private static EditorViewModel MakeEditorVm()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new CountingAtomicFileStore(),
            new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }
}
