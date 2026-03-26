using System.Collections.ObjectModel;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

public sealed class MediaPlayer : IMediaPlayer
{
    private readonly IAudioVideoMixer _mixer;
    private readonly List<IAudioSource> _attachedAudioSources = [];
    private readonly List<IVideoSource> _attachedVideoSources = [];
    private IMediaItem? _activeMedia;
    private readonly Lock _gate = new();

    public MediaPlayer(IAudioVideoMixer mixer)
    {
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
    }

    public AudioVideoMixerState State => _mixer.State;

    public IMediaClock Clock => _mixer.Clock;

    public ClockType ClockType => _mixer.ClockType;

    public AudioVideoSyncMode SyncMode => _mixer.SyncMode;

    public double PositionSeconds => _mixer.PositionSeconds;

    public bool IsRunning => _mixer.IsRunning;

    public IReadOnlyList<IAudioSource> AudioSources => _mixer.AudioSources;

    public IReadOnlyList<IVideoSource> VideoSources => _mixer.VideoSources;

    public MixerSourceDetachOptions AudioSourceDetachOptions => _mixer.AudioSourceDetachOptions;

    public MixerSourceDetachOptions VideoSourceDetachOptions => _mixer.VideoSourceDetachOptions;

    public event EventHandler<AudioSourceErrorEventArgs>? AudioSourceError
    {
        add => _mixer.AudioSourceError += value;
        remove => _mixer.AudioSourceError -= value;
    }

    public event EventHandler<VideoSourceErrorEventArgs>? VideoSourceError
    {
        add => _mixer.VideoSourceError += value;
        remove => _mixer.VideoSourceError -= value;
    }

    public event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged
    {
        add => _mixer.ActiveVideoSourceChanged += value;
        remove => _mixer.ActiveVideoSourceChanged -= value;
    }

    public IReadOnlyList<IAudioOutput> AudioOutputs => _mixer.AudioOutputs;

    public IReadOnlyList<IVideoOutput> VideoOutputs => _mixer.VideoOutputs;

    public int Play(IMediaItem media)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (media is IMediaPlaybackSourceBinding binding)
        {
            var detachCode = DetachCurrentMediaSources();
            if (detachCode != MediaResult.Success)
            {
                return detachCode;
            }

            var attachCode = AttachBoundSources(binding);
            if (attachCode != MediaResult.Success)
            {
                return attachCode;
            }

            lock (_gate)
            {
                _activeMedia = media;
            }
        }

        return _mixer.Start();
    }

    public int Start() => _mixer.Start();

    public int Stop() => _mixer.Stop();

    public int Pause() => _mixer.Pause();

    public int Resume() => _mixer.Resume();

    public int Seek(double positionSeconds) => _mixer.Seek(positionSeconds);

    public int AddAudioSource(IAudioSource source) => _mixer.AddAudioSource(source);

    public int RemoveAudioSource(IAudioSource source) => _mixer.RemoveAudioSource(source);

    public int AddVideoSource(IVideoSource source) => _mixer.AddVideoSource(source);

    public int RemoveVideoSource(IVideoSource source) => _mixer.RemoveVideoSource(source);

    public int ConfigureAudioSourceDetachOptions(MixerSourceDetachOptions options) =>
        _mixer.ConfigureAudioSourceDetachOptions(options);

    public int ConfigureVideoSourceDetachOptions(MixerSourceDetachOptions options) =>
        _mixer.ConfigureVideoSourceDetachOptions(options);

    public int SetClockType(ClockType clockType) => _mixer.SetClockType(clockType);

    public int SetSyncMode(AudioVideoSyncMode syncMode) => _mixer.SetSyncMode(syncMode);

    public int SetActiveVideoSource(IVideoSource source) => _mixer.SetActiveVideoSource(source);

    public int AddAudioOutput(IAudioOutput output) => _mixer.AddAudioOutput(output);

    public int RemoveAudioOutput(IAudioOutput output) => _mixer.RemoveAudioOutput(output);

    public int AddVideoOutput(IVideoOutput output) => _mixer.AddVideoOutput(output);

    public int RemoveVideoOutput(IVideoOutput output) => _mixer.RemoveVideoOutput(output);

    private int DetachCurrentMediaSources()
    {
        List<IAudioSource> audioToRemove;
        List<IVideoSource> videoToRemove;

        lock (_gate)
        {
            audioToRemove = [.. _attachedAudioSources];
            videoToRemove = [.. _attachedVideoSources];
        }

        var firstError = MediaResult.Success;

        foreach (var source in audioToRemove)
        {
            var code = _mixer.RemoveAudioSource(source);
            if (code != MediaResult.Success && firstError == MediaResult.Success)
            {
                firstError = code;
            }
        }

        foreach (var source in videoToRemove)
        {
            var code = _mixer.RemoveVideoSource(source);
            if (code != MediaResult.Success && firstError == MediaResult.Success)
            {
                firstError = code;
            }
        }

        if (firstError != MediaResult.Success)
        {
            return firstError;
        }

        lock (_gate)
        {
            _attachedAudioSources.Clear();
            _attachedVideoSources.Clear();
            _activeMedia = null;
        }

        return MediaResult.Success;
    }

    private int AttachBoundSources(IMediaPlaybackSourceBinding binding)
    {
        var addedAudio = new List<IAudioSource>();
        var addedVideo = new List<IVideoSource>();

        foreach (var source in binding.PlaybackAudioSources)
        {
            var code = _mixer.AddAudioSource(source);
            if (code != MediaResult.Success)
            {
                RollbackAdded(addedAudio, addedVideo);
                return code;
            }

            addedAudio.Add(source);
        }

        foreach (var source in binding.PlaybackVideoSources)
        {
            var code = _mixer.AddVideoSource(source);
            if (code != MediaResult.Success)
            {
                RollbackAdded(addedAudio, addedVideo);
                return code;
            }

            addedVideo.Add(source);
        }

        if (binding.InitialActiveVideoSource is not null)
        {
            var code = _mixer.SetActiveVideoSource(binding.InitialActiveVideoSource);
            if (code != MediaResult.Success)
            {
                RollbackAdded(addedAudio, addedVideo);
                return code;
            }
        }

        lock (_gate)
        {
            _attachedAudioSources.Clear();
            _attachedAudioSources.AddRange(addedAudio);
            _attachedVideoSources.Clear();
            _attachedVideoSources.AddRange(addedVideo);
        }

        return MediaResult.Success;
    }

    private void RollbackAdded(IEnumerable<IAudioSource> addedAudio, IEnumerable<IVideoSource> addedVideo)
    {
        foreach (var source in addedAudio)
        {
            _mixer.RemoveAudioSource(source);
        }

        foreach (var source in addedVideo)
        {
            _mixer.RemoveVideoSource(source);
        }
    }
}

