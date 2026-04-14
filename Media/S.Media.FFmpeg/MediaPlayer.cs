using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

public enum PlaybackState
{
    Idle,
    Opening,
    Ready,
    Playing,
    Paused,
    Stopping,
    Stopped,
    Faulted
}

public enum PlaybackCompletedReason
{
    SourceEnded,
    StoppedByUser,
    ReplacedByOpen,
    Faulted,
}

public enum PlaybackFailureStage
{
    Open,
    Play,
    Pause,
    Stop,
    Runtime,
}

public sealed record PlaybackStateChangedEventArgs(PlaybackState Previous, PlaybackState Current);
public sealed record PlaybackCompletedEventArgs(PlaybackCompletedReason Reason);
public sealed record PlaybackFailedEventArgs(PlaybackFailureStage Stage, Exception Exception);

/// <summary>
/// High-level one-source one-output playback facade built on
/// <see cref="FFmpegDecoder"/> and <see cref="AVMixer"/>.
/// </summary>
/// <remarks>
/// Typical audio-only usage:
/// <code>
/// using var player = new MediaPlayer(audioOutput);
/// player.PlaybackEnded += (_, _) => cts.Cancel();
/// await player.OpenAsync("file.mp3");
/// await player.PlayAsync();
/// try { await Task.Delay(Timeout.Infinite, cts.Token); }
/// catch (OperationCanceledException) { }
/// await player.StopAsync();
/// </code>
/// </remarks>
public sealed class MediaPlayer : IDisposable
{
    private readonly IAudioOutput? _audioOutput;
    private readonly IVideoOutput? _videoOutput;

