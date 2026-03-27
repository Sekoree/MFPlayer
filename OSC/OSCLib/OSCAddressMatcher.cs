using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace OSCLib;

public static class OSCAddressMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> PartRegexCache = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string address)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(address))
            return false;

        if (pattern[0] != '/' || address[0] != '/')
            return false;

        var patternParts = Split(pattern);
        var addressParts = Split(address);

        return MatchParts(patternParts, 0, addressParts, 0);
    }

    private static bool MatchParts(string[] patternParts, int p, string[] addressParts, int a)
    {
        if (p == patternParts.Length)
            return a == addressParts.Length;

        if (patternParts[p].Length == 0)
        {
            for (var i = a; i <= addressParts.Length; i++)
            {
                if (MatchParts(patternParts, p + 1, addressParts, i))
                    return true;
            }

            return false;
        }

        if (a >= addressParts.Length)
            return false;

        if (!MatchPart(patternParts[p], addressParts[a]))
            return false;

        return MatchParts(patternParts, p + 1, addressParts, a + 1);
    }

    private static bool MatchPart(string patternPart, string addressPart)
    {
        var regex = PartRegexCache.GetOrAdd(patternPart, BuildPartRegex);
        return regex.IsMatch(addressPart);
    }

    private static Regex BuildPartRegex(string patternPart)
    {
        var sb = new StringBuilder();
        sb.Append('^');

        for (var i = 0; i < patternPart.Length; i++)
        {
            var ch = patternPart[i];
            switch (ch)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '[':
                    i = AppendBracketPattern(patternPart, i, sb);
                    break;
                case '{':
                    i = AppendAlternationPattern(patternPart, i, sb);
                    break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static int AppendBracketPattern(string patternPart, int startIndex, StringBuilder sb)
    {
        var close = patternPart.IndexOf(']', startIndex + 1);
        if (close < 0)
        {
            sb.Append("\\[");
            return startIndex;
        }

        var content = patternPart.Substring(startIndex + 1, close - startIndex - 1);
        var negate = content.Length > 0 && content[0] == '!';
        var body = negate ? content[1..] : content;

        // Keep OSC character class/range semantics while escaping regex-sensitive class delimiters.
        body = body.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("]", "\\]", StringComparison.Ordinal);
        if (!negate && body.StartsWith('^'))
            body = "\\" + body;

        sb.Append(negate ? "[^" : "[").Append(body).Append(']');
        return close;
    }

    private static int AppendAlternationPattern(string patternPart, int startIndex, StringBuilder sb)
    {
        var close = patternPart.IndexOf('}', startIndex + 1);
        if (close < 0)
        {
            sb.Append("\\{");
            return startIndex;
        }

        var content = patternPart.Substring(startIndex + 1, close - startIndex - 1);
        var options = content.Split(',', StringSplitOptions.None);
        sb.Append("(?:");
        for (var i = 0; i < options.Length; i++)
        {
            if (i > 0)
                sb.Append('|');
            sb.Append(Regex.Escape(options[i]));
        }

        sb.Append(')');
        return close;
    }

    private static string[] Split(string input)
        => input.Split('/', StringSplitOptions.None)[1..];
}
