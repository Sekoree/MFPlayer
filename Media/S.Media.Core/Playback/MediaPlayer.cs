using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

public sealed class MediaPlayer : AudioVideoMixer, IMediaPlayer
{
    private readonly List<IAudioSource> _attachedAudioSources = [];
    private readonly List<IVideoSource> _attachedVideoSources = [];
    private IMediaItem? _activeMedia;
    private readonly Lock _playerGate = new();

    public MediaPlayer(IMediaClock? clock = null, ClockType clockType = ClockType.Hybrid)
        : base(clock, clockType)
    {
    }

    /// <summary>
    /// Consumer-facing playback config. When set, <see cref="Play"/> will call
    /// <see cref="IAudioVideoMixer.StartPlayback"/> with this config automatically.
    /// </summary>
    public AudioVideoMixerConfig? PlaybackConfig { get; set; }

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

            lock (_playerGate)
            {
                _activeMedia = media;
            }
        }

        // Start playback pump threads automatically
        var config = PlaybackConfig ?? new AudioVideoMixerConfig();
        return StartPlayback(config);
    }

    private int DetachCurrentMediaSources()
    {
        List<IAudioSource> audioToRemove;
        List<IVideoSource> videoToRemove;

        lock (_playerGate)
        {
            audioToRemove = [.. _attachedAudioSources];
            videoToRemove = [.. _attachedVideoSources];
        }

        var firstError = MediaResult.Success;

        foreach (var source in audioToRemove)
        {
            var code = RemoveAudioSource(source);
            if (code != MediaResult.Success && firstError == MediaResult.Success)
            {
                firstError = code;
            }
        }

        foreach (var source in videoToRemove)
        {
            var code = RemoveVideoSource(source);
            if (code != MediaResult.Success && firstError == MediaResult.Success)
            {
                firstError = code;
            }
        }

        if (firstError != MediaResult.Success)
        {
            return firstError;
        }

        lock (_playerGate)
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
            var code = AddAudioSource(source);
            if (code != MediaResult.Success)
            {
                RollbackAdded(addedAudio, addedVideo);
                return code;
            }

            addedAudio.Add(source);
        }

        foreach (var source in binding.PlaybackVideoSources)
        {
            var code = AddVideoSource(source);
            if (code != MediaResult.Success)
            {
                RollbackAdded(addedAudio, addedVideo);
                return code;
            }

            addedVideo.Add(source);
        }

        if (binding.InitialActiveVideoSource is not null)
        {
            var code = SetActiveVideoSource(binding.InitialActiveVideoSource);
            if (code != MediaResult.Success)
            {
                RollbackAdded(addedAudio, addedVideo);
                return code;
            }
        }

        lock (_playerGate)
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
            RemoveAudioSource(source);
        }

        foreach (var source in addedVideo)
        {
            RemoveVideoSource(source);
        }
    }
}
