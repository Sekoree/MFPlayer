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
    private readonly IMediaClock _clock;
    private MixerSourceDetachOptions _audioDetachOptions = new();
    private MixerSourceDetachOptions _videoDetachOptions = new();
    private IVideoSource? _activeVideoSource;

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
        State = AudioVideoMixerState.Stopped;

        AudioMixer = new AudioMixer(_clock, ClockType.AudioLed);
        VideoMixer = new VideoMixer(_clock, ClockType.VideoLed);
    }

    public AudioVideoMixerState State { get; private set; }

    public IMediaClock Clock => _clock;

    public ClockType ClockType { get; private set; }

    public double PositionSeconds => _clock.CurrentSeconds;

    public bool IsRunning => State == AudioVideoMixerState.Running;

    public IAudioMixer AudioMixer { get; }

    public IVideoMixer VideoMixer { get; }

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

    // Reserved for implementation-phase detailed source failure reporting.
    public event EventHandler<AudioSourceErrorEventArgs>? AudioSourceError
    {
        add { }
        remove { }
    }

    // Reserved for implementation-phase detailed source failure reporting.
    public event EventHandler<VideoSourceErrorEventArgs>? VideoSourceError
    {
        add { }
        remove { }
    }
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

}

