using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Events;

public sealed class VideoActiveSourceChangedEventArgs : EventArgs
{
    public VideoActiveSourceChangedEventArgs(VideoStreamSource? oldSource, VideoStreamSource? newSource)
    {
        OldSource = oldSource;
        NewSource = newSource;
    }

    public VideoStreamSource? OldSource { get; }

    public VideoStreamSource? NewSource { get; }
}

