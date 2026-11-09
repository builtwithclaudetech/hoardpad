// TabStripConverters.cs — tiny presentation converters for the tab strip (M8.1 / FR-1).

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HoardPad.App.Views;

/// <summary>
/// Maps the tab's <c>IsActive</c> bool to the active-tab accent brush (M8.1 / design §2.2 / §13).
/// <c>true</c> → <c>Brand.Primary.Brush</c> (the active accent, theme-resolved); <c>false</c> → Transparent.
/// </summary>
internal sealed class TabIsActiveConverter : IValueConverter
{
    public static readonly TabIsActiveConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
            return Brushes.Transparent;

        if (Application.Current is { } app
            && app.TryGetResource("Brand.Primary.Brush", app.ActualThemeVariant, out object? res)
            && res is IBrush brush)
            return brush;

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
