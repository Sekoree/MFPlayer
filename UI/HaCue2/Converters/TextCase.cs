using System.Globalization;
using Avalonia.Data.Converters;

namespace HaCue2.Converters;

/// <summary>
/// CSS <c>text-transform: uppercase</c>, which Avalonia has no equivalent for.
/// </summary>
/// <remarks>
/// Applied only where the mockup uppercases <i>bound</i> text - panel headers, whose content is a cue
/// label or a list name coming from the document. Literal chrome (button captions, chips, tab names) is
/// authored uppercase in the XAML instead, because running a converter over a constant only hides what
/// the screen says from anyone reading the source.
/// <para>
/// Invariant casing on purpose: these are UI chrome labels, and a Turkish locale lower-casing the
/// dotted I in "Fit" is a real defect, not a theoretical one.
/// </para>
/// </remarks>
public sealed class UpperCaseConverter : IValueConverter
{
    public static readonly UpperCaseConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToUpperInvariant();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value;
}
