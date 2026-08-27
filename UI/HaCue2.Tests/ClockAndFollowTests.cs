using System.Diagnostics;
using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.Presentation;
using HaCue2.Session;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Review 2026-08-25, C1/C3/C6: the Active panel extrapolates from the stamp taken WHERE the
/// transport positions were read (not on arrival), the timeline sheet's follow mode rides the same
/// rule, and PPM meters fall at the meter's rate rather than the poll's.
/// </summary>
public sealed class ClockAndFollowTests
{
    // ── C1: the poll stamp travels from the snapshot, not from arrival ────────────────────────

    [Fact]
    public void ActiveRowsCarryTheSnapshotsOwnReadStamp()
    {
        var project = ProjectFiles.Create("Show", "/media");
        var cue = new MediaCueNode { Label = "clip" };
        project.CueLists[0].Cues.Add(cue);

        var stamp = 123_456_789L;
        var rows = CuePresentation.Active(
            project,
            [new ActiveCueState(cue.Id, project.CueLists[0].Id,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), false, 0, null, stamp)],
            new Dictionary<Guid, TimeSpan>());

        // The stamp taken beside the position read, not Stopwatch-now at presentation time: the
        // dispatcher hops between the two vary poll to poll, and extrapolating from arrival made
        // the millisecond digits step at every correction.
        Assert.Equal(stamp, Assert.Single(rows).PolledAtTicks);
    }

    [Fact]
    public void AStateWithoutAStampStillGetsAUsableOne()
    {
        var project = ProjectFiles.Create("Show", "/media");
        var cue = new MediaCueNode { Label = "clip" };
        project.CueLists[0].Cues.Add(cue);

        var before = Stopwatch.GetTimestamp();
        var rows = CuePresentation.Active(
            project,
            [new ActiveCueState(cue.Id, project.CueLists[0].Id,
                TimeSpan.FromSeconds(1), null, false, 0)],
            new Dictionary<Guid, TimeSpan>());

        Assert.InRange(Assert.Single(rows).PolledAtTicks, before, Stopwatch.GetTimestamp());
    }

    [Fact]
    public void GroupAggregatesShareTheirChildrensStamp()
    {
        var project = ProjectFiles.Create("Show", "/media");
        var group = new GroupCueNode { Label = "TL", FireMode = GroupFireMode.Timeline };
        var clip = new MediaCueNode { Label = "clip", TimelineOffsetMs = 0 };
        group.Children.Add(clip);
        project.CueLists[0].Cues.Add(group);

        var stamp = 42_000_000L;
        var durations = new Dictionary<Guid, TimeSpan> { [clip.Id] = TimeSpan.FromSeconds(10) };
        var rows = CuePresentation.ActivePanel(
            project,
            CuePresentation.Active(
                project,
                [new ActiveCueState(clip.Id, project.CueLists[0].Id,
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), false, 0, null, stamp)],
                durations),
            durations);

        // The header's remaining/countdown are computed FROM the children's positions, so they
        // extrapolate from the same read stamp - a fresh "now" here re-imports arrival jitter.
        var header = Assert.IsType<ActiveGroupRow>(Assert.Single(rows));
        Assert.Equal(stamp, header.PolledAtTicks);
    }

    // ── C3: the sheet's follow mode ───────────────────────────────────────────────────────────

    private static (TimelineViewModel Sheet, ShowRuntime Runtime, GroupCueNode Group, MediaCueNode Clip)
        TimelineFixture()
    {
        var project = ProjectFiles.Create("Show", "/media");
        var group = new GroupCueNode { Label = "TL", FireMode = GroupFireMode.Timeline };
        var clip = new MediaCueNode { Label = "clip", TimelineOffsetMs = 4_000 };
        group.Children.Add(clip);
        project.CueLists[0].Cues.Add(group);

        var runtime = new ShowRuntime
        {
            MediaDurations = new Dictionary<Guid, TimeSpan> { [clip.Id] = TimeSpan.FromSeconds(10) },
        };

        var sheet = new TimelineViewModel(project, runtime);
        sheet.Show(group);
        return (sheet, runtime, group, clip);
    }

    private static ActiveCueRow Row(Guid id, TimeSpan position, long polledAt) => new()
    {
        CueId = id,
        Number = "1",
        Label = "clip",
        Position = position,
        PolledAtTicks = polledAt,
        Duration = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public void FollowAdoptsTheRunningGroupsPositionInGroupTime()
    {
        var (sheet, runtime, _, clip) = TimelineFixture();
        runtime.ActiveCues = [Row(clip.Id, TimeSpan.FromSeconds(2), polledAt: 0)];
        sheet.FollowTransport = true;

        // One second after the poll stamp, without sleeping: offset 4 s + position 2 s + 1 s.
        sheet.FollowTick(Stopwatch.Frequency);

        Assert.Equal(7_000, sheet.PlayheadAt.TotalMilliseconds, precision: 0);
    }

    [Fact]
    public void FollowHoldsWhereTheRunEndedWhenNothingSounds()
    {
        var (sheet, runtime, _, clip) = TimelineFixture();
        runtime.ActiveCues = [Row(clip.Id, TimeSpan.FromSeconds(2), polledAt: 0)];
        sheet.FollowTransport = true;
        sheet.FollowTick(0);
        var where = sheet.PlayheadAt;

        runtime.ActiveCues = [];
        sheet.FollowTick(Stopwatch.Frequency);

        // Where the run ended is where "play from here" should resume - not a rewind to zero.
        Assert.Equal(where, sheet.PlayheadAt);
    }

    [Fact]
    public void PlacingThePlayheadByHandReleasesFollow()
    {
        var (sheet, runtime, _, clip) = TimelineFixture();
        runtime.ActiveCues = [Row(clip.Id, TimeSpan.FromSeconds(2), polledAt: 0)];
        sheet.FollowTransport = true;

        sheet.PlacePlayhead(0.25);

        // The operator asserted a position; dragging the cursor back on the next tick would fight
        // them, so the latch visibly releases instead.
        Assert.False(sheet.FollowTransport);
        var placed = sheet.PlayheadAt;
        sheet.FollowTick(Stopwatch.Frequency);
        Assert.Equal(placed, sheet.PlayheadAt);
    }

    [Fact]
    public void OffByDefaultAndInertWhileOff()
    {
        var (sheet, runtime, _, clip) = TimelineFixture();
        runtime.ActiveCues = [Row(clip.Id, TimeSpan.FromSeconds(2), polledAt: 0)];

        Assert.False(sheet.FollowTransport);
        var before = sheet.PlayheadAt;
        sheet.FollowTick(Stopwatch.Frequency);
        Assert.Equal(before, sheet.PlayheadAt);
    }

    [Fact]
    public void TheTickerRaisesItsClockTickForFollowers()
    {
        var runtime = new ShowRuntime();
        var now = 77L;
        using var ticker = new ActivePanelTicker(runtime, () => ProjectFiles.Create("Show", "/media"), () => now);
        ticker.FlatList = true;
        ticker.Poll([Row(Guid.NewGuid(), TimeSpan.Zero, polledAt: 0)]);

        long? observed = null;
        ticker.ClockTicked += stamp => observed = stamp;
        ticker.TickClocks();

        Assert.Equal(now, observed);
    }

    // ── C6: PPM ballistics ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PpmJumpsToAPeakInstantlyAndFallsAtTheMetersOwnRate()
    {
        var presenter = new ProgramMeterPresenter();
        var settings = new AppSettings { MeterBallistics = "PPM" };
        var t0 = DateTimeOffset.UnixEpoch;

        var hit = presenter.Present([new ProgramMeter("MAIN", 0.5, 0.8)], settings, t0);
        Assert.Equal(0.8, Assert.Single(hit).Level, precision: 3);

        // One second of silence: the bar has FALLEN by the PPM rate, not snapped to the reading -
        // a reading-tracking bar showed poll-sized steps and missed transients entirely.
        var falling = presenter.Present([new ProgramMeter("MAIN", 0.0, 0.0)], settings, t0.AddSeconds(1));
        Assert.Equal(0.6, Assert.Single(falling).Level, precision: 3);

        // A louder peak takes over instantly.
        var again = presenter.Present([new ProgramMeter("MAIN", 0.2, 0.7)], settings, t0.AddSeconds(1.5));
        Assert.Equal(0.7, Assert.Single(again).Level, precision: 3);
    }

    [Fact]
    public void VuStillReadsTheRmsLevelUntouched()
    {
        var presenter = new ProgramMeterPresenter();
        var settings = new AppSettings { MeterBallistics = "VU" };

        var shown = presenter.Present(
            [new ProgramMeter("MAIN", 0.5, 0.8)], settings, DateTimeOffset.UnixEpoch);

        Assert.Equal(0.5, Assert.Single(shown).Level, precision: 3);
    }
}
