using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// The Active panel's state owner (review F-11): the sounding-cue rows, their grouped projection,
/// and the smooth clock that keeps the millisecond digits moving between engine polls.
/// </summary>
/// <remarks>
/// Extracted from <c>CuesViewModel</c> so the reconciliation and extrapolation rules are testable
/// against a fake clock instead of only through the whole shell. The engine is still polled by the
/// owner - <see cref="Poll"/> is handed each poll's rows - but everything that happens to them
/// afterwards lives here. Scope is a view filter, never a transport boundary: this list always
/// shows everything sounding.
/// </remarks>
public sealed class ActivePanelTicker : IDisposable
{
    private readonly ShowRuntime _runtime;
    private readonly Func<HaCueProject> _project;
    private readonly Func<long> _timestamp;
    private DispatcherTimer? _smoothClock;

    /// <param name="timestamp">
    /// The clock the extrapolation reads, in <see cref="Stopwatch"/> ticks. Defaults to the real
    /// one; tests hand in their own so a "moment later" is a value, not a sleep.
    /// </param>
    public ActivePanelTicker(ShowRuntime runtime, Func<HaCueProject> project, Func<long>? timestamp = null)
    {
        _runtime = runtime;
        _project = project;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        ActiveCues = [.. runtime.ActiveCues];
    }

    /// <summary>The raw sounding list, flat because the transport reads it - bare STOP wants "the
    /// one running longest" and the seek wants a cue by id, and neither should have to walk a tree
    /// to find one.</summary>
    public ObservableCollection<ActiveCueRow> ActiveCues { get; }

    /// <summary>
    /// The same cues as the panel shows them, with a group's sounding children gathered under one
    /// header - unless <see cref="FlatList"/> asks for the run list unadorned.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>Shows sounding cues without group headers when the operator prefers a flat run list.</summary>
    public bool FlatList { get; set; }

