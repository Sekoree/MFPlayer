using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HaCue2.ViewModels;

namespace HaCue2.Converters;

/// <summary>
/// Turns a cue row's state into the wash behind it.
/// </summary>
/// <remarks>
/// A converter rather than the class vocabulary the ListBox used, because a
/// <c>TreeDataGridRow</c> is created by the control and there is no markup on it to hang
/// <c>Classes.is-go</c> from. The colours are still looked up by TOKEN key, so the palette stays in
/// Tokens.axaml and this only decides which token a state means.
/// </remarks>
public sealed class RowWashBrushConverter : IValueConverter
{
    public static readonly RowWashBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as RowWash?) switch
        {
            RowWash.Running => "CueGoWash",
            RowWash.Standby => "CueStandbyWash",
            RowWash.Group => "ActiveGroupWash",
            _ => null,
        };

        if (key is null || Application.Current is not { } app)
            return null;

        return app.Resources.TryGetResource(key, app.ActualThemeVariant, out var brush)
            ? brush as IBrush
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
