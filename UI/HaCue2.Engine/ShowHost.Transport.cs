using System.Diagnostics;
using HaCue2.Core.Compile;
using HaCue2.Core.Model;

namespace HaCue2.Engine;

/// <summary>
/// The operator's verbs, and the record of what they left sounding.
/// </summary>
/// <remarks>
/// One half of this file is the transport an operator presses; the other is the bookkeeping that makes
/// the Active panel possible. They belong together because they are the same fact seen twice — a fire
/// adds to the sounding set, a natural end removes from it, a stop clears it — and splitting them
/// would put the writer and the reader of that state in different files.
/// </remarks>
public sealed partial class ShowHost
{
    /// <summary>A cue that is holding a voice: when it started, and where it came from.</summary>
    private readonly record struct Sounding(long StartedTicks, Guid ListId, bool IsFading);

    /// <summary>What is holding a voice right now, by cue id. Guarded by the host's gate.</summary>
    private readonly Dictionary<Guid, Sounding> _sounding = [];

    private bool _paused;

    /// <summary>
    /// Fires the standby cue of a list and advances its cursor.
    /// </summary>
    /// <remarks>
    /// The cursor is read BEFORE anything fires, because firing is what moves it. The cue is then
    /// resolved by kind here rather than handed to <c>ShowSession.GoAsync</c>: the session would fire a
    /// jump cue as a clip with nothing to play and call that success.
    /// </remarks>
    public async Task<Guid?> GoAsync(CueList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var standby = await _session.GetStandbyCueAsync(ShowCompiler.GroupId(list)).ConfigureAwait(false);

        if (standby is null || !Guid.TryParse(standby.Id, out var id))
            return null;

        await Executor.AdvanceAsync(list, id).ConfigureAwait(false);
        await Executor.FireAsync(id).ConfigureAwait(false);
        return id;
    }

    /// <summary>Puts a list's cursor on a cue without firing it.</summary>
    public Task<bool> StandbyAsync(CueList list, Guid? cueId)
    {
        ArgumentNullException.ThrowIfNull(list);
        return _session.SetStandbyCueAsync(cueId?.ToString(), ShowCompiler.GroupId(list));
    }

    /// <summary>
    /// Runs a timeline group from a position inside it — the rehearsal verb.
    /// </summary>
    /// <remarks>
    /// Distinct from firing the group, which always starts at its top. What an operator rehearsing a
    /// scene wants is the state the show would be in AT that moment, bed and all, which is why a clip
    /// straddling the playhead is started part-way through rather than skipped.
    /// </remarks>
    public Task FireTimelineFromAsync(GroupCueNode group, TimeSpan from) =>
        Executor.FireTimelineAsync(group, from);

    /// <summary>Fires one cue by id, whatever the cursor is doing.</summary>
    public Task<bool> FireAsync(Guid cueId) => Executor.FireAsync(cueId);

    /// <summary>
    /// Stops one cue — the bare STOP.
    /// </summary>
    /// <remarks>
    /// A per-cue stop rather than a stop-all, because on a show with a music bed under a video the
    /// operator who wants the video gone almost never wants the bed gone with it. Stop-all is a
    /// separate, deliberate verb.
    /// </remarks>
    public async Task StopCueAsync(Guid cueId)
    {
        MarkFading(cueId);

        // A visualizer holds a renderer rather than a voice, so the session has nothing to stop for it
        // — asking anyway would silently do nothing and leave the canvas lit.
        await _visualizers.StopAsync(cueId).ConfigureAwait(false);
        await _session.StopCueAsync(cueId.ToString()).ConfigureAwait(false);
        Forget(cueId.ToString());
    }

    /// <summary>Stops everything, fading over the project's stop fade.</summary>
    public Task StopAllAsync() =>
        StopEverythingAsync(TimeSpan.FromMilliseconds(_project.Settings.StopFadeMs));

    /// <summary>
    /// PANIC: stops everything over the project's panic fade.
    /// </summary>
    /// <remarks>
    /// A fade rather than a cut, and a SHORT one — the setting defaults to 250 ms. A true hard cut
    /// through a big PA is a thump that can damage drivers, so "as fast as is safe" is the honest
    /// reading of panic, and the number stays the operator's to set.
    /// </remarks>
    public Task PanicAsync() =>
        StopEverythingAsync(TimeSpan.FromMilliseconds(
            _project.Settings.EffectivePanicFadeMs(MachinePanicFadeMs)));

    private async Task StopEverythingAsync(TimeSpan fade)
    {
        lock (_gate)
        {
            foreach (var id in _sounding.Keys.ToList())
                _sounding[id] = _sounding[id] with { IsFading = true };
        }

        // Visualizers do not fade: a projectM renderer has no level, and holding a canvas lit for the
        // stop fade while everything audible came down would read as a rig that had not stopped.
        await _visualizers.StopAllAsync().ConfigureAwait(false);
        await _session.StopAllAsync(fade).ConfigureAwait(false);

        lock (_gate)
            _sounding.Clear();
    }

    /// <summary>Pauses or resumes the show.</summary>
    public async Task SetPausedAsync(bool paused)
    {
        await _session.SetPausedAsync(paused).ConfigureAwait(false);

        lock (_gate)
            _paused = paused;
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _paused;
        }
    }

    // ── what is sounding ──────────────────────────────────────────────────────────────────────

    private void Remember(Guid cueId, Guid listId)
    {
        lock (_gate)
            _sounding[cueId] = new Sounding(Stopwatch.GetTimestamp(), listId, IsFading: false);
    }

    private void MarkFading(Guid cueId)
    {
        lock (_gate)
        {
            if (_sounding.TryGetValue(cueId, out var entry))
                _sounding[cueId] = entry with { IsFading = true };
        }
    }

    private void Forget(string cueId)
    {
        if (!Guid.TryParse(cueId, out var id))
            return;

        lock (_gate)
            _sounding.Remove(id);
    }

    private List<Guid> SoundingIds()
    {
        lock (_gate)
            return [.. _sounding.Keys];
    }

    /// <summary>
    /// What the show is doing, for the views.
    /// </summary>
    /// <remarks>
    /// Pulled rather than pushed: the session raises events on its own thread and the UI wants a
    /// consistent picture at a moment of its choosing, not a stream of edges to reassemble.
    /// </remarks>
    public async Task<ShowState> SnapshotAsync()
    {
        List<ActiveCueState> active;
        HashSet<Guid> sounding;
        bool paused;

        lock (_gate)
        {
            sounding = [.. _sounding.Keys];
            paused = _paused;
            active =
            [
                .. _sounding.Select(entry => new ActiveCueState(
                    entry.Key,
                    entry.Value.ListId,
                    Stopwatch.GetElapsedTime(entry.Value.StartedTicks),
                    entry.Value.IsFading)),
            ];
        }

        var standby = new Dictionary<Guid, Guid>();

        foreach (var list in _project.CueLists)
        {
            var cue = await _session.GetStandbyCueAsync(ShowCompiler.GroupId(list)).ConfigureAwait(false);

            if (cue is not null && Guid.TryParse(cue.Id, out var id))
                standby[list.Id] = id;
        }

        return new ShowState(sounding, standby, active, paused, Problems);
    }
}
