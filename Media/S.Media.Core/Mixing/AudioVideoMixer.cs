using System.Collections.ObjectModel;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public sealed class AudioVideoMixer : IAudioVideoMixer
{
    private readonly Lock _gate = new();
    private readonly List<IAudioSource> _audioSources = [];
    private readonly List<IVideoSource> _videoSources = [];
    private readonly List<IAudioOutput> _audioOutputs = [];
    private readonly List<IVideoOutput> _videoOutputs = [];
    private readonly IMediaClock _clock;
    private MixerSourceDetachOptions _audioDetachOptions = new();
    private MixerSourceDetachOptions _videoDetachOptions = new();
    private IVideoSource? _activeVideoSource;
    private AudioVideoMixerRuntime? _runtime;

    public AudioVideoMixer(IMediaClock? clock = null, ClockType clockType = ClockType.Hybrid)
    {
        _clock = clock ?? new CoreMediaClock();

        if (MixerClockTypeRules.Validate(MixerKind.AudioVideo, clockType) != MediaResult.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(clockType));
        }

        if (clockType == ClockType.External && _clock is CoreMediaClock)
        {
            throw new ArgumentException("ClockType.External requires a non-CoreMediaClock implementation.", nameof(clockType));
        }

        ClockType = clockType;
        SyncMode = AudioVideoSyncMode.Hybrid;
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

            State = AudioVideoMixerState.Running;
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

            State = AudioVideoMixerState.Paused;
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

            State = AudioVideoMixerState.Running;
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

            State = AudioVideoMixerState.Stopped;
            return MediaResult.Success;
        }
    }

    public int Seek(double positionSeconds)
    {
        return _clock.Seek(positionSeconds);
    }

    public int AddAudioSource(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_audioSources.Any(s => s.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MixerSourceIdCollision;
            }

            _audioSources.Add(source);
            return MediaResult.Success;
        }
    }

    public int RemoveAudioSource(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            _audioSources.RemoveAll(s => s.SourceId == source.SourceId);
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
        var validation = MixerClockTypeRules.Validate(MixerKind.AudioVideo, clockType);
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

    internal VideoPresenterSyncDecision SelectVideoFrame(
        Queue<VideoFrame> queuedVideoFrames,
        double clockSeconds,
        in VideoPresenterSyncPolicyOptions options)
    {
        lock (_gate)
        {
            return VideoPresenterSyncPolicy.SelectNextFrame(queuedVideoFrames, SyncMode, clockSeconds, options);
        }
    }

    /// <summary>
    /// Starts the managed A/V playback runtime using the given configuration.
    /// The runtime orchestrates audio/video pump threads, drift correction and presentation
    /// using the first audio source, video source, audio output and video output.
    /// </summary>
    public int StartPlayback(AudioVideoMixerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            if (_runtime is not null)
            {
                return MediaResult.Success;
            }

            var audioSource = _audioSources.FirstOrDefault();
            var videoSource = _videoSources.FirstOrDefault();
            var audioOutput = _audioOutputs.FirstOrDefault();
            var videoOutput = _videoOutputs.FirstOrDefault();

            if (audioSource is null || videoSource is null || audioOutput is null || videoOutput is null)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            _runtime = new AudioVideoMixerRuntime(this, audioSource, videoSource, audioOutput, videoOutput, config.ToRuntimeOptions());
        }

        var startResult = Start();
        if (startResult != MediaResult.Success)
        {
            lock (_gate)
            {
                _runtime?.Dispose();
                _runtime = null;
            }

            return startResult;
        }

        var runtimeStart = _runtime!.Start();
        if (runtimeStart != MediaResult.Success)
        {
            Stop();
            lock (_gate)
            {
                _runtime?.Dispose();
                _runtime = null;
            }

            return runtimeStart;
        }

        return MediaResult.Success;
    }

    /// <summary>
    /// Stops the managed A/V playback runtime and the mixer.
    /// </summary>
    public int StopPlayback()
    {
        AudioVideoMixerRuntime? runtime;

        lock (_gate)
        {
            runtime = _runtime;
            _runtime = null;
        }

        runtime?.Stop();
        runtime?.Dispose();
        return Stop();
    }

    /// <summary>
    /// Ticks the video presentation step when <see cref="AudioVideoMixerConfig.PresentOnCallerThread"/> is true.
    /// Returns the suggested delay before the next tick.
    /// </summary>
    public TimeSpan TickVideoPresentation()
    {
        AudioVideoMixerRuntime? runtime;

        lock (_gate)
        {
            runtime = _runtime;
        }

        return runtime?.TickVideoPresentation() ?? TimeSpan.Zero;
    }

    /// <summary>
    /// Returns a diagnostic snapshot of the playback runtime, or null if the runtime is not active.
    /// </summary>
    public AudioVideoMixerDebugInfo? GetDebugInfo()
    {
        AudioVideoMixerRuntime? runtime;

        lock (_gate)
        {
            runtime = _runtime;
        }

        return runtime?.GetSnapshot();
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

    internal void RaiseAudioSourceError(Guid sourceId, int errorCode, string? message)
    {
        AudioSourceError?.Invoke(this, new AudioSourceErrorEventArgs(sourceId, errorCode, message));
    }

    internal void RaiseVideoSourceError(Guid sourceId, int errorCode, string? message)
    {
        VideoSourceError?.Invoke(this, new VideoSourceErrorEventArgs(sourceId, errorCode, message));
    }
}
