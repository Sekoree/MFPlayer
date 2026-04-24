using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// Options for <see cref="NDISource.Open"/> and <see cref="NDISource.OpenByNameAsync"/>.
/// </summary>
public sealed class NDISourceOptions
{
    /// <summary>Desired audio sample rate. Default 48000.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Desired audio channel count. Default 2.</summary>
    public int Channels { get; init; } = 2;

    /// <summary>
    /// Unified queue-depth preset used for both audio and video when per-stream overrides are unset.
    /// Defaults to <see cref="NDILatencyPreset.Balanced"/>.
    /// </summary>
    public NDILatencyPreset QueueBufferDepth { get; init; } = NDILatencyPreset.Balanced;

    /// <summary>
    /// Optional audio ring buffer depth override in chunks.
    /// When <see langword="null"/>, <see cref="QueueBufferDepth"/> is used.
    /// </summary>
    public int? AudioBufferDepth { get; init; }

    /// <summary>
    /// Optional video ring buffer depth override in frames.
    /// When <see langword="null"/>, <see cref="QueueBufferDepth"/> is used.
    /// <para>
    /// The ring must be large enough to survive the entire startup window
    /// (format detection + output device open + audio pre-buffer) so that frames
    /// captured from the very beginning of the stream are still available when
    /// playback starts, ensuring zero-offset A/V sync.
    /// At 60 fps, 8 frames ≈ 133 ms; at 30 fps, 8 frames ≈ 267 ms.
    /// Increase to 32 or more for sources with high frame rates or slow startup paths.
    /// </para>
    /// </summary>
    public int? VideoBufferDepth { get; init; }

    /// <summary>
    /// Enables faster startup/capture polling for lower latency at the cost of higher CPU usage.
    /// </summary>
    public bool LowLatency { get; init; }

    /// <summary>
    /// Number of audio samples per NDI FrameSync capture call.
    /// Smaller values reduce audio capture-to-playback latency but increase CPU overhead.
    /// Default 1024 (~21 ms @ 48 kHz); use 256 (~5.3 ms) or 512 (~10.7 ms) for low-latency paths.
    /// Clamped to [64, 4096].
    /// </summary>
    public int AudioFramesPerCapture { get; init; } = 1024;

    /// <summary>
    /// Whether to create and start the video capture channel. Default: <see langword="true"/>.
    /// Set to <see langword="false"/> for audio-only use cases to avoid the overhead (and any
    /// potential format-mismatch crashes) of decoding video frames.
    /// </summary>
    public bool EnableVideo { get; init; } = true;

    /// <summary>NDI receiver settings. <see langword="null"/> uses defaults.</summary>
    public NDIReceiverSettings? ReceiverSettings { get; init; }

    /// <summary>
    /// §4.19 — reconnect policy. <see langword="null"/> (default) disables auto-reconnect.
    /// <c>new NDIReconnectPolicy()</c> enables it with the library defaults (2 s interval).
    /// </summary>
    public NDIReconnectPolicy? ReconnectPolicy { get; init; }

    internal NDIReconnectPolicy? ResolveReconnectPolicy() => ReconnectPolicy;

    /// <summary>
    /// §4.17 / N11 — maximum forward-jump (ms) the NDI video capture loop
    /// tolerates in the source-provided PTS before it falls back to the
    /// synthetic stopwatch clock for the offending frame. Values ≤ 0
    /// disable the jump clamp entirely. Default: 750 ms — rides out a
    /// typical network-stall reconnect without letting a malicious
    /// timestamp skew the player's clock.
    /// </summary>
    public int MaxForwardPtsJumpMs { get; init; } = 750;

    /// <summary>
    /// §4.16 / N4 — which NDI capture channel writes timestamps into the
    /// shared <see cref="NDIClock"/>. Default <see cref="NDIClockPolicy.Both"/>
    /// preserves the legacy behaviour for source-compat; new code should
    /// pick <see cref="NDIClockPolicy.VideoPreferred"/> for A/V sources
    /// (eliminates the sub-frame jitter from two channels racing for clock
    /// authority) or <see cref="NDIClockPolicy.FirstWriter"/> when the
    /// arrival order is unpredictable.
    /// </summary>
    public NDIClockPolicy ClockPolicy { get; init; } = NDIClockPolicy.Both;

