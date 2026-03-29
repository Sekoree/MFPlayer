namespace S.Media.Core.Audio;

/// <summary>
/// Configuration for audio output devices.
/// </summary>
public sealed record AudioOutputConfig
{
    /// <summary>
    /// Resampler algorithm used when source sample rate differs from the output stream rate.
    /// Default: <see cref="AudioResamplerMode.Sinc"/>.
    /// </summary>
    public AudioResamplerMode ResamplerMode { get; init; } = AudioResamplerMode.Sinc;

    /// <summary>
    /// Behaviour when the source channel count exceeds the output channel count and the
    /// caller's route map does not fully cover the difference.
    /// Default: <see cref="ChannelMismatchPolicy.Drop"/>.
    /// </summary>
    public ChannelMismatchPolicy ChannelMismatchPolicy { get; init; } = ChannelMismatchPolicy.Drop;
}
