using System.Collections.ObjectModel;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;

namespace S.Media.Core.Mixing;

public sealed class AudioMixer : IAudioMixer
{
    private readonly Lock _gate = new();
    private readonly List<IAudioSource> _sources = [];
    private readonly Dictionary<Guid, double> _startOffsets = [];
    private readonly IMediaClock _clock;
    private MixerSourceDetachOptions _detachOptions = new();

    public AudioMixer(IMediaClock? clock = null, ClockType clockType = ClockType.AudioLed)
    {
        _clock = clock ?? new CoreMediaClock();

        if (MixerClockTypeRules.Validate(MixerKind.Audio, clockType) != MediaResult.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(clockType));
        }

        if (clockType == ClockType.External && _clock is CoreMediaClock)
        {
            throw new ArgumentException("ClockType.External requires a non-CoreMediaClock implementation.", nameof(clockType));
        }

        ClockType = clockType;
        State = AudioMixerState.Stopped;
        SyncMode = AudioMixerSyncMode.Realtime;
    }

    public AudioMixerState State { get; private set; }

    public AudioMixerSyncMode SyncMode { get; private set; }

    public IMediaClock Clock => _clock;

    public ClockType ClockType { get; private set; }

    public double PositionSeconds => _clock.CurrentSeconds;

    public bool IsRunning => State == AudioMixerState.Running;

    public IReadOnlyList<IAudioSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<IAudioSource>([.. _sources]);
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

    public event EventHandler<AudioMixerStateChangedEventArgs>? StateChanged;

    public event EventHandler<AudioSourceErrorEventArgs>? SourceError;

    public event EventHandler<AudioMixerDropoutEventArgs>? DropoutDetected;

    internal void RaiseSourceError(Guid sourceId, int errorCode, string? message)
    {
        SourceError?.Invoke(this, new AudioSourceErrorEventArgs(sourceId, errorCode, message));
    }

    internal void RaiseDropoutDetected(Guid sourceId, int framesRequested, int framesReceived, double mixerPositionSeconds)
    {
        DropoutDetected?.Invoke(this, new AudioMixerDropoutEventArgs(sourceId, framesRequested, framesReceived, mixerPositionSeconds));
    }

    public int Start()
    {
        lock (_gate)
        {
            if (State == AudioMixerState.Running)
            {
                return MediaResult.Success;
            }

            var result = _clock.Start();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = AudioMixerState.Running;
            StateChanged?.Invoke(this, new AudioMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Pause()
    {
        lock (_gate)
        {
            if (State != AudioMixerState.Running)
            {
                return MediaResult.Success;
            }

            var result = _clock.Pause();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = AudioMixerState.Paused;
            StateChanged?.Invoke(this, new AudioMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Resume()
    {
        lock (_gate)
        {
            if (State != AudioMixerState.Paused)
            {
                return MediaResult.Success;
            }

            var result = _clock.Start();
            if (result != MediaResult.Success)
            {
                return result;
            }

            var previous = State;
            State = AudioMixerState.Running;
            StateChanged?.Invoke(this, new AudioMixerStateChangedEventArgs(previous, State));
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
            State = AudioMixerState.Stopped;
            StateChanged?.Invoke(this, new AudioMixerStateChangedEventArgs(previous, State));
            return MediaResult.Success;
        }
    }

    public int Seek(double positionSeconds) => _clock.Seek(positionSeconds);

    public int AddSource(IAudioSource source) => AddSource(source, 0d);

    public int AddSource(IAudioSource source, double startOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!double.IsFinite(startOffsetSeconds) || startOffsetSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (_sources.Any(s => s.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MixerSourceIdCollision;
            }

            _sources.Add(source);
            _startOffsets[source.SourceId] = startOffsetSeconds;
            return MediaResult.Success;
        }
    }

    public int RemoveSource(IAudioSource source)
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
                _startOffsets.Remove(target.SourceId);
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
            _startOffsets.Clear();
            return MediaResult.Success;
        }
    }

    public int SetSourceStartOffset(IAudioSource source, double startOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!double.IsFinite(startOffsetSeconds) || startOffsetSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (!_sources.Any(s => s.SourceId == source.SourceId))
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            _startOffsets[source.SourceId] = startOffsetSeconds;
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
        var validation = MixerClockTypeRules.Validate(MixerKind.Audio, clockType);
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

    public int SetSyncMode(AudioMixerSyncMode mode)
    {
        lock (_gate)
        {
            SyncMode = mode;
            return MediaResult.Success;
        }
    }

    private int ExecuteDetachSteps(IReadOnlyList<IAudioSource> targets)
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

