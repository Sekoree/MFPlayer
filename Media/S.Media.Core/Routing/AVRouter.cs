using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
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
        // Last gain actually applied to audio leaving this input. Written only
        // on the RT/push thread that last consumed the input's channel; each
        // callback ramps from this value toward <see cref="Volume"/> to avoid
        // zipper noise when the user changes volume between callbacks. Races
        // between multiple endpoints sharing one input are benign — each
        // converges toward the same target.
        public float AppliedVolume = 1.0f;
        // §3.18 / B13: long-backed for Interlocked atomicity; TimeOffset is a wrapper.
        public long TimeOffsetTicks;
        public TimeSpan TimeOffset => TimeSpan.FromTicks(Interlocked.Read(ref TimeOffsetTicks));
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

        // §4.15 / R24, M3 — per-endpoint peak level measured post-map and
        // post-gain, immediately before ReceiveBuffer. Reflects what the
        // endpoint actually consumes (channel map attenuation, endpoint
        // gain) — more meaningful for a VU meter than the pre-map
        // per-input reading.
        public float PeakLevel;

        // §4.13 / M2 — running total of samples that exceeded ±1.0 pre-clip.
        // Rolled over each tick via a swap: `OverflowSamplesThisTick` feeds
        // the diagnostics snapshot and is cleared on read; the cumulative
        // total lives in `OverflowSamplesTotal`.
        public long OverflowSamplesTotal;
        public long OverflowSamplesThisTick;


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
        public bool IsLeaderInput; // §6.4 — prefer this route's PTS for push buffer timecode
        public AudioFormat? OriginalAudioFormat; // §6.5 — format at route creation time
        public AudioFormat? LastSeenAudioFormat; // §6.5 — last observed format (for change detection)

        // Video-specific: per-route subscription into the input channel's frame stream.
        // Each (input, endpoint) pair owns its own bounded queue — the decoder fans
        // each frame out to every subscription, so pull (SDL3/Avalonia) and push
        // (NDI, clone sinks) no longer race for frames on a shared ring.
        public IVideoSubscription? VideoSub;
        // §6.1 / R23 — per-route live-mode bypass; set from VideoRouteOptions.LiveMode.
        public bool LiveMode;

        // §6.2 / R14 — per-route PTS drift state. Two separate trackers because the
        // push tick thread and the pull render thread have independent phase origins and
        // run at different cadences; sharing one tracker would corrupt both paths.
        public readonly PtsDriftTracker PushDrift = new();
        public readonly PtsDriftTracker PullDrift  = new();

        // Pull-path per-route cache: frame that was too early last tick; last successfully
        // presented frame for re-display when the ring is momentarily empty.
        // Written only from the render thread — no synchronisation needed.
        public VideoFrame? PullPendingFrame;
        public VideoFrame? PullLastPresentedFrame;

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
    // Only the audio side is cached: PushVideoTick iterates the flat route
    // snapshot directly (one subscription per route), so a per-endpoint group
    // would be unused overhead.
    private volatile Dictionary<EndpointId, RouteEntry[]> _audioRoutesByEndpoint =
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
    // §3.26 / P3 — per-endpoint output scratch sized to the endpoint's output
    // format × FramesPerBuffer. The pull-audio callback path used to rent two
    // pooled buffers per RT tick (one for the resampler output, one for the
    // channel-map output); both are now served from this pre-rented buffer so
    // the RT thread never hits `ArrayPool.Rent` in steady state. Writes are
    // serialised by PortAudio's callback contract (one Fill invocation per
    // endpoint at a time), so a single shared buffer per endpoint is safe —
    // the push-tick path still rents from the pool because it can overlap
    // multiple endpoints on one tick thread.
    private readonly ConcurrentDictionary<EndpointId, float[]> _outputScratchBuffers = new();
    // §8.4 — push-audio destination/mapped scratch caches per endpoint.
    // Replaces per-tick ArrayPool.Rent/Return for `destBuf` and `mappedBuf`.
    // PushAudioTick is single-threaded, so one mutable buffer per endpoint is
    // sufficient and safe.
    private readonly ConcurrentDictionary<EndpointId, float[]> _pushDestScratchBuffers = new();
    private readonly ConcurrentDictionary<EndpointId, float[]> _pushMappedScratchBuffers = new();

    // Per-endpoint fractional frame accumulator for time-aware push audio.
    // Prevents truncation drift when computing frame counts from elapsed seconds.
    private readonly ConcurrentDictionary<EndpointId, double> _pushAudioFrameAccumulators = new();

    // Per-route pending video frame for push endpoints.
    // When a frame is pulled from the subscription but its PTS is ahead of the clock,
    // it is cached here instead of being dropped.  The next push tick retries it.
    // Keyed by RouteId (not InputId) so N push endpoints on the same input each own
    // their own pending — otherwise they would clobber each other's gate-cache.
    private readonly ConcurrentDictionary<RouteId, VideoFrame> _pushVideoPending = new();

    // §6.2 / R14: push + pull drift trackers moved onto RouteEntry (PushDrift / PullDrift)
    // so each route has independent PTS-origin state regardless of how many routes share
    // the same endpoint. _pushVideoDrift ConcurrentDictionary removed.
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
        // §5.5 / §6.7 — seed both effective cadences from the options so the
        // push loops have valid values even before any endpoint registers
        // (the router may tick briefly with no endpoints during Start/Stop).
        _effectiveAudioCadenceSwTicks = ToSwTicks(_options.AudioTickCadence ?? _options.InternalTickCadence);
        _effectiveVideoCadenceSwTicks = ToSwTicks(_options.VideoTickCadence ?? _options.InternalTickCadence);
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

        // §5.4 — optional audio preroll: hold the caller in StartAsync until
        // every audio input has decoded enough frames for the first video tick
        // to hit the screen without an AV-sync glitch. Only activates when the
        // graph actually has both audio and a pull-video endpoint (no race to
        // protect against otherwise) and MinBufferedFramesPerInput > 0.
        // Runs OUTSIDE _lock so RegisterEndpoint / CreateRoute calls during
        // preroll aren't blocked.
        return WaitForAudioPrerollAsync(ct);
    }

    private async Task WaitForAudioPrerollAsync(CancellationToken ct)
    {
        int minFrames = _options.MinBufferedFramesPerInput;
        if (minFrames <= 0) return;

        // Snapshot inputs + endpoints once; preroll is a one-shot at Start time.
        var audioInputs = _inputs.Values
            .Where(e => e.Kind == InputKind.Audio && e.AudioChannel is not null)
            .Select(e => e.AudioChannel!)
            .ToArray();
        bool hasPullVideoEndpoint = _endpointsSnapshot
            .Any(e => e.Video is IPullVideoEndpoint);

        if (audioInputs.Length == 0 || !hasPullVideoEndpoint) return;

        var deadline = _options.WaitForAudioPreroll;
        if (deadline <= TimeSpan.Zero) return;

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(deadline);
        var start = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!deadlineCts.IsCancellationRequested)
            {
                bool allReady = true;
                foreach (var ch in audioInputs)
                {
                    if (ch.BufferAvailable < minFrames) { allReady = false; break; }
                }
                if (allReady)
                {
                    Log.LogDebug("Audio preroll reached {Frames} frames on {Count} input(s) in {Ms}ms.",
                        minFrames, audioInputs.Length, start.ElapsedMilliseconds);
                    return;
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(10), deadlineCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            if (deadlineCts.IsCancellationRequested)
            {
                Log.LogWarning("Audio preroll deadline ({DeadlineMs}ms) hit with {Count} input(s) still below {MinFrames} frames; starting anyway.",
                    deadline.TotalMilliseconds, audioInputs.Length, minFrames);
            }
        }
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
            _outputScratchBuffers.TryRemove(id, out _);
            _pushDestScratchBuffers.TryRemove(id, out _);
            _pushMappedScratchBuffers.TryRemove(id, out _);
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
        RecomputeEffectiveCadence();
    }

    // §5.5 — effective push-tick cadences. Starts at the options value and is
    // recomputed on every Register/Unregister: when any registered endpoint
    // advertises a lower NominalTickCadence, the router picks that so a fast
    // hardware endpoint isn't held back by the 10 ms default. Read as stopwatch
    // ticks (monotonic) in the push loops so we never pay the TimeSpan math cost
    // on the RT path.
    //
    // §6.7 — split audio + video cadence so a mixed graph can run audio at
    // 5 ms ticks while video runs at the frame cadence (~16.7 ms @ 60 fps).
    // The audio derivation considers only audio-capable endpoint hints; the
    // video derivation considers only video-capable ones.
    private long _effectiveAudioCadenceSwTicks;
    private long _effectiveVideoCadenceSwTicks;

    private static long ToSwTicks(TimeSpan ts)
        => (long)(ts.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    private void RecomputeEffectiveCadence()
    {
        // §6.7 — derive audio and video cadence independently. Options
        // precedence: AudioTickCadence / VideoTickCadence when set, else
        // InternalTickCadence for both. Endpoint hints only lower the value,
        // never raise it.
        TimeSpan audioBase = _options.AudioTickCadence ?? _options.InternalTickCadence;
        TimeSpan videoBase = _options.VideoTickCadence ?? _options.InternalTickCadence;
        TimeSpan audioBest = audioBase;
        TimeSpan videoBest = videoBase;

        foreach (var ep in _endpointsSnapshot)
        {
            if (ep.Audio?.NominalTickCadence is { } a && a < audioBest)
                audioBest = a < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : a;
            if (ep.Video?.NominalTickCadence is { } v && v < videoBest)
                videoBest = v < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : v;
        }

        Volatile.Write(ref _effectiveAudioCadenceSwTicks, ToSwTicks(audioBest));
        Volatile.Write(ref _effectiveVideoCadenceSwTicks, ToSwTicks(videoBest));
    }

    /// <summary>
    /// §5.5 / §6.7 — current effective audio push-tick cadence. Combines
    /// <c>AVRouterOptions.AudioTickCadence</c> (or
    /// <c>InternalTickCadence</c> fallback) with the lowest
    /// <c>IAudioEndpoint.NominalTickCadence</c> across registered audio
    /// endpoints. Exposed for diagnostics and tests.
    /// </summary>
    public TimeSpan EffectiveAudioTickCadence
    {
        get
        {
            long swTicks = Volatile.Read(ref _effectiveAudioCadenceSwTicks);
            return swTicks > 0
                ? TimeSpan.FromSeconds((double)swTicks / System.Diagnostics.Stopwatch.Frequency)
                : (_options.AudioTickCadence ?? _options.InternalTickCadence);
        }
    }

    /// <summary>
    /// §5.5 / §6.7 — current effective video push-tick cadence. Video sibling
    /// of <see cref="EffectiveAudioTickCadence"/>.
    /// </summary>
    public TimeSpan EffectiveVideoTickCadence
    {
        get
        {
            long swTicks = Volatile.Read(ref _effectiveVideoCadenceSwTicks);
            return swTicks > 0
                ? TimeSpan.FromSeconds((double)swTicks / System.Diagnostics.Stopwatch.Frequency)
                : (_options.VideoTickCadence ?? _options.InternalTickCadence);
        }
    }

    /// <summary>
    /// §5.5 / §6.7 — legacy accessor equivalent to
    /// <see cref="EffectiveAudioTickCadence"/> (audio is the primary push
    /// driver and matches the historical single-cadence behaviour). Kept
    /// for existing callers and tests; new code should use the audio /
    /// video sibling explicitly.
    /// </summary>
    public TimeSpan EffectiveTickCadence => EffectiveAudioTickCadence;

    /// <summary>
    /// §3.14 / R6: pre-allocate the per-endpoint scratch buffer at registration
    /// time using the endpoint's preferred frame count and channel count, so the
    /// first push/fill tick never hits <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, Func{TKey, TValue}, Func{TKey, TValue, TValue})"/>
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

        // §3.26 / P3 — pre-rent the output-format scratch for pull endpoints so
        // the resampler and channel-map paths in AudioFillCallbackForEndpoint.Fill
        // never hit ArrayPool.Rent on the RT thread. Size matches the input
        // scratch (both are frames×channels-bounded) so one constant covers both.
        if (audio is IPullAudioEndpoint)
            _outputScratchBuffers[id] = new float[minSize];
        else
        {
            // §8.4 — push endpoints get dedicated destination + channel-map
            // scratch upfront so PushAudioTick does not rent from ArrayPool on
            // every tick.
            _pushDestScratchBuffers[id] = new float[minSize];
            _pushMappedScratchBuffers[id] = new float[minSize];
        }
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

    /// <inheritdoc />
    public event EventHandler<RouteFormatMismatchEventArgs>? RouteFormatMismatch;

    /// <inheritdoc />
    public event Action<RouterDiagnosticsSnapshot>? AVRouterDiagnostics;

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
        // §6.9 — per-pair EMA so multiple A/V pairings can each track their
        // own drift independently (promotes the single-pair fix from §3.54).
        const double alpha = 0.4;
        var state = _driftEmaStates.GetOrAdd((audioInput, videoInput), static _ => new DriftEmaState());
        lock (state)
        {
            if (!state.Valid)
            {
                state.EmaTicks = raw;
                state.Valid = true;
            }
            else
            {
                state.EmaTicks = (long)(state.EmaTicks * (1 - alpha) + raw * alpha);
            }
            return TimeSpan.FromTicks(state.EmaTicks);
        }
    }

    // §6.9 — per-pair EMA state for GetAvDrift. Each (audioInput, videoInput) pair
    // gets its own filter so callers polling different pairings in the same graph
    // (e.g. two NDI sources with independent A/V sync) don't contaminate each other.
    private sealed class DriftEmaState { public long EmaTicks; public bool Valid; }
    private readonly ConcurrentDictionary<(InputId Audio, InputId Video), DriftEmaState> _driftEmaStates = new();

    public float GetInputPeakLevel(InputId id)
    {
        if (!_inputs.TryGetValue(id, out var entry))
            throw new MediaRoutingException($"Input {id} is not registered.");
        return entry.PeakLevel;
    }

    /// <summary>
    /// §4.15 / R24, M3 — peak level currently delivered to the given audio
    /// endpoint, measured post-channel-map and post-endpoint-gain. Returns
    /// 0 for video-only endpoints.
    /// </summary>
    public float GetEndpointPeakLevel(EndpointId id)
    {
        if (!_endpoints.TryGetValue(id, out var entry))
            throw new MediaRoutingException($"Endpoint {id} is not registered.");
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
            e.Id, e.Kind.ToString(), e.Gain, e.PeakLevel,
            Interlocked.Read(ref e.OverflowSamplesTotal))).ToArray();

        var routeSnapshots = _routes.Values.Select(r => new RouteDiagnostics(
            r.Id, r.InputId, r.EndpointId, r.Kind.ToString(), r.Enabled, r.Gain,
            r.TimeOffset,
            r.Resampler is not null,
            r.LiveMode,
            r.Kind == InputKind.Video ? r.PushDrift.Snapshot() : null,
            r.Kind == InputKind.Video ? r.PullDrift.Snapshot() : null)).ToArray();

        return new RouterDiagnosticsSnapshot(
            IsRunning, Clock.Position,
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
            // The endpoint itself holds the callback via FillCallback; no router-side
            // keepalive is needed because _endpoints[id] = entry already roots entry.
            pull.FillCallback = new AudioFillCallbackForEndpoint(this, entry);
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

        // Validate audio format compatibility if endpoint advertises capabilities.
        // ReSharper disable once SuspiciousTypeConversion.Global — no concrete
        // IAudioEndpoint in-tree implements IFormatCapabilities<AudioFormat> yet,
        // but the contract is public so forward-compat implementations (user code
        // or future endpoint assemblies) may light it up.
        if (ep.Audio is IFormatCapabilities<AudioFormat> caps && inp.AudioChannel is not null)
        {
            // §6.10 / R22 / CH9 — promoted from Debug.Assert to throw; an endpoint
            // that implements IFormatCapabilities<AudioFormat> but returns a null
            // list is a contract bug and the router cannot fulfil the route.
            if (caps.SupportedFormats is null)
                throw new MediaRoutingException(
                    $"Endpoint {ep.Id} implements IFormatCapabilities<AudioFormat> " +
                    "but returned null SupportedFormats. See §6.10 / R22 / CH9.");
            var srcFormat = inp.AudioChannel.SourceFormat;
            if (caps.SupportedFormats.Count == 0)
            {
                // Documented contract: empty list == "unknown, try anything" —
                // log at Debug so the warning isn't noisy but the configuration
                // is still discoverable.
                Log.LogDebug(
                    "Audio route {Input}→{Endpoint}: endpoint advertises empty SupportedFormats " +
                    "(§6.10 contract: treated as \"accept anything\").", inp.Id, ep.Id);
            }
            else if (!caps.SupportedFormats.Contains(srcFormat))
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
            // §6.5 — capture the source format at creation time for mismatch detection.
            var originalFmt = inp.AudioChannel?.SourceFormat;
            var route = new RouteEntry(routeId, inp.Id, ep.Id, InputKind.Audio)
            {
                Gain = options.Gain,
                TimeOffset = options.TimeOffset,
                ChannelMap = options.ChannelMap,
                Resampler = options.Resampler,
                IsLeaderInput = options.IsLeaderInput,
                OriginalAudioFormat = originalFmt,
                LastSeenAudioFormat = originalFmt,
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

            // §10.2 / EL2 — correlation scope: the creation log record
            // carries { RouteId, InputId, EndpointId } so consumers can
            // filter the audit trail without reparsing the message.
            using var _ = Log.BeginScope(new Dictionary<string, object>
            {
                ["RouteId"] = routeId.ToString(),
                ["InputId"] = inp.Id.ToString(),
                ["EndpointId"] = ep.Id.ToString(),
            });
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
            // §6.10 / R22 / CH9 — same promotion as the audio path above.
            if (caps.SupportedFormats is null)
                throw new MediaRoutingException(
                    $"Endpoint {ep.Id} implements IFormatCapabilities<PixelFormat> " +
                    "but returned null SupportedFormats. See §6.10 / R22 / CH9.");
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
                LiveMode = options.LiveMode, // §6.1 / R23
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
                // §5.6 — route-level override of capacity + overflow policy.
                // Defaults are the historical behaviour (pull = Wait + deep
                // queue, push = DropOldest + 4); callers can pin either value
                // via VideoRouteOptions.
                int defaultCapacity = isPull
                    ? Math.Max(_options.DefaultFramesPerBuffer, inp.VideoChannel.BufferDepth)
                    : 4;
                int capacity = options.Capacity is int c ? Math.Max(1, c) : defaultCapacity;
                VideoOverflowPolicy overflow = options.OverflowPolicy
                    ?? (isPull ? VideoOverflowPolicy.Wait : VideoOverflowPolicy.DropOldest);

                route.VideoSub = inp.VideoChannel.Subscribe(new VideoSubscriptionOptions(
                    Capacity: capacity,
                    OverflowPolicy: overflow,
                    DebugName: $"{inp.Id}->{ep.Id}"));
            }

            _routes[routeId] = route;
            RebuildVideoRouteSnapshot();

            // §5.3 — auto-propagate color-matrix hint from source → endpoint at
            // route creation so callers don't have to thread it by hand. Only
            // fires when both sides opt in; the receiver is expected to treat
            // Auto/Auto as "no change" and to preserve any explicit value the
            // caller previously set via a concrete property.
            if (inp.VideoChannel is IVideoColorMatrixHint hint &&
                ep.Video is IVideoColorMatrixReceiver sink)
            {
                try
                {
                    sink.ApplyColorMatrixHint(hint.SuggestedYuvColorMatrix, hint.SuggestedYuvColorRange);
                }
                catch (Exception ex)
                {
                    Log.LogWarning(ex,
                        "ApplyColorMatrixHint threw on endpoint {Endpoint}; continuing with endpoint defaults.",
                        ep.Id);
                }
            }

            // §10.2 / EL2 — correlation scope (video sibling).
            using var _ = Log.BeginScope(new Dictionary<string, object>
            {
                ["RouteId"] = routeId.ToString(),
                ["InputId"] = inp.Id.ToString(),
                ["EndpointId"] = ep.Id.ToString(),
            });
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
        // Release push-pending cache and pull-path per-route state.
        if (_pushVideoPending.TryRemove(route.Id, out var pending))
            pending.MemoryOwner?.Dispose();
        // §6.2 / R14: drift trackers live on RouteEntry — nothing to remove from a dict.
        // Release the pull-path cached frames to avoid ref-count leaks.
        route.PullPendingFrame?.MemoryOwner?.Dispose();
        route.PullPendingFrame = null;
        // PullLastPresentedFrame is a re-display copy and not ref-counted from here.
        route.PullLastPresentedFrame = null;
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
        var sw = System.Diagnostics.Stopwatch.StartNew();

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
                EmitDiagnosticsIfSubscribed();
            }
            catch (Exception ex)
            {
                long n = Interlocked.Increment(ref _pushAudioErrorCount);
                if (n <= 3 || n % 100 == 0)
                    Log.LogError(ex, "Error in audio push tick (count={Count})", n);
            }

            // §5.5 / §6.7 — re-read the audio cadence each tick so a
            // register/unregister (or options change) can speed up / slow
            // down the audio push without bouncing the router.
            long audioCadenceTicks = Volatile.Read(ref _effectiveAudioCadenceSwTicks);

            // Sleep for the remaining cadence time — cancellation-aware so StopAsync
            // unblocks us in tens of microseconds (§3.19 / R19+R20).
            long targetTicks = tickStart + audioCadenceTicks;
            WaitUntil(sw, targetTicks, ct);
        }

        videoThread.Join(TimeSpan.FromSeconds(2));
    }

    private void PushVideoThreadLoop(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (_running && !ct.IsCancellationRequested)
        {
            // §5.5 / §6.7 — re-read per-tick so endpoint Register/Unregister
            // reshapes the video push cadence without a Stop/Start bounce.
            long videoCadenceTicks = Volatile.Read(ref _effectiveVideoCadenceSwTicks);
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

    private void EmitDiagnosticsIfSubscribed()
    {
        var handler = AVRouterDiagnostics;
        if (handler is null) return;

        RouterDiagnosticsSnapshot snapshot;
        try
        {
            snapshot = GetDiagnosticsSnapshot();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to build diagnostics snapshot for AVRouterDiagnostics.");
            return;
        }

        try
        {
            handler(snapshot);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "AVRouterDiagnostics subscriber threw; continuing.");
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
            var destBuf = GetOrCreatePushDestScratch(ep.Id, destSamples);
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

                // §6.5 — detect format changes at runtime and fire once per change.
                if (route.OriginalAudioFormat.HasValue &&
                    route.LastSeenAudioFormat.HasValue &&
                    srcFormat != route.LastSeenAudioFormat.Value)
                {
                    route.LastSeenAudioFormat = srcFormat;
                    RouteFormatMismatch?.Invoke(this, new RouteFormatMismatchEventArgs(
                        route.Id, route.InputId, route.EndpointId,
                        route.OriginalAudioFormat, srcFormat));
                }

                    // §6.8 — per-route resampler on the push path. Rates that
                    // don't match the endpoint format are handled by the
                    // route's Resampler when present; otherwise the route is
                    // skipped with a log-once warning (preserves the prior
                    // "homogeneous format required" contract for routes the
                    // caller did not opt into resampling on).
                bool needsResample = srcFormat.SampleRate != format.SampleRate;
                if (needsResample && route.Resampler is null)
                {
                    // Log-once per route via a HashSet we already track
                    // for this exact purpose.
                    if (_pushAudioFormatMismatchWarnings.TryAdd(route.Id, 0))
                    {
                        // §10.2 / EL2 — correlation scope so filters on
                        // RouteId = X surface this warning alongside the
                        // route's creation / removal records.
                        using var _corrScope = Log.BeginRouteScope(route.Id);
                        Log.LogWarning(
                            "Push audio route {Route} ({Input}->{Endpoint}) source rate " +
                            "{SrcRate}Hz != endpoint rate {DstRate}Hz and no Resampler " +
                            "is wired — frames dropped. Set AudioRouteOptions.Resampler " +
                            "to enable push-path rate conversion (§6.8).",
                            route.Id, route.InputId, route.EndpointId,
                            srcFormat.SampleRate, format.SampleRate);
                    }
                    continue;
                }

                    // Channel-count conversion without a map also means we
                    // have no way to produce the right layout — skip.
                if (srcFormat.Channels != format.Channels && route.BakedChannelMap is null)
                    continue;

                    // Pull size: when resampling, ask the resampler how many
                    // input frames it needs to produce `framesPerBuffer`
                    // output frames (accounts for internally-buffered phase).
                int inputFrames = needsResample
                    ? route.Resampler!.GetRequiredInputFrames(framesPerBuffer, srcFormat, format.SampleRate)
                    : framesPerBuffer;
                int srcSamples = inputFrames * srcFormat.Channels;

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

                int filled = channel.FillBuffer(srcSpan, inputFrames);
                if (filled == 0) continue;

                    // §6.4 — leader-flagged route's PTS takes priority over first-active.
                var routePts = ptsBeforeFill + inp.TimeOffset + route.TimeOffset;
                if (route.IsLeaderInput || bufferPts == TimeSpan.MinValue)
                    bufferPts = routePts;

                var filledSpan = srcSpan[..(filled * srcFormat.Channels)];

                ApplyInputVolumeRamped(filledSpan, inp, srcFormat.Channels);
                if (Math.Abs(route.Gain - 1.0f) > 1e-6f)
                    ApplyGain(filledSpan, route.Gain);

                    // Per-input peak metering (post-volume, pre-mix, pre-resample)
                inp.PeakLevel = MeasurePeak(filledSpan);

                    // §6.8 — apply the resampler if wired. The output
                    // buffer is sized for `framesPerBuffer` frames at the
                    // source channel count; channel-map conversion runs on
                    // the resampled output. `srcFramesOut` tracks the frame
                    // count *after* resample for mix / maxFilled accounting.
                ReadOnlySpan<float> outSpan = filledSpan;
                int srcFramesOut = filled;
                float[]? resampledBuf = null;
                if (needsResample)
                {
                    int rsSamples = framesPerBuffer * srcFormat.Channels;
                    resampledBuf = ArrayPool<float>.Shared.Rent(rsSamples);
                    var rsSpan = resampledBuf.AsSpan(0, rsSamples);
                    rsSpan.Clear();
                    int outFrames = route.Resampler!.Resample(filledSpan, rsSpan, srcFormat, format.SampleRate);
                    outSpan = rsSpan[..(outFrames * srcFormat.Channels)];
                    srcFramesOut = outFrames;
                }
                if (srcFramesOut > maxFilled) maxFilled = srcFramesOut;

                try
                {
                    // §3.15 / R4: apply the baked channel map whenever one is
                    // supplied, not only when channel counts differ — user-
                    // defined maps can also reorder / attenuate equal-channel
                    // streams (e.g. stereo L↔R swap, mid-side encode).
                    if (route.BakedChannelMap is not null)
                    {
                        int mappedSamples = srcFramesOut * format.Channels;
                        var mappedBuf = GetOrCreatePushMappedScratch(ep.Id, mappedSamples);
                        var mapped = mappedBuf.AsSpan(0, mappedSamples);
                        mapped.Clear();
                        ApplyChannelMap(outSpan, mapped, route.BakedChannelMap,
                            srcFormat.Channels, format.Channels, srcFramesOut);
                        MixInto(dest, mapped);
                    }
                    else
                    {
                        MixInto(dest, outSpan);
                    }
                }
                finally
                {
                    if (resampledBuf is not null)
                        ArrayPool<float>.Shared.Return(resampledBuf);
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
                var deliverSpan = dest[..(maxFilled * format.Channels)];
                if (Math.Abs(ep.Gain - 1.0f) > 1e-6f)
                    ApplyGain(deliverSpan, ep.Gain);

                // §4.13 / M2 — overflow diagnostic: count samples whose
                // absolute value would clip on a conventional DAC, then
                // optionally round them off with the mixer's soft-clip
                // curve. Count is recorded pre-soft-clip so diagnostics
                // reflect what the raw mix tried to produce.
                int overflows = DefaultAudioMixer.Instance.CountOverflows(deliverSpan);
                if (overflows > 0)
                {
                    Interlocked.Add(ref ep.OverflowSamplesTotal, overflows);
                    Interlocked.Exchange(ref ep.OverflowSamplesThisTick, overflows);
                }
                if (_options.SoftClipThreshold is float t && overflows > 0)
                    DefaultAudioMixer.Instance.ApplySoftClip(deliverSpan, t);

                // §4.15 / R24, M3 — per-endpoint peak meter sampled after
                // channel-map, endpoint-gain and (optional) soft-clip,
                // just before ReceiveBuffer.
                ep.PeakLevel = MeasurePeak(deliverSpan);

                ep.Audio.ReceiveBuffer(deliverSpan, maxFilled, format, bufferPts);
            }
        }
    }

    private void PushVideoTick()
    {
        var routes = _videoRouteSnapshot;
        if (routes.Length == 0) return;

        var clockPos = Clock.Position;
        long earlyToleranceTicks = _options.VideoPtsEarlyTolerance.Ticks;
        long discontinuityResetTicks = _options.VideoPtsDiscontinuityResetThreshold.Ticks;
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
            if (!route.LiveMode)
            {
                // §6.2 / R14: use route.PushDrift (per-route on RouteEntry) instead of
                // a ConcurrentDictionary lookup — O(1) field access, no GC pressure.
                var drift = route.PushDrift;

                // Seed each origin from its own domain. A previous revision clamped
                // the clock origin up to the PTS value whenever clockPos < pts, which
                // only worked for self-fed clocks that were about to jump (e.g. an
                // NDIClock on the first frame). For wall-clock masters (PortAudio,
                // StopwatchClock) that clamp produced a permanent huge negative skew
                // and stuck the pipeline on the first frame.
                bool pushFirstSeed = !drift.HasOrigin;
                drift.SeedIfNeeded(candidate.Pts.Ticks, clockPos.Ticks);

                long relativeClockTicks = drift.RelativeClock(clockPos.Ticks);
                long routeOffsetTicks   = inp.TimeOffset.Ticks + route.TimeOffset.Ticks;
                long relativePtsTicks   = drift.RelativePts(candidate.Pts.Ticks, routeOffsetTicks);

                // Large timestamp discontinuity (live sender restart, profile/FPS
                // switch, source seek): re-seed the drift origin so scheduled mode
                // does not stall for the full jump duration.
                if (!pushFirstSeed && discontinuityResetTicks > 0)
                {
                    long errorTicks = relativePtsTicks - relativeClockTicks;
                    if (Math.Abs(errorTicks) > discontinuityResetTicks)
                    {
                        drift.Reset();
                        drift.SeedIfNeeded(candidate.Pts.Ticks, clockPos.Ticks);
                        relativeClockTicks = drift.RelativeClock(clockPos.Ticks);
                        relativePtsTicks = drift.RelativePts(candidate.Pts.Ticks, routeOffsetTicks);
                        pushFirstSeed = true;
                    }
                }

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

            // §3.11 / B15+B16+R18+CH7 — forward through the ref-counted handle
            // overload. Endpoints that need the data past the call call
            // `handle.Retain()` inside ReceiveFrame; this Release below drops the
            // router's implicit ref, and the rental returns to the pool only once
            // every other subscriber has released as well.
            var handle = new VideoFrameHandle(in candidate);
            ep.Video.ReceiveFrame(in handle);
            handle.Release();
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

                // Apply input volume with a one-callback ramp so user volume
                // changes arrive without zipper noise.
                ApplyInputVolumeRamped(filledSpan, inp, srcFormat.Channels);

                // Apply route gain
                if (Math.Abs(route.Gain - 1.0f) > 1e-6f)
                    ApplyGain(filledSpan, route.Gain);

                // Per-input peak metering (post-volume, pre-mix)
                inp.PeakLevel = MeasurePeak(filledSpan);

                // Apply channel map + resample, then mix into dest.
                // §3.26 / P3 — the two per-route output buffers (resampler and
                // channel-map) are served from the endpoint's pre-rented output
                // scratch, so the RT thread does not call ArrayPool.Rent in
                // steady state. The two paths are mutually exclusive per route,
                // and PortAudio serialises Fill invocations per stream, so one
                // shared scratch is safe.
                if (route.Resampler is not null)
                {
                    int outSamples = frameCount * endpointFormat.Channels;
                    var outScratch = _router.GetOrCreateOutputScratch(_endpoint.Id, outSamples);
                    var resampledBuf = outScratch.AsSpan(0, outSamples);
                    resampledBuf.Clear();
                    int outFrames = route.Resampler.Resample(
                        filledSpan, resampledBuf, srcFormat, endpointFormat.SampleRate);
                    // Channel map is already handled by resampler output format
                    MixInto(dest, resampledBuf[..(outFrames * endpointFormat.Channels)]);
                }
                else if (route.BakedChannelMap is not null)
                {
                    // Apply channel routing: scatter src channels → dest channels
                    int mappedSamples = filled * endpointFormat.Channels;
                    var outScratch = _router.GetOrCreateOutputScratch(_endpoint.Id, mappedSamples);
                    var mapped = outScratch.AsSpan(0, mappedSamples);
                    mapped.Clear();
                    ApplyChannelMap(filledSpan, mapped, route.BakedChannelMap,
                        srcFormat.Channels, endpointFormat.Channels, filled);
                    MixInto(dest, mapped);
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

            // §4.13 / M2 — overflow + optional soft-clip, mirroring the
            // push-path so RT-callback endpoints see the same protection.
            int overflows = DefaultAudioMixer.Instance.CountOverflows(dest);
            if (overflows > 0)
            {
                Interlocked.Add(ref _endpoint.OverflowSamplesTotal, overflows);
                Interlocked.Exchange(ref _endpoint.OverflowSamplesThisTick, overflows);
            }
            if (_router._options.SoftClipThreshold is float t && overflows > 0)
                DefaultAudioMixer.Instance.ApplySoftClip(dest, t);

            // §4.15 / R24, M3 — per-endpoint peak meter sampled post-map,
            // post-gain, post-soft-clip. Matches the push-path symmetry so
            // VU meters read the same signal the endpoint consumes
            // regardless of driving mode (RT callback vs router push).
            _endpoint.PeakLevel = MeasurePeak(dest);
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

        // §6.2 / R14: per-route state (_drift, pending, lastPresented) moved onto
        // RouteEntry so each route has independent phase origins. The callback no
        // longer keeps singleton fields for these — route.PullDrift / PullPendingFrame /
        // PullLastPresentedFrame are used directly in TryPresentNext.


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

                // §6.2 / R14: all per-route state lives on RouteEntry.
                // Try the cached pending frame first (it was too early last tick).
                VideoFrame candidate;
                if (route.PullPendingFrame.HasValue)
                {
                    candidate = route.PullPendingFrame.Value;
                    route.PullPendingFrame = null;
                }
                else
                {
                    if (!route.VideoSub.TryRead(out candidate))
                    {
                        // No new frame — re-present the last one to avoid black.
                        if (route.PullLastPresentedFrame.HasValue)
                        {
                            frame = route.PullLastPresentedFrame.Value;
                            return true;
                        }
                        continue;
                    }
                }

                // §6.1 / R23: PTS check — skip when this route is in live mode.
                if (!route.LiveMode)
                {
                    // §6.2 / R14: use route.PullDrift — independent from the push
                    // tracker (route.PushDrift) since the two paths run on different
                    // threads with different tick cadences.
                    var drift = route.PullDrift;
                    bool firstSeed = !drift.HasOrigin;
                    // Seed each origin from its own domain — see the matching note
                    // on PushVideoTick. Clamping clock up to the PTS value produced
                    // a permanent skew under wall-clock masters.
                    drift.SeedIfNeeded(candidate.Pts.Ticks, clockPosition.Ticks);

                    long routeOffsetTicks   = inp.TimeOffset.Ticks + route.TimeOffset.Ticks;
                    long relativePtsTicks   = drift.RelativePts(candidate.Pts.Ticks, routeOffsetTicks);
                    long relativeClockTicks = drift.RelativeClock(clockPosition.Ticks);
                    long toleranceTicks     = _router._options.VideoPtsEarlyTolerance.Ticks;
                    long discontinuityResetTicks = _router._options.VideoPtsDiscontinuityResetThreshold.Ticks;

                    // Large timestamp discontinuity (live sender restart, profile/FPS
                    // switch, source seek): re-seed the pull-path drift origin so the
                    // early-frame gate doesn't park on stale timeline offsets.
                    if (!firstSeed && discontinuityResetTicks > 0)
                    {
                        long errorTicks = relativePtsTicks - relativeClockTicks;
                        if (Math.Abs(errorTicks) > discontinuityResetTicks)
                        {
                            drift.Reset();
                            drift.SeedIfNeeded(candidate.Pts.Ticks, clockPosition.Ticks);
                            relativePtsTicks = drift.RelativePts(candidate.Pts.Ticks, routeOffsetTicks);
                            relativeClockTicks = drift.RelativeClock(clockPosition.Ticks);
                            firstSeed = true;
                        }
                    }

                    // First-ever frame: skip the gate so the presentation pipeline
                    // (and therefore Clock.Position) gets initialized.
                    if (!firstSeed && relativePtsTicks > relativeClockTicks + toleranceTicks)
                    {
                        // Too early — cache for next tick instead of losing it.
                        if (route.PullPendingFrame.HasValue)
                            route.PullPendingFrame.Value.MemoryOwner?.Dispose();
                        route.PullPendingFrame = candidate;
                        // Re-present last frame to avoid black
                        if (route.PullLastPresentedFrame.HasValue)
                        {
                            frame = route.PullLastPresentedFrame.Value;
                            return true;
                        }
                        continue;
                    }

                    // Drift correction with dead-band (unchanged).
                    if (!firstSeed)
                    {
                        drift.IntegrateError(
                            relativePtsTicks, relativeClockTicks, toleranceTicks,
                            _router._options.VideoPullDriftCorrectionGain);
                    }
                }

                // Release the previously held last-presented frame's ref before replacing.
                if (route.PullLastPresentedFrame.HasValue &&
                    !ReferenceEquals(route.PullLastPresentedFrame.Value.MemoryOwner, candidate.MemoryOwner))
                    route.PullLastPresentedFrame.Value.MemoryOwner?.Dispose();

                route.PullLastPresentedFrame = candidate;
                frame = candidate;
                return true;
            }

            // No route produced a frame — re-present last if any route has one
            for (int i = 0; i < routes.Length; i++)
            {
                var r = routes[i];
                if (r.EndpointId != _endpoint.Id) continue;
                if (r.PullLastPresentedFrame.HasValue)
                {
                    frame = r.PullLastPresentedFrame.Value;
                    return true;
                }
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
    /// §3.26 / P3 — RT-safe accessor for the per-endpoint output scratch.
    /// Fast path is a single <c>TryGetValue</c> + length check, so the RT thread
    /// does not allocate in steady state. On the rare grow path (larger
    /// <c>frameCount</c> than the pre-rented size, or endpoint registered
    /// without a pull hint) we fall back to <see cref="ArrayPool{T}"/>: that
    /// allocation is gated behind a size mismatch and is documented as the
    /// slow path.
    /// </summary>
    private float[] GetOrCreateOutputScratch(EndpointId id, int minSize)
    {
        if (_outputScratchBuffers.TryGetValue(id, out var buf) && buf.Length >= minSize)
            return buf;

        return _outputScratchBuffers.AddOrUpdate(
            id,
            _ => new float[minSize],
            (_, existing) => existing.Length >= minSize ? existing : new float[minSize]);
    }

    /// <summary>
    /// §8.4 — push-audio destination scratch (`destBuf` in PushAudioTick).
    /// Keeps a growable per-endpoint buffer to avoid ArrayPool rent/return
    /// on every tick.
    /// </summary>
    private float[] GetOrCreatePushDestScratch(EndpointId id, int minSize)
    {
        if (_pushDestScratchBuffers.TryGetValue(id, out var buf) && buf.Length >= minSize)
            return buf;

        return _pushDestScratchBuffers.AddOrUpdate(
            id,
            _ => new float[minSize],
            (_, existing) => existing.Length >= minSize ? existing : new float[minSize]);
    }

    /// <summary>
    /// §8.4 — push-audio mapped scratch (`mappedBuf` in PushAudioTick).
    /// Separate from destination scratch because channel-map conversion reads
    /// from source while mixing into destination.
    /// </summary>
    private float[] GetOrCreatePushMappedScratch(EndpointId id, int minSize)
    {
        if (_pushMappedScratchBuffers.TryGetValue(id, out var buf) && buf.Length >= minSize)
            return buf;

        return _pushMappedScratchBuffers.AddOrUpdate(
            id,
            _ => new float[minSize],
            (_, existing) => existing.Length >= minSize ? existing : new float[minSize]);
    }

    /// <summary>
    /// Scatters interleaved source samples into interleaved destination samples
    /// using a pre-baked channel route table.
    /// </summary>
    private static readonly IAudioMixer Mixer = DefaultAudioMixer.Instance;

    private static void ApplyChannelMap(
        ReadOnlySpan<float> src, Span<float> dest,
        (int dstCh, float gain)[][] bakedRoutes,
        int srcChannels, int dstChannels, int frameCount)
        => Mixer.ApplyChannelMap(src, dest, bakedRoutes, srcChannels, dstChannels, frameCount);

    private static void ApplyGain(Span<float> buffer, float gain)
        => Mixer.ApplyGain(buffer, gain);

    /// <summary>
    /// Applies per-input volume with a one-callback linear ramp between the
    /// previously-applied value (<see cref="InputEntry.AppliedVolume"/>) and
    /// the target (<see cref="InputEntry.Volume"/>). Skips the ramp when
    /// already at target and target==1.0 to keep the unity-gain fast path
    /// allocation- and work-free.
    /// </summary>
    private static void ApplyInputVolumeRamped(Span<float> buffer, InputEntry inp, int channels)
    {
        float target   = Volatile.Read(ref inp.Volume);
        float applied  = inp.AppliedVolume;
        bool  atTarget = Math.Abs(applied - target) <= 1e-6f;

        if (atTarget)
        {
            if (Math.Abs(target - 1.0f) > 1e-6f)
                Mixer.ApplyGain(buffer, target);
            return;
        }

        Mixer.ApplyGainRamp(buffer, applied, target, channels);
        inp.AppliedVolume = target;
    }

    private static void MixInto(Span<float> dest, ReadOnlySpan<float> src)
        => Mixer.MixInto(dest, src);

    private static float MeasurePeak(ReadOnlySpan<float> buffer)
        => Mixer.MeasurePeak(buffer);
}
