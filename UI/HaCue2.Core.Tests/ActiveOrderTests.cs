using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Presentation;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The Active panel's row order.
/// </summary>
/// <remarks>
/// Rows are ordered by WHEN each cue was fired, and that order must never change while a cue runs.
/// The old key was the playhead, which rewinds on loop wraps, jumps on seeks and freezes on pause —
/// so the list reshuffled under the operator's pointer whenever any of those happened.
/// </remarks>
public class ActiveOrderTests
{
    private static MediaCueNode Cue(string number) =>
        new() { Number = number, Label = number, MediaPath = $"{number}.wav" };

    private static ActiveCueState State(MediaCueNode cue, Guid listId, long startedTicks, double elapsedSeconds) =>
        new(cue.Id, listId, TimeSpan.FromSeconds(elapsedSeconds), TimeSpan.FromMinutes(3),
            IsFading: false, StartedTicks: startedTicks);

    [Fact]
    public void RowsFollowFireOrderNotThePlayhead()
    {
        var first = Cue("1");
        var second = Cue("2");
        var third = Cue("3");
        var list = new CueList { Name = "Act 1", Cues = [first, second, third] };
        var project = new HaCueProject { CueLists = [list] };

        // Fired 1 → 2 → 3, but the playheads disagree with that order: the first cue just LOOPED
        // (playhead rewound to nearly zero) and the third was seeked ahead. The old
        // sort-by-playhead put 3 first and 1 last; fire order keeps 1, 2, 3.
        var states = new[]
        {
            State(second, list.Id, startedTicks: 2_000, elapsedSeconds: 50),
            State(first, list.Id, startedTicks: 1_000, elapsedSeconds: 0.5),
            State(third, list.Id, startedTicks: 3_000, elapsedSeconds: 170),
        };

        var rows = CuePresentation.Active(project, states, new Dictionary<Guid, TimeSpan>());

        Assert.Equal(["1", "2", "3"], rows.Select(row => row.Label).ToArray());
    }

    [Fact]
    public void APlayheadChangeNeverReordersTheRows()
    {
        var first = Cue("1");
        var second = Cue("2");
        var list = new CueList { Name = "Act 1", Cues = [first, second] };
        var project = new HaCueProject { CueLists = [list] };
        var durations = new Dictionary<Guid, TimeSpan>();

        string[] Order(double firstElapsed, double secondElapsed) =>
        [
            .. CuePresentation.Active(
                project,
                [
                    State(first, list.Id, startedTicks: 1_000, elapsedSeconds: firstElapsed),
                    State(second, list.Id, startedTicks: 2_000, elapsedSeconds: secondElapsed),
                ],
                durations).Select(row => row.Label),
        ];

        // Before the first cue's loop wrap, after it, and with the second seeked past the first:
        // the order is the fire order every time.
        Assert.Equal(["1", "2"], Order(firstElapsed: 100, secondElapsed: 20));
        Assert.Equal(["1", "2"], Order(firstElapsed: 0.1, secondElapsed: 20));
        Assert.Equal(["1", "2"], Order(firstElapsed: 30, secondElapsed: 120));
    }

    [Fact]
    public void ABatchFireBreaksTiesByCueIdSoTheOrderIsDeterministic()
    {
        var cues = Enumerable.Range(1, 5).Select(index => Cue($"1.{index}")).ToList();
        var list = new CueList { Name = "Act 1", Cues = [.. cues.Cast<CueNode>()] };
        var project = new HaCueProject { CueLists = [list] };

        // All stamped in the same batch (identical StartedTicks), shuffled input order.
        var shuffled = new[] { cues[3], cues[0], cues[4], cues[2], cues[1] }
            .Select(cue => State(cue, list.Id, startedTicks: 1_000, elapsedSeconds: 10));

        var once = CuePresentation.Active(project, [.. shuffled], new Dictionary<Guid, TimeSpan>())
            .Select(row => row.CueId).ToArray();
        var again = CuePresentation.Active(project, [.. shuffled], new Dictionary<Guid, TimeSpan>())
            .Select(row => row.CueId).ToArray();

        Assert.Equal(once, again);
        Assert.Equal(once.OrderBy(id => id).ToArray(), once);
    }
}
