// AiFontApplyTests.cs — QA4-1: AI panel fonts applied from Settings. Class name contains "AiFontApply" so
// the schedule's verify filter "FullyQualifiedName~AiFontApply" resolves it.
//
// The FEAT-15 wiring itself (AiPanelStyleApplier as the single writer, called from BOTH the startup seam
// App.ApplyStartupThemeAndFonts and the live-edit seam SettingsView.ApplyLiveFonts) already existed before
// this task and is already covered end-to-end for the RESPONSE/USER fonts by
// SettingsFontApplierAppStartupWiringTests.AppStartupSeamAppliesThePersistedAiStyleAfterTheming and
// Feat15AiPanelStyleRenderTests.SavingAiStyleInSettingsPersistsAndAppliesLiveToTheRealAiPanel. Neither of
// those exercises the AI-panel INPUT font/size through the real end-to-end seam (the real App startup
// composition seam, and the real Settings-Save -> real rendered AiPanelView chain) — QA4-1's literal
// "AI-panel input" wording. These two tests close that gap; no production code changed (the wiring was
// already correct — this task's job was closing the missing live/startup-seam coverage for the input font
// and performing the live-GUI proof, see artifacts/qa4-1/).
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.Hosting;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Host.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiFontApplyTests
{
    /// <summary>
    /// Startup re-apply, through the REAL production seam (<c>App.ApplyStartupThemeAndFonts</c>, built off
    /// the real <see cref="CompositionRoot.ConfigureServices"/> registration set — the exact same wiring
    /// <c>OnFrameworkInitializationCompleted</c> resolves against) — for the AI-panel INPUT font/size
    /// specifically. A relaunch must reflect the persisted input font/size, not the compiled default.
    /// </summary>
    [AvaloniaFact]
    public async Task AppStartupSeamAppliesThePersistedAiInputFontAfterTheming()
    {
        using var paths = new TempAppDataPaths();
        var seedStore = new SettingsStore(new RealAtomicFileStore(), paths);
        await seedStore.SaveAsync(
            HoardPadSettings.Default with { AiInputFontFamily = "Roboto", AiInputFontSize = 20 },
            CancellationToken.None);

        var services = new ServiceCollection();
        CompositionRoot.ConfigureServices(services);
        services.AddSingleton<IAppDataPaths>(paths);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        App.ApplyStartupThemeAndFonts(provider, Application.Current!);

        Assert.Equal("Roboto", ((FontFamily)Application.Current!.Resources["Brand.Font.AiInput"]!).Name);
        Assert.Equal(20d, (double)Application.Current!.Resources["Brand.Font.AiInputSize"]!);
        Assert.NotEqual((double)HoardPadSettings.DefaultAiFontSize, (double)Application.Current!.Resources["Brand.Font.AiInputSize"]!);
    }

    /// <summary>
    /// Live-apply-on-save, through the REAL end-to-end chain: a real <see cref="SettingsViewModel"/> +
    /// rendered <see cref="SettingsView"/>, Save, then a real ALREADY-RENDERED <see cref="AiPanelView"/>'s
    /// prompt <see cref="TextBox"/> reflects the new input font/size with no relaunch — and the value
    /// round-trips through a fresh store load (the relaunch case), so there is no dead write.
    /// </summary>
    [AvaloniaFact]
    public async Task SavingAiInputFontInSettingsPersistsAndAppliesLiveToTheRealPromptBox()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel settingsVm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var settingsView = new SettingsView { DataContext = settingsVm };
        var settingsWindow = new Window { Content = settingsView, Width = 640, Height = 1400 };
        settingsWindow.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        AiPanelViewModel panelVm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        var panelView = new AiPanelView { DataContext = panelVm };
        var panelWindow = new Window { Content = panelView, Width = 300, Height = 600 };
        panelWindow.Show();
        panelWindow.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        TextBox promptBox = panelView.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "PromptBox");
        Assert.NotEqual("Roboto", promptBox.FontFamily.Name);
        Assert.NotEqual(21d, promptBox.FontSize);

        settingsVm.AiInputFontFamily = "Roboto";
        settingsVm.AiInputFontSize = 21;
        await settingsVm.SaveCommand.ExecuteAsync(null);

        panelWindow.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Roboto", promptBox.FontFamily.Name);
        Assert.Equal(21d, promptBox.FontSize);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("Roboto", reloaded.AiInputFontFamily);
        Assert.Equal(21, reloaded.AiInputFontSize);

        settingsWindow.Close();
        panelWindow.Close();
    }
}
