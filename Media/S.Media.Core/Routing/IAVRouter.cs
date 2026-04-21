using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;

namespace S.Media.Core.Routing;

/// <summary>
/// User-facing media routing graph. Replaces <c>IAVMixer</c>.
/// Registers inputs (channels) and endpoints, creates routes between them,
/// and forwards audio/video data. Does not own a base sample rate or frame rate.
/// </summary>
public interface IAVRouter : IAsyncDisposable, IDisposable
{
    // ── Lifecycle ──────────────────────────────────────────────────────

    /// <summary>Whether the router is currently running (clock ticking, data flowing).</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the router's internal clock and push-endpoint tick loop.
    /// Registering endpoints/inputs/routes while stopped is allowed (configuration phase).
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the tick loop and the internal clock.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    // ── Input (channel) management ─────────────────────────────────────

    /// <summary>Registers an audio channel as an input source.</summary>
    InputId RegisterAudioInput(IAudioChannel channel);

    /// <summary>Registers a video channel as an input source.</summary>
    InputId RegisterVideoInput(IVideoChannel channel);

    /// <summary>Removes a previously registered input. All routes from this input are also removed.</summary>
    void UnregisterInput(InputId id);

    // ── Endpoint management ────────────────────────────────────────────

    /// <summary>Registers an audio-only endpoint.</summary>
    EndpointId RegisterEndpoint(IAudioEndpoint endpoint);

    /// <summary>Registers a video-only endpoint.</summary>
    EndpointId RegisterEndpoint(IVideoEndpoint endpoint);

    /// <summary>
    /// Registers a dual-media endpoint (e.g. NDIAVSink).
    /// A single registration handles both audio and video routing.
    /// </summary>
    EndpointId RegisterEndpoint(IAVEndpoint endpoint);

    /// <summary>Removes a previously registered endpoint. All routes to this endpoint are also removed.</summary>
    void UnregisterEndpoint(EndpointId id);

    // ── Routing ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a route from an input to an endpoint with default options.
    /// For audio: auto-derives channel map and auto-creates resampler if needed.
    /// For video: replaces any existing video route to the same endpoint (last-write-wins).
    /// </summary>
    RouteId CreateRoute(InputId input, EndpointId endpoint);

    /// <summary>Creates an audio route with explicit options.</summary>
    RouteId CreateRoute(InputId input, EndpointId endpoint, AudioRouteOptions options);

    /// <summary>Creates a video route with explicit options.</summary>
    RouteId CreateRoute(InputId input, EndpointId endpoint, VideoRouteOptions options);

    /// <summary>
    /// Creates a route by pattern-matching on the concrete <see cref="IRouteOptions"/>
    /// implementation (<see cref="AudioRouteOptions"/> or <see cref="VideoRouteOptions"/>).
    /// Useful for generic code that builds the options object polymorphically.
    /// </summary>
    RouteId CreateRoute(InputId input, EndpointId endpoint, IRouteOptions options)
        => options switch
        {
            AudioRouteOptions a => CreateRoute(input, endpoint, a),
            VideoRouteOptions v => CreateRoute(input, endpoint, v),
            _ => throw new ArgumentException(
                $"Unsupported route options type: {options.GetType().FullName}", nameof(options)),
        };

    /// <summary>Removes a route.</summary>
    void RemoveRoute(RouteId id);

    /// <summary>Enables or disables a route without removing it.</summary>
    void SetRouteEnabled(RouteId id, bool enabled);

    // ── Clock ──────────────────────────────────────────────────────────

    /// <summary>The router's own built-in software clock. Always available as ultimate fallback.</summary>
    IMediaClock InternalClock { get; }

    /// <summary>
    /// The effective clock used for PTS scheduling and push-endpoint delivery.
    /// Returns the highest-priority registered clock, or <see cref="InternalClock"/> if none.
    /// </summary>
    IMediaClock Clock { get; }

    /// <summary>
    /// Registers a clock at the given priority tier. The router automatically selects
    /// the highest-priority clock. Multiple clocks can be registered; if the active one
    /// is unregistered, the router falls back to the next tier.
    /// The router only reads <see cref="IMediaClock.Position"/> and
    /// <see cref="IMediaClock.IsRunning"/> — it never calls Start/Stop/Reset
    /// on an external clock.
    /// </summary>
    void RegisterClock(IMediaClock clock, ClockPriority priority = ClockPriority.Hardware);

    /// <summary>
    /// Removes a previously registered clock. If it was the active clock, the router
    /// falls back to the next-highest priority clock (or internal).
    /// </summary>
    void UnregisterClock(IMediaClock clock);

    /// <summary>
    /// Convenience: sets a single clock at <see cref="ClockPriority.Override"/> priority,
    /// removing any previously override-registered clock.
    /// Pass <see langword="null"/> to remove the override and fall back to priority selection.
    /// </summary>
    void SetClock(IMediaClock? clock);

    // ── Per-input control ──────────────────────────────────────────────

    /// <summary>Sets the volume for an audio input. No-op for video inputs.</summary>
    void SetInputVolume(InputId id, float volume);

    /// <summary>Sets a time offset for an input (audio or video).</summary>
    void SetInputTimeOffset(InputId id, TimeSpan offset);

    /// <summary>Enables or disables an input without unregistering it.</summary>
    void SetInputEnabled(InputId id, bool enabled);

    // ── Per-endpoint control ───────────────────────────────────────────

    /// <summary>Master gain per endpoint, applied after accumulation. Default 1.0.</summary>
    void SetEndpointGain(EndpointId id, float gain);

    // ── Video-specific ─────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/>, video presentation bypasses PTS-based scheduling and
    /// always presents the newest frame. Suitable for live NDI monitoring.
    /// </summary>
    bool VideoLiveMode { get; set; }

    // ── Diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the instantaneous A/V drift between two inputs.
    /// Positive means audio is ahead of video.
    /// </summary>
    TimeSpan GetAvDrift(InputId audioInput, InputId videoInput);

    /// <summary>
    /// Returns the most recent peak sample level (absolute, 0.0–1.0+) for an audio input,
    /// measured post-volume/pre-mix. Returns 0 for video inputs.
    /// </summary>
    float GetInputPeakLevel(InputId id);

    /// <summary>
    /// Returns a point-in-time diagnostic snapshot of the router's full state:
    /// inputs, endpoints, routes, clock position, and per-input peak levels.
    /// </summary>
    RouterDiagnosticsSnapshot GetDiagnosticsSnapshot();
}

