namespace S.Media.Core.Audio.Routing;

/// <summary>
/// A single routing entry: one source channel maps to one destination channel with a gain factor.
/// Multiple entries with the same <see cref="SrcChannel"/> produce fan-out.
/// Multiple entries with the same <see cref="DstChannel"/> produce fan-in / downmix.
/// </summary>
public readonly record struct ChannelRoute(
    int   SrcChannel,
    int   DstChannel,
    float Gain = 1.0f);

