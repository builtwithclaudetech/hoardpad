// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// HOST [host]. Per-user data dir + permission restriction (NFR-7). The recovery subdirectory
/// is MACHINE-LOCAL and NOT synced (§9.2 / R4-MA1).
public interface IAppDataPaths
{
    string AppDataRoot { get; }                 // per-user; restricted to the user where the OS supports it
    string RecoveryDirectory { get; }           // machine-local, non-synced
    string SettingsFilePath { get; }            // FR-36 single human-readable settings file
    string ActivityLogPath(string sessionId);   // §9.3 per-session NDJSON
    string UndoJournalRoot { get; }             // §9.4
    /// Restrict a just-created file/dir to the current user (chmod 600 / Windows ACL) where supported.
    void RestrictToCurrentUser(string path);
}
