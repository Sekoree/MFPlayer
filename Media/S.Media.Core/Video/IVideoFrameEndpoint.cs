using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Unified push endpoint contract for video frames.
/// Implemented by sink/output adapters during API unification.
/// </summary>
public interface IVideoFrameEndpoint : IDisposable
{
    string Name { get; }
    bool IsRunning { get; }
    IReadOnlyList<PixelFormat> SupportedPixelFormats { get; }
    
    /// <summary>
    /// Signals that callers may push source/raw frames and let the endpoint
    /// perform any required conversion at the boundary.
    /// </summary>
    bool PreferRawFramePassthrough => false;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Pushes one video frame into the endpoint.
    /// Implementations may buffer internally.
    /// </summary>
    void WriteFrame(in VideoFrame frame);
}

