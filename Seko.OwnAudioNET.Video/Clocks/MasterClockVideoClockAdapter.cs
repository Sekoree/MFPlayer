using OwnaudioNET.Synchronization;

namespace Seko.OwnAudioNET.Video.Clocks;

/// <summary>
/// Adapts OwnAudio's <see cref="MasterClock"/> to the video-clock abstraction used by video sources and engines.
/// </summary>
public sealed class MasterClockVideoClockAdapter : IVideoClock
{
    private readonly MasterClock _masterClock;

    public MasterClockVideoClockAdapter(MasterClock masterClock)
    {
        _masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
    }

    public MasterClock MasterClock => _masterClock;

    public double CurrentTimestamp => _masterClock.CurrentTimestamp;

    public long CurrentSamplePosition => _masterClock.CurrentSamplePosition;

    public int SampleRate => _masterClock.SampleRate;

    public int Channels => _masterClock.Channels;

    public void SeekTo(double timestamp)
    {
        _masterClock.SeekTo(timestamp);
    }

    public void Reset()
    {
        _masterClock.Reset();
    }

    public long TimestampToSamplePosition(double timestamp)
    {
        return _masterClock.TimestampToSamplePosition(timestamp);
    }

    public double SamplePositionToTimestamp(long samplePosition)
    {
        return _masterClock.SamplePositionToTimestamp(samplePosition);
    }
}

