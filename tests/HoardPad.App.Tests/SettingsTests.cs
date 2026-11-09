// SettingsTests.cs — M6.8 settings surface (FR-35/FR-36, ODQ-12). Class names contain "Settings" so the
// verify filter "FullyQualifiedName~Settings" resolves every case.
//
// Covers the four required gates:
//   - defaults-on-missing : no settings.json -> documented defaults, no crash.
//   - corrupt-file        : garbage/truncated settings.json -> documented defaults, no crash (fail-safe).
//   - atomic round-trip   : save -> reload -> identical values (through the real IAtomicFileStore).
//   - clamp rejection     : a cap set to 0/unbounded is rejected via the Core.Ai clamp; the safe value persists.
// Plus: the SettingsViewModel clamp-through-UI path, the auto-save ceiling clamp, and the persisted-only
// theme field.

using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Hosting;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Records;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class SettingsStoreTests
{
    private static SettingsStore NewStore(out TempAppDataPaths paths)
    {
        paths = new TempAppDataPaths();
        return new SettingsStore(new RealAtomicFileStore(), paths);
    }

    [Fact]
    public async Task MissingFileLoadsDocumentedDefaultsWithoutCrashing()
    {
        SettingsStore store = NewStore(out var paths);
        using var _paths = paths;

        // No settings.json was written — the file is absent.
        Assert.False(File.Exists(paths.SettingsFilePath));

        HoardPadSettings loaded = await store.LoadAsync(CancellationToken.None);

        // The documented defaults (the single source of truth), not a crash / null.
        Assert.Equal(HoardPadSettings.Default, loaded);
        Assert.Equal(AiBackend.ClaudeCli, loaded.ActiveBackend);
        Assert.Equal(AgencyMode.AutoApply, loaded.AgencyMode);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(CapConfig.Pinned, loaded.Caps);          // 25 / 10 / 20 / 80, referenced from Core.Ai.
        Assert.Equal(HoardPadSettings.DefaultAutoSaveSeconds, loaded.AutoSaveSeconds);
        Assert.Equal(HoardPadSettings.DefaultNoReplySeconds, loaded.NoReplySeconds);
        Assert.Equal(HoardPadSettings.DefaultLargeRootThreshold, loaded.LargeRootThreshold);
    }

    [Fact]
    public async Task CorruptFileFallsBackToDocumentedDefaultsWithoutCrashing()
    {
        SettingsStore store = NewStore(out var paths);
        using var _paths = paths;

        // Garbage / truncated bytes at the settings path (not valid JSON).
        await File.WriteAllBytesAsync(paths.SettingsFilePath, Encoding.UTF8.GetBytes("{ this is not json"));

        HoardPadSettings loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(HoardPadSettings.Default, loaded);       // fail-safe: corrupt -> defaults, never a throw.
    }

    [Fact]
    public async Task SaveThenReloadRoundTripsIdenticalValuesAtomically()
    {
        SettingsStore store = NewStore(out var paths);
        using var _paths = paths;

        var saved = HoardPadSettings.Default with
        {
            ActiveBackend = AiBackend.AnthropicApi,
            AnthropicApiKey = "sk-test-1234",
            AnthropicModel = "claude-opus",
            AgencyMode = AgencyMode.ApproveEach,
            AiPanelHotkey = "Ctrl+Alt+H",
            AutoSaveSeconds = 12,
            Theme = AppTheme.Light,
            DefaultLanguage = "Markdown",
            LargeRootThreshold = 5000,
            Caps = new CapConfig(CreateCap: 30, DestructiveCap: 8, FilesTouchedCap: 15, DestructiveMagnitudePercent: 70),
            NoReplySeconds = 90,
            ModelId = "claude-sonnet-4-6",
            EffortLevel = "xhigh",
        };

        await store.SaveAsync(saved, CancellationToken.None);
        Assert.True(File.Exists(paths.SettingsFilePath));     // a single human-readable file (FR-36).

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(saved, reloaded);                        // record equality: every field round-trips.
    }

    [Fact]
    public async Task SaveOverAnExistingFileRoundTripsTheNewValues()
    {
        // Exercises the ReplaceWhenPresent branch (the second write to an existing file).
        SettingsStore store = NewStore(out var paths);
        using var _paths = paths;

        await store.SaveAsync(HoardPadSettings.Default, CancellationToken.None);
        var changed = HoardPadSettings.Default with { Theme = AppTheme.Light, NoReplySeconds = 45 };
        await store.SaveAsync(changed, CancellationToken.None);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(changed, reloaded);
    }
}

public sealed class SettingsClampTests
{
    private static async Task<(SettingsViewModel vm, TempAppDataPaths paths)> NewVmAsync()
    {
        var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);
        return (vm, paths);
    }

    [Fact]
    public async Task SettingACapToZeroIsRejectedAndTheSafeValuePersists()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        int safe = vm.CreateCap;                              // the loaded (clamp-valid) value.
        vm.CreateCap = 0;                                     // 0 disables the backstop (FR-35) — rejected.

        Assert.Equal(safe, vm.CreateCap);                    // the prior safe value is retained.
        Assert.NotEqual(string.Empty, vm.CapValidationMessage); // a clear validation message is surfaced.
        Assert.False(CapClamp.IsValid(vm.Caps with { CreateCap = 0 })); // sanity: the Core.Ai clamp agrees.
        Assert.True(CapClamp.IsValid(vm.Caps));              // the committed caps remain clamp-valid.
    }

    [Fact]
    public async Task SettingACapToUnboundedIsRejectedAndTheSafeValuePersists()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        int safe = vm.DestructiveCap;
        vm.DestructiveCap = int.MaxValue;                    // the FR-35 "unbounded" disabling sentinel.

        Assert.Equal(safe, vm.DestructiveCap);
        Assert.NotEqual(string.Empty, vm.CapValidationMessage);
        Assert.True(CapClamp.IsValid(vm.Caps));
    }

    [Fact]
    public async Task MagnitudePercentOutOfRangeIsRejected()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        int safe = vm.DestructiveMagnitudePercent;
        vm.DestructiveMagnitudePercent = 0;                  // outside 1..100 -> rejected.

        Assert.Equal(safe, vm.DestructiveMagnitudePercent);
        Assert.NotEqual(string.Empty, vm.CapValidationMessage);
    }

    [Fact]
    public async Task SettingACapToAValidValueIsAcceptedAndClearsTheMessage()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        vm.CreateCap = 0;                                    // reject -> message set.
        Assert.NotEqual(string.Empty, vm.CapValidationMessage);

        vm.CreateCap = 42;                                   // a valid, bounded, positive value -> accepted.
        Assert.Equal(42, vm.CreateCap);
        Assert.Equal(string.Empty, vm.CapValidationMessage); // message cleared on the accepted edit.
        Assert.True(CapClamp.IsValid(vm.Caps));
    }

    [Fact]
    public async Task RejectedCapsNeverPersistToDiskOnSave()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        vm.CreateCap = 0;                                    // rejected — _caps keeps the safe value.
        await vm.SaveCommand.ExecuteAsync(null);            // persist through the VM's Save command.

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.True(CapClamp.IsValid(reloaded.Caps));       // a disabling cap NEVER reaches disk.
        Assert.Equal(CapConfig.PinnedCreateCap, reloaded.Caps.CreateCap); // still the safe (default) value.
    }
}

