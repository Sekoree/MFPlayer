using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

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
    private readonly AVRouter _router;
    private readonly List<IMediaEndpoint> _endpoints = [];
    private readonly List<EndpointId> _endpointIds = [];

    private FFmpegDecoder? _decoder;
    private float           _volume         = 1.0f;
    private bool            _decoderStarted;
    private bool            _disposed;
    private PlaybackState   _state = PlaybackState.Idle;

    // Track registered input/route ids for current session
    private InputId? _audioInputId;
    private InputId? _videoInputId;
    private readonly List<RouteId> _routeIds = [];

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
    }

    /// <summary>
    /// Starts a fluent <see cref="MediaPlayerBuilder"/> so endpoints, clock,
    /// decoder/router options and an error handler can be declared up-front,
    /// then the fully-wired <see cref="MediaPlayer"/> materialises on
    /// <see cref="MediaPlayerBuilder.Build"/>. Closes review item §5.1.
    /// </summary>
    public static MediaPlayerBuilder Create() => new();

    /// <summary>
    /// Default <see cref="FFmpegDecoderOptions"/> applied when
    /// <see cref="OpenAsync(string, FFmpegDecoderOptions?, CancellationToken)"/>
    /// is called with a <see langword="null"/> options argument. Set by
    /// <see cref="MediaPlayerBuilder.WithDecoderOptions"/>; <see langword="null"/>
    /// (default) falls back to <see cref="FFmpegDecoder"/>'s built-in defaults.
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
    public bool IsPlaying => _decoderStarted && !_disposed;

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
    /// Registers an audio endpoint. If the endpoint is <see cref="IClockCapableEndpoint"/>,
    /// the router's clock is automatically overridden to use it.
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
    }

    /// <summary>
    /// Async counterpart to <see cref="AddEndpoint(IAudioEndpoint)"/> — closes review
    /// item §4.4 / B19. Registers the endpoint with the router and, if the player
    /// is already running, starts the endpoint cooperatively via
    /// <see cref="IMediaEndpoint.StartAsync"/> instead of the legacy sync
    /// <c>GetAwaiter().GetResult()</c> that could deadlock single-threaded sync
    /// contexts.
    /// </summary>
    public Task AddEndpointAsync(IAudioEndpoint endpoint, CancellationToken ct = default)
        => RegisterEndpointAndMaybeStartAsync(endpoint, _router.RegisterEndpoint(endpoint),
            audio: true, video: false, ct);

    /// <inheritdoc cref="AddEndpointAsync(IAudioEndpoint, CancellationToken)"/>
    public Task AddEndpointAsync(IVideoEndpoint endpoint, CancellationToken ct = default)
        => RegisterEndpointAndMaybeStartAsync(endpoint, _router.RegisterEndpoint(endpoint),
            audio: false, video: true, ct);

    /// <inheritdoc cref="AddEndpointAsync(IAudioEndpoint, CancellationToken)"/>
    public Task AddEndpointAsync(IAVEndpoint endpoint, CancellationToken ct = default)
        => RegisterEndpointAndMaybeStartAsync(endpoint, _router.RegisterEndpoint(endpoint),
            audio: true, video: true, ct);

    /// <summary>
    /// Async counterpart to <see cref="RemoveEndpoint"/>. Stops the endpoint
    /// cooperatively if the player is active, then unregisters it from the router.
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
    }

    // ── Open ──────────────────────────────────────────────────────────────────

    /// <summary>Opens the media file and prepares the pipeline.</summary>
    public async Task OpenAsync(
        string                path,
        FFmpegDecoderOptions? options = null,
        CancellationToken     ct     = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Opening);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.ReplacedByOpen).ConfigureAwait(false);
            AttachDecoder(FFmpegDecoder.Open(path, options ?? DefaultDecoderOptions));
            SetState(PlaybackState.Ready);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Open, ex));
            throw;
        }
    }

    /// <summary>Opens the media from a stream and prepares the pipeline.</summary>
    public async Task OpenAsync(
        Stream                stream,
        FFmpegDecoderOptions? options   = null,
        bool                  leaveOpen = false,
        CancellationToken     ct        = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Opening);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.ReplacedByOpen).ConfigureAwait(false);
            AttachDecoder(FFmpegDecoder.Open(stream, options ?? DefaultDecoderOptions, leaveOpen));
            SetState(PlaybackState.Ready);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
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
            if (_decoder == null)
                throw new MediaException("No media is open. Call OpenAsync first.");

            if (!_decoderStarted)
            {
                _decoder.Start();
                _decoderStarted = true;
            }

            // Start all registered endpoints
            foreach (var ep in _endpoints)
                await ep.StartAsync(ct).ConfigureAwait(false);

            await _router.StartAsync(ct).ConfigureAwait(false);

            SetState(PlaybackState.Playing);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Play, ex));
            throw;
        }
    }

    /// <summary>Pauses playback.</summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        try
        {
            await _router.StopAsync(ct).ConfigureAwait(false);
            foreach (var ep in _endpoints)
                await ep.StopAsync(ct).ConfigureAwait(false);
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
    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Stopping);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.StoppedByUser).ConfigureAwait(false);
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

    /// <summary>Opens and immediately starts playback.</summary>
    public async Task OpenAndPlayAsync(
        string                path,
        FFmpegDecoderOptions? options = null,
        CancellationToken     ct      = default)
    {
        await OpenAsync(path, options, ct).ConfigureAwait(false);
        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Opens from stream and immediately starts playback.</summary>
    public async Task OpenAndPlayAsync(
        Stream                stream,
        FFmpegDecoderOptions? options   = null,
        bool                  leaveOpen = false,
        CancellationToken     ct        = default)
    {
        await OpenAsync(stream, options, leaveOpen, ct).ConfigureAwait(false);
        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits natural end-of-media (source EOF) or an unrecoverable playback
    /// failure, whichever comes first. Returns the
    /// <see cref="PlaybackCompletedReason"/> that ended the session.
    ///
    /// <para>
    /// Intended to replace the hand-rolled <c>CancellationTokenSource</c> + EOF /
    /// drain bookkeeping every test app currently reimplements. Honours
    /// <paramref name="ct"/>; if cancellation fires before completion, the
    /// player is left running (caller decides whether to <see cref="StopAsync"/>).
    /// Closes review finding §4.3.
    /// </para>
    /// </summary>
    /// <param name="drainGrace">
    /// Extra wait after <see cref="PlaybackCompletedReason.SourceEnded"/> to let
    /// the tail of already-buffered audio reach the hardware. Default 300 ms.
    /// </param>
    public async Task<PlaybackCompletedReason> WaitForCompletionAsync(
        TimeSpan          drainGrace = default,
        CancellationToken ct         = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (drainGrace == default) drainGrace = TimeSpan.FromMilliseconds(300);

        var tcs = new TaskCompletionSource<PlaybackCompletedReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? s, PlaybackCompletedEventArgs e) => tcs.TrySetResult(e.Reason);
        void OnFailed(object? s, PlaybackFailedEventArgs e)       => tcs.TrySetException(e.Exception);

        PlaybackCompleted += OnCompleted;
        PlaybackFailed    += OnFailed;
        try
        {
            using var reg = ct.Register(static state =>
                ((TaskCompletionSource<PlaybackCompletedReason>)state!).TrySetCanceled(), tcs);

            var reason = await tcs.Task.ConfigureAwait(false);

            // Apply drain grace only on natural EOF so the hardware can finish
            // playing what's already been pushed through the router.
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
            PlaybackFailed    -= OnFailed;
        }
    }

    // ── IDisposable / IAsyncDisposable ────────────────────────────────────────

    /// <summary>
    /// Synchronous disposal. Delegates to <see cref="DisposeAsync"/>; prefer the
    /// async variant in async call-paths (closes review item §4.4, B19).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Cooperatively stops the router + every registered endpoint + the
    /// decoder, then disposes the underlying <see cref="AVRouter"/>.
    /// Implements review items §4.4 (B19) + §10.3 — the sequence is:
    /// <list type="number">
    ///   <item>Stop the router so no new push ticks fire at endpoints in
    ///         teardown.</item>
    ///   <item>Stop every registered endpoint <b>in parallel</b>
    ///         (<see cref="Task.WhenAll(Task[])"/>) — endpoint
    ///         <c>StopAsync</c>s are independent (they own their own
    ///         render/capture threads) so serialising them would add
    ///         0–N×(slowest stop) to tear-down for no gain.</item>
    ///   <item>Stop the decoder so demux/decode threads join cooperatively.</item>
    ///   <item>Release the session and dispose the router.</item>
    /// </list>
    /// Every step is best-effort: a faulty endpoint StopAsync cannot block
    /// the subsequent steps from running. Endpoints are owned by the
    /// caller (see <see cref="AddEndpoint(IAudioEndpoint)"/>) — we stop
    /// them but do not dispose them.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsActive)
        {
            try { await _router.StopAsync().ConfigureAwait(false); } catch { /* best-effort */ }

            // §10.3 — parallel endpoint stop. Each StopAsync gets its own
            // try/catch wrapper task so one faulty endpoint cannot short-
            // circuit the WhenAll; all results are discarded.
            if (_endpoints.Count > 0)
            {
                var stops = new Task[_endpoints.Count];
                for (int i = 0; i < _endpoints.Count; i++)
                {
                    var ep = _endpoints[i];
                    stops[i] = Task.Run(async () =>
                    {
                        try { await ep.StopAsync().ConfigureAwait(false); } catch { /* best-effort */ }
                    });
                }
                try { await Task.WhenAll(stops).ConfigureAwait(false); }
                catch { /* swallowed — per-endpoint try already caught */ }
            }
        }

        if (_decoder is { } dec)
        {
            try { await dec.StopAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }

        ReleaseSession();
        _router.Dispose();
        SetState(PlaybackState.Stopped);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the player is currently running (endpoints/router have been
    /// started and not yet stopped). Single source of truth derived from
    /// <see cref="_state"/> — closes the §4.11 <c>_isRunning</c> duplication
    /// noted in the review.
    /// </summary>
    private bool IsActive => _state is PlaybackState.Playing or PlaybackState.Paused;

    private void RegisterEndpointAndMaybeStart(IMediaEndpoint endpoint, EndpointId id, bool audio, bool video)
    {
        _endpoints.Add(endpoint);
        _endpointIds.Add(id);
        AutoRouteToEndpoint(id, audio, video);

        if (IsActive)
            endpoint.StartAsync().GetAwaiter().GetResult();
    }

    private async Task RegisterEndpointAndMaybeStartAsync(IMediaEndpoint endpoint, EndpointId id, bool audio, bool video, CancellationToken ct)
    {
        _endpoints.Add(endpoint);
        _endpointIds.Add(id);
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
            // §3.56 — publish volume via the router input (new world) rather than
            // via the channel-level setter which is now [Obsolete].
            _router.SetInputVolume(inputId, _volume);

            // Create routes to all audio-capable endpoints
            for (int i = 0; i < _endpoints.Count; i++)
            {
                if (_endpoints[i] is IAudioEndpoint)
                    _routeIds.Add(_router.CreateRoute(inputId, _endpointIds[i]));
            }
        }

        if (decoder.FirstVideoChannel is { } videoCh)
        {
            var inputId = _router.RegisterVideoInput(videoCh);
            _videoInputId = inputId;

            // Create routes to all video-capable endpoints
            for (int i = 0; i < _endpoints.Count; i++)
            {
                if (_endpoints[i] is IVideoEndpoint)
                    _routeIds.Add(_router.CreateRoute(inputId, _endpointIds[i]));
            }
        }

        decoder.EndOfMedia += OnEndOfMedia;
        _decoder        = decoder;
        _decoderStarted = false;
    }

    // Clock auto-registration is now handled by AVRouter.RegisterEndpoint
    // when the endpoint implements IClockCapableEndpoint.

    private void AutoRouteToEndpoint(EndpointId epId, bool audio, bool video)
    {
        // If there's already an open session, auto-route existing inputs
        if (audio && _audioInputId.HasValue)
            _routeIds.Add(_router.CreateRoute(_audioInputId.Value, epId));
        if (video && _videoInputId.HasValue)
            _routeIds.Add(_router.CreateRoute(_videoInputId.Value, epId));
    }

    private async Task CloseAsync(CancellationToken ct, PlaybackCompletedReason? closeReason = null)
    {
        bool hadSession = _decoder != null || _decoderStarted;
        if (_decoderStarted)
        {
            try { await _router.StopAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
            foreach (var ep in _endpoints)
            {
                try { await ep.StopAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
            }
        }
        ReleaseSession();
        if (hadSession && closeReason.HasValue)
            PlaybackCompleted?.Invoke(this, new PlaybackCompletedEventArgs(closeReason.Value));
    }

    private void ReleaseSession()
    {
        // Remove routes and inputs from router
        foreach (var routeId in _routeIds)
            _router.RemoveRoute(routeId);
        _routeIds.Clear();

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
        _decoder        = null;
        _decoderStarted = false;
    }

    private void OnEndOfMedia(object? sender, EventArgs e)
    {
        if (IsLooping && _decoder != null && !_disposed)
        {
            _decoder.Seek(TimeSpan.Zero);
            return;
        }

        PlaybackCompleted?.Invoke(this, new PlaybackCompletedEventArgs(PlaybackCompletedReason.SourceEnded));
    }

    private void SetState(PlaybackState next)
    {
        var prev = _state;
        if (prev == next)
            return;

        _state = next;
        PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(prev, next));
    }
}