    /// <summary>
    /// Settings for the <see cref="NDIFinder"/> used by <see cref="NDISource.OpenByNameAsync"/>.
    /// Also used for reconnection when the source was opened by name.
    /// <see langword="null"/> uses defaults.
    /// </summary>
    public NDIFinderSettings? FinderSettings { get; init; }

    public int ResolveAudioBufferDepth() => Math.Max(1, AudioBufferDepth ?? ResolveQueueBufferDepth());
    public int ResolveVideoBufferDepth() => Math.Max(1, VideoBufferDepth ?? ResolveQueueBufferDepth());
    public int ResolveQueueBufferDepth() => QueueBufferDepth.ResolveQueueDepth();

    /// <summary>
    /// Creates an <see cref="NDISourceOptions"/> with all source-side knobs pre-configured
    /// from the given <paramref name="preset"/>. Output-side configuration is provided by
    /// <see cref="NDIPlaybackProfile.For"/>.
    /// <para>
    /// The returned options have reconnect enabled via <see cref="ReconnectPolicy"/>
    /// and <see cref="EnableVideo"/> = <see langword="true"/>. Override individual
    /// properties with <c>with { … }</c> if needed.
    /// </para>
    /// </summary>
    /// <param name="preset">Endpoint latency preset.</param>
    /// <param name="sampleRate">Desired audio sample rate. Default 48000.</param>
    /// <param name="channels">Desired audio channel count. Default 2.</param>
    public static NDISourceOptions ForPreset(
        NDIEndpointPreset preset,
        int sampleRate = 48000,
        int channels   = 2)
    {
        var profile = NDIPlaybackProfile.For(preset);
        return new NDISourceOptions
        {
            SampleRate            = sampleRate,
            Channels              = channels,
            QueueBufferDepth      = NDILatencyPreset.FromEndpointPreset(preset),
            LowLatency            = profile.LowLatencyPolling,
            AudioFramesPerCapture = profile.AudioFramesPerCapture,
            EnableVideo           = true,
            ReconnectPolicy       = NDIReconnectPolicy.Default,
            FinderSettings        = new NDIFinderSettings { ShowLocalSources = true },
        };
    }
}

