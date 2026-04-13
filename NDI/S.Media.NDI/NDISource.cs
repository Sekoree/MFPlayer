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

    /// <summary>Audio ring buffer depth in chunks. Default 16.</summary>
    public int AudioBufferDepth { get; init; } = 16;

    /// <summary>Video ring buffer depth in frames. Default 4.</summary>
    public int VideoBufferDepth { get; init; } = 4;

    /// <summary>
    /// Whether to create and start the video capture channel. Default: <see langword="true"/>.
    /// Set to <see langword="false"/> for audio-only use cases to avoid the overhead (and any
    /// potential format-mismatch crashes) of decoding video frames.
    /// </summary>
    public bool EnableVideo { get; init; } = true;

    /// <summary>NDI receiver settings. <see langword="null"/> uses defaults.</summary>
    public NDIReceiverSettings? ReceiverSettings { get; init; }

    /// <summary>
    /// When <see langword="true"/>, monitors the connection and automatically reconnects
    /// if the NDI source goes offline. Default: <see langword="false"/>.
    /// </summary>
    public bool AutoReconnect { get; init; } = false;

    /// <summary>
    /// How often (in milliseconds) to check the connection status when
    /// <see cref="AutoReconnect"/> is enabled. Default: 2000.
    /// </summary>
    public int ConnectionCheckIntervalMs { get; init; } = 2000;

    /// <summary>
    /// Settings for the <see cref="NDIFinder"/> used by <see cref="NDISource.OpenByNameAsync"/>.
    /// Also used for reconnection when the source was opened by name.
    /// <see langword="null"/> uses defaults.
    /// </summary>
    public NDIFinderSettings? FinderSettings { get; init; }
}

/// <summary>
/// Manages the full NDI receive lifecycle: creates an <see cref="NDIReceiver"/>,
/// attaches an <see cref="NDIFrameSync"/>, constructs <see cref="NDIAudioChannel"/> and
/// <see cref="NDIVideoChannel"/>, and starts their capture threads.
/// Analogous to <c>FFmpegDecoder</c> for the NDI pipeline.
/// <para>
/// Supports automatic reconnection (<see cref="NDISourceOptions.AutoReconnect"/>) and
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
    /// </summary>
    public event EventHandler<NDISourceStateChangedEventArgs>? StateChanged;

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

        int ret = NDIReceiver.Create(out var receiver, options.ReceiverSettings);
        if (ret != 0 || receiver == null)
            throw new InvalidOperationException($"NDIReceiver.Create failed: {ret}");

        receiver.Connect(source);

        ret = NDIFrameSync.Create(out var frameSync, receiver);
        if (ret != 0 || frameSync == null)
        {
            receiver.Dispose();
            throw new InvalidOperationException($"NDIFrameSync.Create failed: {ret}");
        }

        var clock = new NDIClock();
        var frameSyncGate = new Lock();
        var audio = new NDIAudioChannel(frameSync, clock,
            frameSyncGate,
            sampleRate:  options.SampleRate,
            channels:    options.Channels,
            bufferDepth: options.AudioBufferDepth);
        var video = options.EnableVideo
            ? new NDIVideoChannel(frameSync, clock, frameSyncGate, bufferDepth: options.VideoBufferDepth)
            : null;

        Log.LogInformation("Opened NDISource: source={SourceName}, sampleRate={SampleRate}, channels={Channels}, enableVideo={EnableVideo}, autoReconnect={AutoReconnect}",
            source.Name, options.SampleRate, options.Channels, options.EnableVideo, options.AutoReconnect);

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
    /// When <see cref="NDISourceOptions.AutoReconnect"/> is <see langword="true"/>, the
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

        var ndiSource = Open(found.Value, options);
        ndiSource._sourceNamePattern = sourceName;

        // Keep finder alive for reconnection if auto-reconnect is enabled.
        if (options.AutoReconnect)
            ndiSource._finder = finder;
        else
            finder.Dispose();

        return ndiSource;
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts capture threads for all channels and the clock.
    /// If <see cref="NDISourceOptions.AutoReconnect"/> is enabled, also starts the
    /// connection watchdog thread.
    /// Call after adding channels to mixers/consumers.
    /// </summary>
    private volatile bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;

        Log.LogInformation("Starting NDISource");
        Clock.Start();
        _audioChannelImpl?.StartCapture();
        _videoChannelImpl?.StartCapture();

        if (_options.AutoReconnect)
            StartWatchThread();
    }

    /// <summary>Stops the clock. Capture threads continue running until <see cref="Dispose"/> is called.</summary>
    public void Stop()
    {
        Log.LogInformation("Stopping NDISource");
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
        // Give the initial connection a moment to establish.
        if (!token.WaitHandle.WaitOne(Math.Max(500, _options.ConnectionCheckIntervalMs)))
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

            token.WaitHandle.WaitOne(_options.ConnectionCheckIntervalMs);
        }
    }

    private void TryReconnect(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

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

    // ── Discovery helpers ────────────────────────────────────────────────────

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
        _watchThread?.Join(TimeSpan.FromSeconds(2));
        _watchCts.Dispose();

        Clock.Stop();
        AudioChannel?.Dispose();
        VideoChannel?.Dispose();
        _frameSync.Dispose();
        _receiver.Dispose();
        _finder?.Dispose();
        Clock.Dispose();
    }
}

