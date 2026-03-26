using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

internal sealed class AudioVideoMixerRuntime : IDisposable
{
    private readonly Lock _gate = new();
    private readonly IAudioVideoMixer _mixer;
    private readonly IAudioSource _audioSource;
    private readonly IVideoSource _videoSource;
    private readonly IAudioOutput _audioOutput;
    private readonly IVideoOutput _videoOutput;
    private readonly AudioVideoMixerRuntimeOptions _options;
    private readonly PlaybackClockAnchor _timelineAnchor = new();
    private readonly DriftCorrectionState _driftCorrection;
    private readonly Queue<VideoFrame> _videoQueue = [];

    private CancellationTokenSource? _cts;
    private Task? _audioTask;
    private Task? _videoTask;
    private Task? _presentTask;
    private bool _running;
    private DateTime _nextCorrectionAtUtc = DateTime.MinValue;

    private long _videoPushed;
    private long _videoPushFailures;
    private long _videoNoFrame;
    private long _videoLateDrops;
    private long _videoQueueTrimDrops;
    private long _videoCoalescedDrops;
    private long _audioPushFailures;
    private long _audioReadFailures;
    private long _audioEmptyReads;
    private long _audioPushedFrames;

    private double _lastDriftMs;
    private double _lastCorrectionSignalMs;
    private double _lastCorrectionStepMs;
    private double _leadMinMs = double.PositiveInfinity;
    private double _leadMaxMs = double.NegativeInfinity;
    private double _leadSumMs;
    private long _leadCount;

    public AudioVideoMixerRuntime(
        IAudioVideoMixer mixer,
        IAudioSource audioSource,
        IVideoSource videoSource,
        IAudioOutput audioOutput,
        IVideoOutput videoOutput,
        AudioVideoMixerRuntimeOptions options)
    {
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        _audioSource = audioSource ?? throw new ArgumentNullException(nameof(audioSource));
        _videoSource = videoSource ?? throw new ArgumentNullException(nameof(videoSource));
        _audioOutput = audioOutput ?? throw new ArgumentNullException(nameof(audioOutput));
        _videoOutput = videoOutput ?? throw new ArgumentNullException(nameof(videoOutput));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _driftCorrection = new DriftCorrectionState(
            _options.AutoDriftCorrection,
            _options.DriftDeadbandMs,
            _options.DriftGain,
            _options.DriftMaxStepMs,
            _options.DriftMaxOffsetMs,
            _options.DriftHardResyncMs);
    }

