// ChecklistSaveAsTests.cs — QA3-7 regression (Option B: auto-save preserved + additive Save-As).
//
// Proves the safe relocate order at the ChecklistViewModel level (write-quiesce -> write-new-first
// (forced .hoard extension) -> flip _path/_hasPersisted), independently of the UI:
//   - ForceHoardExtension: any requested path is coerced to a REAL `.hoard` file — never the reverse
//     failure mode (a `.hoard` saved under a text extension reopens as garbled text).
//   - RelocateAsync writes the NEW file durably, THEN (and only then) flips _path/_hasPersisted.
//   - The write-quiesce step actually forces a real write of any still-coalescing item-text edit to the
//     OLD path BEFORE the relocate proceeds (proven via CountingAtomicFileStore's write count/bytes, not
//     just the in-memory model — a stray write racing the caller's later old-file delete would be the
//     durability hole this step exists to close).
//   - RelocateAsync itself never deletes the old file (that is WorkspaceService's step 5, deliberately
//     last) — the prior path is returned to the caller instead.
//   - Simulated crash mid-relocate (the new-path write throws): the old file and _path/_hasPersisted stay
//     completely untouched — never a lost list, only a possible harmless orphan on a LATER caller step.
//
// The verify filter matches the class name "ChecklistSaveAs".

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Checklist;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.Core.Checklist;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ChecklistSaveAsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChecklistViewModelFactory _factory;

    public ChecklistSaveAsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoardpad-checklist-saveas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _factory = new ChecklistViewModelFactory(new ChecklistFileService(new AtomicFileStore()), TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        for (int elapsed = 0; elapsed < timeoutMs && !predicate(); elapsed += 5)
            await Task.Delay(5);
        Assert.True(predicate(), "condition did not become true within the timeout");
    }

    // ---- Forced .hoard extension (the QA3-7-reverse failure mode: a .hoard saved under the wrong
    //      extension silently reopens as garbled text). ----

    [Fact]
    public async Task RelocateAppendsHoardExtensionWhenTheRequestedPathHasNone()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: _tempDir);
        string requested = TempPath("MyList");

        string? prior = await checklist.RelocateAsync(requested, CancellationToken.None);

        Assert.Equal(TempPath("MyList.hoard"), checklist.FilePath);
        Assert.True(File.Exists(TempPath("MyList.hoard")));
        _ = prior;
        await checklist.DisposeAsync();
    }

    [Fact]
    public async Task RelocateReplacesAWrongExtensionWithHoard()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: _tempDir);
        string requested = TempPath("MyList.txt");

        await checklist.RelocateAsync(requested, CancellationToken.None);

        Assert.Equal(TempPath("MyList.hoard"), checklist.FilePath);
        Assert.False(File.Exists(TempPath("MyList.txt")), "the wrong-extension name must never be written to disk");
        await checklist.DisposeAsync();
    }

    [Fact]
    public async Task RelocateLeavesACorrectCaseInsensitiveHoardExtensionUnchanged()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: _tempDir);
        string requested = TempPath("MyList.HOARD");

        await checklist.RelocateAsync(requested, CancellationToken.None);

        Assert.Equal(TempPath("MyList.HOARD"), checklist.FilePath);
        Assert.True(File.Exists(TempPath("MyList.HOARD")));
        await checklist.DisposeAsync();
    }

    // ---- write-new-first -> flip: the new file is durable and _path/_hasPersisted flip only afterward. ----

    [Fact]
    public async Task RelocateWritesTheNewFileDurablyThenFlipsThePath()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: _tempDir);
        checklist.SetTitle("Groceries");
        await WaitUntil(() => File.Exists(checklist.FilePath));
        string oldPath = checklist.FilePath!;
        string newPath = TempPath("Relocated.hoard");

        string? prior = await checklist.RelocateAsync(newPath, CancellationToken.None);

        Assert.Equal(newPath, checklist.FilePath);
        Assert.Equal(oldPath, prior);
        Assert.True(File.Exists(newPath), "the new path must be durable on disk");
        HoardDocument? loaded = HoardLoader.Load(await File.ReadAllTextAsync(newPath)).Document;
        Assert.Equal("Groceries", loaded?.Title);
        await checklist.DisposeAsync();
    }

    // ---- RelocateAsync never deletes the old file itself — that is the CALLER's step 5, run last. ----

    [Fact]
    public async Task RelocateDoesNotDeleteThePriorFileItself()
    {
        ChecklistViewModel checklist = _factory.CreateNew(folderRoot: _tempDir);
        checklist.SetTitle("Keep me until the caller says so");
        await WaitUntil(() => File.Exists(checklist.FilePath));
        string oldPath = checklist.FilePath!;

        await checklist.RelocateAsync(TempPath("New.hoard"), CancellationToken.None);

        Assert.True(File.Exists(oldPath), "RelocateAsync must leave the old file for the caller's delete-old-LAST step");
        await checklist.DisposeAsync();
    }

    // ---- A never-persisted document has nothing to delete: RelocateAsync returns null. ----

    [Fact]
    public async Task RelocatingANeverPersistedDocumentReturnsNullPriorPath()
    {
        // ForDocument with fileAlreadyExists:false and no mutation yet — nothing has ever hit disk.
        var document = new HoardDocument { Title = "Untouched" };
        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            new ChecklistFileService(new AtomicFileStore()), TimeProvider.System,
            TempPath("NeverSaved.hoard"), document, fileAlreadyExists: false);

        string? prior = await checklist.RelocateAsync(TempPath("First.hoard"), CancellationToken.None);

        Assert.Null(prior);
        Assert.True(File.Exists(checklist.FilePath));
        await checklist.DisposeAsync();
    }

    // ---- Write-quiesce: a still-coalescing item-text edit is force-written to the OLD path (a real,
    //      observable write — not merely present in the in-memory model) before the relocate proceeds. ----

    [Fact]
    public async Task RelocateQuiescesAPendingCoalescedEditToTheOldPathBeforeRelocating()
    {
        var clock = new ManualTimeProvider();
        var store = new CountingAtomicFileStore();
        var fileService = new ChecklistFileService(store);
        string oldPath = TempPath("Coalescing.hoard");
        var document = new HoardDocument
        {
            Title = "Original",
            Items = [new HoardItem { Id = "i1", Text = "first", Order = 0 }],
        };
        ChecklistViewModel checklist =
            ChecklistViewModel.ForDocument(fileService, clock, oldPath, document, fileAlreadyExists: false);

        checklist.EditText("i1", "edited mid-flight"); // still inside the <=150ms coalesce window — no write yet.
        Assert.Equal(0, store.WriteCount);

        string newPath = TempPath("Relocated.hoard");
        await checklist.RelocateAsync(newPath, CancellationToken.None);

        // Two real writes: the quiesce flush to the OLD path, then write-new-first to the NEW path.
        Assert.Equal(2, store.WriteCount);
        Assert.True(store.Exists(oldPath), "the write-quiesce step must have forced a real write to the OLD path");
        Assert.True(store.Exists(newPath));

        byte[]? oldBytes = store.BytesAt(oldPath);
        Assert.NotNull(oldBytes);
        HoardDocument? oldDoc = HoardLoader.Load(System.Text.Encoding.UTF8.GetString(oldBytes!)).Document;
        Assert.Equal("edited mid-flight", oldDoc?.Items[0].Text);

        await checklist.DisposeAsync();
    }

    // ---- Simulated crash mid-relocate: the new-path write fails -> the old file and _path/_hasPersisted
    //      must be left completely untouched (never a lost list). ----

    [Fact]
    public async Task RelocateLeavesTheOldFileAndPathIntactWhenTheNewWriteFails()
    {
        string oldPath = TempPath("Survivor.hoard");
        string newPath = TempPath("WillFail.hoard");
        var real = new AtomicFileStore();
        var faulting = new FaultingAtomicFileStore(real, newPath);
        var fileService = new ChecklistFileService(faulting);

        ChecklistViewModel checklist = ChecklistViewModel.ForDocument(
            fileService, TimeProvider.System, oldPath, new HoardDocument { Title = "Original" },
            fileAlreadyExists: false);
        checklist.SetTitle("Original persisted"); // real write to oldPath through the real store.
        await WaitUntil(() => File.Exists(oldPath));
        byte[] beforeBytes = await File.ReadAllBytesAsync(oldPath);

        await Assert.ThrowsAsync<IOException>(
            () => checklist.RelocateAsync(newPath, CancellationToken.None));

        Assert.Equal(oldPath, checklist.FilePath); // _path never flipped.
        Assert.True(File.Exists(oldPath), "the old file must survive an aborted relocate");
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(oldPath)); // untouched.
        Assert.False(File.Exists(newPath), "a failed write must never leave a partial new file behind");

        await checklist.DisposeAsync();
    }

    /// <summary>Delegates to a real <see cref="IAtomicFileStore"/> for every path except one poisoned target.</summary>
    private sealed class FaultingAtomicFileStore : IAtomicFileStore
    {
        private readonly IAtomicFileStore _inner;
        private readonly string _poisonedPath;

        public FaultingAtomicFileStore(IAtomicFileStore inner, string poisonedPath)
        {
            _inner = inner;
            _poisonedPath = poisonedPath;
        }

        public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            string.Equals(destPath, _poisonedPath, StringComparison.Ordinal)
                ? throw new IOException("simulated crash mid-relocate")
                : _inner.CreateWhenAbsentAsync(destPath, content, ct);

        public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            string.Equals(destPath, _poisonedPath, StringComparison.Ordinal)
                ? throw new IOException("simulated crash mid-relocate")
                : _inner.ReplaceWhenPresentAsync(destPath, content, ct);

        public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            _inner.AppendRecordAsync(logPath, payload, ct);

        public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct) =>
            _inner.OpenAndRepairAsync(logPath, mode, ct);

        public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct) =>
            _inner.ProbeAtomicCapabilityAsync(destPath, ct);
    }
}
