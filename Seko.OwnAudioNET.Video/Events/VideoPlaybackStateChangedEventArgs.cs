namespace Seko.OwnAudioNET.Video.Events;

/// <summary>Describes a video playback state transition.</summary>
public sealed class VideoPlaybackStateChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance describing a state transition.</summary>
    public VideoPlaybackStateChangedEventArgs(VideoPlaybackState oldState, VideoPlaybackState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    /// <summary>The previous playback state.</summary>
    public VideoPlaybackState OldState { get; }

    /// <summary>The new playback state.</summary>
    public VideoPlaybackState NewState { get; }
}

