using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Ownaudio.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Core;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// Mixes audio sources directly to an <see cref="IAudioEngine"/> while coordinating video
/// sources with dedicated audio and video clocks.
/// </summary>
public sealed class AVMixer : IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly Guid _mixerId = Guid.NewGuid();
    private readonly AudioConfig _config;
    private readonly ConcurrentDictionary<Guid, IAudioSource> _audioSources = new();
    private readonly ConcurrentDictionary<Guid, IVideoSource> _videoSources = new();
    private readonly Lock _sourceSnapshotLock = new();
    private IAudioSource[] _cachedAudioSources = Array.Empty<IAudioSource>();
    private IVideoSource[] _cachedVideoSources = Array.Empty<IVideoSource>();
    private volatile bool _sourceSnapshotsDirty = true;
    private readonly ConcurrentDictionary<string, SyncGroupState> _syncGroups = new(StringComparer.Ordinal);
    private readonly MasterClock _audioClock;
    private readonly MasterClock _videoClock;
    private readonly IVideoClock _videoClockAdapter;
    private readonly ManualResetEventSlim _pauseEvent = new(initialState: false);
    private readonly Thread _mixThread;
    private readonly Thread _clockThread;
    private readonly Lock _syncLock = new();
    private readonly Lock _effectsLock = new();
    private readonly List<IEffectProcessor> _masterEffects = new();
    private IEffectProcessor[] _cachedEffects = Array.Empty<IEffectProcessor>();
    private volatile bool _effectsChanged;
    private readonly int _bufferSizeInFrames;
    private volatile float _masterVolume = 1.0f;
    private volatile float _leftPeak;
    private volatile float _rightPeak;
    private long _totalMixedFrames;
    private long _totalUnderruns;
    private volatile bool _stopRequested;
    private volatile bool _isRunning;
    private double _transportBaseTimestampSeconds;
    private long _transportBaseTicks;
    private long _submittedFramesToEngine;
    private bool _mixThreadStarted;
    private bool _clockThreadStarted;
    private bool _disposed;
    private const int ClockPublishIntervalMs = 2;

    // Diagnostic value published for callers that display clock correction. It now represents the
    // leash applied to the raw wall-clock video timeline to keep it from leading queued audio too
    // far, rather than a slow accumulator-based drift nudge.
    private double _videoClockDriftAdjustSeconds;
    private const double MinVideoClockLeadAllowanceSeconds = 1.0 / 60.0;
    private const double MaxVideoClockLeadAllowanceSeconds = 0.050;
    private const double VideoClockLeadBufferMultiplier = 2.0;

    /// <summary>Initializes a new AV mixer using the provided engine for audio rendering.</summary>
    public AVMixer(IAudioEngine engine, int bufferSizeInFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _bufferSizeInFrames = bufferSizeInFrames > 0
            ? bufferSizeInFrames
            : Math.Max(1, _engine.FramesPerBuffer);
        _config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = _bufferSizeInFrames
        };
        _audioClock = new MasterClock(sampleRate: 48000, channels: 2, mode: ClockMode.Realtime);
        _videoClock = new MasterClock(sampleRate: 48000, channels: 2, mode: ClockMode.Realtime);
        _videoClockAdapter = new MasterClockVideoClockAdapter(_videoClock);
        ResetTransportState(0, resetSubmittedFrames: true);

        _mixThread = new Thread(MixThreadLoop)
        {
            Name = "AVMixer.MixThread",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _clockThread = new Thread(ClockThreadLoop)
        {
            Name = "AVMixer.ClockThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
    }

    /// <summary>Gets the unique identifier of this mixer instance.</summary>
    public Guid MixerId => _mixerId;

    /// <summary>Gets the active mixer audio configuration.</summary>
    public AudioConfig Config => _config;

    /// <summary>Gets the current clock rendering mode (Realtime or Offline).</summary>
    public ClockMode RenderingMode
    {
        get => _audioClock.Mode;
        set
        {
            _audioClock.Mode = value;
            _videoClock.Mode = value;
        }
    }

    /// <summary>Clock followed by audio sources and driven from submitted engine buffers.</summary>
    public MasterClock AudioClock => _audioClock;

    /// <summary>Clock followed by video sources and driven from transport elapsed time.</summary>
    public MasterClock VideoClock => _videoClock;

    /// <summary>
    /// Current correction applied to the raw wall-clock video timeline (seconds). Negative values
    /// mean the video clock was held back so it would not outrun queued audio too far.
    /// </summary>
    public double VideoClockDriftAdjustSeconds => Volatile.Read(ref _videoClockDriftAdjustSeconds);

    /// <summary>Gets whether the mixer transport is currently running.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Current number of registered audio sources.</summary>
    public int AudioSourceCount => _audioSources.Count;

    /// <summary>Current number of registered audio sources.</summary>
    public int SourceCount => _audioSources.Count;

    /// <summary>Current number of registered video sources.</summary>
    public int VideoSourceCount => _videoSources.Count;

    /// <summary>Master output gain in range [0, 1].</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Last-cycle peak level for output channel 1.</summary>
    public float LeftPeak => _leftPeak;

    /// <summary>Last-cycle peak level for output channel 2.</summary>
    public float RightPeak => _rightPeak;

    /// <summary>Total number of mixed audio frames sent to the engine.</summary>
    public long TotalMixedFrames => Interlocked.Read(ref _totalMixedFrames);

    /// <summary>Total number of detected source underruns/dropouts.</summary>
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    /// <summary>Raised when an audio source reports an error.</summary>
    public event EventHandler<AudioErrorEventArgs>? SourceError;

    /// <summary>Raised when a source does not provide a full buffer in realtime mode.</summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>Raised when a clock-synchronized source misses part of a requested buffer.</summary>
    public event EventHandler<TrackDropoutEventArgs>? TrackDropout;

    /// <summary>Adds an audio source and attaches it to the dedicated audio clock when supported.</summary>
    public bool AddAudioSource(IAudioSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        if (!_audioSources.TryAdd(source.Id, source))
            return false;

        InvalidateSourceSnapshots();
        source.Error += OnAudioSourceError;

        if (source is IMasterClockSource clockSource)
            clockSource.AttachToClock(AudioClock);

        if (_isRunning && source.State != AudioState.Playing)
            source.Play();

        return true;
    }

    /// <summary>Removes an audio source and detaches it from the shared master clock.</summary>
    public bool RemoveAudioSource(IAudioSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return RemoveAudioSource(source.Id);
    }

    /// <summary>Removes an audio source by ID and detaches it from the shared master clock.</summary>
    public bool RemoveAudioSource(Guid sourceId)
    {
        ThrowIfDisposed();

        if (!_audioSources.TryRemove(sourceId, out var source))
            return false;

        InvalidateSourceSnapshots();
        source.Error -= OnAudioSourceError;

        if (source is IMasterClockSource clockSource)
            clockSource.DetachFromClock();

        RemoveSourceFromAllGroups(sourceId);

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

    /// <summary>Adds a video source, attaches it to the dedicated video clock, and aligns to current transport state.</summary>
    public bool AddVideoSource(IVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        if (!_videoSources.TryAdd(source.Id, source))
            return false;

        InvalidateSourceSnapshots();
        source.AttachToClock(_videoClockAdapter);
        if (IsRunning)
            source.Play();
        else
            source.Pause();

        return true;
    }

    /// <summary>Removes a video source and detaches it from the shared clock.</summary>
    public bool RemoveVideoSource(IVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return RemoveVideoSource(source.Id);
    }

    /// <summary>Removes a video source by ID and detaches it from the shared clock.</summary>
    public bool RemoveVideoSource(Guid sourceId)
    {
        ThrowIfDisposed();

        if (!_videoSources.TryRemove(sourceId, out var source))
            return false;

        InvalidateSourceSnapshots();
        source.DetachFromClock();
        RemoveSourceFromAllGroups(sourceId);
        return true;
    }

    /// <summary>Gets snapshots of currently registered audio sources.</summary>
    public IAudioSource[] GetAudioSources()
    {
        EnsureSourceSnapshots();
        return _cachedAudioSources.ToArray();
    }

    /// <summary>AudioMixer-compatible alias of <see cref="AddAudioSource"/>.</summary>
    public bool AddSource(IAudioSource source) => AddAudioSource(source);

    /// <summary>AudioMixer-compatible alias of <see cref="RemoveAudioSource(IAudioSource)"/>.</summary>
    public bool RemoveSource(IAudioSource source) => RemoveAudioSource(source);

    /// <summary>AudioMixer-compatible alias of <see cref="RemoveAudioSource(Guid)"/>.</summary>
    public bool RemoveSource(Guid sourceId) => RemoveAudioSource(sourceId);

    /// <summary>AudioMixer-compatible alias of <see cref="GetAudioSources"/>.</summary>
    public IAudioSource[] GetSources() => GetAudioSources();

    /// <summary>Removes all registered audio sources.</summary>
    public void ClearSources()
    {
        ThrowIfDisposed();

        foreach (var source in _audioSources.Values.ToArray())
            RemoveAudioSource(source.Id);

        InvalidateSourceSnapshots();
    }

    /// <summary>Sets an audio source volume by source ID.</summary>
    public bool SetSourceVolume(Guid sourceId, float volume)
    {
        ThrowIfDisposed();
        if (!_audioSources.TryGetValue(sourceId, out var source))
            return false;

        source.Volume = Math.Clamp(volume, 0f, 1f);
        return true;
    }

    /// <summary>Sets an audio source volume by instance.</summary>
    public bool SetSourceVolume(IAudioSource source, float volume)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return SetSourceVolume(source.Id, volume);
    }

    /// <summary>Gets an audio source volume by source ID.</summary>
    public bool TryGetSourceVolume(Guid sourceId, out float volume)
    {
        ThrowIfDisposed();
        if (_audioSources.TryGetValue(sourceId, out var source))
        {
            volume = source.Volume;
            return true;
        }

        volume = 0f;
        return false;
    }

    /// <summary>Adds a master effect to the processing chain.</summary>
    public void AddMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(effect);

        lock (_effectsLock)
        {
            effect.Initialize(_config);
            _masterEffects.Add(effect);
            _effectsChanged = true;
        }
    }

    /// <summary>Removes a master effect from the processing chain.</summary>
    public bool RemoveMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(effect);

        lock (_effectsLock)
        {
            var removed = _masterEffects.Remove(effect);
            if (removed)
                _effectsChanged = true;
            return removed;
        }
    }

    /// <summary>Clears all master effects.</summary>
    public void ClearMasterEffects()
    {
        ThrowIfDisposed();
        lock (_effectsLock)
        {
            _masterEffects.Clear();
            _effectsChanged = true;
        }
    }

    /// <summary>Gets a snapshot of all master effects.</summary>
    public IEffectProcessor[] GetMasterEffects()
    {
        ThrowIfDisposed();
        lock (_effectsLock)
            return _masterEffects.ToArray();
    }

    /// <summary>Gets snapshots of currently registered video sources.</summary>
    public IVideoSource[] GetVideoSources()
    {
        EnsureSourceSnapshots();
        return _cachedVideoSources.ToArray();
    }

    /// <summary>Starts or resumes transport for audio and video sources.</summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (_isRunning)
            return;

        ResetClockDriverState();
        _isRunning = true;
        _pauseEvent.Set();

        if (!_mixThreadStarted)
        {
            _mixThreadStarted = true;
            _mixThread.Start();
        }

        if (!_clockThreadStarted)
        {
            _clockThreadStarted = true;
            _clockThread.Start();
        }

        PublishAudioClockTimestamp();
        PublishVideoClockTimestamp();

        var videoSources = GetVideoSourceSnapshot();
        foreach (var videoSource in videoSources)
            videoSource.Play();
    }

    /// <summary>Pauses transport for audio and video sources.</summary>
    public void Pause()
    {
        ThrowIfDisposed();
        if (!_isRunning)
            return;

        var pausedTimestamp = ResolveVideoClockTimestamp();
        _isRunning = false;
        _pauseEvent.Reset();
        ResetTransportState(pausedTimestamp, resetSubmittedFrames: true);
        PublishAudioClockTimestamp(pausedTimestamp);
        PublishVideoClockTimestamp(pausedTimestamp);

        var videoSources = GetVideoSourceSnapshot();
        foreach (var videoSource in videoSources)
            videoSource.Pause();
    }

    /// <summary>Stops transport and rewinds all tracked sources to timeline start.</summary>
    public void Stop()
    {
        ThrowIfDisposed();
        _isRunning = false;
        _pauseEvent.Reset();
        ResetTransportState(0, resetSubmittedFrames: true);
        PublishAudioClockTimestamp(0);
        PublishVideoClockTimestamp(0);

        var audioSources = GetAudioSourceSnapshot();
        foreach (var source in audioSources)
        {
            try
            {
                source.Stop();
            }
            catch
            {
                // Best effort stop.
            }
        }

        _leftPeak = 0f;
        _rightPeak = 0f;

        var videoSources = GetVideoSourceSnapshot();
        foreach (var videoSource in videoSources)
            videoSource.Stop();
    }

    /// <summary>
    /// Seeks the shared timeline and updates registered video sources to their corresponding
    /// track-local positions.
    /// </summary>
    public void Seek(double positionInSeconds)
    {
        Seek(positionInSeconds, safeSeek: false);
    }

    /// <summary>
    /// Seeks the shared timeline and updates registered audio/video sources to their corresponding
    /// track-local positions. When <paramref name="safeSeek"/> is enabled, transport is paused
    /// before the seek and resumed afterwards if it had been running.
    /// </summary>
    public void Seek(double positionInSeconds, bool safeSeek)
    {
        ThrowIfDisposed();

        var target = ClampTimelinePosition(positionInSeconds);
        var wasRunning = safeSeek && IsRunning;
        if (wasRunning)
            Pause();

        lock (_syncLock)
        {
            ResetTransportState(target, resetSubmittedFrames: true);
            PublishAudioClockTimestamp(target);
            PublishVideoClockTimestamp(target);

            var audioSources = GetAudioSourceSnapshot();
            foreach (var source in audioSources)
            {
                var trackPosition = ResolveAudioTrackPosition(source, target);
                source.Seek(trackPosition);

                if (IsRunning && source.State != AudioState.Playing)
                    source.Play();
            }

            var videoSources = GetVideoSourceSnapshot();
            foreach (var source in videoSources)
                SeekVideoToTimeline(source, target);
        }

        if (wasRunning)
            Start();
    }

    /// <summary>
    /// Creates a synchronization group that can include both audio and video sources.
    /// </summary>
    public void CreateSyncGroup(string groupId, IEnumerable<IAudioSource>? audioSources = null, IEnumerable<IVideoSource>? videoSources = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentNullException(nameof(groupId));

        var audioArray = (audioSources ?? Enumerable.Empty<IAudioSource>()).ToArray();
        if (audioArray.Length > 0)
        {
            var group = _syncGroups.GetOrAdd(groupId, static _ => new SyncGroupState());
            foreach (var source in audioArray)
            {
                if (!_audioSources.ContainsKey(source.Id))
                    AddAudioSource(source);

                group.AudioSourceIds.Add(source.Id);
            }
        }

        CreateVideoSyncGroup(groupId, videoSources);
    }

    /// <summary>AudioMixer-compatible overload for creating an audio sync group.</summary>
    public void CreateSyncGroup(string groupId, params IAudioSource[] sources)
    {
        CreateSyncGroup(groupId, sources, Array.Empty<IVideoSource>());
    }

    /// <summary>
    /// Creates or updates a video-only synchronization group.
    /// This can be used without creating an audio sync group.
    /// </summary>
    public void CreateVideoSyncGroup(string groupId, IEnumerable<IVideoSource>? videoSources)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentNullException(nameof(groupId));

        var videoArray = (videoSources ?? Enumerable.Empty<IVideoSource>()).ToArray();
        if (videoArray.Length == 0)
        {
            _syncGroups.GetOrAdd(groupId, static _ => new SyncGroupState());
            return;
        }

        foreach (var source in videoArray)
            AddVideoSourceToSyncGroup(groupId, source);
    }

    /// <summary>Removes a sync group from audio and video registries.</summary>
    public void RemoveSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        RemoveVideoSyncGroup(groupId);
    }

    /// <summary>Gets all audio sources registered in a sync group.</summary>
    public IReadOnlyList<IAudioSource> GetSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return Array.Empty<IAudioSource>();

        return GetAudioSourcesInGroup(groupId);
    }

    /// <summary>Gets all sync-group IDs.</summary>
    public IReadOnlyCollection<string> GetSyncGroupIds()
    {
        ThrowIfDisposed();
        return _syncGroups.Keys.ToArray();
    }


    /// <summary>
    /// Checks all sync groups for drift and asks synchronizable members to resync when drift
    /// exceeds <paramref name="toleranceInFrames"/>.
    /// </summary>
    public void CheckAndResyncAllGroups(int toleranceInFrames = 10)
    {
        ThrowIfDisposed();
        if (toleranceInFrames < 0)
            toleranceInFrames = 0;

        var masterSamplePosition = _audioClock.CurrentSamplePosition;
        var groupIds = _syncGroups.Keys.ToArray();
        foreach (var groupId in groupIds)
            CheckAndResyncGroup(groupId, masterSamplePosition, toleranceInFrames);
    }

    /// <summary>
    /// Sets a sync-group tempo for audio members and stores it as group metadata.
    /// Video members currently remain clock-driven at normal speed.
    /// </summary>
    public bool SetSyncGroupTempo(string groupId, float tempo)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        if (!_syncGroups.TryGetValue(groupId, out var group))
            return false;

        var clampedTempo = Math.Clamp(tempo, 0.25f, 4.0f);
        group.Tempo = clampedTempo;

        foreach (var source in GetAudioSourcesInGroup(groupId))
            source.Tempo = clampedTempo;

        return true;
    }

    /// <summary>Gets the currently stored tempo for a sync group.</summary>
    public float GetSyncGroupTempo(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return 1f;

        return _syncGroups.TryGetValue(groupId, out var group) ? group.Tempo : 1f;
    }

    /// <summary>Adds an audio source to an existing sync group.</summary>
    public bool AddSourceToSyncGroup(string groupId, IAudioSource source)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        ArgumentNullException.ThrowIfNull(source);
        if (!_audioSources.ContainsKey(source.Id))
            AddAudioSource(source);

        lock (_syncLock)
        {
            var group = _syncGroups.GetOrAdd(groupId, static _ => new SyncGroupState());
            var added = group.AudioSourceIds.Add(source.Id);

            if (source is ISynchronizable synchronizable)
            {
                synchronizable.SyncGroupId = groupId;
                synchronizable.IsSynchronized = true;
            }

            return added;
        }
    }

    /// <summary>Removes an audio source from a sync group.</summary>
    public bool RemoveSourceFromSyncGroup(string groupId, IAudioSource source)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        ArgumentNullException.ThrowIfNull(source);
        return RemoveSourceFromSyncGroup(groupId, source.Id);
    }

    /// <summary>Removes an audio source by ID from a sync group.</summary>
    public bool RemoveSourceFromSyncGroup(string groupId, Guid sourceId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        if (!_syncGroups.TryGetValue(groupId, out var group))
            return false;

        var removed = group.AudioSourceIds.Remove(sourceId);
        if (removed && _audioSources.TryGetValue(sourceId, out var source) && source is ISynchronizable synchronizable && string.Equals(synchronizable.SyncGroupId, groupId, StringComparison.Ordinal))
        {
            synchronizable.SyncGroupId = null;
            synchronizable.IsSynchronized = false;
        }

        return removed;
    }

    /// <summary>Gets the longest duration among audio members of a sync group.</summary>
    public double GetSyncGroupDuration(string groupId)
    {
        var sources = GetAudioSourcesInGroup(groupId);
        if (sources.Length == 0)
            return 0;

        var maxDuration = 0.0;
        for (var i = 0; i < sources.Length; i++)
        {
            var duration = sources[i].Duration;
            if (duration > maxDuration)
                maxDuration = duration;
        }

        return maxDuration;
    }

    /// <summary>Gets group position using the longest source as reference.</summary>
    public double GetSyncGroupPosition(string groupId)
    {
        var sources = GetAudioSourcesInGroup(groupId);
        if (sources.Length == 0)
            return 0;

        var longestDuration = double.MinValue;
        var longestPosition = 0.0;
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            if (source.Duration <= longestDuration)
                continue;

            longestDuration = source.Duration;
            longestPosition = source.Position;
        }

        return longestPosition;
    }

    /// <summary>Removes a video-only sync group registration.</summary>
    public void RemoveVideoSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        if (!_syncGroups.TryRemove(groupId, out var group))
            return;

        foreach (var sourceId in group.VideoSourceIds)
        {
            if (_videoSources.TryGetValue(sourceId, out var source) && string.Equals(source.SyncGroupId, groupId, StringComparison.Ordinal))
            {
                source.SyncGroupId = null;
                source.IsSynchronized = false;
            }
        }
    }

    /// <summary>Adds a video source to a video sync group (group is created when missing).</summary>
    public bool AddVideoSourceToSyncGroup(string groupId, IVideoSource source)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        ArgumentNullException.ThrowIfNull(source);
        if (!_videoSources.ContainsKey(source.Id))
            AddVideoSource(source);

        lock (_syncLock)
        {
            var group = _syncGroups.GetOrAdd(groupId, static _ => new SyncGroupState());
            var added = group.VideoSourceIds.Add(source.Id);
            source.SyncGroupId = groupId;
            source.IsSynchronized = true;
            return added;
        }
    }

    /// <summary>Removes a video source from a video sync group.</summary>
    public bool RemoveVideoSourceFromSyncGroup(string groupId, IVideoSource source)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        return RemoveVideoSourceFromSyncGroup(groupId, source.Id);
    }

    /// <summary>Removes a video source by ID from a video sync group.</summary>
    public bool RemoveVideoSourceFromSyncGroup(string groupId, Guid sourceId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        if (!_syncGroups.TryGetValue(groupId, out var group))
            return false;

        var removed = group.VideoSourceIds.Remove(sourceId);
        if (removed && _videoSources.TryGetValue(sourceId, out var source) && string.Equals(source.SyncGroupId, groupId, StringComparison.Ordinal))
        {
            source.SyncGroupId = null;
            source.IsSynchronized = false;
        }

        return removed;
    }

    /// <summary>Gets all sources in a video-only sync group.</summary>
    public IReadOnlyList<IVideoSource> GetVideoSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return Array.Empty<IVideoSource>();

        return GetVideoSourcesInGroup(groupId);
    }

    /// <summary>Gets all registered video sync-group IDs.</summary>
    public IReadOnlyCollection<string> GetVideoSyncGroupIds()
    {
        ThrowIfDisposed();
        return _syncGroups.Keys.ToArray();
    }

    /// <summary>Starts all sources in a sync group.</summary>
    public void StartSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        StartAudioSyncGroup(groupId);
        StartVideoSyncGroup(groupId);
    }

    /// <summary>Pauses all sources in a sync group.</summary>
    public void PauseSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        PauseAudioSyncGroup(groupId);
        PauseVideoSyncGroup(groupId);
    }

    /// <summary>Resumes all sources in a sync group.</summary>
    public void ResumeSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        ResumeAudioSyncGroup(groupId);
        ResumeVideoSyncGroup(groupId);
    }

    /// <summary>Stops all sources in a sync group and rewinds their local positions.</summary>
    public void StopSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        StopAudioSyncGroup(groupId);
        StopVideoSyncGroup(groupId);
    }

    /// <summary>Seeks all sources in a sync group to a timeline position.</summary>
    public void SeekSyncGroup(string groupId, double positionInSeconds)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        var target = ClampTimelinePosition(positionInSeconds);
        SeekAudioSyncGroup(groupId, target);
        SeekVideoSyncGroup(groupId, target);
    }

    /// <summary>Starts all members of a video-only sync group.</summary>
    public void StartVideoSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        foreach (var source in GetVideoSourcesInGroup(groupId))
            source.Play();
    }

    /// <summary>Pauses all members of a video-only sync group.</summary>
    public void PauseVideoSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        foreach (var source in GetVideoSourcesInGroup(groupId))
            source.Pause();
    }

    /// <summary>Resumes all members of a video-only sync group.</summary>
    public void ResumeVideoSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        foreach (var source in GetVideoSourcesInGroup(groupId))
            source.Play();
    }

    /// <summary>Stops and rewinds all members of a video-only sync group.</summary>
    public void StopVideoSyncGroup(string groupId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        foreach (var source in GetVideoSourcesInGroup(groupId))
            source.Stop();
    }

    /// <summary>Seeks all members of a video-only sync group on the shared timeline.</summary>
    public void SeekVideoSyncGroup(string groupId, double positionInSeconds)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        var target = ClampTimelinePosition(positionInSeconds);
        foreach (var source in GetVideoSourcesInGroup(groupId))
            SeekVideoToTimeline(source, target);
    }

    /// <summary>Disposes the AV mixer and detaches all tracked sources.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _stopRequested = true;
        _isRunning = false;
        _pauseEvent.Set();

        if (_mixThreadStarted && _mixThread.IsAlive)
        {
            try
            {
                _mixThread.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best effort thread shutdown.
            }
        }

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

        var videoSources = GetVideoSourceSnapshot();
        foreach (var source in videoSources)
        {
            try
            {
                source.DetachFromClock();
            }
            catch
            {
                // Best effort during disposal.
            }
        }

        var audioSources = GetAudioSourceSnapshot();
        foreach (var source in audioSources)
        {
            if (source is not IMasterClockSource clockSource)
                continue;

            try
            {
                clockSource.DetachFromClock();
            }
            catch
            {
                // Best effort during disposal.
            }
        }

        _syncGroups.Clear();
        _videoSources.Clear();
        _audioSources.Clear();
        InvalidateSourceSnapshots();

        lock (_effectsLock)
        {
            foreach (var effect in _masterEffects)
            {
                try
                {
                    effect.Dispose();
                }
                catch
                {
                    // Best effort during disposal.
                }
            }

            _masterEffects.Clear();
            _cachedEffects = Array.Empty<IEffectProcessor>();
            _effectsChanged = false;
        }

        _pauseEvent.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AVMixer));
    }

    private IVideoSource[] GetVideoSourcesInGroup(string groupId)
    {
        if (!_syncGroups.TryGetValue(groupId, out var group))
            return Array.Empty<IVideoSource>();

        var list = new List<IVideoSource>(group.VideoSourceIds.Count);
        foreach (var sourceId in group.VideoSourceIds)
        {
            if (_videoSources.TryGetValue(sourceId, out var source))
                list.Add(source);
        }

        return list.ToArray();
    }

    private static void SeekVideoToTimeline(IVideoSource source, double timelineSeconds)
    {
        var trackPosition = timelineSeconds - source.StartOffset;
        if (trackPosition <= 0)
        {
            source.Seek(0);
            return;
        }

        source.Seek(trackPosition);
    }

    private static double ResolveAudioTrackPosition(IAudioSource source, double timelineSeconds)
    {
        if (source is IMasterClockSource masterClockSource)
        {
            var trackPosition = timelineSeconds - masterClockSource.StartOffset;
            return Math.Max(0, trackPosition);
        }

        return Math.Max(0, timelineSeconds);
    }

    private static double ClampTimelinePosition(double positionInSeconds)
    {
        if (double.IsNaN(positionInSeconds) || double.IsInfinity(positionInSeconds))
            return 0;

        return Math.Max(0, positionInSeconds);
    }

    private void RemoveSourceFromAllGroups(Guid sourceId)
    {
        lock (_syncLock)
        {
            foreach (var group in _syncGroups.Values)
            {
                group.AudioSourceIds.Remove(sourceId);
                group.VideoSourceIds.Remove(sourceId);
            }
        }
    }

    private void InvalidateSourceSnapshots()
    {
        _sourceSnapshotsDirty = true;
    }

    private void EnsureSourceSnapshots()
    {
        if (!_sourceSnapshotsDirty)
            return;

        lock (_sourceSnapshotLock)
        {
            if (!_sourceSnapshotsDirty)
                return;

            _cachedAudioSources = _audioSources.Values.ToArray();
            _cachedVideoSources = _videoSources.Values.ToArray();
            _sourceSnapshotsDirty = false;
        }
    }

    private IAudioSource[] GetAudioSourceSnapshot()
    {
        EnsureSourceSnapshots();
        return _cachedAudioSources;
    }

    private IVideoSource[] GetVideoSourceSnapshot()
    {
        EnsureSourceSnapshots();
        return _cachedVideoSources;
    }

    private void StartAudioSyncGroup(string groupId)
    {
        foreach (var source in GetAudioSourcesInGroup(groupId))
            source.Play();
    }

    private void PauseAudioSyncGroup(string groupId)
    {
        foreach (var source in GetAudioSourcesInGroup(groupId))
            source.Pause();
    }

    private void ResumeAudioSyncGroup(string groupId)
    {
        foreach (var source in GetAudioSourcesInGroup(groupId))
            source.Play();
    }

    private void StopAudioSyncGroup(string groupId)
    {
        foreach (var source in GetAudioSourcesInGroup(groupId))
            source.Stop();
    }

    private void SeekAudioSyncGroup(string groupId, double positionInSeconds)
    {
        foreach (var source in GetAudioSourcesInGroup(groupId))
        {
            source.Seek(ResolveAudioTrackPosition(source, positionInSeconds));
        }
    }

    private IAudioSource[] GetAudioSourcesInGroup(string groupId)
    {
        if (!_syncGroups.TryGetValue(groupId, out var group))
            return Array.Empty<IAudioSource>();

        var list = new List<IAudioSource>(group.AudioSourceIds.Count);
        foreach (var sourceId in group.AudioSourceIds)
        {
            if (_audioSources.TryGetValue(sourceId, out var source))
                list.Add(source);
        }

        return list.ToArray();
    }

    private void MixThreadLoop()
    {
        var outputChannels = 2;
        var sampleCount = _bufferSizeInFrames * outputChannels;
        var mixBuffer = new float[sampleCount];
        var sourceBuffer = new float[sampleCount];

        while (!_stopRequested)
        {
            if (!_isRunning)
            {
                _pauseEvent.Wait(100);
                continue;
            }

            Array.Clear(mixBuffer, 0, sampleCount);
            var hasActiveSources = false;
            var masterTimestamp = ResolveMasterTimestampForMixCycle();

            var audioSources = GetAudioSourceSnapshot();
            foreach (var source in audioSources)
            {
                if (source.State != AudioState.Playing)
                    continue;

                try
                {
                    var framesRead = ReadSourceFrames(source, sourceBuffer, masterTimestamp);
                    if (framesRead <= 0)
                        continue;

                    MixSourceIntoOutput(source, sourceBuffer, mixBuffer, framesRead, outputChannels);
                    hasActiveSources = true;
                }
                catch
                {
                    // Skip faulty source for this cycle and keep output flowing.
                }
            }

            if (hasActiveSources)
            {
                ApplyMasterVolume(mixBuffer, sampleCount);
                ApplyMasterEffects(mixBuffer, sampleCount);
                CalculatePeakLevels(mixBuffer, sampleCount);
            }
            else
            {
                _leftPeak = 0f;
                _rightPeak = 0f;
            }

            try
            {
                _engine.Send(mixBuffer.AsSpan(0, sampleCount));
                Interlocked.Add(ref _totalMixedFrames, _bufferSizeInFrames);
                Interlocked.Add(ref _submittedFramesToEngine, _bufferSizeInFrames);
                PublishAudioClockTimestamp();
            }
            catch
            {
                Thread.Sleep(5);
            }

            AdvanceMasterClockAfterMixCycle(masterTimestamp);

            if (!hasActiveSources)
                Thread.Sleep(1);
        }
    }

    private void ClockThreadLoop()
    {
        while (!_stopRequested)
        {
            if (!_isRunning)
            {
                _pauseEvent.Wait(100);
                continue;
            }

            ApplyVideoClockDriftCorrection();
            PublishVideoClockTimestamp();
            PublishAudioClockTimestamp();
            Thread.Sleep(ClockPublishIntervalMs);
        }
    }

    private void ApplyVideoClockDriftCorrection()
    {
        // The correction is derived directly inside ResolveVideoClockTimestamp so it can react to
        // the latest wall-clock and submitted-audio positions atomically.
    }

    private void ApplyMasterEffects(float[] mixBuffer, int sampleCount)
    {
        if (_effectsChanged)
        {
            lock (_effectsLock)
            {
                if (_effectsChanged)
                {
                    _cachedEffects = _masterEffects.ToArray();
                    _effectsChanged = false;
                }
            }
        }

        var effects = _cachedEffects;

        if (effects.Length == 0)
            return;

        var span = mixBuffer.AsSpan(0, sampleCount);
        foreach (var effect in effects)
        {
            try
            {
                if (effect.Enabled)
                    effect.Process(span, _bufferSizeInFrames);
            }
            catch (Exception ex)
            {
                OnSourceError(this, new AudioErrorEventArgs($"Master effect {effect.GetType().Name} failed: {ex.Message}", ex));
            }
        }
    }

    private void ApplyMasterVolume(float[] mixBuffer, int sampleCount)
    {
        var volume = _masterVolume;
        if (Math.Abs(volume - 1f) < 0.0001f)
            return;

        for (var i = 0; i < sampleCount; i++)
            mixBuffer[i] *= volume;
    }

    private void CalculatePeakLevels(float[] mixBuffer, int sampleCount)
    {
        float left = 0f;
        float right = 0f;

        for (var i = 0; i + 1 < sampleCount; i += 2)
        {
            var l = Math.Abs(mixBuffer[i]);
            var r = Math.Abs(mixBuffer[i + 1]);
            if (l > left)
                left = l;
            if (r > right)
                right = r;
        }

        _leftPeak = left;
        _rightPeak = right;
    }

    private int ReadSourceFrames(IAudioSource source, float[] sourceBuffer, double masterTimestamp)
    {
        if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
        {
            clockSource.ReadSamplesAtTime(masterTimestamp, sourceBuffer.AsSpan(), _bufferSizeInFrames, out var result);

            if (result.FramesRead < _bufferSizeInFrames || !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                var missedFrames = Math.Max(0, _bufferSizeInFrames - result.FramesRead);
                var currentSamplePosition = _audioClock.CurrentSamplePosition;
                if (BufferUnderrun != null)
                    OnBufferUnderrun(new BufferUnderrunEventArgs(missedFrames, currentSamplePosition));

                Interlocked.Increment(ref _totalUnderruns);

                if (TrackDropout != null)
                {
                    OnTrackDropout(new TrackDropoutEventArgs(
                        source.Id,
                        source.GetType().Name,
                        masterTimestamp,
                        currentSamplePosition,
                        missedFrames,
                        result.ErrorMessage ?? "Buffer underrun"));
                }
            }

            return result.FramesRead;
        }

        return source.ReadSamples(sourceBuffer.AsSpan(), _bufferSizeInFrames);
    }

    private void OnAudioSourceError(object? sender, AudioErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    private void OnSourceError(object? sender, AudioErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    private void OnTrackDropout(TrackDropoutEventArgs e)
    {
        TrackDropout?.Invoke(this, e);
    }

    private void OnBufferUnderrun(BufferUnderrunEventArgs e)
    {
        BufferUnderrun?.Invoke(this, e);
    }


    private void CheckAndResyncGroup(string groupId, long masterSamplePosition, int toleranceInFrames)
    {
        if (!_syncGroups.TryGetValue(groupId, out var group))
            return;

        foreach (var sourceId in group.AudioSourceIds)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                continue;

            if (source is not ISynchronizable syncSource)
                continue;

            var drift = Math.Abs(syncSource.SamplePosition - masterSamplePosition);
            if (drift > toleranceInFrames)
                syncSource.ResyncTo(masterSamplePosition);
        }

        foreach (var sourceId in group.VideoSourceIds)
        {
            if (!_videoSources.TryGetValue(sourceId, out var source))
                continue;

            var drift = Math.Abs(source.SamplePosition - masterSamplePosition);
            if (drift > toleranceInFrames)
                source.ResyncTo(masterSamplePosition);
        }
    }

    private static void MixSourceIntoOutput(IAudioSource source, float[] sourceBuffer, float[] mixBuffer, int framesRead, int outputChannels)
    {
        var sourceChannels = Math.Max(1, source.Config.Channels);

        if (sourceChannels == outputChannels)
        {
            var sampleCount = framesRead * outputChannels;
            var i = 0;

            if (Vector.IsHardwareAccelerated)
            {
                var width = Vector<float>.Count;
                var simdEnd = sampleCount - (sampleCount % width);
                for (; i < simdEnd; i += width)
                {
                    var mixed = new Vector<float>(mixBuffer, i) + new Vector<float>(sourceBuffer, i);
                    mixed.CopyTo(mixBuffer, i);
                }
            }

            for (; i < sampleCount; i++)
                mixBuffer[i] += sourceBuffer[i];
            return;
        }

        if (sourceChannels == 1 && outputChannels == 2)
        {
            for (var frame = 0; frame < framesRead; frame++)
            {
                var mono = sourceBuffer[frame];
                var outIndex = frame * 2;
                mixBuffer[outIndex] += mono;
                mixBuffer[outIndex + 1] += mono;
            }

            return;
        }

        var mappedChannels = Math.Min(sourceChannels, outputChannels);
        for (var frame = 0; frame < framesRead; frame++)
        {
            var sourceBase = frame * sourceChannels;
            var outputBase = frame * outputChannels;
            for (var channel = 0; channel < mappedChannels; channel++)
                mixBuffer[outputBase + channel] += sourceBuffer[sourceBase + channel];
        }
    }

    private double ResolveMasterTimestampForMixCycle()
    {
        return ResolveAudioClockTimestamp();
    }

    private void AdvanceMasterClockAfterMixCycle(double _)
    {
        PublishAudioClockTimestamp();
        PublishVideoClockTimestamp();
    }

    private void ResetClockDriverState()
    {
        ResetTransportState(ResolveVideoClockTimestamp(), resetSubmittedFrames: false);
        PublishAudioClockTimestamp();
        PublishVideoClockTimestamp();
    }

    private double ReadAndUpdateElapsedClockSeconds()
    {
        var baseTicks = Volatile.Read(ref _transportBaseTicks);
        if (baseTicks <= 0)
            return 0;

        var deltaTicks = Stopwatch.GetTimestamp() - baseTicks;
        return deltaTicks <= 0 ? 0 : deltaTicks / (double)Stopwatch.Frequency;
    }

    private void ResetTransportState(double playbackTimestampSeconds, bool resetSubmittedFrames)
    {
        if (resetSubmittedFrames)
            Interlocked.Exchange(ref _submittedFramesToEngine, 0);

        // Always reset the video-clock drift correction so the adjustment from a previous
        // playback segment does not carry over into the new timeline position.
        Volatile.Write(ref _videoClockDriftAdjustSeconds, 0.0);

        Volatile.Write(ref _transportBaseTimestampSeconds, Math.Max(0, playbackTimestampSeconds));
        Volatile.Write(ref _transportBaseTicks, Stopwatch.GetTimestamp());
    }

    private double ResolveAudioClockTimestamp()
    {
        var baseTimestamp = Volatile.Read(ref _transportBaseTimestampSeconds);
        if (!_isRunning)
            return Math.Max(0, baseTimestamp);

        var elapsedSeconds = ReadAndUpdateElapsedClockSeconds();
        var submittedFrames = Interlocked.Read(ref _submittedFramesToEngine);
        var submittedSeconds = Math.Max(0, submittedFrames / (double)_audioClock.SampleRate);

        // Audio is played continuously by the device between mix submissions. Use wall time as the
        // playback estimator, but never allow the clock to advance beyond the audio that has actually
        // been queued to the engine. This removes buffer-quantized clock steps while still preventing
        // the transport from free-running past an underrun.
        var playbackProgress = Math.Min(Math.Max(0, elapsedSeconds), submittedSeconds);
        return Math.Max(0, baseTimestamp + playbackProgress);
    }

    private double ResolveVideoClockTimestamp()
    {
        var baseTimestamp = Volatile.Read(ref _transportBaseTimestampSeconds);
        if (!_isRunning)
            return Math.Max(0, baseTimestamp);

        var elapsedSeconds = ReadAndUpdateElapsedClockSeconds();
        var rawVideoTimestamp = Math.Max(0, baseTimestamp + elapsedSeconds);
        var audioTimestamp = ResolveAudioClockTimestamp();
        var maxLeadSeconds = ResolveVideoClockLeadAllowanceSeconds();
        var leashedVideoTimestamp = Math.Min(rawVideoTimestamp, audioTimestamp + maxLeadSeconds);
        Volatile.Write(ref _videoClockDriftAdjustSeconds, leashedVideoTimestamp - rawVideoTimestamp);
        return leashedVideoTimestamp;
    }

    private double ResolveVideoClockLeadAllowanceSeconds()
    {
        var bufferSeconds = _bufferSizeInFrames / (double)_videoClock.SampleRate;
        var allowance = Math.Max(MinVideoClockLeadAllowanceSeconds, bufferSeconds * VideoClockLeadBufferMultiplier);
        return Math.Min(allowance, MaxVideoClockLeadAllowanceSeconds);
    }

    private void PublishAudioClockTimestamp()
    {
        PublishAudioClockTimestamp(ResolveAudioClockTimestamp());
    }

    private void PublishVideoClockTimestamp()
    {
        PublishVideoClockTimestamp(ResolveVideoClockTimestamp());
    }

    private void PublishAudioClockTimestamp(double timestampSeconds)
    {
        _audioClock.SeekTo(Math.Max(0, timestampSeconds));
    }

    private void PublishVideoClockTimestamp(double timestampSeconds)
    {
        _videoClock.SeekTo(Math.Max(0, timestampSeconds));
    }

    private sealed class SyncGroupState
    {
        public HashSet<Guid> AudioSourceIds { get; } = new();
        public HashSet<Guid> VideoSourceIds { get; } = new();
        public float Tempo { get; set; } = 1f;
    }
}