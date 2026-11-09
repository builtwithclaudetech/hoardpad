// DefaultLanguageOnNewDocTests.cs — QA4-7 RENDER/INTERACTION VERIFIER.
//
// File ▸ New (BUG-7) always minted a fresh EditorViewModel via WorkspaceService.NewDocument, which starts
// Plain Text (FR-8 scratch-buffer default) and NEVER consulted the FR-35 DefaultLanguage setting — the
// persisted value round-tripped through Settings (QA3-1) but had no consumer (the QA3-1 scope note, and
// QA3-4's maintainer-live-test regression). This reopens QA3-4 and closes that follow-up: MainWindowViewModel.
// NewDocument() now applies the shell's DefaultLanguage (seeded by App.axaml.cs from the persisted
// settings, kept live via SettingsStore.SettingsChanged) to the freshly-activated tab through the SAME
// EditorViewModel.SetLanguageCommand path QA2-13's manual Language-menu pick uses — so the grammar
// (Document.GrammarScope), the status-bar label (LanguageMode), and the QA3-4 Language-menu checkmark all
// move together, exactly the "green test, dead UI" bar LanguageMenuCheckmarkTests already set for this
// project (real MainWindow, real CompositionRoot, real rendered MenuItem.IsChecked — not merely the
// LanguageMode string).
//
// The class name contains "DefaultLanguageOnNewDoc" so --filter "FullyQualifiedName~DefaultLanguageOnNewDoc"
// resolves it.

using System.Collections.Generic;
using System.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class DefaultLanguageOnNewDocTests
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

    /// <summary>Finds + opens the top-level "_Language" MenuItem (mirrors LanguageMenuCheckmarkTests).</summary>
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
    private static List<string> CheckedLabels(MenuItem languageMenu) =>
        languageMenu.GetLogicalDescendants().OfType<MenuItem>()
            .Where(m => m.IsChecked)
            .Select(m => m.Header as string)
            .Where(h => h is not null)
            .Select(h => h!)
            .ToList();

    /// <summary>Wires vm.NewDocumentCommand to WorkspaceService.NewDocument — the SAME delegation
    /// App.axaml.cs's composition root performs (shell.NewDocumentHandler), which the test host does not
    /// run (it only calls CompositionRoot.Build(), not App.OnFrameworkInitializationCompleted).</summary>
    private static void WireNewDocumentHandler(MainWindowViewModel vm, WorkspaceService workspace) =>
        vm.NewDocumentHandler = () => { workspace.NewDocument(); return Task.CompletedTask; };

    [AvaloniaFact]
    public async Task NewDocumentAppliesThePersistedDefaultLanguage()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        WireNewDocumentHandler(vm, workspace);
        vm.DefaultLanguage = "Markdown"; // mirrors App.axaml.cs seeding shell.DefaultLanguage from settings.

        await vm.NewDocumentCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // (1) status-bar label.
        Assert.Equal("Markdown", vm.LanguageMode);
        // (2) TextMate grammar actually applied — not just the label.
        Assert.NotNull(vm.Editor);
        Assert.Equal("text.html.markdown", vm.Editor!.Document.GrammarScope);
        // (3) Language-menu checkmark (QA3-4).
        Assert.Equal(["Markdown"], CheckedLabels(languageMenu));
    }

    [AvaloniaFact]
    public async Task NewDocumentStaysPlainTextWhenNoDefaultIsSet()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        WireNewDocumentHandler(vm, workspace);
        Assert.Equal("Plain Text", vm.DefaultLanguage); // the documented FR-35 default, unset.

        await vm.NewDocumentCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Plain Text", vm.LanguageMode);
        Assert.NotNull(vm.Editor);
        Assert.Null(vm.Editor!.Document.GrammarScope);
        Assert.Equal(["Plain Text"], CheckedLabels(languageMenu));
    }

    [AvaloniaFact]
    public async Task ASecondNewDocumentAlsoOpensAsTheDefaultLanguageAndEachTabTracksItsOwnCheckmark()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem languageMenu = OpenLanguageMenu(window);

        WireNewDocumentHandler(vm, workspace);
        vm.DefaultLanguage = "Markdown";

        await vm.NewDocumentCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        DocumentTab tabA = vm.ActiveTab!;
        Assert.Equal(["Markdown"], CheckedLabels(languageMenu));

        await vm.NewDocumentCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        DocumentTab tabB = vm.ActiveTab!;
        Assert.NotSame(tabA, tabB);
        Assert.Equal(["Markdown"], CheckedLabels(languageMenu)); // tabB also opened Markdown.

        // Diverge tabB's language manually (QA2-13 path) — tabA must keep its OWN Markdown checkmark.
        vm.SetLanguageCommand!.Execute("Python");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Python"], CheckedLabels(languageMenu));

        vm.ActivateTab(tabA);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Markdown"], CheckedLabels(languageMenu));

        vm.ActivateTab(tabB);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Python"], CheckedLabels(languageMenu));
    }
}
