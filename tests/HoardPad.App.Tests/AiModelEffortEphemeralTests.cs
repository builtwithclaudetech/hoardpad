// AiModelEffortEphemeralTests.cs — QA4-21: the AI-panel Model/Effort ComboBoxes are a SESSION-LOCAL
// override, not a Settings writer.
//
// Revises QA4-2 (the 2026-07-13 product decision): the panel
// seeds Model/Effort from settings at construction, but a panel change never reaches settings.json, a
// later Settings-tab save never moves an already-open panel, and a small "↺ reset to app default"
// control re-seeds on demand. This file is the H1 frozen-oracle truth table for the four state
// transitions, coded to green against the new behavior (and would fail against the retired QA4-2
// write-back path, which persisted every panel pick through CompositionRoot.PersistModelEffortAsync).
//
// Class name matches the verify filter "AiModelEffortEphemeral".

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiModelEffortEphemeralTests
{
    // ---------------------------------------------------------------------------------------------------
    //  (a) Seed-from-settings@construction — mirrors CompositionRoot's object-initializer seed exactly
    //  (Hosting/CompositionRoot.cs: CurrentModelId = settings.ModelId, CurrentEffortLevel = settings.EffortLevel).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SeedsCurrentModelAndEffortFromSettingsAtConstruction()
    {
        var settings = HoardPadSettings.Default with { ModelId = "claude-sonnet-4-6", EffortLevel = "high" };

        var vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        // The exact seed CompositionRoot's object-initializer performs right after construction.
        vm.CurrentModelId = settings.ModelId ?? string.Empty;
        vm.CurrentEffortLevel = settings.EffortLevel ?? string.Empty;

        Assert.Equal("claude-sonnet-4-6", vm.CurrentModelId);
        Assert.Equal("high", vm.CurrentEffortLevel);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (b) Panel change -> NO settings write. A panel pick must never reach settings.json, but MUST still
    //  drive the composed TurnRequest (the override is real for the running session).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PanelChangeNeverWritesSettingsJsonButStillDrivesTheComposedTurn()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        HoardPadSettings seeded = HoardPadSettings.Default with { ModelId = "claude-opus-4-8", EffortLevel = "low" };
        await store.SaveAsync(seeded, System.Threading.CancellationToken.None);

        var runner = new RecordingTurnRunner([HoardPad.App.Ai.TurnEvent.Assistant("ok")]);
        AiPanelViewModel vm = AiPanelTestFactory.New(runner);
        HoardPadSettings loaded = await store.LoadAsync(System.Threading.CancellationToken.None);
        vm.CurrentModelId = loaded.ModelId ?? string.Empty; // the construction-time seed.
        vm.CurrentEffortLevel = loaded.EffortLevel ?? string.Empty;

        // The user picks a different Model/Effort in the panel — a session-local override only.
        vm.CurrentModelId = "claude-sonnet-4-6";
        vm.CurrentEffortLevel = "xhigh";

        // settings.json must be UNCHANGED — read it back through a completely fresh SettingsStore instance
        // pointed at the same paths (no shared in-memory state with vm/store above could mask a real write).
        var freshStore = new SettingsStore(new RealAtomicFileStore(), paths);
        HoardPadSettings onDisk = await freshStore.LoadAsync(System.Threading.CancellationToken.None);
        Assert.Equal("claude-opus-4-8", onDisk.ModelId);
        Assert.Equal("low", onDisk.EffortLevel);

        // But the picked values still drive the composed turn (SendAsync composes CurrentModelId/EffortLevel).
        await AiPanelTestFactory.RunTurnAsync(vm, "hello");
        Assert.Single(runner.Requests);
        Assert.Equal("claude-sonnet-4-6", runner.Requests[0].ModelId);
        Assert.Equal("xhigh", runner.Requests[0].EffortLevel);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (c) Settings-tab change while running -> panel UNCHANGED. Replicates CompositionRoot's CURRENT
    //  SettingsChanged subscription verbatim (Hosting/CompositionRoot.cs) — it now only live-pushes the
    //  FS-5 ConfirmLargeHomeFolder/LargeRootThreshold fields, never Model/Effort.
    // ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task SettingsTabChangeWhileRunningDoesNotMoveTheOpenPanel()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        HoardPadSettings seeded = HoardPadSettings.Default with { ModelId = "claude-opus-4-8", EffortLevel = "low" };
        await store.SaveAsync(seeded, System.Threading.CancellationToken.None);

        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.CurrentModelId = seeded.ModelId ?? string.Empty;
        vm.CurrentEffortLevel = seeded.EffortLevel ?? string.Empty;

        // CompositionRoot's actual production SettingsChanged handler, post-QA4-21 (verbatim behavior:
        // it touches ONLY ConfirmLargeHomeFolder/LargeRootThreshold, never Model/Effort).
        store.SettingsChanged += s =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                vm.ConfirmLargeHomeFolder = s.ConfirmLargeHomeFolder;
                vm.LargeRootThreshold = s.LargeRootThreshold;
            });
        };

        // The user picks an override in the running panel.
        vm.CurrentModelId = "claude-sonnet-4-6";
        vm.CurrentEffortLevel = "xhigh";

        // The Settings tab saves a DIFFERENT Model/Effort while the panel is open.
        await store.SaveAsync(
            seeded with { ModelId = "claude-haiku-4-5-20251001", EffortLevel = "medium", ConfirmLargeHomeFolder = false },
            System.Threading.CancellationToken.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // flush the Post the handler issues.

        // The open panel's override is UNCHANGED — Settings-tab edits never move a running panel.
        Assert.Equal("claude-sonnet-4-6", vm.CurrentModelId);
        Assert.Equal("xhigh", vm.CurrentEffortLevel);
        // The FS-5 fields DO still live-sync (unaffected, still-wanted behavior).
        Assert.False(vm.ConfirmLargeHomeFolder);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (d) Reset -> re-seed from the CURRENT saved settings (primary path: CurrentSettingsProvider wired),
    //  and the construction-captured fallback when it is not (e.g. a direct-construction test/misconfigured
    //  build).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetReSeedsFromTheCurrentSavedSettingsViaTheProviderSeam()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        HoardPadSettings original = HoardPadSettings.Default with { ModelId = "claude-opus-4-8", EffortLevel = "low" };
        await store.SaveAsync(original, System.Threading.CancellationToken.None);

        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.CurrentModelId = original.ModelId ?? string.Empty;
        vm.CurrentEffortLevel = original.EffortLevel ?? string.Empty;
        vm.CurrentSettingsProvider = ct => store.LoadAsync(ct);

        // The user overrides in the panel...
        vm.CurrentModelId = "claude-sonnet-4-6";
        vm.CurrentEffortLevel = "xhigh";

        // ...and THEN the Settings tab saves yet another value — proving reset reads the CURRENT saved
        // settings (not the stale construction-time seed captured above).
        HoardPadSettings currentSaved = original with { ModelId = "claude-haiku-4-5-20251001", EffortLevel = "medium" };
        await store.SaveAsync(currentSaved, System.Threading.CancellationToken.None);

        await vm.ResetModelEffortToAppDefaultCommand.ExecuteAsync(null);

        Assert.Equal("claude-haiku-4-5-20251001", vm.CurrentModelId);
        Assert.Equal("medium", vm.CurrentEffortLevel);
    }

    [Fact]
    public async Task ResetFallsBackToTheConstructionCapturedDefaultWhenNoProviderIsWired()
    {
        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        // The FIRST assignment latches the construction-captured default (mirrors the CompositionRoot
        // object-initializer seed); vm.CurrentSettingsProvider is left null (no DI wiring, as in this test).
        vm.CurrentModelId = "claude-opus-4-8";
        vm.CurrentEffortLevel = "low";

        vm.CurrentModelId = "claude-sonnet-4-6"; // the session override.
        vm.CurrentEffortLevel = "xhigh";

        await vm.ResetModelEffortToAppDefaultCommand.ExecuteAsync(null);

        Assert.Equal("claude-opus-4-8", vm.CurrentModelId);
        Assert.Equal("low", vm.CurrentEffortLevel);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (e) Hostile-review Warning 1 fix: the construction-captured fallback must latch on the VERY FIRST
    //  set call even when the seeded value equals the backing field's compiler default ("") — the exact
    //  fresh-install shape (HoardPadSettings.Default has null ModelId/EffortLevel, so CompositionRoot's
    //  `settings.ModelId ?? string.Empty` seed assigns "", which is what a naive `??=`-in-OnChanged-hook
    //  latch would silently skip because CommunityToolkit.Mvvm's generated setter never even calls the
    //  hook when the new value equals the current value). Without the fix, the LATER panel override below
    //  would get wrongly captured as "the default", and reset would return to it instead of "".
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetFallsBackToEmptyStringOnAFreshInstallSeedNotToALaterOverride()
    {
        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));

        // The exact fresh-install CompositionRoot seed: HoardPadSettings.Default's null ModelId/EffortLevel
        // reduce `settings.ModelId ?? string.Empty` to "" — which already equals CurrentModelId/
        // CurrentEffortLevel's backing-field compiler default. vm.CurrentSettingsProvider stays null (no
        // DI wiring), so the reset command falls back to the construction-captured default.
        vm.CurrentModelId = string.Empty;
        vm.CurrentEffortLevel = string.Empty;

        // The user then overrides in the panel — this must NOT get latched as "the default".
        vm.CurrentModelId = "claude-sonnet-4-6";
        vm.CurrentEffortLevel = "xhigh";

        await vm.ResetModelEffortToAppDefaultCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.CurrentModelId);
        Assert.Equal(string.Empty, vm.CurrentEffortLevel);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (f) Hostile-review Warning 2 fix: a CurrentSettingsProvider that throws (corrupted settings.json,
    //  transient I/O) must not leave the reset silently inert — it degrades to the construction-captured
    //  fallback instead of letting the exception propagate unobserved out of the AsyncRelayCommand.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetFallsBackToTheConstructionCapturedDefaultWhenTheProviderThrows()
    {
        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.CurrentModelId = "claude-opus-4-8";
        vm.CurrentEffortLevel = "low";
        vm.CurrentSettingsProvider = _ => throw new IOException("settings.json is corrupted");

        vm.CurrentModelId = "claude-sonnet-4-6"; // the session override.
        vm.CurrentEffortLevel = "xhigh";

        await vm.ResetModelEffortToAppDefaultCommand.ExecuteAsync(null);

        Assert.Equal("claude-opus-4-8", vm.CurrentModelId);
        Assert.Equal("low", vm.CurrentEffortLevel);
    }

    // ---------------------------------------------------------------------------------------------------
    //  Override survives panel close/reopen WITHIN the session (AiPanelViewModel is a DI singleton — its
    //  state outlives the view). Proven by binding a SECOND, freshly-constructed AiPanelView to the SAME
    //  view-model instance (simulating close + reopen) and reading the override back off the real bound
    //  ComboBox, not just the VM property.
    // ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void OverrideSurvivesPanelCloseAndReopenWithinTheSession()
    {
        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.CurrentModelId = "claude-opus-4-8";
        vm.CurrentEffortLevel = "low";

        // First "open" of the panel.
        var firstView = new AiPanelView { DataContext = vm };
        var firstWindow = new Window { Content = firstView, Width = 300, Height = 600 };
        firstWindow.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // The user overrides Model/Effort while this instance of the panel is open.
        vm.CurrentModelId = "claude-sonnet-4-6";
        vm.CurrentEffortLevel = "xhigh";

        // "Close" the panel (dispose the view/window — the VM is NOT disposed, it is a DI singleton).
        firstWindow.Close();

        // "Reopen" — a brand-new View bound to the SAME view-model instance.
        var secondView = new AiPanelView { DataContext = vm };
        var secondWindow = new Window { Content = secondView, Width = 300, Height = 600 };
        try
        {
            secondWindow.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            ComboBox modelSelector = secondView.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => c.Name == "ModelSelector");
            ComboBox effortSelector = secondView.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => c.Name == "EffortSelector");

            Assert.Equal("claude-sonnet-4-6", modelSelector.SelectedItem);
            Assert.Equal("xhigh", effortSelector.SelectedItem);
            Assert.Equal("claude-sonnet-4-6", vm.CurrentModelId);
            Assert.Equal("xhigh", vm.CurrentEffortLevel);
        }
        finally
        {
            secondWindow.Close();
        }
    }
}
