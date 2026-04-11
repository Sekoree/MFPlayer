using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// A secondary video destination that receives copies of presented frames.
/// </summary>
public interface IVideoSink : IMediaEndpoint
{
    /// <summary>
    /// Receives a presented frame. Implementations must be non-blocking
    /// (copy the data and return immediately).
    /// </summary>
    void ReceiveFrame(in VideoFrame frame);

    /// <summary>
    /// Optional endpoint diagnostics snapshot.
    /// </summary>
    VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot() => VideoEndpointDiagnosticsSnapshot.Empty;
}
