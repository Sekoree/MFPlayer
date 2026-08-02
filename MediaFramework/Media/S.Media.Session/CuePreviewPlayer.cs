using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Compositor;

namespace S.Media.Session;

/// <summary>
/// The single cue preview: a loaded cue auditioned on a separate device (and, when the audition rig is up,
/// a separate canvas), deliberately OUTSIDE the transport groups.
/// </summary>
/// <remarks>
/// <para>
/// Split from the soundboard (2026-08-02): a preview is cue-shaped - it resolves a cue id through the
/// session's binding table - whereas a voice is a raw path, so the two only ever shared a file. What this
/// needs from the session is <see cref="ISessionPreviewHost"/>: the serial dispatcher, the level/stop bus,
/// the completion tick, and the audition canvas.
/// </para>
/// <para>
/// State is dispatcher-confined (every mutation marshals through
/// <see cref="ISessionVoiceHost.InvokeAsync{T}"/>), and the media open runs OFF the dispatcher with a
/// published claim CTS so a stop / re-preview / dispose preempts it (NXT-19).
/// </para>
/// </remarks>
internal sealed class CuePreviewPlayer
{
    private readonly ISessionPreviewHost _host;
    private readonly ClipStandbyEngine _standby;
    private readonly IAudioBackend? _audioBackend;
    // The preview's monitoring seam (HaCue plan: preview/audition IS monitoring). When present, the
    // preview auditions through a target-owned monitor line - never a device open of its own, so a
    // patched line is never double-opened. Null = the legacy direct device open below (HaPlay).
    private readonly IShowProgramAudioTarget? _programAudio;
    // Device-dependence fix #3: the fallback device is resolved fresh at each use (through the session's
    // 5 s device cache), never a construction-time snapshot - hot-plugged hardware becomes the fallback.
    private readonly Func<string?> _resolveFallbackDeviceId;
    // The spec builder stays on the session (it reads _clipsById / the registry / the device-rate cache);
    // it runs inside a dispatcher work item, so it may read dispatcher-confined session state.
    private readonly Func<string, ClipSpec?> _buildPreviewSpec;

    private IArmedClip? _previewClip;
    private IReadOnlyList<PreviewSink> _previewOutputs = [];

    /// <summary>The audition-canvas layers this preview placed, if the rig was up when it started.</summary>
    private IReadOnlyList<ClipCompositionRuntime.IPlacedClipLayer> _previewLayers = [];
    private CancellationTokenSource? _previewCts;
    private PreviewMonitor? _previewMonitor;
    // The preview's entry on the session's level/stop bus. MONITORING, by the owner's 2026-07-29 decision:
    // the audition path is how the operator hears what they are about to fire, so the master fader must not
    // duck it and stop-all/Panic must not kill it. Empty when no preview is up.
    private Guid _previewSoundingId;

    private sealed record PreviewMonitor(
        string CueId, S.Media.Players.MediaPlayer Player, CancellationToken CancellationToken);

    /// <summary>One preview sink and how to let it go: a non-null <see cref="Release"/> is a BORROWED
    /// monitoring lease (run the hook, never dispose the output); null means the preview owns the
    /// backend-created device output and disposes it.</summary>
    private readonly record struct PreviewSink(IAudioOutput Output, Action? Release);

    /// <summary>Raised (with the cue id) when a preview ends on its own. Raised from the session dispatcher;
    /// <see cref="ShowSession"/> forwards it to its public event.</summary>
    public event Action<string>? PreviewEnded;

    public CuePreviewPlayer(
        ISessionPreviewHost host,
        ClipStandbyEngine standby,
        IAudioBackend? audioBackend,
        IShowProgramAudioTarget? programAudio,
        Func<string?> resolveFallbackDeviceId,
        Func<string, ClipSpec?> buildPreviewSpec)
    {
        _host = host;
        _standby = standby;
        _audioBackend = audioBackend;
        _programAudio = programAudio;
        _resolveFallbackDeviceId = resolveFallbackDeviceId;
        _buildPreviewSpec = buildPreviewSpec;
    }

    // --- preview ------------------------------------------------------------------------------------

    /// <summary>See <see cref="ShowSession.PreviewCueAsync"/> (the public doc lives there).</summary>
    public async Task<bool> PreviewCueAsync(string cueId, string? previewDeviceId)
    {
        // --- SETUP (dispatcher): stop any current preview / pending preview open, resolve the binding, claim.
        var setup = await _host.InvokeAsync<(ClipSpec Spec, CancellationTokenSource Cts)?>(async () =>
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

        // D8: the audition rig is an output like any other, so its width is whatever the selected device
        // is - never a hardcoded stereo. Hardcoding it did not merely mis-place a multichannel preview: a
        // device whose driver only accepts its native width refuses a 2-channel open outright, so audition
        // failed on exactly the interfaces a show is most likely to be run through.
        int AuditionChannels(string? deviceId)
        {
            if (_audioBackend is null)
                return 2;
            try
            {
                var devices = _audioBackend.EnumerateOutputDevices();
                var device = deviceId is { Length: > 0 }
                    ? devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))
                    : devices.FirstOrDefault(d => d.IsDefault);
                // An unknown id is a stale saved setting, not a reason to refuse to audition: stereo is the
                // safe floor, and the open below reports the real failure if even that is wrong.
                return device is { MaxChannels: > 0 } ? device.MaxChannels : 2;
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning(
                    "VoicePlayer: could not read the audition device's channel count ({0}); using stereo.",
                    ex.Message);
                return 2;
            }
        }

