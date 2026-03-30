namespace S.Media.Core.Mixing;

public enum AVSyncMode
{
    /// <summary>Present freshest frame, coalesce older — smooth visual playback.</summary>
    Realtime = 0,

    /// <summary>Clock-aligned presentation with configurable tolerance.</summary>
    Synced = 1,

    /// <summary>Audio master clock drives video pacing — for file-based A/V playback.</summary>
    AudioLed = 2,
}
