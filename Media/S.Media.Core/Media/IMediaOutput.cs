using S.Media.Core.Clock;

namespace S.Media.Core.Media;

/// <summary>Base interface for all media outputs (audio, video, or combined).</summary>
public interface IMediaOutput : IDisposable
{
    /// <summary>The clock driving this output's timeline.</summary>
    IMediaClock Clock { get; }

    /// <summary>Whether the output is currently streaming.</summary>
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

