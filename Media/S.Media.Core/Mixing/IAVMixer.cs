using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public interface IAVMixer : IMixerRouting, IDisposable
{
    AVMixerState State { get; }

    IMediaClock Clock { get; }

    ClockType ClockType { get; }

    AVSyncMode SyncMode { get; }

    double PositionSeconds { get; }

    bool IsRunning { get; }

    /// <summary>Master output volume applied post-mix (0.0 = silent, 1.0 = unity). Default: 1.0.</summary>
    float MasterVolume { get; set; }

    int StartPlayback(AVMixerConfig config);

    int StopPlayback();

    int PausePlayback();

    int ResumePlayback();

    int Seek(double positionSeconds);

    int AddAudioSource(IAudioSource source);

    int AddAudioSource(IAudioSource source, double startOffsetSeconds);

    int SetAudioSourceStartOffset(IAudioSource source, double startOffsetSeconds);

    int RemoveAudioSource(IAudioSource source, bool stopOnDetach = false, bool disposeOnDetach = false);

    int AddVideoSource(IVideoSource source);

    int RemoveVideoSource(IVideoSource source, bool stopOnDetach = false, bool disposeOnDetach = false);

    IReadOnlyList<IAudioSource> AudioSources { get; }

    IReadOnlyList<IVideoSource> VideoSources { get; }

    int SetClockType(ClockType clockType);

    int SetSyncMode(AVSyncMode syncMode);

    int SetActiveVideoSource(IVideoSource source);

    int AddAudioOutput(IAudioSink output);

    int RemoveAudioOutput(IAudioSink output);

    int AddVideoOutput(IVideoOutput output);

    int RemoveVideoOutput(IVideoOutput output);

    IReadOnlyList<IAudioSink> AudioOutputs { get; }

    IReadOnlyList<IVideoOutput> VideoOutputs { get; }

    AVMixerDiagnostics? GetDebugInfo();

    IReadOnlyList<VideoOutputDiagnostics> GetVideoOutputDiagnostics();

    event EventHandler<AVMixerStateChangedEventArgs>? StateChanged;

    event EventHandler<MediaSourceErrorEventArgs>? AudioSourceError;

    event EventHandler<MediaSourceErrorEventArgs>? VideoSourceError;

    event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged;
}
