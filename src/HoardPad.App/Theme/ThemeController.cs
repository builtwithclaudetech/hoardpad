// ThemeController.cs — the M7.1 consumer end of the FR-37 theme switch (architecture §3.7, §8.2).
//
// Bridges the ThemeService (the lockstep switch), the persisted settings (HoardPadSettings.Theme via
// SettingsStore — the M6.8 persistence, CONSUMED not reimplemented), and the shell (the View ▸ Toggle
// Theme command + the status-bar theme segment). It:
//   - applies the persisted theme at startup (ApplyStartupTheme),
//   - toggles dark↔light on the View-menu command (ToggleAsync) — one ThemeService.Switch + one settings
//     save (atomic settings.json, FR-36),
//   - keeps the shell's ThemeStatusLabel in sync ("☾ Dark" / "☀ Light").
// All theme application runs on the UI thread (the synchronous Switch); only the settings save is async.

using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Theme;

/// <summary>
/// The FR-37 consumer end: applies the persisted theme at startup, toggles dark↔light on the View-menu
/// command (one <see cref="ThemeService.Switch(AppTheme)"/> + one persist), and keeps the shell's
/// theme status label in sync. Consumes the M6.8 <see cref="SettingsStore"/> persistence (FR-36) — it
/// does not reimplement it.
/// </summary>
internal sealed class ThemeController
{
    private readonly ThemeService _themes;
    private readonly SettingsStore _store;
    private readonly MainWindowViewModel _shell;

    private AppTheme _current;

    public ThemeController(
        ThemeService themes,
        SettingsStore store,
        MainWindowViewModel shell,
        HoardPadSettings seed)
    {
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(seed);
        _themes = themes;
        _store = store;
        _shell = shell;
        _current = seed.Theme;
    }

    /// <summary>The theme currently applied (FR-37). Seeded from the persisted settings at construction.</summary>
    public AppTheme Current => _current;

    /// <summary>
    /// Apply the persisted theme (FR-37) at startup — the one synchronous lockstep <see cref="ThemeService.Switch(AppTheme)"/>
    /// — and sync the shell's status label + wire the toggle command. UI-thread only.
    /// </summary>
    public void ApplyStartupTheme()
    {
        _themes.Switch(_current);
        _shell.ThemeStatusLabel = StatusLabel(_current);
        _shell.ToggleThemeHandler = () => ToggleAsync(CancellationToken.None);
    }

    /// <summary>
    /// Toggle dark↔light (FR-37): the one synchronous lockstep switch, sync the status label, then persist
    /// the choice atomically (FR-36 — through the M6.8 SettingsStore). The persist runs off the UI thread.
    /// </summary>
    public async Task ToggleAsync(CancellationToken ct)
    {
        _current = _current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _themes.Switch(_current);                          // synchronous, no-yield (architecture §3.7).
        _shell.ThemeStatusLabel = StatusLabel(_current);
        await PersistAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// QA4-17 (reopens FEAT-14): apply <paramref name="theme"/> as the CURRENT theme WITHOUT persisting —
    /// the caller (an AI-requested settings change, already routed through
    /// <c>SettingsStore.UpdateAsync</c>) owns persistence, exactly as <c>App.ApplyAiSettingsChangeAsync</c>
    /// owns persistence for the font/color fields via <c>SettingsFontApplier</c>/<c>AiPanelStyleApplier</c>.
    /// Reuses the SAME synchronous lockstep switch + status-label sync as <see cref="ToggleAsync"/>, so an
    /// AI-applied theme change is visually indistinguishable from a View ▸ Toggle Theme click. Idempotent —
    /// safe to call with the theme already in effect. UI-thread only (same posture as every other method
    /// on this class).
    /// </summary>
    public void SetTheme(AppTheme theme)
    {
        _current = theme;
        _themes.Switch(_current);
        _shell.ThemeStatusLabel = StatusLabel(_current);
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        // W-1 (round-3 hostile review, residual warning): read-modify-write through SettingsStore's own
        // UpdateAsync — the base record is Loaded FRESH under the store's write gate and only Theme is
        // patched, so a concurrent writer of any other field (e.g. the AI panel's Model/Effort pick) can
        // never be silently reverted by a stale snapshot this toggle captured before that write committed
        // (the same stale-seed data-loss class Fix #1 originally closed, now closed against EVERY writer,
        // not just a startup-seed snapshot).
        await _store.UpdateAsync(s => s with { Theme = _current }, ct).ConfigureAwait(true);
    }

    private static string StatusLabel(AppTheme theme) => theme == AppTheme.Light ? "☀ Light" : "☾ Dark";
}
