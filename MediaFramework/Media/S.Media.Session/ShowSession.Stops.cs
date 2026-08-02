using S.Media.Core.Diagnostics;
using S.Media.Routing;

namespace S.Media.Session;

/// <summary>
/// Every path that takes sound DOWN: the group stop, stop-all/Panic across the level bus, the per-cue stop,
/// and the shared claim/ramp machinery all three drive.
/// <para>Split out of the root file (2026-07-30 review §3). It earns its own file because it is one closed
/// argument - who owns a voice while two stops want it, and what "stopped means stopped" obliges each caller
/// to wait for - and that argument was previously interleaved with seeks above it and fade cues below it.
/// The protocol itself lives in <see cref="SoundingStopClaim"/>, shared with the soundboard's stop domain.</para>
/// </summary>
public sealed partial class ShowSession
{
    /// <summary>Soft-stops and releases the active clip on <paramref name="groupId"/>. The cue's configured
    /// fade-out is used, falling back to <paramref name="fadeDuration"/> and then <see cref="DefaultStopFade"/>.
    /// Cancels any in-flight cue fire first so STOP never waits behind a long pre-wait/open (NXT-03). The fade
    /// ramp itself runs OFF the serial dispatcher (NXT-18) - only short gain/opacity steps and the final release
    /// marshal onto it - so other commands (pause, seek, load, a following GO's commit) never queue behind a
    /// long configured fade. The returned task still completes only after the clip is released ("stopped means
    /// stopped").</summary>
    /// <param name="fade">When true (the cue Stop/Panic default) the group's audio + layers ramp to silence over
    /// its <see cref="ShowClipBinding.FadeOut"/> or the stop fade first; when false the clip is
    /// cut immediately (the media-player deck stops hard - it has no soft-stop fade).</param>
    /// <param name="fadeDuration">Stop-fade override for clips with no configured <see cref="ShowClipBinding.FadeOut"/>
    /// (a configured clip fade-out always wins - per-cue > host stop setting). Null = <see cref="DefaultStopFade"/>;
    /// a non-positive value hard-cuts like <paramref name="fade"/> false.</param>
    /// <param name="curve">Gain curve for the stop fade. A clip whose own <see cref="ShowClipBinding.FadeOut"/>
    /// wins the duration precedence also keeps its own <see cref="ShowClipBinding.FadeOutCurve"/>.</param>
    public Task StopAsync(
        string groupId = DefaultGroup,
        bool fade = true,
        TimeSpan? fadeDuration = null,
        FadeCurve curve = FadeCurve.Linear)
    {
        _fires.CancelActiveFire();
        // An explicit non-positive duration hard-cuts even past a configured clip FadeOut (Panic
        // semantics), matching StopAllAsync and this method's own contract.
        if (fadeDuration is { } explicitDuration && explicitDuration <= TimeSpan.Zero)
            fade = false;
        // Every voice in the group: the clip transport points at AND any tail still fading under it. A group
        // stop is "take this group down", so the tail rides the same stop clock rather than playing on.
        return StopVoicesCoreAsync(() => VoicesOf(GetOrAddGroup(groupId)), fade, fadeDuration, curve);
    }

