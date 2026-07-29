using HaViz.Core;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Players;
using S.Media.Routing;

namespace HaViz.Desktop.Playback;

/// <summary>Interleaved float PCM hand-off (same shape as VizNdiEngine.SubmitPcm).</summary>
public delegate void PcmSubmit(ReadOnlySpan<float> interleaved, int sampleRate, int channels);

/// <summary>
/// Router output that forwards the mixed PCM to the visualizer/NDI engine. The only mandatory
/// output on the player's router - the show-critical feed, whether or not a local monitor device
/// is attached. Submit runs on the audio thread - the sink (VizNdiEngine.SubmitPcm) is
/// thread-safe by contract.
/// </summary>
internal sealed class VizTapAudioOutput(AudioFormat format, PcmSubmit sink) : IAudioOutput
{
    public AudioFormat Format => format;

    public void Submit(ReadOnlySpan<float> packedSamples) =>
        sink(packedSamples, format.SampleRate, format.Channels);
}

/// <summary>
/// The desktop counterpart of the Android head's IMiniPlayer: one framework MediaPlayer per track
/// (FFmpeg decode -> AudioRouter -> null pacer + viz tap, optional local monitor output).
/// Device-free by design: no audio hardware is opened or required, so a headless box (no output
/// devices, or no PortAudio at all) still decodes on schedule and feeds the viz/NDI tap. Pacing
/// comes from a <see cref="NullClockedAudioOutput"/> that consumes at exactly chunk/rate on
/// absolute deadlines and masters the MediaClock, so decode/NDI cadence is sample-accurate and
/// drift-free rather than wall-clock approximated. MediaPlayer has no end-of-track event - natural
/// end is IsRunning flipping false - so the owner must call <see cref="Poll"/> from a UI timer to
/// get <see cref="PlaybackEnded"/>. All members are UI-thread only; only the tap's Submit runs on
/// the audio thread.
/// </summary>
public sealed class DesktopMiniPlayer(IMediaRegistry registry, IAudioBackend? backend, PcmSubmit sink)
    : IDisposable
{
    // The device-free pacing output (Ideas/Next-Round-Plan-2026-07-28.md F3). Attached FIRST, while
    // AutoWirePrimary is still on, so the router slaves its pacing to it and the MediaClock masters
    // from its consumed-sample clock; AutoWirePrimary then goes off so no later output can displace
    // it. Deliberately attached whether or not local monitoring is on: the show-critical NDI feed
    // must not change its pacing source when the operator toggles a monitor mid-track.
    private const string PacerOutputId = "pacer";

    // The optional local-monitor device output. Never the clock: AutoWirePrimary is off by the time
    // it attaches, so it is an ordinary drop-on-overflow slave - it can be attached/detached
    // mid-track without touching decode pacing or the visible playhead.
    private const string MonitorOutputId = "monitor";

    // Detaching cuts the route hard (RemoveOutput abandons queued chunks), so "off" first fades
    // the route to silence (click-free one-chunk ramp) and only physically detaches after the
    // fade + pump + device ring have drained. Poll() performs the deferred detach.
    private const long MonitorDetachDelayMs = 250;

    private MediaPlayer? _player;
    private bool _paused;
    private bool _endedRaised;
    private string? _deviceId;
    private bool _localOutputEnabled = true;
    private string? _monitorOutputId;
    private IDisposable? _monitorOutput;
    private long? _monitorDetachDueMs;
    private NullClockedAudioOutput? _pacer;

    public event Action<TrackInfo>? TrackStarted;
    public event Action? PlaybackEnded;
    public event Action<string>? PlaybackError;

    public bool HasTrack => _player is not null;

    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        if (backend is null)
            return [];
        try
        {
            return backend.EnumerateOutputDevices();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Null = backend default (allowed here - monitoring is explicitly local). Takes
    /// effect from the NEXT track or the next monitoring re-enable (the live monitor stream keeps
    /// its already-opened device).</summary>
    public void SetOutputDevice(string? deviceId) => _deviceId = deviceId;

    /// <summary>False (the box only feeds NDI - the Android head's PlayOnDevice) genuinely closes
    /// the local device; decode pacing and the viz/NDI tap are unaffected either way because the
    /// monitor is never the clock. Applies to the current track (click-free fade-out, then
    /// detach on a later <see cref="Poll"/>) and to every following one.</summary>
    public void SetLocalOutputEnabled(bool enabled)
    {
        _localOutputEnabled = enabled;
        if (_player is null)
            return;
        if (enabled)
        {
            _monitorDetachDueMs = null;
            if (_monitorOutputId is not null)
                SetMonitorGain(1f); // fade-out was pending - just restore the still-attached route
            else
                AttachMonitorOutput();
        }
        else if (_monitorOutputId is not null)
        {
            SetMonitorGain(0f);
            _monitorDetachDueMs = Environment.TickCount64 + MonitorDetachDelayMs;
        }
    }

    private void SetMonitorGain(float gain)
    {
        if (_player is not { AudioRouter: { } router, AudioSourceId: { } sourceId } || _monitorOutputId is not { } id)
            return;
        try
        {
            router.SetRouteGain(sourceId, id, gain);
        }
        catch (InvalidOperationException)
        {
            // Route already gone (racing teardown) - the detach path cleans up.
        }
    }

    /// <summary>Best-effort: monitoring is a convenience, so a missing backend/device or a failed
    /// stream must never fail playback (and must not raise <see cref="PlaybackError"/>, which
    /// error-skips the playlist).</summary>
    private void AttachMonitorOutput()
    {
        if (backend is null || _monitorOutputId is not null)
            return;
        if (_player is not { AudioRouter: not null, AudioSourceId: not null } player)
            return;
        IAudioOutput? output = null;
        try
        {
            var devices = backend.EnumerateOutputDevices();
            if (devices.Count == 0)
                return; // headless box - nothing to monitor on
            // A selected device that disappeared (USB churn) falls back to the backend default
            // instead of failing - monitoring should survive device churn on a show box.
            var device = _deviceId is null
                ? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault()
                : devices.FirstOrDefault(d => d.Id == _deviceId);
            var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
            output = backend.CreateOutput(device?.Id, new AudioFormat(rate, 2));
            _monitorOutputId = player.AttachAudioOutput(output, MonitorOutputId);
            _monitorOutput = output as IDisposable;
        }
        catch (Exception)
        {
            (output as IDisposable)?.Dispose();
        }
    }

    private void DetachMonitorOutput()
    {
        _monitorDetachDueMs = null;
        if (_monitorOutputId is { } id)
        {
            try
            {
                _player?.AudioRouter?.RemoveOutput(id);
            }
            catch (ObjectDisposedException)
            {
                // Player teardown already removed it.
            }
            _monitorOutputId = null;
        }
        try
        {
            _monitorOutput?.Dispose();
        }
        catch (Exception)
        {
            // A dying device stream must not take the UI thread down.
        }
        _monitorOutput = null;
    }

    public void Play(TrackInfo track)
    {
        Stop();
        MediaPlayer? player = null;
        try
        {
            // No device output: the source is consumed (Open wires a discarding sink) with zero
            // audio hardware. The null pacer below supplies the sample clock the device would have.
            player = MediaPlayer.Open(registry, track.Uri);
            var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
            var pacerFormat = new AudioFormat(rate, 2);

            // Attach the pacer FIRST (AutoWirePrimary still on): the router's auto-promotion slaves
            // its pacing clock to this output and masters the MediaClock from it - both only legal
            // while the router is stopped, hence before Play(). Then AutoWirePrimary goes off so the
            // optional monitor device can never displace it as primary / MediaClock master, even
            // when attached later while the router is stopped (paused).
            if (player.AudioRouter is not null)
            {
                _pacer = new NullClockedAudioOutput(pacerFormat);
                player.AttachAudioOutput(_pacer, PacerOutputId);
            }
            if (player.AudioRouter is { } router)
                router.AutoWirePrimary = false;

            player.AttachAudioOutput(new VizTapAudioOutput(pacerFormat, sink), "viz-tap");
            _player = player;
            if (_localOutputEnabled)
                AttachMonitorOutput();
            // Nothing else starts a device-free output: without this the pacer reports "always
            // ready" and the router would free-run instead of pacing (see its WaitForCapacity).
            _pacer?.Start();
            player.Play();
            _paused = false;
            _endedRaised = false;
            TrackStarted?.Invoke(track);
        }
        catch (Exception ex)
        {
            player?.Dispose();
            _player = null;
            _monitorOutputId = null;
            DetachMonitorOutput();
            DisposePacer();
            PlaybackError?.Invoke(ex.Message);
        }
    }

    /// <summary>Releases the pacing output. Always AFTER the player is disposed - the router's pump
    /// must have stopped submitting to it first.</summary>
    private void DisposePacer()
    {
        try
        {
            _pacer?.Dispose();
        }
        catch (Exception)
        {
            // A device-free output cannot really fail here, but teardown must never take the UI down.
        }
        _pacer = null;
    }

    public void Pause()
    {
        if (_player is not { } player || _paused)
            return;
        try
        {
            player.Pause();
            _paused = true;
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    public void Resume()
    {
        if (_player is not { } player || !_paused)
            return;
        try
        {
            player.Play();
            _paused = false;
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    public void Stop()
    {
        // Player first (stops the router and its pumps), then the outputs it was submitting to.
        _player?.Dispose();
        _player = null;
        _monitorOutputId = null; // router (and its outputs' routes) died with the player
        DetachMonitorOutput();
        DisposePacer();
        _paused = false;
    }

    /// <summary>Call periodically from the UI thread; raises <see cref="PlaybackEnded"/> once when
    /// the current track finished on its own. Also completes a deferred monitor detach.</summary>
    public void Poll()
    {
        if (_monitorDetachDueMs is { } due && Environment.TickCount64 >= due)
            DetachMonitorOutput();
        if (_player is not { } player || _paused || _endedRaised)
            return;
        // No start grace needed: the pacer is started before Play() and reports advancing from
        // that moment, so IsRunning can't read false while hardware warms up (there is none).
        if (player.IsRunning)
            return;
        _endedRaised = true;
        PlaybackEnded?.Invoke();
    }

    public void Dispose() => Stop();
}
