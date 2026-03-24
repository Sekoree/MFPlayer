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

public sealed class FFMediaItem : IMediaItem, IMediaPlaybackSourceBinding, IDisposable
{
    private readonly bool _ownsSources;
    private readonly Stream? _ownedInputStream;
    private readonly bool _leaveOwnedInputStreamOpen;
    private readonly IReadOnlyList<IAudioSource> _playbackAudioSources;
    private readonly IReadOnlyList<IVideoSource> _playbackVideoSources;
    private readonly FFSharedDemuxSession? _sharedDemuxSession;

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
            var openCode = sharedDemuxSession.Open(openOptions, effectiveDecodeOptions);
            if (openCode != MediaResult.Success)
            {
                sharedDemuxSession.Dispose();
                throw new DecodingException((MediaErrorCode)openCode, "Failed to open shared FFmpeg demux session.");
            }

            effectiveDecodeOptions = sharedDemuxSession.ResolvedDecodeOptions;
        }

        if (openOptions.OpenAudio)
        {
            var audioInfo = CreateAudioStreamInfo(sharedDemuxSession?.AudioStream);
            AudioSource = new FFAudioSource(audioInfo, durationSeconds: double.NaN, isSeekable: true, audioOptions, sharedDemuxSession);
            playbackAudio.Add(AudioSource);
        }

        if (openOptions.OpenVideo)
        {
            var videoInfo = CreateVideoStreamInfo(sharedDemuxSession?.VideoStream);
            VideoSource = new FFVideoSource(videoInfo, durationSeconds: double.NaN, isSeekable: true, totalFrameCount: null, sharedDemuxSession);
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
    }

    public FFAudioSource? AudioSource { get; }

    public FFVideoSource? VideoSource { get; }

    public FFmpegOpenOptions? ResolvedOpenOptions { get; }

    public FFmpegDecodeOptions? ResolvedDecodeOptions { get; }

    internal FFSharedDemuxSession? SharedDemuxSession => _sharedDemuxSession;

    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; }

    public IReadOnlyList<VideoStreamInfo> VideoStreams { get; }

    public MediaMetadataSnapshot? Metadata => null;

    public bool HasMetadata => false;

    public IReadOnlyList<IAudioSource> PlaybackAudioSources { get; }

    public IReadOnlyList<IVideoSource> PlaybackVideoSources { get; }

    public IVideoSource? InitialActiveVideoSource { get; }

    public void Dispose()
    {
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

