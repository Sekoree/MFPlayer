using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;

namespace S.Media.Core.Routing;

/// <summary>
/// §5.10 — fluent builder for <see cref="AVRouter"/>. Advanced callers who
/// compose the router directly (no <c>MediaPlayer</c> facade) use this to
/// register every input / endpoint / clock / route up front and then call
/// <see cref="Build"/> to receive a ready-to-<c>StartAsync</c> router. The
/// builder is the intended entry point for custom pipelines that need
/// several decoders, a timeline, or a non-FFmpeg source.
///
/// <para><b>Typical usage:</b></para>
/// <code>
/// using var router = new RouterBuilder()
///     .WithOptions(new AVRouterOptions { InternalTickCadence = TimeSpan.FromMilliseconds(5) })
///     .AddAudioInput(audioChannel, out var audioIn)
///     .AddEndpoint(portAudio, out var paOut)
///     .AddRoute(audioIn, paOut)
///     .Build();
///
/// await router.StartAsync();
/// </code>
///
/// <para>
/// Every <c>AddX</c> method returns the builder so calls can chain. Opaque
/// ids (<see cref="InputId"/> / <see cref="EndpointId"/>) are handed back
/// via <see langword="out"/> parameters so later <c>AddRoute</c> calls can
/// reference them without a second lookup.
/// </para>
///
/// <para>
/// The builder itself performs no partial-state work — the underlying
/// <see cref="AVRouter"/> is constructed in <see cref="Build"/> and
/// registrations happen there in the recorded order. A failure in any
/// registration disposes the half-wired router and rethrows. This closes
/// review item R8 by construction: the router is never observable in a
/// half-initialised state, because it doesn't exist until every step has
/// succeeded.
/// </para>
/// </summary>
public sealed class RouterBuilder
{
    private AVRouterOptions? _options;

    // Recorded steps. Executed in order inside Build.
    private readonly List<Action<AVRouter>> _steps = new();

    // Tracker ids — auto-incremented and mapped to real ids on Build. This lets
    // the user reference an input/endpoint before the real router exists.
    private int _nextInputToken;
    private int _nextEndpointToken;
    private readonly Dictionary<int, InputId> _inputTokenMap = new();
    private readonly Dictionary<int, EndpointId> _endpointTokenMap = new();

    /// <summary>
    /// Sets <see cref="AVRouter"/> options. Overrides any previously-set
    /// options. Default: <c>new AVRouterOptions()</c>.
    /// </summary>
    public RouterBuilder WithOptions(AVRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        return this;
    }

    /// <summary>
    /// Registers an audio input. The opaque <paramref name="inputToken"/>
    /// identifies this input for later <see cref="AddRoute(int, int)"/>
    /// calls; it is translated to a real <see cref="InputId"/> inside
    /// <see cref="Build"/>.
    /// </summary>
    public RouterBuilder AddAudioInput(IAudioChannel channel, out int inputToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        int token = _nextInputToken++;
        inputToken = token;
        _steps.Add(r => _inputTokenMap[token] = r.RegisterAudioInput(channel));
        return this;
    }

    /// <inheritdoc cref="AddAudioInput"/>
    public RouterBuilder AddVideoInput(IVideoChannel channel, out int inputToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        int token = _nextInputToken++;
        inputToken = token;
        _steps.Add(r => _inputTokenMap[token] = r.RegisterVideoInput(channel));
        return this;
    }

    /// <summary>Registers an audio endpoint.</summary>
    public RouterBuilder AddEndpoint(IAudioEndpoint endpoint, out int endpointToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        int token = _nextEndpointToken++;
        endpointToken = token;
        _steps.Add(r => _endpointTokenMap[token] = r.RegisterEndpoint(endpoint));
        return this;
    }

    /// <summary>Registers a video endpoint.</summary>
    public RouterBuilder AddEndpoint(IVideoEndpoint endpoint, out int endpointToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        int token = _nextEndpointToken++;
        endpointToken = token;
        _steps.Add(r => _endpointTokenMap[token] = r.RegisterEndpoint(endpoint));
        return this;
    }

    /// <summary>Registers a combined audio-and-video endpoint.</summary>
    public RouterBuilder AddEndpoint(IAVEndpoint endpoint, out int endpointToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        int token = _nextEndpointToken++;
        endpointToken = token;
        _steps.Add(r => _endpointTokenMap[token] = r.RegisterEndpoint(endpoint));
        return this;
    }

    /// <summary>
    /// Creates a route from a previously-added input to a previously-added
    /// endpoint. Defaults to router auto-derived options; use
    /// <see cref="AddRoute(int, int, IRouteOptions)"/> for explicit options.
    /// </summary>
    public RouterBuilder AddRoute(int inputToken, int endpointToken)
    {
        _steps.Add(r => r.CreateRoute(_inputTokenMap[inputToken], _endpointTokenMap[endpointToken]));
        return this;
    }

    /// <summary>Creates a route with explicit audio/video options.</summary>
    public RouterBuilder AddRoute(int inputToken, int endpointToken, IRouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _steps.Add(r =>
        {
            InputId input = _inputTokenMap[inputToken];
            EndpointId ep = _endpointTokenMap[endpointToken];
            _ = options switch
            {
                AudioRouteOptions a => r.CreateRoute(input, ep, a),
                VideoRouteOptions v => r.CreateRoute(input, ep, v),
                _ => throw new ArgumentException(
                    $"Unsupported IRouteOptions implementation: {options.GetType().FullName}",
                    nameof(options)),
            };
        });
        return this;
    }

    /// <summary>Registers a clock at the given priority.</summary>
    public RouterBuilder AddClock(IMediaClock clock, ClockPriority priority = ClockPriority.Hardware)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _steps.Add(r => r.RegisterClock(clock, priority));
        return this;
    }

    /// <summary>
    /// Materialises the router and applies every recorded step in order. If
    /// any step throws, the half-initialised router is disposed and the
    /// exception is rethrown — callers never see a partially-wired router.
    /// </summary>
    public AVRouter Build()
    {
        var router = new AVRouter(_options);
        try
        {
            foreach (var step in _steps)
                step(router);
            return router;
        }
        catch
        {
            router.Dispose();
            throw;
        }
    }
}