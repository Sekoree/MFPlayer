using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public interface IAudioVideoMixer
{
    AudioVideoMixerState State { get; }

    IMediaClock Clock { get; }

    ClockType ClockType { get; }

    AudioVideoSyncMode SyncMode { get; }

    double PositionSeconds { get; }

    bool IsRunning { get; }

    int Start();

    int Pause();

    int Resume();

    int Stop();

    int Seek(double positionSeconds);

    int AddAudioSource(IAudioSource source);

    int AddAudioSource(IAudioSource source, double startOffsetSeconds);

    int SetAudioSourceStartOffset(IAudioSource source, double startOffsetSeconds);

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

    int SetSyncMode(AudioVideoSyncMode syncMode);

    int SetActiveVideoSource(IVideoSource source);

    int AddAudioOutput(IAudioOutput output);

    int RemoveAudioOutput(IAudioOutput output);

    int AddVideoOutput(IVideoOutput output);

    int RemoveVideoOutput(IVideoOutput output);

    IReadOnlyList<IAudioOutput> AudioOutputs { get; }

    IReadOnlyList<IVideoOutput> VideoOutputs { get; }

    int StartPlayback(AudioVideoMixerConfig config);

    int StopPlayback();

    TimeSpan TickVideoPresentation();

    AudioVideoMixerDebugInfo? GetDebugInfo();

    event EventHandler<AudioVideoMixerStateChangedEventArgs>? StateChanged;

    event EventHandler<AudioSourceErrorEventArgs>? AudioSourceError;

    event EventHandler<VideoSourceErrorEventArgs>? VideoSourceError;

    event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged;
}
