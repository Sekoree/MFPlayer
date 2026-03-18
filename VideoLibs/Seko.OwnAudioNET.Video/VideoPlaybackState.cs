using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video;

/// <summary>
/// Playback state for video sources managed by <see cref="IVideoSource"/>.
/// Mirrors the transport semantics used by OwnAudio's audio sources.
/// </summary>
public enum VideoPlaybackState
{
    Stopped = 0,
    Playing = 1,
    Paused = 2,
    EndOfStream = 3,
    Error = 4
}

