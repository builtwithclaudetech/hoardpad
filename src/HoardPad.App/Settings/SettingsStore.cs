// SettingsStore.cs — atomic load/save of settings.json (FR-36, architecture §8.2).
//
// Mirrors SessionStore EXACTLY (the proven M6.3 pattern): read bytes off the UI thread + deserialize,
// falling back to the documented defaults (HoardPadSettings.Default) on a missing OR corrupt/unparseable
// file (FR-36 fail-safe — NEVER crash on a bad settings file). Write through the SAME IAtomicFileStore
// atomic path (CreateWhenAbsent for the first write, ReplaceWhenPresent thereafter) so a crash mid-save
// never leaves a torn settings.json (architecture §8.2). Restricts the file to the current user (NFR-7)
// on first create, consistent with SessionStore.EnsureAppDataRoot.
//
// Enum round-trip: AgencyMode / AiBackend already carry [JsonConverter(KebabLowerEnumConverter)] in
// Core (kebab-lower wire tokens); AppTheme is App-local, round-tripped by the JsonStringEnumConverter
// registered here (same posture as SessionStore's TabKind).

using System.Text.Json;
using System.Text.Json.Serialization;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Settings;

/// <summary>
/// Loads and atomically saves the single human-readable <c>settings.json</c> (FR-36, architecture §8.2).
/// A missing/corrupt file falls back to <see cref="HoardPadSettings.Default"/> without crashing (the
/// FR-36 fail-safe). Writes go through the same <see cref="IAtomicFileStore"/> atomic path as every other
/// durable write — no torn settings file on crash.
/// </summary>
internal sealed class SettingsStore : IDisposable
{
    // Human-inspectable (indented); readable string enums (AppTheme — Agency/Backend self-convert via
    // their own [JsonConverter]); omit null per-backend config so the file stays clean until configured.
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAtomicFileStore _store;
    private readonly IAppDataPaths _paths;

    // W-1 (round-3 hostile review): SettingsStore is the SINGLE owner of settings.json. This gate delivers
    // two guarantees, and ONLY for callers that route their write through SaveAsync/UpdateAsync below (it
    // cannot protect a caller that reads the file / an old snapshot and writes some other way — there is
    // no such caller in this codebase, but the guarantee is scoped to this store's own API surface, not
    // universal): (1) write-atomicity — two concurrent SaveAsync/UpdateAsync calls never interleave their
    // underlying disk writes; (2) read-modify-write atomicity for UpdateAsync callers specifically — an
    // UpdateAsync's Load -> mutate -> Save runs as ONE critical section, so no OTHER writer's commit can
    // land between its Load and its Save and get silently reverted by that Save's stale base. Every
    // caller that patches a subset of fields (a partial-field writer) MUST go through UpdateAsync, never a
    // hand-rolled Load-then-SaveAsync — see ThemeController.PersistAsync / App.axaml.cs
    // ApplySettingsChangeAsync for the converted examples. NOT re-entrant (SemaphoreSlim), so the gated
    // public entry points call an UNGATED core — never SaveAsync calling itself.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// BUG-5: Raised after <see cref="SaveAsync"/> completes atomically, carrying the newly-persisted
    /// settings. Subscribers that need live model/effort updates (e.g. <c>AiPanelViewModel</c>) attach
    /// here so they receive the post-save snapshot without polling. Handlers are invoked on the thread
    /// that awaited <see cref="SaveAsync"/> — dispatch to the UI thread when updating observable properties.
    /// </summary>
    public event Action<HoardPadSettings>? SettingsChanged;

