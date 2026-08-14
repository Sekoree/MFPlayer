using System.Globalization;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;

namespace HaCue2.ViewModels;

/// <summary>
/// The inspector's shared edit plumbing (review F-11): the multi-selection journal edits every
/// pane writes through, plus the field parsers that read back what the displays write. Extracted
/// so per-pane editors can take it by constructor - it IS the transaction boundary, so it lives
/// once, and a pane cannot acquire a private variant of the multi-selection rules.
/// </summary>
public sealed class CueEditPlumbing(ProjectJournal journal, IInspectorEditorContext context)
{
    /// <summary>
    /// Applies one property edit to every selected cue of the pane's kind, as one undo step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved per cue by TYPE, which is also the safety rule: a selection of mixed kinds only sees a
    /// pane at all when every member has that tab (the tab set is the intersection), and anything that
    /// is not a <typeparamref name="TCue"/> is skipped rather than coerced.
    /// </para>
    /// <para>
    /// Cues already holding the value are left out entirely, so a multi-selection edit produces one
    /// undo step containing only the cues it actually changed - and none at all when it changed
    /// nothing, which is what keeps a combo box re-announcing its own value from filling the stack.
    /// </para>
    /// </remarks>
    /// <param name="lead">
    /// The cue whose pane the operator is looking at. Present only so <typeparamref name="TCue"/> can
    /// be inferred at the call site - the edit itself is resolved against the selection, and the lead
    /// gets no special treatment beyond being one of them.
    /// </param>
    public void EditEach<TCue, T>(
        TCue lead,
        string property,
        string domain,
        Func<TCue, T> read,
        Action<TCue, T> write,
        T value,
        string description)
        where TCue : CueNode
    {
        _ = lead;

        var targets = context.Selected
            .OfType<TCue>()
            .Where(cue => !EqualityComparer<T>.Default.Equals(read(cue), value))
            .ToList();

        if (targets.Count == 0)
            return;

        if (targets.Count > 1)
        {
            using (journal.Composite($"{description} on {targets.Count} cues", domain))
                foreach (var cue in targets)
                    journal.Do(Write(cue));
        }
        else
        {
            journal.Do(Write(targets[0]));
        }

        journal.CloseGroup();
        context.Reload();
        return;

        SetValueCommand<T> Write(TCue cue) => new(
            cue.Id, property, domain, () => read(cue), parsed => write(cue, parsed), value, description);
    }

    /// <summary>
    /// The same, for a property whose new value has to be computed per cue.
    /// </summary>
    /// <remarks>
    /// Needed by every LIST-valued property here - a fade's targets, a jump's destination, a
    /// visualizer's feed. Handing one <c>List&lt;Guid&gt;</c> to eleven cues would alias them all onto
    /// a single instance, so editing one afterwards would silently edit the rest; and a relative change
    /// ("add this channel") means something different on each cue and must be recomputed from that
    /// cue's own state rather than from the lead's.
    /// </remarks>
    public void EditEach<TCue, T>(
        TCue lead,
        string property,
        string domain,
        Func<TCue, T> read,
        Action<TCue, T> write,
        Func<TCue, T> value,
        string description)
        where TCue : CueNode
    {
        _ = lead;

        var targets = context.Selected
            .OfType<TCue>()
            .Select(cue => (Cue: cue, Value: value(cue)))
            .Where(pair => !EqualityComparer<T>.Default.Equals(read(pair.Cue), pair.Value))
            .ToList();

        if (targets.Count == 0)
            return;

        if (targets.Count > 1)
        {
            using (journal.Composite($"{description} on {targets.Count} cues", domain))
                foreach (var (cue, next) in targets)
                    journal.Do(Write(cue, next));
        }
        else
        {
            journal.Do(Write(targets[0].Cue, targets[0].Value));
        }

        journal.CloseGroup();
        context.Reload();
        return;

        SetValueCommand<T> Write(TCue cue, T next) => new(
            cue.Id, property, domain, () => read(cue), parsed => write(cue, parsed), next, description);
    }

    /// <summary>
    /// Parses a level, accepting the U+2212 MINUS SIGN the app renders as well as a plain hyphen.
    /// </summary>
    /// <remarks>
    /// The display uses a true minus because it aligns in a tabular column; a parser that only knew
    /// hyphens would refuse to read back the value it had just written.
    /// </remarks>
    public static bool TryParseDb(string text, out double value) =>
        double.TryParse(
            text.Replace('−', '-').Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Parses a duration in seconds, accepting the "4.0 s" form the displays write.
    /// </summary>
    /// <remarks>
    /// A parser that only accepted bare numbers would refuse to read back the value it had just
    /// written, which is the commonest way a field appears not to work. Negative durations are
    /// refused rather than clamped - the field keeps the old value so nothing silently becomes zero.
    /// </remarks>
    public static bool TryParseSeconds(string text, out int milliseconds)
    {
        milliseconds = 0;

        var cleaned = text.Replace("s", "", StringComparison.OrdinalIgnoreCase)
            .Replace('−', '-')
            .Trim();

        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && !double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds))
            return false;

        if (seconds < 0 || double.IsNaN(seconds))
            return false;

        milliseconds = (int)Math.Round(seconds * 1000);
        return true;
    }
}
