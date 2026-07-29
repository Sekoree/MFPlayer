using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.Resources;
using HaPlay.Services;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// MTC chase → scheduler (Ideas/Next-Round-Plan-2026-07-28.md D1): the <c>CueScheduleKind.Timecode</c>
/// persistence (including the STJ init-property gotcha - a minimal <c>"schedule":{}</c> must keep the
/// 25 fps default), and the <c>CueSchedulerService</c> runtime driven by real
/// <c>ControlMonitorRecord</c>s off the always-on device-input seam with a hand-advanced tick source.
/// The firing semantics asserted here are deliberately the SAME ones the wall-clock path has: armed
/// gate, edit-mode suppression, grace window with the two-sweep floor, fire-once per occurrence and
/// no backlog catch-up - only the clock differs.
/// </summary>
public sealed class CueTimecodeScheduleTests
{
    // ---- Persistence ----

    [Fact]
    public void TimecodeSchedule_RoundTrips_ThroughViewModelAndJson()
    {
        var node = new MediaCueNode
        {
            Number = "1",
            Label = "Act 2 sting",
            Source = new FilePlaylistItem("/tmp/sting.wav"),
            Schedule = new CueSchedule
            {
                Kind = CueScheduleKind.Timecode,
                Timecode = "01:00:00:00",
                TimecodeRate = CueTimecodeRate.Fps30,
                GraceMs = 2000,
            },
        };

        var vm = CueNodeViewModel.FromModel(node);
        Assert.True(vm.HasSchedule);
        Assert.True(vm.IsScheduleTimecode);
        Assert.False(vm.IsScheduleWallClock);
        Assert.Equal("01:00:00:00", vm.ScheduleTimecodeText);
        Assert.Equal(CueTimecodeRate.Fps30, vm.ScheduleTimecodeRate);

        var back = Assert.IsType<MediaCueNode>(vm.ToModel());
        Assert.Equal(node.Schedule, back.Schedule);

        var json = JsonSerializer.Serialize(new CueList { Nodes = [back] }, CueListJsonContext.Default.CueList);
        var reloaded = Assert.IsType<MediaCueNode>(
            JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!.Nodes[0]);
        Assert.Equal(back.Schedule, reloaded.Schedule);
    }

    [Fact]
    public void LegacyFiles_LoadUnchanged_AndMinimalScheduleKeepsTheTimecodeDefaults()
    {
        // Pre-D1 files carry no timecode fields at all.
        var legacy = """{"nodes":[{"kind":"media","label":"Old","schedule":{"kind":0,"graceMs":5000,"enabled":true}}]}""";
        var schedule = Assert.IsType<CueSchedule>(
            Assert.IsType<MediaCueNode>(
                JsonSerializer.Deserialize(legacy, CueListJsonContext.Default.CueList)!.Nodes[0]).Schedule);
        Assert.Null(schedule.Timecode);
        Assert.Equal(CueTimecodeRate.Fps25, schedule.TimecodeRate);
        Assert.Equal(CueScheduleKind.TimeOfDay, schedule.Kind);

        // The source-gen gotcha: an EMPTY schedule object must keep the property initializers (set, not
        // init), so the 25 fps default survives instead of collapsing to the CLR default (24).
        var minimal = """{"nodes":[{"kind":"media","label":"New","schedule":{}}]}""";
        var minimalSchedule = Assert.IsType<CueSchedule>(
            Assert.IsType<MediaCueNode>(
                JsonSerializer.Deserialize(minimal, CueListJsonContext.Default.CueList)!.Nodes[0]).Schedule);
        Assert.Equal(CueTimecodeRate.Fps25, minimalSchedule.TimecodeRate);
        Assert.Null(minimalSchedule.Timecode);
    }

    [Fact]
    public void TimecodeKind_IsAppendedLast_SoPersistedOrdinalsAreStable()
    {
        // Enum values persist as NUMBERS through the source-generated contract: the three wall-clock
        // kinds must keep the ordinals older projects wrote.
        Assert.Equal(0, (int)CueScheduleKind.TimeOfDay);
        Assert.Equal(1, (int)CueScheduleKind.DateTime);
        Assert.Equal(2, (int)CueScheduleKind.Recurring);
        Assert.Equal(3, (int)CueScheduleKind.Timecode);
    }

