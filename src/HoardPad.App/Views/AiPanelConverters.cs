// AiPanelConverters.cs — value converters for the M6.6 AI-panel view (design §6.4/§6.5/§6.7).
//
// Pure presentation mapping from the AI-panel display models to glyphs / brushes / visibility. No
// safety logic — these are render-only. Brushes resolve the brand.md §13 tokens from the active theme
// dictionary so a theme switch reflows the panel chrome (FR-37).

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HoardPad.App.Ai;
using HoardPad.Core.Records;

namespace HoardPad.App.Views;

/// <summary>Maps an <see cref="ActivityOp"/> to its design §6.4 file-op chip glyph.</summary>
public sealed class OpGlyphConverter : IValueConverter
{
    public static readonly OpGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ActivityOp.Create => "＋",
        ActivityOp.Edit => "▣",
        ActivityOp.Delete => "⌦",
        ActivityOp.Move => "⇄",
        ActivityOp.Copy => "⧉",
        _ => "·",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an <see cref="OpOutcomeBadge"/> to its design §6.4 bracketed label.</summary>
public sealed class OutcomeBadgeConverter : IValueConverter
{
    public static readonly OutcomeBadgeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        OpOutcomeBadge.Applied => "[applied]",
        OpOutcomeBadge.Approved => "[approved]",
        OpOutcomeBadge.Blocked => "[blocked]",
        OpOutcomeBadge.Undone => "[undone]",
        OpOutcomeBadge.Queued => "[queued]",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="CapEmphasis"/> tier to its brand token brush (design §6.5/§10.3): Normal -> on-surface,
/// Warning -> warning, Danger -> danger. Resolved against the active theme dictionary (FR-37).
/// </summary>
public sealed class CapEmphasisBrushConverter : IValueConverter
{
    public static readonly CapEmphasisBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            CapEmphasis.Danger => "Brand.Danger.Brush",
            CapEmphasis.Warning => "Brand.Warning.Brush",
            _ => "Brand.OnSurface.Brush",
        };
        return ResolveBrush(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static IBrush ResolveBrush(string key)
    {
        if (Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? res) && res is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }
}

/// <summary>Maps a danger flag to the danger token (true) or the on-surface token (false) — for safety chips.</summary>
public sealed class DangerBrushConverter : IValueConverter
{
    public static readonly DangerBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        CapEmphasisBrushConverter.ResolveBrush(value is true ? "Brand.Danger.Brush" : "Brand.OnSurface.Brush");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="SandboxState"/> to the brand token brush for the always-visible sandbox line (§6.7).</summary>
public sealed class SandboxStateBrushConverter : IValueConverter
{
    public static readonly SandboxStateBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            SandboxState.Armed => "Brand.Success.Brush",
            SandboxState.SelfTestFailed => "Brand.Danger.Brush",
            SandboxState.Degraded => "Brand.Danger.Brush",
            _ => "Brand.Muted.Brush",
        };
        return CapEmphasisBrushConverter.ResolveBrush(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="BackendConnectionState"/> to its brand-token brush for the BUG-4 status line.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            BackendConnectionState.Connected => "Brand.Success.Brush",
            BackendConnectionState.Connecting => "Brand.Warning.Brush",
            BackendConnectionState.Error => "Brand.Danger.Brush",
            _ => "Brand.Muted.Brush",
        };
        return CapEmphasisBrushConverter.ResolveBrush(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// FEAT-12: maps a transcript line's <see cref="TranscriptKind"/> to the sender-distinct bubble styling
/// (brand.md §13) — background brush, border brush, padding, font family, or font size, selected by
/// <c>ConverterParameter</c> ("Background" | "Border" | "Padding" | "FontFamily" | "FontSize"). A user
/// message gets an <c>accent</c>-bordered <c>overlay-surface</c> bubble; an AI reply gets a <c>primary</c>
/// (jewel)-bordered <c>surface-alt</c> bubble — the same jewel tone as the panel's "AI" header label. Every
/// other kind (file-op chip, safety chip, system note) renders unchanged: a transparent border, a
/// transparent background, zero padding, and NO local font override (falls back to the inherited chrome
/// font/size via <see cref="AvaloniaProperty.UnsetValue"/>).
///
/// FEAT-15: "FontFamily"/"FontSize" read the AI-panel style resources <c>AiPanelStyleApplier</c> writes
/// (<c>Brand.Font.AiUser</c>/<c>AiUserSize</c> for a user message, <c>Brand.Font.AiResponse</c>/
/// <c>AiResponseSize</c> for an assistant message) — the settings-configurable font per sender.
/// </summary>
public sealed class TranscriptBubbleConverter : IValueConverter
{
    public static readonly TranscriptBubbleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        TranscriptKind kind = value as TranscriptKind? ?? default;
        return (parameter as string) switch
        {
            "Background" => ResolveBrushForKind(kind, userKey: "Brand.OverlaySurface.Brush", aiKey: "Brand.SurfaceAlt.Brush"),
            "Border" => ResolveBrushForKind(kind, userKey: "Brand.Accent.Brush", aiKey: "Brand.Primary.Brush"),
            "Padding" => IsMessageKind(kind) ? new Thickness(6, 4) : new Thickness(0),
            "FontFamily" => IsMessageKind(kind)
                ? ResolveResource(kind == TranscriptKind.UserMessage ? "Brand.Font.AiUser" : "Brand.Font.AiResponse")
                    as FontFamily ?? FontFamily.Default
                : AvaloniaProperty.UnsetValue,
            "FontSize" => IsMessageKind(kind)
                ? ResolveResource(kind == TranscriptKind.UserMessage ? "Brand.Font.AiUserSize" : "Brand.Font.AiResponseSize")
                    is double size ? size : (double)HoardPad.App.Settings.HoardPadSettings.DefaultAiFontSize
                : AvaloniaProperty.UnsetValue,
            var unknown => throw new ArgumentOutOfRangeException(nameof(parameter), unknown, "unknown bubble aspect"),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool IsMessageKind(TranscriptKind kind) =>
        kind is TranscriptKind.UserMessage or TranscriptKind.AssistantMessage;

    private static IBrush ResolveBrushForKind(TranscriptKind kind, string userKey, string aiKey) => kind switch
    {
        TranscriptKind.UserMessage => CapEmphasisBrushConverter.ResolveBrush(userKey),
        TranscriptKind.AssistantMessage => CapEmphasisBrushConverter.ResolveBrush(aiKey),
        _ => Brushes.Transparent,
    };

    /// <summary>Resolves an arbitrary (non-brush) Application.Resources value, e.g. a FontFamily or a double.</summary>
    private static object? ResolveResource(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? res) ? res : null;
}

/// <summary>
/// FEAT-15: maps a whole <see cref="TranscriptLine"/> to its rendered text color. Priority order: a
/// danger-chip line (<see cref="TranscriptLine.IsDanger"/>) always wins (safety events stay red regardless
/// of sender); otherwise a user/assistant message resolves the settings-configurable override
/// (<c>Brand.Color.AiUser</c>/<c>AiResponse</c>, written only when the user set a valid hex — see
/// <c>AiPanelStyleApplier</c>); every other case (no override, or a non-message kind) falls back to the
/// live <c>Brand.OnSurface.Brush</c> theme token — the pre-FEAT-15 look.
/// </summary>
public sealed class TranscriptForegroundConverter : IValueConverter
{
    public static readonly TranscriptForegroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TranscriptLine line)
            return CapEmphasisBrushConverter.ResolveBrush("Brand.OnSurface.Brush");

        if (line.IsDanger)
            return CapEmphasisBrushConverter.ResolveBrush("Brand.Danger.Brush");

        string? overrideKey = line.Kind switch
        {
            TranscriptKind.UserMessage => "Brand.Color.AiUser",
            TranscriptKind.AssistantMessage => "Brand.Color.AiResponse",
            _ => null,
        };

        if (overrideKey is not null
            && Application.Current is { } app
            && app.Resources.TryGetValue(overrideKey, out object? res)
            && res is IBrush brush)
        {
            return brush;
        }

        return CapEmphasisBrushConverter.ResolveBrush("Brand.OnSurface.Brush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// PANEL-3 (feature-ai-multidoc-panel FR-MD-6(a)): maps a row's <c>IsFocused</c> bool to a font weight so
/// the focused document's name is visually distinguished in the multi-doc checklist — SemiBold when focused,
/// Normal otherwise. Presentation-only (the focus mark is also the jewel dot beside it).
/// </summary>
public sealed class FocusWeightConverter : IValueConverter
{
    public static readonly FocusWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeight.SemiBold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