    public SettingsStore(IAtomicFileStore store, IAppDataPaths paths)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// Read <c>settings.json</c>, or <see cref="HoardPadSettings.Default"/> if the file is absent OR
    /// corrupt/unparseable (FR-36 fail-safe). Reads bytes off the UI thread; never throws on a bad file.
    /// </summary>
    public async Task<HoardPadSettings> LoadAsync(CancellationToken ct)
    {
        string path = _paths.SettingsFilePath;
        if (!File.Exists(path))
            return HoardPadSettings.Default;

        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            // A null deserialize (literal JSON "null") also degrades to defaults — never a null settings.
            return JsonSerializer.Deserialize<HoardPadSettings>(bytes, SettingsJson) ?? HoardPadSettings.Default;
        }
        catch (JsonException)
        {
            // Corrupt/truncated/garbage settings.json -> documented defaults (FR-36). The user is told
            // once by the settings surface ("Settings could not be read; defaults restored.", design §7.6);
            // here we just never crash the load.
            return HoardPadSettings.Default;
        }
    }

    /// <summary>
    /// Synchronous load for COMPOSITION-TIME seeding only (before the dispatcher loop owns the thread):
    /// reads the file synchronously and falls back to <see cref="HoardPadSettings.Default"/> on
    /// missing/corrupt (FR-36 fail-safe). Used to seed the AI panel + editor defaults at startup so we
    /// never block a Task on the UI thread (the banned <c>.Result</c>/<c>.Wait()</c> deadlock). Prefer
    /// <see cref="LoadAsync"/> from any UI-thread code path.
    /// </summary>
    public HoardPadSettings Load()
    {
        string path = _paths.SettingsFilePath;
        if (!File.Exists(path))
            return HoardPadSettings.Default;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize<HoardPadSettings>(bytes, SettingsJson) ?? HoardPadSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return HoardPadSettings.Default;
        }
    }

    /// <summary>
    /// Persist <paramref name="settings"/> to <c>settings.json</c> atomically (FR-36, architecture §8.2) —
    /// CreateWhenAbsent for the first write, ReplaceWhenPresent thereafter, so a crash mid-save never
    /// leaves a torn file. Restricts the app-data root to the current user (NFR-7) on first create.
    /// W-1: serialized through <see cref="_writeGate"/> against every other <see cref="SaveAsync"/> and
    /// <see cref="UpdateAsync"/> call on this SAME instance — its underlying disk write can never interleave
    /// with a concurrent <see cref="UpdateAsync"/> read-modify-write, and any concurrent
    /// <see cref="UpdateAsync"/> call is guaranteed to Load a base that already reflects THIS save once it
    /// has committed. This is a full-record replacement, not a read-modify-write itself — a caller with only
    /// a subset of fields to persist must use <see cref="UpdateAsync"/> instead, or it will silently revert
    /// whatever <see cref="UpdateAsync"/>/<see cref="SaveAsync"/> caller committed since this caller's own
    /// snapshot was taken.
    /// </summary>
    public async Task SaveAsync(HoardPadSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _writeGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            await SaveCoreAsync(settings, ct).ConfigureAwait(true);
        }
        finally
        {
            _writeGate.Release();
        }

        // Fired AFTER the gate is released — SettingsStore never re-enters itself from a handler, and the
        // one production subscriber (CompositionRoot) dispatches via Dispatcher.UIThread.Post rather than
        // running synchronously here, so there is zero deadlock risk either way.
        SettingsChanged?.Invoke(settings);
    }

    /// <summary>
    /// W-1 (round-3 hostile review): an atomic read-modify-write against the CURRENT on-disk settings —
    /// the single-writer-safe way to persist a partial-field change (e.g. <c>ThemeController.PersistAsync</c>'s
    /// theme-only patch) without clobbering fields a concurrent full-record
    /// <see cref="SaveAsync"/> (the Settings tab) just committed. Acquires <see cref="_writeGate"/> ONCE for
    /// the whole Load -> <paramref name="mutate"/> -> Save sequence, so no other writer's Load can observe a
    /// stale snapshot mid-update. A no-op <paramref name="mutate"/> (the returned record equals the freshly
    /// loaded one) skips the save and the <see cref="SettingsChanged"/> round-trip, mirroring the prior
    /// per-call no-op-when-unchanged behavior.
    /// </summary>
    public async Task UpdateAsync(Func<HoardPadSettings, HoardPadSettings> mutate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        HoardPadSettings? persisted = null;
        await _writeGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            HoardPadSettings current = await LoadAsync(ct).ConfigureAwait(true);
            HoardPadSettings updated = mutate(current);
            if (updated == current)
                return; // nothing actually changed — skip the redundant save + SettingsChanged round-trip.

            await SaveCoreAsync(updated, ct).ConfigureAwait(true);
            persisted = updated;
        }
        finally
        {
            _writeGate.Release();
        }

        SettingsChanged?.Invoke(persisted);
    }

    /// <summary>
    /// The UNGATED atomic write core — CreateWhenAbsent for the first write, ReplaceWhenPresent thereafter.
    /// Never call directly from outside this class; both <see cref="SaveAsync"/> and <see cref="UpdateAsync"/>
    /// hold <see cref="_writeGate"/> (not re-entrant) around every call to this method.
    /// </summary>
    private async Task SaveCoreAsync(HoardPadSettings settings, CancellationToken ct)
    {
        EnsureAppDataRoot();

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJson);
        string path = _paths.SettingsFilePath;

        if (File.Exists(path))
            await _store.ReplaceWhenPresentAsync(path, bytes, ct);
        else
            await _store.CreateWhenAbsentAsync(path, bytes, ct);
    }

    private void EnsureAppDataRoot()
    {
        if (!Directory.Exists(_paths.AppDataRoot))
        {
            Directory.CreateDirectory(_paths.AppDataRoot);
            _paths.RestrictToCurrentUser(_paths.AppDataRoot);
        }
    }

    /// <summary>Releases <see cref="_writeGate"/> (CA1001 — a singleton, disposed by the DI container at app shutdown).</summary>
    public void Dispose() => _writeGate.Dispose();
}
