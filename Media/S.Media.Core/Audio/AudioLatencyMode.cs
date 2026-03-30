namespace S.Media.Core.Audio;

/// <summary>Suggested PortAudio output stream latency mode.</summary>
public enum AudioLatencyMode
{
    /// <summary>
    /// Use the device's default high-output latency. Stable for most playback use cases.
    /// This is the default.
    /// </summary>
    High = 0,

    /// <summary>
    /// Use the device's default low-output latency. Preferred for real-time monitoring
    /// and live performance where minimum round-trip latency is required.
    /// </summary>
    Low = 1,
}

