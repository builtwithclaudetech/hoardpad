// FolderTreeNode.cs — a folder-tree row (QA3-2, design §9.5). Wraps one file-system entry (a file or a
// directory) under the open-folder root. The root row's children are the open folder's IMMEDIATE
// contents, supplied eagerly by the caller (WorkspaceService.PopulateFolderTreeAsync); every other
// directory row LAZY-loads its real children off the UI thread the first time it is expanded (NFR-1 — a
// sync Directory.Enumerate* tree walk must never run on the dispatcher thread, mirrors
// FindReplaceViewModel.ReviewInFolderAsync / ShellFindReplaceWorkspace.EnumerateFolderFiles).
//
// A directory row that has not yet loaded its real children carries one placeholder child so the
// TreeView renders an expander arrow before the real contents are known — Avalonia only shows an
// expander when the bound Children collection is non-empty at container generation.
//
// QA3-2 REVISE (hostile live review, CRITICAL): a large directory (~20k entries) froze the UI for 6-8s.
// The off-thread ENUMERATION was already correct; the freeze was thousands of individual bound
// ObservableCollection.Add calls back on the UI thread, each forcing the TreeView to synchronously
// generate/measure/arrange one more container. BulkObservableCollection.ReplaceAll below replaces that
// with ONE Reset notification (a single generation pass) for both the eager root populate and the lazy
// per-row load.
//
// That Reset fix alone was NECESSARY but not SUFFICIENT: live re-verification after the Reset fix (a real
// rendered TreeView, real CompositionRoot, a real 12,000-entry directory) measured the container
// generation/layout pass ITSELF at ~1ms/realized row REGARDLESS of Add-vs-Reset — Avalonia's TreeView does
// not virtualize a nested level's rows here, so realizing N rows costs O(N) UI-thread time no matter how
// the collection is populated. A single Reset over an unbounded N can therefore still block the dispatcher
// (and any other queued input) for seconds on a pathological directory (node_modules, .git/objects,
// Downloads). MaxChildrenPerDirectory below bounds the REALIZED row count to a constant regardless of the
// real directory's size — the off-thread enumeration still walks/sorts the full directory (cheap, ~tens of
// ms even at 12k entries) so the reported total is accurate, but only the first
// MaxChildrenPerDirectory entries are ever handed to the UI-bound collection, plus one synthetic
// "…N more" notice row when the real count exceeds the cap.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HoardPad.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace its entire contents in ONE
/// <see cref="NotifyCollectionChangedAction.Reset"/> notification instead of N per-item Add
/// notifications (QA3-2 CRITICAL fix — see FolderTreeNode remarks above). A bound <c>ItemsControl</c>
/// reacts to Reset with a single container-generation pass rather than one synchronous pass PER item.
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replace every item in one shot, raising a single Reset (not one notification per item).</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear(); // Collection<T>.Items — the raw backing list; bypasses per-item notifications.
        foreach (T item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// One row in the folder-tree pane (QA3-2). Internal — compiled XAML in this assembly binds it (the
/// same posture as <see cref="HoardPad.App.Session.DocumentTab"/>).
/// </summary>
internal sealed class FolderTreeNode : ObservableObject
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // a symlink loop must never hang a lazy expand.
    };

    /// <summary>
    /// QA3-2 CRITICAL fix (hostile live review): the maximum number of REAL rows handed to the bound
    /// TreeView per directory. Live-measured at ~1ms of UI-thread layout per realized row (Avalonia does
    /// not virtualize this nested level), so an unbounded directory (a real node_modules/.git/objects can
    /// have 10,000s of entries) would still block the dispatcher for seconds regardless of the Reset fix
    /// above. 500 keeps a single expand's realized-row layout cost in the low hundreds of ms — legitimate
    /// source-tree directories essentially never exceed this; anything larger gets the first 500 (sorted,
    /// directories-first) plus a "…N more" notice rather than an unbounded realized set.
    /// </summary>
    internal const int MaxChildrenPerDirectory = 500;

    /// <summary>The transient "still loading" row label — distinct from the permanent "…N more" truncation
    /// notice (both are <see cref="IsPlaceholder"/>, but only this one is replaced once the load completes).</summary>
    internal const string LoadingLabel = "Loading…";

    private bool _isExpanded;
    private bool _childrenLoaded;

    /// <summary>The row label — the entry's file/directory name. For the open-folder root row (design §9.5
    /// — the root row marks the sandbox boundary), the caller (<see cref="Session.WorkspaceService"/>'s
    /// <c>RootFolderDisplayName</c>) passes the root's own leaf name (QA4-6 — was the literal placeholder
    /// <c>"(root)"</c>), so the pane shows which folder is actually open rather than a generic tag.</summary>
    public string Name { get; }

    /// <summary>The absolute on-disk path this row represents (empty for the "Loading…" placeholder row).</summary>
    public string FullPath { get; }

    /// <summary>Whether this row is a directory (drives the expander + the lazy-load trigger).</summary>
    public bool IsDirectory { get; }

    /// <summary>
    /// <c>true</c> for a SYNTHETIC row that is not a real file-system entry — the "Loading…" row a
    /// directory carries until its real children load, or the "…N more" truncation-notice row appended
    /// when a directory exceeds <see cref="MaxChildrenPerDirectory"/> (QA3-2 REVISE — cs-code-review). Both
    /// click-to-open call sites (<see cref="MainWindowViewModel.OpenFolderTreeItemCommand"/> and the
    /// folder-tree DoubleTapped handler) must exclude rows with this flag: they carry an empty
    /// <see cref="FullPath"/>, so treating one like a real file resolves to the current directory and
    /// throws (a silent, unobserved fire-and-forget failure without this flag).
    /// </summary>
    public bool IsPlaceholder { get; }

    /// <summary>This row's children — eagerly supplied for the root, lazily loaded for every other directory.</summary>
    public BulkObservableCollection<FolderTreeNode> Children { get; } = [];

    /// <summary>
    /// Two-way bound to the generated <c>TreeViewItem.IsExpanded</c> (MainWindow.axaml style Setter). The
    /// first time a directory row expands, this triggers the off-thread lazy load of its real children.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
                return;
            if (value && IsDirectory && !_childrenLoaded)
                _ = LoadChildrenAsync();
        }
    }

    /// <summary>
    /// Construct a row. <paramref name="eagerChildren"/> supplies already-known children (the root row);
    /// leave <c>null</c> for a plain directory row, which gets one placeholder child (so the expander
    /// arrow shows) until the user expands it.
    /// </summary>
    public FolderTreeNode(string name, string fullPath, bool isDirectory, IReadOnlyList<FolderTreeNode>? eagerChildren = null)
        : this(name, fullPath, isDirectory, eagerChildren, isPlaceholder: false)
    {
    }

    private FolderTreeNode(
        string name, string fullPath, bool isDirectory, IReadOnlyList<FolderTreeNode>? eagerChildren, bool isPlaceholder)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;

        if (eagerChildren is not null)
        {
            Children.ReplaceAll(eagerChildren); // QA3-2 CRITICAL fix — one Reset, not N Adds.
            _childrenLoaded = true;
        }
        else if (isDirectory)
        {
            Children.Add(new FolderTreeNode(LoadingLabel, string.Empty, isDirectory: false, eagerChildren: null, isPlaceholder: true));
        }
    }

    private async Task LoadChildrenAsync()
    {
        _childrenLoaded = true; // set before the await — a second rapid expand must not double-fire.

        // EnumerateImmediateChildren is fully self-contained (it catches IOException/UnauthorizedAccessException
        // internally and returns empty rather than throwing — see its remarks), so no additional try/catch
        // boundary is needed here; a bare `_ = LoadChildrenAsync()` fire-and-forget is safe by construction.
        IReadOnlyList<FolderTreeNode> children =
            await Task.Run(() => EnumerateImmediateChildren(FullPath)).ConfigureAwait(true);

        // QA3-2 CRITICAL fix: one Reset (ReplaceAll) instead of Clear() + a per-item Add loop — a large
        // directory (~20k entries) froze the UI for 6-8s under the old per-Add pattern (each Add forced a
        // synchronous TreeView container generation/measure/arrange pass).
        Children.ReplaceAll(children);
    }

    /// <summary>
    /// Build the immediate (non-recursive) children of <paramref name="directoryPath"/>: directories
    /// first, then files, both alphabetical. <b>Off-thread caller contract (NFR-1):</b> this method is
    /// itself a plain synchronous tree walk — callers must invoke it via <c>Task.Run</c>, never directly
    /// from the dispatcher thread. Returns empty for a path that no longer exists or is inaccessible —
    /// never throws (every I/O exception this walk can raise is caught below). The full directory is
    /// still walked and sorted (cheap — tens of ms even at 10,000+ entries) so the reported total is
    /// accurate, but the returned list is capped at <see cref="MaxChildrenPerDirectory"/> REAL rows plus
    /// one synthetic "…N more" notice row when the true count exceeds the cap (QA3-2 CRITICAL fix — see
    /// the file-level remarks).
    /// </summary>
    public static IReadOnlyList<FolderTreeNode> EnumerateImmediateChildren(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return Array.Empty<FolderTreeNode>();

        try
        {
            List<FolderTreeNode> directories = Directory.EnumerateDirectories(directoryPath, "*", Options)
                .Select(d => new FolderTreeNode(Path.GetFileName(d), d, isDirectory: true))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<FolderTreeNode> files = Directory.EnumerateFiles(directoryPath, "*", Options)
                .Select(f => new FolderTreeNode(Path.GetFileName(f), f, isDirectory: false))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int total = directories.Count + files.Count;
            List<FolderTreeNode> capped = directories.Concat(files).Take(MaxChildrenPerDirectory).ToList();
            if (total > MaxChildrenPerDirectory)
                capped.Add(CreateTruncationNotice(total - MaxChildrenPerDirectory));

            return capped;
        }
        catch (IOException)
        {
            return Array.Empty<FolderTreeNode>(); // unreadable at the moment of the walk — show empty.
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<FolderTreeNode>();
        }
    }

    /// <summary>The synthetic "…N more" row appended when a directory exceeds <see cref="MaxChildrenPerDirectory"/>.</summary>
    private static FolderTreeNode CreateTruncationNotice(int omittedCount) =>
        new($"…{omittedCount:N0} more item{(omittedCount == 1 ? "" : "s")} not shown (directory too large)",
            string.Empty, isDirectory: false, eagerChildren: null, isPlaceholder: true);
}
