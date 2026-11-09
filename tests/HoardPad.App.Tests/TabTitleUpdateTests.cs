// TabTitleUpdateTests.cs — QA3-12/QA3-13: the tab-strip header must re-evaluate DisplayName when the
// hosted surface VM's own name changes post-construction (Save-As assigning a path to an untitled editor
// tab; a checklist rename). Both bugs share one root cause: DocumentTab.DisplayName was a computed
// projection with no PropertyChanged forwarding from the hosted VM — see DocumentTab.cs's constructor
// comment for the fix. These tests assert the DocumentTab itself raises PropertyChanged(DisplayName) and
// that the value reflects the new name, mirroring the MainWindow.axaml tab chip's `{Binding DisplayName}`
// binding, which targets the DocumentTab instance directly.
//
// All class names contain "TabTitleUpdate" so the verify filter resolves them.

using System.Collections.Generic;
using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.Core.Checklist;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class TabTitleUpdateTests
{
    // ---- QA3-13: renaming a checklist updates the tab header. ----

    [Fact]
    public void RenamingAChecklistRaisesDisplayNamePropertyChangedOnTheTab()
    {
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var doc = new HoardDocument
        {
            Title = "Old Title",
            Settings = new HoardSettings { CheckedItemMode = "strikethrough-in-place" },
            Items = [],
        };
        var checklistVm = ChecklistViewModel.ForDocument(
            new ChecklistFileService(store), clock, "/tmp/hoardpad-test/rename.hoard", doc);
        DocumentTab tab = DocumentTab.ForChecklist("t1", checklistVm);
        Assert.Equal("Old Title", tab.DisplayName);

        var raisedProperties = new List<string?>();
        tab.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        checklistVm.SetTitle("New Title");

        Assert.Contains(nameof(DocumentTab.DisplayName), raisedProperties);
        Assert.Equal("New Title", tab.DisplayName);
    }

    [Fact]
    public void RepeatedChecklistRenamesEachRaiseDisplayNamePropertyChangedOnTheTab()
    {
        // Proves the forwarding subscription wired in DocumentTab's constructor is not a one-shot: a
        // SECOND rename after the first must also re-raise (the tab header must keep tracking, not just
        // catch up once).
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var doc = new HoardDocument
        {
            Title = "First Title",
            Settings = new HoardSettings { CheckedItemMode = "strikethrough-in-place" },
            Items = [],
        };
        var checklistVm = ChecklistViewModel.ForDocument(
            new ChecklistFileService(store), clock, "/tmp/hoardpad-test/rename-twice.hoard", doc);
        DocumentTab tab = DocumentTab.ForChecklist("t1", checklistVm);

        checklistVm.SetTitle("Second Title");
        var raisedProperties = new List<string?>();
        tab.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        checklistVm.SetTitle("Third Title");

        Assert.Contains(nameof(DocumentTab.DisplayName), raisedProperties);
        Assert.Equal("Third Title", tab.DisplayName);
    }

    // ---- QA3-12 (shared root cause): Save-As assigning a path to an untitled/new document updates the
    //      tab header. ----

    [Fact]
    public void SavingANewEditorDocumentRaisesDisplayNamePropertyChangedOnTheTab()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new CountingAtomicFileStore(),
            new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorVm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
        DocumentTab tab = DocumentTab.ForEditor("t2", TabKind.Scratch, editorVm);
        Assert.Equal("untitled", tab.DisplayName);

        var raisedProperties = new List<string?>();
        tab.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        // Mirrors WorkspaceService's Save-As sequence (see WorkspaceService.cs lines ~456-457/578-579):
        // assign the new on-disk path, then re-sync the VM's display segments from the document.
        editorVm.Document.Path = "/tmp/hoardpad-test/new-file.txt";
        editorVm.SyncSegmentsFromDocument();

        Assert.Contains(nameof(DocumentTab.DisplayName), raisedProperties);
        Assert.Equal("new-file.txt", tab.DisplayName);
    }
}
