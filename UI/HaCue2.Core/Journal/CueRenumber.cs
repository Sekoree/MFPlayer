using HaCue2.Core.Model;

namespace HaCue2.Core.Journal;

/// <summary>
/// Renumbering a run of sibling cues, keeping the dotted scheme.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for both callers. The Renumber dialog and auto-renumber-on-insert are the same
/// operation asked for two different reasons, and they disagreed: the dialog carried each group's
/// number down to its children, and the insert path assigned bare integers at every depth. So adding
/// one cue inside a group renumbered <c>1.1, 1.2, 1.3</c> to <c>1, 2, 3</c> — which collides with the
/// top level and destroys the numbering an operator calls over comms.
/// </para>
/// <para>
/// A group's children hang off ITS number, so a subtree is renumbered whenever its owner is: leaving
/// grandchildren at <c>2.1.1</c> under a cue that has just become <c>2.3</c> is the same defect one
/// level down.
/// </para>
/// </remarks>
public static class CueRenumber
{
    /// <summary>How deep the recursion will follow a subtree, as a guard against a cyclic document.</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Numbers <paramref name="siblings"/> in order, and their subtrees under them.
    /// </summary>
    /// <param name="prefix">
    /// The owning group's number, or empty for a cue list's top level. <see cref="CueNumber.Child"/>
    /// already collapses the empty case to a bare integer, so the two depths need no separate branch.
    /// </param>
    public static void Apply(
        ProjectJournal journal,
        IReadOnlyList<CueNode> siblings,
        CueNumber prefix = default,
        int start = 1,
        int step = 1)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(siblings);

        Apply(journal, siblings, prefix, start, step, depth: 0);
    }

    private static void Apply(
        ProjectJournal journal,
        IReadOnlyList<CueNode> siblings,
        CueNumber prefix,
        int start,
        int step,
        int depth)
    {
        if (depth >= MaxDepth)
            return;

        var next = start;

        foreach (var cue in siblings)
        {
            var number = prefix.Child(next);
            var target = cue;

            // Skipped when it already reads that way: a renumber that rewrote every cue would fill the
            // undo stack with no-ops and mark a document dirty for changing nothing.
            if (target.Number != number)
            {
                journal.Do(new SetValueCommand<CueNumber>(
                    target.Id,
                    "number",
                    "cues",
                    () => target.Number,
                    value => target.Number = value,
                    number,
                    $"renumber to {number}"));
            }

            // Children always restart at 1 in steps of 1: the step belongs to the level the operator
            // asked about, and "start at 10, step 10" means 10, 20, 30 with 10.1, 10.2 inside — not
            // 10.10, 10.20.
            if (cue is GroupCueNode group)
                Apply(journal, group.Children, number, start: 1, step: 1, depth + 1);

            next += step;
        }
    }
}