public sealed class SettingsViewModelTests
{
    private static async Task<(SettingsViewModel vm, TempAppDataPaths paths)> NewVmAsync()
    {
        var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);
        return (vm, paths);
    }

    [Fact]
    public async Task AutoSaveIntervalClampsToTheDocumentedCeiling()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        vm.AutoSaveSeconds = 999;                            // above the 30 s hard ceiling (design §7.5).
        Assert.Equal(HoardPadSettings.MaxAutoSaveSeconds, vm.AutoSaveSeconds);

        vm.AutoSaveSeconds = 0;                              // a non-positive interval would disable auto-save.
        Assert.Equal(1, vm.AutoSaveSeconds);                // clamped up to the 1 s floor.
    }

    [Fact]
    public async Task EditedFieldsRoundTripThroughSaveAndReload()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        vm.ActiveBackend = AiBackend.Local;
        vm.AgencyMode = AgencyMode.ReadOnly;
        vm.Theme = AppTheme.Light;                           // persisted-only (the live switch is M7.1).
        vm.AiPanelHotkey = "Ctrl+Shift+P";
        vm.NoReplySeconds = 75;
        vm.LargeRootThreshold = 3000;
        vm.CreateCap = 18;

        await vm.SaveCommand.ExecuteAsync(null);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(AiBackend.Local, reloaded.ActiveBackend);
        Assert.Equal(AgencyMode.ReadOnly, reloaded.AgencyMode);
        Assert.Equal(AppTheme.Light, reloaded.Theme);
        Assert.Equal("Ctrl+Shift+P", reloaded.AiPanelHotkey);
        Assert.Equal(75, reloaded.NoReplySeconds);
        Assert.Equal(3000, reloaded.LargeRootThreshold);
        Assert.Equal(18, reloaded.Caps.CreateCap);
    }

    [Fact]
    public async Task SaveStatusMessageConfirmsSaveThenClearsOnNextEdit()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        Assert.Equal(string.Empty, vm.SaveStatusMessage);    // no stale confirmation before any save.

        vm.NoReplySeconds = 42;
        Assert.Equal(string.Empty, vm.SaveStatusMessage);    // an edit alone never claims a save happened.

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage)); // Save explicitly confirms persistence.

        vm.NoReplySeconds = 99;                              // any further edit invalidates the confirmation
        Assert.Equal(string.Empty, vm.SaveStatusMessage);    // (on-screen values now diverge from disk again).
    }

    [Fact]
    public async Task SettingsTabIsNonPersistedAndNamedSettings()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        HoardPad.App.Session.DocumentTab tab =
            HoardPad.App.Session.DocumentTab.ForSettings("__settings__", vm);

        Assert.True(tab.IsSettings);                         // excluded from the session index (ODQ-12).
        Assert.False(tab.IsChecklist);
        Assert.Equal("Settings", tab.DisplayName);
        Assert.Same(vm, tab.Settings);
    }

    // ----- BUG-5: model + effort tests -----

    [Fact]
    public async Task ModelAndEffortDefaultsToEmptyStrings()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        Assert.Equal(string.Empty, vm.ModelId);
        Assert.Equal(string.Empty, vm.EffortLevel);
        Assert.False(vm.IsCustomModel);
        Assert.Equal(string.Empty, vm.SelectedModelOption);
        Assert.Equal(string.Empty, vm.CustomModelId);
    }

    [Fact]
    public async Task SelectingAKnownModelPresetSetsModelIdToThatValue()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        vm.SelectedModelOption = "claude-sonnet-4-6";

        Assert.Equal("claude-sonnet-4-6", vm.ModelId);
        Assert.False(vm.IsCustomModel);
        Assert.Equal(string.Empty, vm.CustomModelId);
    }

    [Fact]
    public async Task SelectingCustomOtherActivatesCustomEntryAndModelIdTracksIt()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        vm.SelectedModelOption = "Custom/Other...";
        Assert.True(vm.IsCustomModel);

        vm.CustomModelId = "my-custom-model-v2";
        Assert.Equal("my-custom-model-v2", vm.ModelId);
    }

    [Fact]
    public async Task SwitchingFromCustomToPresetClearsCustomModelId()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        vm.SelectedModelOption = "Custom/Other...";
        vm.CustomModelId = "custom-v1";
        Assert.Equal("custom-v1", vm.ModelId);

        vm.SelectedModelOption = "claude-haiku-4-5-20251001";
        Assert.Equal("claude-haiku-4-5-20251001", vm.ModelId);
        Assert.False(vm.IsCustomModel);
        Assert.Equal(string.Empty, vm.CustomModelId);
    }

    [Fact]
    public async Task ModelAndEffortRoundTripThroughToSettingsAndApply()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        // Set a known preset model + effort via the VM surface.
        vm.SelectedModelOption = "claude-opus-4-8";
        vm.EffortLevel = "medium";

        // Verify that ToSettings() produces the correct record.
        HoardPadSettings saved = vm.ToSettings();
        Assert.Equal("claude-opus-4-8", saved.ModelId);
        Assert.Equal("medium", saved.EffortLevel);

        // Persist and reload — Apply path exercised through LoadAsync.
        await store.SaveAsync(saved, CancellationToken.None);
        SettingsViewModel reloaded = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        Assert.Equal("claude-opus-4-8", reloaded.ModelId);
        Assert.Equal("medium", reloaded.EffortLevel);
        Assert.False(reloaded.IsCustomModel);
        Assert.Equal(string.Empty, reloaded.CustomModelId);
    }

    [Fact]
    public async Task CustomModelIdRoundTripsThroughToSettingsAndApply()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        vm.SelectedModelOption = "Custom/Other...";
        vm.CustomModelId = "claude-sonnet-4-20260514";
        vm.EffortLevel = "high";

        HoardPadSettings saved = vm.ToSettings();
        Assert.Equal("claude-sonnet-4-20260514", saved.ModelId);
        Assert.Equal("high", saved.EffortLevel);

        // Persist and reload — should see custom model restored via Apply.
        await store.SaveAsync(saved, CancellationToken.None);
        SettingsViewModel reloaded = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        Assert.Equal("Custom/Other...", reloaded.SelectedModelOption);
        Assert.Equal("claude-sonnet-4-20260514", reloaded.CustomModelId);
        Assert.Equal("claude-sonnet-4-20260514", reloaded.ModelId);
        Assert.True(reloaded.IsCustomModel);
        Assert.Equal("high", reloaded.EffortLevel);
    }

    [Fact]
    public async Task NullModelAndEffortInSettingsBecomeEmptyStringsOnApply()
    {
        // Simulate a HoardPadSettings with null model/effort (the default).
        var nullSettings = HoardPadSettings.Default with
        {
            ModelId = null,
            EffortLevel = null,
        };

        // Create a VM directly (not through LoadAsync) to test the Apply path.
        // We need SettingsStore, but can pass null since Apply doesn't use it.
        // Use a helper that constructs the VM without loading.
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;

        // Re-apply from the null-valued settings.
        // We can't call Apply directly (it's private). Instead, save and re-load.
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        await store.SaveAsync(nullSettings, CancellationToken.None);
        SettingsViewModel reloaded = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        Assert.Equal(string.Empty, reloaded.ModelId);
        Assert.Equal(string.Empty, reloaded.EffortLevel);
    }

    [Fact]
    public async Task ModelAndEffortPersistThroughSaveCommand()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        vm.SelectedModelOption = "claude-haiku-4-5-20251001";
        vm.EffortLevel = "low";

        await vm.SaveCommand.ExecuteAsync(null);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("claude-haiku-4-5-20251001", reloaded.ModelId);
        Assert.Equal("low", reloaded.EffortLevel);
    }

    // ----- FEAT-1: font settings tests -----

    [Fact]
    public async Task FontDefaultsAreEmptyStringAndDefaultSizes()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        Assert.Equal(string.Empty, vm.EditorFontFamily);
        Assert.Equal(HoardPadSettings.DefaultEditorFontSize, vm.EditorFontSize);
        Assert.Equal(string.Empty, vm.UiFontFamily);
        Assert.Equal(HoardPadSettings.DefaultUiFontSize, vm.UiFontSize);
    }

    [Fact]
    public async Task EditorFontSizeClampsTo8To36()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        vm.EditorFontSize = 5;   // below 8
        Assert.Equal(8, vm.EditorFontSize);

        vm.EditorFontSize = 40;  // above 36
        Assert.Equal(36, vm.EditorFontSize);

        vm.EditorFontSize = 14;  // valid
        Assert.Equal(14, vm.EditorFontSize);
    }

    [Fact]
    public async Task UiFontSizeClampsTo8To24()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        vm.UiFontSize = 3;       // below 8
        Assert.Equal(8, vm.UiFontSize);

        vm.UiFontSize = 99;      // above 24
        Assert.Equal(24, vm.UiFontSize);

        vm.UiFontSize = 12;      // valid
        Assert.Equal(12, vm.UiFontSize);
    }

    [Fact]
    public async Task FontFamilyPropertyChangesAreObservable()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        bool editorFired = false;
        bool uiFired = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.EditorFontFamily)) editorFired = true;
            if (e.PropertyName == nameof(SettingsViewModel.UiFontFamily)) uiFired = true;
        };

        vm.EditorFontFamily = "Consolas";
        Assert.True(editorFired);
        Assert.Equal("Consolas", vm.EditorFontFamily);

        vm.UiFontFamily = "system-ui";
        Assert.True(uiFired);
        Assert.Equal("system-ui", vm.UiFontFamily);
    }

    [Fact]
    public async Task FontSizePropertyChangesAreObservable()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        bool editorSizeFired = false;
        bool uiSizeFired = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.EditorFontSize)) editorSizeFired = true;
            if (e.PropertyName == nameof(SettingsViewModel.UiFontSize)) uiSizeFired = true;
        };

        vm.EditorFontSize = 16;
        Assert.True(editorSizeFired);
        Assert.Equal(16, vm.EditorFontSize);

        vm.UiFontSize = 14;
        Assert.True(uiSizeFired);
        Assert.Equal(14, vm.UiFontSize);
    }

    [Fact]
    public async Task FontSettingsRoundTripThroughSaveAndReload()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        vm.EditorFontFamily = "JetBrains Mono";
        vm.EditorFontSize = 16;
        vm.UiFontFamily = "Inter";
        vm.UiFontSize = 14;

        await vm.SaveCommand.ExecuteAsync(null);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("JetBrains Mono", reloaded.EditorFontFamily);
        Assert.Equal(16, reloaded.EditorFontSize);
        Assert.Equal("Inter", reloaded.UiFontFamily);
        Assert.Equal(14, reloaded.UiFontSize);
    }

    [Fact]
    public async Task FontSettingsToSettingsRecordProducesCorrectValues()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        vm.EditorFontFamily = "Fira Code";
        vm.EditorFontSize = 18;
        vm.UiFontFamily = "system-ui";
        vm.UiFontSize = 10;

        HoardPadSettings saved = vm.ToSettings();
        Assert.Equal("Fira Code", saved.EditorFontFamily);
        Assert.Equal(18, saved.EditorFontSize);
        Assert.Equal("system-ui", saved.UiFontFamily);
        Assert.Equal(10, saved.UiFontSize);
    }

    [Fact]
    public async Task NullFontSettingsBecomeEmptyStringsOnApply()
    {
        var nullSettings = HoardPadSettings.Default with
        {
            EditorFontFamily = null,
            UiFontFamily = null,
        };

        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        await store.SaveAsync(nullSettings, CancellationToken.None);
        SettingsViewModel reloaded = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        Assert.Equal(string.Empty, reloaded.EditorFontFamily);
        Assert.Equal(HoardPadSettings.DefaultEditorFontSize, reloaded.EditorFontSize);
        Assert.Equal(string.Empty, reloaded.UiFontFamily);
        Assert.Equal(HoardPadSettings.DefaultUiFontSize, reloaded.UiFontSize);
    }

    [Fact]
    public async Task FontSettingsChangedEventFiresOnFontPropertyChange()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        int eventCount = 0;
        vm.FontSettingsChanged += () => eventCount++;

        vm.EditorFontFamily = "Consolas";
        Assert.Equal(1, eventCount);

        vm.EditorFontSize = 20;
        Assert.Equal(2, eventCount);

        vm.UiFontFamily = "Inter";
        Assert.Equal(3, eventCount);

        vm.UiFontSize = 15;
        Assert.Equal(4, eventCount);

        // Setting to the same value should NOT fire the event (SetProperty guards).
        vm.EditorFontSize = 20;
        Assert.Equal(4, eventCount);
    }

    // ----- FEAT-15: AI-panel style settings tests -----

    [Fact]
    public async Task AiStyleDefaultsAreEmptyStringsAndDefaultSizes()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        Assert.Equal(string.Empty, vm.AiInputFontFamily);
        Assert.Equal(HoardPadSettings.DefaultAiFontSize, vm.AiInputFontSize);
        Assert.Equal(string.Empty, vm.AiResponseFontFamily);
        Assert.Equal(HoardPadSettings.DefaultAiFontSize, vm.AiResponseFontSize);
        Assert.Equal(string.Empty, vm.AiResponseColor);
        Assert.Equal(string.Empty, vm.AiUserFontFamily);
        Assert.Equal(HoardPadSettings.DefaultAiFontSize, vm.AiUserFontSize);
        Assert.Equal(string.Empty, vm.AiUserColor);
    }

    [Fact]
    public async Task AiFontSizesClampTo8To24()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        vm.AiInputFontSize = 3;
        Assert.Equal(HoardPadSettings.MinUiFontSize, vm.AiInputFontSize);
        vm.AiInputFontSize = 99;
        Assert.Equal(HoardPadSettings.MaxUiFontSize, vm.AiInputFontSize);

        vm.AiResponseFontSize = 3;
        Assert.Equal(HoardPadSettings.MinUiFontSize, vm.AiResponseFontSize);
        vm.AiResponseFontSize = 99;
        Assert.Equal(HoardPadSettings.MaxUiFontSize, vm.AiResponseFontSize);

        vm.AiUserFontSize = 3;
        Assert.Equal(HoardPadSettings.MinUiFontSize, vm.AiUserFontSize);
        vm.AiUserFontSize = 99;
        Assert.Equal(HoardPadSettings.MaxUiFontSize, vm.AiUserFontSize);
    }

    [Fact]
    public async Task AiStyleSettingsRoundTripThroughSaveAndReload()
    {
        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        vm.AiInputFontFamily = "Roboto";
        vm.AiInputFontSize = 16;
        vm.AiResponseFontFamily = "Fira Code";
        vm.AiResponseFontSize = 15;
        vm.AiResponseColor = "#E5B93D";
        vm.AiUserFontFamily = "system-ui";
        vm.AiUserFontSize = 13;
        vm.AiUserColor = "#5CC0CF";

        await vm.SaveCommand.ExecuteAsync(null);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("Roboto", reloaded.AiInputFontFamily);
        Assert.Equal(16, reloaded.AiInputFontSize);
        Assert.Equal("Fira Code", reloaded.AiResponseFontFamily);
        Assert.Equal(15, reloaded.AiResponseFontSize);
        Assert.Equal("#E5B93D", reloaded.AiResponseColor);
        Assert.Equal("system-ui", reloaded.AiUserFontFamily);
        Assert.Equal(13, reloaded.AiUserFontSize);
        Assert.Equal("#5CC0CF", reloaded.AiUserColor);

        // Reload survives a fresh VM too (the relaunch path).
        SettingsViewModel reloadedVm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);
        Assert.Equal("Roboto", reloadedVm.AiInputFontFamily);
        Assert.Equal(16, reloadedVm.AiInputFontSize);
        Assert.Equal("Fira Code", reloadedVm.AiResponseFontFamily);
        Assert.Equal("#E5B93D", reloadedVm.AiResponseColor);
        Assert.Equal("system-ui", reloadedVm.AiUserFontFamily);
        Assert.Equal("#5CC0CF", reloadedVm.AiUserColor);
    }

    [Fact]
    public async Task NullAiStyleSettingsBecomeEmptyStringsOnApply()
    {
        var nullSettings = HoardPadSettings.Default with
        {
            AiInputFontFamily = null,
            AiResponseFontFamily = null,
            AiResponseColor = null,
            AiUserFontFamily = null,
            AiUserColor = null,
        };

        (SettingsViewModel vm, TempAppDataPaths paths) = await NewVmAsync();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        await store.SaveAsync(nullSettings, CancellationToken.None);
        SettingsViewModel reloaded = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        Assert.Equal(string.Empty, reloaded.AiInputFontFamily);
        Assert.Equal(string.Empty, reloaded.AiResponseFontFamily);
        Assert.Equal(string.Empty, reloaded.AiResponseColor);
        Assert.Equal(string.Empty, reloaded.AiUserFontFamily);
        Assert.Equal(string.Empty, reloaded.AiUserColor);
    }

    [Fact]
    public async Task AiStyleSettingsChangedEventFiresOnEveryAiPropertyChange()
    {
        (SettingsViewModel vm, TempAppDataPaths _) = await NewVmAsync();

        int eventCount = 0;
        vm.FontSettingsChanged += () => eventCount++;

        vm.AiInputFontFamily = "Roboto"; Assert.Equal(1, eventCount);
        vm.AiInputFontSize = 16; Assert.Equal(2, eventCount);
        vm.AiResponseFontFamily = "Fira Code"; Assert.Equal(3, eventCount);
        vm.AiResponseFontSize = 15; Assert.Equal(4, eventCount);
        vm.AiResponseColor = "#E5B93D"; Assert.Equal(5, eventCount);
        vm.AiUserFontFamily = "system-ui"; Assert.Equal(6, eventCount);
        vm.AiUserFontSize = 13; Assert.Equal(7, eventCount);
        vm.AiUserColor = "#5CC0CF"; Assert.Equal(8, eventCount);
    }
}

