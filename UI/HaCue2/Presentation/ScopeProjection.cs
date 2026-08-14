using HaCue2.Core.Model;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Presentation;

/// <summary>
/// Turns the document into the tree's SCOPE surface (review F-11): the roots the navigator lists,
/// and the rows the tree shows for whichever root is selected.
/// </summary>
/// <remarks>
/// Pure functions over the project, split out of <c>CuesViewModel</c> so "the navigator shows X"
/// and "this scope shows those cues" are direct assertions instead of shell-sized ones. The row
/// building itself stays in <see cref="CuePresentation"/> - this is only the scope filter applied
/// to it.
/// </remarks>
public static class ScopeProjection
{
    /// <summary>
    /// Every list and every group, as scope roots.
    /// </summary>
    /// <remarks>
    /// The tallies are counts of the real subtree, so a group that gains a cue gains a number here
    /// without anyone updating a string. Groups are indented by depth for the same reason the tree is.
    /// </remarks>
    public static IReadOnlyList<ScopeEntry> Scopes(HaCueProject project)
    {
        var entries = new List<ScopeEntry>();

        foreach (var list in project.CueLists)
        {
            entries.Add(new ScopeEntry(list.Id, list.Name, list.Flatten().Count(), IsList: true, 0));

            foreach (var (group, depth) in GroupsIn(list.Cues, 0))
                entries.Add(new ScopeEntry(
                    group.Id,
                    $"{CuePresentation.Number(group.Number)} · {group.Label}",
                    CountIn(group),
                    IsList: false,
                    depth));
        }

        return entries;
    }

    /// <summary>The rows the tree shows for <paramref name="scope"/>: a list's whole tree, a
    /// group's subtree, or nothing when the scope no longer resolves.</summary>
    public static IReadOnlyList<CueRow> Rows(ScopeEntry? scope, HaCueProject project, ShowRuntime runtime) =>
        scope switch
        {
            { IsList: true } when project.CueLists.FirstOrDefault(l => l.Id == scope.Id) is { } list =>
                CuePresentation.Rows(list, project, runtime),
            { IsList: false } when project.FindCue(scope.Id) is { } cue =>
                CuePresentation.Subtree(cue, project, runtime),
            _ => [],
        };

    /// <summary>The name of the list holding <paramref name="groupId"/>, for the breadcrumb.</summary>
    public static string ListNameOf(HaCueProject project, Guid groupId) =>
        project.CueLists.FirstOrDefault(list => list.Flatten().Any(cue => cue.Id == groupId))?.Name
        ?? "show";

    private static IEnumerable<(GroupCueNode Group, int Depth)> GroupsIn(
        IEnumerable<CueNode> cues, int depth)
    {
        foreach (var cue in cues)
        {
            if (cue is not GroupCueNode group)
                continue;

            yield return (group, depth);
            foreach (var nested in GroupsIn(group.Children, depth + 1))
                yield return nested;
        }
    }

    private static int CountIn(GroupCueNode group) =>
        group.Children.Count + group.Children.OfType<GroupCueNode>().Sum(CountIn);
}
