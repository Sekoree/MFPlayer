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
    /// The currently loaded media item, or <see langword="null"/> if nothing is loaded.
    /// Set by <see cref="Play"/> and cleared by <see cref="DetachCurrentMediaSources"/>.
    /// </summary>
    public IMediaItem? ActiveMedia { get { lock (Gate) return _activeMedia; } }

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
        IAudioSource[] audioToRemove;
        IVideoSource[] videoToRemove;

        lock (Gate)
        {
            audioToRemove = _attachedAudioSources.Count > 0
                ? [.. _attachedAudioSources] : [];
            videoToRemove = _attachedVideoSources.Count > 0
                ? [.. _attachedVideoSources] : [];
            _attachedAudioSources.Clear();
            _attachedVideoSources.Clear();
            _activeMedia = null;
        }

        var firstError = MediaResult.Success;

        foreach (var source in audioToRemove)
        {
            var code = RemoveAudioSource(source);
            if (code != MediaResult.Success && firstError == MediaResult.Success)
                firstError = code;
        }

        foreach (var source in videoToRemove)
        {
            var code = RemoveVideoSource(source);
            if (code != MediaResult.Success && firstError == MediaResult.Success)
                firstError = code;
        }

        return firstError;
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

    private static AVMixerConfig BuildDefaultConfig(IMediaPlaybackSourceBinding binding)
    {
        var maxChannels = 2;
        foreach (var source in binding.PlaybackAudioSources)
        {
            var ch = source.StreamInfo.ChannelCount.GetValueOrDefault(2);
            if (ch > maxChannels) maxChannels = ch;
        }

        return AVMixerConfig.ForSourceToStereo(Math.Max(1, maxChannels));
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
