using Microsoft.Extensions.Logging;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.Playback;

/// <summary>Lifecycle state of a <see cref="MediaPlayer"/>.</summary>
public enum PlaybackState
{
    /// <summary>No media loaded; initial state after construction or after <see cref="MediaPlayer.StopAsync"/>.</summary>
    Idle,
    /// <summary>An <see cref="MediaPlayer.OpenAsync(string,FFmpegDecoderOptions?,CancellationToken)"/> call is in progress.</summary>
    Opening,
    /// <summary>Media is open and ready; call <see cref="MediaPlayer.PlayAsync"/> to start.</summary>
    Ready,
    /// <summary>Actively rendering audio/video.</summary>
    Playing,
    /// <summary>Output paused; decode pipeline stays warm.</summary>
    Paused,
    /// <summary>A <see cref="MediaPlayer.StopAsync"/> call is in progress.</summary>
    Stopping,
    /// <summary>Playback stopped; the player may be reused via <see cref="MediaPlayer.OpenAsync(string,FFmpegDecoderOptions?,CancellationToken)"/>.</summary>
    Stopped,
    /// <summary>An unrecoverable error occurred; inspect <see cref="MediaPlayer.PlaybackFailed"/>.</summary>
    Faulted
}

/// <summary>Describes why playback ended.</summary>
public enum PlaybackCompletedReason
{
    /// <summary>The media source reached end-of-file.</summary>
    SourceEnded,
    /// <summary>The user called <see cref="MediaPlayer.StopAsync"/>.</summary>
    StoppedByUser,
    /// <summary>A new <see cref="MediaPlayer.OpenAsync(string,FFmpegDecoderOptions?,CancellationToken)"/> call replaced the current session.</summary>
    ReplacedByOpen,
    /// <summary>An exception occurred during playback.</summary>
    Faulted,
}

/// <summary>Identifies which transport operation failed.</summary>
public enum PlaybackFailureStage
{
    /// <summary>Failure during <see cref="MediaPlayer.OpenAsync(string,FFmpegDecoderOptions?,CancellationToken)"/>.</summary>
    Open,
    /// <summary>Failure during <see cref="MediaPlayer.PlayAsync"/>.</summary>
    Play,
    /// <summary>Failure during <see cref="MediaPlayer.PauseAsync"/>.</summary>
    Pause,
    /// <summary>Failure during <see cref="MediaPlayer.StopAsync"/>.</summary>
    Stop,
    /// <summary>Failure during runtime (e.g. RT callback).</summary>
    Runtime,
}

/// <summary>Carries the previous and current <see cref="PlaybackState"/> on transitions.</summary>
public sealed record PlaybackStateChangedEventArgs(PlaybackState Previous, PlaybackState Current);

/// <summary>Carries the reason playback finished.</summary>
public sealed record PlaybackCompletedEventArgs(PlaybackCompletedReason Reason);

/// <summary>Carries the stage and exception when a transport operation fails.</summary>
public sealed record PlaybackFailedEventArgs(PlaybackFailureStage Stage, Exception Exception);

/// <summary>Which streams to send to a combined audio+video sink (for example, an NDI AV sender) for the current file.</summary>
public enum AveStreamSelection
{
    /// <summary>Send both decoded audio and video when the source and routes exist.</summary>
    Both = 0,
    /// <summary>Send only audio; the video route to this output is disabled.</summary>
    AudioOnly = 1,
    /// <summary>Send only video; the audio route to this output is disabled.</summary>
    VideoOnly = 2,
}

/// <summary>
/// High-level one-source one-output playback facade built on
/// <see cref="FFmpegDecoder"/> and <see cref="AVRouter"/>.
/// </summary>
/// <remarks>
/// Typical audio-only usage:
/// <code>
/// using var player = new MediaPlayer();
/// player.AddEndpoint(audioOutput);
/// player.PlaybackCompleted += (_, _) => cts.Cancel();
/// await player.OpenAsync("file.mp3");
/// await player.PlayAsync();
/// try { await Task.Delay(Timeout.Infinite, cts.Token); }
/// catch (OperationCanceledException) { }
/// await player.StopAsync();
/// </code>
/// </remarks>
public sealed class MediaPlayer : IAsyncDisposable, IDisposable
{
    private static readonly ILogger Log = MediaCoreLogging.GetLogger("S.Media.Playback.MediaPlayer");
    private static readonly TimeSpan SeekPresentPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan SeekPositionTolerance = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan DefaultSeekPresentTimeout = TimeSpan.FromSeconds(2);

    private readonly AVRouter _router;
    private readonly List<IMediaEndpoint> _endpoints = [];
    private readonly List<EndpointId> _endpointIds = [];
    /// <summary>Parallel to <see cref="_endpoints"/>: per-endpoint A/V <see cref="RouteId"/>s (null = no such route in this session).</summary>
    private readonly List<(RouteId? Audio, RouteId? Video)> _perEndpointRoutes = [];
    private readonly List<(Func<MediaPlayer, CancellationToken, Task> BeforePlay, Func<MediaPlayer, CancellationToken, Task>? BeforeClose)> _lifecycleHooks = [];

