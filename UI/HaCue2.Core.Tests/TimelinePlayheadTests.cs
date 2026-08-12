using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// ▶ FROM PLAYHEAD: running a timeline group from a moment inside it.
/// </summary>
/// <remarks>
/// The point of the feature is the state the show would be IN at that moment — the cues after the
/// playhead scheduled, and the bed already running under them part-way through. Skipping the bed would
/// mean rehearsing the second half of a scene without the half of it an operator is least able to
/// judge by imagination.
/// </remarks>
public class TimelinePlayheadTests
{
    private static (CueExecutor Executor, FakeCueHost Host, GroupCueNode Group,
        MediaCueNode Bed, MediaCueNode Stab, MediaCueNode Late) Scene()
    {
        // A three-minute bed from 0:00, a stab at 0:30, and a sting at 2:00.
        var bed = new MediaCueNode { Number = "1.1", Label = "Bed", MediaPath = "bed.wav" };
        var stab = new MediaCueNode
        {
            Number = "1.2", Label = "Stab", MediaPath = "stab.wav", TimelineOffsetMs = 30_000,
        };

        var late = new MediaCueNode
        {
            Number = "1.3", Label = "Sting", MediaPath = "sting.wav", TimelineOffsetMs = 120_000,
        };

        var group = new GroupCueNode
        {
            Number = "1", Label = "Opening", FireMode = GroupFireMode.Timeline,
            Children = [bed, stab, late],
        };

        var project = new HaCueProject { CueLists = [new CueList { Name = "Act 1", Cues = [group] }] };
        var host = new FakeCueHost(project);

        host.Lengths[bed.Id] = TimeSpan.FromMinutes(3);
        host.Lengths[stab.Id] = TimeSpan.FromSeconds(2);
        host.Lengths[late.Id] = TimeSpan.FromSeconds(4);

        return (new CueExecutor(host), host, group, bed, stab, late);
    }