    /// <summary>Stops every PROGRAM source together - the HaPlay Stop/Panic entry point. Both drive this one
    /// method (Panic differs only by resolving a 0 ms fade), so they have identical reach by construction:
    /// whatever the master fader covers, both stop. It walks the same level/stop bus enumeration the fader
    /// does, so transport cues AND soundboard voices come down; the cue preview and audio taps are monitoring
    /// and keep running (killing the operator's audition on Panic would blind the person driving). Per-source
    /// stops run concurrently and each computes its level from its own duration, exactly as the single stop
    /// clock did for several groups.</summary>
    /// <param name="fadeDuration">Same precedence as <see cref="StopAsync"/>: per-clip fade-out > this override >
    /// <see cref="DefaultStopFade"/>. A non-positive value is a hard cut (Panic with a 0 ms setting) - clips
    /// release and visualizers detach without a ramp.</param>
    public async Task StopAllAsync(TimeSpan? fadeDuration = null, FadeCurve curve = FadeCurve.Linear)
    {
        _fires.CancelActiveFire();
        var request = new SoundingStopRequest(fadeDuration ?? DefaultStopFade, curve);
        IReadOnlyList<SoundingSourceRegistration> program;
        try
        {
            program = await InvokeAsync(() =>
            {
                // Panic also means "nothing new starts": a voice whose media is still opening is not on the
                // bus yet (nothing sounds), so the enumeration alone would let it begin playing right after
                // the stop. Cue fires are already preempted by CancelActiveFire above.
                _voicePlayer.CancelPendingVoiceOpens();
                return Task.FromResult(_sounding.ProgramSources());
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // disposed before the stop was claimed - disposal releases every source itself
        }

        // Visualizers are persistent composition surfaces rather than sounding sources. Fade them on the same
        // stop clock instead of detaching them immediately: otherwise an opaque/partly-opaque visualizer vanishes
        // in one frame and exposes the still-fading media layer beneath it as a visible brightness flash.
        var stops = program
            .Select(source => StopSoundingSourceAsync(source, request))
            .Append(FadeOutAndRemoveVisualizersAsync(
                compositionId: null, request.Fade ? request.FadeDuration : TimeSpan.Zero, curve));
        await Task.WhenAll(stops).ConfigureAwait(false);
    }

    /// <summary>Runs one program source's stop, surfacing a failure through <see cref="PlaybackAlert"/> instead
    /// of faulting the whole stop: under Panic the operator needs every OTHER source to still come down, and a
    /// silent swallow is what made a stuck source invisible before.</summary>
    private async Task StopSoundingSourceAsync(SoundingSourceRegistration source, SoundingStopRequest request)
    {
        try
        {
            await source.Stop!(request).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // session disposed mid-stop - disposal owns the teardown
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, $"ShowSession: stop-all/panic failed for sounding source '{source.Label}'");
            // The alert's CueId is the SUBJECT (the cue id / soundboard tile id), never the bus label: the host
            // resolves it to a name for the operator and falls back to the raw string, so a label like
            // "voice:5c1e…" surfaced as gibberish where a cue name belongs. The label still names the exact bus
            // entry in the message and the log.
            RaisePlaybackAlert(new ShowPlaybackAlert(
                source.SubjectId, OutputId: null,
                $"stop-all/panic could not stop '{source.Label}': {ex.Message}", ex));
        }
    }

    /// <summary>Soft-stops the persistent visualizer on one composition. Visualizer Stop cues use the
    /// same fade clock as global Stop/Panic instead of detaching the surface in a single frame.</summary>
    /// <param name="fadeDuration">Fade length for this surface (a Stop cue passes its own transition time).
    /// Null = <see cref="DefaultStopFade"/>; non-positive detaches without a ramp.</param>
    public Task FadeOutCompositionVisualizerAsync(string compositionId, TimeSpan? fadeDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compositionId);
        return FadeOutAndRemoveVisualizersAsync(compositionId, fadeDuration ?? DefaultStopFade);
    }

    /// <summary>Stops the cue with <paramref name="cueId"/> wherever it is playing - as the group's active
    /// clip OR as a crossfade tail still fading out under a newer cue (per-cue stop / cancel - the GUI's
    /// <c>CancelCueCallback</c>). It targets the cue's own VOICES, so stopping the tail leaves the incoming
    /// clip playing and vice versa. No-op when that cue isn't currently sounding anywhere.</summary>
    public Task StopCueAsync(string cueId)
    {
        _fires.CancelActiveFire();
        return StopVoicesCoreAsync(
            () => [.. _groups.Values.SelectMany(
                group => group.Voices
                    .Where(voice => string.Equals(voice.Clip.Spec.Id, cueId, StringComparison.Ordinal))
                    .Select(voice => (group, voice)))],
            fade: true);
    }

    /// <summary>Every voice of one group as stop targets - the group-wide stop's selection.</summary>
    private static IReadOnlyList<(TransportGroup Group, TransportVoice Voice)> VoicesOf(TransportGroup group) =>
        [.. group.Voices.Select(voice => (group, voice))];

    /// <summary>What one stop resolved on the dispatcher at claim time: the voice, its ramp when this stop
    /// won the fade claim, and <see cref="Token"/> - the claim this stop holds.
    /// <para><see cref="Token"/> null means another stop (or a natural fade-out) whose deadline is no later
    /// owns this voice; a token that is CANCELLED by the time the ramp ends means a later, earlier-deadline
    /// stop superseded us mid-ramp. Either way we must not release the voice: the owner does, and we wait for
    /// it. Releasing a voice this stop does not own is what chopped the owner's ramp off mid-fade.</para></summary>
    private sealed record VoiceStopClaim(
        TransportGroup Group, TransportVoice Voice, VoiceStopFade? Fade, CancellationToken? Token,
        DateTime Deadline)
    {
        /// <summary>True when this stop still holds the claim, so it owns the release.</summary>
        public bool OwnsRelease => Token is { IsCancellationRequested: false };
    }

