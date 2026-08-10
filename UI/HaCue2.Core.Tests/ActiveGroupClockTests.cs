using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Presentation;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The clock on a group header in the Active panel.
/// </summary>
/// <remarks>
/// It is the number an operator reads to decide whether they have time to do something else, so a
/// group that claims half an hour when it runs for three minutes is worse than one showing nothing.
/// Every fire mode measures its span differently and only one of them adds up.
/// </remarks>
public class ActiveGroupClockTests
{
    private static MediaCueNode Stem(string number, int offsetMs = 0) =>
        new() { Number = number, Label = number, MediaPath = $"{number}.wav", TimelineOffsetMs = offsetMs };

    /// <summary>One sounding child, thirty seconds in, out of a three-minute file.</summary>
    private static ActiveCueState Playing(MediaCueNode cue, Guid listId, double elapsedSeconds) =>
        new(cue.Id, listId, TimeSpan.FromSeconds(elapsedSeconds), TimeSpan.FromMinutes(3), IsFading: false, StartedTicks: 0);

    private static (ActiveGroupRow Header, GroupCueNode Group) Panel(
        GroupFireMode mode, IReadOnlyList<MediaCueNode> children, double elapsedSeconds)
    {
        var group = new GroupCueNode
        {
            Number = "1", Label = "Band", FireMode = mode, Children = [.. children],
        };

        var list = new CueList { Name = "Act 1", Cues = [group] };
        var project = new HaCueProject { CueLists = [list] };

        var durations = children.ToDictionary(child => child.Id, _ => TimeSpan.FromMinutes(3));
        var states = children.Select(child => Playing(child, list.Id, elapsedSeconds)).ToList();

        var active = CuePresentation.Active(project, states, durations);
        var rows = CuePresentation.ActivePanel(project, active, durations);

        return (rows.OfType<ActiveGroupRow>().Single(), group);
    }

    [Fact]
    public void AnAllTogetherGroupRunsForAsLongAsItsLongestChildNotTheSumOfThem()
    {
        var children = Enumerable.Range(1, 11).Select(index => Stem($"1.{index}")).ToList();
        var (header, _) = Panel(GroupFireMode.AllTogether, children, elapsedSeconds: 30);

        // Eleven three-minute stems fired at once run for three minutes, with two and a half left —
        // not thirty-three minutes with twenty-seven and a half left.
        Assert.Equal("−02:30.000 / 03:00.000", header.Clock);
        Assert.InRange(header.Progress, 0.16, 0.17);
    }

    [Fact]
    public void APlaylistStillAddsItsItemsUpBecauseTheyPlayOneAfterAnother()
    {
        var first = Stem("1.1");
        var second = Stem("1.2");

        // Only the first is sounding: a playlist plays one item at a time.
        var group = new GroupCueNode
        {
            Number = "1", Label = "Interval", FireMode = GroupFireMode.Playlist,
            Children = [first, second],
        };

        var list = new CueList { Name = "Act 1", Cues = [group] };
        var project = new HaCueProject { CueLists = [list] };
        var durations = new Dictionary<Guid, TimeSpan>
        {
            [first.Id] = TimeSpan.FromMinutes(3),
            [second.Id] = TimeSpan.FromMinutes(3),
        };

        var active = CuePresentation.Active(project, [Playing(first, list.Id, 30)], durations);
        var header = CuePresentation.ActivePanel(project, active, durations).OfType<ActiveGroupRow>().Single();

        // Six minutes of material, two and a half left of item one plus three still queued.
        Assert.Equal("−05:30.000 / 06:00.000", header.Clock);
        Assert.Single(header.Upcoming);
        Assert.Equal("in 02:30", header.Upcoming[0].Countdown);
    }

    [Fact]
    public void ATimelineSpansFromItsZeroToTheEndOfItsLastCue()
    {
        var bed = Stem("1.1");
        var stab = Stem("1.2", offsetMs: 60_000);

        var group = new GroupCueNode
        {
            Number = "1", Label = "Opening", FireMode = GroupFireMode.Timeline, Children = [bed, stab],
        };

        var list = new CueList { Name = "Act 1", Cues = [group] };
        var project = new HaCueProject { CueLists = [list] };
        var durations = new Dictionary<Guid, TimeSpan>
        {
            [bed.Id] = TimeSpan.FromMinutes(3),
            [stab.Id] = TimeSpan.FromMinutes(3),
        };

        var active = CuePresentation.Active(project, [Playing(bed, list.Id, 30)], durations);
        var header = CuePresentation.ActivePanel(project, active, durations).OfType<ActiveGroupRow>().Single();

        // The bed runs 0:00–3:00 and the stab 1:00–4:00, so the group is four minutes long and thirty
        // seconds in. The stab is due in another thirty.
        Assert.Equal("−03:30.000 / 04:00.000", header.Clock);
        Assert.Single(header.Upcoming);
        Assert.Equal("in 00:30", header.Upcoming[0].Countdown);
    }
}
