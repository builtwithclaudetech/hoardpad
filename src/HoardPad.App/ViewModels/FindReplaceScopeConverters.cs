// FindReplaceScopeConverters.cs — radio-button ↔ FindReplaceScope enum binding (design §3.5 scope radios).
//
// A RadioButton's IsChecked is a bool, but the VM holds a single FindReplaceScope enum. Each converter
// instance maps "is the scope equal to MY value?" → bool (Convert), and "this radio was checked" → set
// the scope to MY value (ConvertBack). Three shared instances (one per scope) are bound via x:Static so
// the three radios drive the one Scope property mutually-exclusively. Pure view-layer mapping, no logic.

using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace HoardPad.App.ViewModels;

/// <summary>Maps the single <see cref="FindReplaceScope"/> to/from one radio button's checked state.</summary>
internal sealed class FindReplaceScopeConverter : IValueConverter
{
    private readonly FindReplaceScope _scope;

    private FindReplaceScopeConverter(FindReplaceScope scope) => _scope = scope;

    /// <summary>The current-file radio converter.</summary>
    public static readonly FindReplaceScopeConverter CurrentFileInstance = new(FindReplaceScope.CurrentFile);

    /// <summary>The all-open radio converter.</summary>
    public static readonly FindReplaceScopeConverter AllOpenInstance = new(FindReplaceScope.AllOpen);

    /// <summary>The in-folder radio converter.</summary>
    public static readonly FindReplaceScopeConverter InFolderInstance = new(FindReplaceScope.InFolder);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FindReplaceScope scope && scope == _scope;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        // Only the radio being checked (true) writes back its scope; an uncheck is ignored (another radio's
        // check already set the new scope, so writing "nothing" here would fight it — return DoNothing).
        value is true ? _scope : BindingOperations.DoNothing;
}

/// <summary>The three shared scope-radio converter instances, exposed for the x:Static XAML bindings.</summary>
internal static class FindReplaceScopeConverters
{
    public static FindReplaceScopeConverter CurrentFile => FindReplaceScopeConverter.CurrentFileInstance;

    public static FindReplaceScopeConverter AllOpen => FindReplaceScopeConverter.AllOpenInstance;

    public static FindReplaceScopeConverter InFolder => FindReplaceScopeConverter.InFolderInstance;
}
