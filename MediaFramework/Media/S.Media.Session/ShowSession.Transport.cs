using S.Media.Core.Diagnostics;
using S.Media.Time;

namespace S.Media.Session;

/// <summary>
/// A cue prepared ahead of an externally scheduled start edge.
/// </summary>
/// <param name="CueId">Compiled cue/clip id.</param>
/// <param name="RuntimeGroupId">Independent transport slot the cue owns.</param>
/// <param name="StartPosition">
/// Optional media-file position to arm at. Null uses the binding's normal trim-in.
/// </param>
public readonly record struct ScheduledCueStart(
    string CueId,
    string RuntimeGroupId,
    TimeSpan? StartPosition = null);

/// <summary>
/// The operator's transport verbs: fire a cue (on the cue graph or independently of it), GO, and seek -
/// one group or several coordinated. All of them marshal onto the session dispatcher (D5), so this file is
/// where "a command arrived" turns into "the dispatcher will do it, in order".
/// <para>Split out of the root file (2026-07-30 review §3). Stops are deliberately NOT here: they have
/// their own claim protocol and their own file, and mixing the two is what made the root file hard to read
/// the fire path out of.</para>
/// </summary>
public sealed partial class ShowSession
{
    // --- transport commands (marshaled - D5) -------------------------------------------------------

