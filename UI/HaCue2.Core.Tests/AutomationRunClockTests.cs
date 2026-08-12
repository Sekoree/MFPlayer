using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class AutomationRunClockTests
{
    [Fact]
    public void PauseSeekAndResumeShareOneAuthoredCoordinate()
    {
        var host = new FakeCueHost(new HaCueProject());
        var clock = new AutomationRunClock(host, TimeSpan.FromSeconds(2));

        host.AdvanceTimeline(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Position);

        host.TimelinePaused = true;
        host.AdvanceTimeline(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Position);

        var sought = clock.Seek(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), sought.Position);
        Assert.Equal(1, sought.Generation);

        host.TimelinePaused = false;
        host.AdvanceTimeline(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(12), clock.Position);
    }

    [Fact]
    public void SeekingBeforeZeroClampsAndAdvancesGeneration()
    {
        var clock = new AutomationRunClock(
            new FakeCueHost(new HaCueProject()), TimeSpan.FromSeconds(1));

        var first = clock.Seek(TimeSpan.FromSeconds(-5));
        var second = clock.Seek(TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.Zero, first.Position);
        Assert.Equal(1, first.Generation);
        Assert.Equal(TimeSpan.FromSeconds(4), second.Position);
        Assert.Equal(2, second.Generation);
    }
}
