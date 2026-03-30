using System.Runtime.CompilerServices;
using System.Threading.Channels;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

/// <summary>
/// Full-featured audio/video mixer.  Runs three internal threads when
/// <see cref="StartPlayback"/> is called: an audio pump, a video decode pump,
/// and a video presentation dispatcher.
/// </summary>
public class AVMixer : IAVMixer
{
    // ── inner per-output worker (ManagedBackground) ───────────────────────────

    private sealed class OutputWorker : IDisposable
    {
        private readonly IVideoOutput _output;
        private readonly int _capacity;
        private readonly TimeSpan _staleThreshold;   // N6: drop frames older than this
        private readonly Func<double>? _getClockSec; // N6: audio-led clock reference
        private readonly Queue<(VideoFrame Frame, TimeSpan Pts)> _queue = new();
        private readonly Lock _qLock = new();
        private readonly Thread _thread;
        private volatile bool _stop;

        internal long EnqueueDrops;
        internal long StaleDrops;
        internal long PushFailures;

        internal OutputWorker(IVideoOutput output, int capacity,
            TimeSpan staleThreshold, Func<double>? getClockSec)
        {
            _output = output;
            _capacity = Math.Max(1, capacity);
            _staleThreshold = staleThreshold;
            _getClockSec    = getClockSec;
            _thread = new Thread(WorkerLoop)
            { Name = $"AVMixer.Worker-{output.Id}", IsBackground = true };
            _thread.Start();
        }

        internal int QueueDepth { get { lock (_qLock) return _queue.Count; } }

        internal void Enqueue(VideoFrame frame, TimeSpan pts)
        {
            VideoFrame? drop = null;
            lock (_qLock)
            {
                if (_queue.Count >= _capacity)
                {
                    if (_queue.TryDequeue(out var old)) drop = old.Frame;
                    Interlocked.Increment(ref EnqueueDrops);
                }
                _queue.Enqueue((frame.AddRef(), pts));
            }
            drop?.Dispose();
        }

        private void WorkerLoop()
        {
            while (!_stop)
            {
                (VideoFrame Frame, TimeSpan Pts) item;
                bool has;
                lock (_qLock) { has = _queue.TryDequeue(out item); }
                if (!has) { Thread.Sleep(1); continue; }

                // N6: drop frames that are older than the stale threshold relative to the audio clock.
                if (_getClockSec != null && _staleThreshold > TimeSpan.Zero && item.Pts > TimeSpan.Zero)
                {
                    var lag = TimeSpan.FromSeconds(_getClockSec()) - item.Pts;
                    if (lag > _staleThreshold)
                    {
                        item.Frame.Dispose();
                        Interlocked.Increment(ref StaleDrops);
                        continue;
                    }
                }

                using (item.Frame)
                {
                    var code = _output.PushFrame(item.Frame, item.Pts);
                    if (code != MediaResult.Success) Interlocked.Increment(ref PushFailures);
                }
            }
            lock (_qLock)
                while (_queue.TryDequeue(out var item)) item.Frame.Dispose();
        }

        public void Stop()
        {
            _stop = true;
            if (!ReferenceEquals(Thread.CurrentThread, _thread))
                _thread.Join(TimeSpan.FromSeconds(2));
        }

        public void Dispose() => Stop();
    }

    // ── state ─────────────────────────────────────────────────────────────────

    private readonly Lock _gate = new();

    /// <summary>
    /// The mixer's internal synchronisation lock.
    /// Exposed as <see langword="protected"/> so subclasses (e.g. <see cref="S.Media.Core.Playback.MediaPlayer"/>)
    /// can share the same lock and avoid nested-lock deadlocks.
    /// </summary>
    protected Lock Gate => _gate;
    private readonly IMediaClock _clock;
    private readonly List<(IAudioSource Source, double StartOffset)> _audioSources = [];
    private readonly List<IVideoSource> _videoSources = [];
    private readonly List<IAudioSink> _audioOutputs = [];
    private readonly List<IVideoOutput> _videoOutputs = [];
    private readonly List<AudioRoutingRule> _audioRoutingRules = [];
    private readonly List<VideoRoutingRule> _videoRoutingRules = [];
    private readonly Queue<VideoFrame> _videoDecodeQueue = new();
    private readonly Lock _videoQueueLock = new();
    private AVMixerState _state = AVMixerState.Stopped;
    private AVSyncMode _syncMode = AVSyncMode.AudioLed;
    private ClockType _clockType = ClockType.Hybrid;
    private Guid? _activeVideoSourceId;
    private AVMixerConfig? _playbackConfig;
    private bool _disposed;

    // ── seek command channel (§3.9) ───────────────────────────────────────────
    // AudioPumpLoop drains this at the top of every iteration — no thread restart needed.
    private readonly Channel<double> _seekChannel = Channel.CreateBounded<double>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    // ── threads ───────────────────────────────────────────────────────────────

