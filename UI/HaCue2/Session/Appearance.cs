using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace HaCue2.Session;

/// <summary>How dense the app is drawn.</summary>
public enum Density
{
    Compact,
    Normal,
    Relaxed,
}

/// <summary>
/// The appearance settings that change how the app is DRAWN rather than what it contains.
/// </summary>
/// <remarks>
/// <para>
/// Application-scoped, not journaled, and never written into a project: an operator's font size
/// travelling to the next venue inside a show file would be carrying the wrong thing (screen 12 says
/// exactly this).
/// </para>
/// <para>
/// <b>Metrics are live; the palette is not.</b> Row heights and font sizes are pushed in as resource
/// overrides and every control that uses them reads them with <c>DynamicResource</c>, so a change
/// lands immediately. The COLOUR palette is applied at startup instead: 840-odd
/// <c>StaticResource</c> references resolve once when the theme is built, and converting every one of
/// them to a dynamic lookup to make a preference live would be a large change with a real chance of
/// missing some — which would show up as one panel in the wrong colours. The Appearance pane says so
/// rather than pretending.
/// </para>
/// </remarks>
public sealed class Appearance
{
    /// <summary>The keys metrics override, all of them read with DynamicResource.</summary>
    private static readonly (string Key, double Base)[] Metrics =
    [
        ("CueRowHeight", 26),
        ("ActiveRowHeight", 25),
        ("ControlHeight", 22),
    ];

    private static readonly (string Key, double Base)[] FontSizes =
    [
        ("FontSizeBase", 12),
        ("FontSizeSmall", 11),
        ("FontSizeMono", 10),
        ("FontSizeMonoSmall", 9.5),
        ("FontSizeMicro", 9),
        ("FontSizeNano", 8.5),
    ];

    private readonly ResourceDictionary _overrides = [];

    /// <summary>The one instance the app reads. Set before the first window if it is being restored.</summary>
    public static Appearance Current { get; } = new();

    /// <summary>Which palette to build at startup. Changing it later needs a restart.</summary>
    public string Palette { get; set; } = "booth dark";

    public Density Density { get; private set; } = Density.Normal;

    /// <summary>Cue-row height in pixels. The mockup offers 26 / 30 / 38-touch.</summary>
    public double RowHeight { get; private set; } = 26;

    /// <summary>1.0 is the drawn size. Clamped, because a show cannot be operated at 40 %.</summary>
    public double FontScale { get; private set; } = 1;

    /// <summary>
    /// Attaches the override dictionary to the application.
    /// </summary>
    /// <remarks>
    /// Merged LAST so it wins over the theme's own values, and kept as one dictionary that is mutated
    /// rather than swapped — a dictionary that is replaced breaks every DynamicResource pointing at it.
    /// </remarks>
    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (!application.Resources.MergedDictionaries.Contains(_overrides))
            application.Resources.MergedDictionaries.Add(_overrides);

        Apply();
    }

    public void Set(Density density)
    {
        Density = density;
        Apply();
    }

    public void SetRowHeight(double pixels)
    {
        RowHeight = Math.Clamp(pixels, 18, 56);
        Apply();
    }

    public void SetFontScale(double scale)
    {
        // A show cannot be operated at 40 % and a booth screen cannot show it at 300 %.
        FontScale = Math.Clamp(scale, 0.75, 1.75);
        Apply();
    }

    /// <summary>Reads a "38 px touch"-style label back into pixels.</summary>
    public static double ParseRowHeight(string label, double fallback = 26) =>
        double.TryParse(
            new string([.. label.TakeWhile(char.IsAsciiDigit)]),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var pixels) && pixels > 0
            ? pixels
            : fallback;

    /// <summary>Reads a "110 %"-style label back into a scale.</summary>
    public static double ParseFontScale(string label, double fallback = 1) =>
        double.TryParse(
            new string([.. label.Where(character => char.IsAsciiDigit(character) || character is '.' or ',')]),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture,
            out var percent) && percent > 0
            ? percent / 100
            : fallback;

    private void Apply()
    {
        // Density scales the CHROME, not the type: a compact layout is about how much fits on screen,
        // and shrinking the text to achieve it just makes it unreadable. Font scale is separate for
        // exactly that reason.
        var density = Density switch
        {
            Density.Compact => 0.86,
            Density.Relaxed => 1.18,
            _ => 1,
        };

        foreach (var (key, size) in Metrics)
        {
            _overrides[key] = key == "CueRowHeight"
                ? RowHeight * density
                : Math.Round(size * density, 1);
        }

        foreach (var (key, size) in FontSizes)
            _overrides[key] = Math.Round(size * FontScale, 2);
    }
}