    [Fact]
    public async Task FromTheTopIsExactlyWhatFiringTheGroupAlwaysDid()
    {
        var (executor, host, group, bed, stab, late) = Scene();

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        // The virtual master advances instantly, but each child crosses its edge at the authored time.
        // Order and identity are exact; the recorded master time carries a small tolerance because the fake
        // host samples a clock SHARED by every timeline branch, at whatever moment its continuation runs -
        // so a concurrent branch's advance can land between the edge release and this read. That is a
        // property of the harness, not of the schedule (production reads a monotonic device clock and would
        // jitter the same way). Asserting exact equality made this fail intermittently under parallel load.
        var tolerance = TimeSpan.FromMilliseconds(250);
        Assert.Equal([bed.Id, stab.Id, late.Id], host.TimelineStarts.Select(entry => entry.Cue));
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)],
            host.TimelineStarts.Select(entry => Nearest(entry.MasterTime)));

        TimeSpan Nearest(TimeSpan actual) =>
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2) }
                .FirstOrDefault(edge => (actual - edge).Duration() <= tolerance, actual);
    }

    [Fact]
    public async Task ACueAfterThePlayheadIsScheduledRelativeToIt()
    {
        var (executor, host, group, _, _, late) = Scene();

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        // 2:00 in the scene is one minute after a playhead at 1:00. Scheduling it at its raw offset
        // would put it a minute late.
        var scheduled = Assert.Single(host.TimelineStarts, entry => entry.Cue == late.Id);
        Assert.Equal(TimeSpan.FromSeconds(60), scheduled.MasterTime);
    }

    [Fact]
    public async Task ACueThatHasAlreadyFinishedIsNotFiredAtAll()
    {
        var (executor, host, group, _, stab, _) = Scene();

        // The stab runs 0:30–0:32. At 1:00 it is over, and firing it would put a sound in the room
        // that the show does not contain at that moment.
        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.DoesNotContain(stab.Id, host.Played);
        Assert.DoesNotContain(host.TimelineStarts, entry => entry.Cue == stab.Id);
    }

    [Fact]
    public async Task ACueStraddlingThePlayheadStartsPartWayThroughItsFile()
    {
        var (executor, host, group, bed, _, _) = Scene();

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        // It is armed directly at the in-progress FILE position before the edge; no start-at-zero flash/seek.
        var start = Assert.Single(host.TimelineStarts, entry => entry.Cue == bed.Id);
        Assert.Equal(TimeSpan.FromSeconds(60), start.StartPosition);
        Assert.Equal(TimeSpan.Zero, start.MasterTime);
    }

    [Fact]
    public async Task TheSeekAddsTheClipsOwnInPointBecauseItIsInFileTime()
    {
        var (executor, host, group, bed, _, _) = Scene();
        bed.TrimInMs = 10_000;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        // A cue trimmed to start ten seconds in, rehearsed from a minute into the timeline, plays from
        // 1:10 of the FILE. Seeking to 1:00 would play ten seconds the show never contains.
        Assert.Equal(
            TimeSpan.FromSeconds(70),
            Assert.Single(host.TimelineStarts, start => start.Cue == bed.Id).StartPosition);
    }

    [Fact]
    public async Task ATrimmedCueEndsWhereItsTrimSaysRatherThanWhereTheFileDoes()
    {
        var (executor, host, group, bed, _, _) = Scene();

        // Three minutes of file, trimmed to the first forty seconds. At 1:00 it is over.
        bed.TrimOutMs = 40_000;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.DoesNotContain(bed.Id, host.Played);
    }

    [Fact]
    public async Task ALoopingCueRehearsesAtTheMatchingLoopPosition()
    {
        var (executor, host, group, bed, _, _) = Scene();
        host.Lengths[bed.Id] = TimeSpan.FromSeconds(20);
        bed.Loop = true;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(65));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            Assert.Single(host.TimelineStarts, start => start.Cue == bed.Id).StartPosition);
    }

    [Fact]
    public async Task AFrozenCueStillExistsAtALaterRehearsalPlayhead()
    {
        var (executor, host, group, bed, _, _) = Scene();
        host.Lengths[bed.Id] = TimeSpan.FromSeconds(20);
        bed.EndBehavior = CueEndBehavior.FreezeLastFrame;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(65));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.Equal(
            TimeSpan.FromMilliseconds(19_999),
            Assert.Single(host.TimelineStarts, start => start.Cue == bed.Id).StartPosition);
    }

    [Fact]
    public async Task ACueNobodyHasProbedIsPlayedRatherThanSilentlySkipped()
    {
        var (executor, host, group, bed, _, _) = Scene();
        host.Lengths.Remove(bed.Id);

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        // No probe means no length means no way to tell whether it is still running. Playing it is the
        // answer that leaves a rehearsal with something to listen to.
        Assert.Contains(bed.Id, host.Played);
    }

    [Fact]
    public async Task ADisabledCueIsSteppedOverWhereverThePlayheadIs()
    {
        var (executor, host, group, bed, _, late) = Scene();
        bed.Enabled = false;
        late.Enabled = false;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.Empty(host.Played);
        Assert.Empty(host.TimelineStarts);
    }

    [Fact]
    public async Task AClipThatWillNotOpenNeverCrossesTheTimelineEdge()
    {
        var (executor, host, group, _, _, _) = Scene();
        host.PlayFails = true;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.Empty(host.TimelineStarts);
    }

    [Fact]
    public async Task PreWaitIsPartOfTheMasterTimelineCoordinate()
    {
        var cue = new MediaCueNode
        {
            Number = "1.1", MediaPath = "cue.wav", TimelineOffsetMs = 1_000, PreWaitMs = 500,
        };
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.Timeline, Children = [cue],
        };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [group] }] };
        var host = new FakeCueHost(project);
        var executor = new CueExecutor(host);

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.Equal(TimeSpan.FromMilliseconds(1_500), Assert.Single(host.TimelineStarts).MasterTime);
        Assert.Empty(host.Waits); // no second wall-clock pre-wait after the master edge
    }

    [Fact]
    public async Task PauseFreezesTimelineEvenWhileTheDeviceClockKeepsAdvancing()
    {
        var cue = new MediaCueNode
        {
            Number = "1.1", MediaPath = "cue.wav", TimelineOffsetMs = 1_000,
        };
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.Timeline, Children = [cue],
        };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [group] }] };
        var host = new FakeCueHost(project);
        var polls = 0;
        host.TimelineDelayOverride = async (duration, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            host.TimelinePaused = Interlocked.Increment(ref polls) <= 10;
            host.AdvanceTimeline(duration);
        };
        var executor = new CueExecutor(host);

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.Equal(TimeSpan.FromMilliseconds(1_050), Assert.Single(host.TimelineStarts).MasterTime);
    }

    [Fact]
    public async Task DeviceClockReanchorDoesNotMoveAuthoredTimelinePositions()
    {
        var cue = new MediaCueNode
        {
            Number = "1.1", MediaPath = "cue.wav", TimelineOffsetMs = 1_000,
        };
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.Timeline, Children = [cue],
        };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [group] }] };
        var host = new FakeCueHost(project);
        var reanchored = false;
        host.TimelineDelayOverride = async (duration, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            host.AdvanceTimeline(duration);
            if (!reanchored && host.TotalTimelineAdvanced >= TimeSpan.FromMilliseconds(500))
            {
                reanchored = true;
                host.ReanchorTimeline(TimeSpan.Zero);
            }
        };
        var executor = new CueExecutor(host);

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        Assert.InRange(
            host.TotalTimelineAdvanced,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1_050));
    }

    [Fact]
    public async Task MediaVisualizerAndActionDispatchOnTheSameTimelineEdge()
    {
        var media = new MediaCueNode
        {
            Number = "1.1", MediaPath = "cue.wav", TimelineOffsetMs = 1_000,
        };
        var visualizer = new VisualizerCueNode
        {
            Number = "1.2", TimelineOffsetMs = 1_000, HoldMs = 10_000,
        };
        var action = new ActionCueNode { Number = "1.3", TimelineOffsetMs = 1_000 };
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.Timeline, Children = [media, visualizer, action],
        };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [group] }] };
        var host = new FakeCueHost(project);
        var executor = new CueExecutor(host);

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        var mediaTime = Assert.Single(host.TimelineStarts, item => item.Cue == media.Id).MasterTime;
        Assert.Equal(mediaTime, Assert.Single(host.ControlStarts, item => item.Cue == visualizer.Id).MasterTime);
        Assert.Equal(mediaTime, Assert.Single(host.ControlStarts, item => item.Cue == action.Id).MasterTime);
    }

    [Fact]
    public async Task CancellingTimelineRemovesFutureEdges()
    {
        var cue = new MediaCueNode
        {
            Number = "1.1", MediaPath = "cue.wav", TimelineOffsetMs = 1_000,
        };
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.Timeline, Children = [cue],
        };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [group] }] };
        var host = new FakeCueHost(project);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.TimelineDelayOverride = async (_, cancellationToken) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var executor = new CueExecutor(host);

        await executor.FireTimelineAsync(group, TimeSpan.Zero);
        var completion = executor.WaitForTimelineCompletionAsync(group.Id);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await executor.CancelTimelineRunsAsync();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(host.TimelineStarts);
    }
}
