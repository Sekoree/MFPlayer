namespace Seko.OwnAudioNET.Video.Clocks;

/// <summary>
/// Abstraction over a timeline clock that can drive one or more <see cref="Sources.IVideoSource"/> instances.
/// Supports both internally-owned playback clocks and externally-supplied clocks for future A/V sync scenarios.
/// </summary>
public interface IVideoClock
{
    /// <summary>Current playback timestamp in seconds.</summary>
    double CurrentTimestamp { get; }

    /// <summary>Current playback position expressed as sample frames at <see cref="SampleRate"/>.</summary>
    long CurrentSamplePosition { get; }

    /// <summary>Clock sample rate used for timestamp/sample conversions.</summary>
    int SampleRate { get; }

    /// <summary>Logical channel count associated with the clock.</summary>
    int Channels { get; }

    /// <summary>Moves the clock to an absolute timestamp.</summary>
    void SeekTo(double timestamp);

    /// <summary>Resets the clock to zero.</summary>
    void Reset();

    /// <summary>Converts a timestamp in seconds to a sample position.</summary>
    long TimestampToSamplePosition(double timestamp);

    /// <summary>Converts a sample position to a timestamp in seconds.</summary>
    double SamplePositionToTimestamp(long samplePosition);
}

