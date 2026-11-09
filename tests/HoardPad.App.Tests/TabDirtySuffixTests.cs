// TabDirtySuffixTests.cs — FEAT-13: the tab-strip header must show a trailing " *" (FR-5) while the
// hosted editor document has unsaved changes, and drop it the instant the document is saved. This reuses
// the QA3-12/QA3-13 DocumentTab forwarding mechanism (see DocumentTab.cs's constructor comment): the tab
// subscribes to the hosted EditorViewModel's PropertyChanged and now also forwards IsDirty flips as a
// DisplayName re-evaluation, exactly like it already does for DocumentName/DisplayTitle renames.
//
// These are interaction-layer tests: they drive the VM through the same calls the real UI does (an
// in-session buffer mutation via ReplaceBufferText — mirroring what the bound EditorView invokes on a
// keystroke — then a real SaveAsync through an in-memory IAtomicFileStore), not a direct backing-field
// poke, so the suffix is proven to toggle from the same edit/save path the live app exercises.
//
// Class name contains "Tab" so the FEAT-13 verify filter (`FullyQualifiedName~Tab`) resolves it.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class TabDirtySuffixTests
{
    private static EditorViewModel CreateEditorViewModel(string path)
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new CountingAtomicFileStore(),
            new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorVm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
        editorVm.Document.Path = path;
        editorVm.SyncSegmentsFromDocument();
        return editorVm;
    }

    [Fact]
    public void EditingTheBufferAppendsTheDirtySuffixToTheTabDisplayName()
    {
        EditorViewModel editorVm = CreateEditorViewModel("/tmp/hoardpad-test/feat13-edit.txt");
        DocumentTab tab = DocumentTab.ForEditor("t1", TabKind.File, editorVm);
        Assert.Equal("feat13-edit.txt", tab.DisplayName);

        var raisedProperties = new List<string?>();
        tab.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        // Mirrors the real edit path (EditorView -> ReplaceBufferText for an in-session mutation).
        editorVm.ReplaceBufferText("edited content");

        Assert.Contains(nameof(DocumentTab.DisplayName), raisedProperties);
        Assert.Equal("feat13-edit.txt *", tab.DisplayName);
    }

    [Fact]
    public async Task SavingTheDocumentRemovesTheDirtySuffixFromTheTabDisplayName()
    {
        EditorViewModel editorVm = CreateEditorViewModel("/tmp/hoardpad-test/feat13-save.txt");
        DocumentTab tab = DocumentTab.ForEditor("t2", TabKind.File, editorVm);
        editorVm.ReplaceBufferText("edited content");
        Assert.Equal("feat13-save.txt *", tab.DisplayName);

        var raisedProperties = new List<string?>();
        tab.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        await editorVm.SaveAsync(CancellationToken.None);

        Assert.Contains(nameof(DocumentTab.DisplayName), raisedProperties);
        Assert.Equal("feat13-save.txt", tab.DisplayName);
    }

    [Fact]
    public void ANewlyOpenedDocumentTabShowsNoDirtySuffix()
    {
        EditorViewModel editorVm = CreateEditorViewModel("/tmp/hoardpad-test/feat13-clean.txt");
        DocumentTab tab = DocumentTab.ForEditor("t3", TabKind.File, editorVm);

        Assert.False(editorVm.IsDirty);
        Assert.Equal("feat13-clean.txt", tab.DisplayName);
        Assert.DoesNotContain('*', tab.DisplayName);
    }

    [Fact]
    public void ChecklistTabsNeverShowADirtySuffixEvenThoughTheyHaveNoIsDirtyConcept()
    {
        // .hoard auto-saves on every mutation (M6.3) — a checklist tab is never "unsaved" in the FR-5
        // sense, so DisplayName must never append " *" for one, regardless of Editor-side dirty logic.
        var store = new CountingAtomicFileStore();
        var clock = new ManualTimeProvider();
        var doc = new HoardPad.Core.Checklist.HoardDocument
        {
            Title = "Groceries",
            Settings = new HoardPad.Core.Checklist.HoardSettings { CheckedItemMode = "strikethrough-in-place" },
            Items = [],
        };
        var checklistVm = HoardPad.App.ViewModels.ChecklistViewModel.ForDocument(
            new HoardPad.App.Checklist.ChecklistFileService(store), clock, "/tmp/hoardpad-test/feat13.hoard", doc);
        DocumentTab tab = DocumentTab.ForChecklist("t4", checklistVm);

        Assert.Equal("Groceries", tab.DisplayName);
        Assert.DoesNotContain('*', tab.DisplayName);
    }
}
