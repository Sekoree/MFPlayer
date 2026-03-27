namespace S.Media.PortAudio.Input;

public sealed record AudioInputConfig
{
    public int SampleRate { get; init; } = 48_000;

    public int ChannelCount { get; init; } = 2;
}
