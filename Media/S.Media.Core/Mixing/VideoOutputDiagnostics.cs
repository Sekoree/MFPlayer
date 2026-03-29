namespace S.Media.Core.Mixing;

/// <summary>
/// Per-output runtime diagnostics for mixer-managed video dispatch workers.
/// </summary>
public readonly record struct VideoOutputDiagnostics(
    Guid OutputId,
    int QueueDepth,
    int QueueCapacity,
    long EnqueueDrops,
    long StaleDrops,
    long PushFailures);

