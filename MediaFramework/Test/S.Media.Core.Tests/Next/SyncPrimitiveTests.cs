using Xunit;

namespace S.Media.Core.Tests.Next;

public class SourceTimelineTests
{
    [Fact]
    public void Scheduled_unanchored_maps_pts_plus_offset()
    {
        var t = new SourceTimeline(RebasePolicy.Scheduled, TimeSpan.FromMilliseconds(100));
        Assert.False(t.IsAnchored);
        Assert.Equal(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(100), t.DueTime(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Anchor_ties_source_pts_to_master_and_is_invertible()
    {
        var t = new SourceTimeline(RebasePolicy.Holdback);
        t.Anchor(sourcePts: TimeSpan.FromSeconds(100), masterNow: TimeSpan.FromSeconds(2));

        Assert.True(t.IsAnchored);
        Assert.Equal(TimeSpan.FromSeconds(2), t.DueTime(TimeSpan.FromSeconds(100)));   // anchored frame
        Assert.Equal(TimeSpan.FromSeconds(3), t.DueTime(TimeSpan.FromSeconds(101)));   // +1s sender → +1s master
        Assert.Equal(TimeSpan.FromSeconds(100), t.SourceTimeAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(101), t.SourceTimeAt(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Offset_shifts_due_time_while_anchored()
    {
        var t = new SourceTimeline(RebasePolicy.Holdback, TimeSpan.FromMilliseconds(500));
        t.Anchor(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(500), t.DueTime(TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void Reset_drops_anchor()
    {
        var t = new SourceTimeline(RebasePolicy.RebaseToLatest);
        t.Anchor(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(2));
        t.Reset();
        Assert.False(t.IsAnchored);
        Assert.Equal(TimeSpan.FromSeconds(5), t.DueTime(TimeSpan.FromSeconds(5)));
    }
}

public class SessionClockTests
{
    [Fact]
    public void Now_tracks_the_reference_elapsed()
    {
        var clk = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.Zero };
        var sc = new SessionClock(clk);
        Assert.Equal(TimeSpan.Zero, sc.Now);

        clk.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(3), sc.Now);
    }

    [Fact]
    public void SetReference_preserves_now_across_the_swap()
    {
        var a = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var sc = new SessionClock(a);
        Assert.Equal(TimeSpan.FromSeconds(3), sc.Now);

        var b = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(100) };
        sc.SetReference(b);
        Assert.Equal(TimeSpan.FromSeconds(3), sc.Now);          // no jump on swap

        b.ElapsedSinceStart = TimeSpan.FromSeconds(101);
        Assert.Equal(TimeSpan.FromSeconds(4), sc.Now);          // advances from the new reference
    }

    [Fact]
    public void RebaseReference_keeps_master_monotonic_when_the_source_clock_seeks()
    {
        var source = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var sc = new SessionClock(source);
        var beforeSeek = sc.Now;

        source.ElapsedSinceStart = TimeSpan.FromSeconds(40);
        sc.RebaseReference(beforeSeek);

        Assert.Equal(beforeSeek, sc.Now);
        source.ElapsedSinceStart = TimeSpan.FromSeconds(41);
        Assert.Equal(beforeSeek + TimeSpan.FromSeconds(1), sc.Now);
    }

    [Fact]
    public void IsAdvancing_reflects_reference()
    {
        var clk = new FakePlaybackClock { IsAdvancing = false };
        var sc = new SessionClock(clk);
        Assert.False(sc.IsAdvancing);
        clk.IsAdvancing = true;
        Assert.True(sc.IsAdvancing);
    }

    [Fact]
    public void LiveWallClock_is_advancing()
    {
        var sc = SessionClock.LiveWallClock();
        Assert.True(sc.IsAdvancing);
        Assert.True(sc.Now >= TimeSpan.Zero);
    }

    [Fact]
    public void Now_autoRebases_when_the_reference_reanchors_with_no_caller_announcement()
    {
        // S1, the point of plan D for this class. The reference (a MediaClock playhead in production) reports
        // its own re-anchors, and Now compares that id. Before this, group master time stayed monotonic ONLY
        // because ShowSession remembered to call MarkDiscontinuity(preserved) at each of its four seek/loop
        // sites: one missed call on a future seek path silently rewound the whole transport group.
        var reference = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(30) };
        var sc = new SessionClock(reference);
        Assert.Equal(TimeSpan.FromSeconds(30), sc.Now);

        reference.ElapsedSinceStart = TimeSpan.FromSeconds(35);
        Assert.Equal(TimeSpan.FromSeconds(35), sc.Now);

        // A seek / loop wrap: the source coordinate lands at 2 s in a NEW epoch. Nobody calls RebaseReference.
        reference.Reanchor(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(35), sc.Now);  // was 2 s: a 33 s rewind of group master time

        reference.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(36), sc.Now);  // and advances at the new epoch's rate

        // Idempotent: the rebase is published once, not re-derived (and re-shifted) on every read.
        Assert.Equal(TimeSpan.FromSeconds(36), sc.Now);
    }

    [Fact]
    public void Now_holds_when_the_reference_regresses_inside_one_epoch()
    {
        // The other side of the same contract: within one epoch the reference is monotonic BY contract, so a
        // regression is a violation or a torn read. Holding never invents time; following it would rewind
        // every source scheduled against this master.
        var reference = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(12) };
        var sc = new SessionClock(reference);
        Assert.Equal(TimeSpan.FromSeconds(12), sc.Now);

        reference.ElapsedSinceStart = TimeSpan.FromSeconds(11); // same epoch, illegal
        Assert.Equal(TimeSpan.FromSeconds(12), sc.Now);

        reference.ElapsedSinceStart = TimeSpan.FromSeconds(13);
        Assert.Equal(TimeSpan.FromSeconds(13), sc.Now);
    }

    [Fact]
    public void RebaseReference_still_wins_over_the_automatic_rebase()
    {
        // The explicit call is not redundant: it pins group time to an instant the CALLER captured before the
        // jump, which the automatic path can only approximate with the last value it happened to observe.
        var reference = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(20) };
        var sc = new SessionClock(reference);
        var beforeSeek = sc.Now;

        reference.Reanchor(TimeSpan.FromSeconds(90)); // the seek lands far ahead in a new epoch
        _ = sc.Now;                                   // a reader auto-rebases first...
        sc.RebaseReference(beforeSeek);               // ...and the explicit announcement still governs

        Assert.Equal(beforeSeek, sc.Now);
        reference.ElapsedSinceStart = TimeSpan.FromSeconds(91);
        Assert.Equal(beforeSeek + TimeSpan.FromSeconds(1), sc.Now);
    }

    [Fact]
    public void SetReference_onto_a_reanchoring_clock_adopts_its_epoch()
    {
        // SetReference read reference.ElapsedSinceStart separately from anything about its epoch, so it could
        // tear across a re-anchor. It takes one atomic reading now: the swap must not itself look like an
        // unannounced re-anchor on the very next read.
        var a = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(5) };
        var sc = new SessionClock(a);
        Assert.Equal(TimeSpan.FromSeconds(5), sc.Now);

        var b = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(200) };
        sc.SetReference(b);
        Assert.Equal(TimeSpan.FromSeconds(5), sc.Now);
        Assert.Equal(b.EpochId, sc.Reference.Read().EpochId);

        b.ElapsedSinceStart = TimeSpan.FromSeconds(201);
        Assert.Equal(TimeSpan.FromSeconds(6), sc.Now);

        b.Reanchor(TimeSpan.FromSeconds(1)); // and the new reference's re-anchors are absorbed too
        Assert.Equal(TimeSpan.FromSeconds(6), sc.Now);
    }
}