    private FFmpegDecoder? _decoder;
    private float _volume = 1.0f;
    private bool _decoderStarted;
    private volatile bool _disposed;
    private volatile PlaybackState _state = PlaybackState.Idle;
    private int _completionRaised;

    private InputId? _audioInputId;
    private InputId? _videoInputId;

    private AvDriftCorrectionOptions? _driftCorrectionOptions;
    private CancellationTokenSource? _driftCts;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="MediaPlayer"/>. Endpoints can be added before or after construction.
    /// </summary>
    public MediaPlayer() : this(routerOptions: null)
    {
    }

    /// <summary>
    /// Creates a <see cref="MediaPlayer"/> with custom router options — used by
    /// <see cref="MediaPlayerBuilder"/> so callers can tune clock selection, tick
    /// cadence and mixer behaviour without constructing an <see cref="AVRouter"/>
    /// by hand. Kept <c>internal</c> because public surface should go through the
    /// builder (§5.1 / review §"Proposed simplified API").
    /// </summary>
    internal MediaPlayer(AVRouterOptions? routerOptions)
    {
        _router = new AVRouter(routerOptions);
        _router.ActiveClockChanged += OnActiveClockChanged;
    }

    private void OnActiveClockChanged(IMediaClock c)
    {
        try
        {
            var pos = c.Position;
            var running = c.IsRunning;
            Log.LogDebug("ActiveClockChanged → {Type} (running={Run}, pos≈{PosMs:F1}ms)", c.GetType().Name, running, pos.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Active clock handler: failed to read clock for logging.");
        }
    }

    /// <summary>
    /// Starts a fluent <see cref="MediaPlayerBuilder"/> so endpoints, clock,
    /// decoder/router options and an error handler can be declared up-front,
    /// then the fully-wired <see cref="MediaPlayer"/> materialises on
    /// <see cref="MediaPlayerBuilder.Build"/>.
    /// </summary>
    public static MediaPlayerBuilder Create() => new();

    /// <summary>
    /// Default <see cref="FFmpegDecoderOptions"/> applied when
    /// <see cref="OpenAsync(string, FFmpegDecoderOptions?, CancellationToken)"/>
    /// is called with a <see langword="null"/> options argument.
    /// </summary>
    internal FFmpegDecoderOptions? DefaultDecoderOptions { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="PlaybackStateChangedEventArgs"/>
    /// <remarks>
    /// §2.8 — dispatched synchronously on the thread that drove the state change
    /// (typically the caller of <c>PlayAsync</c>/<c>PauseAsync</c>/<c>StopAsync</c>,
    /// or the decoder completion thread for the final <c>Stopped</c> transition).
    /// Handlers must not block; offload heavy work to <see cref="Task.Run(Action)"/>.
    /// </remarks>
    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

    /// <inheritdoc cref="PlaybackCompletedEventArgs"/>
    /// <remarks>
    /// §2.8 — dispatched on the decoder's completion thread (the one that observed
    /// <c>EndOfMedia</c>). Handlers that start another playback should use
    /// <c>Task.Run</c> to avoid reentering the decoder lifecycle from its own
    /// completion callback.
    /// </remarks>
    public event EventHandler<PlaybackCompletedEventArgs>? PlaybackCompleted;

    /// <inheritdoc cref="PlaybackFailedEventArgs"/>
    /// <remarks>
    /// §2.8 — dispatched on whichever thread observed the failure (demux worker,
    /// decode worker, RT push thread). Handlers must not block; the player is
    /// already transitioning to <see cref="PlaybackState.Stopped"/> when this
    /// fires.
    /// </remarks>
    public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;

    // ── Properties ───────────────────────────────────────────────────────────

    /// <summary>Whether the output is actively rendering.</summary>
    public bool IsPlaying => _state == PlaybackState.Playing && !_disposed;

    /// <summary>Current playback state.</summary>
    public PlaybackState State => _state;

    /// <summary>Total duration of the currently open media.</summary>
    public TimeSpan? Duration => _decoder?.Duration;

    /// <summary>When true, playback restarts from the beginning on EOF.</summary>
    public bool IsLooping { get; set; }

    /// <summary>Current decode position.</summary>
    public TimeSpan Position =>
        AudioChannel?.Position ?? VideoChannel?.Position ?? TimeSpan.Zero;

    /// <summary>Normalized playback progress [0..1].</summary>
    public double NormalizedPosition
    {
        get
        {
            var dur = Duration;
            if (!dur.HasValue || dur.Value <= TimeSpan.Zero) return 0.0;
            return Math.Clamp(Position / dur.Value, 0.0, 1.0);
        }
    }

    /// <summary>Playback volume. Range [0..2], default 1.0.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0f, 2f);
            _volume = value;
            if (_audioInputId.HasValue)
                _router.SetInputVolume(_audioInputId.Value, value);
        }
    }

