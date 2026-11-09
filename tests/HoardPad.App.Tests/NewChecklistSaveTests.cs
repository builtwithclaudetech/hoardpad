// NewChecklistSaveTests.cs — QA2-5 regression: renaming a brand-new (never-saved) `.hoard` checklist must
// not throw "The value cannot be an empty string. (Parameter 'path')".
//
// Root cause (2026-06-30 QA2 finding): ChecklistViewModelFactory.CreateNew() minted a ChecklistViewModel
// with path: string.Empty. Design §5.6 promises a new checklist "saves automatically" — the very first
// auto-save (a title rename, an added item, or any terminal mutation) enqueued a write through the write
// queue, which called ChecklistFileService.SaveAsync("", ...) -> AtomicFileStore.AtomicWrite ->
// Path.GetFullPath("") -> ArgumentException("The value cannot be an empty string. (Parameter 'path')"),
// caught by HoardWriteQueue and surfaced as ChecklistViewModel.SaveFailedMessage.
//
// Fix: CreateNew() now resolves a real, uniquified on-disk path (Untitled.hoard / Untitled 2.hoard / ...)
// under the open folder (if any) or the OS Documents folder, so every subsequent auto-save has a valid
// target. This test uses the REAL Host AtomicFileStore against a real temp directory (the in-memory
// CountingAtomicFileStore fake used elsewhere in this test class never reproduces the empty-path throw,
// since it just keys a dictionary by string) — the same pattern as OpenSaveTests. The verify filter
// matches the class name "NewChecklistSave".

using System;
using System.IO;
using System.Threading.Tasks;
using HoardPad.App.Checklist;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.Host;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class NewChecklistSaveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChecklistViewModelFactory _factory;

    public NewChecklistSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoardpad-newchecklist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _factory = new ChecklistViewModelFactory(new ChecklistFileService(new AtomicFileStore()), TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        for (int elapsed = 0; elapsed < timeoutMs && !predicate(); elapsed += 5)
            await Task.Delay(5);
        Assert.True(predicate(), "condition did not become true within the timeout");
    }

    // ---- QA2-5: renaming a new checklist must not throw / must not surface SaveFailedMessage. ----

    [Fact]
    public async Task RenamingANewChecklistPersistsWithoutThrowingOrFailing()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: _tempDir);

        // The reported repro: change the name and hit Enter (SetTitle is the Enter/LostFocus handler).
        checklist.SetTitle("My Renamed Checklist");

        // Give the ≤150ms coalesced write queue time to flush.
        await Task.Delay(250);

        Assert.Null(checklist.SaveFailedMessage);
        Assert.True(File.Exists(checklist.FilePath), "the renamed checklist must have been persisted to a real file");

        await checklist.DisposeAsync();
    }

    // ---- CreateNew assigns a real path under the open folder — never empty. ----

    [Fact]
    public void CreateNewAssignsARealNonEmptyPathUnderTheOpenFolder()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: _tempDir);

        Assert.False(string.IsNullOrEmpty(checklist.FilePath));
        Assert.Equal(Path.Combine(_tempDir, "Untitled.hoard"), checklist.FilePath);
    }

    // ---- Uniquification: a second new checklist in the same folder doesn't collide with the first. ----

    [Fact]
    public async Task SecondNewChecklistInTheSameFolderGetsAUniquifiedName()
    {
        ChecklistViewModel first = _factory.CreateNew(folderRoot: _tempDir);
        first.SetTitle("First");
        await WaitUntil(() => File.Exists(first.FilePath));

        ChecklistViewModel second = _factory.CreateNew(folderRoot: _tempDir);

        Assert.Equal(Path.Combine(_tempDir, "Untitled 2.hoard"), second.FilePath);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    // ---- No open folder: falls back to the OS Documents folder (never an empty path). ----

    [Fact]
    public void CreateNewWithNoOpenFolderFallsBackToDocumentsFolder()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: null);

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.False(string.IsNullOrEmpty(checklist.FilePath));
        Assert.StartsWith(documents, checklist.FilePath!, StringComparison.Ordinal);
    }
}
