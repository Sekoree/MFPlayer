using Seko.OwnAudioNET.Video.Clocks;

namespace Seko.OwnAudioNET.Video.NDI;

public interface INdiExternalTimelineClock : IExternalClock
{
    void OnAudioFrame(long timecode100ns, int frameCount, int sampleRate);

    void OnAudioPlaybackFrames(int frameCount, int sampleRate);

    double ResolveVideoPtsSeconds(long timestamp100ns, long timecode100ns, double frameDurationSeconds);
}

public sealed class NdiExternalTimelineClock : INdiExternalTimelineClock
{
    private const long UndefinedNdiTimestamp = long.MaxValue;
    private const double HundredNanosecondsPerSecond = 10_000_000d;

    private readonly Lock _syncLock = new();
    private readonly NdiExternalTimelineClockOptions _options;

    private bool _anchorSet;
    private double _anchorSeconds;
    private double _lastAudioCapturedSeconds;
    private double _lastAudioPlaybackSeconds;
    private double _lastVideoSeconds;
    private double _smoothedPipelineLatencySeconds;

    public NdiExternalTimelineClock(NdiExternalTimelineClockOptions? options = null)
    {
        _options = (options ?? new NdiExternalTimelineClockOptions()).CloneNormalized();
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

            if (TryToRelativeSeconds(timecode100ns, out var baseSeconds))
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

            var hasTimestamp = TryToRelativeSeconds(timestamp100ns, out var timestampSeconds);
            var hasTimecode = TryToRelativeSeconds(timecode100ns, out var timecodeSeconds);

            double candidate;
            if (hasTimestamp)
                candidate = timestampSeconds;
            else if (hasTimecode)
                candidate = timecodeSeconds;
            else if (_lastVideoSeconds > 0)
                candidate = _lastVideoSeconds + frameDurationSeconds;
            else
                candidate = _lastAudioPlaybackSeconds;

            var pipelineLatencySeconds = Math.Max(0, _lastAudioCapturedSeconds - _lastAudioPlaybackSeconds);
            var smoothing = _options.PipelineLatencySmoothingFactor;
            _smoothedPipelineLatencySeconds = (_smoothedPipelineLatencySeconds * (1.0 - smoothing)) + (pipelineLatencySeconds * smoothing);

            // Audio is what the user actually hears; delay video by buffered-audio latency to keep lipsync.
            candidate = Math.Max(0, candidate + _smoothedPipelineLatencySeconds);

            if (_lastVideoSeconds > 0)
                candidate = Math.Max(candidate, _lastVideoSeconds + (frameDurationSeconds * _options.MinVideoAdvanceFrameRatio));

            _lastVideoSeconds = candidate;
            return candidate;
        }
    }

    private bool TryToRelativeSeconds(long value100ns, out double seconds)
    {
        if (value100ns <= 0 || value100ns == UndefinedNdiTimestamp)
        {
            seconds = 0;
            return false;
        }

        var absoluteSeconds = value100ns / HundredNanosecondsPerSecond;
        if (!_anchorSet)
        {
            _anchorSet = true;
            _anchorSeconds = absoluteSeconds;
        }

        seconds = Math.Max(0, absoluteSeconds - _anchorSeconds);
        return true;
    }
}