    /// <summary>First audio channel of the current decoder.</summary>
    public IAudioChannel? AudioChannel => _decoder?.FirstAudioChannel;

    /// <summary>First video channel of the current decoder.</summary>
    public IVideoChannel? VideoChannel => _decoder?.FirstVideoChannel;

    /// <summary>The underlying router for advanced routing scenarios.</summary>
    public IAVRouter Router => _router;

    // ── Endpoint management ───────────────────────────────────────────────────

    /// <summary>
    /// Registers an audio endpoint.
    /// </summary>
    public void AddEndpoint(IAudioEndpoint endpoint)
    {
        RegisterEndpointAndMaybeStart(endpoint, _router.RegisterEndpoint(endpoint), audio: true, video: false);
    }

    /// <summary>Registers a video endpoint.</summary>
    public void AddEndpoint(IVideoEndpoint endpoint)
    {
        RegisterEndpointAndMaybeStart(endpoint, _router.RegisterEndpoint(endpoint), audio: false, video: true);
    }

    /// <summary>Registers a dual audio+video endpoint.</summary>
    public void AddEndpoint(IAVEndpoint endpoint)
    {
        RegisterEndpointAndMaybeStart(endpoint, _router.RegisterEndpoint(endpoint), audio: true, video: true);
    }

    /// <summary>Removes a previously added endpoint.</summary>
    public void RemoveEndpoint(IMediaEndpoint endpoint)
    {
        int idx = _endpoints.IndexOf(endpoint);
        if (idx < 0) return;
        var epId = _endpointIds[idx];

        if (IsActive)
            endpoint.StopAsync().GetAwaiter().GetResult();

        _router.UnregisterEndpoint(epId);
        _endpoints.RemoveAt(idx);
        _endpointIds.RemoveAt(idx);
        _perEndpointRoutes.RemoveAt(idx);
    }

    /// <summary>
    /// Async counterpart to <see cref="AddEndpoint(IAudioEndpoint)"/>.
    /// </summary>
    public Task AddEndpointAsync(IAudioEndpoint endpoint, CancellationToken ct = default)
        => RegisterEndpointAndMaybeStartAsync(endpoint, _router.RegisterEndpoint(endpoint), audio: true, video: false, ct);

    /// <inheritdoc cref="AddEndpointAsync(IAudioEndpoint, CancellationToken)"/>
    public Task AddEndpointAsync(IVideoEndpoint endpoint, CancellationToken ct = default)
        => RegisterEndpointAndMaybeStartAsync(endpoint, _router.RegisterEndpoint(endpoint), audio: false, video: true, ct);

    /// <inheritdoc cref="AddEndpointAsync(IAudioEndpoint, CancellationToken)"/>
    public Task AddEndpointAsync(IAVEndpoint endpoint, CancellationToken ct = default)
        => RegisterEndpointAndMaybeStartAsync(endpoint, _router.RegisterEndpoint(endpoint), audio: true, video: true, ct);

    /// <summary>
    /// Async counterpart to <see cref="RemoveEndpoint"/>.
    /// </summary>
    public async Task RemoveEndpointAsync(IMediaEndpoint endpoint, CancellationToken ct = default)
    {
        int idx = _endpoints.IndexOf(endpoint);
        if (idx < 0) return;
        var epId = _endpointIds[idx];

        if (IsActive)
            await endpoint.StopAsync(ct).ConfigureAwait(false);

        _router.UnregisterEndpoint(epId);
        _endpoints.RemoveAt(idx);
        _endpointIds.RemoveAt(idx);
        _perEndpointRoutes.RemoveAt(idx);
    }

    /// <summary>
    /// Disables the audio and/or video routes to a registered <see cref="IAVEndpoint"/>
    /// so, for example, you can send only audio to NDI while still routing the full
    /// program to a local display.
    /// </summary>
    public void SetAveStreamSelection(IAVEndpoint endpoint, AveStreamSelection selection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        int idx = _endpoints.IndexOf(endpoint);
        if (idx < 0)
            throw new ArgumentException("Endpoint is not registered with this player.", nameof(endpoint));

        bool wantAudio = selection is AveStreamSelection.Both or AveStreamSelection.AudioOnly;
        bool wantVideo = selection is AveStreamSelection.Both or AveStreamSelection.VideoOnly;
        var (a, v) = _perEndpointRoutes[idx];
        if (a is { } ar) _router.SetRouteEnabled(ar, wantAudio);
        if (v is { } vr) _router.SetRouteEnabled(vr, wantVideo);
        Log.LogInformation(
            "SetAveStreamSelection: endpoint='{Name}' ({Type}) mode={Mode} wantAudio={WA} wantVideo={WV} routeAudio={HasA} routeVideo={HasV}",
            endpoint.Name, endpoint.GetType().Name, selection, wantAudio, wantVideo, a, v);
    }

    // ── External input registration (§5.7) ───────────────────────────────────

