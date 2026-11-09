// TabContextMenuTests.cs — FEAT-7 (tab-strip right-click context menu).
//
// Close, Save, Save As…, Copy Filename, Copy File Path, Copy Folder Path. Close/Save/Save As… route through
// MainWindowViewModel forwarding commands (Close reuses BUG-3's CloseTabCommand; Save/Save As… activate the
// clicked tab first, then reuse FEAT-2's SaveDocumentHandler/SaveAsHandler — WorkspaceService.SaveDocumentAsync
// / SaveAsActiveDocumentAsync operate on ActiveTab only). The three Copy items are a pure View-layer clipboard
// concern (MainWindow.axaml.cs), backed by Avalonia.Headless's in-memory IClipboard stub under test.
//
// Interaction layer: every test opens the REAL Border.ContextMenu (ContextMenu.Open(chip), matching the
// ShowAt/IsOpen pattern Run12RenderVerifyTests.cs already uses for the "+" tab-strip MenuFlyout) and raises the
// MenuItem's real Click routed event — the same event Avalonia's own pointer-click machinery raises — rather
// than invoking a ViewModel method directly, so this proves the XAML wiring (Tag="{Binding}" + Click="...") and
// not just the underlying command/handler logic.
//
// Class name contains "TabContextMenu" so --filter "FullyQualifiedName~TabContextMenu" resolves it.

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class TabContextMenuTests
{
    [AvaloniaFact]
    public void TheContextMenuListsAllSixItemsInTheSpecOrder()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeTab("a");
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        ContextMenu menu = OpenContextMenu(window, tab);

        var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal(
            ["Close", "Save", "Save As…", "Copy Filename", "Copy File Path", "Copy Folder Path"],
            headers);
    }

    [AvaloniaFact]
    public void CloseClosesTheClickedTabEvenWhenItIsNotTheActiveTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a); // a is active; the context menu below targets b.

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        ClickMenuItem(window, b, "Close");

        Assert.DoesNotContain(b, shell.Tabs);
        Assert.Same(a, shell.ActiveTab); // b was never activated by the right-click — BUG-3 reuse.
    }

    [AvaloniaFact]
    public void SaveActivatesTheClickedBackgroundTabAndInvokesTheSaveHandler()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a); // a active; Save is invoked from b's (background) context menu.

        int calls = 0;
        shell.SaveDocumentHandler = () => { calls++; return Task.CompletedTask; };

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        ClickMenuItem(window, b, "Save");

        Assert.Equal(1, calls);
        Assert.Same(b, shell.ActiveTab); // FEAT-7: Save activates the clicked tab first.
    }

    [AvaloniaFact]
    public void SaveAsActivatesTheClickedBackgroundTabAndInvokesTheSaveAsHandler()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a);

        int calls = 0;
        shell.SaveAsHandler = () => { calls++; return Task.CompletedTask; };

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        ClickMenuItem(window, b, "Save As…");

        Assert.Equal(1, calls);
        Assert.Same(b, shell.ActiveTab);
    }

    [AvaloniaFact]
    public async Task CopyFilenamePlacesJustTheFileNameOnTheClipboard()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeTabWithPath("a", Path.Combine("tmp", "hoardpad-test", "notes.txt"));
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        ClickMenuItem(window, tab, "Copy Filename");

        Assert.Equal("notes.txt", await ClipboardTextAsync(window));
    }

    [AvaloniaFact]
    public async Task CopyFilePathPlacesTheFullPathOnTheClipboard()
    {
        var shell = new MainWindowViewModel();
        string fullPath = Path.Combine("tmp", "hoardpad-test", "notes.txt");
        DocumentTab tab = MakeTabWithPath("a", fullPath);
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        ClickMenuItem(window, tab, "Copy File Path");

        Assert.Equal(fullPath, await ClipboardTextAsync(window));
    }

    [AvaloniaFact]
    public async Task CopyFolderPathPlacesTheContainingDirectoryOnTheClipboard()
    {
        var shell = new MainWindowViewModel();
        string folder = Path.Combine("tmp", "hoardpad-test");
        DocumentTab tab = MakeTabWithPath("a", Path.Combine(folder, "notes.txt"));
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        ClickMenuItem(window, tab, "Copy Folder Path");

        Assert.Equal(folder, await ClipboardTextAsync(window));
    }

    [AvaloniaFact]
    public async Task CopyItemsAreNoOpsForANeverSavedScratchBuffer()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeTab("a"); // no path — a never-saved scratch buffer.
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)!.Clipboard!;
        await clipboard.SetTextAsync("sentinel");

        ClickMenuItem(window, tab, "Copy Filename");
        ClickMenuItem(window, tab, "Copy File Path");
        ClickMenuItem(window, tab, "Copy Folder Path");

        Assert.Equal("sentinel", await ClipboardTextAsync(window)); // unchanged — guarded no-op.
    }

    // ----- helpers -----

    private static ContextMenu OpenContextMenu(Window window, DocumentTab tab)
    {
        Border chip = window.GetVisualDescendants().OfType<Border>().First(b => ReferenceEquals(b.Tag, tab));
        ContextMenu menu = Assert.IsType<ContextMenu>(chip.ContextMenu);
        menu.Open(chip);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static void ClickMenuItem(Window window, DocumentTab tab, string header)
    {
        ContextMenu menu = OpenContextMenu(window, tab);
        MenuItem item = menu.Items.OfType<MenuItem>().First(m => (m.Header as string) == header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<string?> ClipboardTextAsync(Window window)
    {
        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
        return clipboard is null ? null : await clipboard.GetTextAsync();
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);

    private static DocumentTab MakeTabWithPath(string id, string path)
    {
        DocumentTab tab = TabFactory.New(id);
        if (tab.Editor is { } editor)
            editor.Document.Path = path;
        return tab;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared factory (file-scoped, mirrors TabFactory in TabActivationClickTests.cs / TabCloseTests.cs).
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
