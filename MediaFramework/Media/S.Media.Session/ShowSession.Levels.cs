using S.Media.Routing;

namespace S.Media.Session;

/// <summary>
/// The level authorities that are not stops: the session master trim driven across the sounding bus, the
/// fade CUE (an absolute ramp to an arbitrary level, audio and/or layer opacity), and pause/resume.
/// <para>Split out of the root file (2026-07-30 review §3). The one rule that binds them, and the reason
/// they belong together rather than next to the stop fades: every one of them composes through
/// <see cref="SoundingLevel"/> - <c>master x source x fade x envelope</c> - instead of writing a route gain
/// directly, which is what stops the four mechanisms overwriting each other.</para>
/// </summary>
public sealed partial class ShowSession
{
    private static readonly TimeSpan FadeStepInterval = FadeRamp.DefaultStepInterval;

    /// <summary>Ramps each route's gain from silence up to its configured target over <paramref name="duration"/>
    /// (the clip was attached silent). The ramp fraction multiplies each route's <c>TargetGain</c>, so a route
    /// set below or above unity fades up to exactly that level rather than to a hardcoded 1.0 (NXT-07).
    /// With <paramref name="fadesVideo"/> the clip's composition layers ride the SAME ramp from black
    /// (they attached at opacity 0) up to their authored opacities (<see cref="TransportGroup.BaseLayerOpacities"/>)
    /// - the incoming half of a dual-voice crossfade, and parity with every downward fade (stop, natural,
    /// outgoing tail), which always ramped opacity alongside audio; a plain per-cue FadeIn on a placed clip
    /// gets the same audio+video ramp (the audio-only behavior before this was an omission, not a choice -
    /// only the fade CUE keeps an explicit video opt-out, <c>AlsoFadeVideoOpacity</c>).
    /// A <see cref="FadeRamp"/>; cancelled when the clip is replaced. The ramp holds the group's clip-fade
    /// slot: a Fade cue fired during the fade-in window preempts it via <see cref="TransportGroup.BeginClipFade"/>
    /// and composes from whatever level the fade-in reached, and a claimed fade-out (operator stop/natural end)
    /// stops it - without either, two 25 ms ramps would alternately overwrite the clip level and the fade-in's
    /// final full-level step would destroy the fade cue's result.</summary>
    private void StartFadeIn(string groupId, TransportVoice voice,
        IReadOnlyList<AudioRouteTarget> routes, TimeSpan duration, FadeShape curve, bool fadesVideo,
        CancellationToken ct)
    {
        var player = voice.Player;
        if (player.AudioSourceId is null && !fadesVideo)
            return;

        // Dispatcher-confined caller (the fire path); a fresh voice can't have an in-flight fade cue,
        // so this claim never cancels anything.
        var slotToken = voice.BeginClipFade();
        FadeRamp.Start(FadeStepInterval, ct, elapsed => InvokeAsync<bool>(() =>
        {
            if (ct.IsCancellationRequested ||
                slotToken.IsCancellationRequested || // a Fade cue took the clip's level over
                !_groups.ContainsKey(groupId) ||
                voice.State != VoiceState.Active ||   // replaced, or handed off to a crossfade tail
                voice.IsFadeOutClaimed ||            // a stop/natural fade-out owns the level now
                (player.AudioRouter is null && !fadesVideo))
            {
                voice.EndClipFade(slotToken);
                return Task.FromResult(true);
            }
            var frac = FadeRamp.LevelUp(elapsed, duration, curve);
            // Audio leg = ApplyAudioScale(frac), exactly as before; the opacity leg ramps each layer
            // from 0 toward its authored value (base × frac) - the mirror of the stop fade's ramp down.
            voice.ApplyFadeLevel(routes, 1f, voice.BaseLayerOpacities, frac);
            if (frac < 1f)
                return Task.FromResult(false);
            voice.EndClipFade(slotToken);
            return Task.FromResult(true);
        }));
    }

