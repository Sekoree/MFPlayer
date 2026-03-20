using System.Diagnostics;
using Seko.OwnAudioNET.Video.Clocks;

namespace Seko.OwnAudioNET.Video.NDI;

internal sealed class NdiTimelineClock
{
    private const double HundredNanosecondsPerSecond = 10_000_000d;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly IExternalClock? _externalClock;

    private readonly Lock _syncLock = new();
    private int _audioSampleRate;
    private long _audioFramesSent;
    private bool _audioTimelineActive;

    public NdiTimelineClock(IExternalClock? externalClock)
    {
        _externalClock = externalClock;
    }

    public void ResetAudioTimeline(int sampleRate)
    {
        lock (_syncLock)
        {
            _audioSampleRate = sampleRate;
            _audioFramesSent = 0;
            _audioTimelineActive = false;
        }
    }

    public long GetAudioTimecode100nsAndAdvance(int frameCount, int sampleRate)
    {
        lock (_syncLock)
        {
            if (_audioSampleRate != sampleRate)
            {
                _audioSampleRate = sampleRate;
                _audioFramesSent = 0;
                _audioTimelineActive = false;
            }

            var timecode = FramesToTimecode100ns(_audioFramesSent, _audioSampleRate);
            _audioFramesSent += Math.Max(0, frameCount);
            _audioTimelineActive = true;
            return timecode;
        }
    }

    public long ResolveVideoTimecode100ns(double incomingMasterTimestamp, bool useIncomingVideoTimestamp)
    {
        double seconds;
        if (useIncomingVideoTimestamp)
        {
            seconds = Math.Max(0, incomingMasterTimestamp);
        }
        else
        {
            lock (_syncLock)
            {
                seconds = _audioTimelineActive && _audioSampleRate > 0
                    ? _audioFramesSent / (double)_audioSampleRate
                    : GetRealtimeSeconds();
            }
        }

        return SecondsToTimecode100ns(seconds);
    }

    public double CurrentSeconds
    {
        get
        {
            lock (_syncLock)
            {
                if (_audioTimelineActive && _audioSampleRate > 0)
                    return _audioFramesSent / (double)_audioSampleRate;

                return GetRealtimeSeconds();
            }
        }
    }

    private double GetRealtimeSeconds()
    {
        var externalSeconds = _externalClock?.CurrentSeconds;
        return externalSeconds is > 0 ? externalSeconds.Value : _stopwatch.Elapsed.TotalSeconds;
    }

    private static long FramesToTimecode100ns(long frameCount, int sampleRate)
    {
        if (sampleRate <= 0)
            return 0;

        var seconds = frameCount / (double)sampleRate;
        return SecondsToTimecode100ns(seconds);
    }

    private static long SecondsToTimecode100ns(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return 0;

        return (long)Math.Round(seconds * HundredNanosecondsPerSecond);
    }
}

