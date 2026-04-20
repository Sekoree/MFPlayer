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
    void ReceiveFrame(in VideoFrame frame);

    /// <summary>Optional endpoint diagnostics snapshot.</summary>
    VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => VideoEndpointDiagnosticsSnapshot.Empty;
}

