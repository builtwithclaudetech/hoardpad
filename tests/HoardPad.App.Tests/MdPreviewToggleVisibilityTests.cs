// MdPreviewToggleVisibilityTests.cs — QA3-15: the toolbar "MD Preview" toggle (bound to
// MainWindowViewModel.IsMarkdown, MainWindow.axaml `IsVisible="{Binding IsMarkdown}"`) stayed visible after
// the active tab stopped being a Markdown document.
//
// Root cause: MainWindowViewModel.ActivateTab only re-pointed the shell's `Editor` property (the source
// IsMarkdown mirrors) inside the "activated tab is a plain text/scratch document" branch. Switching to a
// `.hoard` checklist tab, the settings tab, or closing the last tab left `Editor` — and therefore IsMarkdown
// — stuck on whatever text tab was active before, so the toggle kept showing for a document that was no
// longer the active one (or when there was no active document at all).
//
// Fix: ActivateTab now re-points (or clears) Editor unconditionally for every activation, before branching
// on tab kind. These tests drive the real MainWindowViewModel.ActivateTab/CloseTabCommand exactly as the
// tab strip does, and assert IsMarkdown flips at each activation boundary — non-vacuous: reverting the fix
// (restoring the old conditional-only assignment) turns AtCheckedTest and CloseLastTab red.

using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.Core.Checklist;
using HoardPad.Core.Recovery;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class MdPreviewToggleVisibilityTests
{
    [Fact]
    public void ActivatingAMarkdownTabShowsTheToggle()
    {
        var shell = new MainWindowViewModel();
        DocumentTab markdown = MakeEditorTab("notes.md", "Markdown");
        shell.Tabs.Add(markdown);

        shell.ActivateTab(markdown);

        Assert.True(shell.IsMarkdown);
    }

    [Fact]
    public void SwitchingFromAMarkdownTabToAPlainTextTabHidesTheToggle()
    {
        var shell = new MainWindowViewModel();
        DocumentTab markdown = MakeEditorTab("notes.md", "Markdown");
        DocumentTab plain = MakeEditorTab("readme.txt", "Plain Text");
        shell.Tabs.Add(markdown);
        shell.Tabs.Add(plain);
        shell.ActivateTab(markdown);
        Assert.True(shell.IsMarkdown); // sanity: starts visible.

        shell.ActivateTab(plain);

        Assert.False(shell.IsMarkdown);
    }

    // ---- The QA3-15 regression: switching to a NON-text tab (a .hoard checklist) left IsMarkdown mirroring
    // the previously-active Markdown editor, because ActivateTab's checklist branch never touched Editor. ----

    [Fact]
    public void SwitchingFromAMarkdownTabToAChecklistTabHidesTheToggle()
    {
        var shell = new MainWindowViewModel();
        DocumentTab markdown = MakeEditorTab("notes.md", "Markdown");
        DocumentTab checklist = MakeChecklistTab();
        shell.Tabs.Add(markdown);
        shell.Tabs.Add(checklist);
        shell.ActivateTab(markdown);
        Assert.True(shell.IsMarkdown); // sanity: starts visible.

        shell.ActivateTab(checklist);

        Assert.False(shell.IsMarkdown); // the checklist tab is not Markdown — toggle must hide.
    }

    // ---- The other QA3-15 clause: closing the last (Markdown) tab left no active tab, but Editor — and
    // IsMarkdown — stayed pointed at the closed tab's editor VM. ----

    [Fact]
    public void ClosingTheOnlyMarkdownTabHidesTheToggle()
    {
        var shell = new MainWindowViewModel();
        DocumentTab markdown = MakeEditorTab("notes.md", "Markdown");
        shell.Tabs.Add(markdown);
        shell.ActivateTab(markdown);
        Assert.True(shell.IsMarkdown); // sanity: starts visible.

        shell.CloseTabCommand.Execute(markdown);

        Assert.Null(shell.ActiveTab);
        Assert.False(shell.IsMarkdown); // no active document — the toggle must not show.
    }

    // ---- Round trip: reactivating a Markdown tab after a non-Markdown activation shows the toggle again. ----

    [Fact]
    public void ReactivatingAMarkdownTabAfterAChecklistTabShowsTheToggleAgain()
    {
        var shell = new MainWindowViewModel();
        DocumentTab markdown = MakeEditorTab("notes.md", "Markdown");
        DocumentTab checklist = MakeChecklistTab();
        shell.Tabs.Add(markdown);
        shell.Tabs.Add(checklist);
        shell.ActivateTab(markdown);
        shell.ActivateTab(checklist);
        Assert.False(shell.IsMarkdown);

        shell.ActivateTab(markdown);

        Assert.True(shell.IsMarkdown);
    }

    // ----- Helpers -----

    private static DocumentTab MakeEditorTab(string fileName, string languageLabel)
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new CountingAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var catalog = new LanguageCatalog(registry);
        var doc = new EditorDocument { Path = fileName, Text = string.Empty };
        (string? scope, string resolvedLabel) = catalog.Resolve(fileName);
        doc.GrammarScope = scope;
        doc.LanguageLabel = languageLabel.Length > 0 ? languageLabel : resolvedLabel;
        var editor = new EditorViewModel(fileService, registry, catalog, doc);
        editor.SyncSegmentsFromDocument();
        return DocumentTab.ForEditor(fileName, TabKind.File, editor);
    }

    private static DocumentTab MakeChecklistTab()
    {
        var doc = new HoardDocument
        {
            Title = "List",
            Settings = new HoardSettings { CheckedItemMode = "strikethrough-in-place" },
            Items = [new HoardItem { Id = "a", Text = "a", ParentId = null, Order = 0 }],
        };
        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            new ChecklistFileService(new CountingAtomicFileStore()),
            new ManualTimeProvider(), "/tmp/hoardpad-test/mdpreview.hoard", doc);
        return DocumentTab.ForChecklist("mdpreview.hoard", checklist);
    }
}
