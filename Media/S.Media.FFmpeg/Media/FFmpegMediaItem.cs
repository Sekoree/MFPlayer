using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Decoders.Internal;
using S.Media.FFmpeg.Runtime;
using S.Media.FFmpeg.Sources;

namespace S.Media.FFmpeg.Media;

public sealed class FFmpegMediaItem : IMediaItem, IMediaPlaybackSourceBinding, IDynamicMetadata, IDisposable
{
    private readonly Lock _metadataGate = new();
    private readonly bool _ownsSources;
    private readonly Stream? _ownedInputStream;
    private readonly bool _leaveOwnedInputStreamOpen;
    private readonly IReadOnlyList<IAudioSource> _playbackAudioSources;
    private readonly IReadOnlyList<IVideoSource> _playbackVideoSources;
    private readonly FFSharedDemuxSession? _sharedDemuxSession;
    private MediaMetadataSnapshot? _metadata;
    private string? _metadataSignature;
    private bool _disposed;

    public FFmpegMediaItem(FFmpegOpenOptions openOptions, FFmpegDecodeOptions? decodeOptions = null, FFmpegAudioSourceOptions? audioOptions = null)
        : this(openOptions, decodeOptions, audioOptions, ownedInputStream: null, leaveOwnedInputStreamOpen: true)
    {
    }

    public FFmpegMediaItem(
        Stream inputStream,
        bool leaveInputStreamOpen = true,
        string? inputFormatHint = null,
        FFmpegDecodeOptions? decodeOptions = null,
        FFmpegAudioSourceOptions? audioOptions = null)
        : this(
            new FFmpegOpenOptions
            {
                InputStream = inputStream,
                LeaveInputStreamOpen = leaveInputStreamOpen,
                InputFormatHint = inputFormatHint,
            },
            decodeOptions,
            audioOptions,
            ownedInputStream: inputStream,
            leaveOwnedInputStreamOpen: leaveInputStreamOpen)
    {
    }

    public FFmpegMediaItem(
        Stream inputStream,
        FFmpegOpenOptions openOptions,
        FFmpegDecodeOptions? decodeOptions = null,
        FFmpegAudioSourceOptions? audioOptions = null)
        : this(
            NormalizeStreamOpenOptions(inputStream, openOptions),
            decodeOptions,
            audioOptions,
            ownedInputStream: inputStream,
            leaveOwnedInputStreamOpen: openOptions.LeaveInputStreamOpen)
    {
    }

