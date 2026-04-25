using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.Playback;

/// <summary>
/// Fluent builder for <see cref="MediaPlayer"/> — closes review item §5.1 /
/// "Proposed simplified API". Collects endpoints, clock registrations,
/// decoder/router options and an optional error handler up-front, then
/// materialises a fully-wired <see cref="MediaPlayer"/> on <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// The builder only accepts <i>pre-built</i> endpoint instances. Device-based
/// overloads (e.g. <c>WithAudioOutput(AudioDeviceInfo)</c>) are tracked under
/// §5.2 and land once the one-step endpoint factories
/// (<c>PortAudioEndpoint.Create</c> etc.) are in place — that work lives in the
/// endpoint assemblies so the builder can stay free of concrete endpoint
/// dependencies.
/// </para>
/// <para><b>Typical usage:</b></para>
/// <code>
/// using var player = MediaPlayer.Create()
///     .WithAudioOutput(portAudioEndpoint)
///     .WithVideoOutput(sdl3Endpoint)
///     .WithDecoderOptions(new FFmpegDecoderOptions { PreferHardwareDecoding = true })
///     .OnError(e => logger.LogError(e.Exception, "Playback failed at {Stage}", e.Stage))
///     .Build();
///
/// await player.OpenAndPlayAsync(path);
/// await player.WaitForCompletionAsync();
/// </code>
/// </remarks>
public sealed class MediaPlayerBuilder
{
    // Capture the endpoint + which AV kinds it fulfils so Build can call the
    // correct AddEndpoint overload without a second runtime type-check.
    private readonly record struct PendingEndpoint(IMediaEndpoint Endpoint, bool Audio, bool Video);

    private readonly List<PendingEndpoint>                        _endpoints = [];
    private readonly List<(IMediaClock Clock, ClockPriority Pri)> _clocks    = [];
    private readonly List<(Func<MediaPlayer, CancellationToken, Task> BeforePlay, Func<MediaPlayer, CancellationToken, Task>? BeforeClose)> _lifecycleHooks = [];
    private          AVRouterOptions?                             _routerOptions;
    private          FFmpegDecoderOptions?                        _decoderOptions;
    private          Action<PlaybackFailedEventArgs>?             _onError;
    private          Action<PlaybackStateChangedEventArgs>?       _onStateChanged;
    private          Action<PlaybackCompletedEventArgs>?          _onCompleted;

    // Capture external inputs for registration during Build().
    private readonly record struct PendingInput(object Channel, bool IsAudio, AudioRouteOptions? AudioOptions, VideoRouteOptions? VideoOptions);

    private readonly List<PendingInput> _inputs = [];

    internal MediaPlayerBuilder() { }

    // ── Inputs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// §5.7 — Registers an external audio input (e.g. an <c>NDIAudioChannel</c>)
    /// that is not backed by an FFmpeg decoder. The input is registered with the
    /// router during <see cref="Build"/> and automatically routed to all
    /// audio-capable endpoints. Only one external audio input is supported via the
    /// builder — for multi-input scenarios, use <see cref="MediaPlayer.Router"/>
    /// directly.
    /// </summary>
    public MediaPlayerBuilder WithAudioInput(IAudioChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _inputs.Add(new PendingInput(channel, IsAudio: true, AudioOptions: null, VideoOptions: null));
        return this;
    }

    /// <summary>
    /// §5.7 — Registers an external video input (e.g. an <c>NDIVideoChannel</c>)
    /// that is not backed by an FFmpeg decoder. The input is registered with the
    /// router during <see cref="Build"/> and automatically routed to all
    /// video-capable endpoints. Only one external video input is supported via the
    /// builder — for multi-input scenarios, use <see cref="MediaPlayer.Router"/>
    /// directly.
    /// </summary>
    public MediaPlayerBuilder WithVideoInput(IVideoChannel channel)
        => WithVideoInput(channel, routeOptions: null);