    /// <summary>Per-clip volume-envelope runner: a <see cref="FadeRamp"/>-style loop (same 25 ms step as
    /// every session fade) that samples the envelope at the clip's CURRENT position - the group timeline's
    /// <see cref="TransportTimelineSnapshot.CueTime"/>, i.e. post-StartOffset clip time, so the automation
    /// survives seeks and restarts each loop pass - and writes the factor through the group's one
    /// level-composition path (<see cref="TransportGroup.ApplyEnvelopeLevel"/> → <c>ApplyAudioScale</c>,
    /// where fade × envelope multiply). Runs for the clip's whole life: cancelled on replacement (the
    /// shared clip-work cts) or ended by its identity guard. Never started for an empty envelope.</summary>
    private void StartEnvelopeRunner(
        string groupId,
        TransportVoice voice,
        IReadOnlyList<ShowEnvelopePoint> envelope,
        CancellationToken ct)
    {
        if (voice.Player.AudioSourceId is null)
            return;

        FadeRamp.Start(FadeStepInterval, ct, _ => InvokeAsync<bool>(() =>
        {
            // Active only: the group timeline the envelope samples follows the ACTIVE voice, so a voice
            // handed off to a crossfade tail keeps the level its automation had reached.
            if (ct.IsCancellationRequested ||
                _groups.GetValueOrDefault(groupId) is not { } group ||
                !ReferenceEquals(group.ActiveVoice, voice) ||
                voice.Player.AudioRouter is null)
                return Task.FromResult(true);
            var clipPosition = group.Timeline.GetSnapshot().CueTime;
            voice.ApplyEnvelopeLevel(VolumeEnvelopes.Sample(envelope, clipPosition));
            return Task.FromResult(false); // no target to reach - the envelope rides the clip until release
        }));
    }



    // --- master trim --------------------------------------------------------------------------------

    /// <summary>The current session-wide master trim (1 = unity). Written only on the session
    /// dispatcher by <see cref="SetMasterTrimAsync"/> and read from any thread (a host polls it to
    /// draw the fader), so both ends go through <see cref="Volatile"/>: a float write is atomic, but
    /// atomicity is not VISIBILITY - a plain read may be hoisted out of a polling loop and show a
    /// stale value indefinitely.</summary>
    public float MasterTrim => Volatile.Read(ref _masterTrim);

    private float _masterTrim = 1f;

