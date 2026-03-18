using System.Collections.Concurrent;
using System.Diagnostics;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Source-driven transport engine backed by a shared <see cref="IVideoClock"/>.
/// It advances attached video sources on a dedicated timing thread so video-only playback works without an audio engine.
/// </summary>
public class VideoTransportEngine : IVideoTransportEngine
{
    private enum ClockDriveMode
    {
        InternalRealtime,
        ExternalClock
    }

    private const int DefaultClockSampleRate = 48000;
    private const int DefaultClockChannels = 2;

    private readonly Guid _engineId = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, IVideoSource> _videoSources = new();
    private readonly ConcurrentDictionary<Guid, IVideoOutput> _videoOutputs = new();
    private readonly Lock _syncLock = new();
    private readonly Lock _sourceSnapshotLock = new();
    private readonly Lock _outputSnapshotLock = new();
    private readonly ManualResetEventSlim _pauseEvent = new(initialState: false);
    private readonly Thread _clockThread;
    private readonly IVideoClock _clock;
    private readonly IDisposable? _ownedClockDisposable;
    private readonly bool _ownsClock;
    private readonly ClockDriveMode _clockDriveMode;

    private IVideoSource[] _cachedVideoSources = Array.Empty<IVideoSource>();
    private IVideoOutput[] _cachedVideoOutputs = Array.Empty<IVideoOutput>();
    private volatile bool _sourceSnapshotsDirty = true;
    private volatile bool _outputSnapshotsDirty = true;
    private volatile bool _stopRequested;
    private volatile bool _isRunning;
    private volatile bool _disposed;
    private bool _clockThreadStarted;
    private double _transportBaseTimestampSeconds;
    private long _transportBaseTicks;
    private Guid? _primarySourceId;

    public VideoTransportEngine(VideoTransportEngineConfig? config = null)
        : this(videoClock: null, config: config, ownsClock: true)
    {
    }

    public VideoTransportEngine(IVideoClock? videoClock, VideoTransportEngineConfig? config = null, bool ownsClock = false)
    {
        Config = (config ?? new VideoTransportEngineConfig()).CloneNormalized();
        var hasExternalClock = videoClock != null;

        if (videoClock == null)
        {
            var masterClock = new MasterClock(DefaultClockSampleRate, DefaultClockChannels);
            _clock = new MasterClockVideoClockAdapter(masterClock);
            _ownedClockDisposable = masterClock;
            _ownsClock = true;
        }
        else
        {
            _clock = videoClock;
            _ownsClock = ownsClock;
            _ownedClockDisposable = null;
        }

        EffectiveClockSyncMode = ResolveEffectiveClockSyncMode(Config.ClockSyncMode, hasExternalClock);
        _clockDriveMode = ResolveClockDriveMode(EffectiveClockSyncMode);

        if (_clockDriveMode == ClockDriveMode.InternalRealtime)
        {
            ResetTransportState(0);
            PublishClockTimestamp(0);
        }
        else
        {
            ResetTransportState(_clock.CurrentTimestamp);
        }

        _clockThread = new Thread(ClockThreadLoop)
        {
            Name = "VideoTransportEngine.ClockThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
    }

    public Guid EngineId => _engineId;

    public VideoTransportEngineConfig Config { get; }

    public IVideoClock Clock => _clock;

    /// <summary>
    /// Effective clock sync mode after resolving external-clock availability.
    /// </summary>
    public VideoTransportClockSyncMode EffectiveClockSyncMode { get; }

    public double Position => _clock.CurrentTimestamp;

    public bool IsRunning => _isRunning;

    public int SourceCount => _videoSources.Count;

    public int OutputCount => _videoOutputs.Count;

    public event EventHandler<VideoErrorEventArgs>? SourceError;

    public bool AddVideoSource(IVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        if (!_videoSources.TryAdd(source.Id, source))
            return false;

        source.Error += OnVideoSourceError;
        InvalidateSourceSnapshots();

        lock (_syncLock)
        {
            source.AttachToClock(_clock);
            SeekVideoToTimeline(source, Position);
            if (_isRunning)
                source.Play();
            else
                source.Pause();
        }

        PrimeSource(source);

        lock (_syncLock)
        {
            if (_primarySourceId == null || !_videoSources.ContainsKey(_primarySourceId.Value))
                _primarySourceId = source.Id;

            RebindOutputsToPrimarySourceLocked();
        }

        return true;
    }

    public bool AddSource(IVideoSource source) => AddVideoSource(source);

    public bool RemoveVideoSource(IVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return RemoveVideoSource(source.Id);
    }

    public bool RemoveVideoSource(Guid sourceId)
    {
        ThrowIfDisposed();

        if (!_videoSources.TryRemove(sourceId, out var source))
            return false;

        source.Error -= OnVideoSourceError;
        InvalidateSourceSnapshots();

        lock (_syncLock)
        {
            if (_primarySourceId == sourceId)
                _primarySourceId = null;

            EnsurePrimarySourceLocked();
            RebindOutputsToPrimarySourceLocked();
        }

        try
        {
            source.DetachFromClock();
        }
        catch
        {
            // Best effort during removal.
        }

        try
        {
            source.Stop();
        }
        catch
        {
            // Best effort during removal.
        }

        return true;
    }

    public bool RemoveSource(IVideoSource source) => RemoveVideoSource(source);

    public bool RemoveSource(Guid sourceId) => RemoveVideoSource(sourceId);

    public IVideoSource[] GetVideoSources()
    {
        EnsureSourceSnapshots();
        return _cachedVideoSources.ToArray();
    }

    public IVideoSource[] GetSources() => GetVideoSources();

    public bool AddVideoOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);

