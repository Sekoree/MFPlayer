using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Pull-oriented video frame source for endpoint-requested frame flows.
/// </summary>
public interface IVideoFramePullSource
{
    ValueTask<VideoFrame?> ReadFrameAsync(CancellationToken ct = default);
}

