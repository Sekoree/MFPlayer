namespace Seko.OwnAudioSharp.Video.Events;

/// <summary>Event arguments carrying updated stream metadata after a decoder format change.</summary>
public sealed class VideoStreamInfoChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance with the updated stream info.</summary>
    public VideoStreamInfoChangedEventArgs(VideoStreamInfo streamInfo)
    {
        StreamInfo = streamInfo;
    }

    /// <summary>The latest stream metadata after the format change.</summary>
    public VideoStreamInfo StreamInfo { get; }
}

