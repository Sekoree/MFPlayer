using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public interface IAudioVideoMixer
{
    AudioVideoMixerState State { get; }

    IMediaClock Clock { get; }

    ClockType ClockType { get; }

    double PositionSeconds { get; }

    bool IsRunning { get; }

    IAudioMixer AudioMixer { get; }

    IVideoMixer VideoMixer { get; }

    int Start();

    int Pause();

    int Resume();

    int Stop();

    int Seek(double positionSeconds);

    int AddAudioSource(IAudioSource source);

    int RemoveAudioSource(IAudioSource source);

    int AddVideoSource(IVideoSource source);

    int RemoveVideoSource(IVideoSource source);

    IReadOnlyList<IAudioSource> AudioSources { get; }

    IReadOnlyList<IVideoSource> VideoSources { get; }

    MixerSourceDetachOptions AudioSourceDetachOptions { get; }

    MixerSourceDetachOptions VideoSourceDetachOptions { get; }

    int ConfigureAudioSourceDetachOptions(MixerSourceDetachOptions options);

    int ConfigureVideoSourceDetachOptions(MixerSourceDetachOptions options);

    int SetClockType(ClockType clockType);

    int SetActiveVideoSource(IVideoSource source);

    event EventHandler<AudioSourceErrorEventArgs>? AudioSourceError;

    event EventHandler<VideoSourceErrorEventArgs>? VideoSourceError;

    event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged;
}

