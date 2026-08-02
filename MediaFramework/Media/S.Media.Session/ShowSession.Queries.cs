using S.Media.Compositor;
using S.Media.Core.Video;
using S.Media.Routing;

namespace S.Media.Session;

/// <summary>
/// What a host can ASK the session, and the state that answers cheaply. Two kinds live here and the
/// distinction is the file's whole point: dispatcher-marshaled snapshot queries (D5 - immutable results, no
/// shared mutable state escapes), and the lock-free published VIEWS that let a 4 Hz UI poll read group,
/// composition and audio-pump state without ever queueing behind a parked serial loop (NXT-16).
/// <para>The group registry (<c>GetOrAddGroup</c>, <c>PublishGroupViews</c>) sits with them rather than with
/// the transport commands because publishing a view is the other half of mutating the registry - separating
/// them is exactly how a view goes stale. Split out of the root file, 2026-07-30 review §3.</para>
/// </summary>
public sealed partial class ShowSession
{
    // --- queries (immutable snapshots - D5) --------------------------------------------------------

    /// <summary>An immutable snapshot of each transport group's session time, clip position, and run state.
    /// Lock-free (NXT-16): reads the published group view and pulls live position/run-state off the captured
    /// clock/player without marshaling, so it never queues behind a long-running command on the dispatcher.</summary>
    public Task<IReadOnlyList<TransportSnapshot>> SnapshotAsync() => Task.FromResult(Snapshot());

    /// <summary>The synchronous, lock-free form of <see cref="SnapshotAsync"/> - safe to call from any thread
    /// (e.g. a 250 ms UI position poll) even while the session dispatcher is busy with a long command.</summary>
    public IReadOnlyList<TransportSnapshot> Snapshot()
    {
        var views = _groupViews; // single volatile read of the published view
        var snaps = new TransportSnapshot[views.Count];
        for (var i = 0; i < views.Count; i++)
        {
            var v = views[i];
            // The captured player/clock may be torn down concurrently by a transport command; a racing read
            // just yields a stale/zero value for one poll tick rather than throwing across the query.
            TimeSpan now = TimeSpan.Zero, pos = TimeSpan.Zero, dur = TimeSpan.Zero;
            var running = false;
            var liveDisconnected = false;
            var audioChannels = 0;
            var audioSampleRate = 0;
            var timeline = v.Group.Timeline.GetSnapshot();
            var active = v.Player is not null; // has a clip (playing/paused/frozen) - independent of the clock
            try
            {
                now = timeline.MasterTime;
                if (v.Player is { } p)
                {
                    pos = p.Position;
                    dur = p.Duration;
                    running = p.IsRunning;
                    liveDisconnected = p.IsLiveSourceExhausted; // live input dropped (router may still report running)
                    if (p.AudioSource is { } audio)
                    {
                        audioChannels = audio.Format.Channels;
                        audioSampleRate = audio.Format.SampleRate;
                    }
                }
            }
            catch { /* concurrent teardown - leave zeros for this tick */ }
            snaps[i] = new TransportSnapshot(
                v.GroupId, now, pos, dur, running, active, liveDisconnected, audioChannels, audioSampleRate,
                timeline.Generation)
            {
                Timeline = timeline,
            };
        }
        return snaps;
    }

    /// <summary>An immutable snapshot of the loaded cue definitions, ordered by cue number.</summary>
    public Task<IReadOnlyList<CueDefinition>> GetCueDefinitionsAsync()
    {
        // Lock-free (NXT-16 residue): the graph reference is volatile and CueGraph is internally locked, so
        // this UI/fire-failure query never queues behind the dispatcher (a long command would stall it).
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(_fires.Cues);
    }

    /// <summary>The cue ids whose clips are currently prepared (warm) in the standby engine - a UI "ready"
    /// indicator, and how a test confirms the pre-roll ran.</summary>
    public Task<IReadOnlyList<string>> GetPreparedCueIdsAsync()
    {
        // Lock-free (NXT-16 residue): the standby engine is internally locked - no dispatcher round-trip.
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult<IReadOnlyList<string>>(_standby.PreparedKeys.Select(k => k.Id).ToArray());
    }

    /// <summary>
    /// Attach a live <see cref="IVideoOutput"/> (e.g. a UI preview surface) to a loaded composition's pump - the
    /// composited canvas starts flowing to it on the next pump tick. Returns false if no composition has that id.
    /// The caller owns the output's lifetime; it is not disposed with the runtime.
    /// </summary>
    public Task<bool> AttachCompositionOutputAsync(string compositionId, IVideoOutput output, string outputId = "preview") =>
        InvokeAsync(() =>
            _compositions.TryGetValue(compositionId, out var composition)
                ? Task.FromResult(composition.AddOutput(new ClipCompositionOutputLease(outputId, outputId, output)))
                : Task.FromResult(false));

