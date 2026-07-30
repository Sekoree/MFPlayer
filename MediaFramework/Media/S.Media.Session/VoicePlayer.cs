using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// The session's independent-players surface: soundboard voices (polyphonic keyed one-shots, each a fresh
/// player on an output) and the single cue preview (a loaded cue auditioned on a separate device) - playback
/// that is deliberately OUTSIDE the transport groups. Owns the voice/preview registries and their end
/// monitors; state is dispatcher-confined exactly like the session's (every mutation marshals through
/// <see cref="ShowSession.InvokeAsync{T}"/>), and media opens run OFF the dispatcher with a published claim
/// CTS so a stop / re-fire / dispose preempts them (NXT-19). Split out of <see cref="ShowSession"/> along its
/// ownership seam (review Part-5 #2); the session's public voice/preview API delegates here.
/// </summary>
internal sealed class VoicePlayer
{
    private readonly ShowSession _session;
    private readonly ClipStandbyEngine _standby;
    private readonly IAudioBackend? _audioBackend;
    // Device-dependence fix #3: the fallback device is resolved fresh at each use (through the session's
    // 5 s device cache), never a construction-time snapshot - hot-plugged hardware becomes the fallback.
    private readonly Func<string?> _resolveFallbackDeviceId;
    // Spec builders stay on the session (they read _clipsByCue / the registry / the device-rate cache); both
    // run inside a dispatcher work item, so they may read dispatcher-confined session state.
    private readonly Func<string, ClipSpec?> _buildPreviewSpec;
    private readonly Func<string, string, string?, ClipSpec> _buildVoiceSpec;

    // Preview playback (a loaded cue auditioned on a separate device, independent of the transport groups).
    private IArmedClip? _previewClip;
    private IReadOnlyList<IAudioOutput> _previewOutputs = [];
    private CancellationTokenSource? _previewCts;
    private PreviewMonitor? _previewMonitor;
    // The preview's entry on the session's level/stop bus. MONITORING, by the owner's 2026-07-29 decision:
    // the audition path is how the operator hears what they are about to fire, so the master fader must not
    // duck it and stop-all/Panic must not kill it. Empty when no preview is up.
    private Guid _previewSoundingId;
    // Soundboard voices (task #10): polyphonic one-shots, each a fresh MediaPlayer on an output, keyed by a
    // host id (the GUI's soundboard tile). Owned by the dispatcher.
    private readonly Dictionary<string, VoiceHandle> _voices = new(StringComparer.Ordinal);
    // Voice opens in flight (NXT-19): voiceId → the open's claim CTS, published so a stop / re-fire / dispose
    // preempts the OFF-dispatcher open before it commits. Owned by the dispatcher; a canceller only Cancel()s -
    // the open flow that created the CTS is the one that disposes it (the blocked open still holds its token).
    private readonly Dictionary<string, CancellationTokenSource> _pendingVoiceOpens = new(StringComparer.Ordinal);

    /// <summary>One running soundboard voice. <see cref="Level"/> is its slice of the session's ONE level
    /// composition - the tile volume (Source), any fade ramp (Fade) and the session master trim - so the
    /// three can no longer overwrite each other on the route.</summary>
    private sealed class VoiceHandle(
        IArmedClip clip,
        IReadOnlyList<IAudioOutput> outputs,
        string outputId,
        CancellationTokenSource cts,
        SoundingLevel level)
    {
        public IArmedClip Clip { get; } = clip;
        public IReadOnlyList<IAudioOutput> Outputs { get; } = outputs;
        public string OutputId { get; } = outputId;
        public CancellationTokenSource Cts { get; } = cts;
        public SoundingLevel Level { get; } = level;

        /// <summary>This voice's entry on the session's level/stop bus - dropped when the voice releases.</summary>
        public Guid SoundingId { get; set; }

        /// <summary>The shared stop protocol - claim, release signal, and the bounded wait a losing stop
        /// performs. Identical object to <c>TransportVoice.StopClaim</c>: the rule that Panic supersedes a
        /// running show fade while a longer stop becomes a waiter has to be the same in both domains, which
        /// is exactly why it stopped being written twice (2026-07-30 review §3).</summary>
        public SoundingStopClaim StopClaim { get; } = new();

        /// <summary>Completes once this voice has actually been released.</summary>
        public Task Released => StopClaim.Released;

        /// <summary>Signals <see cref="Released"/>. Called at the END of the release, so an awaiting stop
        /// resumes on a voice that is off the bus and off its device.</summary>
        public void MarkReleased() => StopClaim.MarkReleased();

        /// <summary>True once a bus stop claimed this voice. A soundboard fade-out ramp checks it every step
        /// (and in its completion) so the bus stop preempts it and owns the release from the claim on.</summary>
        public bool IsStopClaimed => StopClaim.IsClaimed;

        /// <summary>Claims this voice for a bus stop - see <see cref="SoundingStopClaim.TryClaim"/>.</summary>
        public CancellationToken? TryClaimStop(DateTime deadline) => StopClaim.TryClaim(deadline);

        /// <summary>Ends any in-flight stop ramp - the voice is going away, so no step may write it again.</summary>
        public void CancelStop() => StopClaim.Cancel();
    }

