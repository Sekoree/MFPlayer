using System.Collections.ObjectModel;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public class AudioVideoMixer : IAudioVideoMixer, ISupportsAdvancedRouting, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<IAudioSource> _audioSources = [];
    private readonly List<IVideoSource> _videoSources = [];
    private readonly List<IAudioOutput> _audioOutputs = [];
    private readonly List<IVideoOutput> _videoOutputs = [];
    private readonly List<AudioRoutingRule> _audioRoutingRules = [];
    private readonly List<VideoRoutingRule> _videoRoutingRules = [];
    private readonly Dictionary<Guid, double> _audioStartOffsets = new();
    private readonly IMediaClock _clock;
    private MixerSourceDetachOptions _audioDetachOptions = new();
    private MixerSourceDetachOptions _videoDetachOptions = new();
    private IVideoSource? _activeVideoSource;

    // Playback runtime state
    private AudioVideoMixerConfig? _config;
    private CancellationTokenSource? _cts;
    private Task? _audioTask;
    private Task? _videoTask;
    private Task? _presentTask;
    private bool _playbackRunning;
    private readonly Queue<VideoFrame> _videoQueue = [];
    private readonly SimpleDriftCorrection _driftCorrection = new();
    private DateTime _nextCorrectionAtUtc = DateTime.MinValue;

    // Debug counters
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

    public AudioVideoMixer(IMediaClock? clock = null, ClockType clockType = ClockType.Hybrid)
    {
        _clock = clock ?? new CoreMediaClock();

        if (MixerClockTypeRules.ValidateClockType(clockType) != MediaResult.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(clockType));
        }

        if (clockType == ClockType.External && _clock is CoreMediaClock)
        {
            throw new ArgumentException("ClockType.External requires a non-CoreMediaClock implementation.", nameof(clockType));
        }

        ClockType = clockType;
        SyncMode = AudioVideoSyncMode.Synced;
        State = AudioVideoMixerState.Stopped;
    }

    public AudioVideoMixerState State { get; private set; }

    public IMediaClock Clock => _clock;

    public ClockType ClockType { get; private set; }

    public AudioVideoSyncMode SyncMode { get; private set; }

    public double PositionSeconds => _clock.CurrentSeconds;

    public bool IsRunning => State == AudioVideoMixerState.Running;

    public IReadOnlyList<IAudioSource> AudioSources
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<IAudioSource>([.. _audioSources]);
            }
        }
    }

    public IReadOnlyList<IVideoSource> VideoSources
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<IVideoSource>([.. _videoSources]);
            }
        }
    }

    public IReadOnlyList<IAudioOutput> AudioOutputs
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<IAudioOutput>([.. _audioOutputs]);
            }
        }
    }

    public IReadOnlyList<IVideoOutput> VideoOutputs
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<IVideoOutput>([.. _videoOutputs]);
            }
        }
    }

    public MixerSourceDetachOptions AudioSourceDetachOptions
    {
        get
        {
            lock (_gate)
            {
                return _audioDetachOptions;
            }
        }
    }

    public MixerSourceDetachOptions VideoSourceDetachOptions
    {
        get
        {
            lock (_gate)
            {
                return _videoDetachOptions;
            }
        }
    }

    public event EventHandler<AudioVideoMixerStateChangedEventArgs>? StateChanged;

    public event EventHandler<AudioSourceErrorEventArgs>? AudioSourceError;

    public event EventHandler<VideoSourceErrorEventArgs>? VideoSourceError;

    public event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged;

    public int Start()
    {
        lock (_gate)
        {
            if (State == AudioVideoMixerState.Running)
            {
                return MediaResult.Success;
            }

            var result = _clock.Start();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = AudioVideoMixerState.Running;
            StateChanged?.Invoke(this, new AudioVideoMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Pause()
    {
        lock (_gate)
        {
            if (State != AudioVideoMixerState.Running)
            {
                return MediaResult.Success;
            }

            var result = _clock.Pause();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = AudioVideoMixerState.Paused;
            StateChanged?.Invoke(this, new AudioVideoMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Resume()
    {
        lock (_gate)
        {
            if (State != AudioVideoMixerState.Paused)
            {
                return MediaResult.Success;
            }

            var result = _clock.Start();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = AudioVideoMixerState.Running;
            StateChanged?.Invoke(this, new AudioVideoMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            var result = _clock.Stop();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = AudioVideoMixerState.Stopped;
            if (previous != State)
            {
                StateChanged?.Invoke(this, new AudioVideoMixerStateChangedEventArgs(previous, State));
            }

            return MediaResult.Success;
        }
    }

    public int Seek(double positionSeconds)
    {
        return _clock.Seek(positionSeconds);
    }

    public int AddAudioSource(IAudioSource source)
    {
        return AddAudioSource(source, 0.0);
    }

    public int AddAudioSource(IAudioSource source, double startOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_audioSources.Any(s => s.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MixerSourceIdCollision;
            }

            _audioSources.Add(source);
            _audioStartOffsets[source.SourceId] = startOffsetSeconds;
            return MediaResult.Success;
        }
    }

    public int SetAudioSourceStartOffset(IAudioSource source, double startOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_audioSources.Any(s => s.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            _audioStartOffsets[source.SourceId] = startOffsetSeconds;
            return MediaResult.Success;
        }
    }

    public int RemoveAudioSource(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            _audioSources.RemoveAll(s => s.SourceId == source.SourceId);
            _audioStartOffsets.Remove(source.SourceId);
            return MediaResult.Success;
        }
    }

    public int AddVideoSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_videoSources.Any(s => s.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MixerSourceIdCollision;
            }

            _videoSources.Add(source);
            return MediaResult.Success;
        }
    }

    public int RemoveVideoSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            _videoSources.RemoveAll(s => s.SourceId == source.SourceId);

            if (_activeVideoSource?.SourceId == source.SourceId)
            {
                var previous = _activeVideoSource.SourceId;
                _activeVideoSource = null;
                ActiveVideoSourceChanged?.Invoke(this, new VideoActiveSourceChangedEventArgs(previous, null));
            }

            return MediaResult.Success;
        }
    }

    public int AddAudioOutput(IAudioOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        lock (_gate)
        {
            if (_audioOutputs.Contains(output))
            {
                return MediaResult.Success;
            }

            _audioOutputs.Add(output);
            return MediaResult.Success;
        }
    }

    public int RemoveAudioOutput(IAudioOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        lock (_gate)
        {
            _audioOutputs.Remove(output);
            return MediaResult.Success;
        }
    }

    public int AddVideoOutput(IVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        lock (_gate)
        {
            if (_videoOutputs.Contains(output))
            {
                return MediaResult.Success;
            }

            _videoOutputs.Add(output);
            return MediaResult.Success;
        }
    }

    public int RemoveVideoOutput(IVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        lock (_gate)
        {
            _videoOutputs.Remove(output);
            return MediaResult.Success;
        }
    }

    public int ConfigureAudioSourceDetachOptions(MixerSourceDetachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _audioDetachOptions = options;
            return MediaResult.Success;
        }
    }

    public int ConfigureVideoSourceDetachOptions(MixerSourceDetachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _videoDetachOptions = options;
            return MediaResult.Success;
        }
    }

    public int SetClockType(ClockType clockType)
    {
        var validation = MixerClockTypeRules.ValidateClockType(clockType);
        if (validation != MediaResult.Success)
        {
            return validation;
        }

        if (clockType == ClockType.External && _clock is CoreMediaClock)
        {
            return (int)MediaErrorCode.MediaExternalClockUnavailable;
        }

        lock (_gate)
        {
            ClockType = clockType;
            return MediaResult.Success;
        }
    }

    public int SetSyncMode(AudioVideoSyncMode syncMode)
    {
        lock (_gate)
        {
            SyncMode = syncMode;
            return MediaResult.Success;
        }
    }

    public int SetActiveVideoSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_videoSources.Any(v => v.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            var previous = _activeVideoSource?.SourceId;
            _activeVideoSource = source;
            ActiveVideoSourceChanged?.Invoke(this, new VideoActiveSourceChangedEventArgs(previous, source.SourceId));
            return MediaResult.Success;
        }
    }

    // ─── Advanced routing (ISupportsAdvancedRouting) ──────────────────

    public IReadOnlyList<AudioRoutingRule> AudioRoutingRules
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<AudioRoutingRule>([.. _audioRoutingRules]);
            }
        }
    }

    public IReadOnlyList<VideoRoutingRule> VideoRoutingRules
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<VideoRoutingRule>([.. _videoRoutingRules]);
            }
        }
    }

    public int AddAudioRoutingRule(AudioRoutingRule rule)
    {
        lock (_gate)
        {
            _audioRoutingRules.Add(rule);
            return MediaResult.Success;
        }
    }

    public int RemoveAudioRoutingRule(AudioRoutingRule rule)
    {
        lock (_gate)
        {
            _audioRoutingRules.Remove(rule);
            return MediaResult.Success;
        }
    }

    public int ClearAudioRoutingRules()
    {
        lock (_gate)
        {
            _audioRoutingRules.Clear();
            return MediaResult.Success;
        }
    }

    public int AddVideoRoutingRule(VideoRoutingRule rule)
    {
        lock (_gate)
        {
            _videoRoutingRules.Add(rule);
            return MediaResult.Success;
        }
    }

    public int RemoveVideoRoutingRule(VideoRoutingRule rule)
    {
        lock (_gate)
        {
            _videoRoutingRules.Remove(rule);
            return MediaResult.Success;
        }
    }

    public int ClearVideoRoutingRules()
    {
        lock (_gate)
        {
            _videoRoutingRules.Clear();
            return MediaResult.Success;
        }
    }

    // ─── Playback runtime ───────────────────────────────────────────────

    /// <summary>
    /// Starts the managed A/V playback pump threads.
    /// Audio: reads from all started audio sources, mixes additively, pushes to all audio outputs.
    /// Video: reads from the active video source, pushes to all video outputs via sync policy.
    /// </summary>
    public int StartPlayback(AudioVideoMixerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            if (_playbackRunning)
            {
                return MediaResult.Success;
            }

            _config = config;
        }

        // Start all audio sources
        List<IAudioSource> audioSources;
        List<IVideoSource> videoSources;
        IVideoSource? activeVideo;

        lock (_gate)
        {
            audioSources = [.. _audioSources];
            videoSources = [.. _videoSources];
            activeVideo = _activeVideoSource;
        }

        foreach (var src in audioSources)
        {
            var r = src.Start();
            if (r != MediaResult.Success)
            {
                RaiseAudioSourceError(src.SourceId, r, "Failed to start audio source");
            }
        }

        if (activeVideo is not null)
        {
            var r = activeVideo.Start();
            if (r != MediaResult.Success)
            {
                RaiseVideoSourceError(activeVideo.SourceId, r, "Failed to start video source");
            }
        }

        var startResult = Start();
        if (startResult != MediaResult.Success)
        {
            return startResult;
        }

        lock (_gate)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            ResetDebugCounters();
            _driftCorrection.Reset();
            _nextCorrectionAtUtc = DateTime.UtcNow;

            _audioTask = Task.Run(() => PumpAudio(token), token);
            _videoTask = Task.Run(() => PumpVideo(token), token);
            if (!config.PresentOnCallerThread)
            {
                _presentTask = Task.Run(() => PresentVideoLoop(token), token);
            }

            _playbackRunning = true;
        }

        return MediaResult.Success;
    }

    /// <summary>
    /// Stops the managed A/V playback pump threads and the mixer clock.
    /// </summary>
    public int StopPlayback()
    {
        CancellationTokenSource? cts;
        Task? audioTask;
        Task? videoTask;
        Task? presentTask;

        lock (_gate)
        {
            if (!_playbackRunning)
            {
                return MediaResult.Success;
            }

            cts = _cts;
            audioTask = _audioTask;
            videoTask = _videoTask;
            presentTask = _presentTask;
            _playbackRunning = false;
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

        // Stop all sources
        List<IAudioSource> audioSources;
        IVideoSource? activeVideo;
        lock (_gate)
        {
            audioSources = [.. _audioSources];
            activeVideo = _activeVideoSource;
        }

        foreach (var src in audioSources)
        {
            src.Stop();
        }

        activeVideo?.Stop();

        return Stop();
    }

    /// <summary>
    /// Ticks the video presentation step when <see cref="AudioVideoMixerConfig.PresentOnCallerThread"/> is true.
    /// Returns the suggested delay before the next tick.
    /// </summary>
    public TimeSpan TickVideoPresentation()
    {
        lock (_gate)
        {
            if (!_playbackRunning)
            {
                return TimeSpan.Zero;
            }
        }

        return PresentVideoStep();
    }

    /// <summary>
    /// Returns a diagnostic snapshot of the playback state, or null if playback is not active.
    /// </summary>
    public AudioVideoMixerDebugInfo? GetDebugInfo()
    {
        lock (_gate)
        {
            if (!_playbackRunning)
            {
                return null;
            }

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
        StopPlayback();
    }

    // ─── Internal helpers ───────────────────────────────────────────────

    internal void RaiseAudioSourceError(Guid sourceId, int errorCode, string? message)
    {
        AudioSourceError?.Invoke(this, new AudioSourceErrorEventArgs(sourceId, errorCode, message));
    }

    internal void RaiseVideoSourceError(Guid sourceId, int errorCode, string? message)
    {
        VideoSourceError?.Invoke(this, new VideoSourceErrorEventArgs(sourceId, errorCode, message));
    }

    // ─── Audio pump (many-to-many) ─────────────────────────────────────

    private void PumpAudio(CancellationToken token)
    {
        var readFrames = Math.Max(1, _config!.AudioReadFrames);
        var sourceChannels = Math.Max(1, _config.SourceChannelCount);
        var sampleRate = Math.Max(1, _config.OutputSampleRate);
        var defaultRouteMap = _config.RouteMap;
        var mixBuffer = new float[readFrames * sourceChannels];

        while (!token.IsCancellationRequested)
        {
            // Snapshot sources, outputs, and routing rules
            List<IAudioSource> sources;
            List<IAudioOutput> outputs;
            List<AudioRoutingRule> rules;
            lock (_gate)
            {
                sources = [.. _audioSources];
                outputs = [.. _audioOutputs];
                rules = [.. _audioRoutingRules];
            }

            // Read samples from all sources (keyed by SourceId for rule lookup)
            var sourceData = new Dictionary<Guid, (float[] Samples, int FramesRead)>();
            var anyFramesRead = false;
            var maxFramesRead = 0;

            Array.Clear(mixBuffer);

            foreach (var source in sources)
            {
                var buf = new float[readFrames * sourceChannels];
                var audioCode = source.ReadSamples(buf, readFrames, out var framesRead);
                if (audioCode != MediaResult.Success)
                {
                    Interlocked.Increment(ref _audioReadFailures);
                    continue;
                }

                if (framesRead <= 0)
                {
                    Interlocked.Increment(ref _audioEmptyReads);
                    continue;
                }

                anyFramesRead = true;
                if (framesRead > maxFramesRead)
                {
                    maxFramesRead = framesRead;
                }

                sourceData[source.SourceId] = (buf, framesRead);

                // Also additive-mix into the global buffer for non-routed fallback
                var sampleCount = framesRead * sourceChannels;
                for (var i = 0; i < sampleCount; i++)
                {
                    mixBuffer[i] += buf[i];
                }
            }

            if (!anyFramesRead)
            {
                Interlocked.Increment(ref _audioEmptyReads);
                Thread.Sleep(1);
                continue;
            }

            var pts = TimeSpan.FromSeconds(_clock.CurrentSeconds);

            if (rules.Count > 0)
            {
                // ── Advanced routing: build a per-output mix buffer ──
                foreach (var output in outputs)
                {
                    var outputRules = rules.FindAll(r => r.OutputId == output.Id);
                    if (outputRules.Count == 0)
                    {
                        continue; // no rules target this output → silence
                    }

                    // Determine max output channel index to size the output buffer
                    var maxOutCh = 0;
                    foreach (var r in outputRules)
                    {
                        if (r.OutputChannel > maxOutCh) maxOutCh = r.OutputChannel;
                    }

                    var outChannels = maxOutCh + 1;
                    var outBuffer = new float[maxFramesRead * outChannels];

                    foreach (var rule in outputRules)
                    {
                        if (!sourceData.TryGetValue(rule.SourceId, out var sd))
                        {
                            continue; // source not available this cycle
                        }

                        var srcCh = rule.SourceChannel;
                        var dstCh = rule.OutputChannel;
                        var gain = rule.Gain;

                        if (srcCh < 0 || srcCh >= sourceChannels) continue;
                        if (dstCh < 0 || dstCh >= outChannels) continue;

                        var frames = sd.FramesRead;
                        for (var f = 0; f < frames; f++)
                        {
                            outBuffer[f * outChannels + dstCh] +=
                                sd.Samples[f * sourceChannels + srcCh] * gain;
                        }
                    }

                    // Clamp
                    var totalSamples = maxFramesRead * outChannels;
                    for (var i = 0; i < totalSamples; i++)
                    {
                        outBuffer[i] = Math.Clamp(outBuffer[i], -1.0f, 1.0f);
                    }

                    // Build a trivial 1:1 route map for the output
                    var outRouteMap = new int[outChannels];
                    for (var ch = 0; ch < outChannels; ch++) outRouteMap[ch] = ch;

                    var frame = new AudioFrame(
                        Samples: outBuffer,
                        FrameCount: maxFramesRead,
                        SourceChannelCount: outChannels,
                        Layout: AudioFrameLayout.Interleaved,
                        SampleRate: sampleRate,
                        PresentationTime: pts);

                    var push = output.PushFrame(in frame, outRouteMap, outChannels);
                    if (push != MediaResult.Success)
                    {
                        Interlocked.Increment(ref _audioPushFailures);
                    }
                }
            }
            else
            {
                // ── Default: global mix → all outputs with config RouteMap ──
                var totalSamples = maxFramesRead * sourceChannels;
                for (var i = 0; i < totalSamples; i++)
                {
                    mixBuffer[i] = Math.Clamp(mixBuffer[i], -1.0f, 1.0f);
                }

                var audioFrame = new AudioFrame(
                    Samples: mixBuffer,
                    FrameCount: maxFramesRead,
                    SourceChannelCount: sourceChannels,
                    Layout: AudioFrameLayout.Interleaved,
                    SampleRate: sampleRate,
                    PresentationTime: pts);

                foreach (var output in outputs)
                {
                    var push = output.PushFrame(in audioFrame, defaultRouteMap, sourceChannels);
                    if (push != MediaResult.Success)
                    {
                        Interlocked.Increment(ref _audioPushFailures);
                    }
                }
            }

            Interlocked.Add(ref _audioPushedFrames, maxFramesRead);

            // Update clock from audio position (use the first source as reference)
            if (sources.Count > 0)
            {
                var refSource = sources[0];
                var corrected = refSource.PositionSeconds + _driftCorrection.CurrentOffsetSeconds;
                _ = _clock.Seek(Math.Max(0, corrected));
            }
        }
    }

    // ─── Video read thread ─────────────────────────────────────────────

    private void PumpVideo(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IVideoSource? activeVideo;
            lock (_gate)
            {
                activeVideo = _activeVideoSource;
            }

            if (activeVideo is null)
            {
                Thread.Sleep(1);
                continue;
            }

            var code = activeVideo.ReadFrame(out var frame);
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
                var capacity = Math.Max(1, _config?.VideoQueueCapacity ?? 3);
                while (_videoQueue.Count >= capacity)
                {
                    _videoQueue.Dequeue().Dispose();
                    _videoQueueTrimDrops++;
                }

                _videoQueue.Enqueue(frame);
            }
        }
    }

    // ─── Video presentation ────────────────────────────────────────────

    private void PresentVideoLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = PresentVideoStep();
            SleepForPresenter(delay);
        }
    }

    private TimeSpan PresentVideoStep()
    {
        VideoFrame? ready = null;
        var minSleep = TimeSpan.FromMilliseconds(1);
        var delay = minSleep;

        lock (_gate)
        {
            var decision = VideoPresenterSyncPolicy.SelectNextFrame(
                _videoQueue,
                SyncMode,
                _clock.CurrentSeconds,
                VideoPresenterSyncPolicyOptions.Default);

            ready = decision.Frame;
            delay = decision.Delay;
            _videoLateDrops += decision.LateDrops;
            _videoCoalescedDrops += decision.CoalescedDrops;
        }

        if (ready is not null)
        {
            try
            {
                // Push to video outputs (with routing rule support)
                List<IVideoOutput> outputs;
                List<VideoRoutingRule> videoRules;
                Guid? activeSourceId;
                lock (_gate)
                {
                    outputs = [.. _videoOutputs];
                    videoRules = [.. _videoRoutingRules];
                    activeSourceId = _activeVideoSource?.SourceId;
                }

                if (videoRules.Count > 0 && activeSourceId is not null)
                {
                    // Advanced routing: only push to outputs that have a matching rule
                    var targetOutputIds = new HashSet<Guid>();
                    foreach (var rule in videoRules)
                    {
                        if (rule.SourceId == activeSourceId.Value)
                        {
                            targetOutputIds.Add(rule.OutputId);
                        }
                    }

                    foreach (var output in outputs)
                    {
                        if (!targetOutputIds.Contains(output.Id)) continue;

                        var push = output.PushFrame(ready, ready.PresentationTime);
                        if (push == MediaResult.Success)
                        {
                            Interlocked.Increment(ref _videoPushed);
                        }
                        else
                        {
                            Interlocked.Increment(ref _videoPushFailures);
                        }
                    }
                }
                else
                {
                    // Default: push to all outputs
                    foreach (var output in outputs)
                    {
                        var push = output.PushFrame(ready, ready.PresentationTime);
                        if (push == MediaResult.Success)
                        {
                            Interlocked.Increment(ref _videoPushed);
                        }
                        else
                        {
                            Interlocked.Increment(ref _videoPushFailures);
                        }
                    }
                }

                // Track lead statistics
                var clockSeconds = _clock.CurrentSeconds;
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
        IVideoSource? activeVideo;
        lock (_gate)
        {
            activeVideo = _activeVideoSource;
        }

        if (activeVideo is null)
        {
            return;
        }

        var clockSeconds = _clock.CurrentSeconds;
        var driftMs = (activeVideo.PositionSeconds - clockSeconds) * 1000.0;

        lock (_gate)
        {
            var leadAvgMs = _leadCount > 0 ? _leadSumMs / _leadCount : 0.0;
            var signalMs = _leadCount > 0 ? leadAvgMs : driftMs;
            var stepMs = _driftCorrection.Update(signalMs);

            _lastDriftMs = driftMs;
            _lastCorrectionSignalMs = signalMs;
            _lastCorrectionStepMs = stepMs;

            _leadMinMs = double.PositiveInfinity;
            _leadMaxMs = double.NegativeInfinity;
            _leadSumMs = 0;
            _leadCount = 0;
        }
    }

    private void ResetDebugCounters()
    {
        _videoPushed = 0;
        _videoPushFailures = 0;
        _videoNoFrame = 0;
        _videoLateDrops = 0;
        _videoQueueTrimDrops = 0;
        _videoCoalescedDrops = 0;
        _audioPushFailures = 0;
        _audioReadFailures = 0;
        _audioEmptyReads = 0;
        _audioPushedFrames = 0;
        _lastDriftMs = 0;
        _lastCorrectionSignalMs = 0;
        _lastCorrectionStepMs = 0;
        _leadMinMs = double.PositiveInfinity;
        _leadMaxMs = double.NegativeInfinity;
        _leadSumMs = 0;
        _leadCount = 0;
    }

    private static void SleepForPresenter(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (delay.TotalMilliseconds > 1)
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

    // ─── Simplified drift correction ───────────────────────────────────

    private sealed class SimpleDriftCorrection
    {
        private readonly Lock _gate = new();

        // Internal-only tuning parameters — consumers don't touch these
        private const double DeadbandMs = 10;
        private const double Gain = 0.15;
        private const double MaxStepMs = 5;
        private const double MaxOffsetMs = 200;
        private const double HardResyncMs = 300;

        private double _offsetSeconds;
        private long _hardResyncCount;

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

        public void Reset()
        {
            lock (_gate)
            {
                _offsetSeconds = 0;
                _hardResyncCount = 0;
            }
        }

        public double Update(double driftMs)
        {
            if (double.IsNaN(driftMs) || double.IsInfinity(driftMs))
            {
                return 0;
            }

            lock (_gate)
            {
                var absDrift = Math.Abs(driftMs);

                if (absDrift <= DeadbandMs)
                {
                    // Decay offset towards zero
                    var offsetMs = _offsetSeconds * 1000.0;
                    if (Math.Abs(offsetMs) <= 0.05)
                    {
                        _offsetSeconds = 0;
                    }
                    else
                    {
                        _offsetSeconds = (offsetMs * 0.995) / 1000.0;
                    }

                    return 0;
                }

                double stepMs;
                if (absDrift >= HardResyncMs)
                {
                    // Hard resync: large step
                    stepMs = Math.Clamp(driftMs * 0.5, -MaxStepMs * 8, MaxStepMs * 8);
                    _hardResyncCount++;
                }
                else
                {
                    // Proportional correction
                    stepMs = Math.Clamp(driftMs * Gain, -MaxStepMs, MaxStepMs);
                }

                var newOffsetMs = (_offsetSeconds * 1000.0) + stepMs;
                newOffsetMs = Math.Clamp(newOffsetMs, -MaxOffsetMs, MaxOffsetMs);
                _offsetSeconds = newOffsetMs / 1000.0;
                return stepMs;
            }
        }
    }
}
