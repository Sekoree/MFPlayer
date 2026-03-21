using Seko.OwnAudioNET.Video.Clocks;

namespace Seko.OwnAudioNET.Video.NDI;

public interface INDIExternalTimelineClock : IExternalClock
{
    void OnAudioFrame(long timecode100ns, int frameCount, int sampleRate);

    void OnAudioPlaybackFrames(int frameCount, int sampleRate);

    double ResolveVideoPtsSeconds(long timestamp100ns, long timecode100ns, double frameDurationSeconds);
}

public sealed class NDIExternalTimelineClock : INDIExternalTimelineClock
{
    private const long UndefinedNDITimestamp = long.MaxValue;
    private const double HundredNanosecondsPerSecond = 10_000_000d;

    private enum TimebaseKind
    {
        Timestamp,
        Timecode
    }

    private readonly Lock _syncLock = new();
    private readonly NDIExternalTimelineClockOptions _options;

    private bool _timestampAnchorSet;
    private double _timestampAnchorSeconds;
    private bool _timecodeAnchorSet;
    private double _timecodeAnchorSeconds;
    private double _lastAudioCapturedSeconds;
    private double _lastAudioPlaybackSeconds;
    private double _lastVideoSeconds;
    private double _smoothedPipelineLatencySeconds;

    public NDIExternalTimelineClock(NDIExternalTimelineClockOptions? options = null)
    {
        _options = (options ?? new NDIExternalTimelineClockOptions()).CloneNormalized();
    }

    public double CurrentSeconds
    {
        get
        {
            lock (_syncLock)
                return Math.Max(_lastAudioPlaybackSeconds, _lastVideoSeconds);
        }
    }

    public void OnAudioFrame(long timecode100ns, int frameCount, int sampleRate)
    {
        lock (_syncLock)
        {
            var durationSeconds = frameCount > 0 && sampleRate > 0
                ? frameCount / (double)sampleRate
                : 0;

            if (TryToRelativeSeconds(timecode100ns, TimebaseKind.Timecode, out var baseSeconds))
                _lastAudioCapturedSeconds = Math.Max(_lastAudioCapturedSeconds, baseSeconds + durationSeconds);
            else
                _lastAudioCapturedSeconds += durationSeconds;
        }
    }

    public void OnAudioPlaybackFrames(int frameCount, int sampleRate)
    {
        if (frameCount <= 0 || sampleRate <= 0)
            return;

        lock (_syncLock)
        {
            _lastAudioPlaybackSeconds += frameCount / (double)sampleRate;
            if (_lastAudioPlaybackSeconds > _lastAudioCapturedSeconds)
                _lastAudioPlaybackSeconds = _lastAudioCapturedSeconds;
        }
    }

    public double ResolveVideoPtsSeconds(long timestamp100ns, long timecode100ns, double frameDurationSeconds)
    {
        lock (_syncLock)
        {
            if (frameDurationSeconds <= 0 || double.IsNaN(frameDurationSeconds) || double.IsInfinity(frameDurationSeconds))
                frameDurationSeconds = _options.DefaultFrameDurationSeconds;

            var hasTimestamp = TryToRelativeSeconds(timestamp100ns, TimebaseKind.Timestamp, out var timestampSeconds);
            var hasTimecode = TryToRelativeSeconds(timecode100ns, TimebaseKind.Timecode, out var timecodeSeconds);

            double candidate;
            if (hasTimestamp)
                candidate = timestampSeconds;
            else if (hasTimecode)
                candidate = timecodeSeconds;
            else if (_lastVideoSeconds > 0)
                candidate = _lastVideoSeconds + frameDurationSeconds;
            else
                candidate = _lastAudioPlaybackSeconds;

            if (_lastVideoSeconds > 0 && candidate > _lastVideoSeconds + _options.MaxTimestampJumpSeconds)
                candidate = _lastVideoSeconds + frameDurationSeconds;

            var pipelineLatencySeconds = Math.Max(0, _lastAudioCapturedSeconds - _lastAudioPlaybackSeconds);
            pipelineLatencySeconds = Math.Min(pipelineLatencySeconds, _options.MaxLatencyCompensationSeconds);
            var smoothing = _options.PipelineLatencySmoothingFactor;
            _smoothedPipelineLatencySeconds = (_smoothedPipelineLatencySeconds * (1.0 - smoothing)) + (pipelineLatencySeconds * smoothing);
            _smoothedPipelineLatencySeconds = Math.Min(_smoothedPipelineLatencySeconds, _options.MaxLatencyCompensationSeconds);

            // Audio is what the user actually hears; delay video by buffered-audio latency to keep lipsync.
            candidate = Math.Max(0, candidate + _smoothedPipelineLatencySeconds);

            if (_lastVideoSeconds > 0)
                candidate = Math.Max(candidate, _lastVideoSeconds + (frameDurationSeconds * _options.MinVideoAdvanceFrameRatio));

            _lastVideoSeconds = candidate;
            return candidate;
        }
    }

    private bool TryToRelativeSeconds(long value100ns, TimebaseKind kind, out double seconds)
    {
        if (value100ns <= 0 || value100ns == UndefinedNDITimestamp)
        {
            seconds = 0;
            return false;
        }

        var absoluteSeconds = value100ns / HundredNanosecondsPerSecond;
        if (kind == TimebaseKind.Timestamp)
        {
            if (!_timestampAnchorSet)
            {
                _timestampAnchorSet = true;
                _timestampAnchorSeconds = absoluteSeconds;
            }

            seconds = Math.Max(0, absoluteSeconds - _timestampAnchorSeconds);
            return true;
        }

        if (!_timecodeAnchorSet)
        {
            _timecodeAnchorSet = true;
            _timecodeAnchorSeconds = absoluteSeconds;
        }

        seconds = Math.Max(0, absoluteSeconds - _timecodeAnchorSeconds);
        return true;
    }
}

