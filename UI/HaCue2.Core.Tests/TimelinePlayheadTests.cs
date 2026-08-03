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

        // Every child scheduled at its own offset, nothing fired early, nothing seeked.
        Assert.Equal(
            [(bed.Id, TimeSpan.Zero), (stab.Id, TimeSpan.FromSeconds(30)), (late.Id, TimeSpan.FromMinutes(2))],
            host.Scheduled.Select(entry => (entry.Cue, entry.When)));

        Assert.Empty(host.Played);
        Assert.Empty(host.Seeks);
    }

    [Fact]
    public async Task ACueAfterThePlayheadIsScheduledRelativeToIt()
    {
        var (executor, host, group, _, _, late) = Scene();

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));

        // 2:00 in the scene is one minute after a playhead at 1:00. Scheduling it at its raw offset
        // would put it a minute late.
        var scheduled = Assert.Single(host.Scheduled, entry => entry.Cue == late.Id);
        Assert.Equal(TimeSpan.FromSeconds(60), scheduled.When);
    }

    [Fact]
    public async Task ACueThatHasAlreadyFinishedIsNotFiredAtAll()
    {
        var (executor, host, group, _, stab, _) = Scene();

        // The stab runs 0:30–0:32. At 1:00 it is over, and firing it would put a sound in the room
        // that the show does not contain at that moment.
        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));

        Assert.DoesNotContain(stab.Id, host.Played);
        Assert.DoesNotContain(host.Scheduled, entry => entry.Cue == stab.Id);
    }

    [Fact]
    public async Task ACueStraddlingThePlayheadStartsPartWayThroughItsFile()
    {
        var (executor, host, group, bed, _, _) = Scene();

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));

        // Fired NOW, not scheduled: it is already running at this moment in the show.
        Assert.Contains(bed.Id, host.Played);
        Assert.DoesNotContain(host.Scheduled, entry => entry.Cue == bed.Id);

        var seek = Assert.Single(host.Seeks, entry => entry.Cue == bed.Id);
        Assert.Equal(TimeSpan.FromSeconds(60), seek.Position);
    }

    [Fact]
    public async Task TheSeekAddsTheClipsOwnInPointBecauseItIsInFileTime()
    {
        var (executor, host, group, bed, _, _) = Scene();
        bed.TrimInMs = 10_000;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));

        // A cue trimmed to start ten seconds in, rehearsed from a minute into the timeline, plays from
        // 1:10 of the FILE. Seeking to 1:00 would play ten seconds the show never contains.
        Assert.Equal(TimeSpan.FromSeconds(70), Assert.Single(host.Seeks, s => s.Cue == bed.Id).Position);
    }

    [Fact]
    public async Task ATrimmedCueEndsWhereItsTrimSaysRatherThanWhereTheFileDoes()
    {
        var (executor, host, group, bed, _, _) = Scene();

        // Three minutes of file, trimmed to the first forty seconds. At 1:00 it is over.
        bed.TrimOutMs = 40_000;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));

        Assert.DoesNotContain(bed.Id, host.Played);
    }

    [Fact]
    public async Task ACueNobodyHasProbedIsPlayedRatherThanSilentlySkipped()
    {
        var (executor, host, group, bed, _, _) = Scene();
        host.Lengths.Remove(bed.Id);

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));

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

        Assert.Empty(host.Played);
        Assert.Empty(host.Scheduled);
    }

    [Fact]
    public async Task AClipThatWillNotOpenIsNotThenSeeked()
    {
        var (executor, host, group, _, _, _) = Scene();
        host.PlayFails = true;

        await executor.FireTimelineAsync(group, TimeSpan.FromSeconds(60));

        // Seeking a voice that never started is asking the session to move something that is not there.
        Assert.Empty(host.Seeks);
    }
}
