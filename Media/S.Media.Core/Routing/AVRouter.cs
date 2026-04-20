using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
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
        public TimeSpan TimeOffset;
        public bool Enabled = true;

        /// <summary>Peak sample level (absolute) measured after volume, before routing. Updated on RT thread.</summary>
        public float PeakLevel;

        /// <summary>
        /// Last video frame dequeued from this input (by any consumer).
        /// Allows push endpoints to forward the same frame that a pull endpoint already presented,
        /// avoiding ring-buffer contention when both pull and push endpoints share an input.
        /// </summary>
        public VideoFrame? LastVideoFrame;

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
        public IAudioResampler? Resampler;
        public bool OwnsResampler; // if we auto-created the resampler

        public RouteEntry(RouteId id, InputId inputId, EndpointId endpointId, InputKind kind)
        {
            Id = id; InputId = inputId; EndpointId = endpointId; Kind = kind;
        }
    }

    // Dictionaries for O(1) lookup. Mutated under _lock.
    private readonly ConcurrentDictionary<InputId, InputEntry> _inputs = new();
    private readonly ConcurrentDictionary<EndpointId, EndpointEntry> _endpoints = new();
    private readonly ConcurrentDictionary<RouteId, RouteEntry> _routes = new();

    // Snapshot arrays for lock-free RT iteration (copy-on-write pattern)
    private volatile RouteEntry[] _audioRouteSnapshot = [];
    private volatile RouteEntry[] _videoRouteSnapshot = [];

    // ── Clock ───────────────────────────────────────────────────────────

    private readonly StopwatchClock _internalClock;
    private readonly Lock _clockLock = new();
    private readonly List<(IMediaClock Clock, ClockPriority Priority, long Order)> _clockRegistry = [];
    private long _clockRegistrationOrder;
    private volatile IMediaClock? _resolvedClock;
    private Thread? _pushThread;
    private volatile bool _running;
    private bool _disposed;

    // ── Scratch buffers (lazy, per-endpoint) ────────────────────────────

    private readonly ConcurrentDictionary<EndpointId, float[]> _scratchBuffers = new();

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

            _internalClock.Start();

            // Start a dedicated high-resolution push thread
            _pushThread = new Thread(PushThreadLoop)
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
        lock (_lock)
        {
            if (!_running) return Task.CompletedTask;
            _running = false;

            // Wait for push thread to exit
            _pushThread?.Join(timeout: TimeSpan.FromSeconds(2));
            _pushThread = null;

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
            if (!_inputs.TryRemove(id, out var entry)) return;

            // Remove all routes from this input
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
        var id = EndpointId.New();
        var entry = new EndpointEntry(id, endpoint);
        SetupPullAudio(entry, endpoint);
        _endpoints[id] = entry;
        AutoRegisterEndpointClock(endpoint);
        Log.LogDebug("Audio endpoint registered: {Id} ({Name})", id, endpoint.Name);
        return id;
    }

    public EndpointId RegisterEndpoint(IVideoEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var id = EndpointId.New();
        var entry = new EndpointEntry(id, endpoint);
        SetupPullVideo(entry, endpoint);
        _endpoints[id] = entry;
        AutoRegisterEndpointClock(endpoint);
        Log.LogDebug("Video endpoint registered: {Id} ({Name})", id, endpoint.Name);
        return id;
    }

    public EndpointId RegisterEndpoint(IAVEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var id = EndpointId.New();
        var entry = new EndpointEntry(id, endpoint);
        SetupPullAudio(entry, endpoint);
        SetupPullVideo(entry, endpoint);
        _endpoints[id] = entry;
        AutoRegisterEndpointClock(endpoint);
        Log.LogDebug("AV endpoint registered: {Id} ({Name})", id, endpoint.Name);
        return id;
    }

    public void UnregisterEndpoint(EndpointId id)
    {
        lock (_lock)
        {
            if (!_endpoints.TryRemove(id, out var entry)) return;

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

            Log.LogDebug("Endpoint unregistered: {Id}", id);
        }
    }

    /// <summary>
    /// If the endpoint implements <see cref="IClockCapableEndpoint"/>, auto-register
    /// its clock at <see cref="ClockPriority.Hardware"/> priority.
    /// </summary>
    private void AutoRegisterEndpointClock(IMediaEndpoint endpoint)
    {
        if (endpoint is IClockCapableEndpoint clockEp)
            RegisterClock(clockEp.Clock, _options.DefaultEndpointClockPriority);
    }

    // ── IAVRouter: Routing ──────────────────────────────────────────────

    public RouteId CreateRoute(InputId input, EndpointId endpoint)
    {
        if (!_inputs.TryGetValue(input, out var inp))
            throw new InvalidOperationException($"Input {input} is not registered.");
        if (!_endpoints.TryGetValue(endpoint, out var ep))
            throw new InvalidOperationException($"Endpoint {endpoint} is not registered.");

        return inp.Kind switch
        {
            InputKind.Audio => CreateAudioRoute(inp, ep, new AudioRouteOptions()),
            InputKind.Video => CreateVideoRoute(inp, ep, new VideoRouteOptions()),
            _ => throw new InvalidOperationException("Unknown input kind.")
        };
    }

    public RouteId CreateRoute(InputId input, EndpointId endpoint, AudioRouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_inputs.TryGetValue(input, out var inp))
            throw new InvalidOperationException($"Input {input} is not registered.");
        if (inp.Kind != InputKind.Audio)
            throw new InvalidOperationException("Audio route options require an audio input.");
        if (!_endpoints.TryGetValue(endpoint, out var ep))
            throw new InvalidOperationException($"Endpoint {endpoint} is not registered.");

        return CreateAudioRoute(inp, ep, options);
    }

    public RouteId CreateRoute(InputId input, EndpointId endpoint, VideoRouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_inputs.TryGetValue(input, out var inp))
            throw new InvalidOperationException($"Input {input} is not registered.");
        if (inp.Kind != InputKind.Video)
            throw new InvalidOperationException("Video route options require a video input.");
        if (!_endpoints.TryGetValue(endpoint, out var ep))
            throw new InvalidOperationException($"Endpoint {endpoint} is not registered.");

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
            throw new InvalidOperationException($"Route {id} is not registered.");
        Volatile.Write(ref route.Enabled, enabled);
    }

    // ── IAVRouter: Clock ────────────────────────────────────────────────

    public IMediaClock InternalClock => _internalClock;

    public IMediaClock Clock => _resolvedClock ?? _internalClock;

    public void RegisterClock(IMediaClock clock, ClockPriority priority = ClockPriority.Hardware)
    {
        lock (_clockLock)
        {
            _clockRegistry.RemoveAll(e => ReferenceEquals(e.Clock, clock));
            _clockRegistry.Add((clock, priority, _clockRegistrationOrder++));
            ResolveActiveClock();
        }
        Log.LogInformation("Clock registered: {Type} at priority {Priority} → active={Active}",
            clock.GetType().Name, priority, Clock.GetType().Name);
    }

    public void UnregisterClock(IMediaClock clock)
    {
        lock (_clockLock)
        {
            int removed = _clockRegistry.RemoveAll(e => ReferenceEquals(e.Clock, clock));
            if (removed > 0) ResolveActiveClock();
        }
        Log.LogInformation("Clock unregistered: {Type} → active={Active}",
            clock.GetType().Name, Clock.GetType().Name);
    }

    public void SetClock(IMediaClock? clock)
    {
        lock (_clockLock)
        {
            _clockRegistry.RemoveAll(e => e.Priority == ClockPriority.Override);
            if (clock is not null)
                _clockRegistry.Add((clock, ClockPriority.Override, _clockRegistrationOrder++));
            ResolveActiveClock();
        }
        Log.LogInformation("Clock override {Action}: {Type} → active={Active}",
            clock is null ? "cleared" : "set",
            clock?.GetType().Name ?? "(none)",
            Clock.GetType().Name);
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
            throw new InvalidOperationException($"Input {id} is not registered.");
        entry.Volume = volume;
    }

    public void SetInputTimeOffset(InputId id, TimeSpan offset)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Input {id} is not registered.");
        entry.TimeOffset = offset;
    }

    public void SetInputEnabled(InputId id, bool enabled)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Input {id} is not registered.");
        entry.Enabled = enabled;
    }

    // ── IAVRouter: Per-endpoint control ─────────────────────────────────

    public void SetEndpointGain(EndpointId id, float gain)
    {
        if (!_endpoints.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Endpoint {id} is not registered.");
        entry.Gain = gain;
    }

    // ── IAVRouter: Video ────────────────────────────────────────────────

    public bool VideoLiveMode { get; set; }

    // ── IAVRouter: Diagnostics ──────────────────────────────────────────

    public TimeSpan GetAvDrift(InputId audioInput, InputId videoInput)
    {
        if (!_inputs.TryGetValue(audioInput, out var aEntry) || aEntry.Kind != InputKind.Audio)
            throw new InvalidOperationException("Audio input not found.");
        if (!_inputs.TryGetValue(videoInput, out var vEntry) || vEntry.Kind != InputKind.Video)
            throw new InvalidOperationException("Video input not found.");

        return aEntry.AudioChannel!.Position - vEntry.VideoChannel!.Position;
    }

    public float GetInputPeakLevel(InputId id)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Input {id} is not registered.");
        return entry.PeakLevel;
    }

    public RouterDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        lock (_lock)
        {
            var inputSnapshots = _inputs.Values.Select(i => new InputDiagnostics(
                i.Id, i.Kind.ToString(), i.Enabled, i.Volume, i.PeakLevel, i.TimeOffset)).ToArray();

            var endpointSnapshots = _endpoints.Values.Select(e => new EndpointDiagnostics(
                e.Id, e.Kind.ToString(), e.Gain)).ToArray();

            var routeSnapshots = _routes.Values.Select(r => new RouteDiagnostics(
                r.Id, r.InputId, r.EndpointId, r.Kind.ToString(), r.Enabled, r.Gain,
                r.Resampler is not null)).ToArray();

            return new RouterDiagnosticsSnapshot(
                IsRunning, Clock.Position, VideoLiveMode,
                inputSnapshots, endpointSnapshots, routeSnapshots);
        }
    }

    // ── IDisposable / IAsyncDisposable ──────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        DisposeCore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopAsync().GetAwaiter().GetResult();
        DisposeCore();
    }

    private void DisposeCore()
    {
        _disposed = true;

        // Dispose auto-created resamplers
        foreach (var route in _routes.Values)
        {
            if (route.OwnsResampler)
                route.Resampler?.Dispose();
        }

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
            throw new InvalidOperationException("Endpoint does not support audio.");

        // Validate audio format compatibility if endpoint advertises capabilities
        if (ep.Audio is IFormatCapabilities<AudioFormat> caps && inp.AudioChannel is not null)
        {
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
            throw new InvalidOperationException("Endpoint does not support video.");

        // Validate pixel format compatibility if endpoint advertises capabilities
        if (ep.Video is IFormatCapabilities<PixelFormat> caps && inp.VideoChannel is not null)
        {
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
            };

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

        if (route.Kind == InputKind.Audio)
            RebuildAudioRouteSnapshot();
        else
            RebuildVideoRouteSnapshot();

        Log.LogDebug("Route removed: {Id}", route.Id);
    }

    private void RebuildAudioRouteSnapshot()
    {
        _audioRouteSnapshot = _routes.Values.Where(r => r.Kind == InputKind.Audio).ToArray();
    }

    private void RebuildVideoRouteSnapshot()
    {
        _videoRouteSnapshot = _routes.Values.Where(r => r.Kind == InputKind.Video).ToArray();
    }

    // ── Push tick (drives push endpoints) ───────────────────────────────

    private void PushThreadLoop()
    {
        var cadence = _options.InternalTickCadence;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long audioCadenceTicks = (long)(cadence.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        // Video push runs on its own thread so large frame copies don't block audio delivery.
        var videoThread = new Thread(PushVideoThreadLoop)
        {
            Name = "AVRouter-PushVideo",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        videoThread.Start();

        while (_running)
        {
            long tickStart = sw.ElapsedTicks;

            try
            {
                PushAudioTick();
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Error in audio push tick");
            }

            // Sleep for the remaining cadence time
            long targetTicks = tickStart + audioCadenceTicks;
            long remaining = targetTicks - sw.ElapsedTicks;
            if (remaining > 0)
            {
                int sleepMs = (int)(remaining * 1000L / System.Diagnostics.Stopwatch.Frequency);
                if (sleepMs > 0)
                    Thread.Sleep(sleepMs);
            }
        }

        videoThread.Join(TimeSpan.FromSeconds(2));
    }

    private void PushVideoThreadLoop()
    {
        var cadence = _options.InternalTickCadence;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long videoCadenceTicks = (long)(cadence.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        while (_running)
        {
            long tickStart = sw.ElapsedTicks;

            try
            {
                PushVideoTick();
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Error in video push tick");
            }

            long targetTicks = tickStart + videoCadenceTicks;
            long remaining = targetTicks - sw.ElapsedTicks;
            if (remaining > 0)
            {
                int sleepMs = (int)(remaining * 1000L / System.Diagnostics.Stopwatch.Frequency);
                if (sleepMs > 0)
                    Thread.Sleep(sleepMs);
            }
        }
    }

    private void PushAudioTick()
    {
        var routes = _audioRouteSnapshot;
        if (routes.Length == 0) return;

        // Collect push endpoints that have at least one active route
        // and accumulate all routes into a single buffer per endpoint.
        // Use a dictionary keyed by EndpointId to group routes.
        Span<EndpointId> seenEps = stackalloc EndpointId[0]; // will use list below
        var processedEndpoints = new HashSet<EndpointId>();

        foreach (var ep in _endpoints.Values)
        {
            if (ep.Audio is null or IPullAudioEndpoint) continue;
            if (!processedEndpoints.Add(ep.Id)) continue;

            // Determine format from the first active route's input
            AudioFormat? outFormat = null;
            int framesPerBuffer = 0;

            // First pass: determine output format and buffer size
            foreach (var route in routes)
            {
                if (!Volatile.Read(ref route.Enabled) || route.EndpointId != ep.Id) continue;
                if (!_inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled) continue;
                var fmt = inp.AudioChannel!.SourceFormat;
                if (outFormat is null)
                {
                    outFormat = fmt;
                    framesPerBuffer = _options.DefaultFramesPerBuffer > 0
                        ? _options.DefaultFramesPerBuffer
                        : (int)(fmt.SampleRate * _options.InternalTickCadence.TotalSeconds);
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

                // Second pass: pull from each route, apply map, mix into dest
                foreach (var route in routes)
                {
                    if (!Volatile.Read(ref route.Enabled) || route.EndpointId != ep.Id) continue;
                    if (!_inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled) continue;

                    var channel = inp.AudioChannel!;
                    var srcFormat = channel.SourceFormat;
                    int srcSamples = framesPerBuffer * srcFormat.Channels;

                    var scratch = GetOrCreateScratch(ep.Id, srcSamples);
                    var srcSpan = scratch.AsSpan(0, srcSamples);
                    srcSpan.Clear();

                    int filled = channel.FillBuffer(srcSpan, framesPerBuffer);
                    if (filled == 0) continue;
                    if (filled > maxFilled) maxFilled = filled;

                    var filledSpan = srcSpan[..(filled * srcFormat.Channels)];

                    if (Math.Abs(inp.Volume - 1.0f) > 1e-6f)
                        ApplyGain(filledSpan, inp.Volume);
                    if (Math.Abs(route.Gain - 1.0f) > 1e-6f)
                        ApplyGain(filledSpan, route.Gain);

                    // Per-input peak metering (post-volume, pre-mix)
                    inp.PeakLevel = MeasurePeak(filledSpan);

                    // Apply channel map if present
                    if (route.BakedChannelMap is not null &&
                        srcFormat.Channels != format.Channels)
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

                if (maxFilled > 0)
                {
                    if (Math.Abs(ep.Gain - 1.0f) > 1e-6f)
                        ApplyGain(dest[..(maxFilled * format.Channels)], ep.Gain);

                    ep.Audio.ReceiveBuffer(
                        dest[..(maxFilled * format.Channels)],
                        maxFilled,
                        format);
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

        foreach (var route in routes)
        {
            if (!Volatile.Read(ref route.Enabled)) continue;
            if (!_inputs.TryGetValue(route.InputId, out var inp) || !inp.Enabled) continue;
            if (!_endpoints.TryGetValue(route.EndpointId, out var ep)) continue;
            if (ep.Video is null or IPullVideoEndpoint) continue; // skip pull endpoints

            var channel = inp.VideoChannel!;

            // Check if a pull endpoint already dequeued a frame for this input.
            // If so, forward that frame instead of competing for the ring buffer.
            var lastFrame = inp.LastVideoFrame;
            if (lastFrame.HasValue)
            {
                var f = lastFrame.Value;
                ep.Video.ReceiveFrame(in f);
                continue;
            }

            VideoFrame[] frameBuf = new VideoFrame[1];
            int got = channel.FillBuffer(frameBuf, 1);
            if (got > 0)
            {
                var frame = frameBuf[0];
                // For push video, apply time offset for PTS comparison
                if (!VideoLiveMode && frame.Pts + inp.TimeOffset > clockPos)
                    continue; // frame is in the future, skip for now

                inp.LastVideoFrame = frame;
                ep.Video.ReceiveFrame(in frame);
            }
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

        // Cross-clock drift compensation
        private bool   _hasOrigin;
        private long   _ptsOriginTicks;
        private long   _clockOriginTicks;
        private const double DriftCorrectionGain = 0.005;

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

                var channel = inp.VideoChannel!;

                // Try the cached pending frame first (it was too early last tick)
                VideoFrame candidate;
                bool fromPending = false;
                if (_pendingFrame.HasValue && _pendingInputId == route.InputId)
                {
                    candidate = _pendingFrame.Value;
                    _pendingFrame = null;
                    fromPending = true;
                }
                else
                {
                    VideoFrame[] buf = new VideoFrame[1];
                    int got = channel.FillBuffer(buf, 1);
                    if (got == 0)
                    {
                        // No new frame — re-present the last one if available
                        if (_lastPresentedFrame.HasValue)
                        {
                            frame = _lastPresentedFrame.Value;
                            return true;
                        }
                        continue;
                    }
                    candidate = buf[0];
                }

                // PTS check (unless live mode)
                if (!_router.VideoLiveMode)
                {
                    if (!_hasOrigin)
                    {
                        _ptsOriginTicks   = candidate.Pts.Ticks;
                        _clockOriginTicks = clockPosition.Ticks;
                        _hasOrigin = true;
                    }

                    long relativePtsTicks   = candidate.Pts.Ticks - _ptsOriginTicks + inp.TimeOffset.Ticks;
                    long relativeClockTicks  = clockPosition.Ticks - _clockOriginTicks;

                    if (relativePtsTicks > relativeClockTicks + TimeSpan.TicksPerMillisecond * 5)
                    {
                        // Too early — cache for next tick instead of losing it
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

                    // Drift correction
                    long errorTicks = relativePtsTicks - relativeClockTicks;
                    _ptsOriginTicks += (long)(errorTicks * DriftCorrectionGain);

                    _lastPresentedRelativePts = relativePtsTicks;
                }

                _lastPresentedFrame = candidate;
                inp.LastVideoFrame = candidate;
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
        if (_scratchBuffers.TryGetValue(id, out var buf) && buf.Length >= minSize)
            return buf;

        var newBuf = new float[minSize];
        _scratchBuffers[id] = newBuf;
        return newBuf;
    }

    /// <summary>
    /// Scatters interleaved source samples into interleaved destination samples
    /// using a pre-baked channel route table.
    /// </summary>
    private static void ApplyChannelMap(
        ReadOnlySpan<float> src, Span<float> dest,
        (int dstCh, float gain)[][] bakedRoutes,
        int srcChannels, int dstChannels, int frameCount)
    {
        for (int f = 0; f < frameCount; f++)
        {
            int srcBase = f * srcChannels;
            int dstBase = f * dstChannels;

            for (int srcCh = 0; srcCh < bakedRoutes.Length; srcCh++)
            {
                float sample = src[srcBase + srcCh];
                var targets = bakedRoutes[srcCh];
                for (int t = 0; t < targets.Length; t++)
                {
                    var (dstCh, gain) = targets[t];
                    if (dstCh < dstChannels)
                        dest[dstBase + dstCh] += sample * gain;
                }
            }
        }
    }

    private static void ApplyGain(Span<float> buffer, float gain)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
        {
            var vGain = new Vector<float>(gain);
            int simdLen = Vector<float>.Count;
            for (; i + simdLen <= buffer.Length; i += simdLen)
            {
                var v = new Vector<float>(buffer[i..]);
                (v * vGain).CopyTo(buffer[i..]);
            }
        }
        for (; i < buffer.Length; i++)
            buffer[i] *= gain;
    }

    private static void MixInto(Span<float> dest, ReadOnlySpan<float> src)
    {
        int len = Math.Min(dest.Length, src.Length);
        int i = 0;
        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count)
        {
            int simdLen = Vector<float>.Count;
            for (; i + simdLen <= len; i += simdLen)
            {
                var d = new Vector<float>(dest[i..]);
                var s = new Vector<float>(src[i..]);
                (d + s).CopyTo(dest[i..]);
            }
        }
        for (; i < len; i++)
            dest[i] += src[i];
    }

    private static float MeasurePeak(ReadOnlySpan<float> buffer)
    {
        float peak = 0f;
        int i = 0;
        if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
        {
            var vMax = Vector<float>.Zero;
            int simdLen = Vector<float>.Count;
            for (; i + simdLen <= buffer.Length; i += simdLen)
            {
                var v = Vector.Abs(new Vector<float>(buffer[i..]));
                vMax = Vector.Max(vMax, v);
            }
            for (int j = 0; j < Vector<float>.Count; j++)
                peak = Math.Max(peak, vMax[j]);
        }
        for (; i < buffer.Length; i++)
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        return peak;
    }
}

