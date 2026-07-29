using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

/// <summary>
/// UX-07: user-facing UI text must go through the <c>Strings</c> resource system, not be hardcoded in AXAML. This
/// lint scans the Views for raw literal <c>Text</c>/<c>Content</c>/<c>Header</c>/<c>Title</c>/<c>ToolTip.Tip</c>/
/// <c>Watermark</c> values that look like words - exempting bindings, <c>x:Static</c>, glyph entities, and single
/// symbols - and fails if the count exceeds the tracked baseline. So no NEW hardcoded string is added, and the
/// existing debt is migrated down over time (lower <see cref="Baseline"/> as strings move to Strings.resx; the
/// test prints the new floor when it drops). See <c>MIDIDevicesView</c> for the migration pattern.
/// </summary>
public sealed class RawStringLiteralLintTests(ITestOutputHelper output)
{
    // Tracked debt of hardcoded user-facing literals across ALL Views (scanned recursively - top-level views,
    // Dialogs/, and the ControlPanes/ dock panes). RATCHET ONLY DOWNWARD - never raise this to accommodate a
    // new literal. (Jumped from 166 to 264 when the scan went recursive: the previous top-level-only glob was
    // blind to Dialogs/ and to the ControlPanes/ views the Control workspace tabs were extracted into.)
    // 264 -> 260 on 2026-07-29: the cue-player round (triggers, timecode chase, master fader, layout
    // rebuild) routed all of its text through Strings.resx and migrated a few existing literals.
    // 260 -> 317 the same day when the attribute regex was CORRECTED (see below): compound names like
    // PlaceholderText were never being scanned, so literals had been invisible since the lint was
    // written. Like the 166 -> 264 jump when the scan went recursive, this is the scan getting more
    // accurate, NOT permission to add literals - the ratchet rule below still stands.
    // 317 -> 304 on review of that re-baseline, which had absorbed two entries that were NOT
    // pre-existing debt: 12 of the 57 were `SizeToContent="Height"` (a layout enum the widened regex
    // dragged in - now excluded by name, see NotUserFacingAttributes; the true pre-existing count was
    // 55), and one was a literal ADDED by the same round, `PlaceholderText="/haplay/cue/5"` in
    // CuePlayerView, now routed through Strings.CueTriggerOscAddressPlaceholder like its neighbours.
    // Re-baselining a corrected scan is fine; re-baselining over a new literal is what the ratchet is
    // there to stop, so verify the delta against the pre-round tree before ever raising this.
    private const int Baseline = 304;

    // The old `\b(Text|Content|…)` made the scan blind to any attribute ENDING in one of these
    // names, because `\b` cannot match between two word characters: `PlaceholderText="…"` - the one
    // actually in use - slipped past it entirely, so placeholder copy was never linted. Now the
    // attribute name is matched WHOLE (any prefix, ending in one of the tokens), which also covers
    // attached forms like `ToolTip.Tip`. Group 1 stays the attribute name, group 2 the value.
    private static readonly Regex Attr = new(
        @"(?<![\w.])([\w.]*(?:Text|Content|Header|Title|Tip|Watermark))\s*=\s*""([^""]*)""",
        RegexOptions.Compiled);
    private static readonly Regex GlyphEntity = new(@"^\s*(&#x?[0-9A-Fa-f]+;\s*)+$", RegexOptions.Compiled);

    // Attribute names that merely END in one of the scanned tokens but never carry user-facing copy.
    // Matching the name WHOLE (the correction that finally exposed PlaceholderText) also dragged
    // `SizeToContent="Height"` in - a Window layout enum. Those 12 hits inflated the tracked debt with
    // entries that can never be migrated to Strings.resx, so the ratchet could never reach them. Add
    // to this set rather than raising Baseline when a non-text attribute starts matching.
    private static readonly HashSet<string> NotUserFacingAttributes =
        new(StringComparer.Ordinal) { "SizeToContent" };

    [Fact]
    public void Views_DoNotAddRawUserFacingStringLiterals()
    {
        var viewsDir = Path.Combine(RepoRoot(), "UI", "HaPlay", "Views");
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
            $"text through Resources/Strings.resx + a Strings accessor (see MIDIDevicesView). Newest offenders:\n  " +
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