/// <summary>
/// QA2-9/QA2-10: the Save button must be reachable regardless of scroll position or window height — the
/// original bug was the button sitting at the end of a long scrollable form, clipped under the window's
/// footer bar. These tests host a REAL <see cref="HoardPad.App.Views.SettingsView"/> in a REAL headless
/// Window sized short enough that the old bottom-of-scroll placement would have clipped the button, and
/// assert it renders with non-zero hit-test bounds fully inside the window.
/// </summary>
public sealed class SettingsViewLayoutTests
{
    [AvaloniaFact]
    public async Task SaveButtonRemainsVisibleAndHitTestableInAShortWindow()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        // Short enough that the pre-fix bottom-of-scroll Save button would render below the visible area.
        var window = new Window { Content = view, Width = 640, Height = 300 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Button? saveButton = view
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "Save");

        Assert.NotNull(saveButton);
        Assert.True(saveButton!.IsVisible);
        Assert.True(saveButton.IsEffectivelyVisible);
        Assert.True(saveButton.Bounds.Width > 0 && saveButton.Bounds.Height > 0);

        Point? topLeft = saveButton.TranslatePoint(new Point(0, 0), window);
        Point? bottomRight = saveButton.TranslatePoint(
            new Point(saveButton.Bounds.Width, saveButton.Bounds.Height), window);
        Assert.True(topLeft is not null && bottomRight is not null);
        Assert.True(topLeft!.Value.Y >= 0 && bottomRight!.Value.Y <= window.Bounds.Height,
            $"Save button (Y {topLeft.Value.Y}..{bottomRight.Value.Y}) must be fully within the window height {window.Bounds.Height}");

