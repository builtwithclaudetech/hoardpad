// FindReplaceTestSupport.cs — fakes for the M6.7 find/replace (FR-17/FR-18) gates.
//
// In-memory fakes so the FindReplaceViewModel orchestration + the three FR-17 scopes drive headless with
// no shell / live editor / real filesystem:
//   - FakeBuffer: an in-memory IFindReplaceBuffer (the current-file + all-open scope buffers).
//   - FakeFindReplaceWorkspace: in-memory buffers + an in-memory "folder" map (path -> text). Writes are
//     recorded so a test asserts WHICH files were written (and that an excluded file was NOT).
//     FaultOnWriteN lets a test inject a fault on the Nth WriteFileAsync call (C1 regression).
//   - ScriptedReviewPrompt: records the plan it was shown + returns a scripted confirmed-paths selection
//     (or null = cancel). The in-folder review-before-write is driven without a real modal.
// Class names contain "FindReplace" so the verify filter "FullyQualifiedName~FindReplace" resolves.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Tests;

/// <summary>An in-memory editor buffer for the current-file + all-open scopes.</summary>
internal sealed class FakeBuffer : IFindReplaceBuffer
{
    public FakeBuffer(string name, string text)
    {
        DisplayName = name;
        Text = text;
    }

    public string DisplayName { get; }

    public string Text { get; private set; }

    public string GetText() => Text;

    public void SetText(string text) => Text = text;
}

/// <summary>An in-memory <see cref="IFindReplaceWorkspace"/>: buffers + an in-memory folder map.</summary>
internal sealed class FakeFindReplaceWorkspace : IFindReplaceWorkspace
{
    private readonly Dictionary<string, string> _folderFiles = new(System.StringComparer.Ordinal);
    private int _writeCallCount;

    /// <summary>The paths actually written (the assertion target — an excluded file must NOT appear here).</summary>
    public List<string> WrittenPaths { get; } = [];

    /// <summary>The new text each written path received (so a test can assert the replacement applied).</summary>
    public Dictionary<string, string> WrittenText { get; } = new(System.StringComparer.Ordinal);

    /// <summary>
    /// When set, the Nth call to <see cref="WriteFileAsync"/> throws <see cref="IOException"/> with this
    /// message — used by C1 regression tests to verify the VM surfaces a failure status, not silently swallows.
    /// (1-based: FaultOnWriteN = 1 means the very first write faults.)
    /// </summary>
    public int? FaultOnWriteN { get; set; }

    public FakeBuffer? Current { get; set; }

    public List<FakeBuffer> AllOpen { get; } = [];

    public string? Root { get; set; }

    public IFindReplaceBuffer? CurrentBuffer => Current;

    public IReadOnlyList<IFindReplaceBuffer> OpenBuffers => AllOpen.Cast<IFindReplaceBuffer>().ToList();

    public string? OpenFolderRoot => Root;

    /// <summary>Seed one in-folder file (path -> text).</summary>
    public void AddFolderFile(string path, string text) => _folderFiles[path] = text;

    public Task<string?> ReadFileAsync(string path, CancellationToken ct) =>
        Task.FromResult(_folderFiles.TryGetValue(path, out string? text) ? text : null);

    public IReadOnlyList<string> EnumerateFolderFiles() => _folderFiles.Keys.ToList();

    public Task WriteFileAsync(string path, string text, CancellationToken ct)
    {
        _writeCallCount++;
        if (FaultOnWriteN.HasValue && _writeCallCount == FaultOnWriteN.Value)
            throw new IOException($"Simulated disk fault on write #{_writeCallCount} to '{path}'");

        WrittenPaths.Add(path);
        WrittenText[path] = text;
        _folderFiles[path] = text;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A variant of <see cref="FakeFindReplaceWorkspace"/> whose <c>EnumerateFolderFiles</c> throws
/// <see cref="OperationCanceledException"/> to verify the VM surfaces a "cancelled" <c>StatusMessage</c>
/// rather than letting the exception escape (C1 regression — scan-cancellation path).
/// </summary>
internal sealed class CancellingEnumerateFindReplaceWorkspace : IFindReplaceWorkspace
{
    private readonly Dictionary<string, string> _files = new(System.StringComparer.Ordinal);

    public List<string> WrittenPaths { get; } = [];
    public string? Root { get; set; }

    public IFindReplaceBuffer? CurrentBuffer => null;
    public IReadOnlyList<IFindReplaceBuffer> OpenBuffers => System.Array.Empty<IFindReplaceBuffer>();
    public string? OpenFolderRoot => Root;

    public void AddFolderFile(string path, string text) => _files[path] = text;

    public IReadOnlyList<string> EnumerateFolderFiles() =>
        throw new OperationCanceledException("Simulated scan cancellation");

    public Task<string?> ReadFileAsync(string path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(path, out string? t) ? t : null);

    public Task WriteFileAsync(string path, string text, CancellationToken ct)
    {
        WrittenPaths.Add(path);
        return Task.CompletedTask;
    }
}

/// <summary>A review prompt that records the plan + returns a scripted confirmed-paths selection (or cancel).</summary>
internal sealed class ScriptedReviewPrompt : IFindReplaceReviewPrompt
{
    private readonly System.Func<IReadOnlyList<InFolderFilePlan>, IReadOnlyList<string>?> _decide;

    /// <summary>The plan the prompt was last shown (the review-before-write surface contents).</summary>
    public IReadOnlyList<InFolderFilePlan>? LastPlan { get; private set; }

    public ScriptedReviewPrompt(System.Func<IReadOnlyList<InFolderFilePlan>, IReadOnlyList<string>?> decide) =>
        _decide = decide;

    /// <summary>Confirm ALL files in the plan (the default "Apply with everything checked").</summary>
    public static ScriptedReviewPrompt ConfirmAll() =>
        new(plan => plan.Select(p => p.Path).ToList());

    /// <summary>Cancel the review (return null — nothing is written).</summary>
    public static ScriptedReviewPrompt Cancel() => new(_ => null);

    /// <summary>Confirm only the given paths (exclude the rest — the uncheck path).</summary>
    public static ScriptedReviewPrompt ConfirmOnly(params string[] paths) =>
        new(_ => paths.ToList());

    public Task<IReadOnlyList<string>?> ReviewAsync(IReadOnlyList<InFolderFilePlan> plan, CancellationToken ct)
    {
        LastPlan = plan;
        return Task.FromResult(_decide(plan));
    }
}
