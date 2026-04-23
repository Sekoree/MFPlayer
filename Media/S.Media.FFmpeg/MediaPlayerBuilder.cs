using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

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
    private          AVRouterOptions?                             _routerOptions;
    private          FFmpegDecoderOptions?                        _decoderOptions;
    private          Action<PlaybackFailedEventArgs>?             _onError;
    private          Action<PlaybackStateChangedEventArgs>?       _onStateChanged;
    private          Action<PlaybackCompletedEventArgs>?          _onCompleted;

    internal MediaPlayerBuilder() { }

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

    /// <summary>Alias of <see cref="WithAudioOutput(IAudioEndpoint)"/> — matches the naming in the review's "Proposed simplified API" sketch.</summary>
    public MediaPlayerBuilder WithAudioSink(IAudioEndpoint endpoint) => WithAudioOutput(endpoint);

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