    /// <summary>
    /// §5.7 — Registers a non-FFmpeg audio input and auto-routes it to every
    /// already-registered audio-capable endpoint. Idempotent per session.
    /// </summary>
    internal InputId RegisterExternalAudioInput(IAudioChannel channel, AudioRouteOptions? routeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (_audioInputId.HasValue)
            return _audioInputId.Value;

        var inputId = _router.RegisterAudioInput(channel);
        _audioInputId = inputId;
        _router.SetInputVolume(inputId, _volume);

        for (int i = 0; i < _endpoints.Count; i++)
        {
            if (_endpoints[i] is IAudioEndpoint or IAVEndpoint)
            {
                var r = routeOptions is null
                    ? _router.CreateRoute(inputId, _endpointIds[i])
                    : _router.CreateRoute(inputId, _endpointIds[i], routeOptions);
                var t = _perEndpointRoutes[i];
                _perEndpointRoutes[i] = (r, t.Video);
            }
        }

        return inputId;
    }

    /// <summary>
    /// §5.7 — Registers a non-FFmpeg video input and auto-routes it to every
    /// already-registered video-capable endpoint. Idempotent per session.
    /// </summary>
    internal InputId RegisterExternalVideoInput(IVideoChannel channel, VideoRouteOptions? routeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (_videoInputId.HasValue)
            return _videoInputId.Value;

        var inputId = _router.RegisterVideoInput(channel);
        _videoInputId = inputId;

        for (int i = 0; i < _endpoints.Count; i++)
        {
            if (_endpoints[i] is IVideoEndpoint v)
            {
                var r = routeOptions is null
                    ? CreateVideoRouteToEndpoint(_router, inputId, _endpointIds[i], v)
                    : _router.CreateRoute(inputId, _endpointIds[i], routeOptions);
                var t = _perEndpointRoutes[i];
                _perEndpointRoutes[i] = (t.Audio, r);
            }
        }

        return inputId;
    }

    // ── Lifecycle hooks (extension point) ────────────────────────────────────

    internal void AddLifecycleHook(
        Func<MediaPlayer, CancellationToken, Task> beforePlay,
        Func<MediaPlayer, CancellationToken, Task>? beforeClose = null)
    {
        ArgumentNullException.ThrowIfNull(beforePlay);
        _lifecycleHooks.Add((beforePlay, beforeClose));
    }

    // ── Drift correction (§5.9) ──────────────────────────────────────────────

    internal void ConfigureDriftCorrection(AvDriftCorrectionOptions options)
    {
        _driftCorrectionOptions = options;
    }

