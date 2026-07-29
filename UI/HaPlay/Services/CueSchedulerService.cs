using System.ComponentModel;
using Avalonia.Threading;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.ViewModels;
using S.Control;

namespace HaPlay.Services;

/// <summary>
/// Central wall-clock scheduler for timed cue triggers (Ideas/CuePlayer-Enhancements.md §4). ONE
/// <see cref="DispatcherTimer"/> (~250 ms) sweeps every scheduled cue in EVERY loaded list - never
/// per-cue timers. All comparisons happen in LOCAL wall time (shows are local-time creatures); a
/// DST-skipped/duplicated hour simply follows the OS clock.
/// <para><b>Scope</b>: all lists (the cross-list merged session). A cue in a non-selected list fires
/// into the same <see cref="S.Media.Session.ShowSession"/> through a headless run - its pre-waits,
/// group modes and playlist picks resolve in its OWN list, and the visible GO/standby transport is
/// left exactly where the operator put it.</para>
/// <para><b>Armed gate</b>: occurrences fire only while the master
/// <see cref="CuePlayerViewModel.SchedulesArmed"/> toggle is ON (session-scoped, defaults OFF) and
/// <see cref="CuePlayerViewModel.IsCueEditMode"/> is OFF. Arming resets the baseline to "now":
/// occurrences that passed while disarmed never fire retroactively.</para>
/// <para><b>Misfire policy</b>: an occurrence due within <c>[due, due + GraceMs]</c> fires exactly
/// once; anything older is skipped with a status log - a backlog (app sleep/suspend) is never caught
/// up. A per-(cue, occurrence) handled set guarantees a fire can't double-trigger inside its own
/// grace window.</para>
/// <para><b>One-shots</b>: a <see cref="CueScheduleKind.DateTime"/> schedule fires once per session.
/// It re-arms only when the operator toggles Schedules armed off/on (which clears the session fired
/// state) or the app restarts - and even then only fires again if its instant is still ahead of the
/// re-arm moment.</para>
/// <para><b>Timecode chase</b> (Ideas/Next-Round-Plan-2026-07-28.md D1): a
/// <see cref="CueScheduleKind.Timecode"/> schedule runs on the incoming MTC chase clock
/// (<see cref="TimecodeChase"/>) instead of wall time, with the SAME semantics throughout - armed
/// gate, grace window (same two-sweep floor), fire-once per occurrence, no backlog catch-up, all
/// loaded lists, and the identical <see cref="CuePlayerViewModel.FireScheduledCueAsync"/> fire path.
/// The chase clock's generation stands in for "today": a relocate starts a new generation whose
/// baseline retires every target behind the new position, so a locate can never fire a burst, and
/// re-winding the source re-arms the targets ahead of it. Nothing fires while the sender is stopped
/// (the clock freezes rather than free-wheeling).</para>
/// <para>Fires run through <see cref="CuePlayerViewModel.FireScheduledCueAsync"/> - the exact
/// operator-selected GO path (immediate-jump-chain reset included) - and are already on the UI
/// thread because the timer ticks there. The clock is injectable for tests, which drive
/// <see cref="Tick"/> directly without starting the timer.</para>
/// </summary>
public sealed class CueSchedulerService : IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Handled entries older than this are pruned (memory bound). Long enough that a
    /// beyond-grace skip can never be re-logged in any realistic session.</summary>
    private static readonly TimeSpan HandledRetention = TimeSpan.FromDays(7);

    private readonly CuePlayerViewModel _cuePlayer;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Session fired/skipped state, keyed by (cue id, occurrence wall ticks). One entry per
    /// occurrence guarantees a fire never double-triggers within its grace window and a missed
    /// occurrence is only logged once.</summary>
    private readonly HashSet<(Guid CueId, long OccurrenceTicks)> _handled = new();

    /// <summary>The same fire-once state for timecode targets, keyed by (cue id, target frame). Its
    /// own set because its key space is chase frames, not wall ticks - and because a generation change
    /// clears it wholesale (a new run re-arms everything ahead of its baseline), which is a cleaner
    /// bound than the wall path's age-based prune.</summary>
    private readonly HashSet<(Guid CueId, long TargetFrame)> _handledTimecode = new();

    private DispatcherTimer? _timer;
    private DateTime _armedBaselineWall;
    private DateTime _lastPruneWall;
    private bool _wasArmed;
    private bool _disposed;

    /// <summary>Chase run this sweep last saw, and the position that run started at. Any target
    /// earlier than the baseline is retired unfired - the "no burst after a locate" rule.</summary>
    private int _chaseGeneration;
    private double _chaseBaselineSeconds;
    private bool _haveChaseBaseline;

    public CueSchedulerService(
        CuePlayerViewModel cuePlayer,
        Func<DateTimeOffset>? now = null,
        CueTimecodeChaseService? timecodeChase = null)
    {
        _cuePlayer = cuePlayer ?? throw new ArgumentNullException(nameof(cuePlayer));
        _now = now ?? (() => DateTimeOffset.Now);
        TimecodeChase = timecodeChase ?? new CueTimecodeChaseService();
        _armedBaselineWall = WallNow();
        _lastPruneWall = _armedBaselineWall;
        _wasArmed = cuePlayer.SchedulesArmed;
        _cuePlayer.PropertyChanged += OnCuePlayerPropertyChanged;
    }

    /// <summary>The MTC chase source. The host feeds it from the always-on device-input event on the
    /// I/O thread; this sweep only ever reads a snapshot. Enabled by the sweep itself, purely on
    /// "does any loaded list carry a Timecode schedule" - a project without one never decodes.</summary>
    public CueTimecodeChaseService TimecodeChase { get; }

    /// <summary>Starts the UI-thread sweep timer. Tests skip this and call <see cref="Tick"/> directly.</summary>
    public void Start()
    {
        if (_disposed || _timer is not null)
            return;
        // The DispatcherTimer(…, callback) ctor starts enabled - the EndpointHealthMonitor precedent.
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => Tick());
    }

    private void OnCuePlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CuePlayerViewModel.SchedulesArmed))
            return;
        var armed = _cuePlayer.SchedulesArmed;
        if (armed && !_wasArmed)
        {
            // Arming means "schedule from NOW": occurrences that passed while disarmed stay dead
            // (baseline), and one-shots that already fired this session re-arm (handled reset) -
            // they can only fire again if their instant is still ahead of this moment.
            _armedBaselineWall = WallNow();
            _handled.Clear();
            // Same rule along the chase: arming mid-roll must not fire everything the timecode has
            // already passed, so the baseline moves to wherever the chase is standing right now.
            _handledTimecode.Clear();
            var chase = TimecodeChase.Read();
            _chaseGeneration = chase.Generation;
            _chaseBaselineSeconds = chase.HasSignal ? chase.PositionSeconds : 0;
            _haveChaseBaseline = chase.HasSignal;
        }
        _wasArmed = armed;
    }

    /// <summary>Local wall-clock reading of the injected clock (production: DateTimeOffset.Now).</summary>
    private DateTime WallNow() => _now().DateTime;

    /// <summary>One sweep over every scheduled cue: refresh row countdowns, then - while the armed
    /// gate is open - resolve each schedule's latest due occurrence and apply the misfire policy.</summary>
    public void Tick()
    {
        if (_disposed)
            return;

        var nowWall = WallNow();
        var gateOpen = _cuePlayer.SchedulesArmed && !_cuePlayer.IsCueEditMode;
        // ONE chase snapshot per sweep: every timecode row compares against the same instant, exactly
        // as every wall-clock row compares against the same nowWall.
        var chase = TimecodeChase.Read();
        SyncChaseGeneration(chase);
        var anyTimecode = false;

        foreach (var node in _cuePlayer.EnumerateScheduledCueNodes().ToList())
        {
            if (node.ScheduleKind == CueScheduleKind.Timecode)
            {
                anyTimecode = true;
                UpdateTimecodeCountdown(node, chase);
                if (gateOpen && node.ScheduleEnabled)
                    TickTimecode(node, chase);
                continue;
            }

            UpdateCountdown(node, nowWall);

            if (!gateOpen || !node.ScheduleEnabled)
                continue;
            if (LatestDueOccurrence(node, nowWall) is not { } due)
                continue;
            if (due <= _armedBaselineWall)
            {
                // Pre-arm occurrence: dead by definition, silently retired.
                _handled.Add((node.Id, due.Ticks));
                continue;
            }
            if (!_handled.Add((node.Id, due.Ticks)))
                continue; // already fired (or skipped) this occurrence

            // Effective grace is floored at two sweep periods (see MinimumGraceMs): a user-entered
            // grace below that (0 in particular) would otherwise mean "never fires" instead of
            // "fire at the next tick".
            var lateMs = (nowWall - due).TotalMilliseconds;
            if (lateMs > Math.Max(MinimumGraceMs, node.ScheduleGraceMs))
            {
                // Beyond grace (app sleep / edit-mode window / long stall): skip and log, never
                // catch up a backlog of missed fires.
                _cuePlayer.StatusMessage = Strings.Format(
                    nameof(Strings.CueScheduleMissedStatusFormat), CueRef(node), due);
                continue;
            }

            // Kick the fire first (its synchronous head sets the generic GO status), then stamp the
            // schedule-specific status so the operator sees WHY the cue started.
            var fire = FireSafeAsync(node);
            _cuePlayer.StatusMessage = Strings.Format(
                nameof(Strings.CueScheduleFiredStatusFormat), CueRef(node));
            _ = fire;
        }

        // Zero cost when unused: the decoder only runs while some loaded list actually wants timecode.
        TimecodeChase.Enabled = anyTimecode;
        UpdateChaseStatus(chase, anyTimecode);
        PruneHandled(nowWall);
    }

    /// <summary>Two sweep periods - the sweep itself observes a crossing 0..TickInterval late, so a
    /// user-entered grace below that would mean "never fires" instead of "fire at the next tick".</summary>
    private static double MinimumGraceMs => 2 * TickInterval.TotalMilliseconds;

    // ----- Timecode chase (Ideas/Next-Round-Plan-2026-07-28.md D1) --------------------------------

    /// <summary>Adopts a new chase run: its start position becomes the baseline and the fired set is
    /// dropped, so targets behind the new position are retired unfired (no burst after a locate) and
    /// targets ahead of it are armed again (rewind-and-replay re-fires, as an operator expects).</summary>
    private void SyncChaseGeneration(in MidiTimecodeChaseState chase)
    {
        if (_haveChaseBaseline && chase.Generation == _chaseGeneration)
            return;
        _chaseGeneration = chase.Generation;
        _chaseBaselineSeconds = chase.GenerationStartSeconds;
        _haveChaseBaseline = true;
        _handledTimecode.Clear();
    }

    /// <summary>One timecode row, with the wall path's misfire policy applied along the chase clock.</summary>
    private void TickTimecode(CueNodeViewModel node, in MidiTimecodeChaseState chase)
    {
        // A stopped/absent sender freezes the clock; crossing tests only make sense while it rolls.
        if (!chase.IsChasing)
            return;
        if (node.ScheduleTimecodeValue is not { } target)
            return;

        var targetSeconds = target.TotalSeconds;
        if (chase.PositionSeconds < targetSeconds)
            return; // not reached yet
        if (targetSeconds < _chaseBaselineSeconds)
        {
            // Behind where this run started: dead by definition (the wall path's pre-arm rule).
            _handledTimecode.Add((node.Id, target.FrameNumber));
            return;
        }
        if (!_handledTimecode.Add((node.Id, target.FrameNumber)))
            return; // already fired (or skipped) in this run

        var lateMs = (chase.PositionSeconds - targetSeconds) * 1000.0;
        if (lateMs > Math.Max(MinimumGraceMs, node.ScheduleGraceMs))
        {
            // Beyond grace (edit-mode window, a long sweep stall): skip and log, never catch up.
            _cuePlayer.StatusMessage = Strings.Format(
                nameof(Strings.CueTimecodeMissedStatusFormat), CueRef(node), target.ToString());
            return;
        }

        var fire = FireSafeAsync(node);
        _cuePlayer.StatusMessage = Strings.Format(
            nameof(Strings.CueTimecodeFiredStatusFormat), CueRef(node), target.ToString());
        _ = fire;
    }

    /// <summary>Row countdown for a timecode target: the distance from the chase position to the
    /// target while the sender rolls, nothing while it is stopped (a countdown that is not counting
    /// would read as a live one).</summary>
    private static void UpdateTimecodeCountdown(CueNodeViewModel node, in MidiTimecodeChaseState chase)
    {
        string? text = null;
        if (node.ScheduleEnabled && chase.IsChasing && node.ScheduleTimecodeValue is { } target)
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(0, target.TotalSeconds - chase.PositionSeconds));
            if (remaining > TimeSpan.Zero)
                text = FormatRemaining(remaining);
        }
        node.ScheduleCountdownText = text;
    }

    /// <summary>Operator-visible chase state (feeds the transport row chip and the armed tooltip):
    /// nothing at all unless a Timecode schedule exists, then "receiving / parked / no signal" plus
    /// the sender's timecode and rate.</summary>
    private void UpdateChaseStatus(in MidiTimecodeChaseState chase, bool anyTimecode)
    {
        string? text = null;
        if (anyTimecode)
        {
            var rate = MidiTimecodeRates.Label(chase.Rate);
            text = chase switch
            {
                { IsChasing: true } => Strings.Format(
                    nameof(Strings.CueTimecodeChaseRunningFormat), chase.Position.ToString(), rate),
                { HasSignal: true } => Strings.Format(
                    nameof(Strings.CueTimecodeChaseParkedFormat), chase.Position.ToString(), rate),
                _ => Strings.CueTimecodeChaseNoSignalStatus,
            };
        }

        _cuePlayer.TimecodeChaseStatus = text;
    }

    private async Task FireSafeAsync(CueNodeViewModel node)
    {
        try
        {
            await _cuePlayer.FireScheduledCueAsync(node);
        }
        catch (Exception ex)
        {
            _cuePlayer.StatusMessage = Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat), CueRef(node), ex.Message);
        }
    }

    /// <summary>Operator-facing name for a status line - list-qualified when the cue lives outside the
    /// selected list, because cue numbers restart per list.</summary>
    private string CueRef(CueNodeViewModel node) => _cuePlayer.CueDisplayQualified(node);

    private static void UpdateCountdown(CueNodeViewModel node, DateTime nowWall)
    {
        string? text = null;
        if (node.ScheduleEnabled && NextDueOccurrence(node, nowWall) is { } next)
        {
            var remaining = next - nowWall;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            text = FormatRemaining(remaining);
        }
        node.ScheduleCountdownText = text;
    }

    /// <summary>"in mm:ss" (or "in h:mm:ss" past an hour) - shared by the wall-clock countdown and the
    /// chase distance so a row reads the same whichever clock drives it.</summary>
    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"in {(long)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"in {remaining.Minutes:D2}:{remaining.Seconds:D2}";

    /// <summary>The most recent occurrence at or before <paramref name="nowWall"/>, or null. Local
    /// wall time throughout; recurring kinds resolve against each day's wall clock (DST follows the
    /// OS clock by construction).</summary>
    internal static DateTime? LatestDueOccurrence(CueNodeViewModel node, DateTime nowWall)
    {
        if (node.ScheduleKind == CueScheduleKind.Timecode)
            return null; // chase-driven: it has no wall-clock occurrence at all
        if (node.ScheduleKind == CueScheduleKind.DateTime)
        {
            if (node.ScheduleDate is not { } date || node.ScheduleTimeOfDay is not { } time)
                return null;
            var due = date.ToDateTime(time);
            return due <= nowWall ? due : null;
        }

        if (node.ScheduleTimeOfDay is not { } tod)
            return null;
        for (var back = 0; back <= 7; back++)
        {
            var day = DateOnly.FromDateTime(nowWall.Date).AddDays(-back);
            if (node.ScheduleKind == CueScheduleKind.Recurring && !IsDayEnabled(node.ScheduleDays, day.DayOfWeek))
                continue;
            var due = day.ToDateTime(tod);
            if (due <= nowWall)
                return due;
        }
        return null;
    }

    /// <summary>The next occurrence strictly after <paramref name="nowWall"/>, or null (drives the
    /// row countdown).</summary>
    internal static DateTime? NextDueOccurrence(CueNodeViewModel node, DateTime nowWall)
    {
        if (node.ScheduleKind == CueScheduleKind.Timecode)
            return null; // chase-driven: it has no wall-clock occurrence at all
        if (node.ScheduleKind == CueScheduleKind.DateTime)
        {
            if (node.ScheduleDate is not { } date || node.ScheduleTimeOfDay is not { } time)
                return null;
            var due = date.ToDateTime(time);
            return due > nowWall ? due : null;
        }

        if (node.ScheduleTimeOfDay is not { } tod)
            return null;
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var day = DateOnly.FromDateTime(nowWall.Date).AddDays(ahead);
            if (node.ScheduleKind == CueScheduleKind.Recurring && !IsDayEnabled(node.ScheduleDays, day.DayOfWeek))
                continue;
            var due = day.ToDateTime(tod);
            if (due > nowWall)
                return due;
        }
        return null;
    }

    private static bool IsDayEnabled(CueScheduleDays days, DayOfWeek day) =>
        days.HasFlag(day switch
        {
            DayOfWeek.Monday => CueScheduleDays.Monday,
            DayOfWeek.Tuesday => CueScheduleDays.Tuesday,
            DayOfWeek.Wednesday => CueScheduleDays.Wednesday,
            DayOfWeek.Thursday => CueScheduleDays.Thursday,
            DayOfWeek.Friday => CueScheduleDays.Friday,
            DayOfWeek.Saturday => CueScheduleDays.Saturday,
            _ => CueScheduleDays.Sunday,
        });

    private void PruneHandled(DateTime nowWall)
    {
        // Cheap periodic bound (every ~10 min of wall time) - the set only grows by one entry per
        // occurrence, so this is purely a long-session memory guard.
        if (nowWall - _lastPruneWall < TimeSpan.FromMinutes(10))
            return;
        _lastPruneWall = nowWall;
        var cutoff = (nowWall - HandledRetention).Ticks;
        _handled.RemoveWhere(entry => entry.OccurrenceTicks < cutoff);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_timer is not null)
            _timer.IsEnabled = false;
        _timer = null;
        // Stops the I/O-thread decode as well: the host may keep feeding device input for a moment
        // while the view-model graph tears down.
        TimecodeChase.Enabled = false;
        _cuePlayer.PropertyChanged -= OnCuePlayerPropertyChanged;
    }
}