        window.Close();
    }

    [AvaloniaFact]
    public async Task SavingUpdatesTheStatusMessageVisibleNextToTheButton()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 640, Height = 300 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        await vm.SaveCommand.ExecuteAsync(null);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        TextBlock? statusText = view
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == vm.SaveStatusMessage && !string.IsNullOrEmpty(t.Text));

        Assert.NotNull(statusText);
        Assert.True(statusText!.IsEffectivelyVisible);

        window.Close();
    }
}

/// <summary>
/// QA3-1: on relaunch, nothing re-applied the persisted font settings — ApplyLiveFonts (SettingsView) was
/// the ONLY writer of the Brand.Font.* resources, so a saved non-default font silently reverted to the
/// BrandTheme.axaml compiled defaults at the next launch. These tests exercise <see cref="SettingsFontApplier"/>
/// directly — the fix's single shared writer — proving (1) the startup path re-applies the PERSISTED value
/// (not the compiled default), (2) an out-of-range persisted value is clamped before it reaches
/// Application.Resources, and (3) the live-edit call site and the startup call site can never drift apart
/// because they both funnel through the same helper.
/// </summary>
public sealed class SettingsFontApplierTests
{
    [AvaloniaFact]
    public async Task StartupReAppliesThePersistedFontSizeRatherThanTheCompiledDefault()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        // Simulate a prior session: the user changed + saved a non-default font size.
        var saved = HoardPadSettings.Default with { EditorFontSize = 30, UiFontSize = 20 };
        await store.SaveAsync(saved, CancellationToken.None);