    /// <summary>
    /// §5.7 — Registers an external video input with explicit route options
    /// (e.g. live-mode, overflow policy, subscription capacity) applied to the
    /// auto-created routes.
    /// </summary>
    public MediaPlayerBuilder WithVideoInput(IVideoChannel channel, VideoRouteOptions? routeOptions)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _inputs.Add(new PendingInput(channel, IsAudio: false, AudioOptions: null, VideoOptions: routeOptions));
        return this;
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a pre-built audio endpoint (output or sink). The builder does
    /// not distinguish between primary output and secondary sink — that's a
    /// concern of the <see cref="AVRouter"/>'s clock-selection logic, which
    /// picks the first clock-capable endpoint.
    /// </summary>
    public MediaPlayerBuilder WithAudioOutput(IAudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoints.Add(new PendingEndpoint(endpoint, Audio: true, Video: false));
        return this;
    }
    /// <summary>Registers a pre-built video endpoint.</summary>
    public MediaPlayerBuilder WithVideoOutput(IVideoEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoints.Add(new PendingEndpoint(endpoint, Audio: false, Video: true));
        return this;
    }

    /// <summary>Registers a pre-built audio+video endpoint (e.g. NDI sender).</summary>
    public MediaPlayerBuilder WithAVOutput(IAVEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoints.Add(new PendingEndpoint(endpoint, Audio: true, Video: true));
        return this;
    }

