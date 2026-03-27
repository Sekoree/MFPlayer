namespace S.Media.Core.Media;

public interface IMediaItem
{
    IReadOnlyList<AudioStreamInfo> AudioStreams { get; }

    IReadOnlyList<VideoStreamInfo> VideoStreams { get; }

    MediaMetadataSnapshot? Metadata { get; }

    bool HasMetadata { get; }
}
