using S.Media.Core.Clock;

namespace S.Media.Core.Media;

/// <summary>Base interface for all media outputs (audio, video, or combined).</summary>
public interface IMediaOutput : IMediaEndpoint
{
    /// <summary>The clock driving this output's timeline.</summary>
    IMediaClock Clock { get; }

    string IMediaEndpoint.Name => GetType().Name;
}
