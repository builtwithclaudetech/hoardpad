// SettingsView.axaml.cs — the settings-tab control code-behind (M6.8, ODQ-12).
//
// Wiring only — InitializeComponent. All settings logic (load/save/clamp/validation) lives in
// SettingsViewModel; this control is a pure DataContext-bound form (the settings-as-tab surface).
//
// FEAT-1: subscribes to the VM's FontSettingsChanged event and applies font changes live by updating
// Application.Current.Resources. FontFamily/FontSize resources use DynamicResource in the editor and
// chrome styles, so resource changes propagate automatically.
//
// QA3-1: the actual resource-key writes now live in SettingsFontApplier (the single writer shared with
// the App.axaml.cs startup re-apply path) — this method only gathers the VM's current values and hands
// them off, so the live-edit and startup paths can never drift apart.

using Avalonia;
using Avalonia.Controls;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Views;

/// <summary>The FR-35 settings surface, hosted as a tab (ODQ-12 — NOT a separate OS window).</summary>
public sealed partial class SettingsView : UserControl
{
    private SettingsViewModel? _vm;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.FontSettingsChanged -= ApplyLiveFonts;
        _vm = DataContext as SettingsViewModel;
        if (_vm is not null)
            _vm.FontSettingsChanged += ApplyLiveFonts;
    }

    /// <summary>
    /// Apply the current font settings to Application.Resources so DynamicResource-bound controls
    /// (the editor, chrome) pick up the change live. Falls back to brand.md §5 chain when the user
    /// has not set an explicit override.
    /// </summary>
    private void ApplyLiveFonts()
    {
        if (_vm is null) return;
        var app = Application.Current;
        if (app is null) return;

        SettingsFontApplier.Apply(app, _vm.EditorFontFamily, _vm.EditorFontSize, _vm.UiFontFamily, _vm.UiFontSize);

        // FEAT-15: AI panel style — same live-edit posture as the font settings above.
        AiPanelStyleApplier.Apply(
            app,
            _vm.AiInputFontFamily, _vm.AiInputFontSize,
            _vm.AiResponseFontFamily, _vm.AiResponseFontSize, _vm.AiResponseColor,
            _vm.AiUserFontFamily, _vm.AiUserFontSize, _vm.AiUserColor);
    }
}
