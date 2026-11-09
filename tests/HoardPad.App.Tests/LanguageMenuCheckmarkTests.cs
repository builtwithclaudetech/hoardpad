// LanguageMenuCheckmarkTests.cs — QA3-4 RENDER/INTERACTION VERIFIER.
//
// The Language submenu (MainWindow.axaml) applied the clicked language via SetLanguageCommand
// (QA2-13/BootstrapTests already proves that), but gave no indication of which language was CURRENTLY
// active — no checkmark, no highlight, nothing. LanguageMode was already live-bound (the status bar
// reads it, and it updates correctly on tab switch), so the fix is purely a menu-item binding
// (LanguageActiveConverter in Views/LanguageMenuConverters.cs). These tests drive the REAL MainWindow
// via the REAL CompositionRoot and assert on the REAL rendered MenuItem.IsChecked values — not merely
// the LanguageMode string — the same "green test, dead UI" gap this project has been burned by before
// (BootstrapTests QA2-13/QA2-14 comments, Run12RenderVerifyTests header).
//
// The class name contains "Language" so --filter "FullyQualifiedName~Language" resolves it.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class LanguageMenuCheckmarkTests
{
    /// <summary>Sum a node's local Bounds top-left up to <paramref name="root"/> (no scale/rotate chrome).</summary>
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

    /// <summary>Finds + opens the top-level "_Language" MenuItem (mirrors BootstrapTests' QA2-13 click).</summary>
    private static MenuItem OpenLanguageMenu(Window window)
    {
        MenuItem? language = window.GetVisualDescendants().OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Language");
        Assert.NotNull(language);

        Avalonia.Point clickPoint = WindowOffsetOf(language!, window)
            + new Avalonia.Point(language!.Bounds.Width / 2, language.Bounds.Height / 2);
        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(language.IsSubMenuOpen);
        return language;
    }

    /// <summary>The labels of every Language submenu item currently showing a checkmark.</summary>
    private static System.Collections.Generic.List<string> CheckedLabels(MenuItem languageMenu) =>
        languageMenu.GetLogicalDescendants().OfType<MenuItem>()
            .Where(m => m.IsChecked)
            .Select(m => m.Header as string)
            .Where(h => h is not null)
            .Select(h => h!)
            .ToList();

    private static DocumentTab AddAndActivateScratchTab(MainWindowViewModel vm, EditorViewModelFactory editorFactory, out EditorViewModel editor)
    {
        editor = editorFactory.Create();
        var tab = DocumentTab.ForEditor(System.Guid.NewGuid().ToString("N"), TabKind.Scratch, editor);
        vm.Tabs.Add(tab);
        vm.ActivateTab(tab);
        return tab;
    }

    [AvaloniaFact]
    public void TheLanguageMenuChecksTheActiveDocumentsLanguage()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        AddAndActivateScratchTab(vm, editorFactory, out _);
        vm.SetLanguageCommand!.Execute("Markdown");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        System.Collections.Generic.List<string> checkedLabels = CheckedLabels(languageMenu);

        Assert.Equal(["Markdown"], checkedLabels);
    }

    [AvaloniaFact]
    public void SwitchingTheActiveTabMovesTheLanguageMenuCheckmarkToTheNewActiveDocumentsLanguage()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        DocumentTab tabA = AddAndActivateScratchTab(vm, editorFactory, out _);
        vm.SetLanguageCommand!.Execute("Markdown"); // tabA's document is now Markdown while active.

        DocumentTab tabB = AddAndActivateScratchTab(vm, editorFactory, out _);
        vm.SetLanguageCommand!.Execute("Python"); // tabB's document is now Python while active.

        // Switch back to tabA (still Markdown) — the checkmark must follow the newly-active tab.
        vm.ActivateTab(tabA);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Markdown"], CheckedLabels(languageMenu));

        // Switch to tabB (Python) — the checkmark must move off Markdown and onto Python.
        vm.ActivateTab(tabB);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Python"], CheckedLabels(languageMenu));
    }

    [AvaloniaFact]
    public void APlainTextActiveDocumentChecksThePlainTextMenuItem()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        // A fresh scratch tab defaults to Plain Text (FR-8) — never touch SetLanguageCommand.
        AddAndActivateScratchTab(vm, editorFactory, out _);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Plain Text"], CheckedLabels(languageMenu));
    }

    // ---- QA3-4 Fix 1 regression guard: the File▸Open / restore auto-detect path (LanguageCatalog.Resolve)
    // must produce the SAME canonical label the Language menu checkmarks — not the old scope-derived
    // LabelFor spelling ("source.cs" -> "Cs", "text.html.markdown" -> "Html"). This must FAIL before Fix 1
    // (nothing would be checked — the auto-detected label wouldn't match any ConverterParameter) and pass
    // after. Drives the REAL EditorViewModel.OpenAsync path (the same one File▸Open / session-restore use),
    // not SetLanguageCommand. ----

    [AvaloniaTheory]
    [InlineData("sample.cs", "C#")]
    [InlineData("sample.md", "Markdown")]
    public async Task OpeningARealFileFromDiskChecksItsCanonicalLanguage(string fileName, string expectedLabel)
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        string tempPath = Path.Combine(Path.GetTempPath(), $"hoardpad-qa3-4-{Guid.NewGuid():N}-{fileName}");
        await File.WriteAllTextAsync(tempPath, "content");
        try
        {
            EditorViewModel editor = editorFactory.Create();
            await editor.OpenAsync(tempPath, CancellationToken.None);
            var tab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.File, editor);
            vm.Tabs.Add(tab);
            vm.ActivateTab(tab);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([expectedLabel], CheckedLabels(languageMenu));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ---- Quoted ConverterParameter regression guard: a few of the 21 menu items quote their
    // ConverterParameter in XAML ('Plain Text', 'C#', 'Shell/Bash', 'C++') because the raw text contains a
    // markup-extension-significant character (space, #, /, +). A quoting mistake would leave the parameter
    // carrying the literal quote characters, so it would never match LanguageMode and NOTHING would check. ----

    [AvaloniaTheory]
    [InlineData("Plain Text")]
    [InlineData("C#")]
    [InlineData("Shell/Bash")]
    [InlineData("C++")]
    public void SelectingAQuotedMenuLabelChecksExactlyThatItem(string label)
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        AddAndActivateScratchTab(vm, editorFactory, out _);
        vm.SetLanguageCommand!.Execute(label);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([label], CheckedLabels(languageMenu));
    }

    // ---- QA3-4 Fix 2 regression guard: a checklist/Settings tab has no editor language, so switching to
    // one from a language-set editor tab must leave NO Language menu item checked (a stale checkmark from
    // the previous editor tab would misleadingly claim a language is active for a non-editor document). ----

    [AvaloniaFact]
    public void ActivatingAChecklistTabAfterALanguagedEditorTabChecksNoLanguageItem()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        AddAndActivateScratchTab(vm, editorFactory, out _);
        vm.SetLanguageCommand!.Execute("Python");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Python"], CheckedLabels(languageMenu));

        workspace.NewChecklist();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(CheckedLabels(languageMenu));
    }

    [AvaloniaFact]
    public async Task ActivatingTheSettingsTabAfterALanguagedEditorTabChecksNoLanguageItem()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        AddAndActivateScratchTab(vm, editorFactory, out _);
        vm.SetLanguageCommand!.Execute("JSON");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["JSON"], CheckedLabels(languageMenu));

        await workspace.OpenSettingsAsync(CancellationToken.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(CheckedLabels(languageMenu));
    }
}
