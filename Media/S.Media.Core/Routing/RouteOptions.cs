using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;

namespace S.Media.Core.Routing;

/// <summary>
/// Marker interface implemented by <see cref="AudioRouteOptions"/> and
/// <see cref="VideoRouteOptions"/>.  Allows higher-level APIs to accept either
/// kind of options object without overloading and lets the router route to the
/// correct <c>CreateRoute</c> specialisation by pattern-matching on the concrete type.
/// </summary>
public interface IRouteOptions
{
}

/// <summary>
/// Options for an audio route (input → endpoint).
/// </summary>
public record AudioRouteOptions : IRouteOptions
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
    /// Route-specific media-time offset applied on top of any input offset.
    /// Positive values delay this route; negative values advance it.
    /// </summary>
    public TimeSpan TimeOffset { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Optional resampler for this route. When <see langword="null"/> and source/endpoint
    /// sample rates differ, the router auto-creates a <see cref="LinearResampler"/>.
    /// </summary>
    public IAudioResampler? Resampler { get; init; }
}

/// <summary>
/// Options for a video route (input → endpoint).
/// </summary>
public record VideoRouteOptions : IRouteOptions
{
    /// <summary>Route-level gain/opacity (future use). Default 1.0.</summary>
    public float Gain { get; init; } = 1.0f;

    /// <summary>
    /// Route-specific media-time offset applied on top of any input offset.
    /// Positive values delay this route; negative values advance it.
    /// </summary>
    public TimeSpan TimeOffset { get; init; } = TimeSpan.Zero;
}

