// FolderRootResolver.cs — FS-1: the single Folder + AI-write root, recomputed every launch (Decision A,
// FINAL, docs/tasklist.md "FS-1"). No open-folder is persisted separately anymore (retires the MA5
// session.json openFolder/canonicalOpenFolder reconciliation as the production root-of-truth, prd.md
// §9.2) — every launch resolves ONE root via this exact 3-tier order:
//   (i)   a file passed in (double-click / "Open With")  -> its containing folder.
//   (ii)  no file passed, a restored tab has a saved path -> the FIRST such tab's folder, walked in the
//         tabs' CURRENT restore order (scratch/untitled tabs are skipped).
//   (iii) neither                                         -> the user's My Documents folder (see
//         MyDocumentsResolver); the folder-tree pane stays collapsed until the user summons it.
//
// Pure function: no I/O of its own. The My-Documents lookup is injected as a lazy delegate so it is only
// ever invoked when tier (iii) genuinely fires, and so tests can script it without touching a real
// environment/filesystem.
//
// NOTE (FS-2 dependency): tier (ii) trusts whatever order restoredTabsInOrder is given — it does NOT
// re-derive an order of its own. FS-2 owns guaranteeing that restore order equals save order (no
// set-hashing); this function's correctness for tier (ii) depends on the caller supplying that order.

using System.IO;

namespace HoardPad.App.Session;

/// <summary>Which FS-1 tier produced a <see cref="FolderRootResolution"/>.</summary>
public enum FolderRootTier
{
    /// <summary>Tier (i): a file was passed in (double-click / "Open With").</summary>
    PassedFile,

    /// <summary>Tier (ii): derived from the first restored tab that has a saved on-disk path.</summary>
    RestoredTab,

    /// <summary>Tier (iii): neither of the above — the My Documents fallback.</summary>
    MyDocumentsFallback,
}

/// <summary>The FS-1 tier-resolution outcome: the resolved root plus which tier produced it.</summary>
/// <param name="Root">The resolved absolute folder path — the single Folder + AI-write root for this launch.</param>
/// <param name="Tier">Which tier produced <see cref="Root"/> (drives the folder-tree pane's initial visibility).</param>
public sealed record FolderRootResolution(string Root, FolderRootTier Tier)
{
    /// <summary>
    /// Tier (iii) only: the folder-tree pane stays COLLAPSED until the user summons it via [Folder] — a
    /// My-Documents fallback is not a project the user asked to browse, unlike tiers (i)/(ii).
    /// </summary>
    public bool KeepFolderPaneCollapsed => Tier == FolderRootTier.MyDocumentsFallback;
}

/// <summary>One restored tab's path fact — the only input tier (ii) needs (null = scratch/untitled, skipped).</summary>
/// <param name="Path">The tab's saved absolute on-disk path, or null for a scratch/untitled buffer.</param>
public readonly record struct TabPathFact(string? Path);

/// <summary>The FS-1 pure 3-tier resolver (Decision A, FINAL).</summary>
public static class FolderRootResolver
{
    /// <summary>
    /// Resolve the single Folder + AI-write root for this launch. <paramref name="restoredTabsInOrder"/>
    /// MUST be supplied in the tabs' actual restore order — tier (ii) reads the FIRST path-bearing entry.
    /// </summary>
    /// <param name="passedFiles">File(s) passed on the command line (double-click / "Open With"), in argv order.</param>
    /// <param name="restoredTabsInOrder">The restored tabs' path facts, in restore order.</param>
    /// <param name="myDocumentsResolver">
    /// Resolves the user's My Documents folder (tier iii only — invoked lazily, only when reached).
    /// </param>
    public static FolderRootResolution Resolve(
        IReadOnlyList<string> passedFiles,
        IReadOnlyList<TabPathFact> restoredTabsInOrder,
        Func<string> myDocumentsResolver)
    {
        ArgumentNullException.ThrowIfNull(passedFiles);
        ArgumentNullException.ThrowIfNull(restoredTabsInOrder);
        ArgumentNullException.ThrowIfNull(myDocumentsResolver);

        if (passedFiles.Count > 0)
            return new FolderRootResolution(ContainingFolderOf(passedFiles[0]), FolderRootTier.PassedFile);

        foreach (TabPathFact tab in restoredTabsInOrder)
        {
            if (!string.IsNullOrEmpty(tab.Path))
                return new FolderRootResolution(ContainingFolderOf(tab.Path), FolderRootTier.RestoredTab);
        }

        return new FolderRootResolution(EnsureAbsolute(myDocumentsResolver()), FolderRootTier.MyDocumentsFallback);
    }

    private static string ContainingFolderOf(string path)
    {
        string full = Path.GetFullPath(path);
        return Path.GetDirectoryName(full)
            ?? throw new ArgumentException($"Path has no containing folder: {path}", nameof(path));
    }

    /// <summary>
    /// The dedicated fallback folder name under the system temp directory (hostile-review Warning #4):
    /// the AI write boundary must never be the shared <c>/tmp</c> root itself — an app-owned subfolder
    /// bounds the fallback's blast radius to HoardPad's own files.
    /// </summary>
    private const string FallbackFolderName = "HoardPad-Fallback";

    /// <summary>
    /// Defense-in-depth (hostile-review Warning 1, tightened by Warning #4): the AI write boundary must
    /// NEVER be an unanchored relative path, and must never be the shared <c>/tmp</c> root itself. Tiers
    /// (i)/(ii) are always absolute via <see cref="ContainingFolderOf"/>'s <see cref="Path.GetFullPath(string)"/>;
    /// tier (iii) delegates to the injected <c>myDocumentsResolver</c>, which — in a genuinely atypical
    /// environment (both <c>SpecialFolder.MyDocuments</c> and <c>SpecialFolder.UserProfile</c> empty) —
    /// could return a bare relative <c>"Documents"</c> (<see cref="MyDocumentsResolver"/>'s
    /// <c>Path.Combine</c> fallback). A relative root would resolve against whatever the OS process's
    /// current directory happens to be at sandbox-gate time — an unpredictable anchor for a security
    /// boundary. If the resolved root is not already rooted, fall back to a dedicated, app-owned
    /// subfolder under the system temp directory (created if missing) — always absolute, and scoped away
    /// from the shared temp root — rather than silently <c>GetFullPath</c>-anchoring it to an arbitrary CWD.
    /// </summary>
    private static string EnsureAbsolute(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        string fallback = Path.Combine(Path.GetTempPath(), FallbackFolderName);
        if (!Directory.Exists(fallback))
            Directory.CreateDirectory(fallback);
        return Path.GetFullPath(fallback);
    }
}