    private FFmpegMediaItem(
        FFmpegOpenOptions openOptions,
        FFmpegDecodeOptions? decodeOptions,
        FFmpegAudioSourceOptions? audioOptions,
        Stream? ownedInputStream,
        bool leaveOwnedInputStreamOpen)
    {
        ArgumentNullException.ThrowIfNull(openOptions);

        var effectiveDecodeOptions = (decodeOptions ?? new FFmpegDecodeOptions()).Normalize();
        var validation = FFmpegConfigValidator.Validate(openOptions, effectiveDecodeOptions, audioOptions);
        if (validation != MediaResult.Success)
        {
            throw new DecodingException((MediaErrorCode)validation, "Invalid FFmpeg open/decode configuration.");
        }

        ResolvedOpenOptions = openOptions;

        var playbackAudio = new List<IAudioSource>();
        var playbackVideo = new List<IVideoSource>();

        FFSharedDemuxSession? sharedDemuxSession = null;
        if (openOptions.UseSharedDecodeContext)
        {
            sharedDemuxSession = new FFSharedDemuxSession();
            sharedDemuxSession.StreamDescriptorsRefreshed += OnStreamDescriptorsRefreshed;
            var openCode = sharedDemuxSession.Open(openOptions, effectiveDecodeOptions);
            if (openCode != MediaResult.Success)
            {
                sharedDemuxSession.StreamDescriptorsRefreshed -= OnStreamDescriptorsRefreshed;
                sharedDemuxSession.Dispose();
                throw new DecodingException((MediaErrorCode)openCode, "Failed to open shared FFmpeg demux session.");
            }

            effectiveDecodeOptions = sharedDemuxSession.ResolvedDecodeOptions;
        }

        if (openOptions.OpenAudio)
        {
            var audioInfo = CreateAudioStreamInfo(sharedDemuxSession?.AudioStream);
            AudioSource = new FFmpegAudioSource(
                audioInfo,
                durationSeconds: ResolveDurationSeconds(audioInfo.Duration),
                isSeekable: ResolveIsSeekable(openOptions),
                audioOptions,
                sharedDemuxSession);
            playbackAudio.Add(AudioSource);
        }

        if (openOptions.OpenVideo)
        {
            var videoInfo = CreateVideoStreamInfo(sharedDemuxSession?.VideoStream);
            VideoSource = new FFmpegVideoSource(
                videoInfo,
                durationSeconds: ResolveDurationSeconds(videoInfo.Duration),
                isSeekable: ResolveIsSeekable(openOptions),
                totalFrameCount: null,
                sharedDemuxSession);
            playbackVideo.Add(VideoSource);
        }

        ResolvedDecodeOptions = effectiveDecodeOptions;

        PlaybackAudioSources = playbackAudio;
        PlaybackVideoSources = playbackVideo;
        InitialActiveVideoSource = VideoSource;
        _ownsSources = true;
        _ownedInputStream = ownedInputStream;
        _leaveOwnedInputStreamOpen = leaveOwnedInputStreamOpen;
        _playbackAudioSources = PlaybackAudioSources;
        _playbackVideoSources = PlaybackVideoSources;
        _sharedDemuxSession = sharedDemuxSession;
        AudioStreams = playbackAudio.OfType<FFmpegAudioSource>().Select(s => s.StreamInfo).ToList();
        VideoStreams = playbackVideo.OfType<FFmpegVideoSource>().Select(s => s.StreamInfo).ToList();
        SetMetadata(BuildInitialMetadata(openOptions, AudioStreams, VideoStreams, ResolveIsSeekable(openOptions)));
    }

    public FFmpegMediaItem(FFmpegAudioSource audioSource)
        : this([audioSource], [], ownsSources: true)
    {
    }

    public FFmpegMediaItem(FFmpegVideoSource videoSource)
        : this([], [videoSource], videoSource, ownsSources: true)
    {
    }

    public FFmpegMediaItem(FFmpegAudioSource audioSource, FFmpegVideoSource videoSource)
        : this([audioSource], [videoSource], videoSource, ownsSources: true)
    {
    }

    public FFmpegMediaItem(
        IReadOnlyList<IAudioSource> playbackAudioSources,
        IReadOnlyList<IVideoSource> playbackVideoSources,
        IVideoSource? initialActiveVideoSource = null,
        bool ownsSources = false)
    {
        PlaybackAudioSources = playbackAudioSources;
        PlaybackVideoSources = playbackVideoSources;
        InitialActiveVideoSource = initialActiveVideoSource;
        _ownsSources = ownsSources;
        _playbackAudioSources = playbackAudioSources;
        _playbackVideoSources = playbackVideoSources;
        _sharedDemuxSession = null;
        _ownedInputStream = null;
        _leaveOwnedInputStreamOpen = true;
        ResolvedOpenOptions = null;
        ResolvedDecodeOptions = null;

        AudioSource = playbackAudioSources.OfType<FFmpegAudioSource>().FirstOrDefault();
        VideoSource = playbackVideoSources.OfType<FFmpegVideoSource>().FirstOrDefault();
        AudioStreams = playbackAudioSources.OfType<FFmpegAudioSource>().Select(s => s.StreamInfo).ToList();
        VideoStreams = playbackVideoSources.OfType<FFmpegVideoSource>().Select(s => s.StreamInfo).ToList();
        SetMetadata(BuildInitialMetadata(
            openOptions: null,
            AudioStreams,
            VideoStreams,
            isSeekable: playbackAudioSources.OfType<FFmpegAudioSource>().FirstOrDefault()?.IsSeekable
                ?? playbackVideoSources.OfType<FFmpegVideoSource>().FirstOrDefault()?.IsSeekable
                ?? true));
    }

