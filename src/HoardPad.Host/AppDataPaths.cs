// AppDataPaths.cs — the per-user data-dir + permission seam (M2.2, NFR-7).
//
// Resolves the §5.5 on-disk layout (settings / session / recovery / activity / undo /
// capstate) under the per-user app-data root, and restricts just-created files/dirs to
// the current user where the OS supports it. The RECOVERY subdirectory is MACHINE-LOCAL
// and NOT synced (R4-MA1 / PRD §9.2) — it lives under a distinct machine-local base, not
// under the (potentially roamed/synced) AppDataRoot, so a recovery snapshot can never
// cross machines and the per-machine `seq` tie-break stays valid.
//
// Base-path choices:
//   Linux:   AppDataRoot  -> $XDG_DATA_HOME  (fallback ~/.local/share) — user data.
//            Recovery     -> $XDG_STATE_HOME (fallback ~/.local/state) — the XDG-designated
//                            MACHINE-LOCAL state location for data that should persist but
//                            need not be synced/backed-up. Distinct from XDG_DATA_HOME.
//   Windows: AppDataRoot  -> LocalApplicationData (%LOCALAPPDATA%) — already NON-roaming.
//            Recovery     -> the same LocalApplicationData base, which is machine-local by
//                            definition (vs ApplicationData/%APPDATA%, which roams).
//
// RestrictToCurrentUser: Linux uses File.SetUnixFileMode (700 for a dir, 600 for a file) —
// the managed BCL equivalent of chmod, consistent with the M2.1 libc P/Invoke posture but
// without a hand-rolled syscall where the BCL already exposes the operation. The Windows ACL
// leg COMPILES for win-x64 but its runtime restriction is DEFERRED to Windows hardware.

using System.Runtime.Versioning;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// Per-user app-data paths + per-user permission restriction (NFR-7). Implements
/// <see cref="IAppDataPaths"/>. The recovery subdirectory is machine-local / non-synced
/// (R4-MA1): on Linux it resolves under <c>$XDG_STATE_HOME</c> (<c>~/.local/state</c>),
/// distinct from the <c>$XDG_DATA_HOME</c>-rooted <see cref="AppDataRoot"/>.
/// </summary>
public sealed class AppDataPaths : IAppDataPaths
{
    private const string AppFolderName = "HoardPad";
    private const string AppDataFallbackSuffix = "-AppData-Fallback";
    private const string RecoveryFallbackSuffix = "-Recovery-Fallback";

    private readonly string _appDataRoot;
    private readonly string _recoveryDirectory;

    public AppDataPaths()
        : this(ResolveDefaultAppDataRoot(), ResolveDefaultRecoveryRoot())
    {
    }

    /// <summary>
    /// Test/seam ctor: inject the per-user data root and the machine-local recovery root
    /// explicitly so the §5.5 layout can be exercised against a temp directory without
    /// touching the real user profile.
    /// </summary>
    public AppDataPaths(string appDataRoot, string recoveryRoot)
    {
        if (string.IsNullOrEmpty(appDataRoot))
            throw new ArgumentException("AppData root must be a non-empty path.", nameof(appDataRoot));
        if (string.IsNullOrEmpty(recoveryRoot))
            throw new ArgumentException("Recovery root must be a non-empty path.", nameof(recoveryRoot));

        _appDataRoot = Path.GetFullPath(appDataRoot);
        // The recovery dir is the AppFolder under the MACHINE-LOCAL base, kept on a base
        // distinct from AppDataRoot so it is provably outside any synced/roamed root.
        _recoveryDirectory = Path.Combine(Path.GetFullPath(recoveryRoot), AppFolderName, "recovery");
    }

    // ---- §5.5 layout ----

    public string AppDataRoot => _appDataRoot;

    public string RecoveryDirectory => _recoveryDirectory;

    public string SettingsFilePath => Path.Combine(_appDataRoot, "settings.json");

    public string ActivityLogPath(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        return Path.Combine(_appDataRoot, "activity", $"{sessionId}.log");
    }

    public string UndoJournalRoot => Path.Combine(_appDataRoot, "undo");

    // ---- per-user permission restriction (NFR-7) ----

