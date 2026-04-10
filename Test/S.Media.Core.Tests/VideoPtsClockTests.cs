using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoPtsClockTests
{
    [Fact]
    public void UpdateFromFrame_DuplicatePts_DoesNotPinClockPosition()
    {
        using var clock = new VideoPtsClock();
        clock.Start();

        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 25; i++)
        {
            Thread.Sleep(2);
            clock.UpdateFromFrame(TimeSpan.FromMilliseconds(100));
        }

        var pos = clock.Position;
        Assert.True(pos > TimeSpan.FromMilliseconds(125), $"Expected clock to progress past 125ms, got {pos}.");
    }

    [Fact]
    public void UpdateFromFrame_LatePts_DoesNotPullClockBackwards()
    {
        using var clock = new VideoPtsClock(sampleRate: 60);
        clock.Start();

        Thread.Sleep(80);
        var before = clock.Position;

        // Feed a clearly late PTS relative to elapsed wall time.
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(10));
        Thread.Sleep(20);

        var after = clock.Position;
        Assert.True(after >= before, $"Clock regressed after late PTS update. before={before}, after={after}");
        Assert.True(after > TimeSpan.FromMilliseconds(90), $"Expected wall-time progression >90ms, got {after}.");
    }
}
