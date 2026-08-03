using Avalonia.Threading;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Presentation;

namespace HaCue2.Session;

/// <summary>
/// Keeps a <see cref="ShowRuntime"/> filled from a running <see cref="ShowHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 5 half of the seam. The views never learn that a session exists: they read the same
/// runtime object they always did, and this replaces the invented values with real ones as they
/// arrive. Everything still unfilled — meters, the bay counters, the log — stays exactly as it was,
/// so what is real and what is not is still visible from one place.
/// </para>
/// <para>
/// Polled rather than pushed. The session raises its events on its own dispatcher and at its own
/// rate; the cue list wants a consistent picture a few times a second, not every edge. A poll also
/// cannot back up: if the UI is busy, ticks are simply missed rather than queued.
/// </para>
/// </remarks>
public sealed class EngineRuntime : IAsyncDisposable
{
    /// <summary>Four times a second — fast enough to read as live, slow enough to cost nothing.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private readonly ShowHost _host;
    private readonly ShowRuntime _runtime;
    private readonly HaCueProject _project;
    private readonly DispatcherTimer _timer;
    private bool _polling;

    public EngineRuntime(ShowHost host, ShowRuntime runtime, HaCueProject project)
    {
        _host = host;
        _runtime = runtime;
        _project = project;

        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Poll());
        _timer.Start();
    }

    /// <summary>Raised when what is sounding or where standby sits actually changed.</summary>
    public event Action? Changed;

    /// <summary>Raised on every poll, for the surfaces whose values move continuously.</summary>
    public event Action? Ticked;

    private async void Poll()
    {
        // One in flight at a time: a snapshot crosses the session dispatcher, and stacking them up
        // behind a busy session would turn a 250 ms tick into an unbounded queue.
        if (_polling)
            return;

        _polling = true;

        try
        {
            var state = await _host.SnapshotAsync().ConfigureAwait(true);

            // The Active panel's CLOCKS move on every tick even when the cue set has not changed, so
            // this cannot early-out on "same cues sounding" the way the tree can — a progress bar that
            // only advanced when something started or stopped would look frozen mid-cue.
            _runtime.ActiveCues = CuePresentation.Active(_project, state.Active, _runtime.MediaDurations);
            _runtime.IsPaused = state.IsPaused;
            AdoptBay();

            var sounding = new HashSet<Guid>(state.Sounding);
            var settled = sounding.SetEquals(_runtime.Sounding) && !StandbyChanged(state);

            _runtime.Sounding = sounding;
            Adopt(state);

            // Two signals, because they cost different amounts. The Active panel re-reads every tick;
            // the cue tree only when something actually started, stopped or moved.
            Ticked?.Invoke();

            if (!settled)
                Changed?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // The host went away between the tick and the await. Nothing to report.
            _timer.Stop();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// Copies the bay's own counters into the runtime the Diagnostics window and drawer read.
    /// </summary>
    /// <remarks>
    /// Every one of these values used to come from <c>SampleShow</c> — invented numbers on the one
    /// screen an operator opens to find out why there is no sound. The meters in particular have to
    /// be read on the tick rather than on a change, because a level that only updated when a cue
    /// started or stopped would sit still through the whole of it.
    /// </remarks>
    private void AdoptBay()
    {
        var bay = _host.Diagnostics();

        _runtime.BayRows = BayPresentation.Rows(_project, bay);
        _runtime.Meters = BayPresentation.Meters(_project, bay);
        _runtime.Levels = BayPresentation.Levels(_project, bay);
        _runtime.BaySummary = BayPresentation.Summary(bay);
        _runtime.BayClock = BayPresentation.Clock(_project, bay);
    }

    private bool StandbyChanged(ShowState state) =>
        _project.CueLists.Any(list =>
            state.Standby.GetValueOrDefault(list.Id) is var cue
            && (cue == Guid.Empty ? null : (Guid?)cue) != list.StandbyCueId);

    /// <summary>
    /// Copies the session's cursor into the document.
    /// </summary>
    /// <remarks>
    /// Written straight onto the list rather than through the journal: where the playhead sits is not
    /// an EDIT, and an undo stack full of "the show advanced" would bury every real change the
    /// operator made.
    /// </remarks>
    private void Adopt(ShowState state)
    {
        foreach (var list in _project.CueLists)
        {
            list.StandbyCueId = state.Standby.TryGetValue(list.Id, out var cue) ? cue : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await _host.DisposeAsync().ConfigureAwait(false);
    }
}
