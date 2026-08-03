using Avalonia.Threading;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Engine;

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

    /// <summary>Raised after each poll that changed anything, so the views re-read.</summary>
    public event Action? Changed;

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

            var sounding = new HashSet<Guid>(state.Sounding);
            if (sounding.SetEquals(_runtime.Sounding) && !StandbyChanged(state))
                return;

            _runtime.Sounding = sounding;
            Adopt(state);
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