    private sealed record PreviewMonitor(
        string CueId, S.Media.Players.MediaPlayer Player, CancellationToken CancellationToken);

    // Lock-free query view (NXT-16 residue): the current voices (id + player), republished on the dispatcher
    // whenever a voice commits or releases, so the soundboard's 200 ms progress poll and the is-playing query
    // never round-trip the dispatcher - a parked loop must not freeze the tiles.
    private volatile VoiceView[] _voiceViews = [];

    private sealed record VoiceView(string Id, S.Media.Players.MediaPlayer Player);

    /// <summary>Republishes the lock-free voice view. Call on the dispatcher after <see cref="_voices"/> changes.</summary>
    private void PublishVoiceViews() =>
        _voiceViews = _voices.Select(kv => new VoiceView(kv.Key, kv.Value.Clip.Player)).ToArray();

    /// <summary>Raised (with the voice id) when a voice ends on its own. Raised from the session dispatcher;
    /// <see cref="ShowSession"/> forwards it to its public event.</summary>
    public event Action<string>? VoiceEnded;

    /// <summary>Raised (with the cue id) when a preview ends on its own. Raised from the session dispatcher;
    /// <see cref="ShowSession"/> forwards it to its public event.</summary>
    public event Action<string>? PreviewEnded;

    public VoicePlayer(
        ShowSession session,
        ClipStandbyEngine standby,
        IAudioBackend? audioBackend,
        Func<string?> resolveFallbackDeviceId,
        Func<string, ClipSpec?> buildPreviewSpec,
        Func<string, string, string?, ClipSpec> buildVoiceSpec)
    {
        _session = session;
        _standby = standby;
        _audioBackend = audioBackend;
        _resolveFallbackDeviceId = resolveFallbackDeviceId;
        _buildPreviewSpec = buildPreviewSpec;
        _buildVoiceSpec = buildVoiceSpec;
    }

    // --- preview ------------------------------------------------------------------------------------

    /// <summary>See <see cref="ShowSession.PreviewCueAsync"/> (the public doc lives there).</summary>
    public async Task<bool> PreviewCueAsync(string cueId, string? previewDeviceId)
    {
        // --- SETUP (dispatcher): stop any current preview / pending preview open, resolve the binding, claim.
        var setup = await _session.InvokeAsync<(ClipSpec Spec, CancellationTokenSource Cts)?>(async () =>
        {
            await ReleasePreviewAsync().ConfigureAwait(false);
            if (_buildPreviewSpec(cueId) is not { } spec)
                return null;
            var claim = new CancellationTokenSource();
            _previewCts = claim; // published: ReleasePreviewAsync cancels it to preempt the open
            return (spec, claim);
        }).ConfigureAwait(false);
        if (setup is not { } s)
            return false;

        // --- OPEN (OFF the dispatcher): the long part - the loop stays free throughout (NXT-19).
        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(s.Spec, s.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // preempted by StopPreview / a replacing preview / dispose - not an error
        }

        // --- COMMIT (dispatcher): only if our claim is still the current preview.
        try
        {
            return await CommitPreviewAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the open completing and the commit - release the orphaned clip directly.
            await armed.ReleaseAsync().ConfigureAwait(false);
            return false;
        }

        Task<bool> CommitPreviewAsync() => _session.InvokeAsync(async () =>
        {
            if (!ReferenceEquals(_previewCts, s.Cts) || s.Cts.IsCancellationRequested || _session.IsDisposed)
            {
                await armed.ReleaseAsync().ConfigureAwait(false);
                return false;
            }

            var player = armed.Player;
            var outputs = new List<IAudioOutput>();
            try
            {
                if (_audioBackend is not null && player.AudioRouter is not null)
                {
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                    var output = _audioBackend.CreateOutput(previewDeviceId ?? _resolveFallbackDeviceId(), new AudioFormat(rate, 2));
                    player.AttachAudioOutput(output, "_preview");
                    outputs.Add(output);
                }

                armed.Start();
                _previewClip = armed;
                _previewOutputs = outputs;
                _previewMonitor = new PreviewMonitor(cueId, player, s.Cts.Token);
                _previewSoundingId = _session.SoundingSources.RegisterMonitoring(
                    $"preview:{cueId}", () => _previewClip is not null, () => 1f);
                _session.NotifyCompletionWorkAvailable();
                return true;
            }
            catch
            {
                // ONE symmetric teardown: adopt whatever this commit had already wired and run the normal
                // release, so a fault anywhere in here (a device that vanished between resolve and attach)
                // cannot leave a bus registration, a monitor entry or a claim behind pointing at a released
                // player. Assigning the fields first is what makes the single teardown cover them.
                _previewClip = armed;
                _previewOutputs = outputs;
                await ReleasePreviewAsync().ConfigureAwait(false);
                throw;
            }
        });
    }

