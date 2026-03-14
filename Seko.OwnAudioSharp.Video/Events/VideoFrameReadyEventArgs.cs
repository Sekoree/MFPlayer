namespace Seko.OwnAudioSharp.Video.Events;

/// <summary>Event arguments carrying a newly promoted <see cref="VideoFrame"/>.</summary>
public sealed class VideoFrameReadyEventArgs : EventArgs
{
    /// <summary>Initializes a new instance with the given frame and master clock timestamp.</summary>
    public VideoFrameReadyEventArgs(VideoFrame frame, double masterTimestamp)
    {
        Frame = frame;
        MasterTimestamp = masterTimestamp;
    }

    /// <summary>The promoted frame. Do not hold a reference beyond the event handler without calling <see cref="VideoFrame.AddRef"/>.</summary>
    public VideoFrame Frame { get; }

    /// <summary>Master clock value (seconds) at the moment the frame was promoted.</summary>
    public double MasterTimestamp { get; }
}