    // ── Clock ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers an additional clock with the router. Clock-capable endpoints
    /// (<see cref="IClockCapableEndpoint"/>) register themselves automatically,
    /// so this is only needed for standalone clocks (e.g. OSC-driven master
    /// clocks, an <c>NDIClock</c> instance that pre-exists the NDI endpoint).
    /// </summary>
    public MediaPlayerBuilder WithClock(IMediaClock clock, ClockPriority priority = ClockPriority.External)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clocks.Add((clock, priority));
        return this;
    }

    // ── Options ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets default <see cref="FFmpegDecoderOptions"/> used when
    /// <see cref="MediaPlayer.OpenAsync(string, FFmpegDecoderOptions?, CancellationToken)"/>
    /// is called with a <see langword="null"/> options argument.
    /// </summary>
    public MediaPlayerBuilder WithDecoderOptions(FFmpegDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _decoderOptions = options;
        return this;
    }

    /// <summary>Sets the <see cref="AVRouterOptions"/> forwarded to the internal <see cref="AVRouter"/>.</summary>
    public MediaPlayerBuilder WithRouterOptions(AVRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _routerOptions = options;
        return this;
    }

    /// <summary>
    /// §5.4 sugar — enables audio-preroll so the first video tick fires only
    /// after every audio input has at least <paramref name="minBufferedFrames"/>
    /// decoded, capped by <paramref name="deadline"/>. Replaces the hand-rolled
    /// warmup block every AV test app reimplements. When no video endpoint is
    /// registered or no audio input exists the router skips the wait regardless
    /// of this setting, so it's safe to call unconditionally from a
    /// reusable factory.
    /// </summary>
    /// <param name="minBufferedFrames">
    /// Minimum frames per input before release. Rule of thumb: target-sample-rate
    /// × 50 ms (e.g. 2400 at 48 kHz) gives a perceptually-gap-free start for
    /// most broadcast sources. Default: 2048.
    /// </param>
    /// <param name="deadline">
    /// Hard upper bound; if any input is still below the threshold when the
    /// deadline hits, the router starts anyway and logs a warning. Default: 1 s.
    /// </param>
    public MediaPlayerBuilder WithAutoPreroll(int minBufferedFrames = 2048, TimeSpan deadline = default)
    {
        if (minBufferedFrames < 0) throw new ArgumentOutOfRangeException(nameof(minBufferedFrames));
        if (deadline == default) deadline = TimeSpan.FromSeconds(1);
        var prev = _routerOptions ?? new AVRouterOptions();
        _routerOptions = prev with
        {
            MinBufferedFramesPerInput = minBufferedFrames,
            WaitForAudioPreroll       = deadline
        };
        return this;
    }

    /// <summary>
    /// §5.9 — Enables a background loop that periodically measures A/V drift via
    /// <see cref="IAVRouter.GetAvDrift"/> and nudges
    /// <see cref="IAVRouter.SetInputTimeOffset"/> to keep audio and video in sync.
    /// Only takes effect when both an audio and a video input are registered.
    /// </summary>
    public MediaPlayerBuilder WithAutoAvDriftCorrection(AvDriftCorrectionOptions? options = null)
    {
        _driftCorrectionOptions = options ?? new AvDriftCorrectionOptions();
        return this;
    }

    private AvDriftCorrectionOptions? _driftCorrectionOptions;

    /// <summary>
    /// Internal extension point used by endpoint/input packages (e.g. NDI) to
    /// orchestrate source-specific pre-play and pre-close work while keeping
    /// the core builder free of concrete backend dependencies.
    /// </summary>
    internal MediaPlayerBuilder WithLifecycleHook(
        Func<MediaPlayer, CancellationToken, Task> beforePlay,
        Func<MediaPlayer, CancellationToken, Task>? beforeClose = null)
    {
        ArgumentNullException.ThrowIfNull(beforePlay);
        _lifecycleHooks.Add((beforePlay, beforeClose));
        return this;
    }

    // ── Event wiring ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a handler for <see cref="MediaPlayer.PlaybackFailed"/>. Attaching
    /// through the builder guarantees the handler is in place <i>before</i>
    /// <see cref="MediaPlayer.OpenAsync(string, FFmpegDecoderOptions?, CancellationToken)"/>
    /// is called, so the first failure cannot slip past.
    /// </summary>
    public MediaPlayerBuilder OnError(Action<PlaybackFailedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onError += handler;
        return this;
    }

    /// <summary>Registers a handler for <see cref="MediaPlayer.PlaybackStateChanged"/>.</summary>
    public MediaPlayerBuilder OnStateChanged(Action<PlaybackStateChangedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onStateChanged += handler;
        return this;
    }

    /// <summary>Registers a handler for <see cref="MediaPlayer.PlaybackCompleted"/>.</summary>
    public MediaPlayerBuilder OnCompleted(Action<PlaybackCompletedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onCompleted += handler;
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Materialises the configured <see cref="MediaPlayer"/>. Endpoints are
    /// registered with the router in declaration order (matters for clock
    /// selection: the first <see cref="IClockCapableEndpoint"/> wins at
    /// <see cref="ClockPriority.Hardware"/> by default). If any step throws,
    /// all already-registered endpoints are removed so the builder leaves no
    /// partially-initialised <see cref="MediaPlayer"/> behind.
    /// </summary>
    public MediaPlayer Build()
    {
        var player = new MediaPlayer(_routerOptions)
        {
            DefaultDecoderOptions = _decoderOptions
        };

        try
        {
            if (_onError        is { } err) player.PlaybackFailed       += (_, e) => err(e);
            if (_onStateChanged is { } ch)  player.PlaybackStateChanged += (_, e) => ch(e);
            if (_onCompleted    is { } cp)  player.PlaybackCompleted    += (_, e) => cp(e);

            foreach (var (clock, pri) in _clocks)
                player.Router.RegisterClock(clock, pri);

            foreach (var pending in _endpoints)
            {
                switch (pending.Endpoint)
                {
                    case IAVEndpoint av:     player.AddEndpoint(av);    break;
                    case IAudioEndpoint ae:  player.AddEndpoint(ae);    break;
                    case IVideoEndpoint ve:  player.AddEndpoint(ve);    break;
                    default:
                        // Not reachable through the public With* methods (all
                        // of them constrain the input), but guard against a
                        // future overload forgetting to update the switch.
                        throw new InvalidOperationException(
                            $"Endpoint type {pending.Endpoint.GetType().Name} is not supported by the builder.");
                }
            }

            // §5.7 — register external inputs after endpoints so routes can be
            // auto-created to all already-registered endpoints.
            foreach (var input in _inputs)
            {
                if (input.IsAudio)
                    player.RegisterExternalAudioInput((IAudioChannel)input.Channel, input.AudioOptions);
                else
                    player.RegisterExternalVideoInput((IVideoChannel)input.Channel, input.VideoOptions);
            }

            // §5.9 — configured here, activated on PlayAsync when inputs are live.
            if (_driftCorrectionOptions is { } dco)
                player.ConfigureDriftCorrection(dco);

            foreach (var (beforePlay, beforeClose) in _lifecycleHooks)
                player.AddLifecycleHook(beforePlay, beforeClose);
        }
        catch
        {
            // Partial-initialisation unwind: DisposeAsync tears down whatever
            // was already registered. DisposeAsync is safe to call on an
            // unstarted player. Sync path — the caller is in Build() not in
            // an async method; best we can do without forcing the API async.
            player.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        return player;
    }
}
