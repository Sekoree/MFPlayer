namespace S.Media.Core.Audio;

/// <summary>Event data for a buffer underrun on an <see cref="IAudioChannel"/>.</summary>
public sealed class BufferUnderrunEventArgs : EventArgs
{
    /// <summary>Position in the timeline when the underrun occurred.</summary>
    public TimeSpan Position { get; }

    /// <summary>Number of sample frames that were filled with silence.</summary>
    public int FramesDropped { get; }

    /// <summary>
    /// <see langword="true"/> when the underrun is caused by end-of-stream
    /// (no more data will arrive from this channel).
    /// </summary>
    public bool IsEof { get; }

    public BufferUnderrunEventArgs(TimeSpan position, int framesDropped, bool isEof = false)
    {
        Position      = position;
        FramesDropped = framesDropped;
        IsEof         = isEof;
    }
}

