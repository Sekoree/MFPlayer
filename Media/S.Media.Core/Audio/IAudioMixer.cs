using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Combines multiple <see cref="IAudioChannel"/> sources into a single output buffer.
/// Called directly from the PortAudio RT callback — all hot-path operations must be allocation-free.
/// </summary>
public interface IAudioMixer : IDisposable
{
    /// <summary>The output this mixer feeds into.</summary>
    IAudioOutput Output { get; }

    /// <summary>Master volume multiplier applied to the final mixed buffer. Range: 0.0 – 1.0+.</summary>
    float MasterVolume { get; set; }

    /// <summary>Number of channels currently registered.</summary>
    int ChannelCount { get; }

    /// <summary>
    /// Peak absolute sample level per output channel, updated each mix tick.
    /// Length equals <see cref="IAudioOutput.HardwareFormat"/>.<c>.Channels</c>.
    /// </summary>
    IReadOnlyList<float> PeakLevels { get; }

    /// <summary>
    /// Registers an audio channel with the mixer.
    /// </summary>
    /// <param name="channel">Source channel (may have any <see cref="IAudioChannel.SourceFormat"/>).</param>
    /// <param name="routeMap">
    /// Mapping from source channels to output channels, with per-route gain.
    /// </param>
    /// <param name="resampler">
    /// Optional resampler. When <see langword="null"/> and the source sample rate differs
    /// from the output sample rate, a <see cref="LinearResampler"/> is created automatically.
    /// Pass a <c>SwrResampler</c> for higher-quality conversion.
    /// </param>
    void AddChannel(
        IAudioChannel    channel,
        ChannelRouteMap  routeMap,
        IAudioResampler? resampler = null);

    /// <summary>Removes a previously registered channel by its <see cref="IAudioChannel.Id"/>.</summary>
    void RemoveChannel(Guid channelId);

    /// <summary>
    /// Fills <paramref name="dest"/> with mixed audio.
    /// Called from the PortAudio RT callback — MUST NOT allocate, lock, or block.
    /// Working buffers must be pre-allocated before the first call.
    /// </summary>
    void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat);
}