    [Fact]
    public void TimecodeText_RejectsGarbage_AndReValidatesWhenTheRateChanges()
    {
        var vm = CueNodeViewModel.FromModel(new MediaCueNode { Label = "Q" });
        vm.ScheduleTimecodeRate = CueTimecodeRate.Fps30;
        vm.ScheduleTimecodeText = "01:00:00:29";
        Assert.Equal("01:00:00:29", vm.ScheduleTimecodeText);

        // Unparseable text leaves the last valid value alone (the LostFocus contract).
        vm.ScheduleTimecodeText = "nonsense";
        Assert.Equal("01:00:00:29", vm.ScheduleTimecodeText);

        // 29 frames is legal at 30 fps and illegal at 25: the frame field is clamped rather than kept
        // out of range (an unparseable target would silently never fire).
        vm.ScheduleTimecodeRate = CueTimecodeRate.Fps25;
        Assert.Equal("01:00:00:24", vm.ScheduleTimecodeText);
        Assert.NotNull(vm.ScheduleTimecodeValue);

        vm.ScheduleTimecodeText = "";
        Assert.Null(vm.ScheduleTimecodeValue);
    }

    // ---- Runtime harness ----

    private sealed class TickSource
    {
        public long Now;
    }

    private sealed record Harness(
        CuePlayerViewModel Vm,
        CueSchedulerService Scheduler,
        CueTimecodeChaseService Chase,
        TickSource Ticks,
        CueNodeViewModel CueVm,
        Guid CueId,
        ConcurrentQueue<Guid> Fired,
        SemaphoreSlim FireSignal);

    /// <summary>Microsecond ticks - every timing assertion below is then plain arithmetic.</summary>
    private const long TicksPerSecond = 1_000_000;

    private static long Ms(double milliseconds) => (long)(milliseconds * 1000.0);

