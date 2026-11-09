// EditorSaveAsTests.cs — FEAT-2/FEAT-8 regression (File ▸ Save As… for editor/text tabs, + the Save
// dialog's File Type selector).
//
// Proves, independently of the OS Save dialog (this codebase's IStorageProvider pickers have no headless
// test seam — see ChecklistSaveAsIntegrationTests's same note):
//   - LanguageCatalog.DefaultExtensionFor: the label -> extension reverse-lookup FEAT-2/FEAT-8 share.
//   - WorkspaceService.DefaultExtensionForEditor: the picker's actual default — the document's CURRENT
//     on-disk extension takes priority over the language-catalog default when the doc already has a path;
//     the language-catalog default only applies to a never-saved scratch buffer.
//   - WorkspaceService.BuildFileTypeChoices (FEAT-8): the Save/Save-As dialog's File Type dropdown, built
//     from LanguageCatalog.AvailableLanguages with the active language's entry ordered first (native save
//     pickers default-select the first FileTypeChoices entry).
//   - RelocateEditorAsync (the picker-independent core of SaveAsActiveEditorAsync): the path-flip — the
//     buffer moves to the new path, is written durably, re-titles the tab (QA3-12 reuse), and the session
//     index is updated immediately so session-restore finds the tab at its new path.
//   - Editor Save-As is NON-DESTRUCTIVE: unlike the QA3-7 checklist relocate, the OLD file is never deleted
//     (standard Notepad++ / VS Code Save-As semantics).
//   - SaveAsActiveEditorAsync / SaveAsActiveDocumentAsync route safely (no-op, no throw) when the active
//     tab is not an editor.
//
// The verify filter matches the class name "SaveAs" (shared with the QA3-7 checklist Save-As suite).

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Checklist;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class EditorSaveAsTests
{
    // ---- LanguageCatalog.DefaultExtensionFor: the label -> extension reverse lookup. ----

    [Theory]
    [InlineData("Python", "py")]
    [InlineData("C#", "cs")]
    [InlineData("Markdown", "md")]
    [InlineData("JSON", "json")]
    public void DefaultExtensionForReturnsTheCatalogExtensionForAKnownLanguage(string label, string expected) =>
        Assert.Equal(expected, LanguageCatalog.DefaultExtensionFor(label));

    [Fact]
    public void DefaultExtensionForReturnsTxtForPlainText() =>
        Assert.Equal("txt", LanguageCatalog.DefaultExtensionFor("Plain Text"));

    [Fact]
    public void DefaultExtensionForFallsBackToTxtForAnUnrecognizedLabel() =>
        Assert.Equal("txt", LanguageCatalog.DefaultExtensionFor("Not A Real Language"));

    [Fact]
    public void DefaultExtensionForDockerfileIsEmptyNotTxt() =>
        // Dockerfile has no conventional extension — the picker must not force one.
        Assert.Equal(string.Empty, LanguageCatalog.DefaultExtensionFor("Dockerfile"));

    // ---- WorkspaceService.DefaultExtensionForEditor: current-path extension wins over language default. ----

    private static EditorViewModel NewEditorVm()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new CountingAtomicFileStore(), new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }

    [Fact]
    public void DefaultExtensionForEditorIsTxtForAPathlessPlainTextScratchBuffer()
    {
        EditorViewModel editor = NewEditorVm();
        Assert.Equal("txt", WorkspaceService.DefaultExtensionForEditor(editor));
    }

    [Fact]
    public void DefaultExtensionForEditorUsesTheLanguageCatalogWhenTheBufferHasNeverBeenSaved()
    {
        EditorViewModel editor = NewEditorVm();
        editor.SetLanguageCommand!.Execute("Python"); // manual Language-menu override (QA2-13), no path yet.

        Assert.Equal("py", WorkspaceService.DefaultExtensionForEditor(editor));
    }

    [Fact]
    public void DefaultExtensionForEditorPrefersTheCurrentOnDiskExtensionOverTheLanguageDefault()
    {
        EditorViewModel editor = NewEditorVm();
        editor.Document.Path = "/tmp/hoardpad-test/notes.log";
        editor.SetLanguageCommand!.Execute("Python"); // language and extension deliberately mismatched.

        Assert.Equal("log", WorkspaceService.DefaultExtensionForEditor(editor));
    }

    [Fact]
    public void DefaultExtensionForEditorFallsBackToLanguageWhenTheCurrentPathHasNoExtension()
    {
        EditorViewModel editor = NewEditorVm();
        editor.Document.Path = "/tmp/hoardpad-test/README"; // no extension at all.

        Assert.Equal("txt", WorkspaceService.DefaultExtensionForEditor(editor)); // still Plain Text.
    }

    // ---- WorkspaceService.BuildFileTypeChoices (FEAT-8): the Save dialog's File Type dropdown. ----

    [Fact]
    public void BuildFileTypeChoicesListsEveryLanguageCatalogEntry()
    {
        IReadOnlyList<FilePickerFileType> choices = WorkspaceService.BuildFileTypeChoices("Plain Text");
        Assert.Equal(LanguageCatalog.AvailableLanguages.Count, choices.Count);
    }

    [Theory]
    [InlineData("Python", "py")]
    [InlineData("C#", "cs")]
    [InlineData("Markdown", "md")]
    [InlineData("Plain Text", "txt")]
    public void BuildFileTypeChoicesOrdersTheActiveLanguageFirst(string activeLabel, string expectedExtension)
    {
        IReadOnlyList<FilePickerFileType> choices = WorkspaceService.BuildFileTypeChoices(activeLabel);

        FilePickerFileType first = choices[0];
        Assert.StartsWith(activeLabel, first.Name, StringComparison.Ordinal);
        Assert.NotNull(first.Patterns);
        Assert.Contains($"*.{expectedExtension}", first.Patterns!);
    }

    [Fact]
    public void BuildFileTypeChoicesGivesDockerfileAnUnrestrictedPatternNotAMalformedDotPattern()
    {
        IReadOnlyList<FilePickerFileType> choices = WorkspaceService.BuildFileTypeChoices("Dockerfile");

        FilePickerFileType first = choices[0];
        Assert.StartsWith("Dockerfile", first.Name, StringComparison.Ordinal);
        Assert.Equal(["*"], first.Patterns);
    }

    [Fact]
    public void BuildFileTypeChoicesFallsBackToCatalogOrderForAnUncatalogedActiveLanguage()
    {
        // "Ruby" is not one of the 20 LanguageCatalog.AvailableLanguages, so no entry can be promoted first —
        // the catalog's own order (Plain Text first) is kept, matching WorkspaceService.DefaultExtensionForEditor's
        // own "txt" fallback for the same off-catalog case.
        IReadOnlyList<FilePickerFileType> choices = WorkspaceService.BuildFileTypeChoices("Ruby");

        Assert.StartsWith("Plain Text", choices[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAsActiveEditorPickerFileTypeChoicesDefaultMatchesTheActiveLanguage()
    {
        // FEAT-8's "Done when": the dropdown's default selection matches the active language — proven here
        // via the exact inputs SaveAsActiveEditorAsync feeds the picker (this codebase's IStorageProvider
        // pickers have no headless test seam to drive the live OS dialog itself).
        EditorViewModel editor = NewEditorVm();
        editor.SetLanguageCommand!.Execute("Rust");

        IReadOnlyList<FilePickerFileType> choices = WorkspaceService.BuildFileTypeChoices(editor.LanguageMode);

        Assert.StartsWith("Rust", choices[0].Name, StringComparison.Ordinal);
        Assert.Contains("*.rs", choices[0].Patterns!);
    }

    /// <summary>An editor VM backed by a REAL disk-writing store (unlike <see cref="NewEditorVm"/>'s in-memory
    /// <see cref="CountingAtomicFileStore"/>) — needed for the WorkspaceService-layer tests below, which assert
    /// against actual files on disk.</summary>
    private static EditorViewModel NewRealEditorVm()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }

    // ---- RelocateEditorAsync: the path-flip + durable write + tab retitle + session-index update. ----

    private static (WorkspaceService ws, MainWindowViewModel shell, TempAppDataPaths paths) NewWorkspace()
    {
        var paths = new TempAppDataPaths();
        var fs = new FakeRecoveryFileSystem();
        var store = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler = new SessionReconciler(fs);
        var sessionService = new SessionService(store, reconciler, fs, new ManualTimeProvider());
        var shell = new MainWindowViewModel();
        var aiPanel = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);

        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var editorFileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorFactory = new EditorViewModelFactory(editorFileService, registry, new LanguageCatalog(registry));
        var checklistFactory = new ChecklistViewModelFactory(
            new ChecklistFileService(new RealAtomicFileStore()), new ManualTimeProvider());
        var settingsFactory = new SettingsTabFactory(new SettingsStore(new RealAtomicFileStore(), paths));

        var ws = new WorkspaceService(
            shell, aiPanel, sessionService, reconciler, new NullAiSessionBinder(),
            new ScriptedRestoreConflictPrompt(RestoreConflictChoice.KeepDisk),
            editorFactory, checklistFactory, settingsFactory, new ScriptedOpenFolderPrompt(null),
            StubMyDocuments.Resolve, new HoardPad.Host.PathResolver());

        return (ws, shell, paths);
    }

    private static DocumentTab AddEditorTab(MainWindowViewModel shell, EditorViewModel editor, string tabId = "editor-tab-1")
    {
        var tab = DocumentTab.ForEditor(tabId, TabKind.Scratch, editor);
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);
        return tab;
    }

    [Fact]
    public async Task RelocateEditorMovesTheBufferToTheNewPathAndWritesItDurably()
    {
        var (ws, shell, paths) = NewWorkspace();
        using var _paths = paths;

        EditorViewModel editor = NewRealEditorVm();
        editor.Document.Text = "hello from a never-saved buffer";
        DocumentTab tab = AddEditorTab(shell, editor);

        string newPath = Path.Combine(paths.AppDataRoot, "Relocated.txt");
        await ws.RelocateEditorAsync(editor, tab.TabId, newPath, CancellationToken.None);

        Assert.Equal(newPath, editor.Document.Path);
        Assert.False(editor.IsDirty);
        Assert.True(File.Exists(newPath));
        Assert.Equal("hello from a never-saved buffer", await File.ReadAllTextAsync(newPath));
        Assert.Equal("Relocated.txt", tab.DisplayName); // QA3-12 reuse — the tab retitles.
    }

    [Fact]
    public async Task RelocateEditorUpdatesTheSessionIndexSoRestoreFindsTheNewPath()
    {
        var (ws, shell, paths) = NewWorkspace();
        using var _paths = paths;

        EditorViewModel editor = NewRealEditorVm();
        editor.Document.Text = "persisted content";
        DocumentTab tab = AddEditorTab(shell, editor);

        string newPath = Path.Combine(paths.AppDataRoot, "Persisted.txt");
        await ws.RelocateEditorAsync(editor, tab.TabId, newPath, CancellationToken.None);

        // ---- Restart over the SAME session.json: session-restore must find the tab at the NEW path. ----
        var fs2 = new FakeRecoveryFileSystem();
        fs2.SetProbe(newPath, new CurrentDiskFile(Exists: true, ContentHash: "H", Mtime: DateTimeOffset.UnixEpoch));
        var store2 = new SessionStore(new RealAtomicFileStore(), paths);
        var reconciler2 = new SessionReconciler(fs2);
        var session2 = new SessionService(store2, reconciler2, fs2, new ManualTimeProvider());

        SessionReconcileResult result = await session2.LoadAndReconcileAsync(CancellationToken.None);

        RestoredTab restoredTab = Assert.Single(result.Tabs);
        Assert.Equal(newPath, restoredTab.Path);
    }

    // ---- Non-destructive Save-As: unlike QA3-7's checklist relocate, the OLD file must survive untouched. ----

    [Fact]
    public async Task RelocateEditorNeverDeletesOrModifiesThePriorFile()
    {
        var (ws, shell, paths) = NewWorkspace();
        using var _paths = paths;

        string oldPath = Path.Combine(paths.AppDataRoot, "Original.txt");
        await File.WriteAllTextAsync(oldPath, "original on-disk content");

        EditorViewModel editor = NewRealEditorVm();
        await editor.OpenAsync(oldPath, CancellationToken.None);
        DocumentTab tab = AddEditorTab(shell, editor);

        string newPath = Path.Combine(paths.AppDataRoot, "Copy.txt");
        await ws.RelocateEditorAsync(editor, tab.TabId, newPath, CancellationToken.None);

        Assert.True(File.Exists(oldPath), "a text Save-As must never delete the prior file");
        Assert.Equal("original on-disk content", await File.ReadAllTextAsync(oldPath));
        Assert.True(File.Exists(newPath));
    }

    // ---- SaveAsActiveEditorAsync / SaveAsActiveDocumentAsync: safe no-op when there is no editor to save. ----

    [Fact]
    public async Task SaveAsActiveEditorAsyncIsANoOpWhenTheActiveTabIsAChecklist()
    {
        var (ws, shell, paths) = NewWorkspace();
        using var _paths = paths;

        var document = new HoardDocument { Title = "List" };
        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            new ChecklistFileService(new RealAtomicFileStore()), new ManualTimeProvider(),
            Path.Combine(paths.AppDataRoot, "List.hoard"), document, fileAlreadyExists: false);
        var tab = DocumentTab.ForChecklist("hoard-tab", checklist);
        shell.Tabs.Add(tab);
        shell.ActivateTab(tab);

        // No owner window exists in the headless test host — the editor-tab guard must return before
        // ever touching Application.Current, so this must complete without throwing.
        await ws.SaveAsActiveEditorAsync(CancellationToken.None);
        await ws.SaveAsActiveDocumentAsync(CancellationToken.None);

        await checklist.DisposeAsync();
    }

    [Fact]
    public async Task SaveAsActiveDocumentAsyncIsANoOpWhenNoTabIsActive()
    {
        var (ws, shell, paths) = NewWorkspace();
        using var _paths = paths;

        Assert.Null(shell.ActiveTab);
        await ws.SaveAsActiveDocumentAsync(CancellationToken.None); // must not throw.
    }

    // ---- MainWindowViewModel.SaveAsCommand CanExecute: gated on an active document tab (any kind). ----

    [Fact]
    public void SaveAsCommandCanExecuteReflectsTheActiveTabKind()
    {
        var shell = new MainWindowViewModel();
        Assert.False(shell.SaveAsCommand.CanExecute(null)); // no tabs open.

        EditorViewModel editor = NewEditorVm();
        var editorTab = DocumentTab.ForEditor("e1", TabKind.Scratch, editor);
        shell.Tabs.Add(editorTab);
        shell.ActivateTab(editorTab);
        Assert.True(shell.SaveAsCommand.CanExecute(null));

        var document = new HoardDocument { Title = "List" };
        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            new ChecklistFileService(new CountingAtomicFileStore()), new ManualTimeProvider(),
            "/tmp/hoardpad-test/saveas-canexecute.hoard", document, fileAlreadyExists: false);
        var checklistTab = DocumentTab.ForChecklist("c1", checklist);
        shell.Tabs.Add(checklistTab);
        shell.ActivateTab(checklistTab);
        Assert.True(shell.SaveAsCommand.CanExecute(null));

        shell.ActivateTab(null);
        Assert.False(shell.SaveAsCommand.CanExecute(null));
    }
}