        // Simulate the NEXT launch: the composition-time synchronous Load() (the same call
        // App.axaml.cs seeds the HoardPadSettings singleton with), then the QA3-1 startup re-apply.
        HoardPadSettings reloaded = store.Load();
        Assert.Equal(30, reloaded.EditorFontSize); // disk persistence already worked pre-fix (sanity).

        Application app = Application.Current!;
        SettingsFontApplier.Apply(app, reloaded);

        Assert.Equal(30d, (double)app.Resources["Brand.Font.CodeSize"]!);
        Assert.NotEqual((double)HoardPadSettings.DefaultEditorFontSize, (double)app.Resources["Brand.Font.CodeSize"]!);
        Assert.Equal(20d, (double)app.Resources["Brand.Font.UiSize"]!);
    }

    [AvaloniaFact]
    public async Task StartupReAppliesThePersistedFontFamilyRatherThanTheCompiledDefault()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        // Simulate a prior session: the user changed + saved non-default font FAMILIES (not just sizes —
        // the size-only proof above cannot stand in for family, since the compiled-default fallback is a
        // different code path: string.IsNullOrEmpty(family) ? DefaultXxxFontFallback : family).
        var saved = HoardPadSettings.Default with { EditorFontFamily = "JetBrains Mono", UiFontFamily = "Roboto" };
        await store.SaveAsync(saved, CancellationToken.None);

        // Simulate the NEXT launch: the composition-time synchronous Load() (the same call App.axaml.cs
        // seeds the HoardPadSettings singleton with), then the QA3-1 startup re-apply.
        HoardPadSettings reloaded = store.Load();
        Assert.Equal("JetBrains Mono", reloaded.EditorFontFamily); // disk persistence already worked pre-fix (sanity).

        Application app = Application.Current!;
        SettingsFontApplier.Apply(app, reloaded);

        string appliedCodeFamily = ((FontFamily)app.Resources["Brand.Font.Code"]!).Name;
        string appliedUiFamily = ((FontFamily)app.Resources["Brand.Font.Ui"]!).Name;

        Assert.Equal("JetBrains Mono", appliedCodeFamily);
        Assert.NotEqual(HoardPadSettings.DefaultCodeFontFallback, appliedCodeFamily);
        Assert.Equal("Roboto", appliedUiFamily);
        Assert.NotEqual(HoardPadSettings.DefaultUiFontFallback, appliedUiFamily);
    }

    [AvaloniaFact]
    public void ApplyClampsAnOutOfRangePersistedFontSizeBeforeWritingToApplicationResources()
    {
        // A hand-edited (or future AI-written) settings.json is not validated on load — HoardPadSettings is
        // a raw record. The startup helper must clamp through the same bounds the VM setters use.
        var outOfRange = HoardPadSettings.Default with { EditorFontSize = 999, UiFontSize = -5 };

        Application app = Application.Current!;
        SettingsFontApplier.Apply(app, outOfRange);

        Assert.Equal((double)HoardPadSettings.MaxEditorFontSize, (double)app.Resources["Brand.Font.CodeSize"]!);
        Assert.Equal((double)HoardPadSettings.MinUiFontSize, (double)app.Resources["Brand.Font.UiSize"]!);
    }

    [AvaloniaFact]
    public async Task LiveEditPathAndStartupPathProduceIdenticalResourcesForTheSameSettingsNoDrift()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 640, Height = 900 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Drive the LIVE-EDIT call site (SettingsView.ApplyLiveFonts, wired to FontSettingsChanged).
        vm.EditorFontFamily = "Fira Code";
        vm.EditorFontSize = 22;
        vm.UiFontFamily = string.Empty; // falls back to the brand.md §5 default chain.
        vm.UiFontSize = 11;

        Application app = Application.Current!;
        var liveCodeFamily = ((FontFamily)app.Resources["Brand.Font.Code"]!).Name;
        var liveCodeSize = (double)app.Resources["Brand.Font.CodeSize"]!;
        var liveUiFamily = ((FontFamily)app.Resources["Brand.Font.Ui"]!).Name;
        var liveUiSize = (double)app.Resources["Brand.Font.UiSize"]!;

        // Overwrite with a sentinel so the STARTUP call site's own write below is unambiguous.
        app.Resources["Brand.Font.Code"] = new FontFamily("sentinel-not-a-real-font");

        // Drive the STARTUP call site directly, from a HoardPadSettings shaped exactly like the VM's state.
        SettingsFontApplier.Apply(app, vm.ToSettings());

        Assert.Equal(liveCodeFamily, ((FontFamily)app.Resources["Brand.Font.Code"]!).Name);
        Assert.Equal(liveCodeSize, (double)app.Resources["Brand.Font.CodeSize"]!);
        Assert.Equal(liveUiFamily, ((FontFamily)app.Resources["Brand.Font.Ui"]!).Name);
        Assert.Equal(liveUiSize, (double)app.Resources["Brand.Font.UiSize"]!);

        window.Close();
    }
}

