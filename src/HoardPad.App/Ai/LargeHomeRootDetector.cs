// LargeHomeRootDetector.cs — FS-5 / FR-10(a) (docs/decisions.md DR-1): the home-root and large-root
// triggers that (when the "Confirm each AI edit in large/home folders" setting is ON) downgrade an
// auto-apply op to a one-time approve-each confirm. This is a MINIMAL implementation scoped to FS-5 —
// see DR-1 for the simplifications vs the full PRD FR-10(a) spec (computed once at arm-time rather
// than re-counted at op-time; a best-effort bounded walk rather than the full mount-boundary/
// symlink-cycle predicate).

using HoardPad.Core.Records;

namespace HoardPad.App.Ai;

/// <summary>
/// Pure(ish) FR-10(a) trigger detection: is the resolved AI write root the user's home directory, or
/// does its writable-set file count exceed a configured threshold. Both checks are cheap best-effort
/// approximations of the full PRD predicate (docs/decisions.md DR-1). The walk always TERMINATES
/// (bounded at <c>threshold + 1</c>) but does NOT guarantee it never throws: it only catches
/// <see cref="UnauthorizedAccessException"/>/<see cref="IOException"/> per entry (permission-denied /
/// vanished-mid-walk), so an exotic failure (e.g. <see cref="File.GetAttributes(string)"/> throwing
/// <see cref="ArgumentException"/>/<see cref="NotSupportedException"/> on a malformed path, or a
/// cancellation via <see cref="CancellationToken"/>) still propagates to the caller. The caller
/// (<c>AiPanelViewModel.ArmAgencyAsync</c>) is responsible for failing an unexpected exception TOWARD
/// CONFIRM, per docs/decisions.md DR-1's failure-direction contract.
/// </summary>
public static class LargeHomeRootDetector
{
    /// <summary>
    /// The live default detector: home-root compares <paramref name="root"/> against
    /// <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c> ($HOME / %USERPROFILE%, matching the
    /// PRD FR-10(a)(a) wording); large-root walks the tree via <see cref="ExceedsThreshold"/>.
    /// </summary>
    public static DowngradeReason DetectLive(string root, int threshold, CancellationToken ct) =>
        Detect(root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), threshold, ct);

    /// <summary>
    /// The testable core: given the already-resolved <paramref name="root"/> and <paramref name="home"/>
    /// paths, decide which FR-10(a) trigger (if any) fires. Home-root takes precedence — a large home
    /// root reports <see cref="DowngradeReason.HomeRoot"/>, not both.
    /// </summary>
    public static DowngradeReason Detect(string root, string home, int threshold, CancellationToken ct)
    {
        if (IsHomeRoot(root, home))
            return DowngradeReason.HomeRoot;

        return ExceedsThreshold(root, threshold, ct) ? DowngradeReason.LargeRoot : DowngradeReason.None;
    }

    /// <summary>
    /// Pure string comparison: does <paramref name="root"/> denote the same directory as
    /// <paramref name="home"/> (trailing-separator-insensitive; case-insensitive on Windows/macOS,
    /// ordinal on Linux). Empty/null on either side never matches.
    /// </summary>
    public static bool IsHomeRoot(string root, string home)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(home))
            return false;

        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(TrimTrailingSeparator(root), TrimTrailingSeparator(home), comparison);
    }

    private static string TrimTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Best-effort bounded writable-set count (DR-1 simplification of PRD FR-10(a)(a)'s R3-MA4
    /// predicate): walks regular-file descendants of <paramref name="root"/>, aborting as soon as the
    /// count exceeds <paramref name="threshold"/> — only "over threshold?" is decided, matching the
    /// PRD's abort-at-threshold+1 early exit. Never recurses into a symlinked/reparse-point directory
    /// (a cheap, conservative cycle guard) and silently skips an entry it cannot stat/enumerate
    /// (permission denied, vanished mid-walk) rather than throwing. A non-existent or empty
    /// <paramref name="root"/> never exceeds. <paramref name="threshold"/> is floored at 1.
    /// </summary>
    public static bool ExceedsThreshold(string root, int threshold, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return false;

        int effectiveThreshold = Math.Max(1, threshold);
        int count = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue; // inaccessible / vanished mid-walk — skip, never crash the walk.
            }

            foreach (string entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                bool isReparsePoint = (attrs & FileAttributes.ReparsePoint) != 0;
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (!isReparsePoint) // never recurse into a symlinked dir (cycle guard).
                        pending.Push(entry);
                    continue;
                }

                if (isReparsePoint) // a symlinked file — skip rather than double-count its target.
                    continue;

                count++;
                if (count > effectiveThreshold)
                    return true;
            }
        }

        return false;
    }
}