    /// <summary>
    /// Creates a media item from the given options without throwing.
    /// </summary>
    /// <param name="options">Open and decode options.</param>
    /// <param name="item">On success, the opened media item. <see langword="null"/> on failure.</param>
    /// <returns><c>0</c> on success; a <see cref="MediaErrorCode"/> value on failure.</returns>
    public static int Create(FFmpegOpenOptions options, out FFmpegMediaItem? item)
    {
        item = null;
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            item = new FFmpegMediaItem(options);
            return MediaResult.Success;
        }
        catch (DecodingException ex)
        {
            return (int)ex.ErrorCode;
        }
    }

    /// <summary>
    /// Creates a media item from a URI without throwing.
    /// </summary>
    /// <param name="uri">Input URI or file path.</param>
    /// <param name="item">On success, the opened media item. <see langword="null"/> on failure.</param>
    /// <returns><c>0</c> on success; a <see cref="MediaErrorCode"/> value on failure.</returns>
    public static int Create(string uri, out FFmpegMediaItem? item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return Create(new FFmpegOpenOptions { InputUri = uri }, out item);
    }

    /// <summary>
    /// Creates a media item from a <see cref="Stream"/> without throwing.
    /// </summary>
    /// <param name="inputStream">The input stream to read from.</param>
    /// <param name="item">On success, the opened media item. <see langword="null"/> on failure.</param>
    /// <param name="leaveInputStreamOpen">
    /// When <see langword="true"/> (the default), the stream is not disposed when the media item is disposed.
    /// </param>
    /// <param name="inputFormatHint">Optional FFmpeg format hint (e.g. <c>"mp4"</c>).</param>
    /// <param name="decodeOptions">Optional decode options.</param>
    /// <param name="audioOptions">Optional audio source options.</param>
    /// <returns><c>0</c> on success; a <see cref="MediaErrorCode"/> value on failure.</returns>
    public static int Create(
        Stream inputStream,
        out FFmpegMediaItem? item,
        bool leaveInputStreamOpen = true,
        string? inputFormatHint = null,
        FFmpegDecodeOptions? decodeOptions = null,
        FFmpegAudioSourceOptions? audioOptions = null)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        return Create(
            new FFmpegOpenOptions
            {
                InputStream = inputStream,
                LeaveInputStreamOpen = leaveInputStreamOpen,
                InputFormatHint = inputFormatHint,
            },
            out item);
    }


    /// <summary>
    /// Seeks all playback sources to <paramref name="positionSeconds"/> via the shared demux
    /// session when one exists, or falls through to per-source seek otherwise.
    /// </summary>
    /// <remarks>
    /// When audio and video sources share an <see cref="FFSharedDemuxSession"/>, this method
    /// seeks once and both sources are repositioned atomically. Calling
    /// <see cref="FFmpegAudioSource.Seek"/> and <see cref="FFmpegVideoSource.Seek"/> independently on
    /// sources that share a session can corrupt the decoder state.
    /// </remarks>
    /// <returns><c>0</c> on success; a <see cref="MediaErrorCode"/> value on failure.</returns>
    public int Seek(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
            return (int)MediaErrorCode.MediaInvalidArgument;

        if (_sharedDemuxSession is not null)
        {
            var seekCode = _sharedDemuxSession.Seek(positionSeconds);
            if (seekCode != MediaResult.Success)
                return seekCode;

            // Update position tracking on all owned sources WITHOUT re-seeking the session.
            // Calling src.Seek() here would cause each source to call _sharedDemuxSession.Seek()
            // a second time, flushing decoder buffers and clearing queues that were just rebuilt.
            foreach (var src in _playbackAudioSources)
                if (src is FFmpegAudioSource ff) ff.NotifySeek(positionSeconds);
            foreach (var src in _playbackVideoSources)
                if (src is FFmpegVideoSource fv) fv.NotifySeek(positionSeconds);

            return MediaResult.Success;
        }

        // No shared session — seek each source independently.
        var result = MediaResult.Success;
        foreach (var src in _playbackAudioSources)
        {
            var r = src.Seek(positionSeconds);
            if (r != MediaResult.Success) result = r;
        }
        foreach (var src in _playbackVideoSources)
        {
            var r = src.Seek(positionSeconds);
            if (r != MediaResult.Success) result = r;
        }
        return result;
    }

    /// <summary>
    /// The primary FFmpeg audio source, or <see langword="null"/> if this item was constructed
    /// from an external source list or without audio (<c>OpenAudio = false</c>).
    /// </summary>
    /// <remarks>
    /// <b>Warning:</b> Do not use this as a null-check for "has audio".
    /// Check <c>PlaybackAudioSources.Count &gt; 0</c> instead — <see cref="AudioSource"/> can be
    /// <see langword="null"/> even when <see cref="PlaybackAudioSources"/> is non-empty (composite
    /// constructor path with non-<see cref="FFmpegAudioSource"/> entries).
    /// </remarks>
    public FFmpegAudioSource? AudioSource { get; }

    /// <summary>
    /// The primary FFmpeg video source, or <see langword="null"/> if this item was constructed
    /// from an external source list or without video (<c>OpenVideo = false</c>).
    /// </summary>
    /// <remarks>
    /// <b>Warning:</b> Do not use this as a null-check for "has video".
    /// Check <c>PlaybackVideoSources.Count &gt; 0</c> instead — <see cref="VideoSource"/> can be
    /// <see langword="null"/> even when <see cref="PlaybackVideoSources"/> is non-empty (composite
    /// constructor path with non-<see cref="FFmpegVideoSource"/> entries).
    /// </remarks>
    public FFmpegVideoSource? VideoSource { get; }

    public FFmpegOpenOptions? ResolvedOpenOptions { get; }

    public FFmpegDecodeOptions? ResolvedDecodeOptions { get; }

    internal FFSharedDemuxSession? SharedDemuxSession => _sharedDemuxSession;

    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; }

    public IReadOnlyList<VideoStreamInfo> VideoStreams { get; }

    public MediaMetadataSnapshot? Metadata => _metadata;

    public bool HasMetadata => _metadata is not null;

    public MediaMetadataSnapshot? GetMetadata() => _metadata;

    public event EventHandler<MediaMetadataSnapshot>? MetadataChanged;

    public IReadOnlyList<IAudioSource> PlaybackAudioSources { get; }

    public IReadOnlyList<IVideoSource> PlaybackVideoSources { get; }

    public IVideoSource? InitialActiveVideoSource { get; }

    public void Dispose()
    {
        lock (_metadataGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            MetadataChanged = null;
        }

        if (!_ownsSources)
        {
            DisposeOwnedInputStreamIfNeeded();
            return;
        }

        foreach (var source in _playbackAudioSources)
        {
            source.Dispose();
        }

        foreach (var source in _playbackVideoSources)
        {
            source.Dispose();
        }

        if (_sharedDemuxSession is not null)
        {
            _sharedDemuxSession.StreamDescriptorsRefreshed -= OnStreamDescriptorsRefreshed;
        }

        _sharedDemuxSession?.Dispose();

        DisposeOwnedInputStreamIfNeeded();
    }

    private static FFmpegOpenOptions NormalizeStreamOpenOptions(Stream inputStream, FFmpegOpenOptions openOptions)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(openOptions);

        if (!string.IsNullOrWhiteSpace(openOptions.InputUri) || openOptions.InputStream is not null)
        {
            throw new DecodingException(MediaErrorCode.FFmpegInvalidConfig, "Stream overload requires openOptions without InputUri/InputStream.");
        }

        return openOptions with { InputStream = inputStream };
    }

    private void DisposeOwnedInputStreamIfNeeded()
    {
        if (_ownedInputStream is null || _leaveOwnedInputStreamOpen)
        {
            return;
        }

        _ownedInputStream.Dispose();
    }

    private void SetMetadata(MediaMetadataSnapshot? metadata)
    {
        EventHandler<MediaMetadataSnapshot>? handler;
        var signature = ComputeMetadataSignature(metadata);

        lock (_metadataGate)
        {
            if (_disposed)
            {
                return;
            }

            if (string.Equals(_metadataSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _metadataSignature = signature;
            _metadata = metadata;
            handler = metadata is null ? null : MetadataChanged;
        }

        if (metadata is not null)
        {
            handler?.Invoke(this, metadata);
        }
    }

    private static double ResolveDurationSeconds(TimeSpan? duration)
    {
        if (!duration.HasValue)
        {
            return double.NaN;
        }

        return duration.Value.TotalSeconds >= 0 ? duration.Value.TotalSeconds : double.NaN;
    }

    private static bool ResolveIsSeekable(FFmpegOpenOptions openOptions)
    {
        return openOptions.InputStream?.CanSeek ?? true;
    }

    private static MediaMetadataSnapshot? BuildInitialMetadata(
        FFmpegOpenOptions? openOptions,
        IReadOnlyList<AudioStreamInfo> audioStreams,
        IReadOnlyList<VideoStreamInfo> videoStreams,
        bool isSeekable)
    {
        var entries = new Dictionary<string, string>
        {
            ["stream.audio.count"] = audioStreams.Count.ToString(),
            ["stream.video.count"] = videoStreams.Count.ToString(),
            ["media.seekable"] = isSeekable ? "true" : "false",
        };

        var firstAudioCodec = audioStreams.FirstOrDefault().Codec;
        if (!string.IsNullOrWhiteSpace(firstAudioCodec))
        {
            entries["stream.audio.codec"] = firstAudioCodec;
        }

        var firstVideoCodec = videoStreams.FirstOrDefault().Codec;
        if (!string.IsNullOrWhiteSpace(firstVideoCodec))
        {
            entries["stream.video.codec"] = firstVideoCodec;
        }

        if (openOptions is not null)
        {
            if (!string.IsNullOrWhiteSpace(openOptions.InputUri))
            {
                entries["open.inputUri"] = openOptions.InputUri!;
            }

            if (!string.IsNullOrWhiteSpace(openOptions.InputFormatHint))
            {
                entries["open.inputFormatHint"] = openOptions.InputFormatHint!;
            }
        }

        return entries.Count == 0
            ? null
            : new MediaMetadataSnapshot
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                AdditionalMetadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(entries),
            };
    }

    private static string? ComputeMetadataSignature(MediaMetadataSnapshot? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var entries = metadata.AdditionalMetadata;
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        // Sort keys without LINQ to avoid per-call allocations.
        var keys = new string[entries.Count];
        var i = 0;
        foreach (var kvp in entries)
            keys[i++] = kvp.Key;
        Array.Sort(keys, StringComparer.Ordinal);

        var sb = new System.Text.StringBuilder(entries.Count * 32);
        for (var k = 0; k < keys.Length; k++)
        {
            if (k > 0) sb.Append('|');
            sb.Append(keys[k]).Append('=').Append(entries[keys[k]]);
        }

        return sb.ToString();
    }

    private void OnStreamDescriptorsRefreshed(object? sender, FFStreamDescriptorSnapshot snapshot)
    {
        var openOptions = ResolvedOpenOptions;
        var seekable = openOptions is null || ResolveIsSeekable(openOptions);
        var audioStreams = snapshot.AudioStream is null
            ? Array.Empty<AudioStreamInfo>()
            : [CreateAudioStreamInfo(snapshot.AudioStream)];
        var videoStreams = snapshot.VideoStream is null
            ? Array.Empty<VideoStreamInfo>()
            : [CreateVideoStreamInfo(snapshot.VideoStream)];

        SetMetadata(BuildInitialMetadata(openOptions, audioStreams, videoStreams, seekable));
    }

    private static AudioStreamInfo CreateAudioStreamInfo(FFStreamDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return new AudioStreamInfo();
        }

        return new AudioStreamInfo
        {
            Codec = descriptor.Value.CodecName,
            SampleRate = descriptor.Value.SampleRate,
            ChannelCount = descriptor.Value.ChannelCount,
            Duration = descriptor.Value.Duration,
        };
    }

    private static VideoStreamInfo CreateVideoStreamInfo(FFStreamDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return new VideoStreamInfo();
        }

        return new VideoStreamInfo
        {
            Codec = descriptor.Value.CodecName,
            Width = descriptor.Value.Width,
            Height = descriptor.Value.Height,
            FrameRate = descriptor.Value.FrameRate,
            Duration = descriptor.Value.Duration,
        };
    }
}