    /// <summary>Stops the current preview, if any - including one still opening (NXT-19).</summary>
    public Task StopPreviewAsync() => _session.InvokeAsync(() => ReleasePreviewAsync().AsTask());

    /// <summary>Releases the preview clip/outputs and preempts a pending preview open. Call on the dispatcher.</summary>
    public async ValueTask ReleasePreviewAsync()
    {
        // Cancel only - never Dispose the CTS here: a preempted preview open (NXT-19) may still hold its token
        // off-dispatcher. A cancelled CTS with no timer holds no unmanaged state, so GC reclaims it.
        _previewCts?.Cancel();
        _previewCts = null;
        _previewMonitor = null;
        _session.SoundingSources.Unregister(_previewSoundingId);
        _previewSoundingId = Guid.Empty;
        var clip = _previewClip;
        var outputs = _previewOutputs;
        _previewClip = null;
        _previewOutputs = [];
        if (clip is not null)
            await clip.ReleaseAsync().ConfigureAwait(false);
        foreach (var output in outputs)
            (output as IDisposable)?.Dispose();
    }

    // --- soundboard voices ----------------------------------------------------------------------------

    /// <summary>See <see cref="ShowSession.FireVoiceAsync"/> (the public doc lives there).</summary>
    public async Task FireVoiceAsync(string voiceId, string mediaPath, string? deviceId, float volume)
    {
        var outputId = $"voice:{voiceId}";

        // --- SETUP (dispatcher): replace any prior voice / pending open and claim this open.
        var (spec, cts) = await _session.InvokeAsync(async () =>
        {
            await ReleaseVoiceAsync(voiceId).ConfigureAwait(false); // re-trigger replaces the prior voice
            var clipSpec = _buildVoiceSpec(outputId, mediaPath, deviceId);
            var claim = new CancellationTokenSource();
            _pendingVoiceOpens[voiceId] = claim;
            return (clipSpec, claim);
        }).ConfigureAwait(false);

        // --- OPEN (OFF the dispatcher): the long part - the loop stays free throughout (NXT-19).
        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(spec, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException;
            try
            {
                await _session.InvokeAsync(() =>
                {
                    if (_pendingVoiceOpens.TryGetValue(voiceId, out var current) && ReferenceEquals(current, cts))
                        _pendingVoiceOpens.Remove(voiceId);
                    cts.Dispose(); // the open flow owns the claim CTS; the open is over, no one else holds the token
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // disposed mid-open - ReleaseAllAsync already dropped/cancelled the pending claim
            }

            if (cancelled)
                return; // preempted by stop/re-fire/dispose - not an error, the voice just never started
            throw; // a real open failure (bad path/device) surfaces to the caller as before
        }

        // --- COMMIT (dispatcher): only if our claim is still current (not stopped/re-fired during the open).
        try
        {
            await CommitVoiceAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the open completing and the commit - release the orphaned clip directly (the
            // standby engine is internally thread-safe; nothing registered it, so nothing else will).
            await armed.ReleaseAsync().ConfigureAwait(false);
        }

        Task CommitVoiceAsync() => _session.InvokeAsync(async () =>
        {
            var current = _pendingVoiceOpens.TryGetValue(voiceId, out var pending) && ReferenceEquals(pending, cts);
            if (current)
                _pendingVoiceOpens.Remove(voiceId);
            if (!current || cts.IsCancellationRequested || _session.IsDisposed)
            {
                cts.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                return;
            }

            var player = armed.Player;
            var outputs = new List<IAudioOutput>();
            // A soundboard voice is PROGRAM audio: it inherits the live master trim at fire time exactly as
            // a transport clip does, and it attaches at the composed level so no untrimmed buffer can reach
            // the device before the first level write.
            var level = new SoundingLevel { Source = volume, Master = _session.MasterTrim };
            // Declared OUTSIDE the try so the failure path can tell "nothing was published yet" (release what
            // this commit wired, by hand) from "the voice is already in _voices and on the bus" (one symmetric
            // teardown through ReleaseVoiceAsync, which un-publishes both).
            VoiceHandle? handle = null;
            try
            {
                if (_audioBackend is not null && player.AudioRouter is not null)
                {
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                    var output = _audioBackend.CreateOutput(deviceId ?? _resolveFallbackDeviceId(), new AudioFormat(rate, 2));
                    player.AttachAudioOutput(output, outputId, gain: level.Effective);
                    outputs.Add(output);
                }

                armed.Start();
                // The claim CTS becomes the running voice's CTS (cancels the end monitor on release).
                var committed = new VoiceHandle(armed, outputs, outputId, cts, level);
                handle = committed;
                _voices[voiceId] = committed;
                committed.SoundingId = _session.SoundingSources.RegisterProgram(
                    $"voice:{voiceId}",
                    // What a stop failure names to the operator: the soundboard tile, not the bus label (the
                    // host resolves ids to names and shows the raw string when it cannot).
                    subjectId: voiceId,
                    isSounding: () => IsCurrent(voiceId, committed),
                    level: () => level.Effective,
                    applyMasterTrim: master =>
                    {
                        // Identity-guarded like every other level write: a registration that outlived its
                        // voice (it cannot - release unregisters - must never touch a released player).
                        if (!IsCurrent(voiceId, committed))
                            return;
                        level.Master = master;
                        ApplyVoiceLevel(committed);
                    },
                    stop: request => StopVoiceForBusAsync(voiceId, committed, request));
                PublishVoiceViews();
                _session.NotifyCompletionWorkAvailable();
            }
            catch
            {
                // Symmetric teardown. Past the publish point (in _voices and on the bus) exactly ONE path
                // owns the undo - the normal release, which unregisters, re-publishes the view and disposes
                // the claim; before it, this commit still owns the outputs and the clip by hand.
                if (handle is not null)
                {
                    await ReleaseVoiceAsync(voiceId, handle).ConfigureAwait(false);
                }
                else
                {
                    foreach (var output in outputs)
                        (output as IDisposable)?.Dispose();
                    await armed.ReleaseAsync().ConfigureAwait(false);
                    cts.Dispose();
                }

                throw;
            }
        });
    }

    /// <summary>Stops one soundboard voice (no <see cref="VoiceEnded"/>).</summary>
    public Task StopVoiceAsync(string voiceId) => _session.InvokeAsync(() => ReleaseVoiceAsync(voiceId).AsTask());

    /// <summary>Stops every soundboard voice - including any still opening (NXT-19).</summary>
    public Task StopAllVoicesAsync() => _session.InvokeAsync(() => ReleaseAllVoicesAsync().AsTask());

    /// <summary>Preempts every soundboard voice whose media is still OPENING (NXT-19) - stop-all/Panic's
    /// "and nothing new starts" half, the voice analogue of the in-flight cue-fire cancellation. A pending
    /// open is not on the level/stop bus yet (nothing sounds), so the bus enumeration alone would let a
    /// stinger that was loading when Panic was hit start playing straight afterwards. Cancel only - the open
    /// flow that created the claim owns disposing it. Dispatcher-confined.</summary>
    public void CancelPendingVoiceOpens()
    {
        foreach (var claim in _pendingVoiceOpens.Values)
            claim.Cancel();
    }

    /// <summary>Live-sets a voice's authored (tile) gain. No-op when the voice isn't playing. The write goes
    /// through the voice's level composition, so a volume nudge multiplies the master trim and any running
    /// fade instead of replacing them (it used to write the raw volume straight onto the route, silently
    /// un-trimming the voice for the rest of its life).</summary>
    public Task SetVoiceVolumeAsync(string voiceId, float volume) =>
        _session.InvokeAsync(() =>
        {
            if (_voices.TryGetValue(voiceId, out var v))
            {
                v.Level.Source = volume;
                ApplyVoiceLevel(v);
            }

            return Task.CompletedTask;
        });

    /// <summary>Writes one voice's composed level (<c>master × volume × fade</c>) to its route - the ONE
    /// place a voice's routed gain is set, the soundboard analogue of
    /// <c>TransportGroup.ApplyAudioScale</c>. Dispatcher-confined.</summary>
    private static void ApplyVoiceLevel(VoiceHandle voice)
    {
        var player = voice.Clip.Player;
        if (player.AudioRouter is { } router && player.AudioSourceId is { } sourceId)
            router.SetRouteGain(sourceId, voice.OutputId, voice.Level.Effective);
    }

    /// <summary>Whether <paramref name="voice"/> is still the voice registered under
    /// <paramref name="voiceId"/> - the identity guard every deferred level/stop write uses so a re-fired
    /// tile's new voice is never touched by the previous one's ramp. Dispatcher-confined.</summary>
    private bool IsCurrent(string voiceId, VoiceHandle voice) =>
        _voices.TryGetValue(voiceId, out var current) && ReferenceEquals(current, voice);

    /// <summary>Fades a voice's gain to silence over <paramref name="duration"/>, then stops it. No
    /// <see cref="VoiceEnded"/>. A zero/negative duration stops immediately.</summary>
    public Task FadeVoiceAsync(string voiceId, TimeSpan duration) =>
        _session.InvokeAsync(() =>
        {
            // A bus stop owns a claimed voice's levels AND its release, so a tile fade must not start a second
            // ramp fighting the show stop's (nor release the voice out from under it).
            if (!_voices.TryGetValue(voiceId, out var v) || v.IsStopClaimed)
                return Task.CompletedTask;
            if (duration <= TimeSpan.Zero)
                return ReleaseVoiceAsync(voiceId).AsTask();
            StartVoiceFadeOut(voiceId, v, duration, v.Cts.Token);
            return Task.CompletedTask;
        });

    /// <summary>Whether a soundboard voice is currently playing - a lock-free view read (NXT-16 residue),
    /// eventually consistent with the dispatcher state like every session snapshot query.</summary>
    public Task<bool> IsVoicePlayingAsync(string voiceId)
    {
        foreach (var v in _voiceViews)
            if (string.Equals(v.Id, voiceId, StringComparison.Ordinal))
                return Task.FromResult(true);
        return Task.FromResult(false);
    }

    /// <summary>Per-voice playhead (id, position, duration) for every currently-playing soundboard voice - a
    /// lock-free view read (NXT-16 residue): the 200 ms soundboard poll must never queue behind the dispatcher.
    /// Player position/duration reads are thread-safe (the transport snapshot reads them the same way).</summary>
    public IReadOnlyList<VoiceProgress> GetVoiceProgress()
    {
        var views = _voiceViews; // single volatile read of the published view
        var snaps = new VoiceProgress[views.Length];
        for (var i = 0; i < views.Length; i++)
        {
            TimeSpan pos = TimeSpan.Zero, dur = TimeSpan.Zero;
            try { pos = views[i].Player.Position; dur = views[i].Player.Duration; }
            catch { /* concurrent teardown - zeros for this tick */ }
            snaps[i] = new VoiceProgress(views[i].Id, pos, dur);
        }
        return snaps;
    }

    public Task<IReadOnlyList<VoiceProgress>> GetVoiceProgressAsync() =>
        Task.FromResult(GetVoiceProgress());

    /// <summary>Releases the preview and every voice (running or still opening) - the session's disposal
    /// teardown. Call on the dispatcher (disposal runs there directly, not through InvokeAsync).</summary>
    public async ValueTask ReleaseAllAsync()
    {
        await ReleasePreviewAsync().ConfigureAwait(false);
        await ReleaseAllVoicesAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseAllVoicesAsync()
    {
        foreach (var id in _voices.Keys.Concat(_pendingVoiceOpens.Keys).Distinct().ToArray())
            await ReleaseVoiceAsync(id).ConfigureAwait(false);
    }

    /// <summary>Releases the voice under <paramref name="voiceId"/>, or - with <paramref name="expected"/> -
    /// only when that exact voice is still the one registered: a fade/stop ramp that reaches its release
    /// after the tile was re-fired must not tear down the NEW voice.</summary>
    private async ValueTask ReleaseVoiceAsync(string voiceId, VoiceHandle? expected = null)
    {
        if (expected is not null && !IsCurrent(voiceId, expected))
            return;

        // Preempt a still-opening voice (NXT-19): cancel its claim so the off-dispatcher open aborts and its
        // commit is refused. Only Cancel here - the open flow that created the CTS disposes it (it still holds
        // the token inside the blocked open).
        if (_pendingVoiceOpens.Remove(voiceId, out var pending))
            pending.Cancel();

        if (!_voices.Remove(voiceId, out var v))
            return;
        // Off the level/stop bus BEFORE the player goes away: a released voice must never be enumerated by a
        // trim or a stop again.
        _session.SoundingSources.Unregister(v.SoundingId);
        PublishVoiceViews();
        v.CancelStop(); // any in-flight stop ramp ends here - nothing may write a released voice's levels
        v.Cts.Cancel();
        v.Cts.Dispose();
        try
        {
            await v.Clip.ReleaseAsync().ConfigureAwait(false);
            foreach (var output in v.Outputs)
                (output as IDisposable)?.Dispose();
        }
        finally
        {
            // LAST, and unconditionally: every stop waiting on this voice resumes only now, with the voice off
            // the bus and off its device - so a stop-all/Panic caller that returns has genuinely stopped it.
            // In a finally because a throwing teardown step must not leave a Panic caller waiting forever;
            // the voice is out of _voices and off the bus by here either way, so it cannot sound again.
            v.MarkReleased();
        }
    }

    /// <summary>Reconciles preview and voice natural ends in the session's single completion-monitor tick.
    /// Call on the session dispatcher.</summary>
    public async ValueTask<bool> PollCompletionsAsync()
    {
        if (_previewMonitor is { } preview)
        {
            if (preview.CancellationToken.IsCancellationRequested
                || !ReferenceEquals(_previewClip?.Player, preview.Player))
            {
                _previewMonitor = null;
            }
            else if (!preview.Player.IsRunning && preview.Player.Position > TimeSpan.Zero)
            {
                var cueId = preview.CueId;
                await ReleasePreviewAsync().ConfigureAwait(false);
                PreviewEnded?.Invoke(cueId);
            }
        }

        foreach (var (voiceId, handle) in _voices.ToArray())
        {
            if (handle.Cts.IsCancellationRequested)
                continue;
            var player = handle.Clip.Player;
            if (player.IsRunning || player.Position <= TimeSpan.Zero)
                continue;

            await ReleaseVoiceAsync(voiceId).ConfigureAwait(false);
            VoiceEnded?.Invoke(voiceId);
        }

        return _previewMonitor is not null || _voices.Count > 0;
    }

    /// <summary>Ramps a voice's FADE factor to 0 over <paramref name="duration"/> then releases it - a
    /// <see cref="FadeRamp"/> at the same step rate as every other fade. The ramp rides the voice's level
    /// composition, so it scales the tile's own volume and the master trim rather than replacing them (the
    /// raw route write it replaces jumped a half-volume tile up to full level on its first step).</summary>
    private void StartVoiceFadeOut(string voiceId, VoiceHandle voice, TimeSpan duration, CancellationToken ct)
    {
        var start = voice.Level.Fade;
        FadeRamp.Start(
            FadeRamp.DefaultStepInterval, ct,
            step: elapsed => _session.InvokeAsync<bool>(() =>
            {
                // A stop-all/Panic claim preempts this ramp: the bus stop owns the voice's levels and its
                // release from the claim on (the Active slot's TryBeginFadeOut rule, for voices).
                if (ct.IsCancellationRequested || !IsCurrent(voiceId, voice) || voice.IsStopClaimed)
                    return Task.FromResult(true);
                voice.Level.Fade = start * FadeRamp.LevelDown(elapsed, duration);
                ApplyVoiceLevel(voice);
                return Task.FromResult(voice.Level.Fade <= 0f);
            }),
            onCompleted: () => _session.InvokeAsync(() =>
                // The claim check has to be REPEATED here, exactly as the transport release ramp repeats it
                // (ShowSession.StartVoiceReleaseRamp): the step above ends the moment a bus stop claims the
                // voice, and releasing it anyway would tear it down at whatever level this fade had reached
                // (an audible click) AND kill the stop's own ramp, which had just started from there.
                voice.IsStopClaimed
                    ? Task.CompletedTask
                    : ReleaseVoiceAsync(voiceId, voice).AsTask()));
    }

    /// <summary>The level/stop bus's stop hook for one voice (stop-all and Panic alike): claims the voice,
    /// rides <paramref name="request"/>'s clock down from the level the voice has NOW - so a stop mid
    /// fade-out never pops it back up - and releases it. The returned task completes only once the voice is
    /// released ("stopped means stopped", matching the transport stop path); a hard-cut request releases
    /// without a ramp. The ramp itself runs OFF the dispatcher, one short marshaled write per step.
    /// <para>A SECOND stop supersedes an in-flight one whenever it lands earlier - the operator hits Stop
    /// with the show's 5 s fade, decides it is still wrong and hits Panic (0 ms): Panic must cut this voice
    /// instantly, exactly as it cuts a transport cue. The claim is therefore NOT a permanent one-shot (which
    /// made the second press a silent no-op and let a stinger sound on for the rest of the first fade, while
    /// <see cref="ShowSession.StopAllAsync"/> reported the show stopped). A stop that lands LATER than the
    /// in-flight one does not shorten it: it simply awaits that stop's release, so both callers still return
    /// only once the voice is gone.</para></summary>
    private async Task StopVoiceForBusAsync(string voiceId, VoiceHandle voice, SoundingStopRequest request)
    {
        var deadline = DateTime.UtcNow + (request.Fade ? request.FadeDuration : TimeSpan.Zero);
        var claim = await _session.InvokeAsync(() => Task.FromResult(
            IsCurrent(voiceId, voice) && voice.TryClaimStop(deadline) is { } token
                ? ((CancellationToken Token, float Start)?)(token, voice.Level.Fade)
                : null)).ConfigureAwait(false);
        if (claim is not { } stop)
        {
            // Released/replaced already, or an in-flight stop lands no later than ours. Either way this call
            // is only done when the voice IS gone, so wait for that release instead of returning early.
            await AwaitReleaseAsync(voiceId, voice, deadline).ConfigureAwait(false);
            return;
        }

        if (request.Fade)
        {
            try
            {
                await FadeRamp.RunAsync(
                    FadeRamp.DefaultStepInterval, stop.Token,
                    elapsed => _session.InvokeAsync(() =>
                    {
                        if (!IsCurrent(voiceId, voice))
                            return Task.FromResult(true); // ended on its own mid-ramp
                        voice.Level.Fade = stop.Start * FadeRamp.LevelDown(elapsed, request.FadeDuration, request.Curve);
                        ApplyVoiceLevel(voice);
                        return Task.FromResult(elapsed >= request.FadeDuration);
                    })).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded mid-delay by a stop landing earlier (Panic over a show fade) - it owns the
                // levels and the release from its claim on.
            }
        }

        if (stop.Token.IsCancellationRequested)
        {
            // Superseded: the stop that took over owns the release, so wait for it rather than cutting its
            // (shorter) ramp short.
            await AwaitReleaseAsync(voiceId, voice, deadline).ConfigureAwait(false);
            return;
        }

        await _session.InvokeAsync(() => ReleaseVoiceAsync(voiceId, voice).AsTask()).ConfigureAwait(false);
    }

    /// <summary>Waits for the stop that OWNS <paramref name="voice"/>'s release to finish it - the losing
    /// caller's half of "stopped means stopped". The bounded wait and its rationale live on
    /// <see cref="SoundingStopClaim.AwaitReleaseAsync"/>, shared with the transport stop domain; all this
    /// adds is what a SOUNDBOARD voice's fallback release actually is.</summary>
    private Task AwaitReleaseAsync(string voiceId, VoiceHandle voice, DateTime deadline) =>
        voice.StopClaim.AwaitReleaseAsync(deadline, "voice", voiceId, () =>
            _session.InvokeAsync(() => ReleaseVoiceAsync(voiceId, voice).AsTask()));
}