/// <summary>
/// FEAT-15: AI panel style (input font, response font/color, user-message font/color). Mirrors
/// <see cref="SettingsFontApplierTests"/> exactly — <see cref="AiPanelStyleApplier"/> is the single shared
/// writer for the live-edit path (<c>SettingsView.ApplyLiveFonts</c>) and the startup re-apply path
/// (<c>App.axaml.cs</c>), so it must prove: (1) the startup path re-applies the PERSISTED value, (2) an
/// out-of-range persisted size is clamped, (3) a color override resolves to a fixed brush while an
/// absent/invalid override falls back to the live on-surface theme token, and (4) the live-edit and
/// startup call sites can never drift apart.
/// </summary>
public sealed class AiPanelStyleApplierTests
{
    [AvaloniaFact]
    public async Task StartupReAppliesThePersistedAiFontSizeRatherThanTheCompiledDefault()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        var saved = HoardPadSettings.Default with { AiInputFontSize = 20, AiResponseFontSize = 18, AiUserFontSize = 16 };
        await store.SaveAsync(saved, CancellationToken.None);

        HoardPadSettings reloaded = store.Load();
        Application app = Application.Current!;
        AiPanelStyleApplier.Apply(app, reloaded);

        Assert.Equal(20d, (double)app.Resources["Brand.Font.AiInputSize"]!);
        Assert.Equal(18d, (double)app.Resources["Brand.Font.AiResponseSize"]!);
        Assert.Equal(16d, (double)app.Resources["Brand.Font.AiUserSize"]!);
        Assert.NotEqual((double)HoardPadSettings.DefaultAiFontSize, (double)app.Resources["Brand.Font.AiInputSize"]!);
    }

    [AvaloniaFact]
    public async Task StartupReAppliesThePersistedAiFontFamilyRatherThanTheCompiledDefault()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        var saved = HoardPadSettings.Default with
        {
            AiInputFontFamily = "Roboto",
            AiResponseFontFamily = "Fira Code",
            AiUserFontFamily = "system-ui",
        };
        await store.SaveAsync(saved, CancellationToken.None);

        HoardPadSettings reloaded = store.Load();
        Application app = Application.Current!;
        AiPanelStyleApplier.Apply(app, reloaded);

        Assert.Equal("Roboto", ((FontFamily)app.Resources["Brand.Font.AiInput"]!).Name);
        Assert.Equal("Fira Code", ((FontFamily)app.Resources["Brand.Font.AiResponse"]!).Name);
        Assert.Equal("system-ui", ((FontFamily)app.Resources["Brand.Font.AiUser"]!).Name);
    }

    [AvaloniaFact]
    public void ApplyClampsAnOutOfRangePersistedAiFontSizeBeforeWritingToApplicationResources()
    {
        var outOfRange = HoardPadSettings.Default with { AiInputFontSize = 999, AiResponseFontSize = -5, AiUserFontSize = 999 };

        Application app = Application.Current!;
        AiPanelStyleApplier.Apply(app, outOfRange);

        Assert.Equal((double)HoardPadSettings.MaxUiFontSize, (double)app.Resources["Brand.Font.AiInputSize"]!);
        Assert.Equal((double)HoardPadSettings.MinUiFontSize, (double)app.Resources["Brand.Font.AiResponseSize"]!);
        Assert.Equal((double)HoardPadSettings.MaxUiFontSize, (double)app.Resources["Brand.Font.AiUserSize"]!);
    }

    [AvaloniaFact]
    public void AValidColorOverrideWritesAFixedSolidColorBrush()
    {
        Application app = Application.Current!;
        AiPanelStyleApplier.Apply(
            app,
            inputFontFamily: null, inputFontSize: 12,
            responseFontFamily: null, responseFontSize: 12, responseColor: "#E5B93D",
            userFontFamily: null, userFontSize: 12, userColor: "#5CC0CF");

        var responseBrush = Assert.IsType<SolidColorBrush>(app.Resources["Brand.Color.AiResponse"]);
        var userBrush = Assert.IsType<SolidColorBrush>(app.Resources["Brand.Color.AiUser"]);
        Assert.Equal(Color.Parse("#E5B93D"), responseBrush.Color);
        Assert.Equal(Color.Parse("#5CC0CF"), userBrush.Color);
    }

    [AvaloniaFact]
    public void AnAbsentOrInvalidColorOverrideRemovesTheResourceKeyRatherThanFreezingAStaleColor()
    {
        Application app = Application.Current!;
        // First set a real override so the key exists...
        AiPanelStyleApplier.Apply(
            app,
            inputFontFamily: null, inputFontSize: 12,
            responseFontFamily: null, responseFontSize: 12, responseColor: "#E5B93D",
            userFontFamily: null, userFontSize: 12, userColor: "not-a-color");

        // ...then clear it (empty) and pass a garbage hex for the other — both must be ABSENT afterward, so
        // TranscriptForegroundConverter falls through to the live on-surface theme token.
        AiPanelStyleApplier.Apply(
            app,
            inputFontFamily: null, inputFontSize: 12,
            responseFontFamily: null, responseFontSize: 12, responseColor: string.Empty,
            userFontFamily: null, userFontSize: 12, userColor: "not-a-color");

        Assert.False(app.Resources.TryGetValue("Brand.Color.AiResponse", out _));
        Assert.False(app.Resources.TryGetValue("Brand.Color.AiUser", out _));
    }

    [AvaloniaFact]
    public async Task LiveEditPathAndStartupPathProduceIdenticalResourcesForTheSameSettingsNoDrift()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 640, Height = 1400 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Drive the LIVE-EDIT call site (SettingsView.ApplyLiveFonts, wired to FontSettingsChanged).
        vm.AiResponseFontFamily = "Fira Code";
        vm.AiResponseFontSize = 19;
        vm.AiResponseColor = "#E5B93D";
        vm.AiUserColor = string.Empty; // falls back to the live on-surface theme token.

        Application app = Application.Current!;
        var liveResponseFamily = ((FontFamily)app.Resources["Brand.Font.AiResponse"]!).Name;
        var liveResponseSize = (double)app.Resources["Brand.Font.AiResponseSize"]!;
        var liveResponseColor = ((SolidColorBrush)app.Resources["Brand.Color.AiResponse"]!).Color;
        bool liveUserColorAbsent = !app.Resources.TryGetValue("Brand.Color.AiUser", out _);

        // Overwrite with a sentinel so the STARTUP call site's own write below is unambiguous.
        app.Resources["Brand.Font.AiResponse"] = new FontFamily("sentinel-not-a-real-font");

        // Drive the STARTUP call site directly, from a HoardPadSettings shaped exactly like the VM's state.
        AiPanelStyleApplier.Apply(app, vm.ToSettings());

        Assert.Equal(liveResponseFamily, ((FontFamily)app.Resources["Brand.Font.AiResponse"]!).Name);
        Assert.Equal(liveResponseSize, (double)app.Resources["Brand.Font.AiResponseSize"]!);
        Assert.Equal(liveResponseColor, ((SolidColorBrush)app.Resources["Brand.Color.AiResponse"]!).Color);
        Assert.Equal(liveUserColorAbsent, !app.Resources.TryGetValue("Brand.Color.AiUser", out _));

        window.Close();
    }
}

