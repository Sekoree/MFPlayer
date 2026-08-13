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
/// the Active panel possible. They belong together because they are the same fact seen twice - a fire
/// adds to the sounding set, a natural end removes from it, a stop clears it - and splitting them
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
    /// trims - and there was no way at all to seek, because a seek addresses a group.
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

        await FlushPendingEditAsync().ConfigureAwait(false);

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
    /// pressed the button - the one moment it must not.
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
            Report($"pre-roll could not warm the next cue - {failure.Message}");
        }
    }

    /// <summary>Warms every list's cursor - what a freshly opened show wants before the first GO.</summary>
    internal void WarmAllStandby()
    {
        foreach (var list in _project.CueLists)
            WarmStandby(list);
    }

    /// <summary>
    /// Runs a timeline group from a position inside it - the rehearsal verb.
    /// </summary>
    /// <remarks>
    /// Distinct from firing the group, which always starts at its top. What an operator rehearsing a
    /// scene wants is the state the show would be in AT that moment, bed and all, which is why a clip
    /// straddling the playhead is started part-way through rather than skipped.
    /// </remarks>
    public async Task FireTimelineFromAsync(GroupCueNode group, TimeSpan from)
    {
        await FlushPendingEditAsync().ConfigureAwait(false);
        await Executor.FireTimelineAsync(group, from).ConfigureAwait(false);
    }

    /// <summary>Fires one cue by id, whatever the cursor is doing.</summary>
    public async Task<bool> FireAsync(Guid cueId)
    {
        await FlushPendingEditAsync().ConfigureAwait(false);
        return await Executor.FireAsync(cueId).ConfigureAwait(false);
    }

    /// <summary>
    /// An edit the host declined to adopt mid-show, offered again now that something is about to fire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by the shell. A cue fired against a document the operator has since edited would play the
    /// old version of itself - the one failure the deferral in <see cref="TryReloadAsync"/> must not
    /// introduce - so every fire passes through here first.
    /// </para>
    /// <para>
    /// Only the OPERATOR's verbs go through this: the executor's own internal fires (a playlist
    /// advancing, an auto-continue, a timeline child reaching its offset) call the executor directly and
    /// deliberately do not, because flushing between two items of a running playlist is precisely the
    /// interruption being avoided.
    /// </para>
    /// </remarks>
    public Func<Task>? PendingEditFlush { get; set; }

    private async Task FlushPendingEditAsync()
    {
        if (PendingEditFlush is not { } flush)
            return;

        try
        {
            await flush().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A flush that failed must never stop the GO. The operator pressed a transport button; the
            // worst case is that it fires the document the engine already had, which is what would have
            // happened anyway.
            Report($"a held edit could not be applied before firing - {failure.Message}");
        }
    }

    /// <summary>
    /// Stops one cue - the bare STOP.
    /// </summary>
    /// <remarks>
    /// A per-cue stop rather than a stop-all, because on a show with a music bed under a video the
    /// operator who wants the video gone almost never wants the bed gone with it. Stop-all is a
    /// separate, deliberate verb.
    /// </remarks>
    public Task StopCueAsync(Guid cueId) => StopCueAsync(cueId, fadeDuration: null);

    /// <summary>The bare STOP with a fade override: null keeps the cue's configured fade-out, a
    /// non-positive duration hard-cuts past it - the operator's second press on a stop that is
    /// already ramping means "now", not "start the same 2-second fade again".</summary>
    public async Task StopCueAsync(Guid cueId, TimeSpan? fadeDuration)
    {
        MarkFading(cueId);
        _outbound.Interrupt(cueId);

        if (await StopAutomationRunAsync(cueId).ConfigureAwait(false))
        {
            Executor.OnStopped(cueId);
            return;
        }

        await StopVisualizerAutomationRunAsync(cueId).ConfigureAwait(false);

        // A visualizer holds a renderer rather than a voice, so the session has nothing to stop for it
        // - asking anyway would silently do nothing and leave the canvas lit.
        await _visualizers.StopAsync(cueId).ConfigureAwait(false);
        await _session.StopCueAsync(cueId.ToString(), fadeDuration).ConfigureAwait(false);
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
    /// A fade rather than a cut, and a SHORT one - the setting defaults to 250 ms. A true hard cut
    /// through a big PA is a thump that can damage drivers, so "as fast as is safe" is the honest
    /// reading of panic, and the number stays the operator's to set.
    /// </remarks>
    public Task PanicAsync() =>
        StopEverythingAsync(TimeSpan.FromMilliseconds(
            _project.Settings.EffectivePanicFadeMs(MachinePanicFadeMs)),
            new FadeShape(FadeCurve.Linear));

    private async Task StopEverythingAsync(TimeSpan fade, FadeShape curve)
    {
        // Cancel future edges and roll prepared voices back BEFORE the stop snapshot/fade. Otherwise a voice
        // hidden behind a timeline gate could cross that gate while the already-active set was fading out.
        if (_executor is { } executor)
            await executor.CancelTimelineRunsAsync().ConfigureAwait(false);

        // Controller cues have no session voice. Cancel and await them before taking the stop snapshot
        // so none can write a level back into a voice while the session is fading it down.
        await StopAllAutomationRunsAsync().ConfigureAwait(false);
        await StopAllVisualizerAutomationRunsAsync().ConfigureAwait(false);

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

        lock (_pausedGate)
        {
            if (_paused == paused)
                return;
            // Close or open the paused interval AT THE TRANSITION. A timeline's coordinate has to
            // exclude paused time, and the only alternative to recording it here is for the timeline to
            // sample a polled flag and attribute whole poll intervals to whichever state it happened to
            // observe - which both quantizes the coordinate to the poll and forces the position to be
            // ACCUMULATED on read (mutating shared state, so every reader needs a gate). Measured
            // exactly, once, at the edge: the timeline's read becomes a pure lock-free subtraction.
            var now = TimelineMasterNow();
            if (paused)
            {
                _pausedSince = now;
            }
            else
            {
                if (_pausedSince is { } since && now > since)
                    _pausedElapsed += now - since;
                _pausedSince = null;
            }

            _paused = paused;
        }
    }

    /// <summary>
    /// Master time this show has spent paused, in the <see cref="ICueExecutionHost.TimelineClock"/>
    /// domain. Monotonic and never faster than master time itself, which is what lets a timeline
    /// subtract it without needing a monotonic clamp of its own.
    /// </summary>
    internal TimeSpan TimelinePausedElapsed
    {
        get
        {
            lock (_pausedGate)
            {
                if (_pausedSince is not { } since)
                    return _pausedElapsed;
                var now = TimelineMasterNow();
                return now > since ? _pausedElapsed + (now - since) : _pausedElapsed;
            }
        }
    }

    /// <summary>The bay master's current elapsed, or zero when there is no bay clock to read.</summary>
    private TimeSpan TimelineMasterNow()
    {
        try { return _bay.Bay.MasterClock.ElapsedSinceStart; }
        catch { return _pausedSince ?? _pausedElapsed; } // torn down mid-read: freeze the interval
    }

    /// <summary>
    /// Pause accounting has its OWN gate: <see cref="TimelinePausedElapsed"/> is read by every
    /// timeline position poll (the scheduler loop plus dispatch-lateness sampling, at millisecond
    /// cadence), and putting those reads on the host-wide <see cref="_gate"/> made the "lock-free"
    /// timeline coordinate contend with the entire transport control plane. Nothing takes
    /// <see cref="_gate"/> while holding this one.
    /// </summary>
    private readonly object _pausedGate = new();
    private TimeSpan _pausedElapsed;
    private TimeSpan? _pausedSince;

    public bool IsPaused
    {
        get
        {
            lock (_pausedGate)
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

    /// <summary>
    /// Raised whenever the sounding set changes - a cue fired, ended, or was forgotten. The UI uses
    /// it to poll a fresh snapshot IMMEDIATELY instead of waiting out its own tick, so the Active
    /// panel reflects a GO on the next dispatcher pass rather than up to a poll period later.
    /// Raised from engine threads; subscribers must marshal themselves.
    /// </summary>
    public event Action? SoundingChanged;

    /// <summary>
    /// Marks a cue sounding, stamped NOW.
    /// </summary>
    /// <remarks>
    /// Called at FIRE START, before the media open completes. The open of a cold file takes long
    /// enough to see, and an Active panel that only shows the cue once it is audible reads as a GO
    /// that did not take - the cue is committed from the operator's point of view the moment they
    /// pressed the button. A fire that then fails takes the entry back down via <see cref="Forget"/>.
    /// </remarks>
    private void Remember(Guid cueId, Guid listId, string groupId)
    {
        lock (_gate)
            _sounding[cueId] = new Sounding(Stopwatch.GetTimestamp(), listId, groupId, IsFading: false);
        SoundingChanged?.Invoke();
    }

    /// <summary>
    /// Re-asserts a fire-start <see cref="Remember"/> after the fire committed, without re-stamping.
    /// </summary>
    /// <remarks>
    /// A re-fire of an already-sounding cue displaces its old voice DURING the fire, and that old
    /// voice's teardown calls <see cref="Forget"/> - which, now that Remember runs before the open,
    /// can race the fresh entry away. This puts it back (with a fresh stamp, since the original
    /// moment was lost with the entry) and leaves a surviving entry - and its fire-start stamp -
    /// untouched, so the Active panel's order still reflects when the operator fired it.
    /// </remarks>
    private void ConfirmSounding(Guid cueId, Guid listId, string groupId)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_sounding.ContainsKey(cueId))
            {
                _sounding[cueId] = new Sounding(Stopwatch.GetTimestamp(), listId, groupId, IsFading: false);
                changed = true;
            }
        }

        if (changed)
            SoundingChanged?.Invoke();
    }

    /// <summary>
    /// Moves a sounding cue's playhead.
    /// </summary>
    /// <remarks>
    /// Addressed by CUE because that is what the operator clicked, and resolved to the transport GROUP
    /// the cue is on, because that is what a seek moves. A cue that is not sounding refuses rather than
    /// seeking something else - there is no useful meaning for "seek a cue that is not playing", and
    /// the group it would land on is whatever played there last.
    /// </remarks>
    public async Task<string?> SeekCueAsync(Guid cueId, TimeSpan position)
    {
        if (await SeekAutomationRunAsync(cueId, position).ConfigureAwait(false))
            return null;
        if (await SeekVisualizerAutomationRunAsync(cueId, position).ConfigureAwait(false))
            return null;

        string group;

        lock (_gate)
        {
            if (!_sounding.TryGetValue(cueId, out var entry))
                return "that cue is not playing";

            group = entry.GroupId;
        }

        if (group.Length == 0)
            return "that cue has no transport to seek";

        await _session.SeekAsync(MediaTimeFor(cueId, position), group).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// CUE time (what the operator scrubbed to) → the MEDIA time the transport seeks in, for whichever
    /// cue this is. Non-media cues have no trim and pass through.
    /// </summary>
    /// <remarks>
    /// The arithmetic lives on <see cref="MediaCueNode.MediaTimeAt"/>, beside its inverse and beside
    /// <see cref="MediaCueNode.TrimmedLength"/>, because this mapping having two independently-written
    /// halves is precisely what made a trimmed cue seek to the wrong place.
    /// </remarks>
    private TimeSpan MediaTimeFor(Guid cueId, TimeSpan cueTime) =>
        MediaTimeFor(_project.FindCue(cueId), cueTime);

    private static TimeSpan MediaTimeFor(CueNode? cue, TimeSpan cueTime) =>
        cue is MediaCueNode media
            ? media.MediaTimeAt(cueTime)
            : cueTime > TimeSpan.Zero ? cueTime : TimeSpan.Zero;

    /// <summary>
    /// Seeks several sounding cues as ONE transport operation - the group-header scrub.
    /// </summary>
    /// <remarks>
    /// Through the session's seek barrier (<c>SeekManyAsync</c>: every group pauses, all seek with
    /// clocks frozen, the running ones resume together), because seeking the cues one at a time
    /// landed each at a different wall moment - eleven stems arrived milliseconds apart and stayed
    /// that far out of sync for the rest of the song. Cues that are not sounding are skipped; the
    /// refusal names them only when NOTHING in the batch could seek.
    /// </remarks>
    public async Task<string?> SeekCuesAsync(IReadOnlyList<(Guid CueId, TimeSpan Position)> seeks)
    {
        // One document walk for the whole batch: FindCue is a linear scan, and this runs once per
        // scrub EVENT for every stem in the group - per-cue lookups made a drag O(cues × stems).
        var cuesById = new Dictionary<Guid, CueNode>();
        foreach (var cue in _project.AllCues())
            cuesById.TryAdd(cue.Id, cue);

        var batch = new List<(string GroupId, TimeSpan Position)>(seeks.Count);
        lock (_gate)
        {
            foreach (var (cueId, position) in seeks)
            {
                // Per cue, not per batch: each cue carries its OWN trim, so one group-wide "seek to
                // 30:50" is a different file position for a trimmed child than for an untrimmed one.
                // That is exactly what a together-group seek means - land every child on the same bar
                // of the song - and it is why the conversion cannot be hoisted out of this loop.
                if (_sounding.TryGetValue(cueId, out var entry) && entry.GroupId.Length > 0)
                    batch.Add((entry.GroupId, MediaTimeFor(cuesById.GetValueOrDefault(cueId), position)));
            }
        }

        if (batch.Count == 0)
            return "nothing in that group is playing";

        await _session.SeekManyAsync(batch).ConfigureAwait(false);
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
        bool removed;
        lock (_gate)
            removed = _sounding.Remove(id);
        if (removed)
            SoundingChanged?.Invoke();
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
        var automationPlayheads = AutomationRunSnapshots();

        paused = IsPaused;
        lock (_gate)
        {
            sounding = [.. _sounding.Keys];
            active =
            [
                .. _sounding.Select(entry =>
                {
                    var wall = Stopwatch.GetElapsedTime(entry.Value.StartedTicks);
                    var playhead = playheads.GetValueOrDefault(entry.Value.GroupId);

                    // The group's position when it has one, and wall time otherwise - a visualizer
                    // cue holds no transport at all, and counting up is better than standing still.
                    //
                    // ONE domain for the whole panel: on-air. A routed cue's transport position tracks
                    // content entering the mix bus - one downstream depth (~pump + device latency)
                    // ahead of the speaker - while a genlocked video-only cue's position tracks the
                    // glass (its start was deferred by exactly that depth). Shown raw, cues that ARE
                    // playing together read ~100–200 ms apart; subtracting AudibleLatency puts every
                    // readout at the speaker/glass truth.
                    var elapsed = playhead is { IsActive: true }
                        ? playhead.ClipPosition > playhead.AudibleLatency
                            ? playhead.ClipPosition - playhead.AudibleLatency
                            : TimeSpan.Zero
                        : automationPlayheads.TryGetValue(entry.Key, out var automation)
                            ? automation.Position
                            : wall;
                    var length = playhead is { ClipDuration.Ticks: > 0 }
                        ? (TimeSpan?)playhead.ClipDuration
                        : automationPlayheads.TryGetValue(entry.Key, out automation)
                            ? automation.Duration
                            : null;

                    // The transport reports MEDIA time; the operator reads CUE time. A trimmed cue's
                    // playhead therefore starts at its trim-in and its transport duration is the whole
                    // file - a cue trimmed to start at 36:00 read "38:17 of 2:36:09" two minutes in,
                    // beside siblings reading "02:17", and the panel looked half an hour out of sync.
                    if (playhead is { IsActive: true }
                        && _project.FindCue(entry.Key) is MediaCueNode media)
                    {
                        elapsed = media.CueTimeAt(elapsed);

                        if (length is { } fullLength && media.TrimmedLength(fullLength) is { } trimmed)
                            length = trimmed;
                    }

                    return new ActiveCueState(
                        entry.Key,
                        entry.Value.ListId,
                        elapsed,
                        length,
                        entry.Value.IsFading,
                        entry.Value.StartedTicks,
                        // Only when something is actually overriding the authored level; the inspector
                        // shows the static field alone otherwise.
                        playhead is { IsVolumeAutomated: true }
                            ? LinearToDb(playhead.CueVolume)
                            : null);
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
