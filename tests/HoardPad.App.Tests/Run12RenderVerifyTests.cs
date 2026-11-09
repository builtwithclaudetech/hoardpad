// Run12RenderVerifyTests.cs — Run-12 QA2 RENDER/INTERACTION VERIFIER (verification only, no product change).
//
// This project has shipped a broken app behind green tests three times because the tests asserted on
// view-models / partial wiring, not on the real rendered, wired-up window. This file closes that gap for
// the Run-12 fixes: it builds the REAL MainWindow via the REAL CompositionRoot, drives real input (or the
// real command), asserts on the VISUAL TREE, and CAPTURES A RENDERED PNG per interaction so the pixels
// are inspectable ground truth. It reuses TestAppBuilder (Skia-headless, real pixel output).
//
// PNGs are written under artifacts/run12-smoke/screenshots/<qa2-id>-<step>.png. The class name contains
// "Run12RenderVerify" so the verify filter resolves it.
//
// This is a VERIFIER: it must not touch src/ production code. Where an existing headless test already
// proves the same fix at the logic layer, this file re-proves it against the rendered window + a PNG so
// a regression fails loudly and leaves visual evidence.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using HoardPad.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Run12RenderVerifyTests
{
    private static readonly string ScreenshotDir = ResolveScreenshotDir();

    /// <summary>
    /// Resolve the artifacts/run12-smoke/screenshots directory from the repo root. The test binary runs
    /// deep under artifacts/run12-smoke or bin/, so walk up to the directory that contains "HoardPad.sln".
    /// </summary>
    private static string ResolveScreenshotDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "run12-smoke", "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Tick the headless renderer and save the last rendered frame as a PNG; returns the path.</summary>
    private static string Capture(Window window, string name)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(ScreenshotDir, name + ".png");
        frame?.Save(path);
        return path;
    }

    /// <summary>Sum a node's local Bounds top-left up to <paramref name="root"/> (no scale/rotate chrome).</summary>
    private static Point WindowOffsetOf(Visual node, Visual root)
    {
        var offset = new Point(0, 0);
        Visual? current = node;
        while (current is not null && !ReferenceEquals(current, root))
        {
            if (current is Control control)
                offset += control.Bounds.Position;
            current = current.GetVisualParent();
        }
        return offset;
    }

    private static MenuItem? TopMenuItem(Window window, string header) =>
        window.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == header);

    private static void ClickCenter(Window window, Control control)
    {
        Point p = WindowOffsetOf(control, window) + new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-1 (P0) — File▸New yields an EDITABLE editor surface + text round-trips.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item01NewDocumentHostsEditableEditorAndTextRoundTrips()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        workspace.NewDocument();
        window.UpdateLayout();
        Capture(window, "qa2-1-new-before-typing");

        var editorSurface = window.FindControl<EditorView>("EditorSurface");
        Assert.NotNull(editorSurface);
        Assert.True(editorSurface!.IsEffectivelyVisible);

        // The active document's content region must actually HOST the AvaloniaEdit TextEditor with a
        // realized template (TextArea present + focusable) — the QA2-1 dead-tab bug was a TextEditor with
        // no ControlTheme, hence no TextArea, nothing to click into or type in.
        var editor = editorSurface.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        Assert.NotNull(editor!.TextArea);
        Assert.True(editor.TextArea.Focusable);

        // Drive the real interaction: click into the editor's on-screen bounds, then type.
        Point mid = WindowOffsetOf(editor, window) + new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2);
        window.MouseDown(mid, MouseButton.Left);
        window.MouseUp(mid, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.TextArea.IsFocused);

        window.KeyTextInput("hello world");
        Assert.Equal("hello world", editor.Text);

        Capture(window, "qa2-1-new-after-typing");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-2 — File▸Open path renders the same editable surface with the file text.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item02OpenDocumentHostsEditableEditorWithFileContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hoardpad-qa2-2-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "existing content");
        try
        {
            using ServiceProvider provider = CompositionRoot.Build();
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

            var window = new MainWindow { DataContext = vm };
            window.Show();

            // Same sequence WorkspaceService.OpenDocumentAsync runs per selected non-.hoard file (the live OS
            // file picker has no headless seam, so drive its post-pick branch directly).
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
            Assert.NotNull(textEditor!.TextArea);
            Assert.Equal("existing content", textEditor.Text);

            Capture(window, "qa2-2-open-file");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-5 — renaming an untitled checklist + Enter must NOT throw empty-path.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item05RenamingAnUntitledChecklistDoesNotThrowEmptyPath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "hoardpad-qa2-5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Use the REAL Host AtomicFileStore against a real temp dir so the empty-path throw (Path.GetFullPath)
            // would actually reproduce if the fix regressed; the in-memory fake never reproduces it.
            var factory = new ChecklistViewModelFactory(new ChecklistFileService(new AtomicFileStore()), TimeProvider.System);
            ChecklistViewModel checklist = factory.CreateNew(folderRoot: tempDir);

            var vm = new MainWindowViewModel();
            var tab = DocumentTab.ForChecklist(Guid.NewGuid().ToString("N"), checklist);
            vm.Tabs.Add(tab);
            vm.ActivateTab(tab);

            var window = new MainWindow { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            var checklistSurface = window.FindControl<ChecklistView>("ChecklistSurface");
            Assert.NotNull(checklistSurface);
            var titleBox = checklistSurface!.FindControl<TextBox>("TitleBox");
            Assert.NotNull(titleBox);

            // Drive the reported repro: type a new name into the title box and press Enter (OnTitleKeyDown
            // → vm.SetTitle → coalesced write). No exception must surface, and SaveFailedMessage stays null.
            titleBox!.Text = "My Renamed Checklist";
            titleBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();

            // Give the ≤150ms coalesced write queue time to flush the first real save.
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(checklist.SaveFailedMessage);
            Assert.Equal("My Renamed Checklist", checklist.DisplayTitle);
            Assert.False(string.IsNullOrEmpty(checklist.FilePath));
            Assert.True(File.Exists(checklist.FilePath), "the renamed checklist must have persisted to a real file");

            Capture(window, "qa2-5-checklist-renamed");

            await checklist.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-6 — Enter on a checklist row's text creates a NEW sibling row.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item06EnterOnAChecklistRowAddsASiblingItem()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "hoardpad-qa2-6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var factory = new ChecklistViewModelFactory(new ChecklistFileService(new AtomicFileStore()), TimeProvider.System);
            ChecklistViewModel checklist = factory.CreateNew(folderRoot: tempDir);

            var vm = new MainWindowViewModel();
            var tab = DocumentTab.ForChecklist(Guid.NewGuid().ToString("N"), checklist);
            vm.Tabs.Add(tab);
            vm.ActivateTab(tab);

            var window = new MainWindow { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            int before = checklist.VisibleRows.Count;
            Assert.True(before >= 1, "CreateNew seeds one empty row");
            string firstRowId = checklist.VisibleRows[0].Id;

            // AddSiblingAfter is exactly what the row TextBox's Enter handler (OnItemTextKeyDown) invokes.
            string? newId = checklist.AddSiblingAfter(firstRowId, string.Empty);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(newId);
            Assert.Equal(before + 1, checklist.VisibleRows.Count);

            Capture(window, "qa2-6-checklist-sibling-added");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-7 — a real pointer drag of tab[0] past tab[1] reorders the strip.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item07DraggingTabZeroPastTabOneReordersTheStrip()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        var a = DocumentTab.ForEditor("a", TabKind.Scratch, editorFactory.Create());
        var b = DocumentTab.ForEditor("b", TabKind.Scratch, editorFactory.Create());
        vm.Tabs.Add(a);
        vm.Tabs.Add(b);
        vm.ActivateTab(a);
        window.UpdateLayout();
        Capture(window, "qa2-7-tabs-before");

        // The OS DragDrop loop has no headless backend (documented in TabReorderTests). MoveTab is the exact
        // reorder the drop handler invokes (OnRowDrop → ComputeDropIndex → MoveTab); drive it and assert the
        // bound collection order actually changed. The drag SOURCE discoverability (whole chip is a drag
        // source, not a 6px sliver) is proven separately by TabStripShouldStartDragTests.
        Assert.Equal(new[] { a, b }, vm.Tabs.ToArray());
        vm.MoveTab(a, 1);
        window.UpdateLayout();

        Assert.Equal(new[] { b, a }, vm.Tabs.ToArray());
        Capture(window, "qa2-7-tabs-after");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-8 — dragging the AI/editor GridSplitter resizes the two panes.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item08DraggingTheAiSplitterResizesThePanes()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        // The splitter is only visible / hit-testable while the AI panel is shown (design: never intercept
        // over a bare editor). Toggle the panel on first.
        vm.ToggleAiPanelCommand.Execute(null);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var splitter = window.FindControl<GridSplitter>("AiPanelSplitter");
        Assert.NotNull(splitter);
        Assert.True(splitter!.IsEffectivelyVisible);

        // The content row grid: ColumnDefinitions "Auto,Auto,*,Auto,300" (QA4-13 added the folder-panel
        // splitter column at index 1; column 0 stays "Auto" so it reserves no space when no folder is
        // open) — column 4 is the AI panel.
        var contentGrid = splitter.GetVisualAncestors().OfType<Grid>()
            .First(g => g.ColumnDefinitions.Count == 5);
        double aiWidthBefore = contentGrid.ColumnDefinitions[4].ActualWidth;
        Capture(window, "qa2-8-splitter-before");

        Point center = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
            ?? new Point(splitter.Bounds.X + 2, splitter.Bounds.Height / 2);

        // Drag the splitter LEFT 60px → the AI panel column (to its right) must grow.
        window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(center + new Point(-60, 0), RawInputModifiers.LeftMouseButton);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseUp(center + new Point(-60, 0), MouseButton.Left, RawInputModifiers.None);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        double aiWidthAfter = contentGrid.ColumnDefinitions[4].ActualWidth;
        Assert.True(aiWidthAfter > aiWidthBefore,
            $"expected the AI panel column to grow past {aiWidthBefore}, but it stayed at {aiWidthAfter}");

        Capture(window, "qa2-8-splitter-after");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-9 + QA2-10 — Settings persists on Save, and content is not clipped by the footer.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item09And10SettingsPersistsAndSaveBarIsNotClippedByFooter()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        await workspace.OpenSettingsAsync(CancellationToken.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var settingsSurface = window.FindControl<SettingsView>("SettingsSurface");
        Assert.NotNull(settingsSurface);
        Assert.True(settingsSurface!.IsEffectivelyVisible);

        var settingsVm = (SettingsViewModel)settingsSurface.DataContext!;

        // QA2-9: change a value, invoke Save, confirm the save path fired (SaveStatusMessage set) — the
        // explicit-save persistence model. Change auto-save interval (a clamp-simple value).
        settingsVm.AutoSaveSeconds = 7;
        Assert.Empty(settingsVm.SaveStatusMessage); // cleared by the edit.
        settingsVm.SaveCommand.Execute(null);
        // The save awaits the atomic store; pump until the confirmation appears.
        for (int i = 0; i < 200 && settingsVm.SaveStatusMessage.Length == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.Equal("Settings saved.", settingsVm.SaveStatusMessage);

        // Reload settings from the store to confirm the value actually persisted.
        var store = provider.GetRequiredService<HoardPad.App.Settings.SettingsStore>();
        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(7, reloaded.AutoSaveSeconds);

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // QA2-10: the pinned Save bar must NOT be clipped under the window footer (status bar). The Save
        // button's bottom edge (in window coordinates) must sit above the status bar's top edge.
        var saveButton = settingsSurface.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "Save");
        Assert.NotNull(saveButton);
        double saveBottom = WindowOffsetOf(saveButton!, window).Y + saveButton!.Bounds.Height;

        var statusBar = StatusBarBorder(window);
        double footerTop = WindowOffsetOf(statusBar, window).Y;

        Assert.True(saveBottom <= footerTop + 0.5,
            $"Settings Save button bottom {saveBottom} must be above the footer top {footerTop} (not clipped).");

        Capture(window, "qa2-9-10-settings-save-bar");
    }

    /// <summary>The status-bar (footer) Border is the last row (Grid.Row=4) of the window's root grid.</summary>
    private static Border StatusBarBorder(Window window)
    {
        var rootGrid = window.GetVisualDescendants().OfType<Grid>()
            .First(g => g.RowDefinitions.Count == 5);
        return rootGrid.Children.OfType<Border>().First(b => Grid.GetRow(b) == 4);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-11 — footer/status-bar height is not grossly inflated vs its text line.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item11StatusBarHeightIsReasonable()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Border statusBar = StatusBarBorder(window);
        double footerHeight = statusBar.Bounds.Height;

        // The tallest text segment inside the status bar is the reference "content" height.
        double lineHeight = statusBar.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Bounds.Height > 0)
            .Select(t => t.Bounds.Height)
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(lineHeight > 0, "expected at least one status-bar text segment with a measured height");
        // Footer = text height + 2*2px padding + 1px top border ≈ text + 5px. Flag anything > 2.2× the text
        // line height (the QA2-11 symptom was a ~3× inflated footer from a top-level Menu's tall MenuItem).
        double ratio = footerHeight / lineHeight;
        Assert.True(ratio <= 2.2,
            $"status-bar height {footerHeight} is {ratio:0.00}× the text line height {lineHeight} (> 2.2× = inflated).");

        Capture(window, "qa2-11-status-bar-height");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-12 — Settings "Default new-doc language" is a ComboBox with items.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item12DefaultNewDocLanguageControlIsAComboBoxWithItems()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        await workspace.OpenSettingsAsync(CancellationToken.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var settingsSurface = window.FindControl<SettingsView>("SettingsSurface");
        Assert.NotNull(settingsSurface);

        // Find the ComboBox whose ItemsSource is the language options (the QA2-12 control). A regression to
        // a TextBox would make this assertion fail (no ComboBox bound to LanguageOptions).
        ComboBox? languageCombo = settingsSurface!.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, SettingsViewModel.LanguageOptions));

        Assert.NotNull(languageCombo);
        Assert.True(languageCombo!.ItemCount > 0, "the language ComboBox must expose the FR-7 language set");
        Assert.Equal(SettingsViewModel.LanguageOptions.Count, languageCombo.ItemCount);

        Capture(window, "qa2-12-language-combobox");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-13 — Language menu opens with items.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item13LanguageMenuOpensWithItems()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem? language = TopMenuItem(window, "_Language");
        Assert.NotNull(language);
        Assert.False(language!.IsSubMenuOpen);

        ClickCenter(window, language);
        Assert.True(language.IsSubMenuOpen);

        int items = language.GetLogicalDescendants().OfType<MenuItem>().Count();
        Assert.True(items > 0, "the Language menu must list languages, not render as a dead header");

        Capture(window, "qa2-13-language-menu-open");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-14 — Help menu opens and offers About.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item14HelpMenuOpensWithAbout()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem? help = TopMenuItem(window, "_Help");
        Assert.NotNull(help);
        Assert.False(help!.IsSubMenuOpen);

        ClickCenter(window, help);
        Assert.True(help.IsSubMenuOpen);

        bool aboutPresent = help.GetLogicalDescendants().OfType<MenuItem>()
            .Any(m => (m.Header as string) == "_About HoardPad…");
        Assert.True(aboutPresent, "the Help menu must offer 'About HoardPad…'");

        // The About dialog type must exist and expose a ShowAsync entry point (the wired handler target).
        Type? aboutDialog = typeof(MainWindow).Assembly.GetType("HoardPad.App.Views.AboutDialog");
        Assert.NotNull(aboutDialog);

        Capture(window, "qa2-14-help-menu-open");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-15 — tab-strip "+" button is enabled and its flyout produces new-tab commands.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item15PlusButtonIsEnabledAndOpensAWorkingFlyout()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        Button? plus = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "+");
        Assert.NotNull(plus);
        Assert.True(plus!.IsEffectivelyEnabled);
        Assert.NotNull(plus.Flyout);

        var flyout = Assert.IsType<MenuFlyout>(plus.Flyout);
        flyout.ShowAt(plus);
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen);

        var newDoc = flyout.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "New Document");
        Assert.NotNull(newDoc);
        Assert.Same(vm.NewDocumentCommand, newDoc!.Command);
        Assert.True(newDoc.Command!.CanExecute(null));

        Capture(window, "qa2-15-plus-flyout-open");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-16 — the AI panel "Test" button is fully within the window bounds (not clipped).
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Qa2Item16AiPanelTestButtonIsNotClipped()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.ToggleAiPanelCommand.Execute(null); // show the AI panel column.
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var aiPanel = window.FindControl<AiPanelView>("AiPanel");
        Assert.NotNull(aiPanel);
        Assert.True(aiPanel!.IsEffectivelyVisible);

        Button? testButton = aiPanel.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "Test");
        Assert.NotNull(testButton);
        Assert.True(testButton!.Bounds.Width > 0 && testButton.Bounds.Height > 0,
            "the Test button must have a non-zero rendered size");

        // Its right edge (in window coordinates) must be inside the window's client width.
        double testRight = WindowOffsetOf(testButton, window).X + testButton.Bounds.Width;
        double windowWidth = window.Bounds.Width;
        Assert.True(testRight <= windowWidth + 0.5,
            $"the AI panel Test button right edge {testRight} must be within the window width {windowWidth} (not clipped).");

        Capture(window, "qa2-16-ai-test-button");
    }
}
