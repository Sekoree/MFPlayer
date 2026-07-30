using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Routing;

namespace S.Media.Session;

/// <summary>
/// How a clip ENDS on its own, and the ramps that carry it out: the per-group end monitor, the session's
/// single completion tick (shared with soundboard voices and the preview), the natural fade-out, the
/// crossfade release ramp, and the loop-with-crossfade wrap.
/// <para>Split out of the root file (2026-07-30 review §3). The operator-driven stops live in
/// <c>ShowSession.Stops.cs</c>; the difference is who decides - here nothing was asked for, the media simply
/// ran out - and the two were previously 700 lines apart in one file with fade cues in between.</para>
/// </summary>
public sealed partial class ShowSession
{
    internal static readonly TimeSpan EndMonitorPollInterval = TimeSpan.FromMilliseconds(100); // also the voice/preview monitors' rate
    private static readonly TimeSpan EndMonitorGuard = TimeSpan.FromMilliseconds(120);

    /// <summary>Consecutive end-monitor ticks a NotifyNaturalEnd clip's audio clock must sit stopped (not
    /// host-paused) before the stall is treated as source EOF - 500 ms at the 100 ms poll, comfortably past a
    /// coordinated seek's transient clock pause (~100–300 ms) while still snappy for cue auto-follow.</summary>
    private const int EndMonitorStallTicks = 5;

    /// <summary>Registers one active clip with the session's consolidated completion loop. Position checks run
    /// on the dispatcher, so pause/seek/replace state stays serialized with transport commands.</summary>
    private void StartEndMonitor(
        string groupId,
        TransportVoice voice,
        TimeSpan end,
        CancellationToken ct)
    {
        if (_groups.GetValueOrDefault(groupId) is not { } group || !ReferenceEquals(group.ActiveVoice, voice))
            return;
        group.EndMonitor = new ClipEndMonitorState(voice, end, ct);
        NotifyCompletionWorkAvailable();
    }

    internal void NotifyCompletionWorkAvailable() => _completionMonitor.NotifyWorkAvailable();

