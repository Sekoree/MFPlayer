using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

public interface IVideoMixer
{
    VideoMixerState State { get; }

    VideoMixerSyncMode SyncMode { get; }

    IMediaClock Clock { get; }

    ClockType ClockType { get; }

    double PositionSeconds { get; }

    bool IsRunning { get; }

    IVideoSource? ActiveSource { get; }

    int SourceCount { get; }

    int Start();

    int Pause();

    int Resume();

    int Stop();

    int Seek(double positionSeconds);

    int AddSource(IVideoSource source);

    int RemoveSource(IVideoSource source);

    int RemoveSource(Guid sourceId);

    int ClearSources();

    IReadOnlyList<IVideoSource> Sources { get; }

    MixerSourceDetachOptions SourceDetachOptions { get; }

    int SetActiveSource(IVideoSource source);

    int ConfigureSourceDetachOptions(MixerSourceDetachOptions options);

    int SetClockType(ClockType clockType);

    int SetSyncMode(VideoMixerSyncMode mode);

    event EventHandler<VideoMixerStateChangedEventArgs>? StateChanged;

    event EventHandler<VideoSourceErrorEventArgs>? SourceError;

    event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveSourceChanged;
}

