using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Receives video frames from the graph. Replaces <c>IVideoOutput</c>, <c>IVideoSink</c>,
/// and <c>IVideoFrameEndpoint</c> with a single unified push contract.
/// </summary>
public interface IVideoEndpoint : IMediaEndpoint
{
    /// <summary>
    /// Called by the graph to deliver a video frame.
    /// Implementations MUST be non-blocking.
    /// </summary>
    /// <remarks>
    /// <b>Ownership:</b> the caller (router) owns <paramref name="frame"/>.<c>MemoryOwner</c>.
    /// Implementations MUST NOT dispose it.  If the endpoint needs to retain the pixel data
    /// past the call (e.g. to render on a later tick), it MUST copy the bytes into its own
    /// buffer.  See <see cref="VideoFrame"/> docs for the full contract.
    /// </remarks>
    void ReceiveFrame(in VideoFrame frame);

    /// <summary>Optional endpoint diagnostics snapshot.</summary>
    VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => VideoEndpointDiagnosticsSnapshot.Empty;
}
