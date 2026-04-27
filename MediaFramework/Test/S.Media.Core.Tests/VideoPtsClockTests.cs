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
        using var clock = new VideoPtsClock(frameRate: 60);
        clock.Start();


        // Anchor the clock with an initial frame (simulates the first presented frame).
        clock.UpdateFromFrame(TimeSpan.Zero);

        Thread.Sleep(80);
        var before = clock.Position;

        // Feed a clearly late PTS relative to elapsed wall time.
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(10));
        Thread.Sleep(20);

        var after = clock.Position;
        Assert.True(after >= before, $"Clock regressed after late PTS update. before={before}, after={after}");
        Assert.True(after > TimeSpan.FromMilliseconds(90), $"Expected wall-time progression >90ms, got {after}.");
    }

    [Fact]
    public void Position_IsMonotonicElapsed_UntilFirstFrameAnchors()
    {
        using var clock = new VideoPtsClock(frameRate: 30);
        clock.Start();

        Thread.Sleep(50);
        var before = clock.Position;
        // Not anchored: advances with Stopwatch so the clock is usable as a master before first present.
        Assert.True(before >= TimeSpan.FromMilliseconds(40), $"Expected ~elapsed wall time before first frame, got {before}.");

        // First frame anchors the stream PTS timeline.
        clock.UpdateFromFrame(TimeSpan.Zero);
        Thread.Sleep(50);
        var after = clock.Position;
        Assert.True(after > TimeSpan.FromMilliseconds(30), $"Expected clock to progress after anchor, got {after}.");
    }

    [Fact]
    public void ApplySelfSlew_Disabled_IgnoresSubSeekDrift()
    {
        // Default (router-corrected) mode: small forward/backward drift must not
        // re-anchor — chasing it would form a positive feedback loop with the
        // upstream PtsDriftTracker. See Doc/Clock-And-AV-Drift-Analysis.md §5.1.
        using var clock = new VideoPtsClock { ApplySelfSlew = false };
        clock.Start();
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(100));

        Thread.Sleep(50);

        // Wall says ~150 ms; feed a PTS at 200 ms (50 ms ahead). Should be ignored.
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(200));
        Thread.Sleep(2);

        // Position should still track ~150 ms, NOT jump to 200 ms.
        var pos = clock.Position;
        Assert.True(pos < TimeSpan.FromMilliseconds(180),
            $"ApplySelfSlew=false must not re-anchor on sub-seek drift; got {pos}.");
    }

    [Fact]
    public void ApplySelfSlew_Enabled_DriftsTowardIncomingPts()
    {
        // Self-slew mode: clock must converge toward the incoming PTS at a bounded
        // rate, never chase instantly. With SelfSlewMaxMsPerSec = 50 and 100 ms
        // wall between updates, max correction per update is ~5 ms.
        using var clock = new VideoPtsClock
        {
            ApplySelfSlew = true,
            SelfSlewMaxMsPerSec = 50  // exaggerated for test speed
        };
        clock.Start();
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(100));

        Thread.Sleep(100);

        // Wall says ~200 ms; feed a PTS at 300 ms (100 ms ahead). Slew should
        // pull the clock forward by at most ~5 ms (50 ms/s × 0.1 s).
        var beforeUpdate = clock.Position;
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(300));
        var afterUpdate = clock.Position;

        var advance = afterUpdate - beforeUpdate;
        Assert.True(advance >= TimeSpan.Zero,
            $"Slew must not push the clock backward; before={beforeUpdate} after={afterUpdate}.");
        Assert.True(advance <= TimeSpan.FromMilliseconds(15),
            $"Slew must clamp single-step adjustment to ~5 ms (≤15 ms tolerance); advance={advance}.");
    }

    [Fact]
    public void ApplySelfSlew_LargeJumpStillReanchors()
    {
        // Large jumps are seeks regardless of mode — the SeekThreshold path is
        // independent of ApplySelfSlew.
        using var clock = new VideoPtsClock { ApplySelfSlew = true };
        clock.Start();
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(100));
        Thread.Sleep(20);

        // Jump 5 s forward — well above SeekThreshold (500 ms).
        clock.UpdateFromFrame(TimeSpan.FromMilliseconds(5_100));
        Thread.Sleep(2);

        var pos = clock.Position;
        Assert.True(pos >= TimeSpan.FromMilliseconds(5_080),
            $"Large jump must re-anchor immediately even in slew mode; got {pos}.");
    }
}