    public void RestrictToCurrentUser(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("A path is required.", nameof(path));

        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path);
            return;
        }

        // Linux/macOS: chmod to owner-only via the managed BCL (no hand-rolled syscall needed —
        // File.SetUnixFileMode is the platform-checked equivalent). 700 for a directory (owner
        // rwx, so the owner can traverse + list), 600 for a regular file (owner rw).
        UnixFileMode mode = Directory.Exists(path)
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute   // 700
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;                             // 600
        File.SetUnixFileMode(path, mode);
    }

    // ---- default base-path resolution ----

    /// <summary>
    /// FR-36: the app-data root must never be install-dir- or CWD-relative. On Windows this is
    /// <c>%LOCALAPPDATA%</c>. On Linux/macOS this mirrors <see cref="ResolveDefaultRecoveryRoot"/>'s
    /// fallback shape (<c>$XDG_DATA_HOME</c>, else <c>$HOME/.local/share</c>) rather than reading
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> directly — not because of
    /// self-contained publish specifically, but because the .NET 8 resolution for that special
    /// folder is itself just this same XDG/HOME chain, and can legitimately resolve to a relative or
    /// empty value in a stripped/minimal environment (e.g. <c>$HOME</c> unset). Both branches below
    /// are guarded so every return value is absolute.
    /// </summary>
    private static string ResolveDefaultAppDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);

        return ResolveDefaultAppDataRootNonWindows(
            Environment.GetEnvironmentVariable,
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Test seam (internal — visible to HoardPad.Host.Tests only): the non-Windows OS-facing lookups
    /// behind <see cref="ResolveDefaultAppDataRoot()"/> extracted as delegates so a test can simulate
    /// a stripped/empty environment (both <c>$XDG_DATA_HOME</c> and <c>$HOME</c> resolving empty)
    /// without touching the real process environment, and assert the <see cref="EnsureAbsolute"/>
    /// guard below still returns an absolute path.
    /// </summary>
    internal static string ResolveDefaultAppDataRootNonWindows(
        Func<string, string?> getEnvironmentVariable,
        Func<string> getUserProfile)
    {
        // Only trust XDG_DATA_HOME when it is itself absolute (XDG Base Directory spec: a relative
        // value is invalid and must be ignored) — otherwise Path.GetFullPath in the ctor would
        // silently anchor a relative XDG value to the process CWD, the exact FR-36 violation this
        // method exists to prevent.
        string? xdgDataHome = getEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome) && Path.IsPathRooted(xdgDataHome))
            return Path.Combine(xdgDataHome, AppFolderName);

        string home = getUserProfile();
        return EnsureAbsolute(Path.Combine(home, ".local", "share", AppFolderName));
    }

    /// <summary>
    /// Defense-in-depth mirroring the house <c>EnsureAbsolute</c> guard pattern
    /// (HoardPad.App/Session/FolderRootResolver.cs): if the resolved candidate is not rooted — e.g.
    /// <see cref="Environment.SpecialFolder.UserProfile"/> resolving empty in a stripped/container
    /// environment with no <c>$HOME</c> — fall back to a dedicated, app-owned directory name under
    /// the system temp root, computed only (no directory I/O here; the caller creates it on first
    /// write, same as every other path in this seam). <see cref="Path.GetTempPath"/> itself reads
    /// <c>TMPDIR</c>/<c>TMP</c>/<c>TEMP</c> verbatim with no absoluteness check, so if that also
    /// resolves relative, this falls back further to a hardcoded absolute root (<c>/tmp</c> — this
    /// private helper is only reached on the non-Windows path) rather than letting
    /// <see cref="Path.GetFullPath(string)"/> silently CWD-anchor the last-resort fallback itself.
    /// </summary>
    private static string EnsureAbsolute(string path, string fallbackSuffix = AppDataFallbackSuffix)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        string tempRoot = Path.GetTempPath();
        if (!Path.IsPathRooted(tempRoot))
            tempRoot = "/tmp";

        string fallback = Path.Combine(tempRoot, AppFolderName + fallbackSuffix);
        return Path.GetFullPath(fallback);
    }

    /// <summary>
    /// The machine-local base for the recovery subdir (R4-MA1). On Linux this is
    /// <c>$XDG_STATE_HOME</c> (fallback <c>~/.local/state</c>) — the XDG-designated location for
    /// machine-local state, deliberately separate from <c>$XDG_DATA_HOME</c> so recovery snapshots
    /// are never under a folder a user might sync. On Windows, <c>LocalApplicationData</c>
    /// (<c>%LOCALAPPDATA%</c>) is already non-roaming and machine-local.
    /// </summary>
    private static string ResolveDefaultRecoveryRoot()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return ResolveDefaultRecoveryRootNonWindows(
            Environment.GetEnvironmentVariable,
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Test seam (internal — visible to HoardPad.Host.Tests only): the non-Windows OS-facing lookups
    /// behind <see cref="ResolveDefaultRecoveryRoot()"/> extracted as delegates, mirroring
    /// <see cref="ResolveDefaultAppDataRootNonWindows"/>, so a test can simulate a stripped/relative
    /// environment (a relative <c>$XDG_STATE_HOME</c>, or an empty <c>$HOME</c>) without touching the
    /// real process environment.
    /// </summary>
    internal static string ResolveDefaultRecoveryRootNonWindows(
        Func<string, string?> getEnvironmentVariable,
        Func<string> getUserProfile)
    {
        // Only trust XDG_STATE_HOME when it is itself absolute (XDG Base Directory spec: a relative
        // value is invalid and must be ignored) — otherwise the ctor's Path.GetFullPath would
        // silently anchor a relative XDG value to the process CWD, the same FR-36-shaped gap
        // ResolveDefaultAppDataRootNonWindows guards against for the app-data root.
        string? xdgStateHome = getEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(xdgStateHome) && Path.IsPathRooted(xdgStateHome))
            return xdgStateHome;

        string home = getUserProfile();
        return EnsureAbsolute(Path.Combine(home, ".local", "state"), RecoveryFallbackSuffix);
    }

    // ---- Windows ACL — COMPILE-ONLY on this box; runtime DEFERRED ----
    //
    // Restricting a file/dir to the current user on Windows means rewriting its DACL to the
    // owner SID only (the FileSystemAccessRule / SetAccessControl path). That requires the
    // Windows-only System.Security.AccessControl surface and real Windows hardware to assert
    // the resulting permissions, so it is DEFERRED (TD §6 pattern). Guarded by
    // OperatingSystem.IsWindows() so it never runs on Linux. The method is a no-op placeholder
    // that compiles for win-x64; wiring the DACL rewrite is the Windows-validation milestone's job.
    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        // DEFERRED: rewrite the DACL to owner-SID-only via FileSecurity.SetAccessControl.
        // Authored as a guarded no-op so the win-x64 build is clean; the runtime ACL restriction
        // is validated on Windows hardware, never on this Linux box.
        _ = path;
    }
}
