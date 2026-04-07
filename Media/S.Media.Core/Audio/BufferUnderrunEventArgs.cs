namespace S.Media.Core.Audio;

/// <summary>Event data for a buffer underrun on an <see cref="IAudioChannel"/>.</summary>
public sealed class BufferUnderrunEventArgs : EventArgs
{
    /// <summary>Position in the timeline when the underrun occurred.</summary>
    public TimeSpan Position { get; }

    /// <summary>Number of sample frames that were filled with silence.</summary>
    public int FramesDropped { get; }

    public BufferUnderrunEventArgs(TimeSpan position, int framesDropped)
    {
        Position     = position;
        FramesDropped = framesDropped;
    }
}

