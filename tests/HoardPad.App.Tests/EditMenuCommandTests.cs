// EditMenuCommandTests.cs — unit tests for the Edit menu command path
// (EditorViewModel.Undo/Redo/Cut/Copy/Paste, BUG-9).
//
// The Edit menu items bind to `{Binding Editor.UndoCommand}` etc. on the shell ViewModel.
// These tests prove the commands exist, are not null, and route to their view-backed
// handlers when subscribed. When no handler is subscribed (test/design path), execution
// is a safe no-op (null-conditional invocation, no throw).
//
// Proven here:
//   - Each command is non-null (the [RelayCommand] generator emitted it).
//   - CanExecute returns true (the default; no CanExecute guard is applied).
//   - Subscribing a handler and executing the command invokes that handler.
//   - Executing without a handler is a safe no-op (no NullReferenceException).

using System;
using HoardPad.App.Editor;
using HoardPad.App.Hosting;
using HoardPad.App.ViewModels;
using HoardPad.Host;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class EditMenuCommandTests
{
    private static EditorViewModel CreateVm()
    {
        var options = new HoardPadRegistryOptions(ThemeName.OneDark);
        var store = new AtomicFileStore();
        return new EditorViewModel(
            new EditorFileService(store, new PathResolver(), new UtfDefaultEncodingDetector()),
            options,
            new LanguageCatalog(options));
    }

    // ---- Commands exist and are non-null. ----

    [Fact]
    public void UndoCommandIsNotNull()
    {
        EditorViewModel vm = CreateVm();
        Assert.NotNull(vm.UndoCommand);
    }

    [Fact]
    public void RedoCommandIsNotNull()
    {
        EditorViewModel vm = CreateVm();
        Assert.NotNull(vm.RedoCommand);
    }

    [Fact]
    public void CutCommandIsNotNull()
    {
        EditorViewModel vm = CreateVm();
        Assert.NotNull(vm.CutCommand);
    }

    [Fact]
    public void CopyCommandIsNotNull()
    {
        EditorViewModel vm = CreateVm();
        Assert.NotNull(vm.CopyCommand);
    }

    [Fact]
    public void PasteCommandIsNotNull()
    {
        EditorViewModel vm = CreateVm();
        Assert.NotNull(vm.PasteCommand);
    }

    // ---- CanExecute is true by default (no guard). ----

    [Fact]
    public void UndoCommandCanExecuteIsTrueByDefault()
    {
        EditorViewModel vm = CreateVm();
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void RedoCommandCanExecuteIsTrueByDefault()
    {
        EditorViewModel vm = CreateVm();
        Assert.True(vm.RedoCommand.CanExecute(null));
    }

    // ---- Executing a command invokes the subscribed handler. ----

    [Fact]
    public void UndoCommandInvokesHandler()
    {
        EditorViewModel vm = CreateVm();
        bool invoked = false;
        vm.UndoRequested += () => invoked = true;

        vm.UndoCommand.Execute(null);

        Assert.True(invoked);
    }

    [Fact]
    public void RedoCommandInvokesHandler()
    {
        EditorViewModel vm = CreateVm();
        bool invoked = false;
        vm.RedoRequested += () => invoked = true;

        vm.RedoCommand.Execute(null);

        Assert.True(invoked);
    }

    [Fact]
    public void CutCommandInvokesHandler()
    {
        EditorViewModel vm = CreateVm();
        bool invoked = false;
        vm.CutRequested += () => invoked = true;

        vm.CutCommand.Execute(null);

        Assert.True(invoked);
    }

    [Fact]
    public void CopyCommandInvokesHandler()
    {
        EditorViewModel vm = CreateVm();
        bool invoked = false;
        vm.CopyRequested += () => invoked = true;

        vm.CopyCommand.Execute(null);

        Assert.True(invoked);
    }

    [Fact]
    public void PasteCommandInvokesHandler()
    {
        EditorViewModel vm = CreateVm();
        bool invoked = false;
        vm.PasteRequested += () => invoked = true;

        vm.PasteCommand.Execute(null);

        Assert.True(invoked);
    }

    // ---- Executing without a handler is a safe no-op. ----

    [Fact]
    public void UndoCommandWithoutHandlerDoesNotThrow()
    {
        EditorViewModel vm = CreateVm();
        // No handler subscribed — null-conditional invocation must not throw.
        vm.UndoCommand.Execute(null);
    }

    [Fact]
    public void RedoCommandWithoutHandlerDoesNotThrow()
    {
        EditorViewModel vm = CreateVm();
        vm.RedoCommand.Execute(null);
    }

    [Fact]
    public void CutCommandWithoutHandlerDoesNotThrow()
    {
        EditorViewModel vm = CreateVm();
        vm.CutCommand.Execute(null);
    }

    [Fact]
    public void CopyCommandWithoutHandlerDoesNotThrow()
    {
        EditorViewModel vm = CreateVm();
        vm.CopyCommand.Execute(null);
    }

    [Fact]
    public void PasteCommandWithoutHandlerDoesNotThrow()
    {
        EditorViewModel vm = CreateVm();
        vm.PasteCommand.Execute(null);
    }
}
