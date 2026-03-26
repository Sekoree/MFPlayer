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

public sealed class FFMediaItem : IMediaItem, IMediaPlaybackSourceBinding, IDynamicMetadata, IDisposable
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

    public FFMediaItem(FFmpegOpenOptions openOptions, FFmpegDecodeOptions? decodeOptions = null, FFAudioSourceOptions? audioOptions = null)
        : this(openOptions, decodeOptions, audioOptions, ownedInputStream: null, leaveOwnedInputStreamOpen: true)
    {
    }

    public FFMediaItem(
        Stream inputStream,
        bool leaveInputStreamOpen = true,
        string? inputFormatHint = null,
        FFmpegDecodeOptions? decodeOptions = null,
        FFAudioSourceOptions? audioOptions = null)
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

    public FFMediaItem(
        Stream inputStream,
        FFmpegOpenOptions openOptions,
        FFmpegDecodeOptions? decodeOptions = null,
        FFAudioSourceOptions? audioOptions = null)
        : this(
            NormalizeStreamOpenOptions(inputStream, openOptions),
            decodeOptions,
            audioOptions,
            ownedInputStream: inputStream,
            leaveOwnedInputStreamOpen: openOptions.LeaveInputStreamOpen)
    {
    }

    private FFMediaItem(
        FFmpegOpenOptions openOptions,
        FFmpegDecodeOptions? decodeOptions,
        FFAudioSourceOptions? audioOptions,
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
            AudioSource = new FFAudioSource(
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
            VideoSource = new FFVideoSource(
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
        AudioStreams = playbackAudio.OfType<FFAudioSource>().Select(s => s.StreamInfo).ToList();
        VideoStreams = playbackVideo.OfType<FFVideoSource>().Select(s => s.StreamInfo).ToList();
        SetMetadata(BuildInitialMetadata(openOptions, AudioStreams, VideoStreams, ResolveIsSeekable(openOptions)));
    }

    public FFMediaItem(FFAudioSource audioSource)
        : this([audioSource], [], ownsSources: true)
    {
    }

    public FFMediaItem(FFVideoSource videoSource)
        : this([], [videoSource], videoSource, ownsSources: true)
    {
    }

    public FFMediaItem(FFAudioSource audioSource, FFVideoSource videoSource)
        : this([audioSource], [videoSource], videoSource, ownsSources: true)
    {
    }

    public FFMediaItem(
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

        AudioSource = playbackAudioSources.OfType<FFAudioSource>().FirstOrDefault();
        VideoSource = playbackVideoSources.OfType<FFVideoSource>().FirstOrDefault();
        AudioStreams = playbackAudioSources.OfType<FFAudioSource>().Select(s => s.StreamInfo).ToList();
        VideoStreams = playbackVideoSources.OfType<FFVideoSource>().Select(s => s.StreamInfo).ToList();
        SetMetadata(BuildInitialMetadata(
            openOptions: null,
            AudioStreams,
            VideoStreams,
            isSeekable: playbackAudioSources.OfType<FFAudioSource>().FirstOrDefault()?.IsSeekable
                ?? playbackVideoSources.OfType<FFVideoSource>().FirstOrDefault()?.IsSeekable
                ?? true));
    }

    /// <summary>
    /// Opens a media item from a URI, returning both audio and video sources via shared decode context.
    /// Throws <see cref="DecodingException"/> on failure.
    /// </summary>
    public static FFMediaItem Open(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return new FFMediaItem(new FFmpegOpenOptions { InputUri = uri });
    }

    /// <summary>
    /// Opens a media item from a <see cref="Stream"/>, returning both audio and video sources via shared decode context.
    /// Throws <see cref="DecodingException"/> on failure.
    /// </summary>
    public static FFMediaItem Open(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new FFMediaItem(stream, leaveOpen);
    }

    /// <summary>
    /// Attempts to open a media item from a URI without throwing.
    /// Returns <c>true</c> on success; <paramref name="item"/> is <c>null</c> on failure.
    /// </summary>
    public static bool TryOpen(string uri, out FFMediaItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        try
        {
            item = Open(uri);
            return true;
        }
        catch (DecodingException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to open a media item from a <see cref="Stream"/> without throwing.
    /// Returns <c>true</c> on success; <paramref name="item"/> is <c>null</c> on failure.
    /// </summary>
    public static bool TryOpen(Stream? stream, out FFMediaItem? item, bool leaveOpen = true)
    {
        item = null;
        if (stream is null)
        {
            return false;
        }

        try
        {
            item = Open(stream, leaveOpen);
            return true;
        }
        catch (DecodingException)
        {
            return false;
        }
    }

    public FFAudioSource? AudioSource { get; }

    public FFVideoSource? VideoSource { get; }

    public FFmpegOpenOptions? ResolvedOpenOptions { get; }

    public FFmpegDecodeOptions? ResolvedDecodeOptions { get; }

    internal FFSharedDemuxSession? SharedDemuxSession => _sharedDemuxSession;

    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; }

    public IReadOnlyList<VideoStreamInfo> VideoStreams { get; }

    public MediaMetadataSnapshot? Metadata => _metadata;

    public bool HasMetadata => _metadata is not null;

    public event EventHandler<MediaMetadataSnapshot>? MetadataUpdated;

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
            MetadataUpdated = null;
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
            handler = metadata is null ? null : MetadataUpdated;
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

        var ordered = metadata.AdditionalMetadata.OrderBy(kvp => kvp.Key, StringComparer.Ordinal);
        return string.Join("|", ordered.Select(kvp => $"{kvp.Key}={kvp.Value}"));
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
            return new AudioStreamInfo
            {
                Codec = "pcm_f32le",
                SampleRate = 48_000,
                ChannelCount = 2,
            };
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
            return new VideoStreamInfo
            {
                Codec = "placeholder_rgba",
                Width = 2,
                Height = 2,
                FrameRate = 30d,
            };
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

