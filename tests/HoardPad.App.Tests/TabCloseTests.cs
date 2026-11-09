// TabCloseTests.cs — BUG-3 tab close button + Ctrl+W (× affordance + keyboard shortcut).
//
// Tests the CloseTab per-tab command and the CloseActiveTab (Ctrl+W) command, including:
//   - removing the tab from the Tabs collection
//   - activating neighbor (right, left, last-closes-editor)
//   - no activation change when closing a background tab
//   - dirty-tab confirmation (ConfirmCloseTab delegate)
//   - clean tabs close without confirmation
//   - closing a tab not in the strip is a no-op
//
// All class names contain "TabClose" so the verify filter resolves them.

using System.Threading.Tasks;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// CloseTab (per-tab × button) — collection removal + neighbor activation
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabCloseCollectionTests
{
    [Fact]
    public void CloseTabRemovesTheTabFromTabs()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a); shell.Tabs.Add(b);
        shell.ActivateTab(a);

        shell.CloseTabCommand.Execute(a);

        Assert.DoesNotContain(a, shell.Tabs);
        Assert.Single(shell.Tabs);
    }

    [Fact]
    public void CloseTabActivatesRightNeighborWhenActiveTabIsClosed()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(b); // middle tab active.

        shell.CloseTabCommand.Execute(b);

        Assert.Same(c, shell.ActiveTab); // right neighbor at same index.
    }

    [Fact]
    public void CloseTabActivatesLeftNeighborWhenLastTabIsClosed()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(c); // last tab active.

        shell.CloseTabCommand.Execute(c);

        Assert.Same(b, shell.ActiveTab); // left neighbor (clamped to Count-1).
    }

    [Fact]
    public void CloseTabClearsEditorWhenLastTabIsClosed()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);
        shell.ActivateTab(a);

        shell.CloseTabCommand.Execute(a);

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.ActiveTab);
    }

    [Fact]
    public void CloseTabDoesNotChangeActiveTabWhenClosingBackgroundTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b"), c = MakeTab("c");
        shell.Tabs.Add(a); shell.Tabs.Add(b); shell.Tabs.Add(c);
        shell.ActivateTab(b); // b is active.

        shell.CloseTabCommand.Execute(a); // close background tab a.

        Assert.Same(b, shell.ActiveTab); // active tab unchanged.
        Assert.Equal(2, shell.Tabs.Count);
        Assert.DoesNotContain(a, shell.Tabs);
    }

    [Fact]
    public void CloseTabOnTabNotInTabsIsNoOp()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);
        shell.ActivateTab(a);
        DocumentTab stranger = MakeTab("x"); // never added.

        int changeCount = 0;
        shell.Tabs.CollectionChanged += (_, _) => changeCount++;

        shell.CloseTabCommand.Execute(stranger);

        Assert.Equal(0, changeCount);
        Assert.Single(shell.Tabs);
        Assert.Same(a, shell.ActiveTab);
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);
}

// ─────────────────────────────────────────────────────────────────────────────
// CloseActiveTab (Ctrl+W / File > Close) — same behavior as CloseTab on the active tab
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabCloseActiveTabTests
{
    [Fact]
    public void CloseActiveTabRemovesTheActiveTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a); shell.Tabs.Add(b);
        shell.ActivateTab(a);

        shell.CloseActiveTabCommand.Execute(null);

        Assert.DoesNotContain(a, shell.Tabs);
        Assert.Single(shell.Tabs);
    }

    [Fact]
    public void CloseActiveTabActivatesRightNeighbor()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a); shell.Tabs.Add(b);
        shell.ActivateTab(a);

        shell.CloseActiveTabCommand.Execute(null);

        Assert.Same(b, shell.ActiveTab);
    }

    [Fact]
    public void CloseActiveTabActivatesLeftNeighborWhenLastActive()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a); shell.Tabs.Add(b);
        shell.ActivateTab(b); // last tab.

        shell.CloseActiveTabCommand.Execute(null);

        Assert.Same(a, shell.ActiveTab); // clamped to Count-1 = 0.
    }

    [Fact]
    public void CloseActiveTabClearsEditorWhenOnlyTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a");
        shell.Tabs.Add(a);
        shell.ActivateTab(a);

        shell.CloseActiveTabCommand.Execute(null);

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.ActiveTab);
    }

    [Fact]
    public void CloseActiveTabIsNoOpWhenNoActiveTab()
    {
        var shell = new MainWindowViewModel();

        // Should not throw.
        shell.CloseActiveTabCommand.Execute(null);

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.ActiveTab);
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);
}

// ─────────────────────────────────────────────────────────────────────────────
// Dirty-tab confirmation — ConfirmCloseTab delegate behavior
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabCloseDirtyConfirmationTests
{
    [Fact]
    public void CloseDirtyTabInvokesConfirmCloseTabDelegate()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeDirtyTab("dirty");
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        bool wasCalled = false;
        shell.ConfirmCloseTab = _ =>
        {
            wasCalled = true;
            return Task.FromResult(true);
        };

        shell.CloseTabCommand.Execute(tab);

        Assert.True(wasCalled);
        Assert.DoesNotContain(tab, shell.Tabs);
    }

    [Fact]
    public void CloseDirtyTabCancelsWhenConfirmReturnsFalse()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeDirtyTab("dirty");
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        bool wasCalled = false;
        shell.ConfirmCloseTab = _ =>
        {
            wasCalled = true;
            return Task.FromResult(false); // user cancels.
        };

        shell.CloseTabCommand.Execute(tab);

        Assert.True(wasCalled);
        Assert.Contains(tab, shell.Tabs); // tab not removed.
        Assert.Same(tab, shell.ActiveTab); // still active.
    }

    [Fact]
    public void CleanTabDoesNotInvokeConfirmCloseTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeTab("clean"); // clean by default.
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        bool wasCalled = false;
        shell.ConfirmCloseTab = _ =>
        {
            wasCalled = true;
            return Task.FromResult(true);
        };

        shell.CloseTabCommand.Execute(tab);

        Assert.False(wasCalled); // clean tab → no confirm.
        Assert.DoesNotContain(tab, shell.Tabs);
    }

    [Fact]
    public void CloseDirtyTabWithNullDelegateProceedsWithoutPrompt()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeDirtyTab("dirty");
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        // ConfirmCloseTab is null by default.
        shell.CloseTabCommand.Execute(tab);

        Assert.DoesNotContain(tab, shell.Tabs);
        Assert.Empty(shell.Tabs);
    }

    [Fact]
    public void CloseActiveDirtyTabViaCtrlWInvokesConfirmCloseTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab tab = MakeDirtyTab("dirty");
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        bool wasCalled = false;
        shell.ConfirmCloseTab = _ =>
        {
            wasCalled = true;
            return Task.FromResult(true);
        };

        shell.CloseActiveTabCommand.Execute(null);

        Assert.True(wasCalled);
        Assert.DoesNotContain(tab, shell.Tabs);
    }

    private static DocumentTab MakeDirtyTab(string id)
    {
        DocumentTab tab = TabFactory.New(id);
        // EditorViewModel's IsDirty is an [ObservableProperty] with a public setter.
        // We reach the Editor via the DocumentTab.Editor property.
        if (tab.Editor is { } editor)
            editor.IsDirty = true;
        return tab;
    }

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared factory — mirrors TabFactory in TabReorderTests.cs (file-scoped).
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
