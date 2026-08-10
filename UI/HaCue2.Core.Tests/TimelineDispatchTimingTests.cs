using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Late-dispatch telemetry: a timeline event that fires past its authored time must be VISIBLE - counted,
/// its worst slip retained, and reported to the operator when the slip is beyond what any healthy rig
/// produces. Before this existed, an event dispatched 300 ms late was indistinguishable in every log and
/// panel from one dispatched on time.
/// </summary>
public class TimelineDispatchTimingTests
{
    private static (CueExecutor Executor, FakeCueHost Host, GroupCueNode Group) Scene()
    {
        var bed = new MediaCueNode { Number = "1.1", Label = "Bed", MediaPath = "bed.wav" };
        var stab = new MediaCueNode
        {
            Number = "1.2", Label = "Stab", MediaPath = "stab.wav", TimelineOffsetMs = 30_000,
        };
        var group = new GroupCueNode
        {
            Number = "1", Label = "Opening", FireMode = GroupFireMode.Timeline,
            Children = [bed, stab],
        };
        var project = new HaCueProject { CueLists = [new CueList { Name = "Act 1", Cues = [group] }] };
        var host = new FakeCueHost(project);
        host.Lengths[bed.Id] = TimeSpan.FromMinutes(3);
        host.Lengths[stab.Id] = TimeSpan.FromSeconds(2);
        return (new CueExecutor(host), host, group);
    }

    [Fact]
    public async Task AnOnTimeDispatchCountsButIsNotLate()
    {
        var (executor, host, group) = Scene();

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        // The initial event IS the timeline epoch and cannot be late by definition; the stab's edge is
        // the one measured. The virtual clock advances exactly what the scheduler asks for, so the
        // measured slip is zero.
        var timing = executor.TimelineDispatchTiming;
        Assert.Equal(1, timing.Dispatched);
        Assert.Equal(0, timing.Late);
        Assert.DoesNotContain(host.Problems, problem => problem.Contains("late"));
    }

    [Fact]
    public async Task AnEventDispatchedWellPastItsDueTimeIsCountedAndReported()
    {
        var (executor, host, group) = Scene();

        // One 100 ms stall as the scheduler closes on the stab's 30 s edge models the excursions this
        // telemetry exists for (a dispatcher turn queued behind long work, a machine stall): the wake
        // lands 50 ms past the authored coordinate and the dispatch is measurably late in SHOW time.
        host.TimelineDelayOverride = (duration, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            host.AdvanceTimeline(duration);
            if (host.TotalTimelineAdvanced >= TimeSpan.FromMilliseconds(29_950)
                && host.TotalTimelineAdvanced < TimeSpan.FromSeconds(30))
                host.AdvanceTimeline(TimeSpan.FromMilliseconds(100));
            return Task.CompletedTask;
        };

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        var timing = executor.TimelineDispatchTiming;
        Assert.Equal(1, timing.Dispatched);
        Assert.Equal(1, timing.Late);
        Assert.True(timing.MaxLateness >= TimeSpan.FromMilliseconds(45),
            $"expected a reportable slip, measured {timing.MaxLateness.TotalMilliseconds:0.0} ms");
        Assert.Contains(host.Problems, problem => problem.Contains("ms late"));
    }
}