public class TransportTimelineTests
{
    [Fact]
    public void BindSource_publishes_master_source_and_cue_coordinates_with_trim()
    {
        var master = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(5) };
        var playhead = new FakeReadOnlyPlayhead { CurrentPosition = TimeSpan.FromSeconds(10) };
        var timeline = new TransportTimeline(new SessionClock(master));

        timeline.BindSource(
            playhead,
            trimStart: TimeSpan.FromSeconds(10),
            trimEnd: TimeSpan.FromSeconds(20));
        var start = timeline.GetSnapshot();

        Assert.Equal(TimeSpan.FromSeconds(5), start.MasterTime);
        Assert.Equal(TimeSpan.FromSeconds(10), start.SourceTime);
        Assert.Equal(TimeSpan.Zero, start.CueTime);
        Assert.Equal(TimeSpan.FromSeconds(5), start.CueOrigin);
        Assert.Equal(TimeSpan.FromSeconds(10), start.TrimStart);
        Assert.Equal(TimeSpan.FromSeconds(20), start.TrimEnd);
        Assert.Equal(RebasePolicy.Scheduled, start.SourceCorrelation.Policy);
        Assert.Equal(1, start.Generation);

        master.ElapsedSinceStart = TimeSpan.FromSeconds(7);
        Assert.Equal(TimeSpan.FromSeconds(12), timeline.GetSnapshot().SourceTime);
        Assert.Equal(TimeSpan.FromSeconds(2), timeline.GetSnapshot().CueTime);
        Assert.Equal(TimeSpan.FromSeconds(7), timeline.MasterTimeAt(TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void Discontinuity_reanchors_mapping_without_moving_the_cue_origin()
    {
        var master = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(2) };
        var playhead = new FakeReadOnlyPlayhead { CurrentPosition = TimeSpan.FromSeconds(10) };
        var timeline = new TransportTimeline(new SessionClock(master));
        timeline.BindSource(playhead, trimStart: TimeSpan.FromSeconds(10));
        var cueOrigin = timeline.GetSnapshot().CueOrigin;

        master.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        playhead.CurrentPosition = TimeSpan.FromSeconds(40); // coordinated seek
        timeline.MarkDiscontinuity();
        master.ElapsedSinceStart = TimeSpan.FromSeconds(4);

        var after = timeline.GetSnapshot();
        Assert.Equal(2, after.Generation);
        Assert.Equal(cueOrigin, after.CueOrigin);
        Assert.Equal(TimeSpan.FromSeconds(41), after.SourceTime);
        Assert.Equal(TimeSpan.FromSeconds(31), after.CueTime);
    }

    [Fact]
    public void Playback_rate_and_live_correlation_are_part_of_the_same_contract()
    {
        var master = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var playhead = new FakeReadOnlyPlayhead
        {
            CurrentPosition = TimeSpan.FromSeconds(100),
            PlaybackRate = 2.0,
        };
        var timeline = new TransportTimeline(new SessionClock(master));
        timeline.BindSource(playhead, isLive: true, sourceOffset: TimeSpan.FromMilliseconds(20));

        master.ElapsedSinceStart = TimeSpan.FromSeconds(2);
        var snapshot = timeline.GetSnapshot();

        Assert.True(snapshot.IsLive);
        Assert.True(snapshot.IsRunning);
        Assert.Equal(2.0, snapshot.PlaybackRate);
        Assert.Equal(RebasePolicy.RebaseToLatest, snapshot.SourceCorrelation.Policy);
        Assert.Equal(TimeSpan.FromMilliseconds(101_960), snapshot.SourceTime);
        Assert.Equal(TimeSpan.FromMilliseconds(1_020), timeline.MasterTimeAt(TimeSpan.FromSeconds(100)));
        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.OutputPresentationTime);
    }

