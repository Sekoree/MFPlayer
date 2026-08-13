using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaCue2.Core.Model;

/// <summary>
/// A cue number: dot-separated numeric segments, ordered segment by segment.
/// </summary>
/// <remarks>
/// <para>
/// Stored as the operator typed it and compared as numbers. The two halves matter for different
/// reasons. Stored as text, because a cue number is an identifier somebody calls over comms and
/// "12.10" is a thing you say - a decimal would silently turn it into "12.1", which is a DIFFERENT
/// cue. Compared numerically, because plain text ordering puts Q10 before Q2 and GO walks this order.
/// </para>
/// <para>
/// Three levels are ordinary in real shows: the HaPlay projects in this repository's sibling documents
/// folder use 1.1.1 and 1.2.1 throughout, one level per nesting depth. That is the evidence this type
/// exists for - the earlier <c>decimal</c> could not hold those at all, so a project written by the
/// app it is replacing could not have been opened.
/// </para>
/// <para>
/// The engine's <c>CueDefinition.Number</c> is an <c>int</c>, and deliberately stays one: it is a
/// dense ordinal the compiler assigns in THIS order, not the number the operator sees. GO's "lowest
/// number greater than the cursor" is the same question asked of either representation.
/// </para>
/// </remarks>
[JsonConverter(typeof(CueNumberJsonConverter))]
public readonly record struct CueNumber : IComparable<CueNumber>
{
    private readonly string? _text;

    public CueNumber(string text) => _text = Normalize(text);

    public static CueNumber Empty => default;

    /// <summary>The number as written. Empty for a cue that has none - a comment, typically.</summary>
    public string Text => _text ?? "";

    public bool IsEmpty => Text.Length == 0;

    /// <summary>How deep the number is: 12 is 1, 12.5 is 2, 12.5.1 is 3.</summary>
    public int Depth => IsEmpty ? 0 : Text.Count(character => character == '.') + 1;

    public static CueNumber Parse(string text) => new(text);

    public static implicit operator CueNumber(string text) => new(text);

    /// <summary>
    /// Reads a number, rejecting anything that is not dot-separated digits.
    /// </summary>
    /// <remarks>
    /// Rejecting rather than coercing: a number the app quietly rewrote is one the operator's paper
    /// running order no longer matches.
    /// </remarks>
    public static bool TryParse(string text, out CueNumber number)
    {
        number = Empty;
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
            return true;

        foreach (var segment in trimmed.Split('.'))
        {
            if (segment.Length == 0 || !segment.All(char.IsAsciiDigit))
                return false;
        }

        number = new CueNumber(trimmed);
        return true;
    }

    /// <summary>
    /// Compares segment by segment, numerically, shorter-first on a shared prefix.
    /// </summary>
    /// <remarks>
    /// So 2 &lt; 10, and 12 &lt; 12.1 &lt; 12.2 &lt; 12.10 &lt; 13 - the order an operator reads down
    /// the list, which is the order GO must walk. An empty number sorts first so an unnumbered comment
    /// stays where it was put rather than jumping to the end.
    /// </remarks>
    public int CompareTo(CueNumber other)
    {
        var mine = Segments();
        var theirs = other.Segments();

        for (var index = 0; index < Math.Max(mine.Length, theirs.Length); index++)
        {
            // A missing segment is lower than any present one: 12 comes before 12.1.
            var left = index < mine.Length ? mine[index] : -1;
            var right = index < theirs.Length ? theirs[index] : -1;

            if (left != right)
                return left.CompareTo(right);
        }

        return 0;
    }

    public static bool operator <(CueNumber left, CueNumber right) => left.CompareTo(right) < 0;

    public static bool operator >(CueNumber left, CueNumber right) => left.CompareTo(right) > 0;

    public static bool operator <=(CueNumber left, CueNumber right) => left.CompareTo(right) <= 0;

    public static bool operator >=(CueNumber left, CueNumber right) => left.CompareTo(right) >= 0;

    public override string ToString() => Text;

    /// <summary>The number one level down from this one, as auto-numbering a new child would make it.</summary>
    public CueNumber Child(int index) => IsEmpty
        ? new CueNumber(index.ToString(CultureInfo.InvariantCulture))
        : new CueNumber($"{Text}.{index.ToString(CultureInfo.InvariantCulture)}");

    private int[] Segments() => IsEmpty
        ? []
        : [.. Text.Split('.').Select(segment => int.TryParse(
            segment, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0)];

    /// <summary>
    /// Drops leading zeros per segment so 012.05 and 12.5 are the same number.
    /// </summary>
    /// <remarks>
    /// They compare equal either way; normalising the TEXT too means the duplicate-number check sees
    /// them as one cue, and it is the check, not the sort, that keeps two cues from answering to the
    /// same call.
    /// </remarks>
    private static string? Normalize(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        return string.Join('.', trimmed.Split('.').Select(segment =>
            int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : segment));
    }
}

/// <summary>
/// Writes a cue number as a plain JSON string.
/// </summary>
/// <remarks>
/// Without this the struct would serialize as an object with a "text" member, which is both uglier to
/// read in a diff and a different shape from every other tool's cue file - HaPlay writes
/// <c>"number": "1.1.1"</c>, and matching that is what makes an importer a mapping rather than a
/// translation.
/// </remarks>
public sealed class CueNumberJsonConverter : JsonConverter<CueNumber>
{
    public override CueNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Numbers are accepted as well as strings so a hand-written or older file that put 12 rather
        // than "12" still loads; it is unambiguous, and refusing would help nobody.
        var text = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => "",
            _ => throw new JsonException($"A cue number must be a string, not {reader.TokenType}."),
        };

        return CueNumber.TryParse(text, out var number)
            ? number
            : throw new JsonException($"“{text}” is not a cue number: expected dot-separated digits.");
    }

    public override void Write(Utf8JsonWriter writer, CueNumber value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Text);
}
