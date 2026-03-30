using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

public sealed class MediaPlayer : AVMixer, IMediaPlayer
{
    private readonly List<IAudioSource> _attachedAudioSources = [];
    private readonly List<IVideoSource> _attachedVideoSources = [];
    private IMediaItem? _activeMedia;

    public MediaPlayer(IMediaClock? clock = null, ClockType clockType = ClockType.Hybrid)
        : base(clock, clockType)
    {
    }

    /// <summary>
    /// Consumer-facing playback config. When set, <see cref="Play"/> will call
    /// <see cref="IAVMixer.StartPlayback"/> with this config automatically.
    /// </summary>
    public AVMixerConfig? PlaybackConfig { get; set; }

    public int Play(IMediaPlaybackSourceBinding media)
    {
        ArgumentNullException.ThrowIfNull(media);

        var detachCode = DetachCurrentMediaSources();
        if (detachCode != MediaResult.Success)
            return detachCode;

        var attachCode = AttachBoundSources(media);
        if (attachCode != MediaResult.Success)
            return attachCode;

        lock (Gate)
        {
            _activeMedia = media as IMediaItem;
        }

        var config = PlaybackConfig ?? BuildDefaultConfig(media);
        return StartPlayback(config);
    }

    private int DetachCurrentMediaSources()
    {
        List<IAudioSource> audioToRemove;
        List<IVideoSource> videoToRemove;

        lock (Gate)
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

        lock (Gate)
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

        lock (Gate)
        {
            _attachedAudioSources.Clear();
            _attachedAudioSources.AddRange(addedAudio);
            _attachedVideoSources.Clear();
            _attachedVideoSources.AddRange(addedVideo);
        }

        return MediaResult.Success;
    }

    private static AVMixerConfig BuildDefaultConfig(IMediaPlaybackSourceBinding binding)    {
        var firstAudio = binding.PlaybackAudioSources.FirstOrDefault();
        var channels = firstAudio?.StreamInfo.ChannelCount.GetValueOrDefault(2) ?? 2;
        return AVMixerConfig.ForSourceToStereo(Math.Max(1, channels));
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
