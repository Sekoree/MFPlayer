using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// A secondary video destination that receives copies of presented frames.
/// Analogous to <see cref="Audio.IAudioSink"/> for the video pipeline.
/// Not implemented in v1 — interface shaped for future NDI send / recording.
/// </summary>
public interface IVideoSink : IDisposable
{
    /// <summary>Human-readable label for diagnostics.</summary>
    string Name { get; }

    /// <summary>Whether this sink is currently accepting data.</summary>
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Receives a presented frame. Implementations must be non-blocking
    /// (copy the data and return immediately).
    /// </summary>
    void ReceiveFrame(in VideoFrame frame);

    /// <summary>
    /// Optional endpoint diagnostics snapshot. Implementations may override.
    /// </summary>
    VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot() => VideoEndpointDiagnosticsSnapshot.Empty;
}

