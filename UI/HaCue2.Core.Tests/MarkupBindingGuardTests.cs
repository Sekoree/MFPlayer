using System.Text.RegularExpressions;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// No input control in the app renders a value the document cannot change.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the same defect kept arriving in a different pane. Six times a screen was drawn
/// complete against literals — cue control-flow, the audition rig, trigger bindings, effect lanes, the
/// record pane, the video output pane — and each time it looked finished in a screenshot while editing
/// nothing. They were found one at a time, by asking "can you actually make one?" of whatever was in
/// front of us, which is not a method.
/// </para>
/// <para>
/// The rule is narrow enough to be true and broad enough to catch that: an INPUT control — a text box,
/// a selector, a checkbox — must take its value from a binding. Static labels, column headings and
/// explanatory sentences are untouched, because those are the things a literal is actually right for.
/// A control that genuinely has nothing behind it belongs in <see cref="Unimplemented"/>, where it is a
/// listed gap rather than a screenshot of a feature.
/// </para>
/// </remarks>
public class MarkupBindingGuardTests
{
    /// <summary>
    /// Controls with no model behind them yet, each with the reason it is not simply a bug.
    /// </summary>
    /// <remarks>
    /// Adding a line here is a deliberate statement that a feature is unfinished. Removing one is what
    /// finishing it looks like. The point is that the list is SHORT, VISIBLE and has to be argued for —
    /// not that it is empty.
    /// </remarks>
    private static readonly Dictionary<string, string> Unimplemented = new(StringComparer.Ordinal)
    {
        // EMPTY, and that is a result rather than a stub: every input control in the app now writes
        // somewhere. The last two out were the curve picker (it sets the fade law and clears the drawn
        // points that would have beaten it) and the timeline's snap/free toggle.
    };

    /// <summary>Every view and control markup file in the app.</summary>
    private static IEnumerable<string> MarkupFiles()
    {
        var root = Repository();

        foreach (var folder in new[] { "Views", "Controls", "Themes" })
        {
            var path = Path.Combine(root, "UI", "HaCue2", folder);

            if (!Directory.Exists(path))
                continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.axaml", SearchOption.AllDirectories))
                yield return file;
        }
    }

    /// <summary>Walks up from the test binaries to the repository root.</summary>
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MFPlayer.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>An opening tag for one of the controls the rule covers.</summary>
    private static readonly Regex Control = new(
        // ListBox is here because the app's segmented controls ARE list boxes — the two-item
        // "fullscreen / windowed" strips. Leaving it out would have exempted most of the selectors in
        // the app from a guard whose whole purpose is selectors.
        @"<(TextBox|ComboBox|ListBox|CheckBox|ToggleSwitch|Slider|NumericUpDown)\b[^>]*?(?:/>|>)",
        RegexOptions.Singleline);

    /// <summary>A value-carrying attribute, and whether it is a binding.</summary>
    private static readonly Regex Value = new(
        @"\b(Text|SelectedIndex|SelectedItem|IsChecked|Value)=""([^""]*)""");

    [Fact]
    public void NoInputControlRendersALiteralValue()
    {
        var offences = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var markup = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match control in Control.Matches(markup))
            {
                var tag = control.Groups[1].Value;

                if (Unimplemented.ContainsKey($"{name}:{tag}"))
                    continue;

                foreach (Match value in Value.Matches(control.Value))
                {
                    // A Slider's Maximum-style bounds and a ComboBox's placeholder are not values; only
                    // the attributes above are, and only when they are not bindings.
                    if (value.Groups[2].Value.StartsWith('{'))
                        continue;

                    var line = markup[..control.Index].Count(character => character == '\n') + 1;

                    offences.Add(
                        $"{name}({line}): <{tag} {value.Groups[1].Value}=\"{value.Groups[2].Value}\">");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "An input control is rendering a value nothing can change. Bind it, make it a TextBlock if "
            + "it is really a label, or add it to Unimplemented with the reason:\n  "
            + string.Join("\n  ", offences));
    }

    [Fact]
    public void TheUnimplementedListIsSmallAndReasoned()
    {
        // A guard whose exception list grows without argument is not a guard. Both halves matter: the
        // count keeps it from becoming a dumping ground, and the reason keeps each entry honest.
        Assert.True(Unimplemented.Count <= 6, $"{Unimplemented.Count} exceptions is too many to be a gap list");

        foreach (var (control, reason) in Unimplemented)
            Assert.True(reason.Length > 30, $"{control} needs a real reason, not “{reason}”");
    }

    [Fact]
    public void EveryListedExceptionStillExists()
    {
        // An exception left behind after the control was fixed or deleted would quietly re-open the
        // hole it was documenting.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in MarkupFiles())
        {
            var markup = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match control in Control.Matches(markup))
            {
                var key = $"{name}:{control.Groups[1].Value}";

                if (Unimplemented.ContainsKey(key)
                    && Value.Matches(control.Value).Any(value => !value.Groups[2].Value.StartsWith('{')))
                    seen.Add(key);
            }
        }

        Assert.Equal(Unimplemented.Keys.OrderBy(key => key, StringComparer.Ordinal), seen.OrderBy(key => key, StringComparer.Ordinal));
    }

    // ── the same rule for buttons ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Buttons that are drawn and do nothing, each with the reason.
    /// </summary>
    /// <remarks>
    /// A button is the most convincing kind of dead surface: it is the thing an operator presses when
    /// they have decided what they want. These are listed rather than removed because the mockup's
    /// design intent is worth keeping — but listed, so nobody has to rediscover them by pressing.
    /// </remarks>
    private static readonly Dictionary<string, string> InertButtons = new(StringComparer.Ordinal)
    {
        // EMPTY, and that is a result rather than a stub: every button drawn in the app now does
        // something. The last out were the two audio-output verbs, IDENTIFY, and the timeline
        // transport row — see the plan's "Interface drawn but unimplemented" table, which this list
        // was the enforcement half of.
    };

    /// <summary>A self-closing button, which is the shape one with no handler takes.</summary>
    private static readonly Regex SelfClosingButton = new(@"<Button\b[^>]*?/>", RegexOptions.Singleline);

    [Fact]
    public void NoButtonIsSilentlyInert()
    {
        var offences = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var markup = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match button in SelfClosingButton.Matches(markup))
            {
                var element = button.Value;

                if (element.Contains("Click=", StringComparison.Ordinal)
                    || element.Contains("Command=", StringComparison.Ordinal)
                    || element.Contains("PointerPressed=", StringComparison.Ordinal))
                    continue;

                var content = Regex.Match(element, @"Content=""([^""]*)""").Groups[1].Value;

                // A bound label belongs to a button whose handler is elsewhere in the element.
                if (content.StartsWith('{') || InertButtons.ContainsKey(content))
                    continue;

                var line = markup[..button.Index].Count(character => character == '\n') + 1;
                offences.Add($"{name}({line}): “{content}”");
            }
        }

        Assert.True(
            offences.Count == 0,
            "A button is drawn and does nothing. Wire it, or add it to InertButtons with the reason:\n  "
            + string.Join("\n  ", offences));
    }

    [Fact]
    public void EveryInertButtonStillExists()
    {
        var content = MarkupFiles()
            .SelectMany(file => SelfClosingButton.Matches(File.ReadAllText(file)))
            .Select(button => Regex.Match(button.Value, @"Content=""([^""]*)""").Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // A button that has since been wired must leave this list, or the list stops meaning anything.
        foreach (var listed in InertButtons.Keys)
            Assert.True(content.Contains(listed), $"“{listed}” is no longer an inert button — remove it");
    }
}