    /// <summary>
    /// Sets the session-wide manual master trim - the live "bring the show down by hand" fader
    /// (Ideas/CuePlayer-Enhancements.md §6). One clamped scalar [0, 1] that MULTIPLIES every PROGRAM
    /// source's routed audio through the same <see cref="SoundingLevel"/> composition as fades and
    /// envelopes, so it composes with (never overwrites) the levels those mechanisms ride:
    /// <c>master × source × fade × envelope</c>. It walks the session's level/stop bus, so it reaches
    /// transport cues and soundboard voices in ONE enumeration; the cue preview and audio taps register
    /// as monitoring and are deliberately untouched (the operator's audition path must not duck when the
    /// show is pulled down). Session-level state, not per-source - sources that start AFTER a trim change
    /// inherit it, and a crossfade's outgoing tail rides it too (unless a stop fade already claimed that
    /// tail and owns its levels). This is a manual trim, not a stop: it is never persisted anywhere.
    /// </summary>
    public Task SetMasterTrimAsync(float scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() =>
        {
            var clamped = float.IsNaN(scale) ? 1f : Math.Clamp(scale, 0f, 1f);
            if (clamped == _masterTrim) // dispatcher-confined write ⇒ a plain read is fine HERE
                return Task.CompletedTask;
            Volatile.Write(ref _masterTrim, clamped);
            foreach (var source in _sounding.ProgramSources())
                source.ApplyMasterTrim!(clamped);
            return Task.CompletedTask;
        });
    }

    /// <summary>Fades the cue with <paramref name="cueId"/> (wherever it is the active clip) to
    /// <paramref name="targetLevel"/> over <paramref name="duration"/> - the Fade-cue entry point.
    /// The ramp starts from the clip's CURRENT level (<see cref="TransportGroup.ClipLevel"/>), so
    /// consecutive fades compose (fade to −10 dB then to silence starts at −10 dB) and a fade UP from a
    /// reduced level works. Identity-guarded like the stop fade: a clip replaced mid-ramp is left alone.
    /// A newer fade on the same clip preempts this ramp and takes over from its level; an operator
    /// stop/natural fade-out (the <see cref="TransportVoice.TryClaimFadeOut"/> claim) also preempts it.
    /// The returned task completes when the ramp ends (and, for a silent stop, the clip is released) or
    /// when the fade was preempted. No-op when the cue isn't playing.</summary>
    /// <param name="targetLevel">Absolute level [0,1] (1 = the clip's full authored gain/opacity).</param>
    /// <param name="stopWhenSilent">With target 0: release the clip at ramp end (the stop-release path);
    /// false keeps it running silently.</param>
    /// <param name="alsoFadeVideo">Ramp the clip's composition-layer opacities in step (toward the
    /// authored opacity × target level); false fades audio only.</param>
    public async Task FadeClipAsync(
        string cueId,
        float targetLevel,
        TimeSpan duration,
        FadeCurve curve = FadeCurve.Linear,
        bool stopWhenSilent = true,
        bool alsoFadeVideo = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(cueId);
        targetLevel = Math.Clamp(targetLevel, 0f, 1f);

        IReadOnlyList<ClipFade> claims;
        try
        {
            // Claim on the dispatcher: capture each targeted clip's current level as the ramp start and
            // take the group's clip-fade slot (cancelling a previous fade cue's ramp on the same clip).
            claims = await InvokeAsync(() =>
            {
                var list = new List<ClipFade>();
                foreach (var group in _groups.Values)
                {
                    // A fade cue targets the voice transport points at, deliberately: a tail is already on
                    // its way out under a ramp of its own (StopCueAsync is the path that reaches one).
                    if (group.ActiveVoice is not { } voice
                        || !string.Equals(voice.Clip.Spec.Id, cueId, StringComparison.Ordinal)
                        || voice.IsFadeOutClaimed) // a stop/natural fade-out owns this voice's levels
                        continue;
                    list.Add(new ClipFade(
                        group, voice, voice.RouteTargets, voice.ClipLevel,
                        voice.CaptureLayerOpacities(), voice.BaseLayerOpacities, voice.BeginClipFade()));
                }

                return Task.FromResult<IReadOnlyList<ClipFade>>(list);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // session disposed while the fade was queued - disposal owns the teardown
        }

        if (claims.Count == 0)
            return;

        try
        {
            await FadeRamp.RunAsync(FadeStepInterval, CancellationToken.None, elapsed => InvokeAsync(() =>
            {
                var applied = false;
                foreach (var fade in claims)
                {
                    if (fade.Token.IsCancellationRequested            // a newer fade cue took this voice over
                        || !ReferenceEquals(fade.Group.ActiveVoice, fade.Voice) // replaced/ended mid-ramp
                        || fade.Voice.IsFadeOutClaimed)              // an operator stop preempts the fade cue
                        continue;
                    var audioLevel = LevelBetween(fade.StartLevel, targetLevel, elapsed, duration, curve);
                    float[]? opacities = null;
                    if (alsoFadeVideo)
                    {
                        opacities = new float[fade.StartLayerOpacities.Count];
                        for (var i = 0; i < opacities.Length; i++)
                        {
                            var baseOpacity = i < fade.BaseLayerOpacities.Count ? fade.BaseLayerOpacities[i] : 0f;
                            opacities[i] = LevelBetween(
                                fade.StartLayerOpacities[i], baseOpacity * targetLevel, elapsed, duration, curve);
                        }
                    }

                    fade.Voice.ApplyClipFadeLevel(fade.RouteTargets, audioLevel, opacities);
                    applied = true;
                }

                return Task.FromResult(!applied || elapsed >= duration);
            })).ConfigureAwait(false);

            await InvokeAsync(async () =>
            {
                foreach (var fade in claims)
                {
                    fade.Voice.EndClipFade(fade.Token);
                    if (fade.Token.IsCancellationRequested
                        || !ReferenceEquals(fade.Group.ActiveVoice, fade.Voice)
                        || fade.Voice.IsFadeOutClaimed)
                        continue; // preempted - the newer fade / the stop owns the voice from here
                    // Silent stop: reuse the stop-release path, claiming the fade-out slot first so a
                    // concurrent stop can't start a second (inaudible) ramp on the released voice. Only THIS
                    // voice goes: a tail still fading beside it keeps its own ramp (releasing the active
                    // voice used to hard-cut the tail with it - an audible click).
                    // The voice is AT silence, so this claim is due now - nothing a concurrent stop could
                    // still ramp is louder, and an immediate deadline means no later stop can take it back.
                    if (stopWhenSilent && targetLevel <= 0f
                        && fade.Voice.TryClaimFadeOut(DateTime.UtcNow) is not null)
                        await ReleaseVoiceAsync(fade.Group, fade.Voice).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // session disposed mid-fade - disposal owns the teardown
        }
    }

    /// <summary>Soft-stops persistent visualizer layers (all, or one composition's) over
    /// <paramref name="duration"/> - non-positive detaches without a ramp. The captured slot identity makes the
    /// final detach safe when a new visualizer is fired onto the same composition while the old one is fading.</summary>
    private async Task FadeOutAndRemoveVisualizersAsync(
        string? compositionId, TimeSpan duration, FadeCurve curve = FadeCurve.Linear)
    {
        try
        {
            var fades = await InvokeAsync(() =>
                    Task.FromResult(_visualizers.CaptureForFade(compositionId)))
                .ConfigureAwait(false);
            if (fades.Count == 0)
                return;

            await FadeRamp.RunAsync(FadeStepInterval, CancellationToken.None, elapsed => InvokeAsync(() =>
            {
                var level = FadeRamp.LevelDown(elapsed, duration, curve);
                var applied = _visualizers.ApplyFadeLevel(fades, level);
                return Task.FromResult(!applied || level <= 0f);
            })).ConfigureAwait(false);

            await InvokeAsync(() =>
            {
                _visualizers.FinalizeFade(fades);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Session disposal owns every remaining surface and tap.
        }
    }

    /// <summary>Pauses or resumes the active clip on <paramref name="groupId"/> - a seamless toggle (codec
    /// pipelines are not flushed, so resume continues from the same frame, matching the GUI engine's
    /// <c>SkipFlush</c> pause). On pause the player's playhead - and therefore the group's session clock +
    /// transport-snapshot position - freezes; resume continues from there (the playback-clock freeze
    /// contract). No-op when the group has no active clip.</summary>
    public Task SetPausedAsync(bool paused, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            if (group.ActiveVoice is { } voice)
            {
                group.PausedByHost = paused; // end-monitor stall detection must not read a host pause as EOF
                // Announce the state change BEFORE applying it: resume's Play() prefills + starts audio
                // hardware, holding IsRunning=false for a while - lock-free snapshot consumers (the deck's
                // 250 ms end poll) key their debounce off the generation, so bumping it up front resets
                // their window at op START instead of only after the (potentially slow) apply.
                group.Timeline.MarkDiscontinuity();
                if (paused)
                    voice.Player.Pause();
                else
                    voice.Player.Play();
                group.Timeline.MarkDiscontinuity(); // rate/state change re-anchors the contract (NXT-04)
            }

            return Task.CompletedTask;
        });

    /// <summary>Pauses or resumes EVERY active transport group together - the all-groups form the UI drives, and
    /// the pause parity of <see cref="StopAllAsync"/>. The single-group <see cref="SetPausedAsync"/> only touches
    /// one group, so a multi-group cue show (cues fired onto several groups) would leave the other groups running
    /// when paused. Runs as one dispatcher operation so the groups toggle behind one epoch.</summary>
    public Task SetAllPausedAsync(bool paused) =>
        InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.ActiveVoice is { } voice)
                {
                    group.PausedByHost = paused; // see SetPausedAsync - keeps the end monitor's stall check honest
                    group.Timeline.MarkDiscontinuity(); // announce BEFORE the slow apply (see SetPausedAsync)
                    if (paused)
                        voice.Player.Pause();
                    else
                        voice.Player.Play();
                    group.Timeline.MarkDiscontinuity(); // see SetPausedAsync (NXT-04)
                }

            return Task.CompletedTask;
        });
}
