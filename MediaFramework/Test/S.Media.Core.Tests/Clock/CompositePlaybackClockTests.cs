using System.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Clock;

public class CompositePlaybackClockTests
{
    [Fact]
    public void PicksHigherPriorityAdvancingClock()
    {
        var low = new StubPlaybackClock(false, TimeSpan.FromSeconds(1));
        var high = new StubPlaybackClock(true, TimeSpan.FromSeconds(5));
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(5), c.ElapsedSinceStart);
    }

    [Fact]
    public void WhenBothAdvancing_UsesHigherPriorityElapsed_NotBlended()
    {
        var low = new StubPlaybackClock(true, TimeSpan.FromSeconds(1));
        var high = new StubPlaybackClock(true, TimeSpan.FromSeconds(99));
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(99), c.ElapsedSinceStart);
    }

    [Fact]
    public void RepeatedElapsedReads_whileAdvancing_remain_consistent()
    {
        var a = new StubPlaybackClock(true, TimeSpan.FromSeconds(3.5));
        var c = new CompositePlaybackClock(new PlaybackClockCandidate(a, 1));
        for (var i = 0; i < 5000; i++)
        {
            Assert.True(c.IsAdvancing);
            Assert.Equal(TimeSpan.FromSeconds(3.5), c.ElapsedSinceStart);
        }
    }

    [Fact]
    public void WhenNoneAdvancing_ElapsedIsZero()
    {
        var a = new StubPlaybackClock(false, TimeSpan.FromSeconds(3));
        var b = new StubPlaybackClock(false, TimeSpan.FromSeconds(4));
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(a, 5),
            new PlaybackClockCandidate(b, 1));

        Assert.False(c.IsAdvancing);
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
    }

    [Fact]
    public void RepeatedElapsedReads_idleToSingleAdvancing_switchesFromZeroToActiveElapsed()
    {
        var a = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(2) };
        var b = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(7) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(a, 10),
            new PlaybackClockCandidate(b, 1));

        for (var i = 0; i < 3000; i++)
        {
            if ((i & 1) == 0)
            {
                a.IsAdvancing = false;
                b.IsAdvancing = false;
                Assert.False(c.IsAdvancing);
                Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
            }
            else
            {
                b.IsAdvancing = true;
                b.ElapsedSinceStart = TimeSpan.FromSeconds(7 + i * 0.0001);
                Assert.True(c.IsAdvancing);
                Assert.Equal(b.ElapsedSinceStart, c.ElapsedSinceStart);
            }
        }
    }

    [Fact]
    public void WhenThreeAdvancing_UsesHighestPriorityElapsed_thenFallsThroughWhenHigherStops()
    {
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var mid = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(2) };
        var high = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(mid, 5),
            new PlaybackClockCandidate(high, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(3), c.ElapsedSinceStart);

        high.IsAdvancing = false;
        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(2), c.ElapsedSinceStart);

        mid.IsAdvancing = false;
        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(1), c.ElapsedSinceStart);

        low.IsAdvancing = false;
        Assert.False(c.IsAdvancing);
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
    }

    [Fact]
    public void WhenSamePriority_bothAdvancing_firstRegisteredWinsTies()
    {
        var first = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(7) };
        var second = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(8) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(first, 10),
            new PlaybackClockCandidate(second, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(7), c.ElapsedSinceStart);
    }

    [Fact]
    public void EqualPriority_fourCandidates_seeded_subsetAdvancing_firstRegisteredWinsAmongAdvancing()
    {
        var a = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var b = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var c = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var d = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var comp = new CompositePlaybackClock(
            new PlaybackClockCandidate(a, 5),
            new PlaybackClockCandidate(b, 5),
            new PlaybackClockCandidate(c, 5),
            new PlaybackClockCandidate(d, 5));

        var rnd = new Random(314_159);
        for (var i = 0; i < 6000; i++)
        {
            a.IsAdvancing = rnd.Next(2) == 0;
            b.IsAdvancing = rnd.Next(2) == 0;
            c.IsAdvancing = rnd.Next(2) == 0;
            d.IsAdvancing = rnd.Next(2) == 0;
            // Each leaf ramps: a leaf is monotonic within its own epoch by contract, and the composite now
            // ENFORCES that on what it emits (a same-epoch regression is held at the high-water), so jittering
            // a leaf up and down here would be testing the composite against a broken leaf rather than
            // against tie-break selection, which is what this case is about.
            a.ElapsedSinceStart = TimeSpan.FromTicks(100 + i);
            b.ElapsedSinceStart = TimeSpan.FromTicks(200 + i);
            c.ElapsedSinceStart = TimeSpan.FromTicks(300 + i);
            d.ElapsedSinceStart = TimeSpan.FromTicks(400 + i);

            var any = a.IsAdvancing || b.IsAdvancing || c.IsAdvancing || d.IsAdvancing;
            Assert.Equal(any, comp.IsAdvancing);
            if (!any)
            {
                Assert.Equal(TimeSpan.Zero, comp.ElapsedSinceStart);
                continue;
            }

            MutableStubPlaybackClock winner;
            if (a.IsAdvancing) winner = a;
            else if (b.IsAdvancing) winner = b;
            else if (c.IsAdvancing) winner = c;
            else winner = d;

            Assert.Equal(winner.ElapsedSinceStart, comp.ElapsedSinceStart);
        }
    }

    [Fact]
    public void RepeatedElapsedReads_threeWayRotatingAdvancing_followsStrictPriority()
    {
        var low = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var mid = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(2) };
        var high = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(mid, 5),
            new PlaybackClockCandidate(high, 10));

        for (var i = 0; i < 3000; i++)
        {
            var phase = i % 4;
            low.IsAdvancing = phase == 1;
            mid.IsAdvancing = phase == 2;
            high.IsAdvancing = phase == 3;
            low.ElapsedSinceStart = TimeSpan.FromSeconds(10 + i * 0.0001);
            mid.ElapsedSinceStart = TimeSpan.FromSeconds(20 + i * 0.0001);
            high.ElapsedSinceStart = TimeSpan.FromSeconds(30 + i * 0.0001);

            if (phase == 0)
            {
                Assert.False(c.IsAdvancing);
                Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
            }
            else if (phase == 1)
            {
                Assert.True(c.IsAdvancing);
                Assert.Equal(low.ElapsedSinceStart, c.ElapsedSinceStart);
            }
            else if (phase == 2)
            {
                Assert.True(c.IsAdvancing);
                Assert.Equal(mid.ElapsedSinceStart, c.ElapsedSinceStart);
            }
            else
            {
                Assert.True(c.IsAdvancing);
                Assert.Equal(high.ElapsedSinceStart, c.ElapsedSinceStart);
            }
        }
    }

    [Fact]
    public void RepeatedElapsedReads_underAlternatingAdvancingCandidates_followsHigherPriority()
    {
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var high = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(10) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 100));

        for (var i = 0; i < 4000; i++)
        {
            if ((i & 1) == 0)
            {
                low.IsAdvancing = false;
                high.IsAdvancing = true;
                high.ElapsedSinceStart = TimeSpan.FromSeconds(10 + i * 0.0001);
            }
            else
            {
                high.IsAdvancing = false;
                low.IsAdvancing = true;
                low.ElapsedSinceStart = TimeSpan.FromSeconds(1 + i * 0.0001);
            }

            Assert.True(c.IsAdvancing);
            var e = c.ElapsedSinceStart;
            if ((i & 1) == 0)
                Assert.Equal(high.ElapsedSinceStart, e);
            else
                Assert.Equal(low.ElapsedSinceStart, e);
        }
    }

    // The blend cases below drive the clock through Read(): it is the authority, and the only member that
    // advances the cross-fade / co-advance filters (see CompositePlaybackClock.Read). ElapsedSinceStart is a
    // projection of the same snapshot, so polling it would observe the blend without ever moving it.

    [Fact]
    public void HandoffCrossFade_zero_duration_matches_snap_for_two_advancing()
    {
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var high = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(99) };
        var snap = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 100));
        var blended = new CompositePlaybackClock(
            new CompositePlaybackClockBlend { HandoffCrossFade = TimeSpan.Zero },
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 100));
        Assert.Equal(snap.Read().Elapsed, blended.Read().Elapsed);
    }

    [Fact]
    public void HandoffCrossFade_lerps_when_advancing_winner_promotes()
    {
        var simTicks = 0L;
        long Now() => simTicks;

        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(10) };
        var high = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(90) };
        var blend = new CompositePlaybackClockBlend { HandoffCrossFade = TimeSpan.FromSeconds(2) };
        var c = new CompositePlaybackClock(blend, Now,
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 100));

        Assert.Equal(TimeSpan.FromSeconds(10), c.Read().Elapsed);

        high.IsAdvancing = true;
        Assert.Equal(TimeSpan.FromSeconds(10), c.Read().Elapsed);

        simTicks += Stopwatch.Frequency / 2;
        var mid = c.Read().Elapsed;
        Assert.True(mid > TimeSpan.FromSeconds(10));
        Assert.True(mid < TimeSpan.FromSeconds(90));

        simTicks += Stopwatch.Frequency * 4;
        Assert.Equal(TimeSpan.FromSeconds(90), c.Read().Elapsed);
    }

    [Fact]
    public void CoAdvanceSmoothing_EMA_toward_leader_when_two_stay_advancing()
    {
        long sim = 0;
        long Now() => sim;
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(10) };
        var high = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(20) };
        var blend = new CompositePlaybackClockBlend { CoAdvanceSmoothingTau = TimeSpan.FromSeconds(1) };
        var c = new CompositePlaybackClock(blend, Now, new PlaybackClockCandidate(low, 1), new PlaybackClockCandidate(high, 100));

        Assert.Equal(TimeSpan.FromSeconds(20), c.Read().Elapsed);

        high.ElapsedSinceStart = TimeSpan.FromSeconds(100);
        sim += Stopwatch.Frequency;
        var step1 = c.Read().Elapsed;
        Assert.True(step1 > TimeSpan.FromSeconds(20));
        Assert.True(step1 < TimeSpan.FromSeconds(100));

        for (var k = 0; k < 50; k++)
        {
            sim += Stopwatch.Frequency * 2;
            _ = c.Read();
        }

        Assert.True(c.Read().Elapsed > TimeSpan.FromSeconds(99));
    }

    [Fact]
    public void CoAdvanceSmoothing_bypasses_when_only_one_candidate_advances()
    {
        long sim = 0;
        long Now() => sim;
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(5) };
        var high = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(999) };
        var blend = new CompositePlaybackClockBlend { CoAdvanceSmoothingTau = TimeSpan.FromSeconds(2) };
        var c = new CompositePlaybackClock(blend, Now, new PlaybackClockCandidate(low, 1), new PlaybackClockCandidate(high, 10));

        Assert.Equal(TimeSpan.FromSeconds(5), c.Read().Elapsed);

        low.ElapsedSinceStart = TimeSpan.FromSeconds(50);
        sim += Stopwatch.Frequency;
        Assert.Equal(TimeSpan.FromSeconds(50), c.Read().Elapsed);
    }

    [Fact]
    public void HandoffCrossFade_deferred_until_complete_then_CoAdvance_can_smooth_joint_advances()
    {
        long sim = 0;
        long Now() => sim;
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(10) };
        var high = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(200) };
        var blend = new CompositePlaybackClockBlend
        {
            HandoffCrossFade = TimeSpan.FromSeconds(2),
            CoAdvanceSmoothingTau = TimeSpan.FromSeconds(1),
        };
        var c = new CompositePlaybackClock(blend, Now, new PlaybackClockCandidate(low, 1), new PlaybackClockCandidate(high, 100));

        Assert.Equal(TimeSpan.FromSeconds(10), c.Read().Elapsed);

        high.IsAdvancing = true;
        Assert.Equal(TimeSpan.FromSeconds(10), c.Read().Elapsed);

        sim += Stopwatch.Frequency / 2;
        var midHandoff = c.Read().Elapsed;
        Assert.InRange(midHandoff.TotalSeconds, 12, 120);

        sim += Stopwatch.Frequency * 4;
        Assert.True(c.Read().Elapsed >= TimeSpan.FromSeconds(195));

        high.ElapsedSinceStart = TimeSpan.FromSeconds(220);
        sim += Stopwatch.Frequency;
        var afterJump = c.Read().Elapsed;
        Assert.True(afterJump < TimeSpan.FromSeconds(219));
        Assert.True(afterJump > TimeSpan.FromSeconds(190));
    }

    [Fact]
    public void Read_CandidateStopsBetweenLegacyPropertiesAndAtomicRead_DoesNotSelectIt()
    {
        var stoppedDuringRead = new StopsWhenSampledClock(TimeSpan.FromSeconds(50));
        var fallback = new MutableStubPlaybackClock
        {
            IsAdvancing = true,
            ElapsedSinceStart = TimeSpan.FromSeconds(10),
        };
        var clock = new CompositePlaybackClock(
            new PlaybackClockCandidate(stoppedDuringRead, 100),
            new PlaybackClockCandidate(fallback, 1));

        var reading = clock.Read();

        Assert.True(reading.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(10), reading.Elapsed);
    }

    [Fact]
    public void HandoffCrossFade_DownwardWinner_RemainsMonotonicWithinCompositeEpoch()
    {
        long sim = 0;
        var low = new MutableStubPlaybackClock
        {
            IsAdvancing = true,
            ElapsedSinceStart = TimeSpan.FromSeconds(100),
        };
        var high = new MutableStubPlaybackClock
        {
            IsAdvancing = false,
            ElapsedSinceStart = TimeSpan.Zero,
        };
        var clock = new CompositePlaybackClock(
            new CompositePlaybackClockBlend { HandoffCrossFade = TimeSpan.FromSeconds(2) },
            () => sim,
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 100));

        _ = clock.Read();
        high.IsAdvancing = true;
        var first = clock.Read();
        Assert.Equal(TimeSpan.Zero, first.Elapsed);

        high.ElapsedSinceStart = TimeSpan.FromSeconds(1);
        sim += Stopwatch.Frequency;
        var middle = clock.Read();
        sim += Stopwatch.Frequency * 2;
        high.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        var afterBlendWindow = clock.Read();

        Assert.Equal(first.EpochId, middle.EpochId);
        Assert.Equal(first.EpochId, afterBlendWindow.EpochId);
        Assert.True(middle.Elapsed >= first.Elapsed);
        Assert.True(afterBlendWindow.Elapsed >= middle.Elapsed);
    }

    [Fact]
    public void IsAdvancing_UsesTheAtomicSample_NotTheCandidatesLegacyProperty()
    {
        // S6: Read() was given the atomic sweep, IsAdvancing was left selecting on the candidates' own
        // IsAdvancing members - so the very split Read_CandidateStopsBetweenLegacyPropertiesAndAtomicRead
        // was written to close survived on the sibling member: this composite reported advancing=true while
        // Read() reported advancing=false about the same instant.
        var stoppedDuringRead = new StopsWhenSampledClock(TimeSpan.FromSeconds(50));
        var clock = new CompositePlaybackClock(new PlaybackClockCandidate(stoppedDuringRead, 100));

        Assert.False(clock.Read().IsAdvancing);
        Assert.False(clock.IsAdvancing);
    }

    [Fact]
    public void LeafRegressesWithinItsOwnEpoch_CompositeHoldsAtItsEpochHighWater()
    {
        // S6: IPlaybackClock makes per-epoch monotonicity an implementation OBLIGATION, and the composite
        // owes it to its own consumers. Forwarding the winner's elapsed verbatim turned a leaf's contract
        // violation into a same-epoch regression of the composite - which a mastered MediaClock is required
        // to hold through rather than fold, so the error propagated as a stall instead of being contained.
        var leaf = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(12) };
        var clock = new CompositePlaybackClock(new PlaybackClockCandidate(leaf, 1));

        var first = clock.Read();
        Assert.Equal(TimeSpan.FromSeconds(12), first.Elapsed);

        leaf.ElapsedSinceStart = TimeSpan.FromSeconds(11); // same (leaf) epoch, illegal regression
        var dipped = clock.Read();
        Assert.Equal(first.EpochId, dipped.EpochId);
        Assert.Equal(TimeSpan.FromSeconds(12), dipped.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(12), clock.ElapsedSinceStart); // and the member agrees

        leaf.ElapsedSinceStart = TimeSpan.FromSeconds(13); // caught up: released, not pinned
        Assert.Equal(TimeSpan.FromSeconds(13), clock.Read().Elapsed);
    }

    [Fact]
    public void HandoffToALowerLeaf_TakesANewEpoch_SoTheHighWaterDoesNotPinIt()
    {
        // The other half of the clamp: a new composite epoch may legitimately start at ANY coordinate, so
        // the high-water is reset with the id. Without that, one hand-off to a leaf sitting further back
        // would freeze the composite for good.
        var high = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(90) };
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(4) };
        var clock = new CompositePlaybackClock(
            new PlaybackClockCandidate(high, 100),
            new PlaybackClockCandidate(low, 1));

        var before = clock.Read();
        Assert.Equal(TimeSpan.FromSeconds(90), before.Elapsed);

        high.IsAdvancing = false;
        var after = clock.Read();
        Assert.NotEqual(before.EpochId, after.EpochId);
        Assert.Equal(TimeSpan.FromSeconds(4), after.Elapsed);

        low.ElapsedSinceStart = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Read().Elapsed);
    }

    [Fact]
    public void MemberReads_DoNotStartTheHandoffCrossFade_OnlyReadDoes()
    {
        // C3: ElapsedSinceStart and EpochId both ran the whole of Read(), mutating _lastEmitted,
        // _transitionStartTicks, _blendEpochId and the co-advance EMA sample tick. So merely LOOKING at the
        // epoch started the cross-fade clock, and a consumer that read the epoch and then the elapsed
        // sampled the smoothing filter twice. Two clocks given byte-identical inputs must not disagree
        // because one of them was observed through its members.
        long sim = 0;
        long Now() => sim;
        var blend = new CompositePlaybackClockBlend { HandoffCrossFade = TimeSpan.FromSeconds(2) };

        var lowControl = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(10) };
        var highControl = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(90) };
        var control = new CompositePlaybackClock(blend, Now,
            new PlaybackClockCandidate(lowControl, 1),
            new PlaybackClockCandidate(highControl, 100));

        var lowObserved = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(10) };
        var highObserved = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(90) };
        var observed = new CompositePlaybackClock(blend, Now,
            new PlaybackClockCandidate(lowObserved, 1),
            new PlaybackClockCandidate(highObserved, 100));

        Assert.Equal(TimeSpan.FromSeconds(10), control.Read().Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(10), observed.Read().Elapsed);

        // The high-priority leaf starts advancing: a hand-off is pending on both clocks.
        highControl.IsAdvancing = true;
        highObserved.IsAdvancing = true;

        // The ONLY difference between the two: somebody looked at the observed one.
        _ = observed.EpochId;
        _ = observed.ElapsedSinceStart;
        _ = observed.IsAdvancing;

        // One second later both take their first post-hand-off Read: each should be at t=0 of its 2 s fade.
        sim += Stopwatch.Frequency;
        Assert.Equal(control.Read().Elapsed, observed.Read().Elapsed);
    }

    [Fact]
    public void ARead_IsOneCoherentSnapshot_ExcludingASecondReaderForItsWholeDuration()
    {
        // C2: the candidate sweep ran unlocked, epoch resolution took _epochGate and the blend took
        // _blendGate, so two concurrent readers interleaved - one pairing a freshly allocated epoch id with
        // the other's winner - and the id then churned on every read. A MediaClock mastered on this clock
        // re-anchors at each new id, discarding whatever accrued between reads. The fix is that a reading is
        // taken under ONE gate, which is observable exactly as mutual exclusion: a reader parked inside a
        // candidate's Read() must exclude the next one.
        var parking = new ParksOnFirstReadClock(TimeSpan.FromSeconds(7));
        var clock = new CompositePlaybackClock(new PlaybackClockCandidate(parking, 1));

        var firstReading = default(ClockReading);
        var firstReader = new Thread(() => firstReading = clock.Read()) { IsBackground = true };
        firstReader.Start();
        Assert.True(parking.Entered.Wait(TimeSpan.FromSeconds(5)), "the first reader never reached the candidate.");

        using var secondAtGate = new ManualResetEventSlim(false);
        using var secondDone = new ManualResetEventSlim(false);
        var secondReading = default(ClockReading);
        var secondReader = new Thread(() =>
        {
            secondAtGate.Set();
            secondReading = clock.Read();
            secondDone.Set();
        }) { IsBackground = true };
        secondReader.Start();
        Assert.True(secondAtGate.Wait(TimeSpan.FromSeconds(5)), "the second reader never started.");

        try
        {
            Assert.False(secondDone.Wait(TimeSpan.FromMilliseconds(250)),
                "a second reader completed a reading while the first was still mid-sweep - sampling is not atomic.");
        }
        finally
        {
            parking.Release.Set();
        }

        Assert.True(firstReader.Join(TimeSpan.FromSeconds(5)), "the parked reader did not drain.");
        Assert.True(secondReader.Join(TimeSpan.FromSeconds(5)), "the second reader did not drain.");
        Assert.Equal(TimeSpan.FromSeconds(7), firstReading.Elapsed);
        Assert.Equal(firstReading.EpochId, secondReading.EpochId); // one pair observed ⇒ one epoch id
    }

    private sealed class MutableStubPlaybackClock : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart { get; set; }
        public bool IsAdvancing { get; set; }
    }

    private sealed class StubPlaybackClock : IPlaybackClock
    {
        private readonly bool _adv;
        private readonly TimeSpan _elapsed;

        public StubPlaybackClock(bool adv, TimeSpan elapsed)
        {
            _adv = adv;
            _elapsed = elapsed;
        }

        public TimeSpan ElapsedSinceStart => _elapsed;
        public bool IsAdvancing => _adv;
    }

    private sealed class StopsWhenSampledClock(TimeSpan elapsed) : IPlaybackClock
    {
        // Models a valid race: the legacy property was observed while advancing, then the clock stopped
        // before the sanctioned atomic sample was taken.
        public TimeSpan ElapsedSinceStart => elapsed;
        public bool IsAdvancing => true;
        public ClockReading Read() => new(PlaybackEpoch.Single, elapsed, false);
    }

    /// <summary>A leaf whose FIRST <see cref="Read"/> parks until released, so a test can hold one composite
    /// reader inside the candidate sweep and watch what a second one is allowed to do.</summary>
    private sealed class ParksOnFirstReadClock(TimeSpan elapsed) : IPlaybackClock
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        private int _reads;

        public TimeSpan ElapsedSinceStart => elapsed;
        public bool IsAdvancing => true;

        public ClockReading Read()
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }

            return new ClockReading(PlaybackEpoch.Single, elapsed, true);
        }
    }
}
