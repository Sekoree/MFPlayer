namespace S.Media.Core.Audio;

public sealed record AudioEngineConfig
{
    public int SampleRate { get; init; } = 48_000;

    public int OutputChannelCount { get; init; } = 2;

    public int FramesPerBuffer { get; init; } = 256;

    public AudioDeviceId? PreferredOutputDevice { get; init; }

    public bool FailOnDeviceLoss { get; init; }
}

