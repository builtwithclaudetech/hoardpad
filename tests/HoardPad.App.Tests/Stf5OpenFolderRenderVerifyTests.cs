// Stf5OpenFolderRenderVerifyTests.cs — STF-5 vision-review evidence: renders the REAL MainWindow built
// through the REAL CompositionRoot (real production wiring, including the real StorageOpenFolderPrompt),
// confirms the toolbar hosts a real "Open Folder…" Button bound to OpenFolderCommand (not a placeholder),
// and captures a rendered PNG to artifacts/stf-live/ for the vision-review gate. Mirrors the
// Run12RenderVerifyTests capture pattern (real Skia-headless rendering, no product code touched here).
// Class name contains "Stf5" so the verify filter "Stf5" resolves it.

using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Hosting;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Stf5OpenFolderRenderVerifyTests
{
    private static readonly string ScreenshotDir = ResolveScreenshotDir();

    /// <summary>
    /// Resolve the artifacts/stf-live directory from the repo root (per the STF build-context file map).
    /// The test binary runs deep under bin/, so walk up to the directory that contains "HoardPad.sln".
    /// </summary>
    private static string ResolveScreenshotDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "stf-live");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Capture(Window window, string name)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(ScreenshotDir, name + ".png");
        frame?.Save(path);
        return path;
    }

    [AvaloniaFact]
    public void ToolbarHostsARealOpenFolderButtonBoundToOpenFolderCommand()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Button? openFolderButton = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "Open Folder…");

        Assert.NotNull(openFolderButton);
        Assert.True(openFolderButton!.IsEffectivelyVisible);
        // The real production command (not a design-time no-op): CanExecute is true because
        // MainWindowViewModel.OpenFolderCommand always executes (it no-ops internally when the
        // Handler is unset), and the composition root DOES set OpenFolderHandler at startup.
        Assert.Same(vm.OpenFolderCommand, openFolderButton.Command);

        Capture(window, "stf5-toolbar-open-folder");
    }
}
