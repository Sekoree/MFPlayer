namespace S.Media.Core.Audio.Routing;

/// <summary>
/// Immutable map describing how source channels scatter into output channels.
/// Build via <see cref="Builder"/> or the static factory helpers.
/// </summary>
public sealed class ChannelRouteMap
{
    /// <summary>All route entries in declaration order.</summary>
    public IReadOnlyList<ChannelRoute> Routes { get; }

    private ChannelRouteMap(IReadOnlyList<ChannelRoute> routes) => Routes = routes;

    // ── Pre-baked lookup for hot path ──────────────────────────────────────
    // Indexed by source channel; each entry is the list of (dstCh, gain) targets.
    // Built by AudioMixer.AddChannel() to avoid any allocation in FillOutputBuffer.

    /// <summary>
    /// Builds the hot-path lookup table: <c>bakedRoutes[srcCh]</c> → array of (dstCh, gain).
    /// Call once per AddChannel, not on every buffer.
    /// </summary>
    public (int dstCh, float gain)[][] BakeRoutes(int srcChannels)
    {
        var table = new List<(int, float)>[srcChannels];
        for (int i = 0; i < srcChannels; i++)
            table[i] = [];

        foreach (var r in Routes)
        {
            if (r.SrcChannel < srcChannels)
                table[r.SrcChannel].Add((r.DstChannel, r.Gain));
        }

        var result = new (int, float)[srcChannels][];
        for (int i = 0; i < srcChannels; i++)
            result[i] = [.. table[i]];
        return result;
    }

    // ── Fluent builder ─────────────────────────────────────────────────────

    public sealed class Builder
    {
        private readonly List<ChannelRoute> _routes = [];

        /// <param name="gain">Volume multiplier for this route. Default 1.0.</param>
        public Builder Route(int src, int dst, float gain = 1.0f)
        {
            _routes.Add(new ChannelRoute(src, dst, gain));
            return this;
        }

        public ChannelRouteMap Build() => new([.. _routes]);
    }

    // ── Static factories ───────────────────────────────────────────────────

    /// <summary>1:1 identity mapping for a <paramref name="channelCount"/>-channel source and output.</summary>
    public static ChannelRouteMap Identity(int channelCount)
    {
        var b = new Builder();
        for (int i = 0; i < channelCount; i++) b.Route(i, i);
        return b.Build();
    }

    /// <summary>
    /// Stereo fan-out: L → <paramref name="dstL1"/> and <paramref name="dstL2"/>,
    ///                 R → <paramref name="dstR1"/> and <paramref name="dstR2"/>.
    /// E.g. stereo source to channels 0+2 (L) and 1+3 (R) of a 4-ch output.
    /// </summary>
    public static ChannelRouteMap StereoFanTo(int dstL1, int dstL2, int dstR1, int dstR2) =>
        new Builder()
            .Route(0, dstL1)
            .Route(0, dstL2)
            .Route(1, dstR1)
            .Route(1, dstR2)
            .Build();

    /// <summary>
    /// Stereo expand: L → <paramref name="baseChannel"/> and <paramref name="baseChannel"/>+1,
    ///                R → <paramref name="baseChannel"/>+2 and <paramref name="baseChannel"/>+3.
    /// E.g. <c>StereoExpandTo(0)</c> maps L→0+1, R→2+3 on a 4-ch output.
    /// </summary>
    public static ChannelRouteMap StereoExpandTo(int baseChannel) =>
        new Builder()
            .Route(0, baseChannel)
            .Route(0, baseChannel + 1)
            .Route(1, baseChannel + 2)
            .Route(1, baseChannel + 3)
            .Build();

    /// <summary>Downmix any number of source channels to a single mono output channel.</summary>
    public static ChannelRouteMap DownmixToMono(int srcChannels, int dstChannel = 0,
                                                float gainPerChannel = 1.0f)
    {
        var b = new Builder();
        for (int i = 0; i < srcChannels; i++) b.Route(i, dstChannel, gainPerChannel);
        return b.Build();
    }

    /// <summary>
    /// A route map with no entries — all source channels produce silence in the destination.
    /// Use when you want a channel to appear in the mixer (for bookkeeping) but not contribute
    /// to a specific mix buffer. Typically passed as the leader route when routing a channel
    /// exclusively to sinks via <see cref="IAudioMixer.RouteTo"/>.
    /// </summary>
    public static ChannelRouteMap Silence() => new([]);
}