    /// <summary>
    /// Starts the smooth clock: the engine is polled at 4 Hz, but the Active panel's millisecond
    /// digits tick at UI rate by extrapolating each row from its poll stamp. Corrections land with
    /// every poll, so the readout can never drift more than one poll interval from the truth.
    /// </summary>
    public void Start()
    {
        _smoothClock ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => TickClocks());
        _smoothClock.Start();
    }

    public void Dispose()
    {
        _smoothClock?.Stop();
        _smoothClock = null;
    }

    /// <summary>
    /// Adopts one engine poll: reconciles the raw list, then the grouped projection, both IN PLACE.
    /// </summary>
    /// <remarks>
    /// In place because clear-and-refill raised one CollectionChanged per row four times a second
    /// (and bounced the Active subhead's count through zero on every tick) for a list whose
    /// membership usually has not changed at all - and replacing the rows wholesale replaced the
    /// CONTROLS: the seek bar died mid-drag, the expander and stop buttons lost their hover the
    /// instant the pointer settled on them, and the group header's open/shut state had to be
    /// smuggled across each rebuild. Rows persist now - matched by cue/group identity, their
    /// observable measurements updated, and only genuinely new/gone/reshaped rows change objects.
    /// </remarks>
    public void Poll(IReadOnlyList<ActiveCueRow> fresh)
    {
        SyncActiveCues(fresh);
        RebuildRows();
    }

    /// <summary>
    /// One UI-rate tick of the Active panel's clocks.
    /// </summary>
    /// <remarks>
    /// Extrapolation is gated on the transport actually running: a paused show's clocks must hold
    /// still rather than creep a poll interval ahead and snap back. A fading cue still advances - its
    /// playhead genuinely runs through the ramp. Upcoming countdowns are STAGED: they stay on their
    /// calm whole-second poll text until the start is inside <see cref="CuePresentation.UpcomingPreciseWindow"/>,
    /// then tick their milliseconds here.
    /// </remarks>
    /// <summary>Raised after each smooth-clock tick that ran, with the tick's timestamp - for other
    /// surfaces that extrapolate off the same rows (the timeline sheet's follow mode) without a
    /// second timer that could disagree with this one.</summary>
    public event Action<long>? ClockTicked;

    public void TickClocks()
    {
        if (Rows.Count == 0 || _runtime.IsPaused)
            return;

        var now = _timestamp();

        foreach (var item in Rows)
        {
            switch (item)
            {
                case ActiveCueRow row:
                    TickActiveRow(row, now);
                    break;
                case ActiveGroupRow group:
                {
                    foreach (var child in group.Children)
                        TickActiveRow(child, now);

                    if (group.TotalValue > TimeSpan.Zero)
                    {
                        var remaining = group.RemainingAtPoll - Stopwatch.GetElapsedTime(group.PolledAtTicks, now);
                        if (remaining < TimeSpan.Zero)
                            remaining = TimeSpan.Zero;
                        group.Clock =
                            $"−{CuePresentation.PreciseClock(remaining)} / {CuePresentation.PreciseClock(group.TotalValue)}";
                        group.Progress = Math.Clamp(1 - (remaining / group.TotalValue), 0, 1);
                    }

                    foreach (var upcoming in group.Upcoming)
                    {
                        var starts = upcoming.StartsInAtPoll - Stopwatch.GetElapsedTime(upcoming.PolledAtTicks, now);
                        if (starts < TimeSpan.Zero)
                            starts = TimeSpan.Zero;
                        if (starts <= CuePresentation.UpcomingPreciseWindow)
                            upcoming.Countdown = CuePresentation.UpcomingCountdown(starts);
                    }

                    break;
                }
            }
        }

        ClockTicked?.Invoke(now);
    }

    /// <summary>
    /// A row's playhead extrapolated to <paramref name="now"/> - the ONE extrapolation rule, exposed
    /// for surfaces that follow the same rows off <see cref="ClockTicked"/> (the timeline sheet's
    /// follow mode). Living here keeps the poll stamp an implementation detail of this class.
    /// </summary>
    public static TimeSpan ExtrapolatedPosition(ActiveCueRow row, long now) =>
        row.Position + Stopwatch.GetElapsedTime(row.PolledAtTicks, now);

    private static void TickActiveRow(ActiveCueRow row, long now)
    {
        var elapsed = ExtrapolatedPosition(row, now);
        row.Clock = CuePresentation.PreciseClock(elapsed);
        if (row.Duration is { TotalMilliseconds: > 0 } length)
        {
            var remaining = length - elapsed;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            row.Remaining = $"−{CuePresentation.PreciseClock(remaining)}";
            row.Progress = Math.Clamp(elapsed / length, 0, 1);
        }

        // The ramps inside the cue run off the SAME extrapolated playhead, so a fade's countdown moves
        // exactly as smoothly as the cue's own and the two can never disagree about where it is.
        foreach (var lane in row.Lanes)
            lane.Tick(elapsed);
    }

    private void RebuildRows()
    {
        IReadOnlyList<object> fresh = FlatList
            ? [.. ActiveCues.Cast<object>()]
            : CuePresentation.ActivePanel(_project(), [.. ActiveCues], _runtime.MediaDurations);

        for (var i = 0; i < fresh.Count; i++)
        {
            var incoming = fresh[i];
            var existingIndex = FindRow(Rows, incoming, i);
            if (existingIndex < 0)
            {
                Rows.Insert(i, incoming);
                continue;
            }

            if (existingIndex != i)
                Rows.Move(existingIndex, i);

            switch (Rows[i], incoming)
            {
                case (ActiveCueRow current, ActiveCueRow freshRow):
                    current.UpdateFrom(freshRow);
                    break;
                case (ActiveGroupRow current, ActiveGroupRow freshGroup):
                    current.UpdateAggregatesFrom(freshGroup);
                    SyncChildren(current.Children, freshGroup.Children);
                    SyncUpcoming(current.Upcoming, freshGroup.Upcoming);
                    current.HasUpcoming = current.Upcoming.Count > 0;
                    break;
            }
        }

        while (Rows.Count > fresh.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    /// <summary>Reconciles the raw sounding list against one poll, by the same identity-and-shape
    /// rules the panel's children use. A row already present keeps its object and adopts the fresh
    /// measurements; membership changes insert, move or trim.</summary>
    private void SyncActiveCues(IReadOnlyList<ActiveCueRow> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            var found = -1;
            for (var j = i; j < ActiveCues.Count; j++)
            {
                if (ActiveCues[j].StructurallySame(fresh[i]))
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                ActiveCues.Insert(i, fresh[i]);
                continue;
            }

            if (found != i)
                ActiveCues.Move(found, i);

            // The flat panel holds these same objects, so a row can meet itself here; adopting from
            // itself would be a harmless no-op, but there is nothing to adopt.
            if (!ReferenceEquals(ActiveCues[i], fresh[i]))
                ActiveCues[i].UpdateFrom(fresh[i]);
        }

        while (ActiveCues.Count > fresh.Count)
            ActiveCues.RemoveAt(ActiveCues.Count - 1);
    }

    /// <summary>An existing row matching <paramref name="incoming"/>'s identity and shape, at or
    /// after <paramref name="from"/>, or −1 when the row is genuinely new (or reshaped - a changed
    /// shape replaces the object, since indentation and labels are deliberately not observable).</summary>
    private static int FindRow(ObservableCollection<object> rows, object incoming, int from)
    {
        for (var i = from; i < rows.Count; i++)
        {
            switch (rows[i], incoming)
            {
                case (ActiveCueRow current, ActiveCueRow fresh) when current.StructurallySame(fresh):
                    return i;
                case (ActiveGroupRow current, ActiveGroupRow fresh)
                    when current.GroupId == fresh.GroupId
                         && current.Number == fresh.Number
                         && current.Label == fresh.Label
                         && current.Mode == fresh.Mode:
                    return i;
            }
        }

        return -1;
    }

    private static void SyncChildren(
        ObservableCollection<ActiveCueRow> current, ObservableCollection<ActiveCueRow> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            var found = -1;
            for (var j = i; j < current.Count; j++)
            {
                if (current[j].StructurallySame(fresh[i]))
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
                current.Insert(i, fresh[i]);
            else
            {
                if (found != i)
                    current.Move(found, i);
                current[i].UpdateFrom(fresh[i]);
            }
        }

        while (current.Count > fresh.Count)
            current.RemoveAt(current.Count - 1);
    }

    private static void SyncUpcoming(
        ObservableCollection<UpcomingCueRow> current, ObservableCollection<UpcomingCueRow> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            var found = -1;
            for (var j = i; j < current.Count; j++)
            {
                if (current[j].Number == fresh[i].Number && current[j].Label == fresh[i].Label)
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
                current.Insert(i, fresh[i]);
            else
            {
                if (found != i)
                    current.Move(found, i);
                current[i].UpdateFrom(fresh[i]);
            }
        }

        while (current.Count > fresh.Count)
            current.RemoveAt(current.Count - 1);
    }
}