        Task<bool> CommitPreviewAsync() => _host.InvokeAsync(async () =>
        {
            if (!ReferenceEquals(_previewCts, s.Cts) || s.Cts.IsCancellationRequested || _host.IsDisposed)
            {
                await armed.ReleaseAsync().ConfigureAwait(false);
                return false;
            }

            var player = armed.Player;
            var outputs = new List<PreviewSink>();
            try
            {
                if (player.AudioRouter is not null && (_programAudio is not null || _audioBackend is not null))
                {
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                    if (_programAudio is { } monitorTarget)
                    {
                        // The monitoring seam: audition through a target-owned line (previewDeviceId
                        // names the endpoint; null = the target's default monitor line). Tracked
                        // BEFORE the attach so the symmetric teardown below releases the lease even
                        // when the attach itself faults.
                        var lease = monitorTarget.AcquireMonitorOutput(
                            previewDeviceId, new AudioFormat(rate, AuditionChannels(previewDeviceId)));
                        outputs.Add(new PreviewSink(lease.Output, lease.Dispose));
                        player.AttachAudioOutput(lease.Output, "_preview");
                    }
                    else
                    {
                        var auditionDevice = previewDeviceId ?? _resolveFallbackDeviceId();
                        var output = _audioBackend!.CreateOutput(
                            auditionDevice, new AudioFormat(rate, AuditionChannels(auditionDevice)));
                        outputs.Add(new PreviewSink(output, null));
                        player.AttachAudioOutput(output, "_preview");
                    }
                }

                // Video half of the audition rig: place the previewed clip onto the hidden audition canvas
                // so the monitor shows it composited - placement, fit, effects, mapping - rather than as a
                // bare source-resolution picture. Skipped silently when the rig is off, which is the common
                // case and must cost nothing.
                var layers = new List<ClipCompositionRuntime.IPlacedClipLayer>();
                if (_host.AuditionComposition is { } audition && player.VideoSource is { } previewVideo)
                {
                    var slot = audition.AddLayer(
                        previewVideo.Format,
                        new VideoPlacementSpec(ShowSession.AuditionCompositionId, 0, Placement: "fit"),
                        // Latest-wins, NOT master-aligned: a preview claims no transport timeline, so the
                        // canvas has no master clock to align PTS against and every frame would look
                        // equidistant - the monitor would freeze on the first one.
                        SlotKeepPolicy.Latest);
                    layers.Add(slot);
                    player.AttachVideoOutput(slot.Output, id: "_audition");
                }

                armed.Start();
                _previewClip = armed;
                _previewOutputs = outputs;
                _previewLayers = layers;
                _previewMonitor = new PreviewMonitor(cueId, player, s.Cts.Token);
                _previewSoundingId = _host.SoundingSources.RegisterMonitoring(
                    $"preview:{cueId}", () => _previewClip is not null, () => 1f);
                _host.NotifyCompletionWorkAvailable();
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
    public Task StopPreviewAsync() => _host.InvokeAsync(() => ReleasePreviewAsync().AsTask());

    /// <summary>Releases the preview clip/outputs and preempts a pending preview open. Call on the dispatcher.</summary>
    public async ValueTask ReleasePreviewAsync()
    {
        // Cancel only - never Dispose the CTS here: a preempted preview open (NXT-19) may still hold its token
        // off-dispatcher. A cancelled CTS with no timer holds no unmanaged state, so GC reclaims it.
        _previewCts?.Cancel();
        _previewCts = null;
        _previewMonitor = null;
        _host.SoundingSources.Unregister(_previewSoundingId);
        _previewSoundingId = Guid.Empty;
        var clip = _previewClip;
        var outputs = _previewOutputs;
        var layers = _previewLayers;
        _previewClip = null;
        _previewOutputs = [];
        _previewLayers = [];
        // Before the clip release: the layers hold the video outputs the player is still fanning to.
        foreach (var layer in layers)
            layer.Dispose();
        if (clip is not null)
            await clip.ReleaseAsync().ConfigureAwait(false);
        foreach (var sink in outputs)
        {
            if (sink.Release is { } release)
                release(); // borrowed monitoring lease - the hook detaches it, the target owns the line
            else
                (sink.Output as IDisposable)?.Dispose();
        }
    }
    /// <summary>Ends the preview if its clip has run out. Returns whether a preview is still up, so the
    /// session's completion monitor knows whether to keep ticking.</summary>
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

        return _previewMonitor is not null;
    }

    /// <summary>Releases the preview - the session's disposal teardown. Call on the dispatcher (disposal
    /// runs there directly, not through InvokeAsync).</summary>
    public ValueTask ReleaseAllAsync() => ReleasePreviewAsync();
}
