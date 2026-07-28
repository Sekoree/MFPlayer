using System.ComponentModel;
using Avalonia.Threading;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.ViewModels;

namespace HaPlay.Services;

/// <summary>
/// Central wall-clock scheduler for timed cue triggers (Ideas/CuePlayer-Enhancements.md §4). ONE
/// <see cref="DispatcherTimer"/> (~250 ms) sweeps every scheduled cue in the selected list - never
/// per-cue timers. All comparisons happen in LOCAL wall time (shows are local-time creatures); a
/// DST-skipped/duplicated hour simply follows the OS clock.
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

    private DispatcherTimer? _timer;
    private DateTime _armedBaselineWall;
    private DateTime _lastPruneWall;
    private bool _wasArmed;
    private bool _disposed;

    public CueSchedulerService(CuePlayerViewModel cuePlayer, Func<DateTimeOffset>? now = null)
    {
        _cuePlayer = cuePlayer ?? throw new ArgumentNullException(nameof(cuePlayer));
        _now = now ?? (() => DateTimeOffset.Now);
        _armedBaselineWall = WallNow();
        _lastPruneWall = _armedBaselineWall;
        _wasArmed = cuePlayer.SchedulesArmed;
        _cuePlayer.PropertyChanged += OnCuePlayerPropertyChanged;
    }

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

        foreach (var node in _cuePlayer.EnumerateScheduledCueNodes().ToList())
        {
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

            var lateMs = (nowWall - due).TotalMilliseconds;
            if (lateMs > Math.Max(0, node.ScheduleGraceMs))
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

        PruneHandled(nowWall);
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

    private static string CueRef(CueNodeViewModel node) =>
        string.IsNullOrWhiteSpace(node.Number) ? node.Label : $"{node.Number} {node.Label}".Trim();

    private static void UpdateCountdown(CueNodeViewModel node, DateTime nowWall)
    {
        string? text = null;
        if (node.ScheduleEnabled && NextDueOccurrence(node, nowWall) is { } next)
        {
            var remaining = next - nowWall;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            text = remaining.TotalHours >= 1
                ? $"in {(long)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                : $"in {remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }
        node.ScheduleCountdownText = text;
    }

    /// <summary>The most recent occurrence at or before <paramref name="nowWall"/>, or null. Local
    /// wall time throughout; recurring kinds resolve against each day's wall clock (DST follows the
    /// OS clock by construction).</summary>
    internal static DateTime? LatestDueOccurrence(CueNodeViewModel node, DateTime nowWall)
    {
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
        _cuePlayer.PropertyChanged -= OnCuePlayerPropertyChanged;
    }
}
