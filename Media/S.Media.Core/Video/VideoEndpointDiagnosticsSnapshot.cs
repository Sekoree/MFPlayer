namespace S.Media.Core.Video;

/// <summary>
/// Endpoint-level video diagnostics counters.
/// </summary>
public readonly record struct VideoEndpointDiagnosticsSnapshot(
    long PassthroughFrames,
    long ConvertedFrames,
    long DroppedFrames,
    long QueueDepth,
    long QueueDrops)
{
    public static VideoEndpointDiagnosticsSnapshot Empty { get; } = new(0, 0, 0, 0, 0);
}