    private FFmpegDecoder? _decoder;
    private AVMixer?        _mixer;
    private float           _volume         = 1.0f;
    private bool            _decoderStarted;
    private bool            _disposed;
    private PlaybackState   _state = PlaybackState.Idle;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="MediaPlayer"/> that routes decoded content to the supplied outputs.
    /// Both outputs must already be opened (device and format configured) before calling
    /// <see cref="OpenAsync(string,FFmpegDecoderOptions?,CancellationToken)"/>.
    /// Neither output is owned or disposed by the player — the caller retains ownership.
    /// </summary>
    /// <param name="audioOutput">Pre-opened audio output, or <see langword="null"/> for video-only.</param>
    /// <param name="videoOutput">Pre-opened video output, or <see langword="null"/> for audio-only.</param>
    public MediaPlayer(IAudioOutput? audioOutput = null, IVideoOutput? videoOutput = null)
    {
        if (audioOutput == null && videoOutput == null)
            throw new ArgumentException("At least one output (audio or video) must be provided.",
                nameof(audioOutput));
        _audioOutput = audioOutput;
        _videoOutput = videoOutput;
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised (on a background thread) when all packets have been demuxed from
    /// the current media source. Subscribe to cancel a wait handle or trigger the next track.
    /// </summary>
    public event EventHandler? PlaybackEnded;
    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;
    public event EventHandler<PlaybackCompletedEventArgs>? PlaybackCompleted;
    public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;

    // ── Properties ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> while the output is actively rendering.
    /// </summary>
    public bool IsPlaying => _decoderStarted && !_disposed;

    /// <summary>
    /// Current decode position.
    /// Returns <see cref="TimeSpan.Zero"/> when no media is open.
    /// </summary>
    public TimeSpan Position =>
        AudioChannel?.Position ?? VideoChannel?.Position ?? TimeSpan.Zero;

    /// <summary>
    /// Playback volume applied to the audio channel. Range [0..2], default 1.0.
    /// May be set before or after
    /// <see cref="OpenAsync(string,FFmpegDecoderOptions?,CancellationToken)"/>;
    /// the value is preserved across <see cref="OpenAsync"/> calls.
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (AudioChannel is { } ch) ch.Volume = value;
        }
    }

    /// <summary>
    /// The first audio channel of the currently open decoder,
    /// or <see langword="null"/> when no media is open or the source has no audio.
    /// </summary>
    public IAudioChannel? AudioChannel => _decoder?.FirstAudioChannel;

    /// <summary>
    /// The first video channel of the currently open decoder,
    /// or <see langword="null"/> when no media is open or the source has no video.
    /// </summary>
    public IVideoChannel? VideoChannel => _decoder?.FirstVideoChannel;

    /// <summary>
    /// Exposes the currently active mixer for advanced routing scenarios.
    /// Returns <see langword="null"/> when no media session is attached.
    /// </summary>
    public IAVMixer? Mixer => _mixer;

    // ── Open ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the media file at <paramref name="path"/> and prepares the pipeline.
    /// Any previously open media is stopped and released first.
    /// Call <see cref="PlayAsync"/> to begin rendering.
    /// </summary>
    public async Task OpenAsync(
        string                path,
        FFmpegDecoderOptions? options = null,
        CancellationToken     ct     = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Opening);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.ReplacedByOpen).ConfigureAwait(false);
            AttachDecoder(FFmpegDecoder.Open(path, options));
            SetState(PlaybackState.Ready);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Open, ex));
            throw;
        }
    }

    /// <summary>
    /// Opens the media from <paramref name="stream"/> and prepares the pipeline.
    /// Any previously open media is stopped and released first.
    /// Call <see cref="PlayAsync"/> to begin rendering.
    /// </summary>
    public async Task OpenAsync(
        Stream                stream,
        FFmpegDecoderOptions? options   = null,
        bool                  leaveOpen = false,
        CancellationToken     ct        = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Opening);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.ReplacedByOpen).ConfigureAwait(false);
            AttachDecoder(FFmpegDecoder.Open(stream, options, leaveOpen));
            SetState(PlaybackState.Ready);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Open, ex));
            throw;
        }
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts or resumes playback of the currently open media.
    /// The decoder is started on the first call; subsequent calls resume the hardware output.
    /// </summary>
    /// <exception cref="InvalidOperationException">No media has been opened.</exception>
    public async Task PlayAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decoder == null)
            throw new InvalidOperationException(
                "No media is open. Call OpenAsync first.");

        try
        {
            if (!_decoderStarted)
            {
                _decoder.Start();
                _decoderStarted = true;
            }

            if (_audioOutput != null)
                await _audioOutput.StartAsync(ct).ConfigureAwait(false);
            if (_videoOutput != null)
                await _videoOutput.StartAsync(ct).ConfigureAwait(false);

            SetState(PlaybackState.Playing);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Play, ex));
            throw;
        }
    }

    /// <summary>
    /// Pauses playback by stopping the hardware output callbacks.
    /// The decode pipeline keeps running so the ring buffers stay warm.
    /// Call <see cref="PlayAsync"/> to resume from the current position.
    /// </summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        try
        {
            if (_audioOutput != null)
                await _audioOutput.StopAsync(ct).ConfigureAwait(false);
            if (_videoOutput != null)
                await _videoOutput.StopAsync(ct).ConfigureAwait(false);
            SetState(PlaybackState.Paused);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Pause, ex));
            throw;
        }
    }

    /// <summary>
    /// Stops playback and releases the current media. The player may be
    /// reused by calling <see cref="OpenAsync"/> again.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(PlaybackState.Stopping);
        try
        {
            await CloseAsync(ct, PlaybackCompletedReason.StoppedByUser).ConfigureAwait(false);
            SetState(PlaybackState.Stopped);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Faulted);
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(PlaybackFailureStage.Stop, ex));
            throw;
        }
    }

    /// <summary>
    /// Seeks to <paramref name="position"/>.
    /// Silently ignored for non-seekable streams.
    /// </summary>
    public void Seek(TimeSpan position) => _decoder?.Seek(position);

    /// <summary>
    /// Registers an additional audio sink and optionally routes the current first audio channel.
    /// </summary>
    public void AddAudioSink(IAudioSink sink, ChannelRouteMap? routeMap = null, int channels = 0, bool autoRouteFirstChannel = true)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var mixer = RequireMixer();
        mixer.RegisterAudioSink(sink, channels);

        if (autoRouteFirstChannel && AudioChannel is { } ch)
        {
            var map = routeMap ?? ChannelRouteMap.Auto(ch.SourceFormat.Channels, channels > 0 ? channels : _audioOutput?.HardwareFormat.Channels ?? ch.SourceFormat.Channels);
            mixer.RouteAudioChannelToSink(ch.Id, sink, map);
        }
    }

    /// <summary>
    /// Registers an additional video sink and optionally routes the current first video channel.
    /// </summary>
    public void AddVideoSink(IVideoSink sink, bool autoRouteFirstChannel = true)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var mixer = RequireMixer();
        mixer.RegisterVideoSink(sink);

        if (autoRouteFirstChannel && VideoChannel is { } ch)
            mixer.RouteVideoChannelToSink(ch.Id, sink);
    }

    /// <summary>
    /// Registers an additional audio endpoint and optionally routes the current first audio channel.
    /// </summary>
    public void AddAudioEndpoint(IAudioBufferEndpoint endpoint, ChannelRouteMap? routeMap = null, int channels = 0, bool autoRouteFirstChannel = true)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var mixer = RequireMixer();
        mixer.RegisterAudioEndpoint(endpoint, channels);

        if (autoRouteFirstChannel && AudioChannel is { } ch)
        {
            if (routeMap != null)
                mixer.RouteAudioChannelToEndpoint(ch.Id, endpoint, routeMap);
            else
                mixer.RouteAudioChannelToEndpoint(ch.Id, endpoint);
        }
    }

    /// <summary>
    /// Registers an additional video endpoint and optionally routes the current first video channel.
    /// </summary>
    public void AddVideoEndpoint(IVideoFrameEndpoint endpoint, bool autoRouteFirstChannel = true)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var mixer = RequireMixer();
        mixer.RegisterVideoEndpoint(endpoint);

        if (autoRouteFirstChannel && VideoChannel is { } ch)
            mixer.RouteVideoChannelToEndpoint(ch.Id, endpoint);
    }

    public void RemoveAudioSink(IAudioSink sink) => RequireMixer().UnregisterAudioSink(sink);
    public void RemoveVideoSink(IVideoSink sink) => RequireMixer().UnregisterVideoSink(sink);
    public void RemoveAudioEndpoint(IAudioBufferEndpoint endpoint) => RequireMixer().UnregisterAudioEndpoint(endpoint);
    public void RemoveVideoEndpoint(IVideoFrameEndpoint endpoint) => RequireMixer().UnregisterVideoEndpoint(endpoint);

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseSession();
        SetState(PlaybackState.Stopped);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void AttachDecoder(FFmpegDecoder decoder)
    {
        var audioFmt = _audioOutput?.HardwareFormat ?? new AudioFormat(48000, 2);
        var videoFmt = _videoOutput?.OutputFormat;

        var mixer = videoFmt.HasValue
            ? new AVMixer(audioFmt, videoFmt.Value)
            : new AVMixer(audioFmt);

        if (_audioOutput != null && decoder.FirstAudioChannel is { } audioCh)
        {
            mixer.AddAudioChannel(audioCh);
            audioCh.Volume = _volume;
            mixer.AttachAudioOutput(_audioOutput);
        }

        if (_videoOutput != null && decoder.FirstVideoChannel is { } videoCh)
        {
            mixer.AddVideoChannel(videoCh);
            mixer.AttachVideoOutput(_videoOutput);
        }

        decoder.EndOfMedia += OnEndOfMedia;

        _decoder        = decoder;
        _mixer          = mixer;
        _decoderStarted = false;
    }

    private async Task CloseAsync(CancellationToken ct, PlaybackCompletedReason? closeReason = null)
    {
        bool hadSession = _decoder != null || _mixer != null || _decoderStarted;
        if (_decoderStarted)
        {
            if (_audioOutput != null)
            {
                try { await _audioOutput.StopAsync(ct).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
            if (_videoOutput != null)
            {
                try { await _videoOutput.StopAsync(ct).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
        ReleaseSession();
        if (hadSession && closeReason.HasValue)
            PlaybackCompleted?.Invoke(this, new PlaybackCompletedEventArgs(closeReason.Value));
    }

    private void ReleaseSession()
    {
        _decoder?.Dispose();
        _mixer?.Dispose();
        _decoder        = null;
        _mixer          = null;
        _decoderStarted = false;
    }

    private void OnEndOfMedia(object? sender, EventArgs e)
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
        PlaybackCompleted?.Invoke(this, new PlaybackCompletedEventArgs(PlaybackCompletedReason.SourceEnded));
    }

    private void SetState(PlaybackState next)
    {
        var prev = _state;
        if (prev == next)
            return;

        _state = next;
        PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(prev, next));
    }

    private IAVMixer RequireMixer()
        => _mixer ?? throw new InvalidOperationException("No active media session. Call OpenAsync first.");
}

