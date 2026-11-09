// OpenFolderToolbarActionTests.cs — STF-5: wires the toolbar/menu "Open Folder…" action end to end at
// the interaction layer. Drives MainWindowViewModel.OpenFolderCommand (the RelayCommand the toolbar
// Button and the File menu item both bind to) with the composition-root Handler wired to a real
// WorkspaceService, a scripted IOpenFolderPrompt standing in for the OS folder picker (STF-5's "picker
// path injected/faked"), and the existing NullAiSessionBinder fake so no real AI spine is touched.
// Asserts the command invocation reaches WorkspaceService.OpenFolderAsync (observable via the workspace's
// OpenFolder/CanonicalOpenFolder — NullAiSessionBinder.BindLiveFolderAsync echoes the bound root back).
// Class name contains "OpenFolder" so the verify filter "OpenFolder" resolves it.

using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class OpenFolderToolbarActionTests
{
    private static (MainWindowViewModel shell, WorkspaceService ws, ScriptedOpenFolderPrompt picker) NewWired(string? pickedRoot)
    {
        var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var shell = new MainWindowViewModel();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        var conflictPrompt = new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk);
        var editorFactory = MakeEditorFactory();
        var checklistFactory = MakeChecklistFactory();
        var aiBinder = new NullAiSessionBinder();
        var picker = new ScriptedOpenFolderPrompt(pickedRoot);

        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler,
            aiBinder, conflictPrompt, editorFactory, checklistFactory, MakeSettingsTabFactory(),
            picker, StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        // The exact composition-root wiring (App.axaml.cs): the toolbar/menu command delegates to the
        // shell's Handler seam, which the composition root points at WorkspaceService.OpenFolderPickerAsync.
        shell.OpenFolderHandler = () => ws.OpenFolderPickerAsync(CancellationToken.None);

        return (shell, ws, picker);
    }

    private static EditorViewModelFactory MakeEditorFactory()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModelFactory(fileService, registry, new LanguageCatalog(registry));
    }

    private static ChecklistViewModelFactory MakeChecklistFactory() =>
        new(new HoardPad.App.Checklist.ChecklistFileService(new RealAtomicFileStore()), new ManualTimeProvider());

    private static SettingsTabFactory MakeSettingsTabFactory() =>
        new(new SettingsStore(new RealAtomicFileStore(), new TempAppDataPaths()));

    [Fact]
    public async Task ToolbarOpenFolderCommandInvokesWorkspaceOpenFolderAsyncOnAConfirmedPick()
    {
        using var _paths = new TempAppDataPaths(); // dispose only; NewWired makes its own.
        (MainWindowViewModel shell, WorkspaceService ws, ScriptedOpenFolderPrompt picker) = NewWired(pickedRoot: "/tmp/hoardpad-stf5-pick");

        Assert.Null(ws.OpenFolder); // no folder bound before the action fires.

        // Drive the SAME command the toolbar Button and the File ▸ Open Folder… menu item bind to.
        await shell.OpenFolderCommand.ExecuteAsync(null);

        // The picker fired exactly once (the OS folder-picker seam was invoked)...
        Assert.Equal(1, picker.CallCount);
        // ...and WorkspaceService.OpenFolderAsync ran to completion: the picked root is now bound
        // (NullAiSessionBinder.BindLiveFolderAsync echoes RawRoot/CanonicalRoot back unchanged).
        Assert.Equal("/tmp/hoardpad-stf5-pick", ws.OpenFolder);
        Assert.Equal("/tmp/hoardpad-stf5-pick", ws.CanonicalOpenFolder);
    }

    [Fact]
    public async Task ToolbarOpenFolderCommandIsANoOpWhenThePickerIsCancelled()
    {
        using var _paths = new TempAppDataPaths();
        (MainWindowViewModel shell, WorkspaceService ws, ScriptedOpenFolderPrompt picker) = NewWired(pickedRoot: null);

        await shell.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(1, picker.CallCount);
        Assert.Null(ws.OpenFolder); // a cancelled picker never reaches OpenFolderAsync.
    }
}
