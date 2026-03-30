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

    /// <summary>
    /// Suggested PortAudio output stream latency mode.
    /// Default: <see cref="AudioLatencyMode.High"/> (stable for most playback use cases).
    /// Use <see cref="AudioLatencyMode.Low"/> for real-time monitoring or live performance.
    /// Use <see cref="AudioLatencyMode.Custom"/> together with <see cref="CustomLatencySeconds"/>
    /// for pro-audio drivers that require a specific buffer latency.
    /// </summary>
    public AudioLatencyMode LatencyMode { get; init; } = AudioLatencyMode.High;

    /// <summary>
    /// Suggested output stream latency in seconds, used only when
    /// <see cref="LatencyMode"/> is <see cref="AudioLatencyMode.Custom"/>.
    /// Ignored for <c>High</c> and <c>Low</c> modes.
    /// Typical values: 0.005–0.100 s (5–100 ms).
    /// </summary>
    public double CustomLatencySeconds { get; init; } = 0.02;

    /// <summary>
    /// Maximum time in milliseconds to wait per <c>Pa_WriteStream</c> call before abandoning the
    /// write and returning <see cref="S.Media.Core.Errors.MediaErrorCode.PortAudioPushFailed"/>.
    /// Default: 2 000 ms. Set to 0 or a negative value to wait indefinitely (not recommended
    /// for production — a stalled device will block the audio pump permanently).
    /// </summary>
    public int WriteTimeoutMs { get; init; } = 2_000;
}
