// LanguageMenuConverters.cs — Language-menu checkmark binding (QA3-4).
//
// The Language submenu (MainWindow.axaml) lists the FR-7 catalog as flat MenuItems, each wired to
// SetLanguageCommand with its own label as CommandParameter (BUG-9-style forwarding from the active
// editor VM). That already applies the language on click, but nothing indicated which one is ACTIVE
// (design §3 — "checkmarks the active"). This converter adds that: each item sets
// ToggleType="CheckBox" and binds IsChecked to the shell's LanguageMode through this converter, with
// ConverterParameter set to the item's own label — Convert is then a plain string comparison. Convert
// re-runs whenever LanguageMode changes (tab switch or a SetLanguageCommand call), so the checkmark
// always tracks the active document.

using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace HoardPad.App.Views;

/// <summary>Compares the bound <c>LanguageMode</c> label to this item's <c>ConverterParameter</c> label.</summary>
internal sealed class LanguageActiveConverter : IValueConverter
{
    public static readonly LanguageActiveConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string current && parameter is string label && string.Equals(current, label, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        // SetLanguageCommand is the only mutation path (it resolves the grammar scope + re-mirrors the
        // segments); a MenuItem's own click-driven IsChecked toggle must not write back through here.
        BindingOperations.DoNothing;
}