    /// <summary>Fires a specific cue by id (PreWait/PostWait/AutoContinue honoured by the cue graph). Runs OFF the
    /// serial dispatcher (NXT-03), so its pre-wait + media open don't park the loop - STOP/seek/load/queries stay
    /// responsive and can abort it.</summary>
    public Task<CueExecutionStatus> FireCueAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCueAsync(cueId);
    }

    /// <summary>Fires a cue with a dual-voice crossfade window (Ideas/Dual-Voice-Crossfade-Design.md): when
    /// the cue's group already holds an active clip, that clip moves to the group's OUTGOING slot - keeping
    /// its outputs/routes/layers - and ramps to silence over <paramref name="crossfade"/> while the incoming
    /// clip fades in over the same window (an implied fade-in when the binding has none; a configured per-cue
    /// FadeIn wins). The outgoing releases at ramp end, or earlier on stop/panic/the next replacement. A null
    /// or non-positive <paramref name="crossfade"/> (or an idle group) is exactly
    /// <see cref="FireCueAsync(string)"/> - the butt-splice path, byte for byte. Transport (pause/seek/
    /// end-monitor) targets the incoming clip only; the outgoing tail is fire-and-forget. An incoming open
    /// failure leaves the current clip untouched (fail loud via the returned status).</summary>
    public Task<CueExecutionStatus> FireCueAsync(
        string cueId, TimeSpan? crossfade, FadeCurve crossfadeCurve = FadeCurve.Linear)
        => FireCueAsync(cueId, crossfade, new FadeShape(crossfadeCurve));

    /// <summary>Fires a cue with a dual-voice crossfade using a built-in or custom shape.</summary>
    public Task<CueExecutionStatus> FireCueAsync(
        string cueId, TimeSpan? crossfade, FadeShape crossfadeCurve)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCueAsync(
            cueId, crossfade is { } duration && duration > TimeSpan.Zero ? (duration, crossfadeCurve) : null);
    }

    /// <summary>
    /// Plays a clip by id on a transport group, with no cue involved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine's cue-free entry point. Everything <see cref="FireCueAsync(string)"/> adds on top of this -
    /// arm/enable checks, pre/post waits, follow-ons, the GO cursor - is cue-list semantics; the act of
    /// playing a clip on a group is not, and a host that has no cue list should not have to fabricate one to
    /// reach it. HaPlay's deck did exactly that, minting a cue per track whose only purpose was to be
    /// looked up and discarded.
    /// </para>
    /// <para>
    /// Replaces whatever the group is playing, exactly as a fired cue would - the group is the slot, so this
    /// is the same transport contract, reached without the cue layer.
    /// </para>
    /// </remarks>
    /// <returns><see cref="CueExecutionStatus.Fired"/>, or <see cref="CueExecutionStatus.NotReady"/> when no
    /// clip has that id.</returns>
    public async Task<CueExecutionStatus> PlayClipAsync(
        string clipId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read the binding table on the dispatcher (it is dispatcher-confined and a load can swap it), then
        // play OFF it - the media open must not park the serial loop (NXT-19).
        var binding = await InvokeAsync(() =>
            Task.FromResult(_clipsById.GetValueOrDefault(clipId))).ConfigureAwait(false);
        if (binding is null)
            return CueExecutionStatus.NotReady;

        await PlayClipAsync(groupId, binding, cancellationToken, waitForStartBarrier: null).ConfigureAwait(false);
        return CueExecutionStatus.Fired;
    }

    /// <summary>Fires one media cue on a caller-owned transport group instead of the group encoded in the
    /// show document. This is the manual-override path used by HaPlay: different children of one authored
    /// group can then play concurrently, while re-firing the same child replaces only its own manual slot.</summary>
    public async Task<CueExecutionStatus> FireCueIndependentAsync(
        string cueId,
        string independentGroupId,
        CancellationToken cancellationToken = default)
        => await FireCueIndependentCoreAsync(
                cueId, independentGroupId, waitForStartBarrier: null, waitForStartEdge: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The coordinated-batch form: identical independent-group semantics, but the armed clip waits at the
    /// caller's barrier before it commits. Kept internal so the public batch API remains the only owner of barrier
    /// participant accounting.</summary>
    internal Task<CueExecutionStatus> FireCueIndependentAtBarrierAsync(
        string cueId,
        string independentGroupId,
        Func<Task>? waitForStartBarrier,
        Func<Task>? waitForStartEdge,
        CancellationToken cancellationToken,
        TimeSpan? initialPosition = null)
    {
        ArgumentNullException.ThrowIfNull(waitForStartBarrier);
        return FireCueIndependentCoreAsync(
            cueId, independentGroupId, waitForStartBarrier, waitForStartEdge, cancellationToken, initialPosition);
    }

    private async Task<CueExecutionStatus> FireCueIndependentCoreAsync(
        string cueId,
        string independentGroupId,
        Func<Task>? waitForStartBarrier,
        Func<Task>? waitForStartEdge,
        CancellationToken cancellationToken,
        TimeSpan? initialPosition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(independentGroupId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_fires.TryGetCue(cueId, out var cue))
            throw new ArgumentException($"cue '{cueId}' is not registered", nameof(cueId));
        if (!cue.Enabled)
            return CueExecutionStatus.SkippedDisabled;
        if (!cue.Armed)
            return CueExecutionStatus.SkippedNotArmed;
        if (!_clipsById.TryGetValue(cueId, out var binding))
            return CueExecutionStatus.NotReady;

        try
        {
            if (cue.PreWait > TimeSpan.Zero)
                await Task.Delay(cue.PreWait, cancellationToken).ConfigureAwait(false);
            await PlayClipAsync(
                    independentGroupId, binding, cancellationToken, waitForStartBarrier, waitForStartEdge,
                    initialPosition: initialPosition)
                .ConfigureAwait(false);
            if (cue.PostWait > TimeSpan.Zero)
                await Task.Delay(cue.PostWait, cancellationToken).ConfigureAwait(false);
            return CueExecutionStatus.Fired;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CueExecutionStatus.Failed;
        }
    }

    /// <summary>Fires several cues together with a coordinated start - the fire-time counterpart of the seek/pause
    /// barriers (NXT-04 start skew / old-engine <c>FireGroupAsync</c> parity). Every cue's clip opens
    /// <em>concurrently</em> instead of each open serializing behind the previous cue's start, so a simultaneous
    /// cue group (the UI's coordinated trigger step) starts together rather than staggered by the sum of the opens
    /// - the cue graph is thread-safe for concurrent fires. All share ONE cancellation source, so a
    /// STOP/LOAD/DISPOSE aborts the whole group. Returns the per-cue statuses in
    /// input order (a cancelled cue reports <see cref="CueExecutionStatus.Failed"/>). Runs OFF the serial
    /// dispatcher (NXT-03) and holds the fire-lock for the whole group so no GO/fire interleaves.</summary>
    public Task<IReadOnlyList<CueExecutionStatus>> FireCuesAsync(IReadOnlyList<string> cueIds)
    {
        ArgumentNullException.ThrowIfNull(cueIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCuesAsync(cueIds);
    }

    /// <summary>Fires several clip-bound cues concurrently on distinct caller-owned runtime groups. Unlike
    /// <see cref="FireCuesAsync"/>, this deliberately does not use each cue's authored <see cref="CueDefinition.GroupId"/>:
    /// callers use it for simultaneously-fired siblings that share one authored group but must each retain an active
    /// clip. The batch holds the normal fire lock, shares cancellation, and returns statuses in input order.</summary>
    public Task<IReadOnlyList<CueExecutionStatus>> FireCuesIndependentAsync(
        IReadOnlyList<(string CueId, string RuntimeGroupId)> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var (cueId, runtimeGroupId) in targets)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cueId, nameof(targets));
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeGroupId, nameof(targets));
        }

        return _fires.FireCuesIndependentAsync(targets, cancellationToken);
    }

    /// <summary>
    /// Prepares several independent cues as one batch, then releases the session fire lock while their
    /// clocks wait at <paramref name="waitForStartEdge"/>. The callback is entered exactly once, after every
    /// viable voice has committed, filled its pre-roll and presented its synchronization frame.
    /// </summary>
    /// <remarks>
    /// This is the absolute-time counterpart of <see cref="FireCuesIndependentAsync"/>. Keeping the
    /// preparation and start edge separate lets a timeline pay decoder/open latency before the authored
    /// cue point. The prepared voices remain silent and their video layers remain hidden until release.
    /// </remarks>
    public Task<IReadOnlyList<CueExecutionStatus>> FireCuesIndependentScheduledAsync(
        IReadOnlyList<ScheduledCueStart> targets,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(waitForStartEdge);
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var target in targets)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target.CueId, nameof(targets));
            ArgumentException.ThrowIfNullOrWhiteSpace(target.RuntimeGroupId, nameof(targets));
        }

        return _fires.FireCuesIndependentScheduledAsync(targets, waitForStartEdge, cancellationToken);
    }

    /// <summary>GO - fires the next armed and enabled cue in <paramref name="groupId"/> after the cursor. A
    /// disabled or unarmed cue is skipped (never fired); the cursor advances only when the chosen cue actually
    /// ran or faulted, so a cue that was momentarily not fireable can still be reached by a later GO (NXT-07).</summary>
    public Task<CueExecutionStatus> GoAsync(string groupId = DefaultGroup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.GoAsync(groupId);
    }

    /// <summary>GO's cursor advance (dispatcher). A no-op when <paramref name="generation"/> no longer matches -
    /// a reload swapped the show between selection and advance, and the fresh show's cursor must not inherit the
    /// old one's progress (the pre-split code got the same outcome by writing to the orphaned group).</summary>
    internal Task AdvanceGoCursorAsync(string groupId, int number, int generation) =>
        InvokeAsync(() =>
        {
            if (_showGeneration == generation)
                _goCursors[groupId] = number;
            return Task.CompletedTask;
        });

    /// <summary>
    /// What GO would fire next on <paramref name="groupId"/> - the list's standby cue - without firing it.
    /// Null when the list has run out or has no armed, enabled cue after its cursor.
    /// </summary>
    /// <remarks>Each cue list is its own transport group, so this is per-list standby: a host (or the
    /// remote API) can show and drive several lists independently off one session.</remarks>
    public Task<CueDefinition?> GetStandbyCueAsync(string groupId = DefaultGroup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.PeekNextAsync(groupId);
    }

    /// <summary>
    /// <see cref="GetStandbyCueAsync"/> for several lists at once, through ONE dispatcher hop.
    /// </summary>
    /// <remarks>One answer per input, same order. This is the form a UI snapshot poll wants: asking
    /// per list queued one round-trip behind the session dispatcher for each, and a busy dispatcher
    /// (a GO, a reload) delayed the whole snapshot by their sum.</remarks>
    public Task<IReadOnlyList<CueDefinition?>> GetStandbyCuesAsync(IReadOnlyList<string> groupIds)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.PeekNextManyAsync(groupIds);
    }

    /// <summary>
    /// Moves a list's GO cursor so <paramref name="cueId"/> becomes standby. Null rewinds to the top.
    /// </summary>
    /// <returns>False when <paramref name="cueId"/> names no cue - the cursor is left alone.</returns>
    /// <remarks>
    /// Sets the position only; nothing is armed, opened or played, so this is safe to call on a list that is
    /// currently sounding - the running clip is untouched and the change takes effect at the next GO.
    /// </remarks>
    public Task<bool> SetStandbyCueAsync(string? cueId, string groupId = DefaultGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() =>
        {
            if (cueId is null)
            {
                _goCursors.Remove(groupId); // back to "nothing fired yet"
                return Task.FromResult(true);
            }

            if (_fires.CursorForStandby(cueId, groupId, DefaultGroup) is not { } cursor)
                return Task.FromResult(false);
            _goCursors[groupId] = cursor;
            return Task.FromResult(true);
        });
    }

    /// <summary>Seeks the active clip on <paramref name="groupId"/> (coordinated A/V seek).</summary>
    public Task SeekAsync(TimeSpan position, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            if (group.ActiveVoice is { } voice)
            {
                // SeekCoordinated pauses+seeks but does NOT resume, so preserve the pre-seek play state: a
                // scrub while playing must keep playing, not freeze. Without this the clip is left paused
                // (IsRunning=false) after every seek, and the media-player deck's poll reads that as "ended"
                // and tears the deck down - i.e. seek "stops playback" (matches SeekManyAsync's resume).
                var wasRunning = voice.Player.IsRunning;
                var masterBeforeSeek = group.Timeline.GetSnapshot().MasterTime;
                SeekCoordinatedRestoringPlayState(
                    voice.Player, position, group, masterBeforeSeek, resume: wasRunning, voice);
            }
            return Task.CompletedTask;
        });

    /// <summary>
    /// Coordinated seek that can never strand the clip paused: <c>SeekCoordinated</c> pauses BEFORE it
    /// seeks, so a decode/demux fault thrown by the seek (observed live: <c>avcodec_send_packet</c>
    /// EINVAL on some codecs) used to skip the resume AND the discontinuity mark - the clip sat frozen
    /// with no error shown, the deck's poll read it as ended, and every later seek failed the same way.
    /// On failure this restores the pre-seek play state (best effort - the demux stays wherever the
    /// failed seek left it) and still marks the discontinuity (the source position may have partially
    /// moved), THEN rethrows so the caller's task surfaces the fault.
    /// </summary>
    /// <summary>A SOURCE position expressed on the clip-time basis the automation lanes are drawn against -
    /// the same <c>sourceTime - TrimStart</c> clamp <see cref="TransportTimeline"/> applies, evaluated for a
    /// position the timeline has not adopted yet.</summary>
    private static TimeSpan ClipTimeOf(TransportGroup group, TimeSpan sourcePosition)
    {
        var snapshot = group.Timeline.GetSnapshot();
        var clipTime = sourcePosition - snapshot.TrimStart;
        if (clipTime < TimeSpan.Zero)
            clipTime = TimeSpan.Zero;
        if (snapshot.TrimEnd is { } trimEnd)
        {
            var clipEnd = trimEnd - snapshot.TrimStart;
            if (clipTime > clipEnd)
                clipTime = clipEnd;
        }

        return clipTime;
    }

    /// <param name="voice">When supplied, every automation lane on the clip is re-sampled at the sought
    /// position BEFORE playback resumes. The lane runners tick only every 25 ms, so without this a scrub
    /// into the middle of a fade resumed at the PRE-seek value and corrected audibly a tick later.</param>
    private static void SeekCoordinatedRestoringPlayState(
        S.Media.Players.MediaPlayer player,
        TimeSpan position,
        TransportGroup group,
        TimeSpan masterBeforeSeek,
        bool resume,
        TransportVoice? voice = null)
    {
        try
        {
            player.SeekCoordinated(position);
            // Derived from the seek TARGET, not from the timeline: the timeline only re-anchors on the
            // MarkDiscontinuity below, so its snapshot still reports the pre-seek position here. This runs
            // before Play(), i.e. before any audio or video produced at the new position can reach a device.
            voice?.SeedAutomationAt(ClipTimeOf(group, position));
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, $"ShowSession: coordinated seek to {position} failed; restoring play state");
            try
            {
                if (resume && !player.IsRunning)
                    player.Play();
            }
            catch (Exception resumeEx)
            {
                MediaDiagnostics.LogError(resumeEx, "ShowSession: resume after a failed seek also failed");
            }
            group.Timeline.MarkDiscontinuity(masterBeforeSeek);
            throw;
        }

        if (resume)
            player.Play();
        group.Timeline.MarkDiscontinuity(masterBeforeSeek); // source jumps; master stays monotonic (NXT-04)
    }

    /// <summary>Seeks several groups together behind one shared epoch - the group-seek barrier (NXT-04 /
    /// old-engine <c>group_seek_barrier</c> parity). Every target group is paused first so its clock freezes,
    /// each is seeked (coordinated), then the ones that were running resume together - so a multi-cue seek lands
    /// atomically instead of each group seeking (and drifting while the others keep advancing) in turn. Runs as
    /// one dispatcher operation, so no other transport command interleaves between the seeks. Groups with no
    /// active clip are skipped; a repeated group id just re-seeks its one active player (last position wins).</summary>
    public Task SeekManyAsync(IReadOnlyList<(string GroupId, TimeSpan Position)> seeks)
    {
        ArgumentNullException.ThrowIfNull(seeks);
        if (seeks.Count == 0)
            return Task.CompletedTask;
        return InvokeAsync(() =>
        {
            // 1) Freeze every target's clock (shared epoch) so a slow demux seek on one group can't let another
            //    group's playhead run on past it. Remember which were running so paused cues stay paused.
            var targets = new List<(
                TransportGroup Group,
                S.Media.Players.MediaPlayer Player,
                TimeSpan Position,
                bool Resume,
                TimeSpan MasterBeforeSeek,
                TransportVoice Voice)>(seeks.Count);
            List<Exception>? errors = null;
            foreach (var (groupId, position) in seeks)
            {
                var group = GetOrAddGroup(groupId);
                if (group.ActiveVoice is not { } voice)
                    continue;
                var wasRunning = voice.Player.IsRunning;
                var masterBeforeSeek = group.Timeline.GetSnapshot().MasterTime;
                if (wasRunning)
                {
                    try
                    {
                        voice.Player.Pause();
                    }
                    catch (Exception ex)
                    {
                        // Skip this group's seek (its clock never froze) but keep the barrier for the rest.
                        MediaDiagnostics.LogError(ex, $"ShowSession: group seek pause failed for group '{groupId}'; skipping its seek");
                        (errors ??= []).Add(ex);
                        continue;
                    }
                }
                targets.Add((group, voice.Player, position, wasRunning, masterBeforeSeek, voice));
            }

            // 2) Seek all with clocks frozen, then 3) release the running ones together from the shared epoch.
            // A failing seek must not break the barrier: the other groups still seek, and EVERY paused group
            // still resumes (a faulted one from its pre-seek position) - a fault used to leave every
            // not-yet-seeked group stranded paused with no error surfaced.
            foreach (var (group, player, position, _, _, voice) in targets)
            {
                try
                {
                    player.SeekCoordinated(position);
                    // Still inside the barrier, before phase 3 resumes anything: every lane lands on its
                    // sought value before a sample or frame from the new position can reach a device.
                    voice.SeedAutomationAt(ClipTimeOf(group, position));
                }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, $"ShowSession: group seek to {position} failed; the clip resumes from its pre-seek position");
                    (errors ??= []).Add(ex);
                }
            }
            // 3) Resume in TWO PHASES, exactly as a group fire does. player.Play() is prepare-and-start in
            //    one call, and the prepare half is slow: decode spin-up, the video jitter-buffer wait, and
            //    a sync present worth up to 250 ms EACH. Resuming with it in a loop therefore started
            //    voice N's clock only after voices 1..N-1 had each finished their whole slow half, so the
            //    barrier that had just frozen every clock together released them staggered - measured
            //    live as ~57 ms per voice, ~700 ms across a 13-cue group, with positions descending in
            //    list order. The pause/seek half was atomic and the resume threw that away.
            //
            //    This is the same defect PreparePlay was introduced to fix on the FIRE path (see
            //    AvPlaybackCoordinator/VoiceStartPolicy: "every voice queued behind a video sibling
            //    started late by that sibling's present-sync"); the seek path simply never adopted it.
            var starters = new List<(TransportGroup Group, Action? Start, TimeSpan MasterBeforeSeek)>(targets.Count);
            foreach (var (group, player, _, resume, masterBeforeSeek, _) in targets)
            {
                if (!resume)
                {
                    starters.Add((group, null, masterBeforeSeek));
                    continue;
                }

                try
                {
                    starters.Add((group, player.PreparePlay(), masterBeforeSeek));
                }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, "ShowSession: group seek resume preparation failed");
                    (errors ??= []).Add(ex);
                    starters.Add((group, null, masterBeforeSeek));
                }
            }

            // The start edge: every prepared voice's clock starts here, back to back, with nothing slow
            // in between. What remains is the cost of the starts themselves (microseconds), not of the
            // preparations.
            foreach (var (group, start, masterBeforeSeek) in starters)
            {
                if (start is not null)
                {
                    try
                    {
                        start();
                    }
                    catch (Exception ex)
                    {
                        MediaDiagnostics.LogError(ex, "ShowSession: group seek resume failed");
                        (errors ??= []).Add(ex);
                    }
                }

                group.Timeline.MarkDiscontinuity(masterBeforeSeek); // all masters preserve the shared pre-seek epoch
            }

            if (errors is not null)
            {
                if (errors.Count == 1)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
                throw new AggregateException("One or more group seeks failed (play state was restored).", errors);
            }

            return Task.CompletedTask;
        });
    }

    /// <see cref="FadeClipAsync"/> calls compose from.</summary>
    public Task<float?> GetClipFadeLevelAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() => Task.FromResult(
            ActiveVoiceOf(cueId) is { } voice ? (float?)voice.ClipLevel : null));
    }

    /// <summary>The live audio levels of the active clip playing <paramref name="cueId"/>, or null when
    /// that cue is not an active clip. <see cref="ClipAudioLevels.EffectiveLevel"/> is the exact product
    /// the route gains are written with (fade × envelope × modifier × master trim), read from the
    /// same group state.</summary>
    public Task<ClipAudioLevels?> GetClipAudioLevelsAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() => Task.FromResult(
            ActiveVoiceOf(cueId) is { } voice
                ? new ClipAudioLevels(
                    voice.ClipLevel, voice.EnvelopeLevel, voice.ModifierLevel, voice.EffectiveAudioLevel,
                    voice.BaseLevel)
                : null));
    }

    /// <summary>Everything the session is currently feeding audio to, with the classification the master
    /// fader, stop-all and Panic key off: transport groups and soundboard voices as
    /// <see cref="SoundingSourceRole.Program"/>, the cue preview and audio taps as
    /// <see cref="SoundingSourceRole.Monitoring"/>. A host status panel reads it; it is also the only way
    /// to observe that monitoring is registered on the bus yet excluded from all three.</summary>
    public Task<IReadOnlyList<SoundingSourceInfo>> GetSoundingSourcesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() => Task.FromResult(_sounding.Snapshot()));
    }
}
