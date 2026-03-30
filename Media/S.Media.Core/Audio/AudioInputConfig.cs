namespace S.Media.Core.Audio;

/// <summary>
/// Configuration for a live audio capture stream.
/// Passed to <see cref="IAudioInput.Start(AudioInputConfig)"/>.
/// </summary>
public sealed record AudioInputConfig
{
    /// <summary>Capture sample rate in Hz. Default: 48 000.</summary>
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Number of capture channels. Default: 2.</summary>
    public int ChannelCount { get; init; } = 2;

    /// <summary>
    /// Number of frames per buffer passed to the native stream open call.
    /// Default: 256. Must be in the range [1, 32 768].
    /// </summary>
    public int FramesPerBuffer { get; init; } = 256;
}