        if (!_videoOutputs.TryAdd(output.Id, output))
            return false;

        InvalidateOutputSnapshots();

        ApplyOutputPresentationSyncMode(output);

        lock (_syncLock)
            RebindOutputsToPrimarySourceLocked();

        return true;
    }

    public bool AddOutput(IVideoOutput output) => AddVideoOutput(output);

    public bool RemoveVideoOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return RemoveVideoOutput(output.Id);
    }

    public bool RemoveVideoOutput(Guid outputId)
    {
        ThrowIfDisposed();

        if (!_videoOutputs.TryRemove(outputId, out var output))
            return false;

        InvalidateOutputSnapshots();
        try
        {
            output.DetachSource();
        }
        catch
        {
            // Best effort during removal.
        }

        return true;
    }

    public bool RemoveOutput(IVideoOutput output) => RemoveVideoOutput(output);

    public bool RemoveOutput(Guid outputId) => RemoveVideoOutput(outputId);

    public IVideoOutput[] GetVideoOutputs()
    {
        EnsureOutputSnapshots();
        return _cachedVideoOutputs.ToArray();
    }

    public IVideoOutput[] GetOutputs() => GetVideoOutputs();

    public void ClearVideoSources()
    {
        ThrowIfDisposed();

        foreach (var source in _videoSources.Values.ToArray())
            RemoveVideoSource(source.Id);
    }

    public void ClearSources() => ClearVideoSources();

    public void ClearVideoOutputs()
    {
        ThrowIfDisposed();

        foreach (var output in _videoOutputs.Values.ToArray())
            RemoveVideoOutput(output.Id);
    }

    public void ClearOutputs() => ClearVideoOutputs();

    public void Start()
    {
        ThrowIfDisposed();
        if (_isRunning)
            return;

        lock (_syncLock)
        {
            if (_clockDriveMode == ClockDriveMode.InternalRealtime)
            {
                ResetTransportState(Position);
                PublishClockTimestamp(Position);
            }

            _isRunning = true;
            _pauseEvent.Set();

            if (!_clockThreadStarted)
            {
                _clockThreadStarted = true;
                _clockThread.Start();
            }

            foreach (var source in GetVideoSourceSnapshot())
                source.Play();
        }

        PumpSourcesOnce(includePausedSources: true);
    }

    public void Pause()
    {
        ThrowIfDisposed();
        if (!_isRunning)
            return;

        lock (_syncLock)
        {
            var pausedTimestamp = ResolveClockTimestamp();
            _isRunning = false;
            _pauseEvent.Reset();

            if (_clockDriveMode == ClockDriveMode.InternalRealtime)
            {
                ResetTransportState(pausedTimestamp);
                PublishClockTimestamp(pausedTimestamp);
            }

            foreach (var source in GetVideoSourceSnapshot())
                source.Pause();
        }
    }

    public void Stop()
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            _isRunning = false;
            _pauseEvent.Reset();
            ResetTransportState(0);
            PublishClockTimestamp(0);

            foreach (var source in GetVideoSourceSnapshot())
                source.Stop();
        }
    }

    public void Seek(double positionInSeconds)
    {
        Seek(positionInSeconds, safeSeek: false);
    }

    public void Seek(double positionInSeconds, bool safeSeek)
    {
        ThrowIfDisposed();

        var target = ClampTimelinePosition(positionInSeconds);
        var wasRunning = safeSeek && _isRunning;
        if (wasRunning)
            Pause();

        lock (_syncLock)
        {
            ResetTransportState(target);
            PublishClockTimestamp(target);

            foreach (var source in GetVideoSourceSnapshot())
                SeekVideoToTimeline(source, target);
        }

        PumpSourcesOnce(includePausedSources: true);

        if (wasRunning)
            Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _stopRequested = true;
        _isRunning = false;
        _pauseEvent.Set();

        if (_clockThreadStarted && _clockThread.IsAlive)
        {
            try
            {
                _clockThread.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best effort thread shutdown.
            }
        }

        foreach (var source in GetVideoSourceSnapshot())
        {
            try
            {
                source.Error -= OnVideoSourceError;
                source.DetachFromClock();
            }
            catch
            {
                // Best effort during disposal.
            }
        }

        foreach (var output in GetVideoOutputSnapshot())
        {
            try
            {
                output.DetachSource();
            }
            catch
            {
                // Best effort during disposal.
            }

            try
            {
                output.Dispose();
            }
            catch
            {
                // Best effort during disposal.
            }
        }

        _videoSources.Clear();
        _videoOutputs.Clear();
        InvalidateSourceSnapshots();
        InvalidateOutputSnapshots();
        _pauseEvent.Dispose();

        if (_ownsClock)
            _ownedClockDisposable?.Dispose();

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoTransportEngine));
    }

    private void OnVideoSourceError(object? sender, VideoErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    private void ClockThreadLoop()
    {
        var nextTick = Stopwatch.GetTimestamp();

        while (!_stopRequested)
        {
            if (!_isRunning)
            {
                _pauseEvent.Wait(100);
                nextTick = Stopwatch.GetTimestamp();
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            if (now < nextTick)
            {
                var remainingTicks = nextTick - now;
                var sleepMs = (int)Math.Min(10, Math.Max(0, remainingTicks * 1000 / Stopwatch.Frequency));
                Thread.Sleep(sleepMs > 0 ? sleepMs : 0);
                continue;
            }

            PublishClockTimestamp();
            PumpSourcesOnce(includePausedSources: false);
            TryPauseWhenPlaybackStops();

            var intervalMs = ResolveAdvanceIntervalMs();
            var intervalTicks = Math.Max(1L, (long)(Stopwatch.Frequency * (intervalMs / 1000.0)));
            do
            {
                nextTick += intervalTicks;
            } while (nextTick <= now);
        }
    }

    private void TryPauseWhenPlaybackStops()
    {
        var sources = GetVideoSourceSnapshot();
        if (sources.Length == 0)
            return;

        var hasPlayableSource = false;
        foreach (var source in sources)
        {
            if (source.State == VideoPlaybackState.Playing)
                return;

            if (source.State is not VideoPlaybackState.Error and not VideoPlaybackState.EndOfStream and not VideoPlaybackState.Stopped)
                hasPlayableSource = true;
        }

        if (!hasPlayableSource)
            Pause();
    }

    private void PrimeSource(IVideoSource source)
    {
        try
        {
            if (source.IsAttachedToClock)
                source.RequestNextFrame(out _);
        }
        catch
        {
            // Best effort prime only.
        }
    }

    private void PumpSourcesOnce(bool includePausedSources)
    {
        foreach (var source in GetVideoSourceSnapshot())
        {
            var state = source.State;
            if (state == VideoPlaybackState.Playing || (includePausedSources && state == VideoPlaybackState.Paused))
            {
                try
                {
                    source.RequestNextFrame(out _);
                }
                catch
                {
                    // Source errors are surfaced via source.Error.
                }
            }
        }
    }

    private int ResolveAdvanceIntervalMs()
    {
        var minimum = Config.MinimumAdvanceIntervalMs;
        var maximum = Config.MaximumAdvanceIntervalMs;
        var intervalMs = FpsToIntervalMs(Config.UnknownSourcePollFps, minimum, maximum);

        foreach (var source in GetVideoSourceSnapshot())
        {
            var fps = source.StreamInfo.FrameRate;
            if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
                continue;

            var candidate = FpsToIntervalMs(fps, minimum, maximum);
            intervalMs = Math.Min(intervalMs, candidate);
        }

        if (Config.TargetFpsLimit is > 0)
        {
            var limitInterval = FpsToIntervalMs(Config.TargetFpsLimit.Value, minimum, maximum);
            intervalMs = Math.Max(intervalMs, limitInterval);
        }

        return Math.Clamp(intervalMs, minimum, maximum);
    }

    private static int FpsToIntervalMs(double fps, int minimum, int maximum)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
            return Math.Clamp(8, minimum, maximum);

        // Use 80% of the frame period to avoid missing frame-promotion windows.
        var interval = Math.Max(minimum, (int)(800.0 / fps));
        return Math.Clamp(interval, minimum, maximum);
    }

    private void ResetTransportState(double playbackTimestampSeconds)
    {
        Volatile.Write(ref _transportBaseTimestampSeconds, Math.Max(0, playbackTimestampSeconds));
        Volatile.Write(ref _transportBaseTicks, Stopwatch.GetTimestamp());
    }

    private double ResolveClockTimestamp()
    {
        if (_clockDriveMode == ClockDriveMode.ExternalClock)
            return Math.Max(0, _clock.CurrentTimestamp);

        var baseTimestamp = Volatile.Read(ref _transportBaseTimestampSeconds);
        if (!_isRunning)
            return Math.Max(0, baseTimestamp);

        var baseTicks = Volatile.Read(ref _transportBaseTicks);
        if (baseTicks <= 0)
            return Math.Max(0, baseTimestamp);

        var deltaTicks = Stopwatch.GetTimestamp() - baseTicks;
        var elapsedSeconds = deltaTicks <= 0 ? 0 : deltaTicks / (double)Stopwatch.Frequency;
        return Math.Max(0, baseTimestamp + elapsedSeconds);
    }

    private void PublishClockTimestamp()
    {
        if (_clockDriveMode != ClockDriveMode.InternalRealtime)
            return;

        PublishClockTimestamp(ResolveClockTimestamp());
    }

    private void PublishClockTimestamp(double timestampSeconds)
    {
        _clock.SeekTo(Math.Max(0, timestampSeconds));
    }

    private void InvalidateSourceSnapshots()
    {
        _sourceSnapshotsDirty = true;
    }

    private void InvalidateOutputSnapshots()
    {
        _outputSnapshotsDirty = true;
    }

    private void EnsureSourceSnapshots()
    {
        if (!_sourceSnapshotsDirty)
            return;

        lock (_sourceSnapshotLock)
        {
            if (!_sourceSnapshotsDirty)
                return;

            _cachedVideoSources = _videoSources.Values.ToArray();
            _sourceSnapshotsDirty = false;
        }
    }

    private IVideoSource[] GetVideoSourceSnapshot()
    {
        EnsureSourceSnapshots();
        return _cachedVideoSources;
    }

    private void EnsureOutputSnapshots()
    {
        if (!_outputSnapshotsDirty)
            return;

        lock (_outputSnapshotLock)
        {
            if (!_outputSnapshotsDirty)
                return;

            _cachedVideoOutputs = _videoOutputs.Values.ToArray();
            _outputSnapshotsDirty = false;
        }
    }

    private IVideoOutput[] GetVideoOutputSnapshot()
    {
        EnsureOutputSnapshots();
        return _cachedVideoOutputs;
    }

    private void EnsurePrimarySourceLocked()
    {
        if (_primarySourceId.HasValue && _videoSources.ContainsKey(_primarySourceId.Value))
            return;

        _primarySourceId = _videoSources.Keys.FirstOrDefault();
        if (_primarySourceId == Guid.Empty)
            _primarySourceId = null;
    }

    private void RebindOutputsToPrimarySourceLocked()
    {
        // Option A routing model: all outputs follow one selected primary source.
        EnsurePrimarySourceLocked();
        IVideoSource? primarySource = null;
        var hasPrimarySource = _primarySourceId.HasValue && _videoSources.TryGetValue(_primarySourceId.Value, out primarySource);

        foreach (var output in _videoOutputs.Values)
        {
            try
            {
                ApplyOutputPresentationSyncMode(output);

                if (!hasPrimarySource)
                {
                    output.DetachSource();
                    continue;
                }

                if (ReferenceEquals(output.Source, primarySource))
                    continue;

                output.AttachSource(primarySource!);
            }
            catch
            {
                // Output-specific failures should not break transport.
            }
        }
    }

    private void ApplyOutputPresentationSyncMode(IVideoOutput output)
    {
        if (output is IVideoPresentationSyncAwareOutput syncAwareOutput)
            syncAwareOutput.PresentationSyncMode = Config.PresentationSyncMode;
    }

    private static void SeekVideoToTimeline(IVideoSource source, double timelineSeconds)
    {
        var trackPosition = timelineSeconds - source.StartOffset;
        source.Seek(Math.Max(0, trackPosition));
    }

    private static double ClampTimelinePosition(double positionInSeconds)
    {
        if (double.IsNaN(positionInSeconds) || double.IsInfinity(positionInSeconds))
            return 0;

        return Math.Max(0, positionInSeconds);
    }

    private static VideoTransportClockSyncMode ResolveEffectiveClockSyncMode(VideoTransportClockSyncMode configuredMode, bool hasExternalClock)
    {
        if (!hasExternalClock)
            return VideoTransportClockSyncMode.VideoOnly;

        return configuredMode == VideoTransportClockSyncMode.VideoOnly
            ? VideoTransportClockSyncMode.VideoOnly
            : VideoTransportClockSyncMode.AudioLed;
    }

    private static ClockDriveMode ResolveClockDriveMode(VideoTransportClockSyncMode mode)
    {
        return mode == VideoTransportClockSyncMode.VideoOnly
            ? ClockDriveMode.InternalRealtime
            : ClockDriveMode.ExternalClock;
    }
}


