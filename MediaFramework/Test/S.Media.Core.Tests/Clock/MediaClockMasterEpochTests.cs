using Xunit;

namespace S.Media.Core.Tests.Clock;

/// <summary>
/// Plan D: MediaClock's master-epoch mechanism. A master <see cref="IPlaybackClock"/> is monotonic within
/// one epoch and REPORTS its own boundaries via <see cref="ClockReading.EpochId"/>; an output Flush
/// (seek/pause/natural EOF), a device restart or a composite handoff starts a new one. MediaClock compares
/// ids - on a new id it folds the old epoch's accrued time into the base and re-anchors (position stays
/// continuous and immediately counts the new segment); within one id it holds at the epoch high-water,
/// which is what makes a transient dip cost nothing. It no longer INFERS a boundary from a regression:
/// that inference double-counted the recovery of any master that dipped and came back.
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

        // The flush lands AFTER the clock-seek: stream stopped, new epoch, segment clock rewound to zero.
        master.Reanchor();
        Assert.Equal(target, clock.CurrentPosition); // held at the seek target, not frozen forever

        // The stream restarts and the new segment starts playing. The 50 ms it has played are real audible
        // progress in the new epoch, and the id told us where that epoch began, so they are counted.
        master.ElapsedSinceStart = TimeSpan.FromMilliseconds(50);
        master.IsAdvancing = true;
        Assert.Equal(target + TimeSpan.FromMilliseconds(50), clock.CurrentPosition);

        master.ElapsedSinceStart = TimeSpan.FromMilliseconds(550);
        Assert.Equal(target + TimeSpan.FromMilliseconds(550), clock.CurrentPosition);
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

        // Natural EOF: run loop flushes the output; stream stops; new epoch, segment clock at zero.
        master.Reanchor();

        Assert.Equal(TimeSpan.FromSeconds(5), clock.CurrentPosition); // held at EOF, not 0:00
        Assert.Equal(TimeSpan.FromSeconds(5), clock.CurrentPosition); // stable across repeated reads
    }

    [Fact]
    public void MasterDipToZeroAndReturn_HoldsThenResumesWithoutJump()
    {
        // A master reports a neutral zero while idle and then the SAME epoch resumes (no id change, so no
        // re-anchor was announced). Holding is what keeps the return jump-free; folding would have made the
        // resumed reading look like 10.5 s of fresh progress.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.5);
        Assert.Equal(TimeSpan.FromSeconds(0.5), clock.CurrentPosition);

        master.IsAdvancing = false;
        master.ElapsedSinceStart = TimeSpan.Zero; // neutral idle report
        Assert.Equal(TimeSpan.FromSeconds(0.5), clock.CurrentPosition); // held

        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.5); // same epoch resumes
        master.IsAdvancing = true;
        Assert.Equal(TimeSpan.FromSeconds(0.5), clock.CurrentPosition); // no +10.5s jump

        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.6);
        Assert.Equal(TimeSpan.FromSeconds(0.6), clock.CurrentPosition);
    }

    [Fact]
    public void MasterReportsNewEpoch_FoldsContinuouslyIntoIt()
    {
        // A composite master hands off to a lower (but advancing) leaf clock - the composite takes a new
        // epoch id for the handoff. Position must stay continuous and keep advancing at the new leaf's rate.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(12);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);

        master.Reanchor(TimeSpan.FromSeconds(3), advancing: true); // handoff to a leaf sitting at 3 s
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition); // continuous fold

        master.ElapsedSinceStart = TimeSpan.FromSeconds(3.5);
        Assert.Equal(TimeSpan.FromSeconds(2.5), clock.CurrentPosition);
    }

    [Fact]
    public void MasterDipsAndRecoversWithinOneEpoch_DoesNotDoubleCountTheRecovery()
    {
        // Plan D's unresolved symptom, pinned. The retired "advancing regression ⇒ new epoch" inference
        // folded the pre-dip accrual into the base AND re-anchored at the dip floor, so the recovery back
        // to a reading the master had already reported was counted a SECOND time (this read 3 s).
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(12);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);

        // The dip: same epoch id (no re-anchor was announced), master briefly reports less than it had.
        master.ElapsedSinceStart = TimeSpan.FromSeconds(11);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition); // held, not folded

        // The recovery: the master returns to where it was. It has still only ever played 2 s.
        master.ElapsedSinceStart = TimeSpan.FromSeconds(12);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);

        master.ElapsedSinceStart = TimeSpan.FromSeconds(12.5);
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

        // The flush lands while paused (new master epoch): Start must fold no drift across it, and the
        // new epoch anchors cleanly.
        master.Reanchor();
        clock.Start();
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);

        master.IsAdvancing = true;
        master.ElapsedSinceStart = TimeSpan.FromSeconds(0.5);
        Assert.Equal(TimeSpan.FromSeconds(2.5), clock.CurrentPosition);
    }

    [Fact]
    public void PauseThenMasterRegressesWithinOneEpoch_FoldsNothing()
    {
        // A master that breaks its per-epoch monotonic contract while we are paused must not drag the
        // playhead backwards on resume.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(1), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);
        clock.Pause();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(1); // same epoch, illegal regression
        clock.Start();
        Assert.Equal(TimeSpan.FromSeconds(2), clock.CurrentPosition);
    }

    [Fact]
    public void PositionEpoch_ChangesOnExplicitReanchorsOnly()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        var fresh = clock.PositionEpoch;

        clock.SetMaster(master);
        var afterSetMaster = clock.PositionEpoch;
        Assert.NotEqual(fresh, afterSetMaster);

        clock.Start();
        Assert.Equal(afterSetMaster, clock.PositionEpoch); // Start re-anchors for continuity, no reposition

        clock.Seek(TimeSpan.FromSeconds(7));
        var afterSeek = clock.PositionEpoch;
        Assert.NotEqual(afterSetMaster, afterSeek);

        // Folding a MASTER epoch change is position-continuous - it must NOT look like a reposition to
        // epoch-scoped consumers (e.g. MediaPlayer's natural-EOF Duration clamp).
        master.ElapsedSinceStart = TimeSpan.FromSeconds(12);
        _ = clock.CurrentPosition;
        master.Reanchor(TimeSpan.FromSeconds(1), advancing: true);
        _ = clock.CurrentPosition;
        Assert.Equal(afterSeek, clock.PositionEpoch);

        clock.Pause();
        Assert.Equal(afterSeek, clock.PositionEpoch);

        clock.Reset();
        Assert.NotEqual(afterSeek, clock.PositionEpoch);
    }

    [Fact]
    public void ReadPosition_ReportsTheEpochThePositionWasComputedIn()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(4), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();
        master.ElapsedSinceStart = TimeSpan.FromSeconds(5);

        var before = clock.ReadPosition();
        Assert.Equal(clock.PositionEpoch, before.EpochId);
        Assert.Equal(TimeSpan.FromSeconds(1), before.Elapsed);
        Assert.True(before.IsAdvancing);

        clock.Seek(TimeSpan.FromSeconds(30));
        var after = clock.ReadPosition();
        Assert.NotEqual(before.EpochId, after.EpochId);
        Assert.Equal(TimeSpan.FromSeconds(30), after.Elapsed);
    }

    /// <summary>A master under test control. <see cref="Read"/> is atomic (the interface requires it of any
    /// clock that ever re-anchors) so the fake cannot hand out a torn pair the production code would then
    /// be judged on.</summary>
    private sealed class FakeClock : IPlaybackClock
    {
        private readonly Lock _gate = new();
        private TimeSpan _elapsed;
        private bool _advancing = true;
        private long _epochId = PlaybackEpoch.Next();

        public TimeSpan ElapsedSinceStart
        {
            get { lock (_gate) return _elapsed; }
            set { lock (_gate) _elapsed = value; }
        }

        public bool IsAdvancing
        {
            get { lock (_gate) return _advancing; }
            set { lock (_gate) _advancing = value; }
        }

        public long EpochId
        {
            get { lock (_gate) return _epochId; }
        }

        public ClockReading Read()
        {
            lock (_gate) return new ClockReading(_epochId, _elapsed, _advancing);
        }

        /// <summary>What a real output does on Flush/Start/device restart: a new epoch whose elapsed
        /// restarts (idle by default, as the stream is stopped until the next producer call).</summary>
        public void Reanchor(TimeSpan elapsed = default, bool advancing = false)
        {
            lock (_gate)
            {
                _epochId = PlaybackEpoch.Next();
                _elapsed = elapsed;
                _advancing = advancing;
            }
        }
    }
}
