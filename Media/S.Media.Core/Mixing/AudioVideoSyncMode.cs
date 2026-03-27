namespace S.Media.Core.Mixing;

public enum AudioVideoSyncMode
{
    /// <summary>Present freshest frame, coalesce older — smooth visual playback.</summary>
    Realtime = 0,

    /// <summary>Clock-aligned presentation with configurable tolerance.</summary>
    Synced = 1,
}
