namespace S.Media.Core.Mixing;

public enum AudioVideoSyncMode
{
    // Favor exact clock alignment; can look choppier under bursty input.
    StrictAv = 0,

    // Blend clock alignment with backlog control.
    Hybrid = 1,

    // Favor smooth visual playback by presenting freshest frames.
    Stable = 2,
}
