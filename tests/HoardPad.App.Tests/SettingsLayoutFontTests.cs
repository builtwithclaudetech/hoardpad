// SettingsLayoutFontTests.cs — QA4-5 (Settings Name/Value column divider scales with UI font) and QA4-18
// (Settings group-header font scales with UI font). Class names contain "SettingsLayout" /
// "SettingsGroupHeaderFont" so the verify filters "FullyQualifiedName~SettingsLayout" /
// "FullyQualifiedName~SettingsGroupHeaderFont" resolve every case.
//
// QA4-5 root cause: TextBlock.label carried a hardcoded Width="200" — the divider between the Name and
// Value columns never moved, so a larger UI font clipped setting names. Fixed by moving every settings
// row to a Grid.row (Auto,Auto columns) with the label column in a shared SharedSizeGroup
// ("SettingsLabelColumn") scoped by Grid.IsSharedSizeScope on the form's outer StackPanel — the label
// column now auto-sizes to the widest font-scaled label across the whole form, live. These tests drive
// the ambient inherited FontSize directly (a plain test Window has no {DynamicResource Brand.Font.UiSize}
// binding — only the REAL MainWindow.axaml does, per QA3-1's SettingsFontChromeReflowTests) — that keeps
// this test isolated to the layout/auto-size behavior QA4-5 is actually about.
//
// QA4-18 root cause: TextBlock.section hardcoded FontSize="15" — the group-header size never moved with
// the UI font setting. Fixed by binding it directly to the new derived Brand.Font.UiSizeSection resource
// (SettingsFontApplier — clampedUiSize * SectionHeaderScale), written alongside Brand.Font.UiSize. Since
// that's a direct DynamicResource on the TextBlock itself (not ambient inheritance), these tests drive it
// through the real SettingsFontApplier.Apply call — the same shared writer the live-edit and startup
// paths both use (QA3-1 "single writer" pattern) — proving the header genuinely re-measures once the
// resource changes, matching SettingsFontApplierTests' established AvaloniaFact pattern for this app.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using Xunit;

namespace HoardPad.App.Tests;

/// <summary>QA4-5: the settings-form Name/Value column divider auto-sizes to the font-scaled label content.</summary>
public sealed class SettingsLayoutTests
{
    // "Confirm each AI edit in large/home folders" — the longest label in the form (design §7.5) — is the
    // row most likely to clip under the pre-fix fixed Width="200".
    private const string LongestLabel = "Confirm each AI edit in large/home folders";

    private static async Task<(Window window, HoardPad.App.Views.SettingsView view, TempAppDataPaths paths)>
        NewShownViewAsync(double fontSize)
    {
        var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 1400, FontSize = fontSize };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return (window, view, paths);
    }

    private static TextBlock FindLabel(HoardPad.App.Views.SettingsView view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("label") && t.Text == text);

    [AvaloniaFact]
    public async Task LongestLabelIsNotClippedAtALargeUiFontSize()
    {
        (Window window, HoardPad.App.Views.SettingsView view, TempAppDataPaths paths) =
            await NewShownViewAsync(HoardPadSettings.MaxUiFontSize);
        using var _paths = paths;

        TextBlock label = FindLabel(view, LongestLabel);

        // The pre-fix Width="200" would clip: Bounds.Width would be pinned at 200 regardless of the text's
        // actual measured content width. Post-fix, the label's Auto column gives it (up to its own right
        // margin) exactly the width its content needs — well above the old hardcoded 200px cap, and equal
        // to its own content-measured DesiredSize once the 12px right margin (TextBlock.label's own Margin,
        // which DesiredSize includes and Bounds excludes) is accounted for.
        Assert.True(label.Bounds.Width > 200);
        double expectedContentWidth = label.DesiredSize.Width - label.Margin.Right;
        Assert.True(Math.Abs(expectedContentWidth - label.Bounds.Width) <= 1.0,
            $"expected the label's rendered width ({label.Bounds.Width}) to match its measured content width ({expectedContentWidth})");

        window.Close();
    }

    [AvaloniaFact]
    public async Task LabelColumnWidensAsTheUiFontSizeGrows()
    {
        (Window smallWindow, HoardPad.App.Views.SettingsView smallView, TempAppDataPaths smallPaths) =
            await NewShownViewAsync(HoardPadSettings.MinUiFontSize);
        using var _smallPaths = smallPaths;
        double smallWidth = FindLabel(smallView, LongestLabel).Bounds.Width;
        smallWindow.Close();

        (Window largeWindow, HoardPad.App.Views.SettingsView largeView, TempAppDataPaths largePaths) =
            await NewShownViewAsync(HoardPadSettings.MaxUiFontSize);
        using var _largePaths = largePaths;
        double largeWidth = FindLabel(largeView, LongestLabel).Bounds.Width;
        largeWindow.Close();

        // The divider moves with the font: a larger UI font size measures a wider label column.
        Assert.True(largeWidth > smallWidth,
            $"expected the label column to widen with the UI font (small={smallWidth}, large={largeWidth})");
    }

    [AvaloniaFact]
    public async Task EveryRowsLabelColumnSharesTheSameDividerPosition()
    {
        (Window window, HoardPad.App.Views.SettingsView view, TempAppDataPaths paths) =
            await NewShownViewAsync(HoardPadSettings.MaxUiFontSize);
        using var _paths = paths;

        // Every TextBlock.label in the form sits in a Grid column carrying the same SharedSizeGroup — their
        // right-hand edges (relative to the window) must all land at the same X, i.e. one continuous divider.
        double[] rightEdges = view
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Classes.Contains("label"))
            .Select(t =>
            {
                Point? topLeft = t.TranslatePoint(new Point(0, 0), window);
                return topLeft!.Value.X + t.Bounds.Width;
            })
            .ToArray();

        Assert.True(rightEdges.Length > 20); // sanity: the form has ~32 rows.
        double first = rightEdges[0];
        // Sub-pixel arrange rounding can differ by <1px between Grid instances sharing a group; the
        // divider is "shared" for this UI purpose as long as every row lands within a pixel of the rest.
        Assert.All(rightEdges, edge => Assert.True(
            Math.Abs(edge - first) <= 1.0, $"expected {edge} within 1px of the shared divider at {first}"));

        window.Close();
    }
}

