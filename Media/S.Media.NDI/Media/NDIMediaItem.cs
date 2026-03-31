using System.Collections.ObjectModel;
using NDILib;
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

    /// <summary>
    /// Creates a media item for a discovered (not yet connected) NDI source.
    /// Stream metadata is unknown until a receiver is connected and frames start arriving.
    /// </summary>
    public NDIMediaItem(NdiDiscoveredSource source, NDIIntegrationOptions? options = null)
    {
        Source = source;
        Options = options ?? new NDIIntegrationOptions();
        // Width/Height/FrameRate are null — actual values come from NDIVideoSource.StreamInfo
        // once capture begins (Issue 5.16).
        AudioStreams = [new AudioStreamInfo { Codec = "NDI", SampleRate = 48_000, ChannelCount = 2 }];
        VideoStreams = [new VideoStreamInfo { Codec = "NDI" }];
    }

    /// <summary>
    /// Creates a media item backed by an active <see cref="NDIReceiver"/>.
    /// A shared <see cref="NDIFrameSyncCoordinator"/> is created automatically and used by
    /// all sources created from this item.  Falls back to <see cref="NDICaptureCoordinator"/>
    /// when the NDI frame-sync cannot be initialised (e.g. unsupported runtime version).
    /// </summary>
    public NDIMediaItem(NDIReceiver receiver, NDIIntegrationOptions? options = null, NDILimitsOptions? limits = null)
    {
        Receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        Options = options ?? new NDIIntegrationOptions();
        var effectiveLimits = limits ?? new NDILimitsOptions();

        // Prefer NDIFrameSyncCoordinator (SDK-managed TBC, dynamic audio resampling).
        // Fall back to manual NDICaptureCoordinator when framesync creation fails.
        if (NDIFrameSyncCoordinator.Create(out var fsCoordinator, receiver) == 0 && fsCoordinator is not null)
        {
            CaptureCoordinator = fsCoordinator;
        }
        else
        {
            CaptureCoordinator = new NDICaptureCoordinator(
                receiver,
                effectiveLimits.MaxPendingVideoFrames,
                effectiveLimits.MaxPendingAudioFrames);
        }

        // Width/Height/FrameRate are null — populated from first captured frame (Issue 5.16).
        AudioStreams = [new AudioStreamInfo { Codec = "NDI", SampleRate = 48_000, ChannelCount = 2 }];
        VideoStreams = [new VideoStreamInfo { Codec = "NDI" }];
    }

    internal NDIMediaItem(NDIReceiver receiver, NDIIntegrationOptions options, INDICaptureCoordinator captureCoordinator)
    {
        Receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        Options = options ?? new NDIIntegrationOptions();
        CaptureCoordinator = captureCoordinator;
        AudioStreams = [new AudioStreamInfo { Codec = "NDI", SampleRate = 48_000, ChannelCount = 2 }];
        VideoStreams = [new VideoStreamInfo { Codec = "NDI" }];
    }

    public NdiDiscoveredSource? Source { get; }
    public NDIReceiver? Receiver { get; }
    internal INDICaptureCoordinator? CaptureCoordinator { get; }
    public NDIIntegrationOptions Options { get; }
    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; }
    public IReadOnlyList<VideoStreamInfo> VideoStreams { get; }
    public MediaMetadataSnapshot? Metadata { get; private set; }
    public bool HasMetadata => Metadata is not null;
    public event EventHandler<MediaMetadataSnapshot>? MetadataChanged;
    public MediaMetadataSnapshot? GetMetadata() => Metadata;

    // Issue 5.18: AsReadOnly() returns a thin wrapper — no per-call allocation.
    public IReadOnlyList<IAudioSource> PlaybackAudioSources => _playbackAudioSources.AsReadOnly();
    public IReadOnlyList<IVideoSource> PlaybackVideoSources => _playbackVideoSources.AsReadOnly();
    public IVideoSource? InitialActiveVideoSource => _playbackVideoSources.FirstOrDefault();

    public int CreateAudioSource(out NDIAudioSource? source)
        => CreateAudioSource(new NDISourceOptions(), out source);

    public int CreateAudioSource(in NDISourceOptions sourceOptions, out NDIAudioSource? source)
    {
        source = new NDIAudioSource(this, sourceOptions, CaptureCoordinator);
        _playbackAudioSources.Add(source);
        return MediaResult.Success;
    }

    public int CreateVideoSource(out NDIVideoSource? source)
        => CreateVideoSource(new NDISourceOptions(), out source);

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
        MetadataChanged?.Invoke(this, snapshot);
    }
}
