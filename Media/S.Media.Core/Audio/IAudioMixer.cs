using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Combines multiple <see cref="IAudioChannel"/> sources into a single output buffer and
/// distributes per-sink mixes to all registered <see cref="IAudioSink"/> instances.
/// Called directly from the PortAudio RT callback — all hot-path operations must be allocation-free.
/// </summary>
public interface IAudioMixer : IDisposable
{
    /// <summary>
    /// The audio format of the leader output (sample rate + channel count).
    /// Used to determine resampler creation and mix-buffer sizing.
    /// </summary>
    AudioFormat LeaderFormat { get; }

    /// <summary>Master volume multiplier applied to all mixed buffers. Range: 0.0 – 1.0+.</summary>
    float MasterVolume { get; set; }

    /// <summary>Number of channels currently registered.</summary>
    int ChannelCount { get; }

    /// <summary>
    /// Peak absolute sample level per output channel of the leader mix, updated each tick.
    /// Length equals <see cref="IAudioOutput.HardwareFormat"/>.<c>.Channels</c>.
    /// </summary>
    IReadOnlyList<float> PeakLevels { get; }

    /// <summary>
    /// Default routing policy applied to channels that have no explicit
    /// <see cref="RouteTo"/> call for a given sink.
    /// </summary>
    ChannelFallback DefaultFallback { get; }

    // ── Channel management ────────────────────────────────────────────────

    /// <summary>
    /// Registers an audio channel with the mixer.
    /// </summary>
    /// <param name="channel">Source channel (may have any <see cref="IAudioChannel.SourceFormat"/>).</param>
    /// <param name="routeMap">
    /// Mapping from source channels to the <b>leader</b> output channels, with per-route gain.
    /// Use <see cref="ChannelRouteMap.Identity"/> for a simple 1:1 pass-through.
    /// </param>
    /// <param name="resampler">
    /// Optional source→leader resampler. When <see langword="null"/> and rates differ, a
    /// <see cref="LinearResampler"/> is created automatically.
    /// </param>
    void AddChannel(
        IAudioChannel    channel,
        ChannelRouteMap  routeMap,
        IAudioResampler? resampler = null);

    /// <summary>Removes a previously registered channel by its <see cref="IAudioChannel.Id"/>.</summary>
    void RemoveChannel(Guid channelId);

    // ── Per-channel time offset ────────────────────────────────────────────

    /// <summary>
    /// Sets a time offset for a registered audio channel.
    /// Positive values delay the channel (silence is inserted at the start);
    /// negative values advance it (initial samples are discarded).
    /// The offset is applied once at the start of playback (or after seek/reset).
    /// </summary>
    /// <param name="channelId">The channel's <see cref="IAudioChannel.Id"/>.</param>
    /// <param name="offset">Time offset to apply. <see cref="TimeSpan.Zero"/> removes any offset.</param>
    void SetChannelTimeOffset(Guid channelId, TimeSpan offset);

    /// <summary>
    /// Gets the current time offset for a registered audio channel.
    /// Returns <see cref="TimeSpan.Zero"/> if no offset has been set.
    /// </summary>
    TimeSpan GetChannelTimeOffset(Guid channelId);

    // ── Per-sink routing (dynamic, thread-safe) ────────────────────────────

    /// <summary>
    /// Routes a registered channel to a registered sink with an explicit channel map.
    /// Replaces any existing route for that <c>(channel, sink)</c> pair.
    /// Thread-safe; takes effect on the next <see cref="FillOutputBuffer"/> call.
    /// </summary>
    /// <param name="channelId"><see cref="IAudioChannel.Id"/> of the source channel.</param>
    /// <param name="sink">Target sink (must already be registered via <see cref="RegisterSink"/>).</param>
    /// <param name="routeMap">Channel map from source channels into the sink's mix buffer.</param>
    void RouteTo(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap);

    /// <summary>
    /// Removes the explicit route from a channel to a sink.
    /// After this call the <see cref="DefaultFallback"/> policy applies.
    /// Thread-safe; no-op if the pair was not routed.
    /// </summary>
    void UnrouteTo(Guid channelId, IAudioSink sink);

    // ── Sink registration (called by AggregateOutput) ─────────────────────

    /// <summary>
    /// Registers a sink as a mix target.
    /// The mixer will maintain a separate mix buffer for this sink and call
    /// <see cref="IAudioSink.ReceiveBuffer"/> from inside <see cref="FillOutputBuffer"/>.
    /// </summary>
    /// <param name="sink">The sink to register.</param>
    /// <param name="channels">
    /// Number of output channels in the sink's mix buffer.
    /// Pass 0 to use the leader's channel count.
    /// </param>
    void RegisterSink(IAudioSink sink, int channels = 0);

    /// <summary>
    /// Removes a sink from the mix targets. All per-channel routes to this sink
    /// are automatically cleaned up. Thread-safe; no-op if not registered.
    /// </summary>
    void UnregisterSink(IAudioSink sink);

    // ── RT fill ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fills <paramref name="dest"/> with the leader mixed audio, and distributes
    /// per-sink mixes to all registered sinks.
    /// Called from the RT callback — MUST NOT allocate, lock, or block.
    /// </summary>
    void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat);
}

