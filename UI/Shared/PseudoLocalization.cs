using System.Text;

namespace HaApps.Localization;

/// <summary>
/// Pseudo-localization for the apps' typed string resources (2026-08-14 review, F-21 acceptance:
/// "a pseudo-localization culture that expands strings and introduces non-ASCII/RTL markers to
/// drive layout tests"). With <c>MFP_PSEUDOLOC=1</c> every resolved string is returned as
/// <c>⟦Ẽxpãñdẽd tẽxt··⟧</c>: accented (catches font/encoding assumptions), ~35% longer (catches
/// layouts sized to the English copy), and bracketed (makes truncation and string concatenation
/// visible at a glance - a clipped string loses its closing bracket).
/// </summary>
/// <remarks>
/// <para>
/// Source-linked into each app (the apps deliberately share no UI-support assembly), applied at the
/// ONE choke point their <c>Strings.Get</c> accessors already are - which is exactly why the F-21
/// migration had to happen first: a hardcoded literal never passes through here.
/// </para>
/// <para>
/// Format placeholders (<c>{0}</c>, <c>{1:0.##}</c>) pass through untouched, so
/// <c>Strings.Format</c> keeps working under pseudo-localization.
/// </para>
/// </remarks>
internal static class PseudoLocalization
{
    /// <summary>Test hook: overrides the environment switch (null = follow MFP_PSEUDOLOC).</summary>
    internal static bool? ForceEnabled;

    private static readonly bool EnvEnabled =
        Environment.GetEnvironmentVariable("MFP_PSEUDOLOC") is "1" or "true";

    internal static bool Enabled => ForceEnabled ?? EnvEnabled;

    /// <summary>Returns <paramref name="value"/> unchanged when disabled; the pseudo-localized
    /// form otherwise.</summary>
    internal static string Apply(string value)
    {
        if (!Enabled || value.Length == 0)
            return value;

        var builder = new StringBuilder(value.Length * 2);
        builder.Append('⟦');
        var inPlaceholder = false;
        foreach (var ch in value)
        {
            if (ch == '{')
                inPlaceholder = true;
            else if (ch == '}')
                inPlaceholder = false;

            builder.Append(inPlaceholder ? ch : Accent(ch));
        }

        // ~35% expansion, at least two pad characters - the middle-dot padding is visibly filler,
        // so a designer never mistakes it for real copy.
        var pad = Math.Max(2, (int)Math.Ceiling(value.Length * 0.35));
        builder.Append('·', pad);
        builder.Append('⟧');
        return builder.ToString();
    }

    private static char Accent(char ch) => ch switch
    {
        'a' => 'ã', 'e' => 'ẽ', 'i' => 'ĩ', 'o' => 'õ', 'u' => 'ũ',
        'c' => 'ç', 'n' => 'ñ', 'y' => 'ý', 'd' => 'ð', 'g' => 'ğ', 's' => 'š',
        'A' => 'Ã', 'E' => 'Ẽ', 'I' => 'Ĩ', 'O' => 'Õ', 'U' => 'Ũ',
        'C' => 'Ç', 'N' => 'Ñ', 'Y' => 'Ý', 'D' => 'Ð', 'G' => 'Ğ', 'S' => 'Š',
        _ => ch,
    };
}
