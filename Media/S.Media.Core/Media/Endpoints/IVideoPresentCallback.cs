using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Implemented by the graph. Set on <see cref="IPullVideoEndpoint"/> at registration time.
/// The endpoint calls this from its render loop.
/// </summary>
public interface IVideoPresentCallback
{
    /// <summary>
    /// Attempts to get the next video frame for presentation.
    /// Returns <see langword="true"/> if a frame is available; <see langword="false"/> otherwise.
    /// Uses out parameter to avoid nullable struct boxing.
    /// </summary>
    /// <remarks>
    /// Legacy overload. Pull endpoints that want zero-copy retention should call
    /// <see cref="TryPresentNext(TimeSpan, out VideoFrameHandle)"/> instead so the
    /// returned handle exposes explicit <see cref="VideoFrameHandle.Retain"/> /
    /// <see cref="VideoFrameHandle.Release"/> semantics (§3.11).
    /// </remarks>
    bool TryPresentNext(TimeSpan clockPosition, out VideoFrame frame);

    /// <summary>
    /// Ref-counted variant of <see cref="TryPresentNext(TimeSpan, out VideoFrame)"/>.
    /// The default implementation wraps the legacy frame in a
    /// <see cref="VideoFrameHandle"/>; pull endpoints may override for explicit retain
    /// paths (e.g. holding a handle across a GPU upload queue boundary).
    /// </summary>
    bool TryPresentNext(TimeSpan clockPosition, out VideoFrameHandle handle)
    {
        if (TryPresentNext(clockPosition, out VideoFrame frame))
        {
            handle = new VideoFrameHandle(in frame);
            return true;
        }
        handle = default;
        return false;
    }
}