    /// <summary>An immutable snapshot of the cue execution log.</summary>
    public Task<IReadOnlyList<CueExecutionLogEntry>> GetCueExecutionLogAsync() =>
        InvokeAsync(() => Task.FromResult(_fires.ExecutionLog));

    /// <summary>A composition's pump stats (frames submitted to its layers + composited), or null when no
    /// composition with that id is loaded - proves the cue→clip→layer→composite path ran (headless).</summary>
    public Task<ClipCompositionRuntimeStats?> GetCompositionStatsAsync(string compositionId) =>
        InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition)
                ? composition.GetStats()
                : (ClipCompositionRuntimeStats?)null));

    /// <summary>Applies (or clears, with <see langword="null"/>) a composition's output mapping at runtime -
    /// projector keystone / multi-panel tiling. Returns false when no composition with that id is loaded.</summary>
    public Task<bool> ApplyCompositionMappingAsync(string compositionId, ClipOutputMappingSpec? mapping) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
                return Task.FromResult(false);
            composition.UpdateCompositionMapping(mapping);
            return Task.FromResult(true);
        });

    /// <summary>
    /// Sets (or clears, with null) a composition's idle frame - what its outputs show while nothing is
    /// playing on that canvas.
    /// </summary>
    /// <remarks>
    /// This level did not exist before: a per-output idle image lived only on local video lines, and only
    /// while the line was NOT held by playback - so the moment a cue list took the line the image stopped
    /// appearing and the canvas simply went black between cues, which is the one time an operator most
    /// wants a logo or a holding slate. Takes precedence over any per-output idle
    /// (<see cref="SetOutputIdleFrameAsync"/>). Ownership transfers on the call.
    /// </remarks>
    public Task<bool> SetCompositionIdleFrameAsync(string compositionId, VideoFrame? frame) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
            {
                frame?.Dispose();
                return Task.FromResult(false);
            }

            composition.SetIdleFrame(frame);
            return Task.FromResult(true);
        });

    /// <summary>
    /// Sets (or clears) ONE output's fallback idle frame, used only when its composition has no idle of
    /// its own - for dressing an output the show does not otherwise cover.
    /// </summary>
    public Task<bool> SetOutputIdleFrameAsync(string compositionId, string outputId, VideoFrame? frame) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
            {
                frame?.Dispose();
                return Task.FromResult(false);
            }

            return Task.FromResult(composition.SetOutputIdleFrame(outputId, frame));
        });

    /// <summary>
    /// Shows (non-null) or hides (null) a calibration pattern on ONE video output of a composition.
    /// </summary>
    /// <remarks>
    /// The per-output counterpart of <see cref="SetCompositionTestPatternAsync"/>, and the one an
    /// operator actually wants at a get-in: the composition-wide pattern is a top-most canvas layer, so
    /// it appears on EVERY output bound to that composition - lighting up the lobby TV and the stream
    /// while you align the projector. This replaces the canvas for the named output alone, upstream of
    /// that output's mapping stage, so the grid is cut and mesh-warped exactly like programme content.
    /// <para>The host renders the grid frame (it owns section masking and what the pattern should look
    /// like) and hands it over; the session owns it from then on. Returns false when the composition or
    /// the output id is unknown - in which case the frame is disposed rather than leaked.</para>
    /// </remarks>
    public Task<bool> SetOutputTestPatternAsync(string compositionId, string outputId, VideoFrame? frame) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
            {
                frame?.Dispose();
                return Task.FromResult(false);
            }

            return Task.FromResult(composition.SetOutputTestPattern(outputId, frame));
        });

    /// <summary>Shows (<paramref name="frame"/> non-null) or hides (null) a mapping-calibration test pattern on a
    /// composition - held in a top-most, full-canvas layer so the operator can align one output's warp against the
    /// live grid. The host renders the grid frame (it owns the mapping/section masking) and hands it here; the
    /// session owns the frame after this call. Returns false when the composition id is unknown.</summary>
    public Task<bool> SetCompositionTestPatternAsync(string compositionId, VideoFrame? frame) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
            {
                frame?.Dispose();
                return Task.FromResult(false);
            }

            if (frame is null)
            {
                if (_testPatternSlots.Remove(compositionId, out var slot))
                    slot.Dispose(); // removes the top layer from the composition
                return Task.FromResult(true);
            }

            var canvas = composition.CanvasFormat;
            if (!_testPatternSlots.TryGetValue(compositionId, out var existing))
            {
                existing = composition.AddLayer(
                    canvas,
                    new VideoPlacementSpec(compositionId, int.MaxValue, Placement: "stretch"));
                _testPatternSlots[compositionId] = existing;
            }

            existing.Output.Configure(canvas);
            existing.Output.Submit(frame); // Submit takes ownership of the frame
            composition.EnsurePumpStarted();
            return Task.FromResult(true);
        });

    /// <summary>Applies (or clears) the mapping for one physical output of a composition. The output id is
    /// supplied by the host's <see cref="ClipCompositionOutputLease"/> and remains stable across live edits.</summary>
    public Task<bool> ApplyOutputMappingAsync(
        string compositionId, string outputId, ClipOutputMappingSpec? mapping) =>
        InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition)
            && composition.UpdateOutputMapping(outputId, mapping)));

    /// <summary>The ACTIVE voice playing <paramref name="cueId"/> on any group, or null. The live-edit and
    /// level-query APIs address "the active clip of cue X" deliberately: a tail is already on its way out
    /// under its own ramp, and re-patching or re-levelling it would fight that ramp. Dispatcher-confined.</summary>
    private TransportVoice? ActiveVoiceOf(string cueId) =>
        _groups.Values
            .Select(group => group.ActiveVoice)
            .FirstOrDefault(voice =>
                voice is not null && string.Equals(voice.Clip.Spec.Id, cueId, StringComparison.Ordinal));

    private TransportGroup GetOrAddGroup(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            // The group itself is not a sounding source - its VOICES are, one bus registration each
            // (CommitVoiceAsync). An idle group therefore has no registration to keep in sync: a voice
            // stamps the live master trim when it is created, so a trim set while the group was idle is
            // already folded into its next fire.
            _groups[groupId] = group = new TransportGroup
            {
                // Looked up by id rather than captured, exactly like every other deferred ramp here.
                StartReleaseRamp = (voice, duration, curve) =>
                    StartVoiceReleaseRamp(groupId, voice, duration, curve),
                VoiceRetired = voice => _sounding.Unregister(voice.SoundingId),
            };
            PublishGroupViews();
        }
        return group;
    }

    /// <summary>Retires every transport group - each group's own teardown releases its voices, and every
    /// release drops that voice's bus registration first. Shared by the document reload and disposal so a
    /// voice can never outlive its registration in one path and not the other.</summary>
    private async ValueTask DisposeGroupsAsync()
    {
        foreach (var group in _groups.Values)
            await group.DisposeAsync().ConfigureAwait(false);

        _groups.Clear();
        PublishGroupViews();
    }

    /// <summary>
    /// Retires only the groups <paramref name="retainedGroupIds"/> does not name, keeping the rest running
    /// with their voices and their GO cursors intact. The reload's selective counterpart to
    /// <see cref="DisposeGroupsAsync"/>.
    /// </summary>
    private async ValueTask DisposeGroupsExceptAsync(IReadOnlySet<string> retainedGroupIds)
    {
        foreach (var (groupId, group) in _groups.ToArray())
        {
            if (retainedGroupIds.Contains(groupId))
                continue;
            await group.DisposeAsync().ConfigureAwait(false);
            _groups.Remove(groupId);
        }

        PublishGroupViews();
    }

    /// <summary>Republishes the lock-free query view (NXT-16). Called on the dispatcher after any change to the
    /// group set or a group's active clip, so <see cref="Snapshot"/> reads never round-trip the dispatcher.</summary>
    private void PublishGroupViews()
    {
        _groupViews = _groups
            .Select(kv => new GroupClockView(kv.Key, kv.Value.ActiveVoice?.Player, kv.Value))
            .ToArray();

        // Audio-pump view: every active clip's device-tagged routed outputs (skips default-device routes, which
        // can't be line-correlated). GetActiveAudioPumpStatsByDevice reads this lock-free.
        var pumps = new List<ActiveAudioPump>();
        foreach (var kv in _groups)
        {
            if (kv.Value.ActiveVoice is not { } active || active.Player.AudioRouter is not { } router)
                continue;
            foreach (var (outputId, deviceId) in active.AudioPumps)
                pumps.Add(new ActiveAudioPump(router, outputId, deviceId));
        }
        _audioPumpsView = pumps;
    }

    /// <summary>Lock-free per-device audio-pump stats (enqueued/dropped chunks) summed across the active cues'
    /// routed outputs - the audio analogue of <see cref="GetCompositionStats"/> for the outputs-panel line-health
    /// poll. Keyed by the PortAudio device id a cue routed audio to; a UI output line maps its device id into this.
    /// Reads a volatile snapshot (republished on fire/stop) then each router's own thread-safe pump stats - no
    /// dispatcher marshaling. Empty when no active cue routes device-addressed audio.</summary>
    public IReadOnlyDictionary<string, (long Enqueued, long Dropped)> GetActiveAudioPumpStatsByDevice()
    {
        var view = _audioPumpsView;
        var result = new Dictionary<string, (long Enqueued, long Dropped)>(StringComparer.Ordinal);
        foreach (var pump in view)
        {
            try
            {
                var st = pump.Router.GetPumpStats(pump.OutputId);
                var cur = result.TryGetValue(pump.DeviceId, out var v) ? v : default;
                result[pump.DeviceId] = (cur.Enqueued + st.Enqueued, cur.Dropped + st.Dropped);
            }
            catch (ArgumentException) { /* output retired between snapshot publish and read */ }
        }

        return result;
    }

    /// <summary>Allocation-free single-device variant of <see cref="GetActiveAudioPumpStatsByDevice"/> for the
    /// per-line 1 Hz health polls (each wants exactly one device id): walks the same lock-free view and sums
    /// only matching pumps instead of building the whole dictionary per poll.</summary>
    public bool TryGetActiveAudioPumpStats(string deviceId, out (long Enqueued, long Dropped) stats)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        long enqueued = 0, dropped = 0;
        var found = false;
        foreach (var pump in _audioPumpsView)
        {
            if (!string.Equals(pump.DeviceId, deviceId, StringComparison.Ordinal))
                continue;
            try
            {
                var st = pump.Router.GetPumpStats(pump.OutputId);
                enqueued += st.Enqueued;
                dropped += st.Dropped;
                found = true;
            }
            catch (ArgumentException) { /* output retired between snapshot publish and read */ }
        }

        stats = (enqueued, dropped);
        return found;
    }

    /// <summary>One active clip's full pipeline snapshot for the debug-stats poll: the transport group it
    /// plays in, the clip id, and the player's <see cref="S.Media.Players.MediaPlayerMetrics"/> (decode
    /// timing, jitter-buffer depth, router mix timing, per-output pump queues/drops/submit timing).</summary>
    public sealed record ActiveClipPipelineMetrics(
        string GroupId,
        string? ClipId,
        S.Media.Players.MediaPlayerMetrics Metrics);

    /// <summary>Lock-free pipeline metrics for every group's active clip - the debug-stats analogue of
    /// <see cref="GetActiveAudioPumpStatsByDevice"/>. Walks the published group view (no dispatcher
    /// marshaling) and reads each player's own thread-safe counters. Empty when nothing is playing.</summary>
    public IReadOnlyList<ActiveClipPipelineMetrics> GetActiveClipPipelineMetrics()
    {
        var views = _groupViews; // single volatile read of the published view
        if (views.Count == 0)
            return [];
        var result = new List<ActiveClipPipelineMetrics>(views.Count);
        foreach (var view in views)
        {
            if (view.Player is not { } player)
                continue;
            try
            {
                result.Add(new ActiveClipPipelineMetrics(
                    view.GroupId,
                    view.Group.ActiveBinding?.ClipId,
                    player.GetMetrics()));
            }
            catch (ObjectDisposedException) { /* clip retired between snapshot publish and read */ }
        }

        return result;
    }

    /// <summary>Lock-free stats for every loaded composition (id → runtime stats) - the multi-composition
    /// variant of <see cref="GetCompositionStats"/> for the debug-stats poll.</summary>
    public IReadOnlyList<ClipCompositionRuntimeStats> GetAllCompositionStats()
    {
        var view = _compositionsView;
        if (view.Count == 0)
            return [];
        var result = new List<ClipCompositionRuntimeStats>(view.Count);
        foreach (var runtime in view.Values)
        {
            try { result.Add(runtime.GetStats()); }
            catch (ObjectDisposedException) { /* retired between snapshot publish and read */ }
        }

        return result;
    }

    /// <summary>Republishes the lock-free composition view after a load/dispose changes <see cref="_compositions"/>.
    /// Call on the dispatcher (the only place <see cref="_compositions"/> is mutated).</summary>
    private void PublishCompositionsView() =>
        _compositionsView = new Dictionary<string, ClipCompositionRuntime>(_compositions, StringComparer.Ordinal);

    /// <summary>Lock-free per-composition stats for a UI health poll - no dispatcher marshaling (mirrors
    /// <see cref="SnapshotAsync"/>). Reads a volatile snapshot of the compositions republished on load, then the
    /// runtime's own thread-safe <c>GetStats</c>. Null when no such composition exists (or it is mid-teardown).</summary>
    public ClipCompositionRuntimeStats? GetCompositionStats(string compositionId)
    {
        if (!_compositionsView.TryGetValue(compositionId, out var runtime))
            return null;
        try { return runtime.GetStats(); }
        catch (ObjectDisposedException) { return null; } // retired between snapshot publish and read
    }
}