/// <summary>QA4-18: the Settings-tab group-header font size scales with the UI font size setting.</summary>
public sealed class SettingsGroupHeaderFontTests
{
    private static async Task<(Window window, HoardPad.App.Views.SettingsView view, TempAppDataPaths paths)>
        NewShownViewAsync(int uiFontSize)
    {
        var paths = new TempAppDataPaths();
        var store = new SettingsStore(new RealAtomicFileStore(), paths);
        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);

        var view = new HoardPad.App.Views.SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 1400 };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // The QA3-1 single-writer path: real production apply (not a hand-set resource), same as
        // SettingsFontApplierTests / SettingsFontChromeReflowTests exercise elsewhere in this file.
        SettingsFontApplier.Apply(Application.Current!, vm.EditorFontFamily, vm.EditorFontSize, vm.UiFontFamily, uiFontSize);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return (window, view, paths);
    }

    private static TextBlock FindSectionHeader(HoardPad.App.Views.SettingsView view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("section") && t.Text == text);

    [AvaloniaFact]
    public async Task GroupHeaderFontSizeIsAFixedMultipleOfTheClampedUiFontSize()
    {
        (Window window, HoardPad.App.Views.SettingsView view, TempAppDataPaths paths) =
            await NewShownViewAsync(20);
        using var _paths = paths;

        TextBlock header = FindSectionHeader(view, "AI");

        Assert.Equal(20d * 1.25, header.FontSize);

        window.Close();
    }

    [AvaloniaFact]
    public async Task GroupHeaderGrowsAsTheUiFontSizeGrows()
    {
        (Window smallWindow, HoardPad.App.Views.SettingsView smallView, TempAppDataPaths smallPaths) =
            await NewShownViewAsync(HoardPadSettings.MinUiFontSize);
        using var _smallPaths = smallPaths;
        double smallHeaderSize = FindSectionHeader(smallView, "Editor").FontSize;
        smallWindow.Close();

        (Window largeWindow, HoardPad.App.Views.SettingsView largeView, TempAppDataPaths largePaths) =
            await NewShownViewAsync(HoardPadSettings.MaxUiFontSize);
        using var _largePaths = largePaths;
        double largeHeaderSize = FindSectionHeader(largeView, "Editor").FontSize;
        largeWindow.Close();

        Assert.True(largeHeaderSize > smallHeaderSize,
            $"expected the group-header font to grow with the UI font (small={smallHeaderSize}, large={largeHeaderSize})");
    }

    [AvaloniaFact]
    public async Task EveryGroupHeaderInTheFormScalesTogether()
    {
        (Window window, HoardPad.App.Views.SettingsView view, TempAppDataPaths paths) =
            await NewShownViewAsync(16);
        using var _paths = paths;

        double[] headerSizes = view
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Classes.Contains("section"))
            .Select(t => t.FontSize)
            .ToArray();

        Assert.True(headerSizes.Length >= 7); // AI / Editor / Font / AI panel / Appearance / Files & save / AI safety.
        Assert.All(headerSizes, size => Assert.Equal(16d * 1.25, size));

        window.Close();
    }
}
