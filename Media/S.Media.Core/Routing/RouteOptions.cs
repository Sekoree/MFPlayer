using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;

namespace S.Media.Core.Routing;

/// <summary>
/// Options for an audio route (input → endpoint).
/// </summary>
public record AudioRouteOptions
{
    /// <summary>
    /// Source→destination channel mapping with per-route gain.
    /// <see langword="null"/> = auto-derive (mono→stereo expansion or 1:1 based on
    /// source channel count and endpoint channel count).
    /// </summary>
    public ChannelRouteMap? ChannelMap { get; init; }

    /// <summary>Route-level gain multiplier. Default 1.0.</summary>
    public float Gain { get; init; } = 1.0f;

    /// <summary>
    /// Optional resampler for this route. When <see langword="null"/> and source/endpoint
    /// sample rates differ, the router auto-creates a <see cref="LinearResampler"/>.
    /// </summary>
    public IAudioResampler? Resampler { get; init; }
}

/// <summary>
/// Options for a video route (input → endpoint).
/// </summary>
public record VideoRouteOptions
{
    /// <summary>Route-level gain/opacity (future use). Default 1.0.</summary>
    public float Gain { get; init; } = 1.0f;
}

