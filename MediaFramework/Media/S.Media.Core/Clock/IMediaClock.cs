namespace S.Media.Core.Clock;

/// <summary>Provides the current playback position and optional tick notifications for a media pipeline.</summary>
public interface IMediaClock
{
    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>
    /// Approximate interval between <see cref="Tick"/> events. Informational.
    /// Replaces the former <c>SampleRate</c> property — clocks are now media-agnostic.
    /// </summary>
    TimeSpan TickCadence { get; }

    /// <summary>Whether the clock is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Raised approximately once per tick cadence.
    /// Useful for UI position updates and A/V synchronisation. NOT raised on the RT audio thread.
    /// </summary>
    event Action<TimeSpan>? Tick;

    void Start();
    void Stop();
    void Reset();
}