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

    /// <summary>
    /// Mono source → both channels (0 and 1) of a stereo output.
    /// </summary>
    public static ChannelRouteMap MonoToStereo() =>
        new Builder().Route(0, 0).Route(0, 1).Build();

    /// <summary>
    /// Automatic route map for the most common playback wiring:
    /// pass-through up to <c>min(srcChannels, dstChannels)</c>,
    /// with mono-to-stereo expansion when <paramref name="srcChannels"/> == 1 and
    /// <paramref name="dstChannels"/> >= 2.
    /// </summary>
    public static ChannelRouteMap Auto(int srcChannels, int dstChannels)
    {
        if (srcChannels == 1 && dstChannels >= 2)
            return MonoToStereo();
        int common = Math.Min(srcChannels, dstChannels);
        var b = new Builder();
        for (int i = 0; i < common; i++) b.Route(i, i);
        return b.Build();
    }

    /// <summary>
    /// Automatic route map that additionally downmixes multi-channel content
    /// (3+ source channels) into the first <paramref name="dstChannels"/> output
    /// channels when <paramref name="dstChannels"/> is ≤ 2.
    ///
    /// <list type="bullet">
    /// <item><paramref name="srcChannels"/> == 1, <paramref name="dstChannels"/> ≥ 2 → mono→stereo fan-out.</item>
    /// <item><paramref name="srcChannels"/> == 2, <paramref name="dstChannels"/> == 1 → L+R → mono (0.5× each).</item>
    /// <item><paramref name="srcChannels"/> ≥ 3, <paramref name="dstChannels"/> == 2 → ITU-R BS.775 5.1→stereo downmix
    ///   (front + 0.707·center + 0.707·surround).</item>
    /// <item>Otherwise falls back to <see cref="Auto(int,int)"/> passthrough.</item>
    /// </list>
    ///
    /// <para>Closes review finding §4.2.</para>
    /// </summary>
    public static ChannelRouteMap AutoStereoDownmix(int srcChannels, int dstChannels)
    {
        if (srcChannels <= 0 || dstChannels <= 0) return Silence();

        // Mono source → fan out.
        if (srcChannels == 1) return dstChannels >= 2 ? MonoToStereo() : Identity(1);

        // Stereo source, mono destination → average L+R.
        if (srcChannels == 2 && dstChannels == 1)
            return new Builder().Route(0, 0, 0.5f).Route(1, 0, 0.5f).Build();

        // 5.1-ish source to stereo → ITU-R BS.775 downmix.
        // FFmpeg/WAVE canonical order: FL FR FC LFE BL BR (+ side channels).
        if (srcChannels >= 3 && dstChannels == 2)
        {
            const float c   = 0.7071067811865476f; // -3 dB
            const float lfe = 0.0f;                // drop LFE in stereo downmix
            var b = new Builder();
            b.Route(0, 0);           // FL → L
            b.Route(1, 1);           // FR → R
            if (srcChannels >= 3)
            {
                b.Route(2, 0, c);    // FC → L (-3 dB)
                b.Route(2, 1, c);    // FC → R (-3 dB)
            }
            if (srcChannels >= 4 && lfe > 0f)
            {
                b.Route(3, 0, lfe);  // LFE → L
                b.Route(3, 1, lfe);  // LFE → R
            }
            if (srcChannels >= 5) { b.Route(4, 0, c); }  // BL/SL → L
            if (srcChannels >= 6) { b.Route(5, 1, c); }  // BR/SR → R
            // Additional channels (7.1+): pan to nearest side.
            for (int i = 6; i < srcChannels; i++)
                b.Route(i, (i % 2 == 0) ? 0 : 1, c);
            return b.Build();
        }

        // Everything else: passthrough semantics.
        return Auto(srcChannels, dstChannels);
    }
}

