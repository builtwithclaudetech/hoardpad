// SurfaceFactories.cs — per-tab surface-VM factories (carry-forward A).
//
// The document/tab model mints a FRESH surface VM per opened document (one EditorViewModel per text tab,
// one ChecklistViewModel per .hoard tab — each ChecklistViewModel owns its own serialized write queue,
// so they are session-scoped, never DI singletons, per the M6.1 composition-root note). These thin
// factories close over the shared, stateless services (EditorFileService / registry / catalog /
// ChecklistFileService / TimeProvider) so the WorkspaceService can create a tab without service-locating.

using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Checklist;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.Core.Checklist;

namespace HoardPad.App.Session;

/// <summary>Mints a fresh <see cref="EditorViewModel"/> per opened text document (carry-forward A).</summary>
internal sealed class EditorViewModelFactory
{
    private readonly EditorFileService _fileService;
    private readonly HoardPadRegistryOptions _registry;
    private readonly LanguageCatalog _catalog;

    public EditorViewModelFactory(EditorFileService fileService, HoardPadRegistryOptions registry, LanguageCatalog catalog)
    {
        _fileService = fileService;
        _registry = registry;
        _catalog = catalog;
    }

    /// <summary>A new empty editor VM (a scratch buffer, or the target of a subsequent OpenAsync).</summary>
    public EditorViewModel Create() => new(_fileService, _registry, _catalog);
}

/// <summary>Mints a fresh session-scoped <see cref="ChecklistViewModel"/> per opened <c>.hoard</c> (carry-forward A).</summary>
internal sealed class ChecklistViewModelFactory
{
    private readonly ChecklistFileService _fileService;
    private readonly TimeProvider _time;

    public ChecklistViewModelFactory(ChecklistFileService fileService, TimeProvider time)
    {
        _fileService = fileService;
        _time = time;
    }

    /// <summary>Open an existing <c>.hoard</c> from disk into a fresh VM (FR-30).</summary>
    public Task<ChecklistViewModel> OpenAsync(string path, CancellationToken ct) =>
        ChecklistViewModel.OpenAsync(_fileService, _time, path, ct);

    /// <summary>
    /// Create a fresh empty checklist VM (BUG-10: File ▸ New Checklist). Immediately assigned a real,
    /// uniquified on-disk path (QA2-5) — design §5.6 promises a new checklist "saves automatically" with
    /// no Save-As friction, so <c>path: string.Empty</c> is never handed to the VM (an empty path reaches
    /// <c>Path.GetFullPath</c> inside <c>AtomicFileStore</c> and throws on the very first auto-save,
    /// e.g. the title-rename Enter key). <paramref name="folderRoot"/> is the currently open folder, if
    /// any (<see cref="WorkspaceService.OpenFolder"/>); falls back to the OS Documents folder so a new
    /// checklist always lands somewhere the user can find it even with no folder open.
    /// </summary>
    public ChecklistViewModel CreateNew(string? folderRoot = null)
    {
        var document = new HoardDocument
        {
            Title = "New Checklist",
            Items = new HoardItem[]
            {
                new()
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = string.Empty,
                    Order = 0,
                },
            },
        };
        string path = ResolveNewChecklistPath(folderRoot);
        return ChecklistViewModel.ForDocument(_fileService, _time, path, document, fileAlreadyExists: false);
    }

    /// <summary>
    /// Pick a non-colliding <c>Untitled.hoard</c> / <c>Untitled 2.hoard</c> / ... path under
    /// <paramref name="folderRoot"/> (or the OS Documents folder when no folder is open).
    /// </summary>
    private static string ResolveNewChecklistPath(string? folderRoot)
    {
        string baseDir = !string.IsNullOrEmpty(folderRoot)
            ? folderRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        string candidate = Path.Combine(baseDir, "Untitled.hoard");
        for (int suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(baseDir, $"Untitled {suffix}.hoard");
        return candidate;
    }

    /// <summary>
    /// Wrap recovered <c>.hoard</c> JSON text into a fresh VM (the kept-recovered restore branch). The
    /// recovered JSON is parsed through the pure Core loader (migration/repair); a rejected file falls back
    /// to an empty document so a corrupt recovered buffer never crashes the restore.
    /// </summary>
    /// <param name="path">The <c>.hoard</c> file path (empty for a never-saved buffer).</param>
    /// <param name="recoveredJson">The recovered JSON text from the snapshot.</param>
    /// <param name="fileExistsOnDisk">
    /// Whether the on-disk file exists, as determined by the §9.2 reconcile probe that already ran
    /// (REVISE-7 fix: avoids a synchronous <c>File.Exists</c> on the UI thread; the caller threads the
    /// probe result through from <see cref="RestoredTab.OnDiskFileDeleted"/>).
    /// </param>
    public ChecklistViewModel FromRecoveredText(string path, string recoveredJson, bool fileExistsOnDisk)
    {
        HoardLoadResult result = HoardLoader.Load(recoveredJson);
        HoardDocument document = result.Document ?? new HoardDocument();
        // fileAlreadyExists: true iff the on-disk file is present (the probe told us, REVISE-7). The
        // recovered buffer overlays it so the next write is a REPLACE; a missing file means CREATE.
        return ChecklistViewModel.ForDocument(_fileService, _time, path, document, fileAlreadyExists: fileExistsOnDisk);
    }
}
