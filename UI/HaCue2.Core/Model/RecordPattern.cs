using System.Globalization;
using System.Text;

namespace HaCue2.Core.Model;

/// <summary>
/// What a recording is called (register item 30).
/// </summary>
/// <remarks>
/// <para>
/// The tokens an operator can put in a filename pattern, and the one place that expands them. It is
/// pure so the help popover's example rendering and the file actually written cannot disagree — the
/// popover shows what <see cref="Expand"/> returns, not a hand-written imitation of it.
/// </para>
/// <para>
/// <b>Expansion is also a security boundary.</b> A pattern becomes a path, and both the pattern and
/// the values substituted into it are operator text. A project named <c>../../../etc/passwd</c> must
/// produce a file in the recording directory with an odd name, never a write outside it, so every
/// substituted value AND the expanded result are stripped of separators and the other characters a
/// filename may not carry.
/// </para>
/// </remarks>
public static class RecordPattern
{
    /// <summary>The pattern a new record line starts with.</summary>
    public const string Default = "{project}-{date}-{n}";

    /// <summary>One insert token, as the dropdown and the help popover list it.</summary>
    /// <param name="Token">The literal text inserted, braces included.</param>
    /// <param name="Meaning">What it stands for, for the popover's left column.</param>
    public readonly record struct RecordToken(string Token, string Meaning);

    /// <summary>Every token, in the order the insert dropdown offers them.</summary>
    public static IReadOnlyList<RecordToken> Tokens { get; } =
    [
        new("{date}", "The date the recording started, as 2026-08-03"),
        new("{time}", "The time it started, as 143005"),
        new("{project}", "The show's name"),
        new("{list}", "The cue list that was active"),
        new("{n}", "A counter that climbs until the name is free"),
    ];

    /// <summary>
    /// The values a pattern is expanded against.
    /// </summary>
    /// <remarks>
    /// The timestamp is passed in rather than read from the clock so a test can assert an exact name,
    /// and so every file in one arm agrees on the moment even if the encoders open a second apart.
    /// </remarks>
    /// <param name="Attempt">
    /// Zero-based. <c>{n}</c> renders it one-based, and it climbs while a name is taken — the pattern
    /// is expanded again per attempt rather than having a suffix bolted onto the first result, because
    /// an operator who wrote <c>{n}</c> in the middle meant the number to appear there.
    /// </param>
    public readonly record struct RecordNaming(
        string ProjectName = "",
        string ListName = "",
        DateTimeOffset Timestamp = default,
        int Attempt = 0);

    /// <summary>Expands a pattern into a bare filename, without an extension.</summary>
    public static string Expand(string pattern, RecordNaming naming)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            pattern = Default;

        var built = new StringBuilder(pattern.Length + 32);

        for (var index = 0; index < pattern.Length;)
        {
            if (pattern[index] != '{' || pattern.IndexOf('}', index) is not (var close and > -1))
            {
                built.Append(pattern[index++]);
                continue;
            }

            var token = pattern[(index + 1)..close];

            if (Substitute(token, naming) is { } value)
            {
                built.Append(value);
                index = close + 1;
            }
            else
            {
                // An unrecognised token is left standing rather than dropped. A typo that silently
                // vanished would leave every recording named the same thing and the operator with no
                // clue why; one that survives into the filename is a typo somebody can see and fix.
                built.Append(pattern[index++]);
            }
        }

        var name = Clean(built.ToString());

        // A name needs something READABLE in it, not merely something in it. A pattern of nothing but
        // separators cleans to "---", which is a legal filename and a useless one — the operator would
        // be looking for a recording and find punctuation.
        return name.Any(char.IsLetterOrDigit) ? name : "recording";
    }

    private static string? Substitute(string token, RecordNaming naming) =>
        token.ToLowerInvariant() switch
        {
            "date" => naming.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => naming.Timestamp.ToString("HHmmss", CultureInfo.InvariantCulture),
            "project" => Clean(naming.ProjectName),
            "list" => Clean(naming.ListName),
            "n" => (naming.Attempt + 1).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

    /// <summary>
    /// Strips everything a filename may not carry, on every platform rather than this one.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetInvalidFileNameChars"/> alone is not enough: on Linux it returns only the
    /// separator and NUL, so a show written on Linux would name a file <c>act:one?.mkv</c> and fail to
    /// open on the Windows machine it is carried to. A show file travels between machines, so the
    /// strictest rule is the only portable one.
    /// </remarks>
    private static string Clean(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var built = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsControl(character) || "\\/:*?\"<>|".Contains(character, StringComparison.Ordinal))
                built.Append('-');
            else
                built.Append(character);
        }

        // A leading dot hides the recording on Unix, and trailing dots and spaces are silently dropped
        // by Windows — both turn "the file is not where I put it" into a mystery.
        return built.ToString().Trim().Trim('.').Trim();
    }

    /// <summary>
    /// A worked example for the help popover, rendered through <see cref="Expand"/> itself.
    /// </summary>
    public static string Example(string pattern, string projectName, DateTimeOffset now) =>
        Expand(pattern, new RecordNaming(projectName, "Act One", now));
}
