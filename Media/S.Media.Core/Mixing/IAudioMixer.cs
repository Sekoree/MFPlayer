using S.Media.Core.Audio;
using S.Media.Core.Clock;

namespace S.Media.Core.Mixing;

public interface IAudioMixer
{
    AudioMixerState State { get; }

    AudioMixerSyncMode SyncMode { get; }

    IMediaClock Clock { get; }

    ClockType ClockType { get; }

    double PositionSeconds { get; }

    bool IsRunning { get; }

    int Start();

    int Pause();

    int Resume();

    int Stop();

    int Seek(double positionSeconds);

    int AddSource(IAudioSource source);

    int AddSource(IAudioSource source, double startOffsetSeconds);

    int RemoveSource(IAudioSource source);

    int RemoveSource(Guid sourceId);

    int ClearSources();

    IReadOnlyList<IAudioSource> Sources { get; }

    int SourceCount { get; }

    MixerSourceDetachOptions SourceDetachOptions { get; }

    int SetSourceStartOffset(IAudioSource source, double startOffsetSeconds);

    int ConfigureSourceDetachOptions(MixerSourceDetachOptions options);

    int SetClockType(ClockType clockType);

    int SetSyncMode(AudioMixerSyncMode mode);

    event EventHandler<AudioMixerStateChangedEventArgs>? StateChanged;

    event EventHandler<AudioSourceErrorEventArgs>? SourceError;

    event EventHandler<AudioMixerDropoutEventArgs>? DropoutDetected;
}

