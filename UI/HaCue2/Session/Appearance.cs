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
/// <b>Everything here is live.</b> Metrics and colours are both pushed in as resource overrides, and
/// every control reads them with <c>DynamicResource</c>, so a change lands on the next layout without
/// a restart. Getting the palette there needed the colour references converted from
/// <c>StaticResource</c> - a static lookup resolves once when the theme is built, which is why this
/// was startup-only until the conversion.
/// </para>
/// <para>
/// A palette re-states only what actually depends on the surface: the 13 base colours, the 13 brushes
/// built from them, and the 14 pre-computed OPAQUE mixes. The 35 gel-over-transparent washes are
/// palette-independent by construction - they tint whatever they sit on, which is why the mockup
/// authored them with an alpha channel.
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

    /// <summary>The palettes the Appearance pane offers. Booth dark is the tokens themselves.</summary>
    public static IReadOnlyList<string> Palettes { get; } = ["booth dark", "dark", "light"];

    private readonly ResourceDictionary _palette = [];
    private string _paletteName = "booth dark";

    /// <summary>Which palette is drawn. Live - the app re-tints on the next layout.</summary>
    public string Palette
    {
        get => _paletteName;
        set
        {
            if (value == _paletteName)
                return;

            _paletteName = value;
            ApplyPalette();
        }
    }

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
    /// rather than swapped - a dictionary that is replaced breaks every DynamicResource pointing at it.
    /// </remarks>
    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        // Palette first, metrics last: both are appended AFTER the theme bundle, so they win, and a
        // metric never depends on a colour.
        if (!application.Resources.MergedDictionaries.Contains(_palette))
            application.Resources.MergedDictionaries.Add(_palette);

        if (!application.Resources.MergedDictionaries.Contains(_overrides))
            application.Resources.MergedDictionaries.Add(_overrides);

        ApplyPalette();
        Apply();
    }

    /// <summary>
    /// Adopts the operator's saved appearance in one pass.
    /// </summary>
    /// <remarks>
    /// One call rather than four setters, because each of those re-applies the whole override
    /// dictionary - doing it four times at start-up is four layout passes before the first window is
    /// even shown. The palette is set last: it is the only one that rebuilds the theme.
    /// </remarks>
    public void Adopt(Machine.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Density = settings.Density switch
        {
            "compact" => Session.Density.Compact,
            "relaxed" => Session.Density.Relaxed,
            _ => Session.Density.Normal,
        };

        RowHeight = Math.Clamp(ParseRowHeight(settings.RowSize), 18, 56);
        FontScale = Math.Clamp(ParseFontScale(settings.FontScale), 0.75, 1.75);
        Apply();

        Palette = settings.Theme;
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

    /// <summary>
    /// Loads the chosen palette's overrides into the live dictionary.
    /// </summary>
    /// <remarks>
    /// The dictionary is CLEARED and refilled rather than swapped: a replaced dictionary breaks every
    /// DynamicResource already pointing into it, which would leave the app half re-tinted.
    /// </remarks>
    private void ApplyPalette()
    {
        _palette.Clear();

        // "booth dark" is Tokens.axaml itself - nothing to override, which is why it is the default
        // and why an empty dictionary is the correct representation of it.
        ResourceDictionary? loaded = _paletteName switch
        {
            "light" => new Themes.LightPalette(),
            "dark" => new Themes.NeutralDarkPalette(),
            _ => null,
        };

        if (loaded is null)
            return;

        foreach (var entry in loaded)
            _palette[entry.Key] = entry.Value;
    }

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
