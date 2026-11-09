// MyDocumentsResolver.cs — FS-1's "My Documents" resolution rule (the BUG-10 gap this closes):
// Environment.GetFolderPath(SpecialFolder.MyDocuments) honors XDG_DOCUMENTS_DIR on Linux but returns an
// EMPTY STRING when there is no user-dirs.dirs (a bare/minimal Linux account) — silently breaking tier
// (iii) if used unchecked. Falls back to ~/Documents (created if missing) only in that empty case.
// Reliable on Windows (the real Documents known-folder), so the fallback almost never fires there.

using System.IO;

namespace HoardPad.App.Session;

/// <summary>FS-1's "My Documents" fallback resolution (tier iii).</summary>
public static class MyDocumentsResolver
{
    /// <summary>
    /// Pure resolution given the two already-queried OS paths + an ensure-directory-exists delegate — the
    /// testable core (no direct <see cref="Environment"/>/<see cref="Directory"/> calls of its own).
    /// </summary>
    /// <param name="myDocumentsFromApi">
    /// The raw <c>Environment.GetFolderPath(SpecialFolder.MyDocuments)</c> result. Used as-is when non-empty.
    /// </param>
    /// <param name="userProfile">
    /// The raw <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c> result — the fallback's base
    /// (<c>{userProfile}/Documents</c>), only consulted when <paramref name="myDocumentsFromApi"/> is empty.
    /// </param>
    /// <param name="ensureDirectoryExists">Create the fallback folder if it does not already exist.</param>
    public static string Resolve(string myDocumentsFromApi, string userProfile, Action<string> ensureDirectoryExists)
    {
        ArgumentNullException.ThrowIfNull(myDocumentsFromApi);
        ArgumentNullException.ThrowIfNull(userProfile);
        ArgumentNullException.ThrowIfNull(ensureDirectoryExists);

        if (myDocumentsFromApi.Length > 0)
            return myDocumentsFromApi;

        string fallback = Path.Combine(userProfile, "Documents");
        ensureDirectoryExists(fallback);
        return fallback;
    }

    /// <summary>
    /// The live OS resolution: Linux honors <c>XDG_DOCUMENTS_DIR</c> via
    /// <c>GetFolderPath(SpecialFolder.MyDocuments)</c> when set, else falls back to <c>~/Documents</c>
    /// (created if missing); Windows' <c>MyDocuments</c> known-folder API is reliable, so the fallback
    /// almost never fires there.
    /// </summary>
    public static string ResolveLive() =>
        Resolve(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            dir =>
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            });
}
