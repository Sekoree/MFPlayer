namespace S.Media.Core;

/// <summary>Provides the current playback position and optional tick notifications for a media pipeline.</summary>
public interface IMediaClock
{
    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Sample rate associated with this clock (e.g. the hardware output rate).</summary>
    double SampleRate { get; }

    /// <summary>Whether the clock is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Raised approximately once per output buffer (≈ framesPerBuffer / sampleRate seconds).
    /// Useful for UI position updates and A/V synchronisation. NOT raised on the RT audio thread.
    /// </summary>
    event Action<TimeSpan>? Tick;

    void Start();
    void Stop();
    void Reset();
}