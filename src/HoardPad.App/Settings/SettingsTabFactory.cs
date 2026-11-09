// SettingsTabFactory.cs — mints the settings document tab (M6.8, ODQ-12).
//
// The settings surface is a tab in the existing document/tab model (ODQ-12 — settings-as-tab, NOT a
// separate OS window). This thin factory closes over the SettingsStore so the workspace / a menu command
// can open the settings tab without service-locating: it loads the current settings (or defaults if the
// file is missing/corrupt, FR-36) into a fresh SettingsViewModel and wraps it as a non-persisted tab.

using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Settings;

/// <summary>
/// Mints the settings document tab (ODQ-12). Loads the current settings into a fresh
/// <see cref="SettingsViewModel"/> and wraps it as a non-persisted <see cref="DocumentTab"/>.
/// </summary>
internal sealed class SettingsTabFactory
{
    /// <summary>The fixed tab id for the (single) settings tab — used to focus it if already open.</summary>
    public const string SettingsTabId = "__settings__";

    private readonly SettingsStore _store;

    public SettingsTabFactory(SettingsStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Open the settings tab: load the current settings (or documented defaults if missing/corrupt, FR-36)
    /// into a fresh VM and wrap it as a settings tab. Runs the load off the UI thread.
    /// </summary>
    public async Task<DocumentTab> CreateAsync(CancellationToken ct)
    {
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(_store, ct).ConfigureAwait(true);
        return DocumentTab.ForSettings(SettingsTabId, vm);
    }
}