    /// <summary>One claimed voice's stop ramp: it runs from the level and opacities the voice held AT CLAIM
    /// TIME, so claiming a voice mid-ramp (a fade cue, or a crossfade tail part-way down) composes on top of
    /// what that ramp reached instead of popping the voice back up to full level.</summary>
    private sealed record VoiceStopFade(
        TransportVoice Voice,
        TimeSpan Duration,
        FadeCurve Curve,
        float StartAudioScale,
        IReadOnlyList<float> StartLayerOpacities,
        IReadOnlyList<AudioRouteTarget> RouteTargets,
        CancellationToken Token);

    /// <summary>The shared stop path (NXT-18): resolves the target VOICES and claims their fades ON the
    /// dispatcher, ramps OFF it (<see cref="RunStopFadeAsync"/>), then releases each claimed voice back ON the
    /// dispatcher - identity-guarded by the voice's own state, so a cue fired DURING the fade survives (a stop
    /// only releases the voices it saw at claim time). A voice whose fade claim lost to an in-flight natural
    /// fade-out (or to a concurrent stop whose deadline is no later) skips the ramp AND the release - that stop
    /// owns both - and instead waits for it to finish, so this call still returns only once the voice is gone.
    /// A stop that is superseded mid-ramp becomes a waiter the same way. <paramref name="selectVoices"/> runs
    /// on the dispatcher.</summary>
    private async Task StopVoicesCoreAsync(
        Func<IReadOnlyList<(TransportGroup Group, TransportVoice Voice)>> selectVoices,
        bool fade,
        TimeSpan? fadeDuration = null,
        FadeCurve curve = FadeCurve.Linear)
    {
        var claims = await InvokeAsync(() =>
        {
            var targets = selectVoices();
            var list = new List<VoiceStopClaim>(targets.Count);
            var claimedAt = DateTime.UtcNow;
            foreach (var (group, voice) in targets)
            {
                // Duration precedence: per-clip FadeOut > the caller's stop-fade override > session default.
                // A clip fading on its OWN duration keeps its own curve too. Resolved BEFORE the claim
                // because the deadline it implies is what decides who owns the voice: a hard cut lands now,
                // so it supersedes every ramp, and between two ramps the shorter one wins.
                var clipFadeWins = voice.Binding.FadeOut > TimeSpan.Zero;
                var duration = fade
                    ? clipFadeWins ? voice.Binding.FadeOut : fadeDuration ?? DefaultStopFade
                    : TimeSpan.Zero;
                var token = voice.TryClaimFadeOut(claimedAt + duration);

                VoiceStopFade? stopFade = null;
                if (fade && token is { } claim)
                    stopFade = new VoiceStopFade(
                        voice,
                        duration,
                        clipFadeWins ? voice.Binding.FadeOutCurve : curve,
                        voice.ClipLevel,
                        voice.CaptureLayerFadeLevels(),
                        voice.RouteTargets,
                        claim);

                list.Add(new VoiceStopClaim(group, voice, stopFade, token, claimedAt + duration));
            }

            return Task.FromResult<IReadOnlyList<VoiceStopClaim>>(list);
        }).ConfigureAwait(false);

        if (claims.Count == 0)
            return; // nothing was sounding in the selection (an idle group's bus stop lands here)

        var fades = claims.Where(c => c.Fade is not null).Select(c => c.Fade!).ToArray();
        if (fades.Length > 0)
            await RunStopFadeAsync(fades).ConfigureAwait(false);

        // Release ONLY the voices this stop still owns. A voice we never claimed - or one a Panic took off us
        // mid-ramp - belongs to the stop that holds its claim; releasing it here is precisely what used to cut
        // that stop's ramp off at whatever level it had reached.
        var owned = claims.Where(c => c.OwnsRelease).ToArray();
        try
        {
            if (owned.Length > 0)
                await InvokeAsync(async () =>
                {
                    foreach (var claim in owned)
                        await ReleaseVoiceAsync(claim.Group, claim.Voice).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The session was disposed mid-stop - disposal releases every voice itself.
        }

        // …and for the rest, wait until the owner has finished, so every caller of every stop returns on the
        // same guarantee ("stopped means stopped") rather than only the one that happened to win the claim.
        foreach (var claim in claims)
            if (!claim.OwnsRelease)
                await AwaitVoiceReleaseAsync(claim.Group, claim.Voice, claim.Deadline).ConfigureAwait(false);
    }

    /// <summary>Waits for the stop that OWNS <paramref name="voice"/> to finish releasing it - the losing
    /// caller's half of "stopped means stopped". The bounded wait, its rationale and the expiry fallback all
    /// live on <see cref="SoundingStopClaim.AwaitReleaseAsync"/>, shared with the soundboard's stop domain;
    /// all this adds is what a TRANSPORT voice's fallback release actually is.</summary>
    private Task AwaitVoiceReleaseAsync(TransportGroup group, TransportVoice voice, DateTime deadline) =>
        voice.StopClaim.AwaitReleaseAsync(deadline, "cue", voice.Binding.CueId, async () =>
        {
            try
            {
                await InvokeAsync(() => ReleaseVoiceAsync(group, voice).AsTask()).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // disposal owns the teardown
            }
        });

    /// <summary>Ramps the claimed stop fades to silence OFF the dispatcher (an awaited <see cref="FadeRamp"/>):
    /// each step marshals one short gain/opacity commit onto it, so the serial loop is never parked for the
    /// fade duration (NXT-18). All fades advance from one clock, so Panic fades every claimed voice
    /// concurrently - each computing its own level from its own duration - and a crossfade tail claimed
    /// alongside its group's active clip comes down on the SAME stop clock. Exits early when every claimed
    /// voice has retired (nothing left to fade).</summary>
    private async Task RunStopFadeAsync(IReadOnlyList<VoiceStopFade> fades)
    {
        var maxDuration = TimeSpan.Zero;
        foreach (var fade in fades)
            if (fade.Duration > maxDuration)
                maxDuration = fade.Duration;
        if (maxDuration <= TimeSpan.Zero)
            return;

        try
        {
            await FadeRamp.RunAsync(FadeStepInterval, CancellationToken.None, elapsed => InvokeAsync(() =>
            {
                var applied = false;
                foreach (var fade in fades)
                {
                    if (fade.Voice.State == VoiceState.Retired)
                        continue; // released during the fade - nothing left to ramp
                    if (fade.Token.IsCancellationRequested)
                        continue; // superseded by an earlier-deadline stop, which now owns these levels
                    fade.Voice.ApplyFadeLevel(
                        fade.RouteTargets, fade.StartAudioScale, fade.StartLayerOpacities,
                        FadeRamp.LevelDown(elapsed, fade.Duration, fade.Curve));
                    applied = true;
                }

                return Task.FromResult(!applied || elapsed >= maxDuration);
            })).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // session disposed mid-fade - disposal owns the teardown
        }
    }

    /// <summary>One voice claimed by <see cref="FadeClipAsync"/>: the group, the voice that was active at
    /// claim time (the only one this fade may touch), its levels THEN (the ramp's start - consecutive fades
    /// compose), the authored full-level opacities (a fade UP knows where "level 1" is), and the claim's
    /// token (cancelled when a newer fade cue takes the same voice over).</summary>
    private sealed record ClipFade(
        TransportGroup Group,
        TransportVoice Voice,
        IReadOnlyList<AudioRouteTarget> RouteTargets,
        float StartLevel,
        IReadOnlyList<float> StartLayerOpacities,
        IReadOnlyList<float> BaseLayerFadeLevels,
        CancellationToken Token);

    /// <summary>Interpolates one fade-cue step between two arbitrary levels - the shared
    /// <see cref="FadeCurves.LevelBetween"/> (also the envelope sampler's segment interpolation), so
    /// target 0 matches the stop fade exactly and a fade up eases the same way a fade-in does.</summary>
    private static float LevelBetween(
        float start, float target, TimeSpan elapsed, TimeSpan duration, FadeCurve curve) =>
        FadeCurves.LevelBetween(start, target, elapsed, duration, curve);

    /// <summary>The persistent fade level (1 = full) of the active clip playing <paramref name="cueId"/>,
    /// or null when that cue is not an active clip. This is the level consecutive
}
