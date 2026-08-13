using Avalonia.Media;

namespace HaCue2.Presentation;

/// <summary>
/// The cue-row colour tags: a small fixed palette, addressed by index.
/// </summary>
/// <remarks>
/// <para>
/// An INDEX in the document rather than a colour, so the palette can be restyled with the theme and so
/// a show does not carry hex codes chosen against a skin it may not be opened under. Eight, because a
/// palette an operator has to scroll is a palette they stop using - and because eight is roughly the
/// number of meanings anybody assigns before they start writing them in the note instead.
/// </para>
/// <para>
/// The names are what the picker shows. They are colours rather than meanings on purpose: "act one"
/// belongs to one show and "amber" belongs to everybody.
/// </para>
/// </remarks>
public static class CueColors
{
    public static IReadOnlyList<string> Names { get; } =
        ["none", "red", "amber", "green", "teal", "blue", "violet", "magenta", "grey"];

    /// <summary>
    /// The band drawn at the left of a tagged row, or transparent for the untagged.
    /// </summary>
    /// <remarks>
    /// Muted rather than saturated: this is a band beside text on a dark panel, and a full-strength
    /// swatch there pulls the eye off the cue numbers the operator is actually reading.
    /// </remarks>
    public static IBrush Brush(int tag) => tag >= 0 && tag < Brushes.Count
        ? Brushes[tag]
        : Avalonia.Media.Brushes.Transparent;

    private static readonly IReadOnlyList<IBrush> Brushes =
    [
        Avalonia.Media.Brushes.Transparent,
        new SolidColorBrush(Color.FromRgb(0xC4, 0x4A, 0x4A)),
        new SolidColorBrush(Color.FromRgb(0xC4, 0x8E, 0x3A)),
        new SolidColorBrush(Color.FromRgb(0x54, 0xA0, 0x5E)),
        new SolidColorBrush(Color.FromRgb(0x3E, 0x9C, 0x9C)),
        new SolidColorBrush(Color.FromRgb(0x51, 0x74, 0xC4)),
        new SolidColorBrush(Color.FromRgb(0x8A, 0x5C, 0xC4)),
        new SolidColorBrush(Color.FromRgb(0xB8, 0x4E, 0x94)),
        new SolidColorBrush(Color.FromRgb(0x77, 0x7E, 0x8A)),
    ];
}
