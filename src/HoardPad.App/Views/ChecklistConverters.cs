// ChecklistConverters.cs — tiny presentation converters for the checklist rows (design §5.1).
//
// Pure view-layer mapping: depth → indent width (the per-tier indent guide inset), collapsed flag → twisty
// glyph, struck flag → text decoration. No logic — display-only. (The glyphs here are placeholders per the
// design glyph note F3; the brand icon set replaces them in the theming pass.)

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HoardPad.App.Views;

/// <summary>Depth (0/1/2) → indent inset width in px (20px per tier) for the indent guide.</summary>
internal sealed class DepthToIndentConverter : IValueConverter
{
    private const double PerTier = 20.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int depth ? depth * PerTier : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapsed flag → the ▸/▾ twisty glyph (placeholder per design glyph note F3).</summary>
internal sealed class CollapsedToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▸" : "▾";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Struck flag → Strikethrough text decorations (a checked item, strikethrough-in-place mode).</summary>
internal sealed class StruckToDecorationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextDecorations.Strikethrough : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
