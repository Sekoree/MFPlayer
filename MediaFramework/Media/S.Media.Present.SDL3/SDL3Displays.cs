namespace S.Media.Present.SDL3;

/// <summary>One attached display, as the compositor pipeline should describe a canvas for it.</summary>
/// <param name="Index">Position in SDL's display order — the same index window placement uses.</param>
/// <param name="RefreshNumerator">Exact refresh numerator, or 0 when the platform reports only the
/// rounded <paramref name="RefreshHz"/> (or nothing at all).</param>
/// <remarks>
/// The exact rational matters more than the rounded number: a canvas authored at 60.000 against a
/// panel running 59.94 beats once every ~17 s — the drop cadence nobody can attribute — and
/// "59.94" typed from the rounded float is still not 60000/1001. Callers that build a canvas rate
/// should prefer the numerator/denominator when present.
/// </remarks>
public sealed record SDL3DisplayInfo(
    int Index,
    string Name,
    int Width,
    int Height,
    double RefreshHz,
    int RefreshNumerator,
    int RefreshDenominator);

/// <summary>Enumerates the machine's displays with their CURRENT desktop modes.</summary>
public static class SDL3Displays
{
    /// <summary>
    /// The attached displays, best-effort: a headless box, a failed SDL init, or a display that
    /// reports no mode simply contributes nothing. Never throws — this feeds pickers and hints,
    /// and "no presets" is the correct degradation everywhere it is used.
    /// </summary>
    public static IReadOnlyList<SDL3DisplayInfo> Enumerate()
    {
        var found = new List<SDL3DisplayInfo>();
        try
        {
            SDL3Runtime.Acquire();
            try
            {
                var displays = SDL.GetDisplays(out _);
                if (displays is null)
                    return found;

                for (var index = 0; index < displays.Length; index++)
                {
                    var id = displays[index];
                    if (SDL.GetDesktopDisplayMode(id) is not { } mode)
                        continue;

                    var name = SDL.GetDisplayName(id);
                    // SDL reports the desktop mode in POINTS with a density scale (a 2560×1600 panel
                    // at 150 % says 1707×1067 × 1.5). A canvas is authored in pixels.
                    var density = mode.PixelDensity > 0 ? mode.PixelDensity : 1f;
                    found.Add(new SDL3DisplayInfo(
                        index,
                        string.IsNullOrWhiteSpace(name) ? $"Display {index + 1}" : name,
                        (int)Math.Round(mode.W * density),
                        (int)Math.Round(mode.H * density),
                        mode.RefreshRate,
                        mode.RefreshRateNumerator,
                        mode.RefreshRateDenominator));
                }
            }
            finally
            {
                SDL3Runtime.Release();
            }
        }
        catch
        {
            // No SDL, no video driver, no displays - the caller shows no presets and life goes on.
        }

        return found;
    }
}
