// FindReplaceWorkspace.cs — the scope seam the FindReplaceViewModel drives (FR-17 three scopes).
//
// Abstracts the three FR-17 scopes away from the VM so the replace orchestration unit-tests without the
// shell / WorkspaceService / live editor control:
//   - CURRENT FILE : the active editor buffer (get text + replace-all in place).
//   - ALL OPEN     : every open TEXT tab's buffer (the shell's tab set, .hoard tabs excluded).
//   - IN FOLDER    : the open-folder root (recursive), read off-disk, written through IAtomicFileStore.
//
// This is a USER-initiated multi-file edit, NOT the AI sandbox path — so it does NOT route through the
// AI file-ops service / SandboxPolicy (M6.7 constraint). But the in-folder write IS a disk mutation:
// it goes through IAtomicFileStore (the same atomic write path the editor uses), never raw File.Write,
// and only AFTER the review modal confirms (review-before-write, FR-18 / OQ-2).

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HoardPad.App.Editor;

/// <summary>One open text buffer the all-open scope replaces over (the tab's display name + a get/set seam).</summary>
internal interface IFindReplaceBuffer
{
    /// <summary>The tab's display name (for the all-open result summary / per-buffer reporting).</summary>
    string DisplayName { get; }

    /// <summary>The current buffer text.</summary>
    string GetText();

    /// <summary>Replace the buffer text in place (re-mirrors into the live editor where one is bound).</summary>
    void SetText(string text);
}

/// <summary>One in-folder file's review plan: its path, display label, and the match count found in it.</summary>
internal readonly record struct InFolderFilePlan(string Path, string DisplayLabel, int MatchCount);

/// <summary>The seam giving the VM access to the three FR-17 scopes (production wraps the shell + workspace).</summary>
internal interface IFindReplaceWorkspace
{
    /// <summary>The active editor buffer (current-file scope), or null when no text document is active.</summary>
    IFindReplaceBuffer? CurrentBuffer { get; }

    /// <summary>Every open TEXT buffer (all-open scope). Excludes <c>.hoard</c> / settings tabs.</summary>
    IReadOnlyList<IFindReplaceBuffer> OpenBuffers { get; }

    /// <summary>The open-folder root (in-folder scope), or null when no folder is open (the scope is then disabled).</summary>
    string? OpenFolderRoot { get; }

    /// <summary>Read a file's text (off the UI thread). Returns null when the file cannot be read as text.</summary>
    Task<string?> ReadFileAsync(string path, CancellationToken ct);

    /// <summary>Enumerate the in-folder root recursively (the candidate files for an in-folder replace).</summary>
    IReadOnlyList<string> EnumerateFolderFiles();

    /// <summary>Atomically write a file's new text through <c>IAtomicFileStore</c> (FR-18 / NFR-4 — never raw File.Write).</summary>
    Task WriteFileAsync(string path, string text, CancellationToken ct);
}
