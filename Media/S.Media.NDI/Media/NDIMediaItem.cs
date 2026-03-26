using System.Collections.ObjectModel;
using NdiLib;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Input;

namespace S.Media.NDI.Media;

public sealed class NDIMediaItem : IMediaItem, IDynamicMetadata, IMediaPlaybackSourceBinding
{
    private readonly List<IAudioSource> _playbackAudioSources = [];
    private readonly List<IVideoSource> _playbackVideoSources = [];

    public NDIMediaItem(NdiDiscoveredSource source, NDIIntegrationOptions? options = null)
    {
        Source = source;
        Options = options ?? new NDIIntegrationOptions();
        AudioStreams = [new AudioStreamInfo { Codec = "NDI", SampleRate = 48_000, ChannelCount = 2 }];
        VideoStreams = [new VideoStreamInfo { Codec = "NDI", Width = 1920, Height = 1080, FrameRate = 60 }];
    }


    public NDIMediaItem(NdiReceiver receiver, NDIIntegrationOptions? options = null)
    {
        Receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        Options = options ?? new NDIIntegrationOptions();
        CaptureCoordinator = new NdiCaptureCoordinator(receiver);
        AudioStreams = [new AudioStreamInfo { Codec = "NDI", SampleRate = 48_000, ChannelCount = 2 }];
        VideoStreams = [new VideoStreamInfo { Codec = "NDI", Width = 1920, Height = 1080, FrameRate = 60 }];
    }

    internal NDIMediaItem(NdiReceiver receiver, NDIIntegrationOptions? options, NdiCaptureCoordinator captureCoordinator)
        : this(receiver, options)
    {
        CaptureCoordinator = captureCoordinator;
    }

    public NdiDiscoveredSource? Source { get; }

    public NdiReceiver? Receiver { get; }

    internal NdiCaptureCoordinator? CaptureCoordinator { get; }

    public NDIIntegrationOptions Options { get; }

    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; }

    public IReadOnlyList<VideoStreamInfo> VideoStreams { get; }

    public MediaMetadataSnapshot? Metadata { get; private set; }

    public bool HasMetadata => Metadata is not null;

    public event EventHandler<MediaMetadataSnapshot>? MetadataUpdated;

    public IReadOnlyList<IAudioSource> PlaybackAudioSources => _playbackAudioSources.ToArray();

    public IReadOnlyList<IVideoSource> PlaybackVideoSources => _playbackVideoSources.ToArray();

    public IVideoSource? InitialActiveVideoSource => _playbackVideoSources.FirstOrDefault();

    public int CreateAudioSource(out NDIAudioSource? source)
    {
        return CreateAudioSource(new NDISourceOptions(), out source);
    }

    public int CreateAudioSource(in NDISourceOptions sourceOptions, out NDIAudioSource? source)
    {
        source = new NDIAudioSource(this, sourceOptions, CaptureCoordinator);
        _playbackAudioSources.Add(source);
        return MediaResult.Success;
    }

    public int CreateVideoSource(out NDIVideoSource? source)
    {
        return CreateVideoSource(new NDISourceOptions(), out source);
    }

    public int CreateVideoSource(in NDISourceOptions sourceOptions, out NDIVideoSource? source)
    {
        source = new NDIVideoSource(this, sourceOptions, CaptureCoordinator);
        _playbackVideoSources.Add(source);
        return MediaResult.Success;
    }

    public void PublishMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        var snapshot = new MediaMetadataSnapshot
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AdditionalMetadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata)),
        };

        Metadata = snapshot;
        MetadataUpdated?.Invoke(this, snapshot);
    }
}

