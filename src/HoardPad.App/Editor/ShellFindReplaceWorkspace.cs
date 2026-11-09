// ShellFindReplaceWorkspace.cs — the production IFindReplaceWorkspace over the live shell + workspace.
//
// Bridges the FindReplaceViewModel's three FR-17 scopes to the real app:
//   - CURRENT FILE : the shell's active text tab → its EditorViewModel buffer.
//   - ALL OPEN     : every open TEXT tab on the shell (.hoard / settings tabs excluded).
//   - IN FOLDER    : the WorkspaceService open-folder root, enumerated recursively; files read off-disk
//                    (UTF-8 text — non-text/binary files are skipped) and written through IAtomicFileStore.
//
// The in-folder write is a USER-initiated disk mutation, NOT the AI sandbox path — so it does NOT route
// through the AI file-ops service / SandboxPolicy. It still writes atomically via IAtomicFileStore (the
// editor's durable write path), and only after the review modal confirms (review-before-write).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Editor;

/// <summary>An editor buffer adapter over an <see cref="EditorViewModel"/> (current-file + all-open scopes).</summary>
internal sealed class EditorViewModelBuffer : IFindReplaceBuffer
{
    private readonly EditorViewModel _editor;

    public EditorViewModelBuffer(EditorViewModel editor) => _editor = editor;

    public string DisplayName => _editor.DocumentName;

    public string GetText() => _editor.Document.Text;

    // ReplaceBufferText marks dirty + raises BufferMutationRequested so a bound EditorView re-mirrors the
    // new text through the AvaloniaEdit undo stack (QA3-5 — a replace must remain undoable via Ctrl+Z).
    public void SetText(string text) => _editor.ReplaceBufferText(text);
}

/// <summary>The production scope seam: the live shell tab set + the open-folder root + the atomic store.</summary>
internal sealed class ShellFindReplaceWorkspace : IFindReplaceWorkspace
{
    // UTF-8 (no BOM) — the editor's default buffer encoding (NFR-9). An in-folder bulk text replace operates
    // on UTF-8 text; a file that does not decode cleanly as UTF-8 is skipped (binary / other-encoding files
    // are out of scope for a bulk text replace, design §3.5 shows source files). Per-file encoding detection
    // is a per-document concern (the IEncodingDetector seam), not a bulk-replace concern.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly MainWindowViewModel _shell;
    private readonly WorkspaceService _workspace;
    private readonly IAtomicFileStore _store;

    public ShellFindReplaceWorkspace(MainWindowViewModel shell, WorkspaceService workspace, IAtomicFileStore store)
    {
        _shell = shell;
        _workspace = workspace;
        _store = store;
    }

    public IFindReplaceBuffer? CurrentBuffer =>
        _shell.ActiveTab?.Editor is { } editor ? new EditorViewModelBuffer(editor) : null;

    public IReadOnlyList<IFindReplaceBuffer> OpenBuffers =>
        _shell.Tabs
            .Where(t => t.Editor is not null)
            .Select(t => (IFindReplaceBuffer)new EditorViewModelBuffer(t.Editor!))
            .ToList();

    public string? OpenFolderRoot => _workspace.OpenFolder;

    public async Task<string?> ReadFileAsync(string path, CancellationToken ct)
    {
        // Read off the UI thread (mirrors EditorFileService). A file that is not valid UTF-8 text throws
        // DecoderFallbackException → return null (skip it): a bulk text replace never touches binary files.
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null; // locked / unreadable file — skip it rather than fail the whole in-folder run.
        }
        catch (System.UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return Utf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null; // not UTF-8 text (binary / other encoding) — skip.
        }
    }

    public IReadOnlyList<string> EnumerateFolderFiles()
    {
        string? root = _workspace.OpenFolder;
        if (root is null || !Directory.Exists(root))
            return System.Array.Empty<string>();

        // Recursive (FR-17 in-folder is recursive). EnumerationOptions skips reparse points so a symlink
        // loop cannot run away; hidden/system files are included (a user replace is over the whole tree).
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        return Directory.EnumerateFiles(root, "*", options).ToList();
    }

    public async Task WriteFileAsync(string path, string text, CancellationToken ct)
    {
        // Atomic write through the store (FR-18 / NFR-4) — never raw File.WriteAllText. The file already
        // exists (it was read + matched), so ReplaceWhenPresentAsync is the correct atomic-rename leg.
        ReadOnlyMemory<byte> content = Utf8NoBom.GetBytes(text);
        await _store.ReplaceWhenPresentAsync(path, content, ct).ConfigureAwait(false);
    }
}
