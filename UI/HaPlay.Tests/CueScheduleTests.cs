using System.Collections.Concurrent;
using System.Text.Json;
using HaPlay.Services;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Timed cue triggers (Ideas/CuePlayer-Enhancements.md §4): schedule persistence (incl. the
/// STJ init-property gotcha - GraceMs 5000 / Enabled true must survive a minimal
/// <c>"schedule":{}</c>), the VM round-trip on the CueNode base, and the CueSchedulerService
/// runtime with an injected clock - exactly-once fires inside the grace window, beyond-grace skips
/// with a log (never a caught-up backlog), the armed + edit-mode gates, recurring day-mask
/// filtering, one-shot once-per-session semantics, and disabled schedules staying inert.</summary>
public sealed class CueScheduleTests
{
    // ---- Persistence ----

    [Fact]
    public void Schedule_RoundTrip_ThroughViewModelAndJson()
    {
        var node = new MediaCueNode
        {
            Number = "1",
            Label = "Opener",
            Source = new FilePlaylistItem("/tmp/opener.wav"),
            Schedule = new CueSchedule
            {
                Kind = CueScheduleKind.Recurring,
                TimeOfDay = new TimeOnly(15, 30),
                Days = CueScheduleDays.Monday | CueScheduleDays.Friday,
                GraceMs = 3000,
                Enabled = false, // retained-while-disabled must round-trip too
            },
        };

        // VM round-trip preserves the payload (Schedule lives on the CueNode base like ColorTag).
        var vm = CueNodeViewModel.FromModel(node);
        Assert.True(vm.HasSchedule);
        Assert.False(vm.ScheduleEnabled);
        Assert.Equal(CueScheduleKind.Recurring, vm.ScheduleKind);
        Assert.Equal(new TimeOnly(15, 30), vm.ScheduleTimeOfDay);
        Assert.True(vm.ScheduleOnMonday);
        Assert.True(vm.ScheduleOnFriday);
        Assert.False(vm.ScheduleOnTuesday);
        Assert.Equal(3000, vm.ScheduleGraceMs);
        var back = Assert.IsType<MediaCueNode>(vm.ToModel());
        Assert.Equal(node.Schedule, back.Schedule);

        // JSON round-trip through the cue-list contract (TimeOnly must survive source-gen).
        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<MediaCueNode>(Assert.Single(loaded.Nodes));
        Assert.Equal(node.Schedule, reloaded.Schedule);
    }

    [Fact]
    public void OneShot_RoundTrip_PreservesInstant()
    {
        var wall = new DateTime(2026, 12, 31, 15, 0, 0);
        var at = new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
        var node = new ActionCueNode
        {
            Label = "Doors",
            Schedule = new CueSchedule { Kind = CueScheduleKind.DateTime, At = at },
        };

        var vm = CueNodeViewModel.FromModel(node);
        Assert.True(vm.IsScheduleOneShot);
        Assert.Equal("2026-12-31", vm.ScheduleDateText);
        Assert.Equal("15:00", vm.ScheduleTimeText);

        var back = Assert.IsType<ActionCueNode>(vm.ToModel());
        Assert.NotNull(back.Schedule?.At);
        Assert.Equal(at, back.Schedule!.At!.Value);
        Assert.Null(back.Schedule.TimeOfDay); // one-shots persist At only

        // JSON round-trip (DateTimeOffset through the source-gen context).
        var json = JsonSerializer.Serialize(new CueList { Nodes = [back] }, CueListJsonContext.Default.CueList);
        var reloaded = Assert.IsType<ActionCueNode>(
            JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!.Nodes[0]);
        Assert.Equal(back.Schedule, reloaded.Schedule);
    }

    [Fact]
    public void Schedule_LegacyAbsence_LoadsNull_AndEmptyObjectKeepsDefaults()
    {
        // Old files carry no "schedule" field at all - must load unchanged (null schedule).
        var legacy = """{"nodes":[{"kind":"media","label":"Old"}]}""";
        var loaded = JsonSerializer.Deserialize(legacy, CueListJsonContext.Default.CueList)!;
        Assert.Null(Assert.IsType<MediaCueNode>(loaded.Nodes[0]).Schedule);

        // The STJ source-gen gotcha: a minimal "schedule":{} must keep the C# property initializers
        // (set, not init - see the CueSchedule doc note), NOT collapse to CLR defaults.
        var minimal = """{"nodes":[{"kind":"media","label":"New","schedule":{}}]}""";
        var minimalLoaded = JsonSerializer.Deserialize(minimal, CueListJsonContext.Default.CueList)!;
        var schedule = Assert.IsType<CueSchedule>(
            Assert.IsType<MediaCueNode>(minimalLoaded.Nodes[0]).Schedule);
        Assert.Equal(5000, schedule.GraceMs);
        Assert.True(schedule.Enabled);
        Assert.Equal(CueScheduleKind.TimeOfDay, schedule.Kind);
        Assert.Null(schedule.TimeOfDay);
        Assert.Null(schedule.At);
        Assert.Equal(CueScheduleDays.None, schedule.Days);
    }

    [Fact]
    public void CueWithoutSchedule_WritesNoScheduleField()
    {
        var vm = CueNodeViewModel.FromModel(new MediaCueNode { Label = "Plain" });
        Assert.False(vm.HasSchedule);
        var back = Assert.IsType<MediaCueNode>(vm.ToModel());
        Assert.Null(back.Schedule);

        var json = JsonSerializer.Serialize(
            new CueList { Nodes = [back] }, CueListJsonContext.Default.CueList);
        Assert.DoesNotContain("schedule", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Runtime harness ----

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; set; }
    }

    private sealed record Harness(
        CuePlayerViewModel Vm,
        CueSchedulerService Scheduler,
        TestClock Clock,
        CueNodeViewModel CueVm,
        Guid CueId,
        ConcurrentQueue<Guid> Fired,
        SemaphoreSlim FireSignal);

    /// <summary>Fixed base day for the deterministic-clock tests: 2026-07-29 is a Wednesday.</summary>
    private static readonly DateTime BaseDay = new(2026, 7, 29);

    private static Harness BuildHarness(CueSchedule schedule, DateTimeOffset start, bool arm = true)
    {
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Scheduled song",
            Source = new FilePlaylistItem("/tmp/song.wav"),
            Schedule = schedule,
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [cue] }]);
        vm.IsCueEditMode = false; // the ctor default is edit mode ON - a show runs with it off

        var fired = new ConcurrentQueue<Guid>();
        var signal = new SemaphoreSlim(0);
        vm.MediaCueExecutor = (m, _) =>
        {
            vm.OnCueStarted(m.Id);
            fired.Enqueue(m.Id);
            signal.Release();
            return Task.FromResult<string?>(null);
        };

        var clock = new TestClock { Now = start };
        var scheduler = new CueSchedulerService(vm, () => clock.Now);
        if (arm)
            vm.SchedulesArmed = true; // AFTER service creation - arming stamps the "from now" baseline

        return new Harness(vm, scheduler, clock, vm.SelectedCueList!.Nodes[0], cue.Id, fired, signal);
    }

    private static async Task<Guid> NextFiredAsync(Harness h)
    {
        Assert.True(await h.FireSignal.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting for a scheduled fire");
        Assert.True(h.Fired.TryDequeue(out var id));
        return id;
    }

    private static async Task AssertNoFiresAsync(Harness h, int graceMs = 250)
    {
        await Task.Delay(graceMs);
        Assert.Empty(h.Fired);
    }

    private static DateTimeOffset At(TimeSpan timeOfDay, int dayOffset = 0) =>
        new(BaseDay.AddDays(dayOffset) + timeOfDay, TimeSpan.FromHours(2));

    private static CueScheduleDays DayFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => CueScheduleDays.Monday,
        DayOfWeek.Tuesday => CueScheduleDays.Tuesday,
        DayOfWeek.Wednesday => CueScheduleDays.Wednesday,
        DayOfWeek.Thursday => CueScheduleDays.Thursday,
        DayOfWeek.Friday => CueScheduleDays.Friday,
        DayOfWeek.Saturday => CueScheduleDays.Saturday,
        _ => CueScheduleDays.Sunday,
    };

    // ---- Scheduler runtime ----

    [Fact]
    public async Task Scheduler_FiresExactlyOnce_InsideGraceWindow()
    {
        var h = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0) },
            start: At(new TimeSpan(14, 59, 50)));

        h.Scheduler.Tick(); // not due yet
        await AssertNoFiresAsync(h);

        h.Clock.Now = At(new TimeSpan(15, 0, 1)); // 1 s late - inside the 5 s default grace
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Same occurrence never double-triggers within its grace window.
        h.Scheduler.Tick();
        h.Clock.Now = At(new TimeSpan(15, 0, 4));
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
    }

    [Fact]
    public async Task Scheduler_ZeroGrace_StillFires_WhenObservedLateByLessThanTheSweepFloor()
    {
        // The sweep itself observes an occurrence 0..TickInterval late, so the effective grace is
        // floored at 2×TickInterval (500 ms): GraceMs 0 means "fire at the next tick", not "never
        // fires". Regression: a 300 ms-late observation was silently skipped as beyond grace.
        var h = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0), GraceMs = 0 },
            start: At(new TimeSpan(14, 59, 50)));

        h.Scheduler.Tick(); // not due yet
        await AssertNoFiresAsync(h);

        h.Clock.Now = At(new TimeSpan(15, 0, 0)) + TimeSpan.FromMilliseconds(300); // < 2×TickInterval late
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Beyond the floor the zero grace still skips (the floor is a sweep allowance, not a new
        // minimum grace of "whenever"): a fresh occurrence observed 2 s late is dropped with a log.
        var late = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0), GraceMs = 0 },
            start: At(new TimeSpan(14, 59, 50)));
        late.Clock.Now = At(new TimeSpan(15, 0, 2));
        late.Scheduler.Tick();
        await AssertNoFiresAsync(late);
        Assert.Contains("beyond grace", late.Vm.StatusMessage ?? string.Empty);
    }

    [Fact]
    public async Task Scheduler_SkipsBeyondGrace_LogsOnce_AndCatchesTheNextDay()
    {
        var h = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0), GraceMs = 5000 },
            start: At(new TimeSpan(14, 59, 50)));

        // App "slept" past the occurrence: 2 minutes late is beyond the 5 s grace.
        h.Clock.Now = At(new TimeSpan(15, 2, 0));
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
        Assert.Contains("beyond grace", h.Vm.StatusMessage ?? string.Empty);

        // The skip is logged once, not per tick.
        h.Vm.StatusMessage = null;
        h.Scheduler.Tick();
        Assert.Null(h.Vm.StatusMessage);

        // Daily schedule recovers on the next day's occurrence.
        h.Clock.Now = At(new TimeSpan(15, 0, 2), dayOffset: 1);
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Scheduler_DisarmedGate_NeverFires_AndArmingIsFromNow()
    {
        var h = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0) },
            start: At(new TimeSpan(14, 59, 50)),
            arm: false);

        h.Clock.Now = At(new TimeSpan(15, 0, 1));
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
        Assert.Null(h.Vm.StatusMessage); // a closed gate neither fires nor spams skip logs

        // Arming AFTER the occurrence is "schedule from now": the passed occurrence stays dead
        // even though it is still inside its grace window.
        h.Vm.SchedulesArmed = true;
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);

        // The next day's occurrence fires normally.
        h.Clock.Now = At(new TimeSpan(15, 0, 1), dayOffset: 1);
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Scheduler_EditMode_SuppressesFires_UntilLeftWithinGrace()
    {
        var h = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0) },
            start: At(new TimeSpan(14, 59, 50)));
        h.Vm.IsCueEditMode = true;

        h.Clock.Now = At(new TimeSpan(15, 0, 1));
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h); // an operator editing at 14:59 must not have Q50 fire

        // Leaving edit mode inside the grace window still honors the occurrence (misfire policy).
        h.Vm.IsCueEditMode = false;
        h.Clock.Now = At(new TimeSpan(15, 0, 3));
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Scheduler_RecurringDayMask_Filters()
    {
        // BaseDay is a Wednesday; a Thursday-only schedule must not fire on it…
        var wrongDay = BuildHarness(
            new CueSchedule
            {
                Kind = CueScheduleKind.Recurring,
                TimeOfDay = new TimeOnly(15, 0),
                Days = DayFlag(BaseDay.AddDays(1).DayOfWeek),
            },
            start: At(new TimeSpan(14, 59, 50)));
        wrongDay.Clock.Now = At(new TimeSpan(15, 0, 1));
        wrongDay.Scheduler.Tick();
        await AssertNoFiresAsync(wrongDay);

        // …while a schedule whose mask includes Wednesday fires.
        var rightDay = BuildHarness(
            new CueSchedule
            {
                Kind = CueScheduleKind.Recurring,
                TimeOfDay = new TimeOnly(15, 0),
                Days = DayFlag(BaseDay.DayOfWeek) | CueScheduleDays.Saturday,
            },
            start: At(new TimeSpan(14, 59, 50)));
        rightDay.Clock.Now = At(new TimeSpan(15, 0, 1));
        rightDay.Scheduler.Tick();
        Assert.Equal(rightDay.CueId, await NextFiredAsync(rightDay));
    }

    [Fact]
    public async Task Scheduler_OneShot_FiresOncePerSession()
    {
        // One-shot At must carry the MACHINE-local offset: the VM maps it into local wall time,
        // which is the clock domain the scheduler compares against (keeps the test TZ-independent).
        var wall = BaseDay + new TimeSpan(15, 0, 0);
        var h = BuildHarness(
            new CueSchedule
            {
                Kind = CueScheduleKind.DateTime,
                At = new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)),
            },
            start: At(new TimeSpan(14, 59, 50)));

        h.Clock.Now = At(new TimeSpan(15, 0, 1));
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Never again - not later, not the next day.
        h.Scheduler.Tick();
        h.Clock.Now = At(new TimeSpan(15, 0, 1), dayOffset: 1);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);

        // Re-arming clears the session fired state, but the "from now" baseline means a PAST
        // one-shot still cannot re-fire - it only could if its instant were still ahead.
        h.Vm.SchedulesArmed = false;
        h.Vm.SchedulesArmed = true;
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
    }

    [Fact]
    public async Task Scheduler_DisabledSchedule_NeverFires()
    {
        var h = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0), Enabled = false },
            start: At(new TimeSpan(14, 59, 50)));

        h.Clock.Now = At(new TimeSpan(15, 0, 1));
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
        Assert.Null(h.CueVm.ScheduleCountdownText); // no countdown while disabled
        Assert.EndsWith("(off)", h.CueVm.ScheduleBadgeDisplay);
    }

    [Fact]
    public void Scheduler_Countdown_AndClockBadge_ShowOnTheRow()
    {
        var h = BuildHarness(
            new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0) },
            start: At(new TimeSpan(14, 58, 30)));

        h.Scheduler.Tick();
        Assert.Equal("in 01:30", h.CueVm.ScheduleCountdownText);
        // The badge rides the tree's Target column (the Jump-target display precedent).
        Assert.Contains("⏰ 15:00", h.CueVm.TargetDisplay);
        Assert.Contains("in 01:30", h.CueVm.TargetDisplay);
    }
}
