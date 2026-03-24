using System.Collections.ObjectModel;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public sealed class VideoMixer : IVideoMixer
{
    private readonly Lock _gate = new();
    private readonly List<IVideoSource> _sources = [];
    private readonly IMediaClock _clock;
    private MixerSourceDetachOptions _detachOptions = new();
    private IVideoSource? _activeSource;

    public VideoMixer(IMediaClock? clock = null, ClockType clockType = ClockType.VideoLed)
    {
        _clock = clock ?? new CoreMediaClock();

        if (MixerClockTypeRules.Validate(MixerKind.Video, clockType) != MediaResult.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(clockType));
        }

        if (clockType == ClockType.External && _clock is CoreMediaClock)
        {
            throw new ArgumentException("ClockType.External requires a non-CoreMediaClock implementation.", nameof(clockType));
        }

        ClockType = clockType;
        State = VideoMixerState.Stopped;
        SyncMode = VideoMixerSyncMode.Realtime;
    }

    public VideoMixerState State { get; private set; }

    public VideoMixerSyncMode SyncMode { get; private set; }

    public IMediaClock Clock => _clock;

    public ClockType ClockType { get; private set; }

    public double PositionSeconds => _clock.CurrentSeconds;

    public bool IsRunning => State == VideoMixerState.Running;

    public IVideoSource? ActiveSource
    {
        get
        {
            lock (_gate)
            {
                return _activeSource;
            }
        }
    }

    public int SourceCount
    {
        get
        {
            lock (_gate)
            {
                return _sources.Count;
            }
        }
    }

    public IReadOnlyList<IVideoSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<IVideoSource>([.. _sources]);
            }
        }
    }

    public MixerSourceDetachOptions SourceDetachOptions
    {
        get
        {
            lock (_gate)
            {
                return _detachOptions;
            }
        }
    }

    public event EventHandler<VideoMixerStateChangedEventArgs>? StateChanged;

    // Reserved for implementation-phase detailed source failure reporting.
    public event EventHandler<VideoSourceErrorEventArgs>? SourceError
    {
        add { }
        remove { }
    }

    public event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveSourceChanged;

    public int Start()
    {
        lock (_gate)
        {
            if (State == VideoMixerState.Running)
            {
                return MediaResult.Success;
            }

            var result = _clock.Start();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = VideoMixerState.Running;
            StateChanged?.Invoke(this, new VideoMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Pause()
    {
        lock (_gate)
        {
            if (State != VideoMixerState.Running)
            {
                return MediaResult.Success;
            }

            var result = _clock.Pause();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = VideoMixerState.Paused;
            StateChanged?.Invoke(this, new VideoMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Resume()
    {
        lock (_gate)
        {
            if (State != VideoMixerState.Paused)
            {
                return MediaResult.Success;
            }

            var result = _clock.Start();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = VideoMixerState.Running;
            StateChanged?.Invoke(this, new VideoMixerStateChangedEventArgs(previous, State));
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
            State = VideoMixerState.Stopped;
            StateChanged?.Invoke(this, new VideoMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Seek(double positionSeconds) => _clock.Seek(positionSeconds);

    public int AddSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_sources.Any(s => s.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MixerSourceIdCollision;
            }

            _sources.Add(source);
            return MediaResult.Success;
        }
    }

    public int RemoveSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RemoveSource(source.SourceId);
    }

    public int RemoveSource(Guid sourceId)
    {
        lock (_gate)
        {
            var targets = _sources.Where(s => s.SourceId == sourceId).ToList();
            if (targets.Count == 0)
            {
                return MediaResult.Success;
            }

            var detachResult = ExecuteDetachSteps(targets);
            if (detachResult != MediaResult.Success)
            {
                return detachResult;
            }

            foreach (var target in targets)
            {
                _sources.Remove(target);
            }

            if (_activeSource is not null && targets.Any(t => t.SourceId == _activeSource.SourceId))
            {
                var previous = _activeSource.SourceId;
                _activeSource = null;
                ActiveSourceChanged?.Invoke(this, new VideoActiveSourceChangedEventArgs(previous, null));
            }

            return MediaResult.Success;
        }
    }

    public int ClearSources()
    {
        lock (_gate)
        {
            var targets = _sources.ToList();
            var detachResult = ExecuteDetachSteps(targets);
            if (detachResult != MediaResult.Success)
            {
                return detachResult;
            }

            _sources.Clear();

            if (_activeSource is not null)
            {
                var previous = _activeSource.SourceId;
                _activeSource = null;
                ActiveSourceChanged?.Invoke(this, new VideoActiveSourceChangedEventArgs(previous, null));
            }

            return MediaResult.Success;
        }
    }

    public int SetActiveSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_sources.Any(v => v.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            var previous = _activeSource?.SourceId;
            _activeSource = source;
            ActiveSourceChanged?.Invoke(this, new VideoActiveSourceChangedEventArgs(previous, source.SourceId));
            return MediaResult.Success;
        }
    }

    public int ConfigureSourceDetachOptions(MixerSourceDetachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _detachOptions = options;
            return MediaResult.Success;
        }
    }

    public int SetClockType(ClockType clockType)
    {
        var validation = MixerClockTypeRules.Validate(MixerKind.Video, clockType);
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

    public int SetSyncMode(VideoMixerSyncMode mode)
    {
        lock (_gate)
        {
            SyncMode = mode;
            return MediaResult.Success;
        }
    }

    private int ExecuteDetachSteps(IReadOnlyList<IVideoSource> targets)
    {
        var firstError = MediaResult.Success;

        if (_detachOptions.StopOnDetach)
        {
            foreach (var source in targets)
            {
                var code = source.Stop();
                if (code != MediaResult.Success && firstError == MediaResult.Success)
                {
                    firstError = code;
                }
            }
        }

        if (firstError != MediaResult.Success)
        {
            return firstError;
        }

        if (_detachOptions.DisposeOnDetach)
        {
            foreach (var source in targets)
            {
                try
                {
                    source.Dispose();
                }
                catch
                {
                    if (firstError == MediaResult.Success)
                    {
                        firstError = (int)MediaErrorCode.MixerDetachStepFailed;
                    }
                }
            }
        }

        return firstError;
    }
}

