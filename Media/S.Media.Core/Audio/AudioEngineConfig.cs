namespace S.Media.Core.Audio;

public sealed record AudioEngineConfig
{
    private readonly string? _preferredHostApi;

    public int SampleRate { get; init; } = 48_000;

    public int OutputChannelCount { get; init; } = 2;

    public int FramesPerBuffer { get; init; } = 256;

    public AudioDeviceId? PreferredOutputDevice { get; init; }

    /// <summary>
    /// Preferred PortAudio host API identifier. Empty or whitespace strings are normalized to <c>null</c>.
    /// </summary>
    public string? PreferredHostApi
    {
        get => _preferredHostApi;
        init => _preferredHostApi = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public bool FailOnDeviceLoss { get; init; }
}
