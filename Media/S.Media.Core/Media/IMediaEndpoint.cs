namespace S.Media.Core.Media;

/// <summary>
/// Common lifecycle contract shared by all media endpoints:
/// outputs, sinks, frame endpoints, and buffer endpoints.
/// </summary>
public interface IMediaEndpoint : IDisposable
{
    /// <summary>Human-readable label for diagnostics.</summary>
    string Name { get; }

    /// <summary>Whether this endpoint is currently running.</summary>
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