    public int Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return MediaResult.Success;
            }

            var audioStart = _audioSource.Start();
            if (audioStart != MediaResult.Success)
            {
                return audioStart;
            }

            var videoStart = _videoSource.Start();
            if (videoStart != MediaResult.Success)
            {
                _audioSource.Stop();
                return videoStart;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _audioTask = Task.Run(() => PumpAudio(token), token);
            _videoTask = Task.Run(() => PumpVideo(token), token);
            if (!_options.PresentOnCallerThread)
            {
                _presentTask = Task.Run(() => PresentVideo(token), token);
            }

            _nextCorrectionAtUtc = DateTime.UtcNow;
            _running = true;
            return MediaResult.Success;
        }
    }

    public TimeSpan TickVideoPresentation()
    {
        if (!_options.PresentOnCallerThread)
        {
            return TimeSpan.Zero;
        }

        lock (_gate)
        {
            if (!_running)
            {
                return TimeSpan.Zero;
            }
        }

        return PresentVideoStep();
    }

    public int Stop()
    {
        CancellationTokenSource? cts;
        Task? audioTask;
        Task? videoTask;
        Task? presentTask;

        lock (_gate)
        {
            if (!_running)
            {
                return MediaResult.Success;
            }

            cts = _cts;
            audioTask = _audioTask;
            videoTask = _videoTask;
            presentTask = _presentTask;
            _running = false;
            _cts = null;
            _audioTask = null;
            _videoTask = null;
            _presentTask = null;
        }

        cts?.Cancel();
        WaitTask(audioTask);
        WaitTask(videoTask);
        WaitTask(presentTask);
        cts?.Dispose();

        lock (_gate)
        {
            while (_videoQueue.Count > 0)
            {
                _videoQueue.Dequeue().Dispose();
            }
        }

        _audioSource.Stop();
        _videoSource.Stop();
        return MediaResult.Success;
    }

    public AudioVideoMixerDebugInfo GetSnapshot()
    {
        lock (_gate)
        {
            var leadAvg = _leadCount > 0 ? _leadSumMs / _leadCount : 0.0;
            var leadMin = double.IsPositiveInfinity(_leadMinMs) ? 0.0 : _leadMinMs;
            var leadMax = double.IsNegativeInfinity(_leadMaxMs) ? 0.0 : _leadMaxMs;
            return new AudioVideoMixerDebugInfo(
                VideoPushed: _videoPushed,
                VideoPushFailures: _videoPushFailures,
                VideoNoFrame: _videoNoFrame,
                VideoLateDrops: _videoLateDrops,
                VideoQueueTrimDrops: _videoQueueTrimDrops,
                VideoCoalescedDrops: _videoCoalescedDrops,
                VideoQueueDepth: _videoQueue.Count,
                AudioPushFailures: _audioPushFailures,
                AudioReadFailures: _audioReadFailures,
                AudioEmptyReads: _audioEmptyReads,
                AudioPushedFrames: _audioPushedFrames,
                DriftMs: _lastDriftMs,
                CorrectionSignalMs: _lastCorrectionSignalMs,
                CorrectionStepMs: _lastCorrectionStepMs,
                CorrectionOffsetMs: _driftCorrection.CurrentOffsetMs,
                CorrectionResyncCount: _driftCorrection.HardResyncCount,
                LeadMinMs: leadMin,
                LeadAvgMs: leadAvg,
                LeadMaxMs: leadMax);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void PumpAudio(CancellationToken token)
    {
        var readFrames = Math.Max(1, _options.AudioReadFrames);
        var sourceChannels = Math.Max(1, _options.SourceChannelCount);
        var sampleRate = Math.Max(1, _options.OutputSampleRate);
        var buffer = new float[readFrames * sourceChannels];
        double? monotonicTimelineSeconds = null;
        double? lastAppliedTimelineSeconds = null;

        while (!token.IsCancellationRequested)
        {
            var audioCode = _audioSource.ReadSamples(buffer, readFrames, out var framesRead);
            if (audioCode != MediaResult.Success)
            {
                Interlocked.Increment(ref _audioReadFailures);
                Thread.Sleep(1);
                continue;
            }

            if (framesRead <= 0)
            {
                Interlocked.Increment(ref _audioEmptyReads);
                Thread.Sleep(1);
                continue;
            }

            var audioFrame = new AudioFrame(
                Samples: buffer,
                FrameCount: framesRead,
                SourceChannelCount: sourceChannels,
                Layout: AudioFrameLayout.Interleaved,
                SampleRate: sampleRate,
                PresentationTime: TimeSpan.FromSeconds(_audioSource.PositionSeconds));

            var push = _audioOutput.PushFrame(in audioFrame, _options.RouteMap, sourceChannels);
            if (push != MediaResult.Success)
            {
                Interlocked.Increment(ref _audioPushFailures);
                Thread.Sleep(1);
                continue;
            }

            Interlocked.Add(ref _audioPushedFrames, framesRead);
            monotonicTimelineSeconds ??= Math.Max(0, _audioSource.PositionSeconds);
            monotonicTimelineSeconds += framesRead / (double)sampleRate;

            var correctedTimelineSeconds = monotonicTimelineSeconds.Value + _driftCorrection.CurrentOffsetSeconds;
            if (lastAppliedTimelineSeconds.HasValue && correctedTimelineSeconds < lastAppliedTimelineSeconds.Value)
            {
                correctedTimelineSeconds = lastAppliedTimelineSeconds.Value;
            }

            lastAppliedTimelineSeconds = correctedTimelineSeconds;
            _ = _mixer.Seek(correctedTimelineSeconds);
            _timelineAnchor.Update(correctedTimelineSeconds);
        }
    }

    private void PumpVideo(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var code = _videoSource.ReadFrame(out var frame);
            if (code != MediaResult.Success)
            {
                if (code == (int)MediaErrorCode.NDIVideoFallbackUnavailable)
                {
                    Interlocked.Increment(ref _videoNoFrame);
                }
                else
                {
                    Interlocked.Increment(ref _videoPushFailures);
                }

                Thread.Sleep(1);
                continue;
            }

            lock (_gate)
            {
                while (_videoQueue.Count >= Math.Max(1, _options.VideoQueueCapacity))
                {
                    _videoQueue.Dequeue().Dispose();
                    _videoQueueTrimDrops++;
                }

                _videoQueue.Enqueue(frame);
            }
        }
    }

    private void PresentVideo(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = PresentVideoStep();
            var minSleep = _options.PresenterMinSleep <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : _options.PresenterMinSleep;
            SleepForPresenter(delay, minSleep);
        }
    }

    private TimeSpan PresentVideoStep()
    {
        VideoFrame? ready = null;
        var minSleep = _options.PresenterMinSleep <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : _options.PresenterMinSleep;
        var delay = minSleep;

        lock (_gate)
        {
            var decision = VideoPresenterSyncPolicy.SelectNextFrame(
                _videoQueue,
                _mixer.SyncMode,
                _timelineAnchor.Estimate(_mixer.Clock.CurrentSeconds),
                _options.SyncPolicyOptions);

            ready = decision.Frame;
            delay = decision.Delay;
            _videoLateDrops += decision.LateDrops;
            _videoCoalescedDrops += decision.CoalescedDrops;
        }

        if (ready is not null)
        {
            try
            {
                var push = _videoOutput.PushFrame(ready, ready.PresentationTime);
                if (push == MediaResult.Success)
                {
                    Interlocked.Increment(ref _videoPushed);
                    var clockSeconds = _timelineAnchor.Estimate(_mixer.Clock.CurrentSeconds);
                    var leadMs = (ready.PresentationTime.TotalSeconds - clockSeconds) * 1000.0;
                    lock (_gate)
                    {
                        _leadMinMs = Math.Min(_leadMinMs, leadMs);
                        _leadMaxMs = Math.Max(_leadMaxMs, leadMs);
                        _leadSumMs += leadMs;
                        _leadCount++;
                    }

                    delay = TimeSpan.Zero;
                }
                else
                {
                    Interlocked.Increment(ref _videoPushFailures);
                }
            }
            finally
            {
                ready.Dispose();
            }
        }

        if (DateTime.UtcNow >= _nextCorrectionAtUtc)
        {
            ApplyDriftCorrection();
            _nextCorrectionAtUtc = DateTime.UtcNow.AddSeconds(1);
        }

        return delay;
    }

    private void ApplyDriftCorrection()
    {
        var estimatedClock = _timelineAnchor.Estimate(_mixer.Clock.CurrentSeconds);
        var driftMs = (_videoSource.PositionSeconds - estimatedClock) * 1000.0;

        lock (_gate)
        {
            var leadAvgMs = _leadCount > 0 ? _leadSumMs / _leadCount : 0.0;
            var signalMs = _leadCount > 0 ? leadAvgMs : driftMs;
            var stepMs = _driftCorrection.UpdateFromDrift(signalMs);

            _lastDriftMs = driftMs;
            _lastCorrectionSignalMs = signalMs;
            _lastCorrectionStepMs = stepMs;

            _leadMinMs = double.PositiveInfinity;
            _leadMaxMs = double.NegativeInfinity;
            _leadSumMs = 0;
            _leadCount = 0;
        }
    }

    private static void SleepForPresenter(TimeSpan delay, TimeSpan minPresenterSleep)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (delay > minPresenterSleep)
        {
            var sleepMs = Math.Max(1, (int)Math.Floor(delay.TotalMilliseconds) - 1);
            Thread.Sleep(sleepMs);
        }
        else
        {
            Thread.SpinWait(200);
        }
    }

    private static void WaitTask(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort background task shutdown.
        }
    }

    private sealed class PlaybackClockAnchor
    {
        private readonly Lock _gate = new();
        private bool _hasAnchor;
        private double _anchorSeconds;
        private long _anchorTick;

        public void Update(double currentSeconds)
        {
            lock (_gate)
            {
                _anchorSeconds = currentSeconds;
                _anchorTick = Stopwatch.GetTimestamp();
                _hasAnchor = true;
            }
        }

        public double Estimate(double fallbackSeconds)
        {
            lock (_gate)
            {
                if (!_hasAnchor)
                {
                    return fallbackSeconds;
                }

                var elapsedTicks = Stopwatch.GetTimestamp() - _anchorTick;
                var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
                return _anchorSeconds + Math.Max(0, elapsedSeconds);
            }
        }
    }

    private sealed class DriftCorrectionState
    {
        private readonly Lock _gate = new();
        private readonly bool _enabled;
        private readonly double _deadbandMs;
        private readonly double _gain;
        private readonly double _maxStepMs;
        private readonly double _maxOffsetMs;
        private readonly double _hardResyncMs;
        private double _offsetSeconds;
        private long _hardResyncCount;

        public DriftCorrectionState(bool enabled, int deadbandMs, double gain, int maxStepMs, int maxOffsetMs, int hardResyncMs)
        {
            _enabled = enabled;
            _deadbandMs = Math.Max(0, deadbandMs);
            _gain = Math.Clamp(gain, 0.01, 1.0);
            _maxStepMs = Math.Max(0.1, maxStepMs);
            _maxOffsetMs = Math.Max(_maxStepMs, maxOffsetMs);
            _hardResyncMs = Math.Max(_maxStepMs, hardResyncMs);
        }

        public double CurrentOffsetSeconds
        {
            get
            {
                lock (_gate)
                {
                    return _offsetSeconds;
                }
            }
        }

        public double CurrentOffsetMs => CurrentOffsetSeconds * 1000.0;

        public long HardResyncCount
        {
            get
            {
                lock (_gate)
                {
                    return _hardResyncCount;
                }
            }
        }

        public double UpdateFromDrift(double driftMs)
        {
            if (!_enabled || double.IsNaN(driftMs) || double.IsInfinity(driftMs))
            {
                return 0;
            }

            lock (_gate)
            {
                var absDrift = Math.Abs(driftMs);
                if (absDrift <= _deadbandMs)
                {
                    var deadbandOffsetMs = _offsetSeconds * 1000.0;
                    if (Math.Abs(deadbandOffsetMs) <= 0.05)
                    {
                        _offsetSeconds = 0;
                    }
                    else
                    {
                        _offsetSeconds = (deadbandOffsetMs * 0.995) / 1000.0;
                    }

                    return 0;
                }

                double stepMs;
                if (absDrift >= _hardResyncMs)
                {
                    stepMs = Math.Clamp(driftMs * 0.5, -_maxStepMs * 8, _maxStepMs * 8);
                    _hardResyncCount++;
                }
                else
                {
                    stepMs = Math.Clamp(driftMs * _gain, -_maxStepMs, _maxStepMs);
                }

                var offsetMs = (_offsetSeconds * 1000.0) + stepMs;
                offsetMs = Math.Clamp(offsetMs, -_maxOffsetMs, _maxOffsetMs);
                _offsetSeconds = offsetMs / 1000.0;
                return stepMs;
            }
        }
    }
}

