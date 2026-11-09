// FolderTreeRenderVerifyTests.cs — QA3-2 RENDER/INTERACTION VERIFIER (verification only, no product
// change). Builds the REAL MainWindow via the REAL CompositionRoot, drives the REAL WorkspaceService
// against a throwaway fixture folder (one file + one subdirectory), and CAPTURES A RENDERED PNG of the
// populated TreeView so the root node (labeled with the open folder's own leaf name — QA4-6, was the
// literal "(root)"), the "sub" directory row, and the "notes.txt" file row are all inspectable pixel
// ground truth — not merely asserted view-model state. Mirrors the Run-12 / STF-5 render-verify pattern
// (real Skia-headless rendering via TestAppBuilder).
//
// The class name contains "FolderTreeRenderVerify" so --filter "FullyQualifiedName~Folder" resolves it.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FolderTreeRenderVerifyTests
{
    private static readonly string ScreenshotDir = ResolveScreenshotDir();

    private static string ResolveScreenshotDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "qa3-live", "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Capture(Window window, string name)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(ScreenshotDir, name + ".png");
        frame?.Save(path);
        return path;
    }

    /// <summary>A throwaway fixture folder with one file AND one subdirectory (QA3-2 verify contract).</summary>
    private sealed class FixtureFolder : IDisposable
    {
        public string Root { get; }

        public FixtureFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "hoardpad-foldertree-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "notes.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(Root, "sub"));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [AvaloniaFact]
    public async Task OpeningAFolderRendersTheTreeWithTheDirectoryAndFileRowsVisible()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        using var fixture = new FixtureFolder();

        // Same production population path OpenFolderToolbarActionTests exercises via the picker seam — here
        // we call it directly (the picker itself is a scripted OS dialog, already covered there).
        await workspace.OpenFolderAsync(fixture.Root, CancellationToken.None);

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        TreeView? tree = window.GetVisualDescendants().OfType<TreeView>().FirstOrDefault(t => t.Name == "FolderTree");
        Assert.NotNull(tree);
        Assert.True(tree!.IsEffectivelyVisible, "the folder-tree pane must be shown once a folder is armed (design §9.5).");

        // The rendered row text — not merely the bound FolderTreeNode collection — so a broken ItemTemplate
        // (e.g. an unresolved TreeDataTemplate) would fail this assertion even if the VM state were correct.
        var rowLabels = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => t is not null)
            .ToList();

        Assert.Contains(Path.GetFileName(fixture.Root), rowLabels); // QA4-6: leaf folder name, not "(root)".
        Assert.Contains("sub", rowLabels);
        Assert.Contains("notes.txt", rowLabels);

        Capture(window, "qa3-2-folder-tree-populated");
    }
}
