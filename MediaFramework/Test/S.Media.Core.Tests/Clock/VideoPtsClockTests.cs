using Xunit;

namespace S.Media.Core.Tests.Clock;

public class VideoPtsClockTests
{
    [Fact]
    public void BeginSession_ZeroPts_ElapsedTracksWallAndPts()
    {
        var c = new VideoPtsClock();
        c.BeginSession(TimeSpan.Zero);
        Thread.Sleep(15);
        c.NotifyFramePts(TimeSpan.FromMilliseconds(40));
        Thread.Sleep(10);

        var e = c.ElapsedSinceStart;
        Assert.InRange(e.TotalMilliseconds, 30, 200);
        Assert.True(c.IsAdvancing);
    }

    [Fact]
    public void Pause_FreezesElapsed_ResumeContinuesFromAnchor()
    {
        var c = new VideoPtsClock();
        c.BeginSession(TimeSpan.Zero);
        c.NotifyFramePts(TimeSpan.FromSeconds(1));
        Thread.Sleep(20);
        var beforeMs = c.ElapsedSinceStart.TotalMilliseconds;
        c.Pause();
        Thread.Sleep(40);
        // Wall vs PTS merge can move slightly on some schedulers; require "mostly frozen" not sample-accurate.
        Assert.True(Math.Abs(c.ElapsedSinceStart.TotalMilliseconds - beforeMs) < 30.0);
        Assert.False(c.IsAdvancing);

        c.Resume();
        Assert.True(c.IsAdvancing);
        var mid = c.ElapsedSinceStart;
        Thread.Sleep(15);
        Assert.True(c.ElapsedSinceStart > mid);
    }

    [Fact]
    public void Seek_RepositionsElapsed()
    {
        var c = new VideoPtsClock();
        c.BeginSession(TimeSpan.Zero);
        c.NotifyFramePts(TimeSpan.FromSeconds(2));
        c.Seek(TimeSpan.FromSeconds(10));
        var e = c.ElapsedSinceStart;
        Assert.InRange(e.TotalMilliseconds, 9970, 10_030);
    }

    [Fact]
    public void AsMediaClockMaster_PinsPositionForward_ButNeverRewindsOnALatePts()
    {
        // D3 contract for video-only PTS mastering: a presented frame AHEAD of the interpolated position
        // pulls the playhead onto its PTS, while a frame BEHIND it is absorbed by MediaClock's monotonic
        // epoch fold (position holds and keeps advancing) instead of rewinding the transport.
        var pts = new VideoPtsClock();
        pts.BeginSession(TimeSpan.Zero);
        using var clock = new MediaClock();
        clock.SetMaster(pts);
        clock.Start();

        pts.NotifyFramePts(TimeSpan.FromSeconds(2));
        var pinned = clock.CurrentPosition;
        Assert.InRange(pinned.TotalSeconds, 1.9, 2.5);

        pts.NotifyFramePts(TimeSpan.FromSeconds(1));
        var afterLatePts = clock.CurrentPosition;
        Assert.True(afterLatePts >= pinned,
            $"a late PTS rewound the playhead ({pinned} → {afterLatePts}); MediaClock must fold instead.");
        Assert.InRange((afterLatePts - pinned).TotalMilliseconds, 0, 500);
    }

    [Fact]
    public void AsMediaClockMaster_SourceStall_KeepsThePlayheadMoving()
    {
        // The stall guarantee behind shipping the wiring at all: with no further frames the master keeps
        // interpolating on wall time, so a stalled or sparse-PTS video-only clip cannot freeze the transport.
        var pts = new VideoPtsClock();
        pts.BeginSession(TimeSpan.Zero);
        using var clock = new MediaClock();
        clock.SetMaster(pts);
        clock.Start();

        pts.NotifyFramePts(TimeSpan.FromMilliseconds(40));
        var before = clock.CurrentPosition;
        Thread.Sleep(30);
        Assert.True(clock.CurrentPosition > before,
            "the playhead stopped advancing while the video source delivered no frames.");
    }
}