    private async Task<bool> PollCompletionWorkFromBackgroundAsync()
    {
        if (_disposed)
            return false;
        try
        {
            return await _dispatcher.InvokeAsync(PollCompletionWorkAsync).ConfigureAwait(false);
        }
        catch (SessionDispatcherOverloadedException)
        {
            return true; // bounded overload is transient; retry on the next shared tick
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>One dispatcher command checks every transport clip, preview, and soundboard voice.</summary>
    private async Task<bool> PollCompletionWorkAsync()
    {
        if (_disposed)
            return false;

        foreach (var (groupId, group) in _groups)
        {
            if (group.EndMonitor is not { } monitor)
                continue;
            if (await PollClipEndAsync(groupId, group, monitor).ConfigureAwait(false))
                group.ClearEndMonitor(monitor);
        }

        var voicesRemain = await _voicePlayer.PollCompletionsAsync().ConfigureAwait(false);
        return voicesRemain || _groups.Values.Any(g => g.EndMonitor is not null);
    }

    /// <summary>Applies one tick of loop/trim-out/freeze/natural-end behavior. Returns true when this monitor is done.</summary>
    private async Task<bool> PollClipEndAsync(
        string groupId,
        TransportGroup group,
        ClipEndMonitorState monitor)
    {
        var voice = monitor.Voice;
        var binding = voice.Binding;
        var player = voice.Player;
        if (monitor.CancellationToken.IsCancellationRequested || !ReferenceEquals(group.ActiveVoice, voice))
            return true;

        var loops = binding.Loop || binding.EndBehavior == ClipEndBehavior.Loop;
        var freezes = binding.EndBehavior == ClipEndBehavior.FreezeLastFrame;
        var position = player.Position;
        var remaining = monitor.End - position;

        // A timeline discontinuity (seek/pause/resume) restarts the EOF-stall persistence window.
        var generation = group.Timeline.Generation;
        if (generation != monitor.LastTimelineGeneration)
        {
            monitor.LastTimelineGeneration = generation;
            monitor.StalledTicks = 0;
        }

        if (binding.NotifyNaturalEnd && !loops && !freezes
            && player.SampleRate > 0
            && !group.PausedByHost
            && !player.IsRunning
            && position > TimeSpan.Zero)
        {
            if (++monitor.StalledTicks >= EndMonitorStallTicks)
            {
                await ReleaseActiveVoiceAsync(group).ConfigureAwait(false);
                ClipNaturallyEnded?.Invoke(binding.CueId);
                return true;
            }
        }
        else
        {
            monitor.StalledTicks = 0;
        }

        // Pre-end notify (dual-voice crossfade): one-shot per committed clip, so a crossfading host fires
        // the next item exactly once even though the poll keeps ticking inside the window - and a backwards
        // seek after the notification does not re-fire it (the host has already advanced).
        if (!monitor.PreEndNotified
            && binding.PreEndNotify > TimeSpan.Zero
            && !loops && !freezes
            && remaining > TimeSpan.Zero && remaining <= binding.PreEndNotify)
        {
            monitor.PreEndNotified = true;
            ClipApproachingEnd?.Invoke(binding.CueId);
        }

        var naturalFade = binding.FadeOut > TimeSpan.Zero
            ? binding.FadeOut
            : binding.EndBehavior == ClipEndBehavior.FadeOutAndStop
                ? DefaultStopFade
                : TimeSpan.Zero;
        if (!loops && !freezes && naturalFade > TimeSpan.Zero
            && remaining > TimeSpan.Zero && remaining <= naturalFade
            // The clip's own tail-out is due to finish when the clip does, so that is its claim deadline: an
            // operator stop shorter than the remaining tail takes the voice over, a longer one waits for it.
            && voice.TryClaimFadeOut(DateTime.UtcNow + remaining) is { } naturalClaim)
        {
            StartNaturalFadeOut(
                groupId,
                voice,
                voice.RouteTargets,
                voice.ClipLevel,
                voice.CaptureLayerOpacities(),
                remaining,
                binding.FadeOutCurve,
                monitor.CancellationToken,
                naturalClaim);
            return true;
        }

        // Loop-with-crossfade (dual-voice design §3): inside the window, re-fire the SAME binding as a
        // fresh incoming voice through the crossfade replacement path - the current pass becomes the
        // outgoing tail and the next pass fades in over it. One-shot per committed clip (the incoming
        // commit replaces this monitor; a failed re-open clears the flag below so the butt-splice wrap
        // resumes). Only when the window is shorter than the trimmed pass, else an instant re-fire per
        // pass would churn voices continuously - such a clip falls back to the seamless-seek loop.
        if (loops && binding.LoopCrossfade > TimeSpan.Zero
            && !monitor.LoopCrossfadePending
            && binding.LoopCrossfade < monitor.End - binding.StartOffset
            && remaining > TimeSpan.Zero && remaining <= binding.LoopCrossfade)
        {
            monitor.LoopCrossfadePending = true;
            // Same fire-and-forget discipline as the warm-up launch: suppress ExecutionContext flow so
            // the dispatcher's AsyncLocal identity never leaks into the re-fire's continuations (NXT-22).
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(() => LoopCrossfadeReplaceAsync(groupId, binding, monitor));
        }

        if (position < monitor.End - EndMonitorGuard)
            return false;
        if (loops)
        {
            // The incoming voice owns this wrap - keep polling instead of seeking back, so the tail
            // plays out under the crossfade. If the re-open failed, the cleared flag re-enables the
            // butt-splice wrap on the next tick (degraded but still looping).
            if (monitor.LoopCrossfadePending)
                return false;
            var masterBeforeLoop = group.Timeline.GetSnapshot().MasterTime;
            SeekCoordinatedRestoringPlayState(
                player, binding.StartOffset, group, masterBeforeLoop, resume: true);
            return false;
        }
        if (freezes)
        {
            player.Pause();
            group.Timeline.MarkDiscontinuity();
            return true;
        }

        await ReleaseActiveVoiceAsync(group).ConfigureAwait(false);
        ClipNaturallyEnded?.Invoke(binding.CueId);
        return true;
    }

    private sealed class ClipEndMonitorState(
        TransportVoice voice,
        TimeSpan end,
        CancellationToken cancellationToken)
    {
        /// <summary>The voice this monitor watches - the group's Active at the time it was armed. Every tick
        /// re-checks that identity, so a monitor whose voice was replaced (or handed off to a crossfade tail)
        /// ends instead of emitting a second natural end for a clip nothing is playing.</summary>
        public TransportVoice Voice { get; } = voice;
        public TimeSpan End { get; } = end;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int StalledTicks { get; set; }
        public int LastTimelineGeneration { get; set; } = -1;

        /// <summary>Whether this clip's one-shot <see cref="ShowClipBinding.PreEndNotify"/> notification
        /// (<see cref="ClipApproachingEnd"/>) already fired.</summary>
        public bool PreEndNotified { get; set; }

        /// <summary>Whether this pass's loop-with-crossfade re-fire is in flight (or committed). While set,
        /// the monitor's butt-splice wrap is suppressed - the incoming voice owns the loop boundary. Cleared
        /// (on the dispatcher) if the re-open fails, restoring the seek-back wrap as the fallback.</summary>
        public bool LoopCrossfadePending { get; set; }
    }

    /// <summary>The loop-with-crossfade re-fire (<see cref="ShowClipBinding.LoopCrossfade"/>): opens a FRESH
    /// instance of the same binding (via the standby engine - a warm prepared clip makes the open cheap, a
    /// cold one simply re-decodes) and commits it through the crossfade replacement path, so the finishing
    /// pass moves to the group's Outgoing slot and plays its tail under the incoming pass. Runs off the
    /// dispatcher like any fire; the OLD clip's monitor token scopes it, so a stop/replace during the open
    /// discards the armed clip at commit instead of resurrecting the cue. A crossfaded wrap is still one
    /// loop pass: no cue event fires (this bypasses the cue graph on purpose - loop wraps never re-trigger
    /// auto-follow/fault policies), and <c>ClipNaturallyEnded</c> stays reserved for the real natural end.
    /// EqualPower, like every constant-power program crossfade (HaPlay's playlist advance).</summary>
    private async Task LoopCrossfadeReplaceAsync(
        string groupId, ShowClipBinding binding, ClipEndMonitorState monitor)
    {
        try
        {
            await PlayClipAsync(
                    groupId, binding, monitor.CancellationToken,
                    crossfade: (binding.LoopCrossfade, FadeCurve.EqualPower))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                MediaDiagnostics.LogWarning(
                    "ShowSession: loop-crossfade re-open failed for cue '{0}'; falling back to the butt-splice wrap ({1}).",
                    binding.CueId, ex.Message);
            // Re-arm the seek-back wrap on the dispatcher (monitor state is dispatcher-confined). If the
            // clip was stopped/replaced meanwhile the monitor is dead and the flag is inert either way.
            try
            {
                await InvokeAsync(() =>
                {
                    monitor.LoopCrossfadePending = false;
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch
            {
                // session torn down / dispatcher saturated mid-flight - nothing left to re-arm (a live
                // monitor only stays wedged until its clip is stopped or the show reloads)
            }
        }
    }

    /// <summary>Runs a natural-end audio/video fade without occupying the session dispatcher between steps
    /// (a <see cref="FadeRamp"/>), then releases the clip if it is still active.
    /// <para><paramref name="claim"/> is this fade's stop claim. An operator stop shorter than the clip's own
    /// tail-out supersedes it (<see cref="TransportVoice.TryClaimFadeOut"/>), and both halves have to honour
    /// that: the ramp stops writing levels the stop now owns, and the release is left to the stop - otherwise
    /// the natural fade would release the voice under a stop that is still ramping it, and the cue would be
    /// reported as having ENDED NATURALLY when the operator stopped it.</para></summary>
    private void StartNaturalFadeOut(
        string groupId,
        TransportVoice voice,
        IReadOnlyList<AudioRouteTarget> routeTargets,
        float startAudioScale,
        IReadOnlyList<float> startLayerOpacities,
        TimeSpan duration,
        FadeCurve curve,
        CancellationToken ct,
        CancellationToken claim)
    {
        FadeRamp.Start(
            FadeStepInterval, ct,
            step: elapsed => InvokeAsync<bool>(() =>
            {
                if (ct.IsCancellationRequested
                    || claim.IsCancellationRequested
                    || _groups.GetValueOrDefault(groupId) is not { } group
                    || !ReferenceEquals(group.ActiveVoice, voice))
                    return Task.FromResult(true);
                var scale = FadeRamp.LevelDown(elapsed, duration, curve);
                voice.ApplyFadeLevel(routeTargets, startAudioScale, startLayerOpacities, scale);
                return Task.FromResult(scale <= 0f);
            }),
            onCompleted: () => InvokeAsync(async () =>
            {
                if (!claim.IsCancellationRequested
                    && _groups.GetValueOrDefault(groupId) is { } group
                    && ReferenceEquals(group.ActiveVoice, voice))
                {
                    var endedCueId = voice.Clip.Spec.Id;
                    await ReleaseActiveVoiceAsync(group).ConfigureAwait(false);
                    ClipNaturallyEnded?.Invoke(endedCueId); // natural fade-out completed
                }
            }));
    }

    /// <summary>Runs a voice's release ramp without occupying the session dispatcher between steps (a
    /// <see cref="FadeRamp"/>, like every session fade): the voice's routes and layers ramp from the level
    /// and opacities it held at the handoff down to silence over the window, then it releases through the
    /// same teardown every other path uses. Armed by the handoff itself
    /// (<see cref="TransportGroup.StartReleaseRamp"/>), so a voice can never be left releasing with nothing
    /// to bring it down. The ramp start is the voice's CURRENT fade level, never its composed level - the
    /// live master trim keeps multiplying through <see cref="SoundingLevel.Effective"/> on every step, so
    /// capturing the composed value here would apply the trim twice. A stop claim
    /// (<see cref="TransportVoice.TryClaimFadeOut"/>) or a hard release cancels the ramp and owns the
    /// release instead.</summary>
    private void StartVoiceReleaseRamp(
        string groupId, TransportVoice voice, TimeSpan duration, FadeCurve curve)
    {
        var ct = voice.BeginReleaseRamp();
        var startLevel = voice.ClipLevel;
        var startOpacities = voice.CaptureLayerOpacities();
        FadeRamp.Start(
            FadeStepInterval, ct,
            step: elapsed => InvokeAsync<bool>(() =>
            {
                if (ct.IsCancellationRequested
                    || voice.State != VoiceState.Releasing
                    || voice.IsFadeOutClaimed)
                    return Task.FromResult(true);
                var scale = FadeRamp.LevelDown(elapsed, duration, curve);
                voice.ApplyFadeLevel(voice.RouteTargets, startLevel, startOpacities, scale);
                return Task.FromResult(scale <= 0f);
            }),
            onCompleted: () => InvokeAsync(async () =>
            {
                if (_groups.GetValueOrDefault(groupId) is { } group
                    && voice.State == VoiceState.Releasing
                    && !voice.IsFadeOutClaimed)
                    await ReleaseVoiceAsync(group, voice).ConfigureAwait(false);
            }));
    }
}