/// <summary>
/// QA3-1 panel review, Critical (project-manager finding): <c>Brand.Font.Ui</c>/<c>Brand.Font.UiSize</c>
/// were WRITTEN by <see cref="SettingsFontApplier"/> but had ZERO consumers — <c>MainWindow.axaml</c>
/// hardcoded <c>FontFamily="Inter"</c> (a literal, not a <c>DynamicResource</c>), so a user who changed the
/// UI chrome font/size, saved, and relaunched saw the value echoed back in the Settings tab with NO visible
/// effect anywhere — verbatim the QA3-1 symptom for the UI-font half of the fix. Fixed by rebinding
/// MainWindow's (and every dialog window's) FontFamily/FontSize to <c>{DynamicResource Brand.Font.Ui}</c> /
/// <c>{DynamicResource Brand.Font.UiSize}</c>. This test proves the REAL rendered MainWindow chrome
/// actually reflows — not just that the resource dictionary holds the right value.
/// </summary>
public sealed class SettingsFontChromeReflowTests
{
    [AvaloniaFact]
    public void ApplyingANonDefaultUiFontReflowsTheRealMainWindowChrome()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new HoardPad.App.Views.MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Sanity: "Roboto" is not the compiled-default chrome font (brand.md §5 Inter stack) before any
        // Settings change — otherwise the assertion below would pass even with the old hardcoded literal.
        Assert.NotEqual("Roboto", window.FontFamily.Name);

        SettingsFontApplier.Apply(Application.Current!, "Consolas", 14, "Roboto", 17);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Roboto", window.FontFamily.Name);
        Assert.Equal(17d, window.FontSize);

        window.Close();
    }

    /// <summary>
    /// QA3-1 defensive-hardening regression (hostile-review escalation, root cause NOT confirmed on this
    /// machine): a live process-boundary hostile review reported the chrome's
    /// DynamicResource losing to later theme machinery in a real windowed process. <see
    /// cref="SettingsFontApplier.ReassertWindowFont"/> re-asserts the font as a LOCAL value independent of
    /// DynamicResource propagation. This test simulates the theorized clobber directly (something resets
    /// the Window's font back to the compiled default AFTER the resource write) and proves the reassert
    /// still wins — the mechanism does not depend on whatever clobbered it.
    /// </summary>
    [AvaloniaFact]
    public void ReassertWindowFontWinsEvenIfSomethingElseClobberedTheWindowFontAfterApply()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new HoardPad.App.Views.MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        SettingsFontApplier.Apply(Application.Current!, "Consolas", 14, "Roboto", 17);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Simulate the theorized failure mode: something resets the Window's font to the compiled default
        // AFTER Apply already ran (the exact symptom the hostile review reported).
        window.FontFamily = new FontFamily("Inter, system-ui, Segoe UI, Roboto, Noto Sans, sans-serif");
        window.FontSize = HoardPadSettings.DefaultUiFontSize;

        SettingsFontApplier.ReassertWindowFont(window);

        Assert.Equal("Roboto", window.FontFamily.Name);
        Assert.Equal(17d, window.FontSize);

        window.Close();
    }
}

/// <summary>
/// QA3-1 panel review, Warning (hostile-reviewer finding): no test proved <c>App.axaml.cs</c>'s
/// <c>OnFrameworkInitializationCompleted</c> actually CALLS the startup font re-apply, in the right order
/// (after the theme applies) — a future edit that drops or misorders that call would reintroduce QA3-1
/// with zero test failures (this project's dead-wiring regression pattern, Run 9). Fixed by extracting the
/// two previously-independent call-site lines into one atomic, named seam
/// (<see cref="HoardPad.App.App.ApplyStartupThemeAndFonts"/>) that <c>OnFrameworkInitializationCompleted</c>
/// calls — there is no longer a second line to drop or reorder. This test builds the REAL
/// <see cref="CompositionRoot.ConfigureServices"/> registration set (pointed at a throwaway temp profile,
/// the same override-after-configure pattern <c>Run12AiClusterVerifyTests</c> uses so the real user's
/// app-data is never touched) with a <c>settings.json</c> PRE-SEEDED to a non-default font size, then calls
/// the real seam directly and asserts the PERSISTED value — not the compiled default — reaches
/// <see cref="Application.Resources"/>.
/// </summary>
public sealed class SettingsFontApplierAppStartupWiringTests
{
    [AvaloniaFact]
    public async Task AppStartupSeamAppliesThePersistedFontAfterTheming()
    {
        using var paths = new TempAppDataPaths();

        // Pre-seed settings.json with a non-default font size, exactly as a prior session's Save would.
        var seedStore = new SettingsStore(new RealAtomicFileStore(), paths);
        await seedStore.SaveAsync(HoardPadSettings.Default with { EditorFontSize = 28 }, CancellationToken.None);

        var services = new ServiceCollection();
        CompositionRoot.ConfigureServices(services);
        // Last wins: point the REAL production seam map at the throwaway profile above, so this exercises
        // the exact registration set App.axaml.cs resolves against, without touching the real user's app-data.
        services.AddSingleton<HoardPad.Core.Host.Abstractions.IAppDataPaths>(paths);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        HoardPad.App.App.ApplyStartupThemeAndFonts(provider, Application.Current!);

        Assert.Equal(28d, (double)Application.Current!.Resources["Brand.Font.CodeSize"]!);
        Assert.NotEqual(
            (double)HoardPadSettings.DefaultEditorFontSize, (double)Application.Current!.Resources["Brand.Font.CodeSize"]!);
    }

