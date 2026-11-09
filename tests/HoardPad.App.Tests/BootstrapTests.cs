// BootstrapTests.cs — the M6.1 Skia-headless app-bootstrap test (architecture §1.6).
//
// Runs on the Skia-headless UI thread ([AvaloniaFact] via TestAppBuilder) and proves the shell
// actually bootstraps: the composition root resolves the shell view-model, the MainWindow builds
// and shows headless with its brand chrome, and the VM<->XAML bindings are live (the window Title
// reflects the VM, including the FR-5 dirty marker). The verify filter matches "Bootstrap".
// Method names are PascalCase to satisfy CA1707 under TreatWarningsAsErrors (house convention).

using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaEdit;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class BootstrapTests
{
    [AvaloniaFact]
    public void MainWindowShowsWithTheResolvedShellViewModel()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Same(vm, window.DataContext);
        Assert.NotNull(window.Icon);
    }

    [AvaloniaFact]
    public void WindowTitleBindsTheViewModelIncludingTheDirtyMarker()
    {
        var vm = new MainWindowViewModel { DocumentName = "notes.md" };
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Equal("HoardPad — notes.md", window.Title);

        vm.IsDirty = true;
        Assert.Equal("HoardPad — notes.md •", window.Title);
    }

    [AvaloniaFact]
    public void ViewToggleCommandsFlipTheRegionVisibilityFlags()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.False(vm.IsAiPanelVisible);
        vm.ToggleAiPanelCommand.Execute(null);
        Assert.True(vm.IsAiPanelVisible);

        Assert.False(vm.IsFolderTreeVisible);
        vm.ToggleFolderTreeCommand.Execute(null);
        Assert.True(vm.IsFolderTreeVisible);
    }

    // ----- Toolbar command bindings (BUG-7): New / Open / Save / Find -----

    [Fact]
    public void ToolbarNewDocumentCommandIsNotNull()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.NewDocumentCommand);
        Assert.True(vm.NewDocumentCommand.CanExecute(null));
    }

    [Fact]
    public void ToolbarOpenDocumentCommandIsNotNull()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.OpenDocumentCommand);
        Assert.True(vm.OpenDocumentCommand.CanExecute(null));
    }

    [Fact]
    public void ToolbarSaveDocumentCommandIsNotNull()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.SaveDocumentCommand);
        Assert.True(vm.SaveDocumentCommand.CanExecute(null));
    }

    [Fact]
    public void ToolbarShowFindCommandIsNotNull()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.ShowFindCommand);
        Assert.True(vm.ShowFindCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToolbarNewOpenSaveCommandsAreNoOpWithoutHandler()
    {
        // Without a handler set, these commands must execute without throwing.
        var vm = new MainWindowViewModel();
        Assert.Null(vm.NewDocumentHandler);
        Assert.Null(vm.OpenDocumentHandler);
        Assert.Null(vm.SaveDocumentHandler);

        await vm.NewDocumentCommand.ExecuteAsync(null);
        await vm.OpenDocumentCommand.ExecuteAsync(null);
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        // No assertion needed — the test passes if no exception was thrown.
    }

    [Fact]
    public async Task ToolbarNewOpenSaveCommandsInvokeTheHandlerWhenSet()
    {
        bool newCalled = false;
        bool openCalled = false;
        bool saveCalled = false;

        var vm = new MainWindowViewModel
        {
            NewDocumentHandler = () => { newCalled = true; return Task.CompletedTask; },
            OpenDocumentHandler = () => { openCalled = true; return Task.CompletedTask; },
            SaveDocumentHandler = () => { saveCalled = true; return Task.CompletedTask; },
        };

        await vm.NewDocumentCommand.ExecuteAsync(null);
        Assert.True(newCalled);

        await vm.OpenDocumentCommand.ExecuteAsync(null);
        Assert.True(openCalled);

        await vm.SaveDocumentCommand.ExecuteAsync(null);
        Assert.True(saveCalled);
    }

    // ----- BUG-10: New Checklist command -----

    [Fact]
    public void NewChecklistCommandIsNotNull()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.NewChecklistCommand);
        Assert.True(vm.NewChecklistCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewChecklistCommandIsNoOpWithoutHandler()
    {
        var vm = new MainWindowViewModel();
        Assert.Null(vm.NewChecklistHandler);
        await vm.NewChecklistCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task NewChecklistCommandInvokesHandlerWhenSet()
    {
        bool called = false;
        var vm = new MainWindowViewModel
        {
            NewChecklistHandler = () => { called = true; return Task.CompletedTask; },
        };
        await vm.NewChecklistCommand.ExecuteAsync(null);
        Assert.True(called);
    }

    // ----- File menu command bindings (BUG-8): Close / Exit -----

    [Fact]
    public void FileCloseActiveTabCommandIsNotNull()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.CloseActiveTabCommand);
        Assert.True(vm.CloseActiveTabCommand.CanExecute(null));
    }

    [Fact]
    public void FileExitApplicationCommandIsNotNull()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.ExitApplicationCommand);
        Assert.True(vm.ExitApplicationCommand.CanExecute(null));
    }

    [Fact]
    public void FileCloseActiveTabIsNoOpWhenNoActiveTab()
    {
        // Must not throw when no tab is active.
        var vm = new MainWindowViewModel();
        vm.CloseActiveTabCommand.Execute(null);
    }

    [Fact]
    public async Task FileExitApplicationIsNoOpWithoutHandler()
    {
        var vm = new MainWindowViewModel();
        Assert.Null(vm.ExitHandler);
        await vm.ExitApplicationCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task FileExitApplicationInvokesTheHandlerWhenSet()
    {
        bool exitCalled = false;
        var vm = new MainWindowViewModel
        {
            ExitHandler = () => { exitCalled = true; return Task.CompletedTask; },
        };

        await vm.ExitApplicationCommand.ExecuteAsync(null);
        Assert.True(exitCalled);
    }

    [Fact]
    public void CloseActiveTabRemovesTheTabAndActivatesNeighbor()
    {
        using var provider = CompositionRoot.Build();
        var factory = provider.GetRequiredService<EditorViewModelFactory>();

        var vm = new MainWindowViewModel();
        var editor1 = factory.Create();
        var editor2 = factory.Create();
        var tab1 = DocumentTab.ForEditor("id1", TabKind.Scratch, editor1);
        var tab2 = DocumentTab.ForEditor("id2", TabKind.Scratch, editor2);
        vm.Tabs.Add(tab1);
        vm.Tabs.Add(tab2);
        vm.ActivateTab(tab1);

        vm.CloseActiveTabCommand.Execute(null);

        Assert.Single(vm.Tabs);
        Assert.Same(tab2, vm.ActiveTab);
    }

    [Fact]
    public void CloseActiveTabOfLastTabLeavesNoActiveTab()
    {
        using var provider = CompositionRoot.Build();
        var factory = provider.GetRequiredService<EditorViewModelFactory>();

        var vm = new MainWindowViewModel();
        var tab = DocumentTab.ForEditor("id1", TabKind.Scratch, factory.Create());
        vm.Tabs.Add(tab);
        vm.ActivateTab(tab);

        vm.CloseActiveTabCommand.Execute(null);

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.ActiveTab);
    }

    // ----- QA2-1/QA2-2: File▸New / File▸Open must render an EDITABLE editor surface, not a dead tab. -----
    //
    // Root cause (debug-investigator, 2026-06-30): App.axaml's <Application.Styles> included only
    // <FluentTheme/> — AvaloniaEdit's TextEditor ControlTheme (avares://AvaloniaEdit/Themes/Fluent/
    // AvaloniaEdit.xaml) was never merged in, so the TextEditor control had NO template applied: no
    // TextArea in the visual tree, no focus adorner, nothing to click into or type in. Fixed by adding
    // the StyleInclude. This test proves the REAL interaction end-to-end (real MainWindow + real
    // CompositionRoot + a simulated mouse click + simulated keystrokes) so a regression here fails loudly
    // instead of silently reverting to a dead tab (the M8.1 drag-reorder lesson: a green headless test
    // that doesn't simulate the actual pointer/keyboard interaction can pass while the real UI is inert).

    [AvaloniaFact]
    public void NewDocumentRendersAnEditableEditorSurfaceEndToEnd()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        workspace.NewDocument();
        window.UpdateLayout();

        var editorSurface = window.FindControl<EditorView>("EditorSurface");
        Assert.NotNull(editorSurface);
        Assert.True(editorSurface!.IsEffectivelyVisible);

        var editor = editorSurface.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);

        // The template must actually be applied — TextArea is the templated child AvaloniaEdit's own
        // ControlTheme installs; its absence (or Focusable=false with no template) is exactly the QA2-1 bug.
        Assert.NotNull(editor!.TextArea);
        Assert.True(editor.TextArea.Focusable);

        // Simulate the real live smoke test: click inside the editor's ACTUAL on-screen bounds, then type —
        // the same sequence the maintainer performs by hand. Resolve the click point from the editor's real window
        // position (menu + toolbar + tab-strip chrome sits above it) rather than a hardcoded window-relative
        // guess, so this stays correct if chrome heights change; a coordinate guess landing on the tab strip
        // / toolbar instead of the editor was the exact failure mode this test's own first draft hit.
        Avalonia.Point editorMidpoint = WindowOffsetOf(editor, window)
            + new Avalonia.Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2);

        window.MouseDown(editorMidpoint, Avalonia.Input.MouseButton.Left);
        window.MouseUp(editorMidpoint, Avalonia.Input.MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(editor.TextArea.IsFocused);

        window.KeyTextInput("hello");
        Assert.Equal("hello", editor.Text);
    }

    /// <summary>
    /// Accumulate <paramref name="node"/>'s position up to <paramref name="root"/> by summing each ancestor's
    /// local <c>Bounds</c> top-left (no scale/rotate transforms in this chrome, so plain addition is exact).
    /// Used only to resolve a real click point for the headless mouse simulation above.
    /// </summary>
    private static Avalonia.Point WindowOffsetOf(Avalonia.Visual node, Avalonia.Visual root)
    {
        var offset = new Avalonia.Point(0, 0);
        Avalonia.Visual? current = node;
        while (current is not null && !ReferenceEquals(current, root))
        {
            if (current is Control control)
                offset += control.Bounds.Position;
            current = current.GetVisualParent();
        }
        return offset;
    }

    [AvaloniaFact]
    public void CommandedToolbarButtonsAreEffectivelyEnabledAfterShow()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        var newButton = FindButtonByContent(window, "New");
        Assert.NotNull(newButton);
        Assert.True(newButton!.IsEffectivelyEnabled);

        var openButton = FindButtonByContent(window, "Open");
        Assert.NotNull(openButton);
        Assert.True(openButton!.IsEffectivelyEnabled);
    }

    private static Button? FindButtonByContent(Avalonia.Visual root, string content)
    {
        foreach (var descendant in root.GetVisualDescendants())
        {
            if (descendant is Button { Content: string text } button
                && text == content)
                return button;
        }
        return null;
    }

    // ----- QA2-15: the tab-strip "+" button must be enabled AND its popup menu must actually open
    // and expose working commands, not just render as a Button with no Command (the original defect —
    // a normal click did nothing because the Button had no Command, and there was no MenuFlyout at all
    // despite the maintainer observing a popup "when clicked just right", which was the OS default press visual). -----
    [AvaloniaFact]
    public void TabStripPlusButtonIsEnabledAndItsFlyoutMenuOpensWithWorkingCommands()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        var plusButton = FindButtonByContent(window, "+");
        Assert.NotNull(plusButton);
        Assert.True(plusButton!.IsEffectivelyEnabled);

        Assert.NotNull(plusButton.Flyout);
        var menuFlyout = Assert.IsType<MenuFlyout>(plusButton.Flyout);
        menuFlyout.ShowAt(plusButton);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(menuFlyout.IsOpen);

        var newDocumentItem = menuFlyout.Items
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "New Document");
        var newChecklistItem = menuFlyout.Items
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "New Checklist");

        Assert.NotNull(newDocumentItem);
        Assert.NotNull(newChecklistItem);
        Assert.Same(vm.NewDocumentCommand, newDocumentItem!.Command);
        Assert.Same(vm.NewChecklistCommand, newChecklistItem!.Command);
        Assert.True(newDocumentItem.Command!.CanExecute(null));
        Assert.True(newChecklistItem.Command!.CanExecute(null));
    }

    // ----- QA2-13/QA2-14: the top menu bar must actually open its submenu on click. -----
    //
    // A bound Command on a MenuItem proves the click handler is wired, but says nothing about whether
    // the MENU ITSELF renders and opens — the same class of "green test, dead UI" gap the QA2-1 dead-tab
    // bug exposed for the editor surface. This drives a real simulated click on the top-level "_File"
    // MenuItem and asserts its submenu actually opens (IsSubMenuOpen), then that its child items are
    // present in the visual tree once open.
    [AvaloniaFact]
    public void FileMenuOpensItsSubmenuOnClick()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem? fileMenuItem = window
            .GetVisualDescendants()
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_File");

        Assert.NotNull(fileMenuItem);
        Assert.False(fileMenuItem!.IsSubMenuOpen);

        Avalonia.Point clickPoint = WindowOffsetOf(fileMenuItem, window)
            + new Avalonia.Point(fileMenuItem.Bounds.Width / 2, fileMenuItem.Bounds.Height / 2);

        window.MouseDown(clickPoint, Avalonia.Input.MouseButton.Left);
        window.MouseUp(clickPoint, Avalonia.Input.MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(fileMenuItem.IsSubMenuOpen);

        // The submenu itself renders into a Popup (a separate visual root), so its items are found via
        // the MenuItem's logical children rather than GetVisualDescendants() from the parent MenuItem.
        bool newItemVisible = fileMenuItem
            .GetLogicalDescendants()
            .OfType<MenuItem>()
            .Any(m => (m.Header as string) == "_New");
        Assert.True(newItemVisible);
    }

    // ----- QA2-2: File▸Open must render the SAME editable editor surface as File▸New. -----
    //
    // Same root cause as QA2-1 (App.axaml missing the AvaloniaEdit ControlTheme StyleInclude) and the
    // same fix — the New and Open paths both end at DocumentTab.ForEditor + ActivateTab, which repoints
    // the ONE shared EditorSurface/TextEditor's DataContext (MainWindow.axaml EditorSurface binds
    // DataContext="{Binding Editor}"). This test drives the Open code path directly — EditorViewModelFactory
    // .Create() + EditorViewModel.OpenAsync(path) + DocumentTab.ForEditor + ActivateTab, exactly what
    // WorkspaceService.OpenDocumentAsync does per selected file — because the live OS file picker
    // (IStorageProvider.OpenFilePickerAsync) has no test seam to drive headlessly. It then proves the
    // interaction end-to-end like the QA2-1 test: real click + real keystrokes into the opened document.
    [AvaloniaFact]
    public async Task OpenDocumentRendersAnEditableEditorSurfaceEndToEnd()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hoardpad-qa2-2-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(path, "existing content");
        try
        {
            using ServiceProvider provider = CompositionRoot.Build();
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

            var window = new MainWindow { DataContext = vm };
            window.Show();

            // Same sequence as WorkspaceService.OpenDocumentAsync's per-file branch for a non-.hoard path.
            EditorViewModel editor = editorFactory.Create();
            await editor.OpenAsync(path, CancellationToken.None);
            var tab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.File, editor);
            vm.Tabs.Add(tab);
            vm.ActivateTab(tab);
            window.UpdateLayout();

            var editorSurface = window.FindControl<EditorView>("EditorSurface");
            Assert.NotNull(editorSurface);
            Assert.True(editorSurface!.IsEffectivelyVisible);

            var textEditor = editorSurface.FindControl<TextEditor>("Editor");
            Assert.NotNull(textEditor);

            // The template must actually be applied — TextArea is the templated child AvaloniaEdit's own
            // ControlTheme installs; its absence is exactly the QA2-1/QA2-2 dead-tab bug.
            Assert.NotNull(textEditor!.TextArea);
            Assert.True(textEditor.TextArea.Focusable);
            Assert.Equal("existing content", textEditor.Text);

            Avalonia.Point editorMidpoint = WindowOffsetOf(textEditor, window)
                + new Avalonia.Point(textEditor.Bounds.Width / 2, textEditor.Bounds.Height / 2);

            window.MouseDown(editorMidpoint, Avalonia.Input.MouseButton.Left);
            window.MouseUp(editorMidpoint, Avalonia.Input.MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(textEditor.TextArea.IsFocused);

            window.KeyTextInput(" more");
            Assert.Equal("existing content more", textEditor.Text);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ----- QA2-13: the top-level "_Language" menu must actually list languages and open on click. -----
    //
    // Before this fix the MenuItem had no children (<MenuItem Header="_Language" />), so it rendered as
    // a dead, unopenable header — clicking it did nothing because there was nothing to open. This mirrors
    // FileMenuOpensItsSubmenuOnClick's real-click assertion, then additionally proves every one of the 20
    // FR-7 languages + "Plain Text" is present (LanguageCatalog.AvailableLanguages, the same canonical
    // source QA2-12's Settings dropdown reads).
    [AvaloniaFact]
    public void LanguageMenuOpensAndListsAllCatalogLanguages()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem? languageMenuItem = window
            .GetVisualDescendants()
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Language");

        Assert.NotNull(languageMenuItem);
        Assert.False(languageMenuItem!.IsSubMenuOpen);

        Avalonia.Point clickPoint = WindowOffsetOf(languageMenuItem, window)
            + new Avalonia.Point(languageMenuItem.Bounds.Width / 2, languageMenuItem.Bounds.Height / 2);

        window.MouseDown(clickPoint, Avalonia.Input.MouseButton.Left);
        window.MouseUp(clickPoint, Avalonia.Input.MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(languageMenuItem.IsSubMenuOpen);

        var itemHeaders = languageMenuItem
            .GetLogicalDescendants()
            .OfType<MenuItem>()
            .Select(m => m.Header as string)
            .ToList();

        foreach ((string label, string? _) in HoardPad.App.Editor.LanguageCatalog.AvailableLanguages)
            Assert.Contains(label, itemHeaders);
    }

    // ----- QA2-13: selecting a Language menu item must actually apply the grammar to the active document. -----
    //
    // A bound Command proves the click is wired, but not that the effect is real — the same "green test,
    // dead UI" gap QA2-1 exposed. This drives the real SetLanguageCommand end-to-end against a live editor
    // surface and asserts the resolved grammar scope + status-bar language label both change, and that
    // switching to "Plain Text" clears the scope again without touching the buffer text.
    [AvaloniaFact]
    public void SelectingALanguageMenuItemAppliesTheGrammarToTheActiveDocument()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        EditorViewModel editor = editorFactory.Create();
        editor.Document.Text = "const x = 1;";
        var tab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.File, editor);
        vm.Tabs.Add(tab);
        vm.ActivateTab(tab);
        window.UpdateLayout();

        Assert.NotNull(vm.SetLanguageCommand);
        vm.SetLanguageCommand!.Execute("JavaScript");

        Assert.Equal("source.js", editor.Document.GrammarScope);
        Assert.Equal("JavaScript", editor.Document.LanguageLabel);
        Assert.Equal("JavaScript", vm.LanguageMode);
        Assert.Equal("const x = 1;", editor.Document.Text);

        vm.SetLanguageCommand!.Execute("Plain Text");

        Assert.Null(editor.Document.GrammarScope);
        Assert.Equal("Plain Text", editor.Document.LanguageLabel);
        Assert.Equal("const x = 1;", editor.Document.Text);
    }

    // ----- QA2-14: the top-level "_Help" menu must actually open and offer About HoardPad. -----
    //
    // Before this fix the MenuItem had no children (<MenuItem Header="_Help" />) — the same dead-header
    // shape as the QA2-13 Language menu regression, so clicking it did nothing because there was nothing
    // to open. Mirrors FileMenuOpensItsSubmenuOnClick's real-click assertion, then proves the "About
    // HoardPad…" child item is present once open.
    [AvaloniaFact]
    public void HelpMenuOpensAndListsAboutHoardPad()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem? helpMenuItem = window
            .GetVisualDescendants()
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Help");

        Assert.NotNull(helpMenuItem);
        Assert.False(helpMenuItem!.IsSubMenuOpen);

        Avalonia.Point clickPoint = WindowOffsetOf(helpMenuItem, window)
            + new Avalonia.Point(helpMenuItem.Bounds.Width / 2, helpMenuItem.Bounds.Height / 2);

        window.MouseDown(clickPoint, Avalonia.Input.MouseButton.Left);
        window.MouseUp(clickPoint, Avalonia.Input.MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(helpMenuItem.IsSubMenuOpen);

        bool aboutItemVisible = helpMenuItem
            .GetLogicalDescendants()
            .OfType<MenuItem>()
            .Any(m => (m.Header as string) == "_About HoardPad…");
        Assert.True(aboutItemVisible);
    }

    // ----- QA2-14: selecting "About HoardPad…" must actually invoke the wired Help handler. -----
    //
    // The real handler shows a modal Window (AboutDialog.ShowAsync), which MainWindow.axaml.cs wires
    // from OnDataContextChanged using `this` as the owner — the same pattern as the BUG-3 close-confirm
    // prompt (ConfirmCloseTab), which likewise has no dedicated dialog-driving test. This proves the
    // command-to-handler wiring end-to-end (the part that was actually broken — an empty menu had no
    // command to wire at all) without needing a headless-popup driver for the dialog itself.
    [AvaloniaFact]
    public void ShowHelpCommandInvokesTheWiredHandler()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        bool invoked = false;
        vm.ShowHelpHandler = () =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        Assert.NotNull(vm.ShowHelpCommand);
        vm.ShowHelpCommand.Execute(null);

        Assert.True(invoked);
    }
}
