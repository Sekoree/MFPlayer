using System.Diagnostics;
using OwnaudioNET.Synchronization;

namespace Seko.OwnAudioNET.Video.Clocks;

/// <summary>
/// Adapts OwnAudio's <see cref="MasterClock"/> to the video-clock abstraction used by video sources and engines.
/// </summary>
public sealed class MasterClockVideoClockAdapter : IVideoClock
{
    private const double DefaultPredictionWindowSeconds = 1.0 / 30.0;
    private const double MinimumPredictionWindowSeconds = 1.0 / 240.0;
    private const double MaximumPredictionWindowSeconds = 0.100;

    private readonly MasterClock _masterClock;
    private readonly Lock _syncLock = new();
    private long _lastObservedSamplePosition = long.MinValue;
    private double _lastObservedTimestamp = double.NaN;
    private long _lastObservationStopwatchTicks;
    private double _predictionWindowSeconds = DefaultPredictionWindowSeconds;

    public MasterClockVideoClockAdapter(MasterClock masterClock)
    {
        _masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
    }

    public MasterClock MasterClock => _masterClock;

    public double CurrentTimestamp
    {
        get
        {
            var observedSamplePosition = _masterClock.CurrentSamplePosition;
            var observedTimestamp = _masterClock.CurrentTimestamp;
            var nowTicks = Stopwatch.GetTimestamp();

            lock (_syncLock)
            {
                RefreshObservationLocked(observedSamplePosition, observedTimestamp, nowTicks);

                var elapsedSeconds = Math.Max(0, (nowTicks - _lastObservationStopwatchTicks) / (double)Stopwatch.Frequency);
                var predictedTimestamp = _lastObservedTimestamp + Math.Min(elapsedSeconds, _predictionWindowSeconds);
                return Math.Max(observedTimestamp, predictedTimestamp);
            }
        }
    }

    public long CurrentSamplePosition => TimestampToSamplePosition(CurrentTimestamp);

    public int SampleRate => _masterClock.SampleRate;

    public int Channels => _masterClock.Channels;

    public void SeekTo(double timestamp)
    {
        _masterClock.SeekTo(timestamp);

        var nowTicks = Stopwatch.GetTimestamp();
        lock (_syncLock)
        {
            _lastObservedSamplePosition = _masterClock.CurrentSamplePosition;
            _lastObservedTimestamp = _masterClock.CurrentTimestamp;
            _lastObservationStopwatchTicks = nowTicks;
        }
    }

    public void Reset()
    {
        _masterClock.Reset();

        var nowTicks = Stopwatch.GetTimestamp();
        lock (_syncLock)
        {
            _lastObservedSamplePosition = 0;
            _lastObservedTimestamp = 0;
            _lastObservationStopwatchTicks = nowTicks;
        }
    }

    public long TimestampToSamplePosition(double timestamp)
    {
        return _masterClock.TimestampToSamplePosition(timestamp);
    }

    public double SamplePositionToTimestamp(long samplePosition)
    {
        return _masterClock.SamplePositionToTimestamp(samplePosition);
    }

    private void RefreshObservationLocked(long observedSamplePosition, double observedTimestamp, long nowTicks)
    {
        if (observedSamplePosition == long.MinValue)
            observedSamplePosition = 0;

        if (_lastObservedSamplePosition == long.MinValue || double.IsNaN(_lastObservedTimestamp))
        {
            _lastObservedSamplePosition = observedSamplePosition;
            _lastObservedTimestamp = observedTimestamp;
            _lastObservationStopwatchTicks = nowTicks;
            return;
        }

        var samplePositionChanged = observedSamplePosition != _lastObservedSamplePosition;
        var timestampChanged = Math.Abs(observedTimestamp - _lastObservedTimestamp) > 1e-9;
        if (!samplePositionChanged && !timestampChanged)
            return;

        if (observedTimestamp >= _lastObservedTimestamp)
        {
            var observedAdvanceSeconds = observedTimestamp - _lastObservedTimestamp;
            if (observedAdvanceSeconds > 0)
            {
                _predictionWindowSeconds = Math.Clamp(
                    observedAdvanceSeconds,
                    MinimumPredictionWindowSeconds,
                    MaximumPredictionWindowSeconds);
            }
        }

        _lastObservedSamplePosition = observedSamplePosition;
        _lastObservedTimestamp = observedTimestamp;
        _lastObservationStopwatchTicks = nowTicks;
    }
}

