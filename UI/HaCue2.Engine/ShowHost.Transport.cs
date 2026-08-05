using System.Diagnostics;
using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using S.Media.Session;

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
    /// <summary>
    /// A cue that is holding a voice: when it started, where it came from, and which transport it is on.
    /// </summary>
    /// <param name="GroupId">
    /// The session group whose playhead IS this cue's playhead. Without it the Active panel could only
    /// count wall time from the fire, which is a different number the moment anything pauses, seeks or
    /// trims — and there was no way at all to seek, because a seek addresses a group.
    /// </param>
    private readonly record struct Sounding(long StartedTicks, Guid ListId, string GroupId, bool IsFading);

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

        if (_project.CueLists.FirstOrDefault(candidate => candidate.Id == list.Id) is not { } runtimeList)
            return null;

        var standby = await _session.GetStandbyCueAsync(ShowCompiler.GroupId(runtimeList)).ConfigureAwait(false);

        if (standby is null || !Guid.TryParse(standby.Id, out var id))
            return null;

        await Executor.AdvanceAsync(runtimeList, id).ConfigureAwait(false);
        await Executor.FireAsync(id).ConfigureAwait(false);

        // The cursor has moved; open what it now points at, so the NEXT go is instant.
        WarmStandby(runtimeList);
        return id;
    }

    /// <summary>Puts a list's cursor on a cue without firing it.</summary>
    public async Task<bool> StandbyAsync(CueList list, Guid? cueId)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_project.CueLists.FirstOrDefault(candidate => candidate.Id == list.Id) is not { } runtimeList)
            return false;

        var moved = await _session.SetStandbyCueAsync(
            cueId?.ToString(), ShowCompiler.GroupId(runtimeList)).ConfigureAwait(false);
        if (moved)
        {
            runtimeList.StandbyCueId = cueId;
            WarmStandby(runtimeList);
        }

        return moved;
    }

    // ── pre-roll ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the next few cues' media before anybody presses GO.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The difference between a GO that plays and a GO that opens a file first. Opening a 4 K clip off a
    /// slow disk takes long enough to be seen, and until now HaCue2 did it at the moment the operator
    /// pressed the button — the one moment it must not.
    /// </para>
    /// <para>
    /// Fired and NOT awaited, deliberately: pre-roll is best-effort and must never delay the transport
    /// verb that triggered it. The session swallows its own failures for the same reason; a warm that
    /// did not happen costs the next GO an open, which is exactly where it was before.
    /// </para>
    /// </remarks>
    private void WarmStandby(CueList list)
    {
        var count = _project.Settings.PreRollCount;

        if (count <= 0)
            return;

        _ = WarmAsync(ShowCompiler.GroupId(list), count);
    }

    private async Task WarmAsync(string groupId, int count)
    {
        try
        {
            await _session.WarmUpcomingAsync(groupId, count).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Best-effort by contract. Reported once rather than swallowed silently, because a rig
            // where pre-roll always fails is a rig where every GO pays for an open.
            Report($"pre-roll could not warm the next cue — {failure.Message}");
        }
    }

    /// <summary>Warms every list's cursor — what a freshly opened show wants before the first GO.</summary>
    internal void WarmAllStandby()
    {
        foreach (var list in _project.CueLists)
            WarmStandby(list);
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
        Executor.OnStopped(cueId);
    }

    /// <summary>Stops everything, fading over the project's stop fade.</summary>
    public Task StopAllAsync() =>
        StopEverythingAsync(
            TimeSpan.FromMilliseconds(_project.Settings.StopFadeMs),
            _project.Settings.StopFadeCurve.Resolve(_project));

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
            _project.Settings.EffectivePanicFadeMs(MachinePanicFadeMs)),
            new FadeShape(FadeCurve.Linear));

    private async Task StopEverythingAsync(TimeSpan fade, FadeShape curve)
    {
        List<Guid> active;
        lock (_gate)
        {
            active = [.. _sounding.Keys];
            foreach (var id in active)
                _sounding[id] = _sounding[id] with { IsFading = true };
        }

        // OSC/MIDI effect lanes are not session voices. Interrupt them explicitly so a global
        // stop still sends each lane's authored final value instead of leaving external gear at
        // the last intermediate value it received.
        foreach (var id in active)
            _outbound.Interrupt(id);

        // Visualizers do not fade: a projectM renderer has no level, and holding a canvas lit for the
        // stop fade while everything audible came down would read as a rig that had not stopped.
        await _visualizers.StopAllAsync().ConfigureAwait(false);
        await _session.StopAllAsync(fade, curve).ConfigureAwait(false);

        _executor?.ResetTransientState();

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

    /// <summary>Which transport a cue lands on, as the last compile decided.</summary>
    private string GroupOf(Guid cueId)
    {
        lock (_gate)
            return _cueGroups.GetValueOrDefault(cueId, "");
    }

    private void Remember(Guid cueId, Guid listId, string groupId)
    {
        lock (_gate)
            _sounding[cueId] = new Sounding(Stopwatch.GetTimestamp(), listId, groupId, IsFading: false);
    }

    /// <summary>
    /// Moves a sounding cue's playhead.
    /// </summary>
    /// <remarks>
    /// Addressed by CUE because that is what the operator clicked, and resolved to the transport GROUP
    /// the cue is on, because that is what a seek moves. A cue that is not sounding refuses rather than
    /// seeking something else — there is no useful meaning for "seek a cue that is not playing", and
    /// the group it would land on is whatever played there last.
    /// </remarks>
    public async Task<string?> SeekCueAsync(Guid cueId, TimeSpan position)
    {
        string group;

        lock (_gate)
        {
            if (!_sounding.TryGetValue(cueId, out var entry))
                return "that cue is not playing";

            group = entry.GroupId;
        }

        if (group.Length == 0)
            return "that cue has no transport to seek";

        await _session.SeekAsync(position < TimeSpan.Zero ? TimeSpan.Zero : position, group)
            .ConfigureAwait(false);
        return null;
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

        _outbound.Interrupt(id);
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

        // The session's own playheads, by transport group. Lock-free and safe to read from any thread,
        // which is why it is taken OUTSIDE the host's gate. This is what makes the Active panel's clock
        // and progress bar true: wall time since the fire is a different number as soon as anything is
        // paused, seeked, trimmed or looped, and it is the number the panel used to show.
        var playheads = _session.Snapshot()
            .ToDictionary(snapshot => snapshot.GroupId, StringComparer.Ordinal);

        lock (_gate)
        {
            sounding = [.. _sounding.Keys];
            paused = _paused;
            active =
            [
                .. _sounding.Select(entry =>
                {
                    var wall = Stopwatch.GetElapsedTime(entry.Value.StartedTicks);
                    var playhead = playheads.GetValueOrDefault(entry.Value.GroupId);

                    return new ActiveCueState(
                        entry.Key,
                        entry.Value.ListId,
                        // The group's position when it has one, and wall time otherwise — a visualizer
                        // cue holds no transport at all, and counting up is better than standing still.
                        playhead is { IsActive: true } ? playhead.ClipPosition : wall,
                        playhead is { ClipDuration.Ticks: > 0 } ? playhead.ClipDuration : null,
                        entry.Value.IsFading);
                }),
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