    private CancellationTokenSource? _cancelSource;
    private ManualResetEventSlim _pauseEvent = new(initialState: true);
    private Thread? _audioPumpThread;
    private Thread? _videoDecodeThread;
    private Thread? _videoPresentThread;
    private Dictionary<Guid, OutputWorker> _videoWorkers = [];

    // Audio-led clock position written by audio pump, read by video presenter.
    // Stored as bit-reinterpreted long; Interlocked provides the memory-ordering guarantee.
    private long _audioTimelineBits; // BitConverter.DoubleToInt64Bits

    private double _audioTimelineSeconds
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _audioTimelineBits));
        set => Interlocked.Exchange(ref _audioTimelineBits, BitConverter.DoubleToInt64Bits(value));
    }

    // ── diagnostics ───────────────────────────────────────────────────────────

    private long _videoPushed, _videoPushFailures, _videoNoFrame, _videoLateDrops;
    private long _videoQueueTrimDrops, _videoCoalescedDrops;
    private long _audioPushFailures, _audioReadFailures, _audioEmptyReads, _audioPushedFrames;
    private volatile int _videoQueueDepthVal;

    // ── master volume (bit-reinterpreted for lock-free float access) ──────────
    private int _masterVolumeBits = BitConverter.SingleToInt32Bits(1.0f);

    // ── cached sources snapshot flag (§10.10) — set on source list changes ───
    private volatile bool _audioSourcesNeedsUpdate = true;

    // ── cached video-output / routing-rule snapshots (N7) ────────────────────
    // Avoids per-frame lock acquisition and list allocation in the hot PushFrameToOutputs path.
    private volatile bool _videoOutputsNeedsUpdate      = true;
    private volatile bool _videoRoutingRulesNeedsUpdate = true;
    private IVideoOutput[]      _videoOutputsCache      = [];
    private VideoRoutingRule[]  _videoRoutingRulesCache = [];

    // ── construction ──────────────────────────────────────────────────────────

    public AVMixer() : this(clock: null, ClockType.Hybrid) { }

    public AVMixer(IMediaClock? clock, ClockType clockType = ClockType.Hybrid)
    {
        _clock = clock ?? new CoreMediaClock();
        _clockType = clockType;
    }

    // ── IAVMixer: properties ──────────────────────────────────────────

    public AVMixerState State { get { lock (_gate) return _state; } }
    public IMediaClock Clock => _clock;
    public ClockType ClockType { get { lock (_gate) return _clockType; } }
    public AVSyncMode SyncMode { get { lock (_gate) return _syncMode; } }
    public double PositionSeconds => _clock.CurrentSeconds;
    public bool IsRunning { get { lock (_gate) return _state == AVMixerState.Running; } }

    public IReadOnlyList<IAudioSource> AudioSources
    { get { lock (_gate) return _audioSources.ConvertAll(x => x.Source); } }

    public IReadOnlyList<IVideoSource> VideoSources
    { get { lock (_gate) return [.. _videoSources]; } }

    public IReadOnlyList<IAudioSink> AudioOutputs
    { get { lock (_gate) return [.. _audioOutputs]; } }

    public IReadOnlyList<IVideoOutput> VideoOutputs
    { get { lock (_gate) return [.. _videoOutputs]; } }


    // ── IMixerRouting ──────────────────────────────────────────────────────────

    public IReadOnlyList<AudioRoutingRule> AudioRoutingRules
    { get { lock (_gate) return [.. _audioRoutingRules]; } }

    public IReadOnlyList<VideoRoutingRule> VideoRoutingRules
    { get { lock (_gate) return [.. _videoRoutingRules]; } }

    public int AddAudioRoutingRule(AudioRoutingRule rule)    { lock (_gate) _audioRoutingRules.Add(rule);    return MediaResult.Success; }
    public int RemoveAudioRoutingRule(AudioRoutingRule rule) { lock (_gate) _audioRoutingRules.Remove(rule); return MediaResult.Success; }
    public int ClearAudioRoutingRules()                     { lock (_gate) _audioRoutingRules.Clear();       return MediaResult.Success; }
    public int AddVideoRoutingRule(VideoRoutingRule rule)
    {
        lock (_gate) _videoRoutingRules.Add(rule);
        _videoRoutingRulesNeedsUpdate = true;
        return MediaResult.Success;
    }
    public int RemoveVideoRoutingRule(VideoRoutingRule rule)
    {
        lock (_gate) _videoRoutingRules.Remove(rule);
        _videoRoutingRulesNeedsUpdate = true;
        return MediaResult.Success;
    }
    public int ClearVideoRoutingRules()
    {
        lock (_gate) _videoRoutingRules.Clear();
        _videoRoutingRulesNeedsUpdate = true;
        return MediaResult.Success;
    }

    public float MasterVolume
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _masterVolumeBits));
        set => Volatile.Write(ref _masterVolumeBits, BitConverter.SingleToInt32Bits(Math.Clamp(value, 0f, 1f)));
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────
    // Start / Stop / Pause / Resume are protected: external callers must use
    // StartPlayback / StopPlayback / PausePlayback / ResumePlayback.
    // Subclasses (e.g. MediaPlayer) may call them directly.

    protected int Start()
    {
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
            if (_state == AVMixerState.Running) return MediaResult.Success;
            _state = AVMixerState.Running;
        }
        _clock.Start();
        RaiseStateChanged(AVMixerState.Stopped, AVMixerState.Running);
        return MediaResult.Success;
    }

    protected int Pause()
    {
        AVMixerState prev;
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
            if (_state != AVMixerState.Running) return MediaResult.Success;
            prev = _state;
            _state = AVMixerState.Paused;
        }
        _pauseEvent.Reset();
        _clock.Pause();
        RaiseStateChanged(prev, AVMixerState.Paused);
        return MediaResult.Success;
    }

    protected int Resume()
    {
        AVMixerState prev;
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
            if (_state != AVMixerState.Paused) return MediaResult.Success;
            prev = _state;
            _state = AVMixerState.Running;
        }
        _clock.Start();
        _pauseEvent.Set();
        RaiseStateChanged(prev, AVMixerState.Running);
        return MediaResult.Success;
    }

    protected int Stop()
    {
        AVMixerState prev;
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
            if (_state == AVMixerState.Stopped) return MediaResult.Success;
            prev = _state;
            _state = AVMixerState.Stopped;
        }
        _pauseEvent.Set();
        _clock.Stop();
        RaiseStateChanged(prev, AVMixerState.Stopped);
        return MediaResult.Success;
    }

    public int Seek(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
            return (int)MediaErrorCode.MediaInvalidArgument;

        // Fast path when idle — execute synchronously (no pump threads running).
        bool hasPump;
        lock (_gate) hasPump = _cancelSource is not null;

        if (!hasPump)
        {
            ClearVideoQueue();
            _audioTimelineSeconds = positionSeconds;
            _clock.Seek(positionSeconds);
            foreach (var (src, _) in GetAudioSourcesSnapshot()) _ = src.Seek(positionSeconds);
            foreach (var src in GetVideoSourcesSnapshot())      _ = src.Seek(positionSeconds);
            return MediaResult.Success;
        }

        // Running — enqueue for AudioPumpLoop (drops oldest if already pending).
        _seekChannel.Writer.TryWrite(positionSeconds);
        return MediaResult.Success;
    }

    // ── source / output management ────────────────────────────────────────────

    public int AddAudioSource(IAudioSource source) => AddAudioSource(source, 0);

    public int AddAudioSource(IAudioSource source, double startOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
            if (_audioSources.Any(x => x.Source.Id == source.Id))
                return (int)MediaErrorCode.MixerSourceIdCollision;
            _audioSources.Add((source, Math.Max(0, startOffsetSeconds)));
            _audioSourcesNeedsUpdate = true;
        }
        return MediaResult.Success;
    }

    public int SetAudioSourceStartOffset(IAudioSource source, double startOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            for (var i = 0; i < _audioSources.Count; i++)
            {
                if (_audioSources[i].Source.Id != source.Id) continue;
                _audioSources[i] = (_audioSources[i].Source, Math.Max(0, startOffsetSeconds));
                return MediaResult.Success;
            }
        }
        return (int)MediaErrorCode.MediaInvalidArgument;
    }

    public int RemoveAudioSource(IAudioSource source, bool stopOnDetach = false, bool disposeOnDetach = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        IAudioSource? found = null;
        lock (_gate)
        {
            for (var i = 0; i < _audioSources.Count; i++)
            {
                if (_audioSources[i].Source.Id != source.Id) continue;
                found = _audioSources[i].Source;
                _audioSources.RemoveAt(i);
                _audioSourcesNeedsUpdate = true;
                break;
            }
        }
        if (found is null) return (int)MediaErrorCode.MediaInvalidArgument;
        if (stopOnDetach)   found.Stop();
        if (disposeOnDetach) found.Dispose();
        return MediaResult.Success;
    }

    public int AddVideoSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.MediaObjectDisposed;
            if (_videoSources.Any(x => x.Id == source.Id))
                return (int)MediaErrorCode.MixerSourceIdCollision;
            _videoSources.Add(source);
            _activeVideoSourceId ??= source.Id;
        }
        return MediaResult.Success;
    }

    public int RemoveVideoSource(IVideoSource source, bool stopOnDetach = false, bool disposeOnDetach = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        IVideoSource? found = null;
        lock (_gate)
        {
            for (var i = 0; i < _videoSources.Count; i++)
            {
                if (_videoSources[i].Id != source.Id) continue;
                found = _videoSources[i];
                _videoSources.RemoveAt(i);
                break;
            }
            if (_activeVideoSourceId == source.Id)
                _activeVideoSourceId = _videoSources.Count > 0 ? _videoSources[0].Id : null;
        }
        if (found is null) return (int)MediaErrorCode.MediaInvalidArgument;
        if (stopOnDetach)   found.Stop();
        if (disposeOnDetach) found.Dispose();
        return MediaResult.Success;
    }

    public int SetActiveVideoSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Guid? prev;
        lock (_gate)
        {
            if (!_videoSources.Any(x => x.Id == source.Id))
                return (int)MediaErrorCode.MediaInvalidArgument;
            prev = _activeVideoSourceId;
            _activeVideoSourceId = source.Id;
        }
        if (prev != source.Id)
            ActiveVideoSourceChanged?.Invoke(this, new VideoActiveSourceChangedEventArgs(prev, source.Id));
        return MediaResult.Success;
    }

    public int AddAudioOutput(IAudioSink output)    { ArgumentNullException.ThrowIfNull(output); lock (_gate) _audioOutputs.Add(output);    return MediaResult.Success; }
    public int RemoveAudioOutput(IAudioSink output) { ArgumentNullException.ThrowIfNull(output); lock (_gate) _audioOutputs.Remove(output); return MediaResult.Success; }

    public int AddVideoOutput(IVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate) _videoOutputs.Add(output);
        _videoOutputsNeedsUpdate = true;
        return MediaResult.Success;
    }

    public int RemoveVideoOutput(IVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            _videoOutputs.Remove(output);
            if (_videoWorkers.Remove(output.Id, out var w)) w.Dispose();
        }
        _videoOutputsNeedsUpdate = true;
        return MediaResult.Success;
    }

    // ── configuration ─────────────────────────────────────────────────────────

    public int SetSyncMode(AVSyncMode syncMode)   { lock (_gate) _syncMode = syncMode;         return MediaResult.Success; }

    public int SetClockType(ClockType clockType)
    {
        if (clockType != ClockType.External && clockType != ClockType.Hybrid)
            return (int)MediaErrorCode.MixerClockTypeInvalid;
        lock (_gate) _clockType = clockType;
        return MediaResult.Success;
    }

    // ── playback ──────────────────────────────────────────────────────────────

    public int StartPlayback(AVMixerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            if (_disposed)        return (int)MediaErrorCode.MediaObjectDisposed;
            if (_cancelSource is not null) return MediaResult.Success;
        }

        if (State != AVMixerState.Running) { var c = Start(); if (c != MediaResult.Success) return c; }

        foreach (var (src, _) in GetAudioSourcesSnapshot())
            if (src.State == AudioSourceState.Stopped) src.Start();
        foreach (var src in GetVideoSourcesSnapshot())
            if (src.State == VideoSourceState.Stopped) src.Start();

        StartPlaybackThreads(config);
        return MediaResult.Success;
    }

    public int StopPlayback() { StopPlaybackThreads(); return MediaResult.Success; }

    public int PausePlayback() => Pause();

    public int ResumePlayback() => Resume();


    // ── diagnostics ───────────────────────────────────────────────────────────

    public AVMixerDiagnostics? GetDebugInfo()
    {
        int wQDepth = 0, wQMax = 0;
        long wEnqDrop = 0, wStaleDrop = 0, wFail = 0;
        lock (_gate)
        {
            foreach (var w in _videoWorkers.Values)
            {
                var d = w.QueueDepth;
                wQDepth += d;
                if (d > wQMax) wQMax = d;
                wEnqDrop  += Interlocked.Read(ref w.EnqueueDrops);
                wStaleDrop += Interlocked.Read(ref w.StaleDrops);
                wFail      += Interlocked.Read(ref w.PushFailures);
            }
        }
        return new AVMixerDiagnostics(
            Interlocked.Read(ref _videoPushed), Interlocked.Read(ref _videoPushFailures),
            Interlocked.Read(ref _videoNoFrame), Interlocked.Read(ref _videoLateDrops),
            Interlocked.Read(ref _videoQueueTrimDrops), Interlocked.Read(ref _videoCoalescedDrops),
            _videoQueueDepthVal,
            Interlocked.Read(ref _audioPushFailures), Interlocked.Read(ref _audioReadFailures),
            Interlocked.Read(ref _audioEmptyReads), Interlocked.Read(ref _audioPushedFrames),
            wEnqDrop, wStaleDrop, wFail, wQDepth, wQMax);
    }

    public IReadOnlyList<VideoOutputDiagnostics> GetVideoOutputDiagnostics()
    {
        lock (_gate)
        {
            var list = new List<VideoOutputDiagnostics>(_videoOutputs.Count);
            foreach (var o in _videoOutputs)
            {
                var cap = _playbackConfig?.GetVideoOutputQueueCapacity(o.Id) ?? 0;
                if (_videoWorkers.TryGetValue(o.Id, out var w))
                    list.Add(new VideoOutputDiagnostics(o.Id, w.QueueDepth, cap,
                        Interlocked.Read(ref w.EnqueueDrops),
                        Interlocked.Read(ref w.StaleDrops),
                        Interlocked.Read(ref w.PushFailures)));
                else
                    list.Add(new VideoOutputDiagnostics(o.Id, 0, 0, 0, 0, 0));
            }
            return list;
        }
    }

    // ── events ────────────────────────────────────────────────────────────────

    public event EventHandler<AVMixerStateChangedEventArgs>? StateChanged;
    public event EventHandler<MediaSourceErrorEventArgs>?            AudioSourceError;
    public event EventHandler<MediaSourceErrorEventArgs>?            VideoSourceError;
    public event EventHandler<VideoActiveSourceChangedEventArgs>?    ActiveVideoSourceChanged;

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        lock (_gate) { if (_disposed) return; _disposed = true; }
        StopPlaybackThreads();
        Stop();
        _pauseEvent.Dispose();
    }

    // ── private: thread management ────────────────────────────────────────────

    private void StartPlaybackThreads(AVMixerConfig config)
    {
        lock (_gate)
        {
            _playbackConfig = config;
            _cancelSource   = new CancellationTokenSource();
            _pauseEvent.Set();

            // If the config carries an explicit sync-mode preference, apply it now.
            if (config.SyncMode.HasValue) _syncMode = config.SyncMode.Value;

            if (config.PresentationHostPolicy == VideoDispatchPolicy.BackgroundWorker)
            {
                var staleThreshold = config.PresenterSyncOptions?.StaleFrameDropThreshold
                    ?? config.OutputStaleFrameThreshold;
                if (staleThreshold <= TimeSpan.Zero)
                    staleThreshold = TimeSpan.FromMilliseconds(200);

                foreach (var output in _videoOutputs)
                {
                    if (_videoWorkers.ContainsKey(output.Id)) continue;
                    var cap = config.GetVideoOutputQueueCapacity(output.Id);
                    _videoWorkers[output.Id] = new OutputWorker(
                        output, cap, staleThreshold, () => _audioTimelineSeconds);
                }
            }
        }

        var ct = _cancelSource!.Token;

        // N5: audio pump always starts — handles empty-source case gracefully (same as video threads).
        _audioPumpThread = new Thread(() => AudioPumpLoop(ct))
        { Name = "AVMixer.AudioPump", IsBackground = true, Priority = ThreadPriority.Highest };
        _audioPumpThread.Start();

        // Video threads always start so that sources added after StartPlayback are served.
        // VideoDecodeLoop and VideoPresentLoop handle the empty-source case gracefully
        // (GetActiveVideoSource returns null → Thread.Sleep(2)).
        _videoDecodeThread = new Thread(() => VideoDecodeLoop(ct))
        { Name = "AVMixer.VideoDecode", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _videoDecodeThread.Start();

        _videoPresentThread = new Thread(() => VideoPresentLoop(ct))
        { Name = "AVMixer.VideoPresent", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _videoPresentThread.Start();
    }

    private void StopPlaybackThreads()
    {
        CancellationTokenSource? cts;
        Dictionary<Guid, OutputWorker> workers;
        lock (_gate)
        {
            cts = _cancelSource;
            _cancelSource = null;
            workers = _videoWorkers;
            _videoWorkers = [];
        }

        if (cts is null) return;
        _pauseEvent.Set();
        cts.Cancel();

        _audioPumpThread?.Join(TimeSpan.FromSeconds(4));
        _videoDecodeThread?.Join(TimeSpan.FromSeconds(4));
        _videoPresentThread?.Join(TimeSpan.FromSeconds(4));
        _audioPumpThread = _videoDecodeThread = _videoPresentThread = null;

        foreach (var w in workers.Values) w.Dispose();
        cts.Dispose();
        ClearVideoQueue();
    }

    // ── private: audio pump ───────────────────────────────────────────────────

    private void AudioPumpLoop(CancellationToken ct)
    {
        var config = _playbackConfig!;
        var sourceChannels = Math.Max(1, config.SourceChannelCount);
        var routeMap = config.RouteMap?.Length > 0 ? config.RouteMap : [0, 1];
        var framesPerBatch = config.AudioReadFrames > 0 ? config.AudioReadFrames : 1024;
        var mixBuf  = new float[framesPerBatch * sourceChannels];
        var tempBuf = new float[framesPerBatch * sourceChannels];
        var timelineSamples = 0L;

        // Per-source buffers used only when routing rules are active.
        var sourceBufs   = new Dictionary<Guid, float[]>();
        var sourceFrames = new Dictionary<Guid, int>();

        // Per-output mix buffers for the routing path.
        // Keyed by output Id; sized to framesPerBatch * outputChannelCount (derived from rules).
        var outputBufs = new Dictionary<Guid, float[]>();

        // Per-source resamplers, created lazily when a source's rate != sampleRate (§10.9 / P2-6).
        // Only populated when config.ResamplerFactory != null.
        var resamplers = new Dictionary<Guid, IAudioResampler>();
        // Resampler output buffer, sized on demand.
        float[]? resampledBuf = null;

        // Bootstrap snapshot before the loop.
        (IAudioSource Source, double StartOffset)[] srcs = GetAudioSourcesSnapshot().ToArray();
        _audioSourcesNeedsUpdate = false;

        int sampleRate;
        if (config.OutputSampleRate > 0)
            sampleRate = config.OutputSampleRate;
        else if (srcs.Length > 0 && srcs[0].Source.StreamInfo.SampleRate.GetValueOrDefault(0) > 0)
            sampleRate = srcs[0].Source.StreamInfo.SampleRate!.Value;
        else
            sampleRate = 48_000;

        try
        {

        while (!ct.IsCancellationRequested)
        {
            _pauseEvent.Wait(ct);
            if (ct.IsCancellationRequested) break;

            // §3.9 — drain seek channel (no thread restart needed).
            if (_seekChannel.Reader.TryRead(out var seekPos))
            {
                timelineSamples = (long)(seekPos * sampleRate);
                ClearVideoQueue();
                _audioTimelineSeconds = seekPos;
                _clock.Seek(seekPos);
                foreach (var (src, _) in srcs)                      _ = src.Seek(seekPos);
                foreach (var vSrc in GetVideoSourcesSnapshot())      _ = vSrc.Seek(seekPos);
                // Reset resampler fractional state after a seek.
                foreach (var r in resamplers.Values) r.Reset();
                continue;
            }

            // Refresh cached snapshot only when the source list changed.
            if (_audioSourcesNeedsUpdate)
            {
                srcs = GetAudioSourcesSnapshot().ToArray();
                _audioSourcesNeedsUpdate = false;
            }

            var timelineSeconds = (double)timelineSamples / sampleRate;

            // ── choose path based on routing rules ────────────────────────────
            AudioRoutingRule[]? rules = null;
            lock (_gate)
            {
                if (_audioRoutingRules.Count > 0)
                    rules = [.. _audioRoutingRules];
            }

            if (rules is null)
            {
                // ── FAST PATH: no routing rules — mix all sources to all outputs ──
                Array.Clear(mixBuf, 0, mixBuf.Length);
                var framesProduced = 0;
                var anyRead = false;

                foreach (var (src, offset) in srcs)
                {
                    if (src.State != AudioSourceState.Running) continue;
                    if (timelineSeconds < offset) continue;

                    Array.Clear(tempBuf, 0, tempBuf.Length);
                    if (src.ReadSamples(tempBuf, framesPerBatch, out var fr) == MediaResult.Success && fr > 0)
                    {
                        // ── Optional resampling (§10.9 / P2-6) ───────────────────────
                        var srcRate = src.StreamInfo.SampleRate.GetValueOrDefault(0);
                        if (config.ResamplerFactory != null && srcRate > 0 && srcRate != sampleRate)
                        {
                            // Get or create a resampler for this source.
                            if (!resamplers.TryGetValue(src.Id, out var resampler))
                                resamplers[src.Id] = resampler = config.ResamplerFactory(srcRate, sampleRate);

                            // Ensure the output buffer is large enough.
                            var needed = resampler.EstimateOutputFrameCount(fr) * sourceChannels;
                            if (resampledBuf is null || resampledBuf.Length < needed)
                                resampledBuf = new float[needed];

                            var outFrames = resampler.Resample(
                                new ReadOnlySpan<float>(tempBuf, 0, fr * sourceChannels),
                                fr, new Span<float>(resampledBuf, 0, needed));

                            if (outFrames > 0)
                            {
                                anyRead = true;
                                if (outFrames > framesProduced) framesProduced = outFrames;
                                AudioMixUtils.MixInto(mixBuf, resampledBuf, outFrames * sourceChannels, src.Volume);
                            }
                        }
                        else
                        {
                            anyRead = true;
                            if (fr > framesProduced) framesProduced = fr;
                            AudioMixUtils.MixInto(mixBuf, tempBuf, fr * sourceChannels, src.Volume);
                        }
                    }
                    else Interlocked.Increment(ref _audioReadFailures);
                }

                if (!anyRead)
                {
                    Interlocked.Increment(ref _audioEmptyReads);
                    Thread.Sleep(1);
                    continue;
                }

                var active = framesProduced * sourceChannels;
                AudioMixUtils.Clamp(mixBuf, active);
                AudioMixUtils.ApplyVolume(mixBuf, active, MasterVolume);

                var frame = BuildAudioFrame(mixBuf, framesProduced, sourceChannels, sampleRate, timelineSeconds);
                foreach (var output in GetAudioOutputsSnapshot())
                {
                    if (output.PushFrame(in frame, routeMap, sourceChannels) != MediaResult.Success)
                        Interlocked.Increment(ref _audioPushFailures);
                    else
                        Interlocked.Increment(ref _audioPushedFrames);
                }

                timelineSamples += framesProduced;
                UpdateAudioClock(timelineSamples, sampleRate);
            }
            else
            {
                // ── ROUTING PATH: read each source once, then mix per-output ──
                // This allows per-source-to-output filtering with per-rule gain.
                sourceFrames.Clear();
                var framesProduced = 0;
                var anyRead = false;

                foreach (var (src, offset) in srcs)
                {
                    if (src.State != AudioSourceState.Running) continue;
                    if (timelineSeconds < offset) continue;

                    var size = framesPerBatch * sourceChannels;
                    if (!sourceBufs.TryGetValue(src.Id, out var sbuf) || sbuf.Length < size)
                        sourceBufs[src.Id] = sbuf = new float[size];

                    Array.Clear(sbuf, 0, size);
                    if (src.ReadSamples(sbuf, framesPerBatch, out var fr) == MediaResult.Success && fr > 0)
                    {
                        anyRead = true;
                        if (fr > framesProduced) framesProduced = fr;
                        sourceFrames[src.Id] = fr;
                    }
                    else
                    {
                        Interlocked.Increment(ref _audioReadFailures);
                        sourceFrames[src.Id] = 0;
                    }
                }

                if (!anyRead)
                {
                    Interlocked.Increment(ref _audioEmptyReads);
                    Thread.Sleep(1);
                    continue;
                }

                var outputs = GetAudioOutputsSnapshot();

                foreach (var output in outputs)
                {
                    // ── Determine output channel count from rules targeting this output ──
                    // The output channel count equals max(rule.OutputChannel) + 1 across all
                    // rules that target this output.  Zero means no rules — skip.
                    var outChanCount = 0;
                    foreach (var r in rules)
                        if (r.OutputId == output.Id && r.OutputChannel + 1 > outChanCount)
                            outChanCount = r.OutputChannel + 1;
                    if (outChanCount == 0) continue;

                    // ── Get / resize the per-output mix buffer ──
                    var outSize = framesProduced * outChanCount;
                    if (!outputBufs.TryGetValue(output.Id, out var outBuf) || outBuf.Length < outSize)
                        outputBufs[output.Id] = outBuf = new float[outSize];
                    Array.Clear(outBuf, 0, outSize);

                    // ── Mix each source's channels into the output buffer via routing rules ──
                    foreach (var (src, _) in srcs)
                    {
                        if (!sourceBufs.TryGetValue(src.Id, out var sbuf)) continue;
                        if (!sourceFrames.TryGetValue(src.Id, out var fr) || fr == 0) continue;

                        var srcVol = src.Volume;

                        // Apply ALL rules for this source→output pair.
                        // Multiple rules are normal: e.g. (ch0→ch0, gain=1) + (ch1→ch1, gain=1).
                        foreach (var r in rules)
                        {
                            if (r.SourceId != src.Id || r.OutputId != output.Id) continue;

                            // Guard against out-of-range channel indices.
                            if ((uint)r.SourceChannel >= (uint)sourceChannels) continue;
                            if ((uint)r.OutputChannel  >= (uint)outChanCount)   continue;

                            AudioMixUtils.MixChannel(
                                outBuf, r.OutputChannel, outChanCount,
                                sbuf,   r.SourceChannel, sourceChannels,
                                fr, srcVol * r.Gain);
                        }
                    }

                    // ── Post-process and push ──
                    AudioMixUtils.Clamp(outBuf, outSize);
                    AudioMixUtils.ApplyVolume(outBuf, outSize, MasterVolume);

                    // Build a frame whose channel count matches the output layout derived from rules.
                    // Use the identity PushFrame overload — outBuf is already routed correctly.
                    var outFrame = BuildAudioFrame(outBuf, framesProduced, outChanCount, sampleRate, timelineSeconds);
                    if (output.PushFrame(in outFrame) != MediaResult.Success)
                        Interlocked.Increment(ref _audioPushFailures);
                    else
                        Interlocked.Increment(ref _audioPushedFrames);
                }

                timelineSamples += framesProduced;
                UpdateAudioClock(timelineSamples, sampleRate);
            }
        }
        }
        finally
        {
            // Dispose any per-source resamplers created during this playback session.
            foreach (var r in resamplers.Values) r.Dispose();
            resamplers.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AudioFrame BuildAudioFrame(
        float[] buf, int frames, int channels, int sampleRate, double timelineSeconds) =>
        new(Samples: new ReadOnlyMemory<float>(buf, 0, frames * channels),
            FrameCount: frames,
            SourceChannelCount: channels,
            Layout: AudioFrameLayout.Interleaved,
            SampleRate: sampleRate,
            PresentationTime: TimeSpan.FromSeconds(timelineSeconds));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateAudioClock(long timelineSamples, int sampleRate)
    {
        if (_syncMode != AVSyncMode.AudioLed) return;
        var secs = (double)timelineSamples / sampleRate;
        _audioTimelineSeconds = secs;
        _clock.Seek(secs);
    }

    // ── private: video decode ─────────────────────────────────────────────────

    private void VideoDecodeLoop(CancellationToken ct)
    {
        var capacity = _playbackConfig!.VideoDecodeQueueCapacity;
        while (!ct.IsCancellationRequested)
        {
            _pauseEvent.Wait(ct);
            if (ct.IsCancellationRequested) break;

            var src = GetActiveVideoSource();
            if (src is null) { Thread.Sleep(2); continue; }

            // N14: skip sources that are not actively running.
            // N12: sleep longer at EndOfStream to avoid busy-waiting at EOF.
            if (src.State == VideoSourceState.Stopped)     { Thread.Sleep(5);  continue; }
            if (src.State == VideoSourceState.EndOfStream) { Thread.Sleep(50); continue; }

            if (src.ReadFrame(out var frame) != MediaResult.Success) { Thread.Sleep(2); continue; }

            // N8: merge depth check and enqueue into a single lock to eliminate the TOCTOU window.
            bool enqueued;
            lock (_videoQueueLock)
            {
                if (_videoDecodeQueue.Count < capacity)
                {
                    _videoDecodeQueue.Enqueue(frame);
                    _videoQueueDepthVal = _videoDecodeQueue.Count;
                    enqueued = true;
                }
                else enqueued = false;
            }
            if (!enqueued) { frame.Dispose(); Thread.Sleep(1); }
        }
    }

    // ── private: video presentation ───────────────────────────────────────────

    private void VideoPresentLoop(CancellationToken ct)
    {
        var config = _playbackConfig!;
        var policyOptions = config.PresenterSyncOptions ?? new VideoSyncOptions(
            StaleFrameDropThreshold: config.OutputStaleFrameThreshold,
            FrameEarlyTolerance: TimeSpan.FromMilliseconds(2),
            MinDelay: TimeSpan.FromMilliseconds(1),
            MaxWait: TimeSpan.FromMilliseconds(50));
        var syncMode   = _syncMode;
        var useWorkers = config.PresentationHostPolicy == VideoDispatchPolicy.BackgroundWorker;

        while (!ct.IsCancellationRequested)
        {
            _pauseEvent.Wait(ct);
            if (ct.IsCancellationRequested) break;

            // N7: refresh output and routing-rule caches when the lists have changed.
            if (_videoOutputsNeedsUpdate)
            {
                lock (_gate) { _videoOutputsCache = [.. _videoOutputs]; }
                _videoOutputsNeedsUpdate = false;
            }
            if (_videoRoutingRulesNeedsUpdate)
            {
                lock (_gate) { _videoRoutingRulesCache = [.. _videoRoutingRules]; }
                _videoRoutingRulesNeedsUpdate = false;
            }

            var clockSec = syncMode == AVSyncMode.AudioLed
                ? _audioTimelineSeconds
                : _clock.CurrentSeconds;

            VideoPresenterSyncDecision decision;
            lock (_videoQueueLock)
            {
                decision = VideoSyncPolicy.SelectNextFrame(
                    _videoDecodeQueue, syncMode, clockSec, policyOptions);
                _videoQueueDepthVal = _videoDecodeQueue.Count;
            }

            Interlocked.Add(ref _videoLateDrops,     decision.LateDrops);
            Interlocked.Add(ref _videoCoalescedDrops, decision.CoalescedDrops);

            if (decision.Frame is not null)
            {
                using var frame = decision.Frame;
                PushFrameToOutputs(frame, frame.PresentationTime, config, useWorkers);
            }
            else
            {
                Interlocked.Increment(ref _videoNoFrame);
            }

            if (decision.Delay > TimeSpan.Zero)
                Thread.Sleep(Math.Clamp((int)Math.Ceiling(decision.Delay.TotalMilliseconds), 1, 50));
        }
    }

    private void PushFrameToOutputs(
        VideoFrame frame, TimeSpan pts, AVMixerConfig config, bool useWorkers)
    {
        // N7: use pre-cached snapshots — refreshed at the top of VideoPresentLoop, no lock/allocation per frame.
        var outputs  = _videoOutputsCache;
        var rules    = _videoRoutingRulesCache;
        var hasRules = rules.Length > 0;

        Dictionary<Guid, OutputWorker> workers;
        lock (_gate) workers = _videoWorkers;   // only lock for the worker-dict reference (cheap)

        foreach (var output in outputs)
        {
            if (hasRules && !Array.Exists(rules, r => r.OutputId == output.Id)) continue;

            if (useWorkers && workers.TryGetValue(output.Id, out var worker))
            {
                worker.Enqueue(frame, pts);
                Interlocked.Increment(ref _videoPushed);
            }
            else
            {
                if (output.PushFrame(frame, pts) != MediaResult.Success)
                    Interlocked.Increment(ref _videoPushFailures);
                else
                    Interlocked.Increment(ref _videoPushed);
            }
        }
    }

    // ── private: helpers ──────────────────────────────────────────────────────

    private IVideoSource? GetActiveVideoSource()
    {
        lock (_gate)
        {
            var id = _activeVideoSourceId;
            return id is null ? null : _videoSources.FirstOrDefault(s => s.Id == id);
        }
    }

    private List<(IAudioSource Source, double StartOffset)> GetAudioSourcesSnapshot()
    { lock (_gate) { return [.. _audioSources]; } }

    private List<IVideoSource> GetVideoSourcesSnapshot()
    { lock (_gate) { return [.. _videoSources]; } }

    private List<IAudioSink> GetAudioOutputsSnapshot()
    { lock (_gate) { return [.. _audioOutputs]; } }

    private void ClearVideoQueue()
    {
        lock (_videoQueueLock)
        {
            while (_videoDecodeQueue.TryDequeue(out var f)) f.Dispose();
            _videoQueueDepthVal = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RaiseStateChanged(AVMixerState prev, AVMixerState next) =>
        StateChanged?.Invoke(this, new AVMixerStateChangedEventArgs(prev, next));
}
