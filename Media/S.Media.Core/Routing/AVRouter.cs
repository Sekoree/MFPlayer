using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Mixing;
using S.Media.Core.Video;

namespace S.Media.Core.Routing;

/// <summary>
/// Concrete implementation of <see cref="IAVRouter"/>.
/// Thin routing graph: registers inputs (channels) and endpoints, creates routes
/// between them, and forwards audio/video data. Does not own a base sample rate
/// or frame rate — those are per-endpoint concerns.
/// </summary>
public sealed class AVRouter : IAVRouter
{
    private static readonly ILogger Log = MediaCoreLogging.GetLogger(nameof(AVRouter));

    private readonly AVRouterOptions _options;
    private readonly Lock _lock = new();

    // ── Internal state ──────────────────────────────────────────────────

    private enum InputKind { Audio, Video }

    private sealed class InputEntry
    {
        public readonly InputId Id;
        public readonly InputKind Kind;
        public readonly IAudioChannel? AudioChannel;
        public readonly IVideoChannel? VideoChannel;
        public float Volume = 1.0f;
        // §3.18 / B13: long-backed for Interlocked atomicity; TimeOffset is a wrapper.
        public long TimeOffsetTicks;
        public TimeSpan TimeOffset
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref TimeOffsetTicks));
            set => Interlocked.Exchange(ref TimeOffsetTicks, value.Ticks);
        }
        public bool Enabled = true;

        /// <summary>Peak sample level (absolute) measured after volume, before routing. Updated on RT thread.</summary>
        public float PeakLevel;


        public InputEntry(InputId id, IAudioChannel ch)
        {
            Id = id; Kind = InputKind.Audio; AudioChannel = ch;
        }

        public InputEntry(InputId id, IVideoChannel ch)
        {
            Id = id; Kind = InputKind.Video; VideoChannel = ch;
        }
    }

    private enum EndpointKind { Audio, Video, AV }

    private sealed class EndpointEntry
    {
        public readonly EndpointId Id;
        public readonly EndpointKind Kind;
        public readonly IAudioEndpoint? Audio;
        public readonly IVideoEndpoint? Video;
        public float Gain = 1.0f;

        // For pull audio endpoints: the fill callback we install
        public AudioFillCallbackForEndpoint? AudioFillCb;

        public EndpointEntry(EndpointId id, IAudioEndpoint ep)
        {
            Id = id; Kind = EndpointKind.Audio; Audio = ep;
        }

        public EndpointEntry(EndpointId id, IVideoEndpoint ep)
        {
            Id = id; Kind = EndpointKind.Video; Video = ep;
        }

        public EndpointEntry(EndpointId id, IAVEndpoint ep)
        {
            Id = id; Kind = EndpointKind.AV; Audio = ep; Video = ep;
        }
    }

    private sealed class RouteEntry
    {
        public readonly RouteId Id;
        public readonly InputId InputId;
        public readonly EndpointId EndpointId;
        public readonly InputKind Kind; // audio or video
        public bool Enabled = true;

        // Audio-specific
        public ChannelRouteMap? ChannelMap;
        public (int dstCh, float gain)[][]? BakedChannelMap; // pre-computed from ChannelMap
        public float Gain = 1.0f;
        public TimeSpan TimeOffset;
        public IAudioResampler? Resampler;
        public bool OwnsResampler; // if we auto-created the resampler

        // Video-specific: per-route subscription into the input channel's frame stream.
        // Each (input, endpoint) pair owns its own bounded queue — the decoder fans
        // each frame out to every subscription, so pull (SDL3/Avalonia) and push
        // (NDI, clone sinks) no longer race for frames on a shared ring.
        public IVideoSubscription? VideoSub;

        public RouteEntry(RouteId id, InputId inputId, EndpointId endpointId, InputKind kind)
        {
            Id = id; InputId = inputId; EndpointId = endpointId; Kind = kind;
        }
    }

    // Dictionaries for O(1) lookup. Mutated under _lock.
    private readonly ConcurrentDictionary<InputId, InputEntry> _inputs = new();
    private readonly ConcurrentDictionary<EndpointId, EndpointEntry> _endpoints = new();
    private readonly ConcurrentDictionary<RouteId, RouteEntry> _routes = new();

    // §3.16 / R7: COW snapshot of endpoint entries rebuilt under _lock whenever
    // Register/Unregister mutates _endpoints. The push-tick enumerates this array
    // instead of .Values so it cannot observe a half-initialised endpoint during
    // the SetupPull* → _endpoints[id]=entry → AutoRegisterEndpointClock window
    // (which, before §3.17, ran outside the lock).
    private volatile EndpointEntry[] _endpointsSnapshot = [];

    // Snapshot arrays for lock-free RT iteration (copy-on-write pattern)
    private volatile RouteEntry[] _audioRouteSnapshot = [];
    private volatile RouteEntry[] _videoRouteSnapshot = [];

    // Per-endpoint route lookup caches — rebuilt under _lock whenever a route
    // or endpoint is added/removed so push-tick hot paths can grab the per-endpoint
    // route list in O(1) instead of rescanning the full snapshot each endpoint
    // (previously O(E·R) per tick; now O(E + R) total) — §4.2 of Code-Review-Findings.
    private volatile Dictionary<EndpointId, RouteEntry[]> _audioRoutesByEndpoint =
        new();
    private volatile Dictionary<EndpointId, RouteEntry[]> _videoRoutesByEndpoint =
        new();

    // ── Clock ───────────────────────────────────────────────────────────

    private readonly StopwatchClock _internalClock;
    private readonly Lock _clockLock = new();
    private readonly List<(IMediaClock Clock, ClockPriority Priority, long Order)> _clockRegistry = [];
    private long _clockRegistrationOrder;
    private volatile IMediaClock? _resolvedClock;
    private Thread? _pushThread;
    // §3.19 / R19+R20: cancellation token for the push threads so StopAsync can
    // unblock an in-flight WaitUntil spin/sleep immediately instead of waiting
    // for the 5 ms cadence tick to notice _running==false. Rebuilt per Start so
    // a second Start after Stop gets a fresh token.
    private CancellationTokenSource? _pushCts;
    private volatile bool _running;
    private volatile bool _disposed;

    // ── Scratch buffers (lazy, per-endpoint) ────────────────────────────

    private readonly ConcurrentDictionary<EndpointId, float[]> _scratchBuffers = new();

    // Per-endpoint fractional frame accumulator for time-aware push audio.
    // Prevents truncation drift when computing frame counts from elapsed seconds.
    private readonly ConcurrentDictionary<EndpointId, double> _pushAudioFrameAccumulators = new();

    // Per-route pending video frame for push endpoints.
    // When a frame is pulled from the subscription but its PTS is ahead of the clock,
    // it is cached here instead of being dropped.  The next push tick retries it.
    // Keyed by RouteId (not InputId) so N push endpoints on the same input each own
    // their own pending — otherwise they would clobber each other's gate-cache.
    private readonly ConcurrentDictionary<RouteId, VideoFrame> _pushVideoPending = new();

    // Per-route push video drift correction origin.
    // Tracks PTS↔clock offset and applies smooth proportional correction so
    // sub-frame drift converges to zero without ±½-frame oscillation.
    // See PtsDriftTracker for the shared state machine (also used by the pull
    // callback below) — §5.1 of Code-Review-Findings.
    private readonly ConcurrentDictionary<RouteId, PtsDriftTracker> _pushVideoDrift = new();
    private readonly ConcurrentDictionary<RouteId, byte> _pushAudioFormatMismatchWarnings = new();

    // §3.20 / EL3: rate-limit repeated push-tick exceptions. Log the first three,
    // then every 100th, so a persistent fault doesn't flood the log while still
    // producing periodic breadcrumbs. Counters are per-tick-loop (one per thread),
    // touched only from the owning loop, so no synchronisation required.
    private long _pushAudioErrorCount;
    private long _pushVideoErrorCount;

    // Reusable 1-element scratch for PushVideoTick's IVideoChannel.FillBuffer calls.
    // Only the push-video thread touches this, so no synchronization is required.
    // (Kept only for legacy audio scratch parity; video fan-out now reads subs directly.)

    // ── Constructor ─────────────────────────────────────────────────────

    public AVRouter(AVRouterOptions? options = null)
    {
        _options = options ?? new AVRouterOptions();
        _internalClock = new StopwatchClock(_options.InternalTickCadence);
    }

    // ── IAVRouter: Lifecycle ────────────────────────────────────────────

    public bool IsRunning => _running;

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_running) return Task.CompletedTask;
            _running = true;
            _pushCts = new CancellationTokenSource();

            _internalClock.Start();

            // Start a dedicated high-resolution push thread. The threads read
            // _pushCts.Token via a captured copy so StopAsync's cancellation is
            // observed even if the field is replaced by a subsequent Start.
            var token = _pushCts.Token;
            _pushThread = new Thread(() => PushThreadLoop(token))
            {
                Name = "AVRouter-PushTick",
                IsBackground = true,
            };
            _pushThread.Start();

            Log.LogInformation("AVRouter started (tick cadence={Cadence}ms)",
                _options.InternalTickCadence.TotalMilliseconds);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Thread? threadToJoin;
        CancellationTokenSource? ctsToDispose;
        lock (_lock)
        {
            if (!_running) return Task.CompletedTask;
            _running = false;

            // §3.19: cancel before releasing the lock so WaitUntil wakes
            // immediately and the Join below completes in tens of
            // microseconds rather than the 2-second safety timeout.
            ctsToDispose = _pushCts;
            _pushCts = null;
            threadToJoin = _pushThread;
            _pushThread = null;
        }

        try { ctsToDispose?.Cancel(); } catch (ObjectDisposedException) { /* raced */ }
        // Join outside the router lock so the push threads' own reads of
        // _inputs / _endpoints (which do not lock) never fight for _lock
        // against a stopping caller.
        threadToJoin?.Join(timeout: TimeSpan.FromSeconds(2));
        ctsToDispose?.Dispose();

        lock (_lock)
        {
            _internalClock.Stop();

            Log.LogInformation("AVRouter stopped");
        }

        return Task.CompletedTask;
    }

    // ── IAVRouter: Input management ─────────────────────────────────────

    public InputId RegisterAudioInput(IAudioChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_lock)
        {
            var id = InputId.New();
            var entry = new InputEntry(id, channel);
            _inputs[id] = entry;
            Log.LogDebug("Audio input registered: {Id} ({Format})", id, channel.SourceFormat);
            return id;
        }
    }

    public InputId RegisterVideoInput(IVideoChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_lock)
        {
            var id = InputId.New();
            var entry = new InputEntry(id, channel);
            _inputs[id] = entry;
            Log.LogDebug("Video input registered: {Id}", id);
            return id;
        }
    }

    public void UnregisterInput(InputId id)
    {
        lock (_lock)
        {
            if (!_inputs.TryRemove(id, out _)) return;

            // Remove all routes from this input (RemoveRouteInternal disposes each
            // route's subscription and releases per-route push-video bookkeeping).
            var toRemove = _routes.Values.Where(r => r.InputId == id).ToList();
            foreach (var route in toRemove)
                RemoveRouteInternal(route);


            Log.LogDebug("Input unregistered: {Id}", id);
        }
    }

    // ── IAVRouter: Endpoint management ──────────────────────────────────

    public EndpointId RegisterEndpoint(IAudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        // §3.17 / R8: run the whole Setup → _endpoints[id]=entry → snapshot rebuild →
        // clock auto-register sequence under _lock so concurrent push ticks never
        // observe a half-initialised endpoint (entry visible but FillCallback not
        // yet attached, or clock not yet registered).
        lock (_lock)
        {
            var id = EndpointId.New();
            var entry = new EndpointEntry(id, endpoint);
            SetupPullAudio(entry, endpoint);
            PreallocateScratch(id, endpoint);
            _endpoints[id] = entry;
            RebuildEndpointsSnapshot();
            AutoRegisterEndpointClock(endpoint);
            Log.LogDebug("Audio endpoint registered: {Id} ({Name})", id, endpoint.Name);
            return id;
        }
    }

    public EndpointId RegisterEndpoint(IVideoEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_lock)
        {
            var id = EndpointId.New();
            var entry = new EndpointEntry(id, endpoint);
            SetupPullVideo(entry, endpoint);
            _endpoints[id] = entry;
            RebuildEndpointsSnapshot();
            AutoRegisterEndpointClock(endpoint);
            Log.LogDebug("Video endpoint registered: {Id} ({Name})", id, endpoint.Name);
            return id;
        }
    }

    public EndpointId RegisterEndpoint(IAVEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_lock)
        {
            var id = EndpointId.New();
            var entry = new EndpointEntry(id, endpoint);
            SetupPullAudio(entry, endpoint);
            SetupPullVideo(entry, endpoint);
            PreallocateScratch(id, endpoint);
            _endpoints[id] = entry;
            RebuildEndpointsSnapshot();
            AutoRegisterEndpointClock(endpoint);
            Log.LogDebug("AV endpoint registered: {Id} ({Name})", id, endpoint.Name);
            return id;
        }
    }

    public void UnregisterEndpoint(EndpointId id)
    {
        lock (_lock)
        {
            if (!_endpoints.TryRemove(id, out var entry)) return;
            RebuildEndpointsSnapshot();

            // Detach pull callback
            if (entry.Audio is IPullAudioEndpoint pull)
                pull.FillCallback = null;
            if (entry.Video is IPullVideoEndpoint pullV)
                pullV.PresentCallback = null;

            // Auto-unregister endpoint clock
            var ep = (IMediaEndpoint?)entry.Audio ?? entry.Video;
            if (ep is IClockCapableEndpoint clockEp)
                UnregisterClock(clockEp.Clock);

            // Remove all routes to this endpoint
            var toRemove = _routes.Values.Where(r => r.EndpointId == id).ToList();
            foreach (var route in toRemove)
                RemoveRouteInternal(route);

            _scratchBuffers.TryRemove(id, out _);
            _pushAudioFrameAccumulators.TryRemove(id, out _);

            Log.LogDebug("Endpoint unregistered: {Id}", id);
        }
    }

    private void RebuildEndpointsSnapshot()
    {
        // Caller must hold _lock. .Values on ConcurrentDictionary is snapshot-like
        // but not guaranteed-consistent under concurrent mutation; taking it
        // inside the lock is sufficient because all mutations go through _lock.
        _endpointsSnapshot = _endpoints.Values.ToArray();
    }

    /// <summary>
    /// §3.14 / R6: pre-allocate the per-endpoint scratch buffer at registration
    /// time using the endpoint's preferred frame count and channel count, so the
    /// first push/fill tick never hits <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate"/>
    /// on an RT thread. If the endpoint exposes no hint, fall back to a
    /// conservative 2048-float buffer (≈ 512 frames × 4 channels) which is
    /// big enough that the common case never reallocates.
    /// </summary>
    private void PreallocateScratch(EndpointId id, IAudioEndpoint audio)
    {
        int channels;
        int framesPerBuffer;
        if (audio is IPullAudioEndpoint pull)
        {
            channels = Math.Max(1, pull.EndpointFormat.Channels);
            framesPerBuffer = Math.Max(1, pull.FramesPerBuffer);
        }
        else
        {
            var hint = audio.NegotiatedFormat;
            channels = Math.Max(1, hint?.Channels ?? 2);
            framesPerBuffer = _options.DefaultFramesPerBuffer > 0
                ? _options.DefaultFramesPerBuffer
                : 512;
        }

        int minSize = framesPerBuffer * channels;
        if (minSize < 2048) minSize = 2048;
        _scratchBuffers[id] = new float[minSize];
    }

    /// <summary>
    /// If the endpoint implements <see cref="IClockCapableEndpoint"/>, auto-register
    /// its clock. Priority resolution (review §4.8 / R11):
    /// <list type="number">
    ///   <item>If the endpoint overrode <see cref="IClockCapableEndpoint.DefaultPriority"/>
    ///         (i.e. returned anything other than the interface default
    ///         <see cref="ClockPriority.Hardware"/>), that value wins — virtual endpoints
    ///         declare <see cref="ClockPriority.Internal"/>, NDI receive declares
    ///         <see cref="ClockPriority.External"/>, etc.</item>
    ///   <item>Otherwise fall back to
    ///         <see cref="AVRouterOptions.DefaultEndpointClockPriority"/> so existing
    ///         global overrides keep working.</item>
    /// </list>
    /// </summary>
    private void AutoRegisterEndpointClock(IMediaEndpoint endpoint)
    {
        if (endpoint is IClockCapableEndpoint clockEp)
        {
            var endpointPref = clockEp.DefaultPriority;
            var priority = endpointPref == ClockPriority.Hardware
                ? _options.DefaultEndpointClockPriority
                : endpointPref;
            RegisterClock(clockEp.Clock, priority);
        }
    }

    // ── IAVRouter: Routing ──────────────────────────────────────────────

    public RouteId CreateRoute(InputId input, EndpointId endpoint)
    {
        if (!_inputs.TryGetValue(input, out var inp))
            throw new MediaRoutingException($"Input {input} is not registered.");
        if (!_endpoints.TryGetValue(endpoint, out var ep))
            throw new MediaRoutingException($"Endpoint {endpoint} is not registered.");

        return inp.Kind switch
        {
            InputKind.Audio => CreateAudioRoute(inp, ep, new AudioRouteOptions()),
            InputKind.Video => CreateVideoRoute(inp, ep, new VideoRouteOptions()),
            _ => throw new MediaRoutingException("Unknown input kind.")
        };
    }

    public RouteId CreateRoute(InputId input, EndpointId endpoint, AudioRouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_inputs.TryGetValue(input, out var inp))
            throw new MediaRoutingException($"Input {input} is not registered.");
        if (inp.Kind != InputKind.Audio)
            throw new MediaRoutingException("Audio route options require an audio input.");
        if (!_endpoints.TryGetValue(endpoint, out var ep))
            throw new MediaRoutingException($"Endpoint {endpoint} is not registered.");

        return CreateAudioRoute(inp, ep, options);
    }

    public RouteId CreateRoute(InputId input, EndpointId endpoint, VideoRouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_inputs.TryGetValue(input, out var inp))
            throw new MediaRoutingException($"Input {input} is not registered.");
        if (inp.Kind != InputKind.Video)
            throw new MediaRoutingException("Video route options require a video input.");
        if (!_endpoints.TryGetValue(endpoint, out var ep))
            throw new MediaRoutingException($"Endpoint {endpoint} is not registered.");

        return CreateVideoRoute(inp, ep, options);
    }

    public void RemoveRoute(RouteId id)
    {
        lock (_lock)
        {
            if (!_routes.TryGetValue(id, out var route)) return;
            RemoveRouteInternal(route);
        }
    }

    public void SetRouteEnabled(RouteId id, bool enabled)
    {
        if (!_routes.TryGetValue(id, out var route))
            throw new MediaRoutingException($"Route {id} is not registered.");
        Volatile.Write(ref route.Enabled, enabled);
    }

    // ── IAVRouter: Clock ────────────────────────────────────────────────

    public IMediaClock InternalClock => _internalClock;

    public IMediaClock Clock => _resolvedClock ?? _internalClock;

    /// <summary>
    /// §4.9 / R10: raised on the caller's thread (outside <c>_clockLock</c>) whenever
    /// <see cref="Clock"/> changes as a result of <see cref="RegisterClock"/>,
    /// <see cref="UnregisterClock"/>, or <see cref="SetClock"/>. The event argument is
    /// the new active clock (never null — the internal fallback is surfaced when the
    /// registry is empty). Subscribers may call back into the router safely because
    /// the lock is released before the invocation.
    /// </summary>
    public event Action<IMediaClock>? ActiveClockChanged;

    public void RegisterClock(IMediaClock clock, ClockPriority priority = ClockPriority.Hardware)
    {
        IMediaClock? previous;
        IMediaClock current;
        lock (_clockLock)
        {
            previous = _resolvedClock;
            _clockRegistry.RemoveAll(e => ReferenceEquals(e.Clock, clock));
            _clockRegistry.Add((clock, priority, _clockRegistrationOrder++));
            ResolveActiveClock();
            current = Clock; // internal fallback if registry is empty
        }
        Log.LogInformation("Clock registered: {Type} at priority {Priority} → active={Active}",
            clock.GetType().Name, priority, current.GetType().Name);
        RaiseActiveClockChangedIfChanged(previous, current);
    }

    public void UnregisterClock(IMediaClock clock)
    {
        IMediaClock? previous;
        IMediaClock current;
        lock (_clockLock)
        {
            previous = _resolvedClock;
            int removed = _clockRegistry.RemoveAll(e => ReferenceEquals(e.Clock, clock));
            if (removed > 0) ResolveActiveClock();
            current = Clock;
        }
        Log.LogInformation("Clock unregistered: {Type} → active={Active}",
            clock.GetType().Name, current.GetType().Name);
        RaiseActiveClockChangedIfChanged(previous, current);
    }

    public void SetClock(IMediaClock? clock)
    {
        IMediaClock? previous;
        IMediaClock current;
        lock (_clockLock)
        {
            previous = _resolvedClock;
            _clockRegistry.RemoveAll(e => e.Priority == ClockPriority.Override);
            if (clock is not null)
            {
                // Remove any prior registration of this same clock (e.g. the
                // auto Hardware-priority one added by RegisterEndpoint) so the
                // registry only contains the Override entry. Keeps the clock
                // list clean and makes diagnostic logs unambiguous.
                _clockRegistry.RemoveAll(e => ReferenceEquals(e.Clock, clock));
                _clockRegistry.Add((clock, ClockPriority.Override, _clockRegistrationOrder++));
            }
            ResolveActiveClock();
            current = Clock;
        }
        Log.LogInformation("Clock override {Action}: {Type} → active={Active}",
            clock is null ? "cleared" : "set",
            clock?.GetType().Name ?? "(none)",
            current.GetType().Name);
        RaiseActiveClockChangedIfChanged(previous, current);
    }

    private void RaiseActiveClockChangedIfChanged(IMediaClock? previous, IMediaClock current)
    {
        // Compare against the _previous_ resolved clock — not Clock (which falls
        // back to the internal clock when the registry is empty). Transitioning
        // between (non-null registry entry) and (null → internal) is a real
        // change and must fire; transitioning from internal to internal is not.
        var previousActive = previous ?? _internalClock;
        if (ReferenceEquals(previousActive, current)) return;
        var handler = ActiveClockChanged;
        if (handler is null) return;
        try { handler(current); }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "ActiveClockChanged subscriber threw; continuing.");
        }
    }

    private void ResolveActiveClock()
    {
        if (_clockRegistry.Count == 0) { _resolvedClock = null; return; }
        var best = _clockRegistry[0];
        for (int i = 1; i < _clockRegistry.Count; i++)
        {
            var e = _clockRegistry[i];
            if (e.Priority > best.Priority || (e.Priority == best.Priority && e.Order > best.Order))
                best = e;
        }
        _resolvedClock = best.Clock;
    }

    // ── IAVRouter: Per-input control ────────────────────────────────────

    public void SetInputVolume(InputId id, float volume)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new MediaRoutingException($"Input {id} is not registered.");
        // §3.18 / B13+R15: publish with Volatile.Write so the push/fill threads observe
        // the new value on the next iteration even on weakly ordered architectures
        // (ARM64). The float itself is 4 bytes and aligned, so the store is atomic.
        Volatile.Write(ref entry.Volume, volume);
    }

    public void SetInputTimeOffset(InputId id, TimeSpan offset)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new MediaRoutingException($"Input {id} is not registered.");
        // TimeSpan is a 64-bit struct; write the underlying ticks atomically so
        // readers on 32-bit runtimes cannot tear (§3.18 / B13). 64-bit stores are
        // atomic on 64-bit runtimes already, but Interlocked.Exchange is the
        // cheapest universal guarantee.
        Interlocked.Exchange(ref entry.TimeOffsetTicks, offset.Ticks);
    }

    public void SetInputEnabled(InputId id, bool enabled)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new MediaRoutingException($"Input {id} is not registered.");
        Volatile.Write(ref entry.Enabled, enabled);
    }

    // ── IAVRouter: Per-endpoint control ─────────────────────────────────

    public void SetEndpointGain(EndpointId id, float gain)
    {
        if (!_endpoints.TryGetValue(id, out var entry))
            throw new MediaRoutingException($"Endpoint {id} is not registered.");
        entry.Gain = gain;
    }

    // ── IAVRouter: Video ────────────────────────────────────────────────

    public bool BypassVideoPtsScheduling { get; set; }

    // ── IAVRouter: Diagnostics ──────────────────────────────────────────

    public TimeSpan GetAvDrift(InputId audioInput, InputId videoInput)
    {
        if (!_inputs.TryGetValue(audioInput, out var aEntry) || aEntry.Kind != InputKind.Audio)
            throw new MediaRoutingException("Audio input not found.");
        if (!_inputs.TryGetValue(videoInput, out var vEntry) || vEntry.Kind != InputKind.Video)
            throw new MediaRoutingException("Video input not found.");

        // Compare both streams to the master clock rather than to each other directly.
        // This cancels out most of the wall-clock jitter that would otherwise appear as
        // a ±½-frame sawtooth on the readout.  Video quantization noise is further
        // suppressed by using NextExpectedPts (interpolated) and an EMA filter.
        var clockPos  = Clock.Position;
        var aChannel  = aEntry.AudioChannel!;
        var vChannel  = vEntry.VideoChannel!;
        var aPosition = aChannel.Position;
        // Clamp the "interpolated" video position between the last-presented PTS and the
        // next expected PTS so a fast clock can't run it past the ring head.
        long clockTicks     = clockPos.Ticks;
        long vLastPtsTicks  = vChannel.Position.Ticks;
        long vNextPtsTicks  = vChannel.NextExpectedPts.Ticks;
        long vPosTicks      = Math.Clamp(clockTicks, vLastPtsTicks, Math.Max(vLastPtsTicks, vNextPtsTicks));

        long audioLead = aPosition.Ticks - clockTicks;
        long videoLead = vPosTicks - clockTicks;
        long raw       = audioLead - videoLead; // +ve → audio ahead of video

        // One-pole IIR smoothing so a human-readable UI number doesn't flicker.
        // α = 0.4 converges in ≈250 ms at 100 Hz sampling — responsive enough that
        // the displayed value tracks real drift closely (so tightening the
        // correction gains is visible to the user) while still swallowing
        // single-sample PTS-quantization noise.
        const double Alpha = 0.4;
        lock (_driftEmaLock)
        {
            // §3.54 / R16: single-pair EMA. When the caller polls a different
            // (audioInput, videoInput) pair from the previous call, reset the
            // filter so the new pair is not contaminated by the previous one's
            // history. Full per-pair keying is tracked as §6.9; this is the
            // minimum viable correctness fix.
            if (!_driftEmaValid ||
                _driftEmaAudioInput != audioInput ||
                _driftEmaVideoInput != videoInput)
            {
                _driftEmaTicks = raw;
                _driftEmaValid = true;
                _driftEmaAudioInput = audioInput;
                _driftEmaVideoInput = videoInput;
            }
            else
            {
                _driftEmaTicks = (long)(_driftEmaTicks * (1 - Alpha) + raw * Alpha);
            }
            return TimeSpan.FromTicks(_driftEmaTicks);
        }
    }

    // EMA state for GetAvDrift — kept on the router because it's a smoothed diagnostic
    // readout shared across all audio↔video pairings. Only one pair is tracked at a
    // time (§3.54); the filter resets when the caller switches pairs. §6.9 will
    // promote this to a per-pair dictionary for multi-pair scenarios.
    private readonly Lock _driftEmaLock = new();
    private long _driftEmaTicks;
    private bool _driftEmaValid;
    private InputId _driftEmaAudioInput;
    private InputId _driftEmaVideoInput;

    public float GetInputPeakLevel(InputId id)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new MediaRoutingException($"Input {id} is not registered.");
        return entry.PeakLevel;
    }

    public RouterDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        // Lock-free: ConcurrentDictionary.Values returns an eventually-consistent snapshot
        // enumerator, which is all the diagnostics consumer needs.  Previously this held
        // _lock and ran three LINQ pipelines, blocking route mutation for the duration
        // (§4.9).  A slightly stale diagnostics view is acceptable here — the reporter
        // will call this again on the next sampling tick.
        var inputSnapshots = _inputs.Values.Select(i => new InputDiagnostics(
            i.Id, i.Kind.ToString(), i.Enabled, i.Volume, i.PeakLevel, i.TimeOffset)).ToArray();

        var endpointSnapshots = _endpoints.Values.Select(e => new EndpointDiagnostics(
            e.Id, e.Kind.ToString(), e.Gain)).ToArray();

        var routeSnapshots = _routes.Values.Select(r => new RouteDiagnostics(
            r.Id, r.InputId, r.EndpointId, r.Kind.ToString(), r.Enabled, r.Gain,
            r.TimeOffset,
            r.Resampler is not null)).ToArray();

        return new RouterDiagnosticsSnapshot(
            IsRunning, Clock.Position, BypassVideoPtsScheduling,
            inputSnapshots, endpointSnapshots, routeSnapshots);
    }

    // ── IDisposable / IAsyncDisposable ──────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;                     // publish FIRST — StopAsync may observe it
        await StopAsync();
        DisposeCore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        DisposeCore();
    }

    private void DisposeCore()
    {
        // §3.13 / R9: tear down every route symmetrically through RemoveRouteInternal
        // so per-route state (auto-resamplers, video subscriptions, push-video
        // pending frames, drift trackers, mismatch-warning dedupe entries) is
        // released even if the caller never explicitly removed routes. Without
        // this, repeated router create→dispose cycles leaked subscriptions and
        // bled the pool.
        lock (_lock)
        {
            // Snapshot ids to avoid mutating the dictionary under enumeration.
            var routeIds = _routes.Keys.ToArray();
            foreach (var id in routeIds)
            {
                if (_routes.TryGetValue(id, out var route))
                    RemoveRouteInternal(route);
            }
        }

        // Defensive: any video frames still pending in the router-scoped slot
        // (shouldn't exist after the loop above, since RemoveRouteInternal also
        // drains _pushVideoPending — but leave as a belt-and-braces guard).
        foreach (var kv in _pushVideoPending)
            kv.Value.MemoryOwner?.Dispose();
        _pushVideoPending.Clear();

        _internalClock.Dispose();
        Log.LogInformation("AVRouter disposed");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private implementation
    // ═══════════════════════════════════════════════════════════════════

    // ── Pull endpoint setup ─────────────────────────────────────────────

    private void SetupPullAudio(EndpointEntry entry, IAudioEndpoint ep)
    {
        if (ep is IPullAudioEndpoint pull)
        {
            var cb = new AudioFillCallbackForEndpoint(this, entry);
            entry.AudioFillCb = cb;
            pull.FillCallback = cb;
        }
    }

    private void SetupPullVideo(EndpointEntry entry, IVideoEndpoint ep)
    {
        if (ep is IPullVideoEndpoint pull)
        {
            pull.PresentCallback = new VideoPresentCallbackForEndpoint(this, entry);
        }
    }

    // ── Route creation ──────────────────────────────────────────────────

    private RouteId CreateAudioRoute(InputEntry inp, EndpointEntry ep, AudioRouteOptions options)
    {
        if (ep.Audio is null)
            throw new MediaRoutingException("Endpoint does not support audio.");

        // Validate audio format compatibility if endpoint advertises capabilities
        if (ep.Audio is IFormatCapabilities<AudioFormat> caps && inp.AudioChannel is not null)
        {
            System.Diagnostics.Debug.Assert(caps.SupportedFormats is not null,
                "IFormatCapabilities<AudioFormat>.SupportedFormats must be non-null (§3.53 / CH9).");
            var srcFormat = inp.AudioChannel.SourceFormat;
            if (caps.SupportedFormats.Count > 0 && !caps.SupportedFormats.Contains(srcFormat))
            {
                Log.LogWarning(
                    "Audio route {Input}→{Endpoint}: source format {SrcFormat} " +
                    "not in endpoint's supported formats. " +
                    "Route created — resampler/channel map may handle the mismatch.",
                    inp.Id, ep.Id, srcFormat);
            }
        }

        lock (_lock)
        {
            var routeId = RouteId.New();
            var route = new RouteEntry(routeId, inp.Id, ep.Id, InputKind.Audio)
            {
                Gain = options.Gain,
                TimeOffset = options.TimeOffset,
                ChannelMap = options.ChannelMap,
                Resampler = options.Resampler,
            };

            // Auto-derive channel map if not provided
            if (route.ChannelMap is null && inp.AudioChannel is not null)
            {
                int srcCh = inp.AudioChannel.SourceFormat.Channels;
                int dstCh = ep.Audio is IPullAudioEndpoint pullEp
                    ? pullEp.EndpointFormat.Channels
                    : srcCh; // push endpoints: assume same channel count
                route.ChannelMap = ChannelRouteMap.Auto(srcCh, dstCh);
            }

            // Auto-create resampler if rates differ
            if (route.Resampler is null && inp.AudioChannel is not null && ep.Audio is IPullAudioEndpoint pullAudio)
            {
                int srcRate = inp.AudioChannel.SourceFormat.SampleRate;
                int dstRate = pullAudio.EndpointFormat.SampleRate;
                if (srcRate != dstRate)
                {
                    route.Resampler = new LinearResampler();
                    route.OwnsResampler = true;
                    Log.LogDebug("Auto-created resampler for route {Route}: {SrcRate}→{DstRate}",
                        routeId, srcRate, dstRate);
                }
            }

            // Pre-bake channel map for RT hot path
            if (route.ChannelMap is not null && inp.AudioChannel is not null)
                route.BakedChannelMap = route.ChannelMap.BakeRoutes(inp.AudioChannel.SourceFormat.Channels);

            _routes[routeId] = route;
            RebuildAudioRouteSnapshot();

            Log.LogDebug("Audio route created: {Route} ({Input}→{Endpoint})", routeId, inp.Id, ep.Id);
            return routeId;
        }
    }

    private RouteId CreateVideoRoute(InputEntry inp, EndpointEntry ep, VideoRouteOptions options)
    {
        if (ep.Video is null)
            throw new MediaRoutingException("Endpoint does not support video.");

        // Validate pixel format compatibility if endpoint advertises capabilities
        if (ep.Video is IFormatCapabilities<PixelFormat> caps && inp.VideoChannel is not null)
        {
            System.Diagnostics.Debug.Assert(caps.SupportedFormats is not null,
                "IFormatCapabilities<PixelFormat>.SupportedFormats must be non-null (§3.53 / CH9).");
            var srcFormat = inp.VideoChannel.SourceFormat;
            if (srcFormat.Width > 0 && caps.SupportedFormats.Count > 0)
            {
                if (!caps.SupportedFormats.Contains(srcFormat.PixelFormat))
                {
                    Log.LogWarning(
                        "Video route {Input}→{Endpoint}: source pixel format {SrcFormat} " +
                        "not in endpoint's supported formats [{Supported}]. " +
                        "Route created but frames may be rejected or require conversion.",
                        inp.Id, ep.Id, srcFormat.PixelFormat,
                        string.Join(", ", caps.SupportedFormats));
                }
            }
        }

        lock (_lock)
        {
            // Last-write-wins: remove any existing video route to this endpoint
            var existing = _routes.Values
                .FirstOrDefault(r => r.Kind == InputKind.Video && r.EndpointId == ep.Id);
            if (existing is not null)
                RemoveRouteInternal(existing);

            var routeId = RouteId.New();
            var route = new RouteEntry(routeId, inp.Id, ep.Id, InputKind.Video)
            {
                Gain = options.Gain,
                TimeOffset = options.TimeOffset,
            };

            // Create a private subscription into the input channel so this endpoint
            // never races other consumers for frames. Push endpoints use DropOldest
            // (slow push must not stall the decoder); pull endpoints use Wait with
            // a deeper queue (vsync-paced, they are the pace-setter).
            //
            // Push capacity = 4: gives ~4 push-ticks of headroom against transient
            // stalls.  Lower values tempt frame loss when tick timing quantises
            // against the source frame rate (e.g. 24 fps vs 5 ms tick).
            if (inp.VideoChannel is not null)
            {
                bool isPull = ep.Video is IPullVideoEndpoint;
                route.VideoSub = inp.VideoChannel.Subscribe(new VideoSubscriptionOptions(
                    Capacity: isPull ? Math.Max(_options.DefaultFramesPerBuffer, inp.VideoChannel.BufferDepth) : 4,
                    OverflowPolicy: isPull ? VideoOverflowPolicy.Wait : VideoOverflowPolicy.DropOldest,
                    DebugName: $"{inp.Id}->{ep.Id}"));
            }

            _routes[routeId] = route;
            RebuildVideoRouteSnapshot();

            Log.LogDebug("Video route created: {Route} ({Input}→{Endpoint})", routeId, inp.Id, ep.Id);
            return routeId;
        }
    }

    private void RemoveRouteInternal(RouteEntry route)
    {
        // Caller must hold _lock
        _routes.TryRemove(route.Id, out _);

        if (route.OwnsResampler)
            route.Resampler?.Dispose();

        // Dispose the per-route video subscription so its queued frames release
        // their refcounts (pool rentals return) and the decoder stops fanning
        // frames out to it.
        route.VideoSub?.Dispose();
        route.VideoSub = null;
        // Per-route push-video bookkeeping must also be released when the route is gone.
        if (_pushVideoPending.TryRemove(route.Id, out var pending))
            pending.MemoryOwner?.Dispose();
        _pushVideoDrift.TryRemove(route.Id, out _);
        _pushAudioFormatMismatchWarnings.TryRemove(route.Id, out _);

        if (route.Kind == InputKind.Audio)
            RebuildAudioRouteSnapshot();
        else
            RebuildVideoRouteSnapshot();

        Log.LogDebug("Route removed: {Id}", route.Id);
    }

    private void RebuildAudioRouteSnapshot()
    {
        var arr = _routes.Values.Where(r => r.Kind == InputKind.Audio).ToArray();
        _audioRouteSnapshot = arr;
        _audioRoutesByEndpoint = GroupByEndpoint(arr);
    }

    private void RebuildVideoRouteSnapshot()
    {
        var arr = _routes.Values.Where(r => r.Kind == InputKind.Video).ToArray();
        _videoRouteSnapshot = arr;
        _videoRoutesByEndpoint = GroupByEndpoint(arr);
    }

    private static Dictionary<EndpointId, RouteEntry[]> GroupByEndpoint(RouteEntry[] routes)
    {
        // Group + ToArray once so the push tick enumeration is pure array iteration.
        var dict = new Dictionary<EndpointId, RouteEntry[]>();
        if (routes.Length == 0) return dict;

        // Count per endpoint first to size the arrays exactly (avoids List<> resize churn).
        var counts = new Dictionary<EndpointId, int>();
        foreach (var r in routes)
            counts[r.EndpointId] = counts.TryGetValue(r.EndpointId, out var c) ? c + 1 : 1;

        var cursors = new Dictionary<EndpointId, int>(counts.Count);
        foreach (var (id, c) in counts)
        {
            dict[id] = new RouteEntry[c];
            cursors[id] = 0;
        }

        foreach (var r in routes)
        {
            int i = cursors[r.EndpointId]++;
            dict[r.EndpointId][i] = r;
        }
        return dict;
    }

    // ── Push tick (drives push endpoints) ───────────────────────────────

    private void PushThreadLoop(CancellationToken ct)
    {
        var cadence = _options.InternalTickCadence;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long audioCadenceTicks = (long)(cadence.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        // Video push runs on its own thread so large frame copies don't block audio delivery.
        var videoThread = new Thread(() => PushVideoThreadLoop(ct))
        {
            Name = "AVRouter-PushVideo",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        videoThread.Start();

        // Time-aware audio push: track wall-clock elapsed time so frame counts
        // are derived from actual elapsed time, not the nominal tick cadence.
        // This eliminates audio speed fluctuations caused by Thread.Sleep jitter.
        long lastAudioSw = sw.ElapsedTicks;

        while (_running && !ct.IsCancellationRequested)
        {
            long tickStart = sw.ElapsedTicks;
            long elapsedSw = tickStart - lastAudioSw;
            lastAudioSw = tickStart;

            // Convert to seconds for frame count computation.
            double elapsedSeconds = (double)elapsedSw / System.Diagnostics.Stopwatch.Frequency;

            try
            {
                PushAudioTick(elapsedSeconds);
            }
            catch (Exception ex)
            {
                long n = Interlocked.Increment(ref _pushAudioErrorCount);
                if (n <= 3 || n % 100 == 0)
                    Log.LogError(ex, "Error in audio push tick (count={Count})", n);
            }

            // Sleep for the remaining cadence time — cancellation-aware so StopAsync
            // unblocks us in tens of microseconds (§3.19 / R19+R20).
            long targetTicks = tickStart + audioCadenceTicks;
            WaitUntil(sw, targetTicks, ct);
        }

        videoThread.Join(TimeSpan.FromSeconds(2));
    }

    private void PushVideoThreadLoop(CancellationToken ct)
    {
        var cadence = _options.InternalTickCadence;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long videoCadenceTicks = (long)(cadence.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        while (_running && !ct.IsCancellationRequested)
        {
            long tickStart = sw.ElapsedTicks;

            try
            {
                PushVideoTick();
            }
            catch (Exception ex)
            {
                long n = Interlocked.Increment(ref _pushVideoErrorCount);
                if (n <= 3 || n % 100 == 0)
                    Log.LogError(ex, "Error in video push tick (count={Count})", n);
            }

            long targetTicks = tickStart + videoCadenceTicks;
            WaitUntil(sw, targetTicks, ct);
        }
    }

    /// <summary>
    /// Hybrid spin+sleep wait until the stopwatch reaches <paramref name="targetTicks"/>.
    /// Linux's <c>Thread.Sleep</c> granularity is ~1–4 ms; at a 10 ms cadence that is ±10–40%
    /// jitter, directly modulating the audio frame-count-per-tick (§4.10).  We coarse-sleep
    /// up to ~3 ms short of the deadline, then spin-wait the tail for microsecond accuracy.
    /// The wait is cancellation-aware so StopAsync can unblock it immediately (§3.19).
    /// </summary>
    private static void WaitUntil(System.Diagnostics.Stopwatch sw, long targetTicks, CancellationToken ct)
    {
        long remaining = targetTicks - sw.ElapsedTicks;
        if (remaining <= 0) return;

        long msTicks = System.Diagnostics.Stopwatch.Frequency / 1000;      // ticks per millisecond
        long coarseThresholdTicks = msTicks * 3;                            // spin for final 3 ms

        if (remaining > coarseThresholdTicks)
        {
            int sleepMs = (int)((remaining - coarseThresholdTicks) * 1000L / System.Diagnostics.Stopwatch.Frequency);
            if (sleepMs > 0)
            {
                // WaitHandle.WaitOne on the cancellation handle returns true when
                // cancelled, giving us an immediate wake without throwing.
                if (ct.WaitHandle.WaitOne(sleepMs)) return;
            }
        }

        while (sw.ElapsedTicks < targetTicks)
        {
            if (ct.IsCancellationRequested) return;
            Thread.SpinWait(20);
        }
    }

    private void PushAudioTick(double elapsedSeconds)
    {
        var routesByEp = _audioRoutesByEndpoint;
        if (routesByEp.Count == 0) return;

        // §3.16 / R7: iterate the COW endpoint snapshot (rebuilt under _lock
        // during Register/Unregister) so this tick cannot observe a
        // half-initialised endpoint.
        var endpointsSnapshot = _endpointsSnapshot;
        foreach (var ep in endpointsSnapshot)
        {
            if (ep.Audio is null or IPullAudioEndpoint) continue;
            if (!routesByEp.TryGetValue(ep.Id, out var routes) || routes.Length == 0) continue;

            // Determine format from the first active route's input
            AudioFormat? outFormat = null;
            int framesPerBuffer = 0;

            // First pass: determine output format and buffer size
            foreach (var route in routes)
            {
                if (!Volatile.Read(ref route.Enabled)) continue;
                if (!_inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled) continue;
                var fmt = inp.AudioChannel!.SourceFormat;
                if (outFormat is null)
                {
                    outFormat = fmt;

                    if (_options.DefaultFramesPerBuffer > 0)
                    {
                        framesPerBuffer = _options.DefaultFramesPerBuffer;
                    }
                    else
                    {
                        // Time-aware: compute frame count from actual elapsed wall-clock
                        // time plus any fractional remainder from the previous tick.
                        // This keeps the long-term average rate exactly at the source
                        // sample rate regardless of Thread.Sleep jitter.
                        double accum = _pushAudioFrameAccumulators.GetOrAdd(ep.Id, 0.0);
                        double exact = fmt.SampleRate * elapsedSeconds + accum;
                        framesPerBuffer = (int)exact;
                        _pushAudioFrameAccumulators[ep.Id] = exact - framesPerBuffer;
                    }
                }
                else if (fmt.SampleRate != outFormat.Value.SampleRate ||
                         fmt.Channels != outFormat.Value.Channels)
                {
                    if (_pushAudioFormatMismatchWarnings.TryAdd(route.Id, 0))
                    {
                        Log.LogWarning(
                            "Push audio route {Route} ({Input}->{Endpoint}) skipped due to format mismatch. " +
                            "Expected {ExpectedRate}Hz/{ExpectedCh}ch, got {ActualRate}Hz/{ActualCh}ch. " +
                            "Use homogeneous route formats or route-level resampling before push endpoints.",
                            route.Id, route.InputId, route.EndpointId,
                            outFormat.Value.SampleRate, outFormat.Value.Channels,
                            fmt.SampleRate, fmt.Channels);
                    }
                    continue;
                }
                break;
            }

            if (outFormat is null || framesPerBuffer == 0) continue;
            var format = outFormat.Value;

            int destSamples = framesPerBuffer * format.Channels;
            var destBuf = ArrayPool<float>.Shared.Rent(destSamples);
            try
            {
                var dest = destBuf.AsSpan(0, destSamples);
                dest.Clear();
                int maxFilled = 0;

                // Stream-time PTS of the first sample in the delivered buffer.  Seeded
                // from the first active input's Position BEFORE its FillBuffer call,
                // which is the read-head PTS that the upcoming samples will start at.
                // Sinks that stamp media timecode (NDIAVEndpoint) use this directly; other
                // sinks see it discarded by the default <see cref="IAudioEndpoint.ReceiveBuffer"/>
                // overload.  TimeSpan.MinValue means "no PTS" (all inputs empty).
                TimeSpan bufferPts = TimeSpan.MinValue;

                // Second pass: pull from each route, apply map, mix into dest
                foreach (var route in routes)
                {
                    if (!Volatile.Read(ref route.Enabled)) continue;
                    if (!_inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled) continue;

                    var channel = inp.AudioChannel!;
                    var srcFormat = channel.SourceFormat;
                    if (srcFormat.SampleRate != format.SampleRate || srcFormat.Channels != format.Channels)
                        continue;
                    int srcSamples = framesPerBuffer * srcFormat.Channels;

                    var scratch = GetOrCreateScratch(ep.Id, srcSamples);
                    var srcSpan = scratch.AsSpan(0, srcSamples);
                    srcSpan.Clear();

                    // Snapshot Position BEFORE the read: that's the stream PTS of the
                    // first sample we're about to consume.  Use the first input that
                    // actually produces data as the buffer's reference PTS — sufficient
                    // for the typical single-input-per-endpoint case; for N-input mixes
                    // the sinks that care about PTS (NDI) typically have one decoder
                    // channel per input anyway.
                    var ptsBeforeFill = channel.Position;

                    int filled = channel.FillBuffer(srcSpan, framesPerBuffer);
                    if (filled == 0) continue;
                    if (filled > maxFilled) maxFilled = filled;
                    if (bufferPts == TimeSpan.MinValue)
                        bufferPts = ptsBeforeFill + inp.TimeOffset + route.TimeOffset;

                    var filledSpan = srcSpan[..(filled * srcFormat.Channels)];

                    if (Math.Abs(inp.Volume - 1.0f) > 1e-6f)
                        ApplyGain(filledSpan, inp.Volume);
                    if (Math.Abs(route.Gain - 1.0f) > 1e-6f)
                        ApplyGain(filledSpan, route.Gain);

                    // Per-input peak metering (post-volume, pre-mix)
                    inp.PeakLevel = MeasurePeak(filledSpan);

                    // §3.15 / R4: apply the baked channel map whenever one is
                    // supplied, not only when channel counts differ — user-
                    // defined maps can also reorder / attenuate equal-channel
                    // streams (e.g. stereo L↔R swap, mid-side encode).
                    if (route.BakedChannelMap is not null)
                    {
                        int mappedSamples = filled * format.Channels;
                        var mappedBuf = ArrayPool<float>.Shared.Rent(mappedSamples);
                        try
                        {
                            var mapped = mappedBuf.AsSpan(0, mappedSamples);
                            mapped.Clear();
                            ApplyChannelMap(filledSpan, mapped, route.BakedChannelMap,
                                srcFormat.Channels, format.Channels, filled);
                            MixInto(dest, mapped);
                        }
                        finally { ArrayPool<float>.Shared.Return(mappedBuf); }
                    }
                    else
                    {
                        MixInto(dest, filledSpan);
                    }
                }

                // Only deliver when the decoder actually produced content.  Sending
                // synthetic silence during startup underruns would label that silence
                // with whatever PTS we chose — any choice creates a permanent A/V
                // offset on sinks that align audio ↔ video by timecode (NDI etc.).
                // Skipping the send leaves those sinks' timelines paused until real
                // decoded audio arrives, at which point the buffer is labelled with
                // its correct stream PTS.
                if (maxFilled > 0)
                {
                    if (Math.Abs(ep.Gain - 1.0f) > 1e-6f)
                        ApplyGain(dest[..(maxFilled * format.Channels)], ep.Gain);

                    ep.Audio.ReceiveBuffer(
                        dest[..(maxFilled * format.Channels)],
                        maxFilled,
                        format,
                        bufferPts);
                }
            }
            finally { ArrayPool<float>.Shared.Return(destBuf); }
        }
    }

    private void PushVideoTick()
    {
        var routes = _videoRouteSnapshot;
        if (routes.Length == 0) return;

        var clockPos = Clock.Position;
        long earlyToleranceTicks = _options.VideoPtsEarlyTolerance.Ticks;
        int maxCatchUp = _options.VideoMaxCatchUpFramesPerTick;

        foreach (var route in routes)
        {
            if (!Volatile.Read(ref route.Enabled)) continue;
            if (!_inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled) continue;
            if (!_endpoints.TryGetValue(route.EndpointId, out var ep)) continue;
            if (ep.Video is null or IPullVideoEndpoint) continue; // skip pull endpoints
            if (route.VideoSub is null) continue; // route has no input channel (shouldn't happen)

            // 1) Try the per-route pending frame (pulled last tick but was too early).
            VideoFrame candidate;
            bool haveCandidate = _pushVideoPending.TryRemove(route.Id, out candidate);

            if (!haveCandidate)
            {
                // 2) Pull a fresh frame from this route's private subscription. The
                // decoder fans each frame out to every subscription, so pulling here
                // does not race any other endpoint's subscription.
                if (!route.VideoSub.TryRead(out candidate))
                    continue;
            }

            bool didCatchUp = false;

            // 3) PTS gate with smooth drift correction (unless live mode).
            if (!BypassVideoPtsScheduling)
            {
                var drift = _pushVideoDrift.GetOrAdd(route.Id, _ => new PtsDriftTracker());

                // Same self-feedback seeding alignment as the pull-video path
                // (see VideoPresentCallbackForEndpoint for the long-form note):
                // if the master clock hasn't yet caught up to the first frame's
                // PTS, the post-UpdateFromFrame jump would otherwise leave the
                // drift tracker permanently biased.
                bool pushFirstSeed = !drift.HasOrigin;
                long pushSeedClockTicks = clockPos.Ticks < candidate.Pts.Ticks
                    ? candidate.Pts.Ticks
                    : clockPos.Ticks;
                drift.SeedIfNeeded(candidate.Pts.Ticks, pushSeedClockTicks);

                long relativeClockTicks = drift.RelativeClock(clockPos.Ticks);
                long routeOffsetTicks   = inp.TimeOffset.Ticks + route.TimeOffset.Ticks;
                long relativePtsTicks   = drift.RelativePts(candidate.Pts.Ticks, routeOffsetTicks);

                // Too early — cache for next tick.
                if (relativePtsTicks > relativeClockTicks + earlyToleranceTicks)
                {
                    // §3.12 / B12+R17: atomic swap. AddOrUpdate's updater runs
                    // under the bucket lock, so concurrent RemoveRouteInternal
                    // (which calls TryRemove on this key) cannot see or
                    // double-dispose an intermediate state.
                    _pushVideoPending.AddOrUpdate(
                        route.Id,
                        candidate,
                        (_, stale) => { stale.MemoryOwner?.Dispose(); return candidate; });
                    continue;
                }

                // Catch-up: if this frame is late and newer frames are queued, skip
                // forward up to maxCatchUp frames. Skipped frames release their refs.
                if (maxCatchUp > 0)
                {
                    for (int skip = 0; skip < maxCatchUp; skip++)
                    {
                        if (!route.VideoSub.TryRead(out var next)) break;

                        long nextRelPts = drift.RelativePts(next.Pts.Ticks, routeOffsetTicks);

                        if (nextRelPts > relativeClockTicks + earlyToleranceTicks)
                        {
                            // Next frame is too early — cache it and present the
                            // current candidate (the latest "on time" frame).
                            _pushVideoPending.AddOrUpdate(
                                route.Id,
                                next,
                                (_, stale) => { stale.MemoryOwner?.Dispose(); return next; });
                            break;
                        }

                        // Next frame is also on-time or late — skip the current
                        // candidate (release its buffer) and promote next.
                        candidate.MemoryOwner?.Dispose();
                        candidate = next;
                        relativePtsTicks = nextRelPts;
                        didCatchUp = true;
                    }
                }

                // Smooth drift correction applied AFTER catch-up, using the PTS of the
                // frame that will actually be presented. See PtsDriftTracker.IntegrateError
                // for dead-band and gating semantics.
                if (!didCatchUp && !pushFirstSeed)
                    drift.IntegrateError(
                        relativePtsTicks, relativeClockTicks,
                        earlyToleranceTicks,
                        _options.VideoPushDriftCorrectionGain);
            }

            // Forward to the push endpoint (push endpoints copy the buffer synchronously
            // inside ReceiveFrame), then release our refcount — the rental returns to
            // the pool when every other subscriber also releases.
            ep.Video.ReceiveFrame(in candidate);
            candidate.MemoryOwner?.Dispose();
        }
    }

    // ── Pull callbacks (called from endpoint's RT thread) ───────────────

    /// <summary>
    /// Implements <see cref="IAudioFillCallback"/> for a specific pull audio endpoint.
    /// Called from the endpoint's hardware RT callback.
    /// </summary>
    private sealed class AudioFillCallbackForEndpoint : IAudioFillCallback
    {
        private readonly AVRouter _router;
        private readonly EndpointEntry _endpoint;

        public AudioFillCallbackForEndpoint(AVRouter router, EndpointEntry endpoint)
        {
            _router = router;
            _endpoint = endpoint;
        }

        public void Fill(Span<float> dest, int frameCount, AudioFormat endpointFormat)
        {
            dest.Clear();
            if (!_router._running) return;

            var routes = _router._audioRouteSnapshot;
            for (int i = 0; i < routes.Length; i++)
            {
                var route = routes[i];
                if (!Volatile.Read(ref route.Enabled) || route.EndpointId != _endpoint.Id) continue;

                if (!_router._inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled)
                    continue;

                var channel = inp.AudioChannel!;
                var srcFormat = channel.SourceFormat;

                // Need scratch for per-channel pull
                int srcSamples = frameCount * srcFormat.Channels;
                var scratch = _router.GetOrCreateScratch(
                    _endpoint.Id, srcSamples);
                var srcSpan = scratch.AsSpan(0, srcSamples);
                srcSpan.Clear();

                int filled = channel.FillBuffer(srcSpan, frameCount);
                if (filled == 0) continue;

                var filledSpan = srcSpan[..(filled * srcFormat.Channels)];

                // Apply input volume
                if (Math.Abs(inp.Volume - 1.0f) > 1e-6f)
                    ApplyGain(filledSpan, inp.Volume);

                // Apply route gain
                if (Math.Abs(route.Gain - 1.0f) > 1e-6f)
                    ApplyGain(filledSpan, route.Gain);

                // Per-input peak metering (post-volume, pre-mix)
                inp.PeakLevel = MeasurePeak(filledSpan);

                // Apply channel map + resample, then mix into dest
                if (route.Resampler is not null)
                {
                    int outSamples = frameCount * endpointFormat.Channels;
                    var rented = ArrayPool<float>.Shared.Rent(outSamples);
                    try
                    {
                        var resampledBuf = rented.AsSpan(0, outSamples);
                        resampledBuf.Clear();
                        int outFrames = route.Resampler.Resample(
                            filledSpan, resampledBuf, srcFormat, endpointFormat.SampleRate);
                        // Channel map is already handled by resampler output format
                        MixInto(dest, resampledBuf[..(outFrames * endpointFormat.Channels)]);
                    }
                    finally { ArrayPool<float>.Shared.Return(rented); }
                }
                else if (route.BakedChannelMap is not null)
                {
                    // Apply channel routing: scatter src channels → dest channels
                    int mappedSamples = filled * endpointFormat.Channels;
                    var rented = ArrayPool<float>.Shared.Rent(mappedSamples);
                    try
                    {
                        var mapped = rented.AsSpan(0, mappedSamples);
                        mapped.Clear();
                        ApplyChannelMap(filledSpan, mapped, route.BakedChannelMap,
                            srcFormat.Channels, endpointFormat.Channels, filled);
                        MixInto(dest, mapped);
                    }
                    finally { ArrayPool<float>.Shared.Return(rented); }
                }
                else
                {
                    // No channel map, no resampler — direct mix
                    MixInto(dest, filledSpan);
                }
            }

            // Apply endpoint gain
            if (Math.Abs(_endpoint.Gain - 1.0f) > 1e-6f)
                ApplyGain(dest, _endpoint.Gain);
        }
    }

    /// <summary>
    /// Implements <see cref="IVideoPresentCallback"/> for a specific pull video endpoint.
    /// Called from the endpoint's render loop.
    /// </summary>
    private sealed class VideoPresentCallbackForEndpoint : IVideoPresentCallback
    {
        private readonly AVRouter _router;
        private readonly EndpointEntry _endpoint;

        // Cross-clock drift compensation (shared state machine with push path).
        private readonly PtsDriftTracker _drift = new();

        // Cache a frame that was fetched but too early to present, so it's
        // retried on the next render tick instead of being lost.
        private VideoFrame? _pendingFrame;
        private InputId _pendingInputId;

        // Rate limiting: hold the last presented frame so the render loop
        // (which may run faster than the content frame rate) doesn't drain the ring.
        private VideoFrame? _lastPresentedFrame;
        private long _lastPresentedRelativePts = long.MinValue;


        public VideoPresentCallbackForEndpoint(AVRouter router, EndpointEntry endpoint)
        {
            _router = router;
            _endpoint = endpoint;
        }

        public bool TryPresentNext(TimeSpan clockPosition, out VideoFrame frame)
        {
            frame = default;
            if (!_router._running) return false;

            var routes = _router._videoRouteSnapshot;
            for (int i = 0; i < routes.Length; i++)
            {
                var route = routes[i];
                if (!Volatile.Read(ref route.Enabled) || route.EndpointId != _endpoint.Id) continue;

                if (!_router._inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled)
                    continue;
                if (route.VideoSub is null) continue;

                // Try the cached pending frame first (it was too early last tick)
                VideoFrame candidate;
                if (_pendingFrame.HasValue && _pendingInputId == route.InputId)
                {
                    candidate = _pendingFrame.Value;
                    _pendingFrame = null;
                }
                else
                {
                    if (!route.VideoSub.TryRead(out candidate))
                    {
                        // No new frame in our private subscription — re-present the
                        // last one if available.  Other consumers of the same input
                        // have their own subscriptions; we don't share a ring.
                        if (_lastPresentedFrame.HasValue)
                        {
                            frame = _lastPresentedFrame.Value;
                            return true;
                        }
                        continue;
                    }
                }

                // PTS check (unless live mode)
                if (!_router.BypassVideoPtsScheduling)
                {
                    // Drift-tracker seed alignment (no-NDI 165fps runaway fix) — see
                    // the long-form note on the original implementation; unchanged.
                    bool firstSeed = !_drift.HasOrigin;
                    long seedClockTicks = clockPosition.Ticks < candidate.Pts.Ticks
                        ? candidate.Pts.Ticks
                        : clockPosition.Ticks;
                    _drift.SeedIfNeeded(candidate.Pts.Ticks, seedClockTicks);

                    long routeOffsetTicks   = inp.TimeOffset.Ticks + route.TimeOffset.Ticks;
                    long relativePtsTicks   = _drift.RelativePts(candidate.Pts.Ticks, routeOffsetTicks);
                    long relativeClockTicks = _drift.RelativeClock(clockPosition.Ticks);
                    long toleranceTicks     = _router._options.VideoPtsEarlyTolerance.Ticks;

                    // First-ever frame: skip the gate so the presentation pipeline
                    // (and therefore Clock.Position) gets initialized.
                    if (!firstSeed && relativePtsTicks > relativeClockTicks + toleranceTicks)
                    {
                        // Too early — cache for next tick instead of losing it.
                        if (_pendingFrame.HasValue)
                            _pendingFrame.Value.MemoryOwner?.Dispose();
                        _pendingFrame = candidate;
                        _pendingInputId = route.InputId;
                        // Re-present last frame to avoid black
                        if (_lastPresentedFrame.HasValue)
                        {
                            frame = _lastPresentedFrame.Value;
                            return true;
                        }
                        continue;
                    }

                    // Drift correction with dead-band (unchanged).
                    if (!firstSeed)
                    {
                        _drift.IntegrateError(
                            relativePtsTicks, relativeClockTicks, toleranceTicks,
                            _router._options.VideoPullDriftCorrectionGain);
                    }

                    _lastPresentedRelativePts = relativePtsTicks;
                }

                // Release the previously held last-presented frame's ref before
                // replacing. With ref-counted buffers (RefCountedVideoBuffer) a
                // release-then-retain on the same instance is safe; when the new
                // candidate is a different buffer, this returns the old rental.
                if (_lastPresentedFrame.HasValue &&
                    !ReferenceEquals(_lastPresentedFrame.Value.MemoryOwner, candidate.MemoryOwner))
                    _lastPresentedFrame.Value.MemoryOwner?.Dispose();

                _lastPresentedFrame = candidate;
                frame = candidate;
                return true;
            }

            // No route produced a frame — re-present last if available
            if (_lastPresentedFrame.HasValue)
            {
                frame = _lastPresentedFrame.Value;
                return true;
            }

            return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private float[] GetOrCreateScratch(EndpointId id, int minSize)
    {
        // §3.14 / R6: race-free atomic growth. The previous "TryGetValue → new →
        // indexer assign" sequence had a three-step window in which two concurrent
        // Register + push-tick callers could lose one buffer and duplicate the
        // other. AddOrUpdate runs its factories under the bucket lock, so the
        // grown buffer is published atomically. In the fast path (size already
        // sufficient) we avoid AddOrUpdate entirely.
        if (_scratchBuffers.TryGetValue(id, out var buf) && buf.Length >= minSize)
            return buf;

        return _scratchBuffers.AddOrUpdate(
            id,
            _ => new float[minSize],
            (_, existing) => existing.Length >= minSize ? existing : new float[minSize]);
    }

    /// <summary>
    /// Scatters interleaved source samples into interleaved destination samples
    /// using a pre-baked channel route table.
    /// </summary>
    private static readonly IAudioMixer _mixer = DefaultAudioMixer.Instance;

    private static void ApplyChannelMap(
        ReadOnlySpan<float> src, Span<float> dest,
        (int dstCh, float gain)[][] bakedRoutes,
        int srcChannels, int dstChannels, int frameCount)
        => _mixer.ApplyChannelMap(src, dest, bakedRoutes, srcChannels, dstChannels, frameCount);

    private static void ApplyGain(Span<float> buffer, float gain)
        => _mixer.ApplyGain(buffer, gain);

    private static void MixInto(Span<float> dest, ReadOnlySpan<float> src)
        => _mixer.MixInto(dest, src);

    private static float MeasurePeak(ReadOnlySpan<float> buffer)
        => _mixer.MeasurePeak(buffer);
}

