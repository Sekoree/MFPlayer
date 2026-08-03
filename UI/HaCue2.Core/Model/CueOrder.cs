namespace HaCue2.Core.Model;

/// <summary>
/// Where the GO cursor goes next.
/// </summary>
/// <remarks>
/// One rule, in one place, because two places would eventually disagree — and the two callers are the
/// running transport and the editor's cursor-only GO, which is exactly the pair whose disagreement
/// nobody would notice until a show. The engine and the shell both ask here.
/// </remarks>
public static class CueOrder
{
    /// <summary>
    /// The next enabled cue after this one, in the list's own fire order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A group's children are skipped, not stepped into.</b> <see cref="CueList.Flatten"/> is
    /// depth-first, so a group's descendants immediately follow it — and "next" after a group means the
    /// cue AFTER the group, because firing the group already dealt with everything inside it. Stepping
    /// in would fire a group's first child twice: once as part of the group, once as the next cue.
    /// </para>
    /// <para>
    /// <b>Disabled cues are stepped over</b>, which is the whole reason disabling exists — dropping a
    /// cue for one performance by deleting it is how shows lose cues. A disabled GROUP takes its
    /// children with it: the operator disabled the whole thing.
    /// </para>
    /// </remarks>
    public static CueNode? NextEnabled(CueList list, Guid? after)
    {
        ArgumentNullException.ThrowIfNull(list);

        var order = list.Flatten().ToList();

        // No cursor yet means the top of the list — a fresh list's first GO fires its first cue.
        var at = after is { } id ? order.FindIndex(cue => cue.Id == id) : -1;

        if (after is not null && at < 0)
            return null;

        var index = at < 0 ? 0 : at + Subtree(order[at]);

        while (index < order.Count)
        {
            if (order[index].Enabled)
                return order[index];

            // A disabled group is skipped whole. Walking into it would fire children of a group the
            // operator switched off.
            index += Subtree(order[index]);
        }

        return null;
    }

    /// <summary>How many entries a cue occupies in a depth-first walk — itself plus its descendants.</summary>
    public static int Subtree(CueNode cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        return cue is GroupCueNode group ? 1 + group.Children.Sum(Subtree) : 1;
    }
}
