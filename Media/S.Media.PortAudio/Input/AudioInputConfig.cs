namespace S.Media.PortAudio.Input;

public sealed record AudioInputConfig
{
    public int SampleRate { get; init; } = 48_000;

    public int ChannelCount { get; init; } = 2;

    /// <summary>
    /// Number of frames per buffer passed to <c>Pa_OpenDefaultStream</c>.
    /// Default: 256. Must be in the range [1, 32 768].
    /// </summary>
    public int FramesPerBuffer { get; init; } = 256;
}