    [Fact]
    public void Clear_freezes_an_inactive_timeline_and_bumps_generation()
    {
        var master = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var timeline = new TransportTimeline(new SessionClock(master));
        timeline.BindSource(new FakeReadOnlyPlayhead());

        timeline.Clear();
        master.ElapsedSinceStart = TimeSpan.FromSeconds(4);
        var snapshot = timeline.GetSnapshot();

        Assert.Equal(2, snapshot.Generation);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(0d, snapshot.PlaybackRate);
        Assert.Equal(TimeSpan.FromSeconds(4), snapshot.MasterTime);
        Assert.Equal(TimeSpan.Zero, snapshot.SourceTime);
    }
}

public class SourceSyncGroupTests
{
    [Fact]
    public void Anchoring_the_group_anchors_all_members_with_their_own_offset()
    {
        var group = new SourceSyncGroup(RebasePolicy.Holdback);
        var video = group.AddStream(correctionOffset: TimeSpan.FromMilliseconds(20)); // known A/V phase
        var audio = group.AddStream();

        Assert.Equal(2, group.Members.Count);
        Assert.Equal(RebasePolicy.Holdback, video.Policy);

        group.Anchor(senderPts: TimeSpan.FromSeconds(50), masterNow: TimeSpan.FromSeconds(1));

        Assert.True(group.IsAnchored);
        Assert.True(video.IsAnchored && audio.IsAnchored);
        // Shared sender anchor; per-stream offset is the only difference (the correctable A/V phase).
        Assert.Equal(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(20), video.DueTime(TimeSpan.FromSeconds(50)));
        Assert.Equal(TimeSpan.FromSeconds(1), audio.DueTime(TimeSpan.FromSeconds(50)));
    }
}
