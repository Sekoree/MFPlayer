using Xunit;

namespace S.Media.Core.Tests.Clock;

/// <summary>
/// Review §2.14: MediaClock's master-epoch mechanism. A master <see cref="IPlaybackClock"/> is monotonic
/// within one hardware segment; an output Flush (seek/pause/natural EOF) rewinds its ElapsedSinceStart to
/// zero for a new segment ("epoch"). MediaClock detects the epoch boundary as a regression below the
/// per-epoch high-water and either folds (master advancing again - new epoch live) or holds (master idle),
/// instead of the old clamp-to-zero-delta that froze the playhead at the base position.
/// </summary>
public class MediaClockMasterEpochTests
{
    [Fact]
    public void FlushLandingAfterClockSeek_HoldsSeekTarget_ThenAdvancesInNewEpoch()
    {
        // The §2.14 torn window: AvPlaybackCoordinator.Seek re-anchors the clock at the master's
        // pre-flush elapsed, then a racing flush rewinds the master. The old clamp froze the playhead
        // at the seek target until the device clock re-passed the stale anchor (potentially minutes).
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(42), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();
        master.ElapsedSinceStart = TimeSpan.FromSeconds(42.2);
        Assert.Equal(TimeSpan.FromSeconds(0.2), clock.CurrentPosition);

        var target = TimeSpan.FromSeconds(10);
        clock.Seek(target); // anchors at the stale, pre-flush 42.2s

        // The flush lands AFTER the clock-seek: stream stopped, segment clock rewound to zero.
        master.IsAdvancing = false;
        master.ElapsedSinceStart = TimeSpan.Zero;
        Assert.Equal(target, clock.CurrentPosition); // held at the seek target, not frozen forever

        // The stream restarts and the new segment starts playing.
        master.ElapsedSinceStart = TimeSpan.FromMilliseconds(50);
        master.IsAdvancing = true;
        Assert.Equal(target, clock.CurrentPosition); // fold is continuous - still the seek target

        master.ElapsedSinceStart = TimeSpan.FromMilliseconds(550);
        Assert.Equal(target + TimeSpan.FromMilliseconds(500), clock.CurrentPosition);
    }

    [Fact]
    public void NaturalEofFlush_HoldsLastPlayedPosition_NotZero()
    {
        // The "deck shows playing, stuck at the beginning" class: the natural-EOF output flush rewinds
        // the master's segment clock to ~the anchor value, so base + clamp(elapsed - anchor) read ~0:00.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.Zero, IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.CurrentPosition); // raises the epoch high-water

        // Natural EOF: run loop flushes the output; stream stops; segment clock rewinds to zero.
        master.IsAdvancing = false;
        master.ElapsedSinceStart = TimeSpan.Zero;

        Assert.Equal(TimeSpan.FromSeconds(5), clock.CurrentPosition); // held at EOF, not 0:00
        Assert.Equal(TimeSpan.FromSeconds(5), clock.CurrentPosition); // stable across repeated reads
    }

    [Fact]
    public void MasterDipToZeroAndReturn_HoldsThenResumesWithoutJump()
    {
        // A composite master reports its neutral zero while no candidate advances, then the SAME
        // segment resumes. Holding (not folding) while idle means the return is jump-free; the old
        // clamp snapped the position to the base during the dip.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.5);
        Assert.Equal(TimeSpan.FromSeconds(0.5), clock.CurrentPosition);

        master.IsAdvancing = false;
        master.ElapsedSinceStart = TimeSpan.Zero; // neutral idle report
        Assert.Equal(TimeSpan.FromSeconds(0.5), clock.CurrentPosition); // held

        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.5); // same segment resumes
        master.IsAdvancing = true;
        Assert.Equal(TimeSpan.FromSeconds(0.5), clock.CurrentPosition); // no +10.5s jump

        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.6);
        Assert.Equal(TimeSpan.FromSeconds(0.6), clock.CurrentPosition);
    }

    [Fact]
    public void AdvancingRegression_FoldsContinuouslyIntoNewEpoch()
    {
        // A composite master hands off to a lower (but advancing) leaf clock: position must stay
        // continuous and keep advancing at the new leaf's rate - the documented "re-anchor on handoff"
        // now happens automatically at the epoch boundary.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(12);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);

        master.ElapsedSinceStart = TimeSpan.FromSeconds(3); // handoff, still advancing
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition); // continuous fold

        master.ElapsedSinceStart = TimeSpan.FromSeconds(3.5);
        Assert.Equal(TimeSpan.FromSeconds(2.5), clock.CurrentPosition);
    }

    [Fact]
    public void PauseAcrossMasterEpochReset_ResumesFromPausedPosition()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(1), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);
        clock.Pause();
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);

        // The flush lands while paused (master elapsed regresses below the pause snapshot):
        // Start must not fold negative drift, and the new epoch anchors cleanly.
        master.IsAdvancing = false;
        master.ElapsedSinceStart = TimeSpan.Zero;
        clock.Start();
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);

        master.IsAdvancing = true;
        master.ElapsedSinceStart = TimeSpan.FromSeconds(0.5);
        Assert.Equal(TimeSpan.FromSeconds(2.5), clock.CurrentPosition);
    }

    [Fact]
    public void TimebaseGeneration_BumpsOnExplicitReanchorsOnly()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        Assert.Equal(0, clock.TimebaseGeneration);

        clock.SetMaster(master);
        Assert.Equal(1, clock.TimebaseGeneration);

        clock.Start();
        Assert.Equal(1, clock.TimebaseGeneration); // Start re-anchors for continuity, no reposition

        clock.Seek(TimeSpan.FromSeconds(7));
        Assert.Equal(2, clock.TimebaseGeneration);

        // A master epoch fold is position-continuous - it must NOT look like a reposition to
        // generation-scoped consumers (e.g. MediaPlayer's natural-EOF Duration clamp).
        master.ElapsedSinceStart = TimeSpan.FromSeconds(12);
        _ = clock.CurrentPosition;
        master.ElapsedSinceStart = TimeSpan.FromSeconds(1); // regression while advancing → fold
        _ = clock.CurrentPosition;
        Assert.Equal(2, clock.TimebaseGeneration);

        clock.Pause();
        Assert.Equal(2, clock.TimebaseGeneration);

        clock.Reset();
        Assert.Equal(3, clock.TimebaseGeneration);
    }

    private sealed class FakeClock : IPlaybackClock
    {
        private long _elapsedTicks;
        private volatile bool _isAdvancing = true;

        public TimeSpan ElapsedSinceStart
        {
            get => TimeSpan.FromTicks(Volatile.Read(ref _elapsedTicks));
            set => Volatile.Write(ref _elapsedTicks, value.Ticks);
        }

        public bool IsAdvancing
        {
            get => _isAdvancing;
            set => _isAdvancing = value;
        }
    }
}