    /// <summary>FEAT-15: the same startup seam also re-applies the persisted AI-panel style (AiPanelStyleApplier).</summary>
    [AvaloniaFact]
    public async Task AppStartupSeamAppliesThePersistedAiStyleAfterTheming()
    {
        using var paths = new TempAppDataPaths();

        var seedStore = new SettingsStore(new RealAtomicFileStore(), paths);
        await seedStore.SaveAsync(
            HoardPadSettings.Default with { AiResponseFontSize = 21, AiResponseColor = "#E5B93D" },
            CancellationToken.None);

        var services = new ServiceCollection();
        CompositionRoot.ConfigureServices(services);
        services.AddSingleton<HoardPad.Core.Host.Abstractions.IAppDataPaths>(paths);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        HoardPad.App.App.ApplyStartupThemeAndFonts(provider, Application.Current!);

        Assert.Equal(21d, (double)Application.Current!.Resources["Brand.Font.AiResponseSize"]!);
        var brush = Assert.IsType<SolidColorBrush>(Application.Current!.Resources["Brand.Color.AiResponse"]);
        Assert.Equal(Color.Parse("#E5B93D"), brush.Color);
    }
}

/// <summary>
/// QA3-1 part 3 (hostile-reviewer objection, verified not fixed): Avalonia's <c>NumericUpDown.Value</c>
/// is TwoWay by default and pushes the parsed value back to the bound VM property on every valid keystroke
/// (via <c>SetCurrentValue</c>), NOT only on <c>LostFocus</c>/Enter — confirmed by reading the Avalonia
/// 11.3.6 source (<c>TextBoxOnTextChanged</c> -&gt; <c>OnTextChanged</c> -&gt; <c>SyncTextAndValueProperties</c>
/// -&gt; <c>SetValueInternal</c>). These tests drive the REAL rendered <see cref="NumericUpDown"/> controls
/// in <see cref="HoardPad.App.Views.SettingsView"/> with simulated keyboard text input and assert the VM
/// already reflects the typed value with NO explicit focus loss / commit step — proving "type then click
/// Save without tabbing away" already works, so no binding-trigger fix is needed.
/// </summary>
public sealed class SettingsFontSizeNumericUpDownCommitTests
{
    /// <summary>
    /// Give the NumericUpDown keyboard focus directly (rather than a coordinate-based click — the
    /// settings form scrolls, so a click target's on-screen position is scroll-offset-dependent). Real
    /// keyboard/mouse focus lands here the same way: NumericUpDown.OnGotFocus forwards focus to its
    /// internal "PART_TextBox" and auto-selects its text (Avalonia 11.3.6 FocusChanged), so this
    /// exercises the exact same commit path a live click-then-type would.
    /// </summary>
    private static void FocusAndSelectAll(NumericUpDown control)
    {
        control.Focus();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task TypingIntoEditorFontSizeWithoutLosingFocusCommitsToTheVmBeforeSave()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 640, Height = 900 };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Editor font size is the only NumericUpDown with Maximum=36 (the design §7.3 8..36 range).
        NumericUpDown? editorFontSizeControl = view
            .GetVisualDescendants()
            .OfType<NumericUpDown>()
            .FirstOrDefault(n => n.Maximum == 36);
        Assert.NotNull(editorFontSizeControl);

        FocusAndSelectAll(editorFontSizeControl!); // focuses the control (auto-selects its text).
        window.KeyTextInput("30");                         // replaces the selection — no LostFocus/Enter yet.
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(30, vm.EditorFontSize);          // committed to the VM with focus still on the control.

        await vm.SaveCommand.ExecuteAsync(null);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(30, reloaded.EditorFontSize);

        window.Close();
    }

    [AvaloniaFact]
    public async Task TypingIntoUiFontSizeWithoutLosingFocusCommitsToTheVmBeforeSave()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 640, Height = 900 };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // UI font size is the only NumericUpDown with Maximum=24 (the design §7.3 8..24 range).
        NumericUpDown? uiFontSizeControl = view
            .GetVisualDescendants()
            .OfType<NumericUpDown>()
            .FirstOrDefault(n => n.Maximum == 24);
        Assert.NotNull(uiFontSizeControl);

        FocusAndSelectAll(uiFontSizeControl!);
        window.KeyTextInput("18");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(18, vm.UiFontSize);

        await vm.SaveCommand.ExecuteAsync(null);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(18, reloaded.UiFontSize);

        window.Close();
    }
}

/// <summary>
/// QA2-12: "Default new-doc language" must be a populated dropdown (ComboBox), not a free-text box —
/// the original bug was a bare <c>TextBox</c> pre-filled with "Plain Text" and no guidance. These tests
/// host a REAL <see cref="HoardPad.App.Views.SettingsView"/> and assert the control the view renders for
/// that row is a <see cref="ComboBox"/> with a non-empty, "Plain Text"-first item source and no sibling
/// free-text box for the same setting.
/// </summary>
public sealed class SettingsLanguageDropdownTests
{
    [Fact]
    public void LanguageOptionsIsNonEmptyAndPlainTextIsFirst()
    {
        Assert.NotEmpty(SettingsViewModel.LanguageOptions);
        Assert.Equal(HoardPadSettings.DefaultLanguageLabel, SettingsViewModel.LanguageOptions[0]);
        // FR-7 lists 20 languages; plus the "Plain Text" default = 21 entries.
        Assert.Equal(21, SettingsViewModel.LanguageOptions.Count);
    }

    [AvaloniaFact]
    public async Task DefaultLanguageRendersAsAPopulatedComboBoxPreselectedToPlainTextOnFirstUse()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 640, Height = 900 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        ComboBox? languageCombo = view
            .GetVisualDescendants()
            .OfType<ComboBox>()
            .FirstOrDefault(c => c.ItemsSource == SettingsViewModel.LanguageOptions);

        Assert.NotNull(languageCombo);
        Assert.True(languageCombo!.ItemCount > 0);
        Assert.Equal(HoardPadSettings.DefaultLanguageLabel, languageCombo.SelectedItem);

        window.Close();
    }
}
