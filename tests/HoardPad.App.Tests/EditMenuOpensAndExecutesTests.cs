// EditMenuOpensAndExecutesTests.cs — BUG-9 interaction-layer regression guard.
//
// EditMenuCommandTests.cs only proves EditorViewModel's own Undo/Redo/Cut/Copy/Paste commands exist
// and route to their view-backed handlers when invoked directly — it never builds a real MainWindow or
// touches the "_Edit" MenuItem, so it stayed green the whole time BUG-9 was live (the parent MenuItem
// simply never opened). BUG-9's root cause: the five child MenuItems under "_Edit" bound their Command
// to a two-segment compiled-binding path through the internal-typed `Editor` property
// (`{Binding Editor.UndoCommand}`), which Avalonia's compiled-binding generator cannot traverse — every
// child Command resolved to null, so the parent "_Edit" MenuItem (which Avalonia treats as having no
// actionable children) never dropped down. The fix exposes flat public forwarding command properties on
// MainWindowViewModel (UndoCommand, RedoCommand, CutCommand, CopyCommand, PasteCommand,
// ReinterpretCommand, ToggleMarkdownPreviewCommand) and rebinds the XAML to the flat single-segment path.
//
// This test drives a REAL simulated click on the top-level "_Edit" MenuItem (mirrors
// BootstrapTests.FileMenuOpensItsSubmenuOnClick / HelpMenuOpensAndListsAboutHoardPad), proving the
// submenu actually opens, then finds the "_Undo" child MenuItem inside it and executes its bound
// Command, proving the forwarded command is wired all the way through to the live EditorViewModel (not
// merely non-null).

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Hosting;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class EditMenuOpensAndExecutesTests
{
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

    [AvaloniaFact]
    public void EditMenuOpensItsSubmenuOnClick()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem? editMenuItem = window
            .GetVisualDescendants()
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Edit");

        Assert.NotNull(editMenuItem);
        Assert.False(editMenuItem!.IsSubMenuOpen);

        Point clickPoint = WindowOffsetOf(editMenuItem, window)
            + new Point(editMenuItem.Bounds.Width / 2, editMenuItem.Bounds.Height / 2);

        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(editMenuItem.IsSubMenuOpen); // BUG-9: this used to stay false — every child Command was null.

        var undoItem = editMenuItem
            .GetLogicalDescendants()
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Undo");

        Assert.NotNull(undoItem);
        Assert.NotNull(undoItem!.Command);
        Assert.Same(vm.UndoCommand, undoItem.Command);
        Assert.True(undoItem.Command!.CanExecute(null));
    }

    [AvaloniaFact]
    public void UndoMenuItemExecutingItsCommandInvokesTheLiveEditorHandler()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var editor = provider.GetRequiredService<EditorViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem editMenuItem = window
            .GetVisualDescendants()
            .OfType<MenuItem>()
            .First(m => (m.Header as string) == "_Edit");

        Point clickPoint = WindowOffsetOf(editMenuItem, window)
            + new Point(editMenuItem.Bounds.Width / 2, editMenuItem.Bounds.Height / 2);
        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editMenuItem.IsSubMenuOpen);

        var undoItem = editMenuItem
            .GetLogicalDescendants()
            .OfType<MenuItem>()
            .First(m => (m.Header as string) == "_Undo");

        bool invoked = false;
        editor.UndoRequested += () => invoked = true;

        undoItem.Command!.Execute(null);

        Assert.True(invoked); // proves the child MenuItem's Command reaches the real live editor, not a stub.
    }
}
