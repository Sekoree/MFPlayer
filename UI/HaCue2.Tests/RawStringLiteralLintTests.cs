using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace HaCue2.Tests;

/// <summary>
/// F-21 (2026-08-14 review): operator-facing UI text must go through <c>HaCue2.Resources.Strings</c>
/// rather than being hardcoded in AXAML - hardcoded copy blocks translation, prevents long-string
/// layout tests, and let a literal like "No device channel receives Lobby" state a wrong FACT (the
/// N-04 incident). This lint mirrors HaPlay's proven ratchet: it scans the Views for raw literal
/// <c>Text</c>/<c>Content</c>/<c>Header</c>/<c>Title</c>/<c>ToolTip.Tip</c>/<c>Watermark</c> values
/// that look like words - exempting bindings, <c>x:Static</c>, glyph entities and single symbols -
/// and fails if the count exceeds the tracked baseline. No NEW hardcoded string can be added, and
/// the existing debt migrates down screen by screen (lower <see cref="Baseline"/> as strings move to
/// Strings.resx; the test prints the new floor when it drops). ShellWindow is the migrated exemplar.
/// </summary>
public sealed class RawStringLiteralLintTests(ITestOutputHelper output)
{
    // Tracked debt of hardcoded user-facing literals across ALL Views (recursive). RATCHET ONLY
    // DOWNWARD - never raise this to accommodate a new literal. Baselined 2026-08-14 after the
    // ShellWindow migration; the big remaining screens are InspectorPane (160), VideoView (118),
    // SettingsWindow (100), AudioView (75) and TargetsView (63).
    private const int Baseline = 832;

    // The attribute name is matched WHOLE (any prefix, ending in one of the tokens) so compound
    // names like PlaceholderText and attached forms like ToolTip.Tip are covered - the exact
    // correction HaPlay's lint needed after its `\b`-anchored first version silently skipped them.
    private static readonly Regex Attr = new(
        @"(?<![\w.])([\w.]*(?:Text|Content|Header|Title|Tip|Watermark))\s*=\s*""([^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex GlyphEntity = new(@"^\s*(&#x?[0-9A-Fa-f]+;\s*)+$", RegexOptions.Compiled);

    // Attribute names that merely END in a scanned token but never carry user-facing copy.
    private static readonly HashSet<string> NotUserFacingAttributes =
        new(StringComparer.Ordinal) { "SizeToContent" };

    [Fact]
    public void Views_DoNotAddRawUserFacingStringLiterals()
    {
        var viewsDir = Path.Combine(RepoRoot(), "UI", "HaCue2", "Views");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(viewsDir, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Attr.Matches(text))
                if (!NotUserFacingAttributes.Contains(m.Groups[1].Value) && IsUserFacing(m.Groups[2].Value))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}=\"{m.Groups[2].Value}\"");
        }

        output.WriteLine($"raw user-facing literals: {offenders.Count} (baseline {Baseline})");

        Assert.True(offenders.Count <= Baseline,
            $"hardcoded user-facing string literals grew to {offenders.Count} (baseline {Baseline}). Route new UI " +
            $"text through Resources/Strings.resx + the Strings accessor (see ShellWindow). Newest offenders:\n  " +
            string.Join("\n  ", offenders.TakeLast(15)));

        if (offenders.Count < Baseline)
            output.WriteLine($"NOTE: strings were migrated - lower the Baseline constant to {offenders.Count}.");
    }

    private static bool IsUserFacing(string value)
    {
        if (value.StartsWith('{')) return false;          // {Binding} / {x:Static ...}
        if (GlyphEntity.IsMatch(value)) return false;      // icon glyphs encoded as &#x…; entities
        return value.Count(char.IsLetter) >= 2;            // real words, not symbols / numbers / single chars
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.sln above the test output directory.");
        return dir!.FullName;
    }
}