    private static Harness BuildHarness(CueSchedule schedule, bool arm = true)
    {
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Chased cue",
            Source = new FilePlaylistItem("/tmp/song.wav"),
            Schedule = schedule,
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [cue] }]);
        vm.IsCueEditMode = false;

        var fired = new ConcurrentQueue<Guid>();
        var signal = new SemaphoreSlim(0);
        vm.MediaCueExecutor = (m, _) =>
        {
            vm.OnCueStarted(m.Id);
            fired.Enqueue(m.Id);
            signal.Release();
            return Task.FromResult<string?>(null);
        };

        var ticks = new TickSource();
        var chase = new CueTimecodeChaseService(ticks: () => ticks.Now, ticksPerSecond: TicksPerSecond);
        // The wall clock is pinned: nothing here should depend on it, and a moving one would make the
        // wall-path branches of the sweep non-deterministic.
        var scheduler = new CueSchedulerService(
            vm, () => new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero), chase);
        if (arm)
            vm.SchedulesArmed = true;

        return new Harness(vm, scheduler, chase, ticks, vm.SelectedCueList!.Nodes[0], cue.Id, fired, signal);
    }

    private static CueSchedule TimecodeSchedule(string target, int graceMs = 5000, bool enabled = true) =>
        new()
        {
            Kind = CueScheduleKind.Timecode,
            Timecode = target,
            TimecodeRate = CueTimecodeRate.Fps25,
            GraceMs = graceMs,
            Enabled = enabled,
        };

    private static async Task<Guid> NextFiredAsync(Harness h)
    {
        Assert.True(await h.FireSignal.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting for a timecode fire");
        Assert.True(h.Fired.TryDequeue(out var id));
        return id;
    }

    private static async Task AssertNoFiresAsync(Harness h)
    {
        await Task.Delay(150);
        Assert.Empty(h.Fired);
    }

    // ---- Wire helpers: real monitor records off the always-on device-input seam ----

    private static ControlMonitorRecord QuarterFrameRecord(byte dataByte) => new()
    {
        Direction = ControlMonitorDirection.Input,
        Protocol = ControlMonitorProtocol.MIDI,
        Result = ControlMonitorResult.Received,
        Endpoint = "MTC In",
        MIDIMessageType = ControlMIDIMessageType.MIDITimeCode,
        // ControlMIDIMessagePayload.FromMIDIMessage puts the quarter-frame DATA byte in Value.
        MIDIValue = dataByte,
    };

    private static ControlMonitorRecord FullFrameRecord(byte[] sysEx) => new()
    {
        Direction = ControlMonitorDirection.Input,
        Protocol = ControlMonitorProtocol.MIDI,
        Result = ControlMonitorResult.Received,
        Endpoint = "MTC In",
        MIDIMessageType = ControlMIDIMessageType.SysEx,
        MIDIValue = sysEx.Length,
        RawBytes = sysEx,
    };

    /// <summary>Pushes one complete 8-message quarter-frame sequence through the chase service,
    /// advancing the tick source by one quarter-frame period (10 ms at 25 fps) per message - so
    /// consecutive calls advance the timecode by exactly 2 frames of real time.</summary>
    private static void FeedTimecode(Harness h, int hours, int minutes, int seconds, int frames)
    {
        Span<byte> bytes =
        [
            (byte)(0x00 | (frames & 0x0F)),
            (byte)(0x10 | ((frames >> 4) & 0x01)),
            (byte)(0x20 | (seconds & 0x0F)),
            (byte)(0x30 | ((seconds >> 4) & 0x03)),
            (byte)(0x40 | (minutes & 0x0F)),
            (byte)(0x50 | ((minutes >> 4) & 0x03)),
            (byte)(0x60 | (hours & 0x0F)),
            (byte)(0x70 | ((hours >> 4) & 0x01) | ((int)MidiTimecodeRate.Fps25 << 1)),
        ];
        foreach (var b in bytes)
        {
            h.Ticks.Now += Ms(10);
            Assert.True(h.Chase.OnControlInput(QuarterFrameRecord(b)));
        }
    }

    /// <summary>Rolls the chase forward from <paramref name="startFrame"/> for a number of assemblies
    /// (one per 2 frames), the way a deck rolling in real time looks on the wire.</summary>
    private static void Roll(Harness h, MidiTimecodeValue start, int assemblies)
    {
        var frame = start.FrameNumber;
        for (var i = 0; i < assemblies; i++)
        {
            var tc = MidiTimecodeValue.FromFrameNumber(frame, MidiTimecodeRate.Fps25);
            FeedTimecode(h, tc.Hours, tc.Minutes, tc.Seconds, tc.Frames);
            frame += 2;
        }
    }

    private static void Locate(Harness h, int hours, int minutes, int seconds, int frames)
    {
        h.Ticks.Now += Ms(10);
        byte[] sysEx =
        [
            0xF0, 0x7F, 0x7F, 0x01, 0x01,
            (byte)(((int)MidiTimecodeRate.Fps25 << 5) | (hours & 0x1F)),
            (byte)minutes, (byte)seconds, (byte)frames,
            0xF7,
        ];
        Assert.True(h.Chase.OnControlInput(FullFrameRecord(sysEx)));
    }

    // ---- Scheduler runtime ----

    [Fact]
    public async Task Scheduler_FiresExactlyOnce_WhenTheChaseCrossesTheTarget()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Scheduler.Tick(); // the sweep is what turns decoding on - see the default-off test below
        Assert.True(h.Chase.Enabled);

        FeedTimecode(h, 0, 59, 59, 23); // lock, 2 frames short of the target
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);

        FeedTimecode(h, 1, 0, 0, 0); // crossed
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));
        // (No StatusMessage assertion on the fire path: the GO run stamps its own status once it gets
        // going, so which of the two lands last is a race - the wall-clock tests avoid it the same way.)

        // Never twice in the same pass, however long the sender keeps rolling past it.
        Roll(h, new MidiTimecodeValue(1, 0, 0, 2, MidiTimecodeRate.Fps25), 5);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
    }

    [Fact]
    public async Task Scheduler_BeyondGrace_SkipsAndLogs_NeverCatchesUp()
    {
        // GraceMs 0 still gets the two-sweep floor (500 ms), exactly like the wall-clock path…
        var h = BuildHarness(TimecodeSchedule("01:00:00:00", graceMs: 0));
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        FeedTimecode(h, 1, 0, 0, 0); // 70 ms past the target when observed
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // …but a target the sweep only sees seconds later (edit-mode window, a stalled sweep) is
        // skipped with a log rather than fired late.
        var late = BuildHarness(TimecodeSchedule("01:00:00:00", graceMs: 0));
        late.Scheduler.Tick();
        Roll(late, new MidiTimecodeValue(0, 59, 59, 23, MidiTimecodeRate.Fps25), 60); // rolls ~5 s past
        late.Scheduler.Tick();
        await AssertNoFiresAsync(late);
        Assert.Contains("beyond grace", late.Vm.StatusMessage ?? string.Empty);

        // Logged once, not per sweep.
        late.Vm.StatusMessage = null;
        late.Scheduler.Tick();
        Assert.Null(late.Vm.StatusMessage);
    }

    [Fact]
    public async Task Scheduler_LocatePastTheTarget_NeverFiresABurst()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        h.Scheduler.Tick();

        // The operator locates the source PAST the target and rolls from there. The new run's baseline
        // retires everything behind it - silently, not as a burst and not as a pile of skip logs.
        Locate(h, 1, 0, 5, 0);
        h.Scheduler.Tick();
        Assert.Null(h.Vm.StatusMessage);
        Roll(h, new MidiTimecodeValue(1, 0, 5, 0, MidiTimecodeRate.Fps25), 4);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
    }

    [Fact]
    public async Task Scheduler_RewindAndReplay_ReArmsTheTarget()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        FeedTimecode(h, 1, 0, 0, 0);
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Rewind: a new pass over the same target must fire it again - that is what a rehearsal is.
        Locate(h, 0, 59, 55, 0);
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        FeedTimecode(h, 1, 0, 0, 0);
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    /// <summary>Mirror of the wall path's <c>due &lt;= _armedBaselineWall</c>: an occurrence exactly ON
    /// the baseline is dead by definition. Along the chase the baseline is the run's START POSITION, so
    /// locating ONTO a target is not crossing it - the chase test used a strict <c>&lt;</c> and fired
    /// it, which is also how a locate that lands exactly on a cue produced a fire the wall path would
    /// never have made.</summary>
    [Fact]
    public async Task Scheduler_TargetExactlyOnTheGenerationStart_IsRetiredLikeTheWallBaseline()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        h.Scheduler.Tick();

        // The operator locates EXACTLY onto the target and rolls from there.
        Locate(h, 1, 0, 0, 0);
        h.Scheduler.Tick();
        Roll(h, new MidiTimecodeValue(1, 0, 0, 0, MidiTimecodeRate.Fps25), 4);
        h.Scheduler.Tick();

        await AssertNoFiresAsync(h);
    }

    /// <summary>
    /// Fire-once has to survive a generation change, exactly as the wall path's handled set survives
    /// every clock event (it is age-pruned, never dropped wholesale). The chase position is a wall-time
    /// extrapolation that leads the last decoded label by a few frames, so a dropout right after a
    /// crossing re-baselines the run just BEHIND the target that already fired - nothing rewound, yet
    /// the wholesale clear re-armed it and it fired a second time.
    /// </summary>
    [Fact]
    public async Task Scheduler_GenerationBumpJustBehindAFiredTarget_DoesNotFireItTwice()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:03"));
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 24);
        FeedTimecode(h, 1, 0, 0, 0);
        h.Ticks.Now += Ms(90); // interpolation carries the position past the target between assemblies
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // A dropout long enough to open a new run; the sender resumes 2 frames behind where the
        // interpolated position had already got to.
        h.Ticks.Now += Ms(600);
        FeedTimecode(h, 1, 0, 0, 2);
        Roll(h, new MidiTimecodeValue(1, 0, 0, 4, MidiTimecodeRate.Fps25), 3);
        h.Scheduler.Tick();

        await AssertNoFiresAsync(h);
    }

    [Fact]
    public async Task Scheduler_StoppedSender_FiresNothing_AndClearsTheCountdown()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        h.Scheduler.Tick();
        Assert.NotNull(h.CueVm.ScheduleCountdownText);

        // Sender switched off just short of the target: the clock freezes rather than free-wheeling
        // through it, so nothing fires however long we wait.
        h.Ticks.Now += Ms(30_000);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
        Assert.Null(h.CueVm.ScheduleCountdownText);
        Assert.False(h.Chase.Read().IsChasing);
        Assert.Contains("stopped", h.Vm.TimecodeChaseStatus ?? string.Empty);
    }

    [Fact]
    public async Task Scheduler_DisarmedGate_NeverFires_AndArmingIsFromTheCurrentPosition()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"), arm: false);
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        FeedTimecode(h, 1, 0, 0, 0);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
        Assert.Null(h.Vm.StatusMessage); // a closed gate neither fires nor spams skip logs

        // Arming mid-roll is "from HERE": a target the source has already passed stays dead.
        h.Vm.SchedulesArmed = true;
        Roll(h, new MidiTimecodeValue(1, 0, 0, 2, MidiTimecodeRate.Fps25), 4);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
    }

    [Fact]
    public async Task Scheduler_EditMode_SuppressesFires()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Vm.IsCueEditMode = true;
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        FeedTimecode(h, 1, 0, 0, 0);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);

        // Leaving edit mode inside the grace window still honors the crossing (the misfire policy).
        h.Vm.IsCueEditMode = false;
        h.Scheduler.Tick();
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Scheduler_DisabledSchedule_NeverFires()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00", enabled: false));
        h.Scheduler.Tick();
        FeedTimecode(h, 0, 59, 59, 23);
        FeedTimecode(h, 1, 0, 0, 0);
        h.Scheduler.Tick();
        await AssertNoFiresAsync(h);
        Assert.Null(h.CueVm.ScheduleCountdownText);
        Assert.EndsWith("(off)", h.CueVm.ScheduleBadgeDisplay);
    }

    [Fact]
    public void Chase_IsOff_AndDecodesNothing_UntilSomeListWantsTimecode()
    {
        // A project with only wall-clock schedules must never decode: the record is still swallowed
        // (a quarter-frame can never match a cue trigger) but nothing is parsed or anchored.
        var h = BuildHarness(new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0) });
        h.Scheduler.Tick();
        Assert.False(h.Chase.Enabled);

        FeedTimecode(h, 1, 0, 0, 0);
        Assert.False(h.Chase.Read().HasSignal);
        h.Scheduler.Tick();
        Assert.Null(h.Vm.TimecodeChaseStatus);
        Assert.False(h.Vm.HasTimecodeChaseStatus);
    }

    [Fact]
    public void Chase_SurfacesTheSenderState_OnTheRowBadgeAndTheArmTooltip()
    {
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Scheduler.Tick();
        Assert.Equal(Strings.CueTimecodeChaseNoSignalStatus, h.Vm.TimecodeChaseStatus);

        FeedTimecode(h, 0, 59, 58, 0);
        h.Scheduler.Tick();
        var status = h.Vm.TimecodeChaseStatus ?? string.Empty;
        Assert.Contains("00:59:58", status);
        Assert.Contains("25", status);
        Assert.True(h.Vm.HasTimecodeChaseStatus);
        // The armed toggle's tooltip carries the same line, so it is reachable without the chip.
        Assert.Contains(status, h.Vm.SchedulesArmedTooltip);

        // Row badge + live distance to the target ride the tree's Target column, like the clock badge.
        Assert.Contains("⏱ 01:00:00:00 @25", h.CueVm.TargetDisplay);
        Assert.Equal("in 00:01", h.CueVm.ScheduleCountdownText);
    }

    // ---- View realisation ----

    [Fact]
    public void Drawer_RealisesTheTimecodeFields_AndTheTransportChip()
    {
        // Lays the REAL CuePlayerView out with a timecode-scheduled cue selected: catches a broken
        // binding path or a missing resource in the new drawer rows / transport chip, which no
        // view-model test can see.
        DispatchUi(() =>
                {
                    HeadlessAppTheme.ApplyProductionBaseTheme();
                    var vm = new CuePlayerViewModel();
                    vm.ApplyCueLists([
                        new CueList
                        {
                            Nodes =
                            [
                                new MediaCueNode
                                {
                                    Number = "1",
                                    Label = "Chased cue",
                                    Source = new FilePlaylistItem("/tmp/song.wav"),
                                    Schedule = TimecodeSchedule("01:00:00:00"),
                                },
                            ],
                        },
                    ]);
                    vm.SelectedCueNode = vm.SelectedCueList!.Nodes[0];
                    vm.TimecodeChaseStatus = "Timecode 01:00:00:00 @ 25 fps";

                    var window = new Window { Width = 1280, Height = 900, Content = new CuePlayerView { DataContext = vm } };
                    try
                    {
                        window.Show();
                        Dispatcher.UIThread.RunJobs();

                        // IsEffectivelyVisible, not just presence: the wall-clock rows share the same
                        // grid cells and stay in the tree, collapsed, when the chase kind is selected.
                        var texts = window.GetVisualDescendants().OfType<TextBlock>()
                            .Where(t => t.IsEffectivelyVisible)
                            .Select(t => t.Text ?? string.Empty).ToList();
                        Assert.Contains(Strings.CueScheduleTimecodeLabel, texts);
                        Assert.Contains(Strings.CueScheduleTimecodeRateLabel, texts);
                        Assert.Contains(vm.TimecodeChaseStatus, texts);
                        Assert.DoesNotContain(Strings.CueScheduleTimeLabel, texts);
                        Assert.DoesNotContain(Strings.CueScheduleDateLabel, texts);

                        var boxes = window.GetVisualDescendants().OfType<TextBox>()
                            .Where(t => t.IsEffectivelyVisible)
                            .Select(t => t.Text ?? string.Empty).ToList();
                        Assert.Contains("01:00:00:00", boxes);
                    }
                    finally
                    {
                        window.Close();
                    }
                });
    }

    /// <summary>Runs a synchronous body on the headless UI session and OBSERVES its result (discarding
    /// the Task would throw every assertion failure inside the body away - the
    /// <c>CuePlayerViewInteractionTests.DispatchUi</c> precedent).</summary>
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CueTimecodeScheduleTests).Assembly)
            .Dispatch(action, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    [Fact]
    public void Chase_IgnoresASecondSource_WhileTheFirstIsTalking()
    {
        // Two Control devices bound to the same physical port produce two records per message, which
        // would break quarter-frame sequencing outright. The first source latches.
        var h = BuildHarness(TimecodeSchedule("01:00:00:00"));
        h.Scheduler.Tick();

        Span<byte> bytes =
        [
            0x00, 0x10, 0x20, 0x30, 0x40, 0x50, 0x61, 0x72,
        ]; // 01:00:00:00 @ 25 fps
        foreach (var b in bytes)
        {
            h.Ticks.Now += Ms(10);
            h.Chase.OnControlInput(QuarterFrameRecord(b));
            // A duplicate of the same message from a DIFFERENT device instance, interleaved.
            var duplicate = QuarterFrameRecord(b) with { DeviceInstanceId = Guid.NewGuid() };
            h.Chase.OnControlInput(duplicate);
        }

        var state = h.Chase.Read();
        Assert.True(state.HasSignal); // the sequence assembled despite the interleaved duplicates
        var target = new MidiTimecodeValue(1, 0, 0, 0, MidiTimecodeRate.Fps25).TotalSeconds;
        Assert.InRange(state.PositionSeconds, target, target + 0.2); // + the interpolated read lag
    }
}
