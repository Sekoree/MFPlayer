using NDILib;
using S.Media.Core.Routing;
using S.Media.Playback;

namespace S.Media.NDI;

/// <summary>
/// §5.7 / §5.8 — NDI input helpers for <see cref="MediaPlayerBuilder"/>.
/// </summary>
public static class MediaPlayerBuilderExtensions
{
    /// <summary>
    /// Wires a pre-opened <see cref="NDIAVChannel"/> into the builder. Playback
    /// uses video-first startup ordering (video format probe + prebuffer before
    /// audio start), then routes the available channels into the player.
    /// </summary>
    public static MediaPlayerBuilder WithNDIInput(
        this MediaPlayerBuilder builder,
        NDIAVChannel source,
        NDIEndpointPreset preset = NDIEndpointPreset.Balanced)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        var lifecycle = new NdiInputLifecycle(
            openAsync: _ => Task.FromResult(source),
            ownsSource: false,
            profile: NDIPlaybackProfile.For(preset));

        return builder.WithLifecycleHook(lifecycle.BeforePlayAsync, lifecycle.BeforeCloseAsync);
    }

    /// <summary>
    /// Wires an NDI input discovered by <see cref="NDIFinder"/> into the builder.
    /// The source is opened lazily on first <see cref="MediaPlayer.PlayAsync"/>.
    /// </summary>
    public static MediaPlayerBuilder WithNDIInput(
        this MediaPlayerBuilder builder,
        NDIDiscoveredSource source,
        NDIEndpointPreset preset = NDIEndpointPreset.Balanced,
        NDIReconnectPolicy? reconnect = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var opts = BuildSourceOptions(preset, reconnect);
        var lifecycle = new NdiInputLifecycle(
            openAsync: _ => Task.FromResult(NDIAVChannel.Open(source, opts)),
            ownsSource: true,
            profile: NDIPlaybackProfile.For(preset));

        return builder.WithLifecycleHook(lifecycle.BeforePlayAsync, lifecycle.BeforeCloseAsync);
    }

    /// <summary>
    /// Wires an NDI source name/pattern into the builder. The source is opened
    /// lazily on first <see cref="MediaPlayer.PlayAsync"/> via
    /// <see cref="NDIAVChannel.OpenByNameAsync(string,NDISourceOptions?,CancellationToken)"/>.
    /// </summary>
    public static MediaPlayerBuilder WithNDIInput(
        this MediaPlayerBuilder builder,
        string sourceName,
        NDIEndpointPreset preset = NDIEndpointPreset.Balanced,
        NDIReconnectPolicy? reconnect = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("Source name must not be empty.", nameof(sourceName));

        var opts = BuildSourceOptions(preset, reconnect);
        var lifecycle = new NdiInputLifecycle(
            openAsync: ct => NDIAVChannel.OpenByNameAsync(sourceName, opts, ct),
            ownsSource: true,
            profile: NDIPlaybackProfile.For(preset));

        return builder.WithLifecycleHook(lifecycle.BeforePlayAsync, lifecycle.BeforeCloseAsync);
    }

    private static NDISourceOptions BuildSourceOptions(
        NDIEndpointPreset preset,
        NDIReconnectPolicy? reconnect)
    {
        var profile = NDIPlaybackProfile.For(preset);
        return new NDISourceOptions
        {
            SampleRate = 48000,
            Channels = 2,
            QueueBufferDepth = NDILatencyPreset.FromEndpointPreset(preset),
            LowLatency = profile.LowLatencyPolling,
            AudioFramesPerCapture = profile.AudioFramesPerCapture,
            EnableVideo = true,
            ReconnectPolicy = reconnect,
            FinderSettings = reconnect is null ? null : new NDIFinderSettings { ShowLocalSources = true }
        };
    }

    private sealed class NdiInputLifecycle(
        Func<CancellationToken, Task<NDIAVChannel>> openAsync,
        bool ownsSource,
        NDIPlaybackProfile profile)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Func<CancellationToken, Task<NDIAVChannel>> _openAsync = openAsync;
        private readonly bool _ownsSource = ownsSource;
        private readonly NDIPlaybackProfile _profile = profile;

        private NDIAVChannel? _source;
        private MediaPlayer? _player;
        private EventHandler<NDIVideoFormatChangedEventArgs>? _formatChangedHandler;
        private bool _attached;
        private bool _clockRegistered;

        private static readonly TimeSpan FirstVideoFrameTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PrebufferTimeout = TimeSpan.FromSeconds(5);

        public async Task BeforePlayAsync(MediaPlayer player, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _source ??= await _openAsync(ct).ConfigureAwait(false);
                _player = player;

                if (!_attached)
                {
                    if (_source.AudioChannel is { } audio)
                        player.RegisterExternalAudioInput(audio);

                    if (_source.VideoChannel is { } video)
                    {
                        player.RegisterExternalVideoInput(video, new VideoRouteOptions
                        {
                            LiveMode = _profile.BypassVideoPtsScheduling
                        });
                    }

                    player.Router.RegisterClock(_source.Clock, ClockPriority.Hardware);
                    _clockRegistered = true;
                    _attached = true;
                }

                // §5.7 / NDI required #6: video-first startup ordering.
                if (_source.VideoChannel is not null)
                {
                    _source.StartVideoCapture();
                    await WaitBestEffortAsync(
                        _source.WaitForVideoBufferAsync(1, ct),
                        FirstVideoFrameTimeout,
                        ct).ConfigureAwait(false);
                }

                if (_source.AudioChannel is not null)
                    _source.StartAudioCapture();

                var waits = new List<Task>(2);
                if (_source.AudioChannel is not null)
                    waits.Add(_source.WaitForAudioBufferAsync(_profile.AudioPreBufferChunks, ct));
                if (_source.VideoChannel is not null)
                    waits.Add(_source.WaitForVideoBufferAsync(_profile.VideoPreBufferFrames, ct));

                if (waits.Count > 0)
                    await WaitBestEffortAsync(Task.WhenAll(waits), PrebufferTimeout, ct).ConfigureAwait(false);

                // §Dynamic-source-format / TODO #NDI-jump — at this point the
                // NDI capture loop has produced ≥ 1 frame so VideoChannel.SourceFormat
                // carries real Width / Height / fps from the source's reported
                // FrameRateN/D. Re-broadcast it to all dynamic-metadata video
                // endpoints so they stamp the correct fps from the very first
                // outgoing frame (the route's initial AnnounceUpcomingVideoFormat
                // ran with default(VideoFormat) — see MediaPlayer.AnnounceVideoSourceFormat
                // for the rationale).
                if (_source.VideoChannel is { } vch)
                {
                    var fmt = vch.SourceFormat;
                    if (fmt.Width > 0 && fmt.Height > 0)
                        player.AnnounceVideoSourceFormat(fmt);

                    // Also follow live format changes (sender re-init, profile
                    // switch, resolution change). NDIAVChannel forwards the
                    // underlying NDIVideoChannel.FormatChanged.
                    if (_formatChangedHandler is null)
                    {
                        _formatChangedHandler = OnVideoFormatChanged;
                        _source.VideoFormatChanged += _formatChangedHandler;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private void OnVideoFormatChanged(object? sender, NDIVideoFormatChangedEventArgs e)
        {
            // Capture-thread context: keep this fast. Just forward to the
            // player; AnnounceVideoSourceFormat itself only iterates endpoints
            // and posts hints (no I/O, no waits).
            var player = _player;
            if (player is null) return;
            try { player.AnnounceVideoSourceFormat(e.NewFormat); }
            catch { /* swallow — capture thread must not throw */ }
        }

        public async Task BeforeCloseAsync(MediaPlayer player, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_source is null)
                    return;

                if (_formatChangedHandler is not null)
                {
                    try { _source.VideoFormatChanged -= _formatChangedHandler; } catch { }
                    _formatChangedHandler = null;
                }

                try { _source.Stop(); } catch { }

                if (_clockRegistered)
                {
                    try { player.Router.UnregisterClock(_source.Clock); } catch { }
                    _clockRegistered = false;
                }

                _attached = false;
                _player = null;

                if (_ownsSource)
                {
                    try { _source.Dispose(); } catch { }
                    _source = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task WaitBestEffortAsync(Task task, TimeSpan timeout, CancellationToken externalCt)
        {
            if (timeout <= TimeSpan.Zero)
            {
                await task.ConfigureAwait(false);
                return;
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, timeoutCts.Token);

            try
            {
                await task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!externalCt.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                // Timed out — proceed with best effort startup.
            }
        }
    }
}