    internal void StartDriftCorrectionLoop(AvDriftCorrectionOptions options)
    {
        if (_driftCts is not null)
            return;
        if (!_audioInputId.HasValue || !_videoInputId.HasValue)
            return;

        var audioId = _audioInputId.Value;
        var videoId = _videoInputId.Value;
        _driftCts = new CancellationTokenSource();
        var ct = _driftCts.Token;

        _ = Task.Run(async () =>
        {
            double currentOffsetMs = 0;

            try { await Task.Delay(options.InitialDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var drift = _router.GetAvDrift(audioId, videoId);
                    double absDriftMs = Math.Abs(drift.TotalMilliseconds);

                    if (absDriftMs < options.MinDriftMs)
                    {
                        // no-op
                    }
                    else if (absDriftMs < options.IgnoreOutlierDriftMs)
                    {
                        double requestedStepMs = -drift.TotalMilliseconds * options.CorrectionGain;
                        double clampedStepMs = Math.Clamp(requestedStepMs, -options.MaxStepMs, options.MaxStepMs);
                        double nextOffsetMs = Math.Clamp(currentOffsetMs + clampedStepMs, -options.MaxAbsOffsetMs, options.MaxAbsOffsetMs);
                        currentOffsetMs = nextOffsetMs;
                        // Keep the correction on video so audio hardware remains the primary cadence source.
                        _router.SetInputTimeOffset(videoId, TimeSpan.FromMilliseconds(nextOffsetMs));
                    }

                    await Task.Delay(options.Interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Best effort: keep loop alive.
                    try { await Task.Delay(options.Interval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    private void StopDriftCorrectionLoop()
    {
        if (_driftCts is not { } cts) return;
        _driftCts = null;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    // ── Open ──────────────────────────────────────────────────────────────────

    /// <summary>Opens the media file and prepares the pipeline.</summary>
    public async Task OpenAsync(
        string path,
        FFmpegDecoderOptions? options = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Opening);
        try
        {
            // Keep hardware endpoints running when the media file is replaced (shared output pool).
            await CloseAsync(ct, PlaybackCompletedReason.ReplacedByOpen, stopRegisteredEndpoints: false)
                .ConfigureAwait(false);
            AttachDecoder(FFmpegDecoder.Open(path, options ?? DefaultDecoderOptions));
            SetState(PlaybackState.Ready);
            Log.LogInformation(
                "OpenAsync(file) ready: {Path} audioInput={A} videoInput={V} endpointCount={E}",
                path, _audioInputId, _videoInputId, _endpoints.Count);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            Log.LogError(ex, "OpenAsync(file) failed: {Path}", path);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Open, ex));
            throw;
        }
    }

    /// <summary>Opens the media from a stream and prepares the pipeline.</summary>
    public async Task OpenAsync(
        Stream stream,
        FFmpegDecoderOptions? options = null,
        bool leaveOpen = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Opening);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.ReplacedByOpen, stopRegisteredEndpoints: false)
                .ConfigureAwait(false);
            AttachDecoder(FFmpegDecoder.Open(stream, options ?? DefaultDecoderOptions, leaveOpen));
            SetState(PlaybackState.Ready);
            Log.LogInformation(
                "OpenAsync(stream) ready: audioInput={A} videoInput={V} endpointCount={E}",
                _audioInputId, _videoInputId, _endpoints.Count);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            Log.LogError(ex, "OpenAsync(stream) failed");
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Open, ex));
            throw;
        }
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    /// <summary>Starts or resumes playback.</summary>
    public async Task PlayAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (_lifecycleHooks.Count > 0)
                await RunBeforePlayHooks(ct).ConfigureAwait(false);

            if (_decoder == null && !_audioInputId.HasValue && !_videoInputId.HasValue)
            {
                throw new MediaException(
                    "No media is open and no external inputs are registered. Call OpenAsync first or register inputs via the builder.");
            }

            if (_decoder != null && !_decoderStarted)
            {
                _decoder.Start();
                _decoderStarted = true;
                // Give decoders a brief head start so push A/V sinks (notably NDI-only)
                // don't emit video significantly ahead of the first audio buffer.
                await WarmupDecodedBuffersAsync(ct).ConfigureAwait(false);
            }

            // When endpoints are shared with an external output pool, they can already be
            // running; IMediaEndpoint.StartAsync must not be called again (e.g. PortAudio
            // Pa_StartStream on an active stream will fail with paStreamIsNotStopped).
            foreach (var ep in _endpoints)
            {
                if (!ep.IsRunning)
                    await ep.StartAsync(ct).ConfigureAwait(false);
            }

            await _router.StartAsync(ct).ConfigureAwait(false);

            if (_driftCorrectionOptions is { } dco)
                StartDriftCorrectionLoop(dco);

            SetState(PlaybackState.Playing);
            Log.LogInformation(
                "PlayAsync: playing; endpoints={N} router clock={ClockType}",
                _endpoints.Count, _router.Clock.GetType().Name);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            Log.LogError(ex, "PlayAsync failed");
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Play, ex));
            throw;
        }
    }

    /// <summary>Pauses playback.</summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await _router.StopAsync(ct).ConfigureAwait(false);
            foreach (var ep in _endpoints)
                await ep.StopAsync(ct).ConfigureAwait(false);

            StopDriftCorrectionLoop();
            SetState(PlaybackState.Paused);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Pause, ex));
            throw;
        }
    }

    /// <summary>Stops playback and releases the current media.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="stopRegisteredEndpoints">
    /// When <see langword="true"/> (default), each <see cref="IMediaEndpoint"/> added via
    /// <c>AddEndpoint</c> is stopped like today. Set to <see langword="false"/> when those endpoints
    /// are owned/managed elsewhere (e.g. a global output pool) and only the transport graph should
    /// be torn down.
    /// </param>
    public async Task StopAsync(CancellationToken ct = default, bool stopRegisteredEndpoints = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Stopping);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.StoppedByUser, stopRegisteredEndpoints)
                .ConfigureAwait(false);
            SetState(PlaybackState.Stopped);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Stop, ex));
            throw;
        }
    }

    /// <summary>Seeks to <paramref name="position"/>.</summary>
    public void Seek(TimeSpan position) => _decoder?.Seek(position);

    /// <summary>
    /// Seeks to <paramref name="position"/> and resolves when the first post-seek media is present.
    /// For active playback (<see cref="PlaybackState.Playing"/> / <see cref="PlaybackState.Paused"/>),
    /// this waits until at least one channel exposes post-seek buffered data.
    /// </summary>
    public Task SeekAsync(
        TimeSpan position,
        CancellationToken ct = default)
        => SeekAsync(position, DefaultSeekPresentTimeout, ct);

    /// <inheritdoc cref="SeekAsync(TimeSpan, CancellationToken)"/>
    public async Task SeekAsync(
        TimeSpan position,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Seek timeout must be greater than zero.");
        if (_decoder is null)
            throw new MediaException("No media is open. Call OpenAsync before SeekAsync.");

        _decoder.Seek(position);

        // No active transport => no "presented post-seek" signal to await.
        if (_state is not PlaybackState.Playing and not PlaybackState.Paused)
            return;

        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (HasPostSeekPresentation(position))
                return;

            if (DateTimeOffset.UtcNow - started >= timeout)
            {
                throw new TimeoutException(
                    $"SeekAsync timed out waiting for first post-seek media at {position}.");
            }

            await Task.Delay(SeekPresentPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Opens and immediately starts playback.</summary>
    public async Task OpenAndPlayAsync(
        string path,
        FFmpegDecoderOptions? options = null,
        CancellationToken ct = default)
    {
        await OpenAsync(path, options, ct).ConfigureAwait(false);
        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Opens from stream and immediately starts playback.</summary>
    public async Task OpenAndPlayAsync(
        Stream stream,
        FFmpegDecoderOptions? options = null,
        bool leaveOpen = false,
        CancellationToken ct = default)
    {
        await OpenAsync(stream, options, leaveOpen, ct).ConfigureAwait(false);
        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits natural end-of-media (source EOF) or an unrecoverable playback
    /// failure, whichever comes first.
    /// </summary>
    public async Task<PlaybackCompletedReason> WaitForCompletionAsync(
        TimeSpan drainGrace = default,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (drainGrace == default) drainGrace = TimeSpan.FromMilliseconds(300);

        var tcs = new TaskCompletionSource<PlaybackCompletedReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? s, PlaybackCompletedEventArgs e) => tcs.TrySetResult(e.Reason);
        void OnFailed(object? s, PlaybackFailedEventArgs e) => tcs.TrySetException(e.Exception);

        PlaybackCompleted += OnCompleted;
        PlaybackFailed += OnFailed;

        if (_state is PlaybackState.Stopped or PlaybackState.Idle or PlaybackState.Faulted)
            tcs.TrySetResult(PlaybackCompletedReason.SourceEnded);

        try
        {
            using var reg = ct.Register(static state =>
                ((TaskCompletionSource<PlaybackCompletedReason>)state!).TrySetCanceled(), tcs);

            var reason = await tcs.Task.ConfigureAwait(false);

            if (reason == PlaybackCompletedReason.SourceEnded && drainGrace > TimeSpan.Zero)
            {
                try { await Task.Delay(drainGrace, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            return reason;
        }
        finally
        {
            PlaybackCompleted -= OnCompleted;
            PlaybackFailed -= OnFailed;
        }
    }

    // ── IDisposable / IAsyncDisposable ────────────────────────────────────────

    /// <summary>
    /// Synchronous disposal. Delegates to <see cref="DisposeAsync"/>; prefer the
    /// async variant in async call-paths.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Cooperatively stops the router + every registered endpoint + decoder, then
    /// disposes the underlying <see cref="AVRouter"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsActive)
        {
            try { await _router.StopAsync().ConfigureAwait(false); } catch { }

            if (_endpoints.Count > 0)
            {
                var stops = new Task[_endpoints.Count];
                for (int i = 0; i < _endpoints.Count; i++)
                {
                    var ep = _endpoints[i];
                    stops[i] = Task.Run(async () =>
                    {
                        try { await ep.StopAsync().ConfigureAwait(false); } catch { }
                    });
                }

                try { await Task.WhenAll(stops).ConfigureAwait(false); } catch { }
            }
        }

        if (_decoder is { } dec)
        {
            try { await dec.StopAsync().ConfigureAwait(false); } catch { }
        }

        if (_lifecycleHooks.Count > 0)
        {
            try { await RunBeforeCloseHooks(CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        ReleaseSession();
        _router.ActiveClockChanged -= OnActiveClockChanged;
        _router.Dispose();
        SetState(PlaybackState.Stopped);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the player is currently running (endpoints/router have been
    /// started and not yet stopped).
    /// </summary>
    private bool IsActive => _state is PlaybackState.Playing or PlaybackState.Paused;

    private void RegisterEndpointAndMaybeStart(IMediaEndpoint endpoint, EndpointId id, bool audio, bool video)
    {
        _endpoints.Add(endpoint);
        _endpointIds.Add(id);
        _perEndpointRoutes.Add((null, null));
        Log.LogDebug("AddEndpoint: {Type} '{Name}' wantAudio={A} wantVideo={V} (session has audioIn={HasA} videoIn={HasV})",
            endpoint.GetType().Name, endpoint.Name, audio, video, _audioInputId, _videoInputId);
        AutoRouteToEndpoint(id, audio, video);

        if (IsActive)
            endpoint.StartAsync().GetAwaiter().GetResult();
    }

    private async Task RegisterEndpointAndMaybeStartAsync(IMediaEndpoint endpoint, EndpointId id, bool audio, bool video, CancellationToken ct)
    {
        _endpoints.Add(endpoint);
        _endpointIds.Add(id);
        _perEndpointRoutes.Add((null, null));
        Log.LogDebug("AddEndpointAsync: {Type} '{Name}' wantAudio={A} wantVideo={V}", endpoint.GetType().Name, endpoint.Name, audio, video);
        AutoRouteToEndpoint(id, audio, video);

        if (IsActive)
            await endpoint.StartAsync(ct).ConfigureAwait(false);
    }

    private void AttachDecoder(FFmpegDecoder decoder)
    {
        if (decoder.FirstAudioChannel is { } audioCh)
        {
            var inputId = _router.RegisterAudioInput(audioCh);
            _audioInputId = inputId;
            _router.SetInputVolume(inputId, _volume);

            for (int i = 0; i < _endpoints.Count; i++)
            {
                if (_endpoints[i] is IAudioEndpoint)
                {
                    var r = _router.CreateRoute(inputId, _endpointIds[i]);
                    var t = _perEndpointRoutes[i];
                    _perEndpointRoutes[i] = (r, t.Video);
                }
            }
        }

        if (decoder.FirstVideoChannel is { } videoCh)
        {
            var inputId = _router.RegisterVideoInput(videoCh);
            _videoInputId = inputId;

            for (int i = 0; i < _endpoints.Count; i++)
            {
                if (_endpoints[i] is IVideoEndpoint v)
                {
                    var r = CreateVideoRouteToEndpoint(_router, inputId, _endpointIds[i], v);
                    var t = _perEndpointRoutes[i];
                    _perEndpointRoutes[i] = (t.Audio, r);
                }
            }
        }

        decoder.EndOfMedia += OnEndOfMedia;
        _decoder = decoder;
        _decoderStarted = false;
        for (int i = 0; i < _endpoints.Count; i++)
        {
            var (ar, vr) = _perEndpointRoutes[i];
            Log.LogDebug(
                "AttachDecoder: ep[{I}] {Type} '{Name}' routeA={A} routeV={V}",
                i, _endpoints[i].GetType().Name, _endpoints[i].Name, ar, vr);
        }
    }

    private void AutoRouteToEndpoint(EndpointId epId, bool audio, bool video)
    {
        int idx = _endpointIds.IndexOf(epId);
        if (idx < 0) return;

        if (audio && _audioInputId.HasValue)
        {
            var r = _router.CreateRoute(_audioInputId.Value, epId);
            var t = _perEndpointRoutes[idx];
            _perEndpointRoutes[idx] = (r, t.Video);
        }
        if (video && _videoInputId.HasValue && _endpoints[idx] is IVideoEndpoint v)
        {
            var r = CreateVideoRouteToEndpoint(_router, _videoInputId.Value, epId, v);
            var t = _perEndpointRoutes[idx];
            _perEndpointRoutes[idx] = (t.Audio, r);
        }
    }

    /// <summary>
    /// Push sinks (e.g. NDI) do not have a <see cref="IClockCapableEndpoint"/>+pull clock that
    /// tracks decoded PTS. Without <see cref="VideoRouteOptions.LiveMode"/>, the push thread
    /// keeps frames pending against PortAudio or the internal stopwatch and video never
    /// reaches the sink. Pull renderers (Avalonia, SDL3) use the default scheduled mode.
    /// </summary>
    private static RouteId CreateVideoRouteToEndpoint(
        IAVRouter router, InputId inputId, EndpointId epId, IVideoEndpoint v) =>
        v is IAVEndpoint
            // Keep A/V sinks on scheduled mode so decoded-file bursts cannot run video
            // content far ahead of audio content. NDI endpoints still stamp/send using
            // their own timing, but router-side PTS gating prevents startup/steady-state
            // "video content lead" that manifests as audio-late at receivers.
            //
            // Also force Wait overflow for AV sinks: DropOldest skips older video frames
            // under transient pressure, which advances video content PTS relative to the
            // audio path and recreates the "video slightly ahead" symptom.
            ? router.CreateRoute(inputId, epId, new VideoRouteOptions
            {
                LiveMode = false,
                OverflowPolicy = VideoOverflowPolicy.Wait,
                Capacity = 12
            })
            : v is not IPullVideoEndpoint
            ? router.CreateRoute(inputId, epId, new VideoRouteOptions { LiveMode = true })
            : router.CreateRoute(inputId, epId);

    private async Task CloseAsync(
        CancellationToken ct,
        PlaybackCompletedReason? closeReason = null,
        bool stopRegisteredEndpoints = true)
    {
        bool hadSession = _decoder != null || _decoderStarted || _audioInputId.HasValue || _videoInputId.HasValue;

        // Always run the cooperative stop pass. This remains idempotent when
        // already stopped and avoids state-ordering gaps (e.g. Stopping/Opening
        // set before CloseAsync) where endpoint stop calls were skipped.
        {
            try { await _router.StopAsync(ct).ConfigureAwait(false); } catch { }
            if (stopRegisteredEndpoints)
            {
                foreach (var ep in _endpoints)
                {
                    try { await ep.StopAsync(ct).ConfigureAwait(false); } catch { }
                }
            }
        }

        if (_lifecycleHooks.Count > 0)
            await RunBeforeCloseHooks(ct).ConfigureAwait(false);

        ReleaseSession();

        if (hadSession && closeReason.HasValue)
        {
            if (Interlocked.Exchange(ref _completionRaised, 1) == 0)
                PlaybackCompleted?.Invoke(this, new PlaybackCompletedEventArgs(closeReason.Value));
        }
    }

    private void ReleaseSession()
    {
        Interlocked.Exchange(ref _completionRaised, 0);
        StopDriftCorrectionLoop();

        Log.LogDebug("ReleaseSession: tearing down {N} per-endpoint route sets", _perEndpointRoutes.Count);
        for (int i = 0; i < _perEndpointRoutes.Count; i++)
        {
            var (a, v) = _perEndpointRoutes[i];
            if (a is { } ar) _router.RemoveRoute(ar);
            if (v is { } vr) _router.RemoveRoute(vr);
        }
        _perEndpointRoutes.Clear();
        for (int n = 0; n < _endpoints.Count; n++)
            _perEndpointRoutes.Add((null, null));

        if (_audioInputId.HasValue)
        {
            _router.UnregisterInput(_audioInputId.Value);
            _audioInputId = null;
        }

        if (_videoInputId.HasValue)
        {
            _router.UnregisterInput(_videoInputId.Value);
            _videoInputId = null;
        }

        _decoder?.Dispose();
        _decoder = null;
        _decoderStarted = false;
    }

    private void OnEndOfMedia(object? sender, EventArgs e)
    {
        if (IsLooping && _decoder != null && !_disposed)
        {
            _decoder.Seek(TimeSpan.Zero);
            return;
        }

        // Don't raise SourceEnded the instant the demuxer hits EOF — the channel
        // ring and the endpoint's device buffer still have audio queued. Poll
        // the channel until it drains, then add a small device-side grace so
        // the tail reaches the speakers before listeners treat playback as over.
        _ = RaiseSourceEndedAfterDrainAsync();
    }

    private async Task RaiseSourceEndedAfterDrainAsync()
    {
        try
        {
            var ch = _decoder?.FirstAudioChannel;
            if (ch is not null)
            {
                // Only drain while we are still in the same "playing" session. When the user
                // opens another file, OpenAsync sets Opening before CloseAsync/ReleaseSession —
                // isStopping would NOT include Opening and this loop would spin until the
                // (often disposed) channel drained or worse.
                while (_disposed is false
                       && _state == PlaybackState.Playing
                       && ch.BufferAvailable > 0)
                    await Task.Delay(20).ConfigureAwait(false);

                if (_disposed is false
                    && _state == PlaybackState.Playing
                    && ch is not null)
                {
                    // Device buffer grace — PortAudio's host-API queue.
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Decoder disposed or channel torn down mid-drain, or a new file opened.
            return;
        }

        // ReplacedByOpen: state is no longer Playing (e.g. Opening, Ready) — do not treat as SourceEnded.
        if (_disposed || _state != PlaybackState.Playing)
        {
            if (!_disposed)
                Log.LogDebug("SourceEnded drain skipped: state={State} (new open replaced session).", _state);
            return;
        }
        if (Interlocked.Exchange(ref _completionRaised, 1) != 0) return;
        SetState(PlaybackState.Stopped);
        PlaybackCompleted?.Invoke(this, new PlaybackCompletedEventArgs(PlaybackCompletedReason.SourceEnded));
    }

    private bool IsStoppingOrStopped()
        => _state is PlaybackState.Stopping or PlaybackState.Stopped or PlaybackState.Idle;

    private bool HasPostSeekPresentation(TimeSpan target)
    {
        bool audioReady = false;
        bool videoReady = false;

        if (AudioChannel is { } audio)
        {
            audioReady = audio.BufferAvailable > 0 ||
                         audio.Position >= target - SeekPositionTolerance;
        }

        if (VideoChannel is { } video)
        {
            videoReady = video.BufferAvailable > 0 ||
                         video.Position >= target - SeekPositionTolerance;
        }

        return (AudioChannel, VideoChannel) switch
        {
            (null, null) => true,
            (not null, null) => audioReady,
            (null, not null) => videoReady,
            _ => audioReady || videoReady
        };
    }

    private void SetState(PlaybackState next)
    {
        var prev = _state;
        if (prev == next)
            return;

        _state = next;
        PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(prev, next));
    }

    private async Task RunBeforePlayHooks(CancellationToken ct)
    {
        foreach (var (beforePlay, _) in _lifecycleHooks)
            await beforePlay(this, ct).ConfigureAwait(false);
    }

    private async Task RunBeforeCloseHooks(CancellationToken ct)
    {
        foreach (var (_, beforeClose) in _lifecycleHooks)
        {
            if (beforeClose is null) continue;
            await beforeClose(this, ct).ConfigureAwait(false);
        }
    }

    private async Task WarmupDecodedBuffersAsync(CancellationToken ct)
    {
        var audio = AudioChannel;
        var video = VideoChannel;
        if (audio is null || video is null)
            return;

        // Keep warmup short and bounded: we only need enough decoded lead so the
        // first router ticks can feed both streams immediately.
        const int minAudioFrames = 480; // ~10 ms @ 48 kHz
        const int minVideoFrames = 1;
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);

        while (DateTime.UtcNow < timeoutAt && !ct.IsCancellationRequested)
        {
            if (audio.BufferAvailable >= minAudioFrames && video.BufferAvailable >= minVideoFrames)
                return;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }
}