/// <summary>
/// Manages the full NDI receive lifecycle: creates an <see cref="NDIReceiver"/>,
/// attaches an <see cref="NDIFrameSync"/>, constructs <see cref="NDIAudioChannel"/> and
/// <see cref="NDIVideoChannel"/>, and starts their capture threads.
/// Analogous to <c>FFmpegDecoder</c> for the NDI pipeline.
/// <para>
/// Supports automatic reconnection (<see cref="NDISourceOptions.ReconnectPolicy"/>) and
/// name-based discovery (<see cref="OpenByNameAsync"/>).
/// </para>
/// </summary>
public sealed class NDISource : IDisposable
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDISource));

    private readonly NDIReceiver  _receiver;
    private readonly NDIFrameSync _frameSync;
    private readonly NDISourceOptions _options;
    private readonly NDIAudioChannel? _audioChannelImpl;
    private readonly NDIVideoChannel? _videoChannelImpl;

    // Connection tracking
    private NDIDiscoveredSource? _connectedSource;
    private string? _sourceNamePattern;
    private NDIFinder? _finder;          // kept alive for name-based reconnection
    private Thread? _watchThread;
    private readonly CancellationTokenSource _watchCts = new();

    // §3.42 / N1, N2, N19 — session gate serialises reconnect (`recv_connect`) and
    // framesync create/destroy against teardown. The capture threads themselves
    // do NOT take this gate — they use the per-frame `_frameSyncGate`s on the
    // channels — so a reconnect cannot glitch audio while the gate is held.
    private readonly Lock _sessionGate = new();

    private volatile NDISourceState _state = NDISourceState.Disconnected;
    private bool _disposed;

    /// <summary>The audio channel for this source. <see langword="null"/> if audio is not available.</summary>
    public IAudioChannel? AudioChannel { get; }

    /// <summary>The video channel for this source. <see langword="null"/> if video is not available.</summary>
    public IVideoChannel? VideoChannel { get; }

    /// <summary>The clock driven by NDI frame timestamps.</summary>
    public NDIClock Clock { get; }

    /// <summary>Current connection state.</summary>
    public NDISourceState State => _state;

    /// <summary>
    /// Raised when the connection state changes (e.g. Connected → Reconnecting → Connected).
    /// <para>
    /// §3.47f — the event args' <see cref="NDISourceStateChangedEventArgs.NewState"/>
    /// is authoritative for the state at the moment of the transition. The
    /// <see cref="State"/> property may already have advanced to a later
    /// state by the time a handler runs (dispatch happens on the
    /// <see cref="ThreadPool"/>, so multiple transitions can queue up).
    /// </para>
    /// </summary>
    public event EventHandler<NDISourceStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// §4.17 / N7 — raised once per distinct unsupported FourCC encountered by either
    /// the video or audio capture thread. Check <see cref="NDIUnsupportedFourCcEventArgs.IsAudio"/>
    /// to dispatch. <para>§2.8 — dispatched synchronously on the NDI capture thread;
    /// handlers must be fast and must not re-enter the capture path.</para>
    /// </summary>
    public event EventHandler<NDIUnsupportedFourCcEventArgs>? UnsupportedFourCc;

    /// <summary>
    /// §4.17 / N11 — raised when the NDI source's video format changes (dimensions,
    /// pixel format, or frame rate) between two consecutive frames. Never raised on
    /// the first frame. <para>§2.8 — dispatched on the NDI video capture thread.
    /// Handlers must be non-blocking.</para>
    /// </summary>
    public event EventHandler<NDIVideoFormatChangedEventArgs>? VideoFormatChanged;

    private NDISource(
        NDIReceiver      receiver,
        NDIFrameSync     frameSync,
        NDIClock         clock,
        IAudioChannel?   audio,
        IVideoChannel?   video,
        NDISourceOptions options)
    {
        _receiver    = receiver;
        _frameSync   = frameSync;
        Clock        = clock;
        AudioChannel = audio;
        VideoChannel = video;
        _audioChannelImpl = audio as NDIAudioChannel;
        _videoChannelImpl = video as NDIVideoChannel;
        _options     = options;

        // §4.17 / N7, N11 — forward channel-level events onto the public
        // source surface so host apps don't reach through the IAudioChannel /
        // IVideoChannel interfaces. Propagate the forward-jump tolerance
        // option; values ≤ 0 disable the clamp entirely inside the channel.
        if (_videoChannelImpl is not null)
        {
            _videoChannelImpl.MaxForwardPtsJumpMs = options.MaxForwardPtsJumpMs;
            _videoChannelImpl.UnsupportedFourCc += (s, e) => UnsupportedFourCc?.Invoke(this, e);
            _videoChannelImpl.FormatChanged     += (s, e) => VideoFormatChanged?.Invoke(this, e);
        }
        if (_audioChannelImpl is not null)
        {
            _audioChannelImpl.UnsupportedFourCc += (s, e) => UnsupportedFourCc?.Invoke(this, e);
        }
    }

    // ── Factory: Open by discovered source ──────────────────────────────────

    /// <summary>
    /// Connects to an NDI source by name, creates all channels, and returns a ready-to-use
    /// <see cref="NDISource"/>. Call <see cref="Start"/> after adding channels to mixers.
    /// </summary>
    /// <param name="source">Discovered NDI source (from <see cref="NDIFinder"/>).</param>
    /// <param name="options">Options; <see langword="null"/> uses defaults.</param>
    /// <exception cref="InvalidOperationException">Thrown if the receiver or frame-sync cannot be created.</exception>
    public static NDISource Open(NDIDiscoveredSource source, NDISourceOptions? options = null)
    {
        options ??= new NDISourceOptions();
        int resolvedAudioDepth = options.ResolveAudioBufferDepth();
        int resolvedVideoDepth = options.ResolveVideoBufferDepth();
        int resolvedQueueDepth = options.ResolveQueueBufferDepth();

        int ret = NDIReceiver.Create(out var receiver, options.ReceiverSettings);
        if (ret != 0 || receiver == null)
            throw new InvalidOperationException($"NDIReceiver.Create failed: {ret}");

        // §3.47g / N20 — create the framesync BEFORE connecting the receiver,
        // matching the reference SDK sample order. Connecting first can cause
        // the initial frame or two to be lost before the framesync is ready to
        // receive them.
        ret = NDIFrameSync.Create(out var frameSync, receiver);
        if (ret != 0 || frameSync == null)
        {
            receiver.Dispose();
            throw new InvalidOperationException($"NDIFrameSync.Create failed: {ret}");
        }

        try
        {
            receiver.Connect(source);
        }
        catch
        {
            frameSync.Dispose();
            receiver.Dispose();
            throw;
        }

        var clock = new NDIClock(sampleRate: options.SampleRate);
        // Each channel gets its own lock — the NDI FrameSync API is internally thread-safe
        // for concurrent audio/video captures on the same instance, so there is no need to
        // share a gate.  A shared gate caused audio capture to stall while video held the
        // lock during the Marshal.Copy of a full video frame (~8 MB for 1080p UYVY).
        // §4.16 / N4 — when VideoPreferred is requested but video is disabled,
        // fall back to AudioPreferred so the audio channel still writes the
        // clock (otherwise the clock never advances). Mirror for
        // AudioPreferred on video-only sources — resolved just below where
        // the video channel is constructed.
        var effectiveClockPolicy = options.ClockPolicy;
        if (effectiveClockPolicy == NDIClockPolicy.VideoPreferred && !options.EnableVideo)
            effectiveClockPolicy = NDIClockPolicy.AudioPreferred;

        var audio = new NDIAudioChannel(frameSync, clock,
            frameSyncGate: null,   // creates its own per-channel Lock
            sampleRate:  options.SampleRate,
            channels:    options.Channels,
            bufferDepth: resolvedAudioDepth,
            preferLowLatency: options.LowLatency,
            framesPerCapture: options.AudioFramesPerCapture,
            clockPolicy: effectiveClockPolicy);
        var video = options.EnableVideo
            ? new NDIVideoChannel(frameSync, clock, frameSyncGate: null, bufferDepth: resolvedVideoDepth, preferLowLatency: options.LowLatency, clockPolicy: effectiveClockPolicy)
            : null;

        Log.LogInformation("Opened NDISource: source={SourceName}, sampleRate={SampleRate}, channels={Channels}, queueDepth={QueueDepth}, audioDepth={AudioDepth}, videoDepth={VideoDepth}, lowLatency={LowLatency}, enableVideo={EnableVideo}, reconnect={Reconnect}",
            source.Name, options.SampleRate, options.Channels, resolvedQueueDepth, resolvedAudioDepth, resolvedVideoDepth, options.LowLatency, options.EnableVideo,
            options.ResolveReconnectPolicy() is { } p ? $"every {p.EffectiveCheckIntervalMs}ms" : "off");

        var ndiSource = new NDISource(receiver, frameSync, clock, audio, video, options);
        ndiSource._connectedSource = source;
        ndiSource.SetState(NDISourceState.Connected);
        return ndiSource;
    }

    // ── Factory: Open by name (async discovery) ──────────────────────────────

    /// <summary>
    /// Discovers an NDI source by name and connects to it. Waits until a matching source
    /// appears on the network or <paramref name="ct"/> is cancelled.
    /// <para>
    /// Source name matching is case-insensitive and supports partial matches.
    /// NDI source names are typically <c>"HOSTNAME (SourceName)"</c>.
    /// </para>
    /// <para>
    /// When <see cref="NDISourceOptions.ReconnectPolicy"/> is configured, the
    /// internal <see cref="NDIFinder"/> is kept alive so the source can be rediscovered
    /// if it goes offline and reappears (possibly at a different IP address).
    /// </para>
    /// </summary>
    /// <param name="sourceName">
    /// Full or partial NDI source name to match (e.g. <c>"MY-PC (OBS)"</c> or just <c>"OBS"</c>).
    /// </param>
    /// <param name="options">Options; <see langword="null"/> uses defaults.</param>
    /// <param name="ct">Cancellation token to abort the discovery wait.</param>
    /// <returns>A connected <see cref="NDISource"/>. Call <see cref="Start"/> to begin capture.</returns>
    /// <exception cref="InvalidOperationException">Thrown if NDIFinder or receiver creation fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="ct"/> is cancelled before a source is found.</exception>
    public static async Task<NDISource> OpenByNameAsync(
        string sourceName,
        NDISourceOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        options ??= new NDISourceOptions();

        Log.LogInformation("OpenByNameAsync: searching for source matching '{SourceName}'", sourceName);

        int finderRet = NDIFinder.Create(out var finder, options.FinderSettings);
        if (finderRet != 0 || finder == null)
            throw new InvalidOperationException($"NDIFinder.Create failed: {finderRet}");

        NDIDiscoveredSource? found = null;
        try
        {
            found = await DiscoverSourceAsync(finder, sourceName, ct).ConfigureAwait(false);
        }
        catch
        {
            finder.Dispose();
            throw;
        }

        Log.LogInformation("OpenByNameAsync: found source '{FoundName}' at {Url}",
            found.Value.Name, found.Value.UrlAddress ?? "(no url)");

        NDISource ndiSource;
        try
        {
            ndiSource = Open(found.Value, options);
        }
        catch
        {
            // §3.47c / N13 — the discovery finder was kept alive for the Open
            // call; if Open itself throws (receiver/framesync creation or connect
            // failure), dispose the finder before propagating or we leak the
            // mDNS discovery threads for the lifetime of the process.
            finder.Dispose();
            throw;
        }
        ndiSource._sourceNamePattern = sourceName;

        // §4.19 — keep finder alive when reconnect is requested.
        if (options.ResolveReconnectPolicy() is not null)
            ndiSource._finder = finder;
        else
            finder.Dispose();

        return ndiSource;
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts capture threads for all channels and the clock.
    /// If <see cref="NDISourceOptions.ReconnectPolicy"/> is configured, also starts the
    /// connection watchdog thread.
    /// Call after adding channels to mixers/consumers.
    /// <para>
    /// For lower latency, call <see cref="StartVideoCapture"/> first to detect the video
    /// format, then call <see cref="StartAudioCapture"/> after the first video frame
    /// arrives. This ensures the audio ring contains only real content (no framesync
    /// silence from before the NDI source began streaming), eliminating the T_conn worth
    /// of silent pre-buffering that would otherwise be baked into playback start.
    /// </para>
    /// </summary>
    private volatile bool _started;
    private volatile bool _videoStarted;
    private volatile bool _audioStarted;

    /// <summary>
    /// Ensures the clock and connection watchdog are started (idempotent).
    /// Called internally by both <see cref="StartVideoCapture"/> and <see cref="StartAudioCapture"/>.
    /// </summary>
    private void EnsureCommonStarted()
    {
        if (_started) return;
        _started = true;
        Log.LogInformation("Starting NDISource common components (clock + watchdog)");
        Clock.Start();
        // §4.19 — resolve the policy once at start time so later option
        // mutation doesn't race the watchdog. null = no reconnect.
        _resolvedReconnect = _options.ResolveReconnectPolicy();
        if (_resolvedReconnect is not null)
            StartWatchThread();
    }

    // §4.19 — resolved reconnect policy, captured at Start so the watchdog
    // reads a stable value.
    private NDIReconnectPolicy? _resolvedReconnect;

    /// <summary>
    /// Starts only the video capture thread (and the internal clock / watchdog).
    /// Use this when you want to detect the video format before committing to audio
    /// capture — call <see cref="StartAudioCapture"/> once the first video frame confirms
    /// the NDI source is streaming real content.
    /// Idempotent: safe to call multiple times.
    /// </summary>
    public void StartVideoCapture()
    {
        if (_videoStarted) return;
        _videoStarted = true;
        EnsureCommonStarted();
        Log.LogInformation("Starting NDISource video capture");
        _videoChannelImpl?.StartCapture();
    }

    /// <summary>
    /// Starts only the audio capture thread (and the internal clock / watchdog if not yet
    /// running). Call this after <see cref="StartVideoCapture"/> and after the first video
    /// frame has arrived so that the audio ring is filled with real audio instead of
    /// framesync-generated silence.
    /// Idempotent: safe to call multiple times.
    /// </summary>
    public void StartAudioCapture()
    {
        if (_audioStarted) return;
        _audioStarted = true;
        EnsureCommonStarted();
        Log.LogInformation("Starting NDISource audio capture");
        _audioChannelImpl?.StartCapture();
    }

    /// <summary>
    /// Starts all capture threads (audio + video) and the clock/watchdog.
    /// Equivalent to calling <see cref="StartVideoCapture"/> followed by
    /// <see cref="StartAudioCapture"/>. Idempotent.
    /// </summary>
    public void Start()
    {
        StartVideoCapture();
        StartAudioCapture();
    }

    /// <summary>
    /// Stops the media clock only. Capture threads continue running until
    /// <see cref="Dispose"/> is called. Renamed from <c>Stop</c> in §3.45 to
    /// make the narrow scope explicit.
    /// </summary>
    public void StopClock()
    {
        Log.LogInformation("Stopping NDISource clock");
        Clock.Stop();
    }

    /// <summary>
    /// Waits until the underlying NDI audio ring reaches a minimum number of chunks.
    /// No-op when audio is unavailable.
    /// </summary>
    public Task WaitForAudioBufferAsync(int minChunks, CancellationToken ct = default)
    {
        return _audioChannelImpl != null
            ? _audioChannelImpl.WaitForBufferAsync(minChunks, ct)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Waits until the underlying NDI video ring reaches a minimum number of frames.
    /// No-op when video is unavailable or disabled via <see cref="NDISourceOptions.EnableVideo"/>.
    /// <para>
    /// Call this concurrently with <see cref="WaitForAudioBufferAsync"/> (via
    /// <see cref="Task.WhenAll"/>) before starting playback so both rings accumulate content
    /// from the same NDI timestamp origin, preventing a fixed A/V offset at startup.
    /// </para>
    /// </summary>
    public Task WaitForVideoBufferAsync(int minFrames, CancellationToken ct = default)
    {
        return _videoChannelImpl != null
            ? _videoChannelImpl.WaitForBufferAsync(minFrames, ct)
            : Task.CompletedTask;
    }

    // ── Connection watchdog ──────────────────────────────────────────────────

    private void StartWatchThread()
    {
        _watchThread = new Thread(WatchLoop)
        {
            Name         = "NDISource.Watch",
            IsBackground = true,
            Priority     = ThreadPriority.BelowNormal
        };
        _watchThread.Start();
    }

    private void WatchLoop()
    {
        var token = _watchCts.Token;
        // §4.19 — watchdog always runs under a resolved policy (StartWatchThread
        // is only reached when EnsureCommonStarted resolved a non-null one).
        var policy = _resolvedReconnect ?? NDIReconnectPolicy.Default;
        // Give the initial connection a moment to establish.
        if (!token.WaitHandle.WaitOne(Math.Max(policy.EffectiveInitialDelayMs, policy.EffectiveCheckIntervalMs)))
        {
            // cancelled during initial wait
            if (token.IsCancellationRequested) return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                int connections = _receiver.GetConnectionCount();

                if (connections > 0)
                {
                    if (_state == NDISourceState.Reconnecting)
                    {
                        Log.LogInformation("NDISource reconnected to '{SourceName}'",
                            _connectedSource?.Name ?? _sourceNamePattern);
                        SetState(NDISourceState.Connected);
                    }
                }
                else if (_state == NDISourceState.Connected)
                {
                    Log.LogWarning("NDISource lost connection to '{SourceName}', attempting reconnection",
                        _connectedSource?.Name ?? _sourceNamePattern);
                    SetState(NDISourceState.Reconnecting);
                    TryReconnect(token);
                }
                else if (_state == NDISourceState.Reconnecting)
                {
                    // Still disconnected — retry.
                    TryReconnect(token);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Log.LogWarning(ex, "NDISource watch-loop error");
            }

            // §3.47d / N15 — WaitOne returns `true` when the token fires; the
            // next iteration would otherwise spend one extra poll interval
            // hanging around. Exit immediately.
            if (token.WaitHandle.WaitOne(policy.EffectiveCheckIntervalMs))
                break;
        }
    }

    private void TryReconnect(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

        // §3.42 — serialise reconnect against Dispose so the receiver/framesync
        // cannot be torn down mid-Connect.
        lock (_sessionGate)
        {
            if (_disposed || token.IsCancellationRequested) return;

            // If we have a finder and a name pattern, rediscover the source.
            // This handles the case where the source's IP changed after a restart.
            if (_finder != null && _sourceNamePattern != null)
            {
                Log.LogDebug("Reconnect: searching for source matching '{Pattern}' via finder", _sourceNamePattern);
                _finder.WaitForSources(1000);
                if (token.IsCancellationRequested) return;

                var sources = _finder.GetCurrentSources();
                var found = MatchSource(sources, _sourceNamePattern);
                if (found.HasValue)
                {
                    Log.LogDebug("Reconnect: rediscovered '{SourceName}', reconnecting receiver", found.Value.Name);
                    _connectedSource = found.Value;
                    _receiver.Connect(found.Value);
                    return;
                }
                Log.LogDebug("Reconnect: source not yet visible, will retry");
            }
            else if (_connectedSource.HasValue)
            {
                // Direct reconnect with the original source reference.
                Log.LogDebug("Reconnect: retrying connection to '{SourceName}'", _connectedSource.Value.Name);
                _receiver.Connect(_connectedSource.Value);
            }
        }
    }

    // ── Public discovery helpers ─────────────────────────────────────────────

    /// <summary>
    /// Discovers NDI sources on the network and returns all sources found within
    /// <paramref name="timeout"/> (default 5 s).
    /// <para>
    /// The method polls in 500 ms increments. It returns early once at least one
    /// source has been found and <paramref name="minDiscoveryMs"/> milliseconds have
    /// elapsed (default 500), giving late-announcing sources a fair chance to appear.
    /// </para>
    /// </summary>
    /// <param name="timeout">Maximum discovery window. <see langword="null"/> defaults to 5 s.</param>
    /// <param name="settings">Finder settings. <see langword="null"/> uses defaults (local sources visible).</param>
    /// <param name="minDiscoveryMs">
    /// Minimum wait even if sources appear immediately. Guards against returning a
    /// partial list when multiple sources announce at roughly the same time.
    /// Default: 500 ms.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All discovered sources; an empty array if none appeared within the timeout.</returns>
    public static async Task<NDIDiscoveredSource[]> DiscoverAsync(
        TimeSpan?         timeout          = null,
        NDIFinderSettings? settings        = null,
        int               minDiscoveryMs   = 500,
        CancellationToken ct              = default)
    {
        var deadline  = timeout ?? TimeSpan.FromSeconds(5);
        var minWait   = TimeSpan.FromMilliseconds(Math.Max(0, minDiscoveryMs));

        int ret = NDIFinder.Create(out var finder, settings ?? new NDIFinderSettings { ShowLocalSources = true });
        if (ret != 0 || finder == null)
            throw new InvalidOperationException($"NDIFinder.Create failed: {ret}");

        using (finder)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            NDIDiscoveredSource[] best = [];

            while (sw.Elapsed < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // WaitForSources is blocking: use a capped 500 ms poll interval.
                uint pollMs = (uint)Math.Min(500, Math.Max(1, (deadline - sw.Elapsed).TotalMilliseconds));
                finder.WaitForSources(pollMs);

                var current = finder.GetCurrentSources();
                if (current.Length > best.Length) best = current;

                // Stop early: sources found and minimum wait elapsed.
                if (best.Length > 0 && sw.Elapsed >= minWait) break;
            }

            // Return the latest snapshot (may include sources added in the last poll).
            var final = finder.GetCurrentSources();
            return final.Length >= best.Length ? final : best;
        }
    }

    // ── Private discovery helpers ────────────────────────────────────────────

    private static async Task<NDIDiscoveredSource> DiscoverSourceAsync(
        NDIFinder finder,
        string namePattern,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // WaitForSources blocks up to the timeout — use a moderate timeout
            // so we can check cancellation regularly.
            finder.WaitForSources(500);
            ct.ThrowIfCancellationRequested();

            var sources = finder.GetCurrentSources();
            var match = MatchSource(sources, namePattern);
            if (match.HasValue)
                return match.Value;

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException("Source discovery was cancelled.", ct);
    }

    /// <summary>
    /// Matches a source by name. Tries exact match first (case-insensitive),
    /// then falls back to contains match. NDI names are typically <c>"HOSTNAME (SourceName)"</c>.
    /// </summary>
    public static NDIDiscoveredSource? MatchSource(NDIDiscoveredSource[] sources, string pattern)
    {
        // 1. Exact match (case-insensitive)
        foreach (var s in sources)
            if (s.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return s;

        // 2. Contains match (case-insensitive) — matches partial names like "OBS" in "MY-PC (OBS)"
        foreach (var s in sources)
            if (s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return s;

        return null;
    }

    // ── State management ─────────────────────────────────────────────────────

    private void SetState(NDISourceState newState)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;

        Log.LogDebug("NDISource state: {OldState} → {NewState}", old, newState);

        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, args) = ((NDISource, NDISourceStateChangedEventArgs))s!;
            self.StateChanged?.Invoke(self, args);
        }, (this, new NDISourceStateChangedEventArgs(old, newState,
            _connectedSource?.Name ?? _sourceNamePattern)));
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;

        // Fire state change BEFORE _disposed = true so handlers can still access this instance.
        SetState(NDISourceState.Disconnected);

        _disposed = true;
        Log.LogInformation("Disposing NDISource");

        _watchCts.Cancel();
        // §3.42 / N19 — loop-join instead of hard 2 s timeout so a slow
        // `_finder.WaitForSources` inside TryReconnect cannot leave the watch
        // thread alive when we enter the session-gate teardown below.
        LoopJoin(_watchThread, "watch");
        _watchCts.Dispose();

        // §3.42 — take the session gate so any in-flight reconnect attempt
        // completes before we tear the receiver/framesync down. The capture
        // threads use their own per-channel `_frameSyncGate` locks, so this
        // only blocks on reconnect/state work, not on the RT hot path.
        lock (_sessionGate)
        {
            Clock.Stop();
            AudioChannel?.Dispose();
            VideoChannel?.Dispose();
            _frameSync.Dispose();
            _receiver.Dispose();
            _finder?.Dispose();
            Clock.Dispose();
        }
    }

    private static void LoopJoin(Thread? thread, string name)
    {
        if (thread is null) return;
        int timeoutMs = 500;
        while (!thread.Join(timeoutMs))
        {
            Log.LogWarning(
                "NDI {ThreadName} thread still alive after {Timeout} ms — retrying join",
                name, timeoutMs);
            timeoutMs = Math.Min(timeoutMs * 2, 5_000);
        }
    }
}
