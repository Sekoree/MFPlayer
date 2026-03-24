namespace S.Media.Core.Mixing;

public sealed class AudioMixerDropoutEventArgs : EventArgs
{
    public AudioMixerDropoutEventArgs(Guid sourceId, int framesRequested, int framesReceived, double mixerPositionSeconds)
    {
        SourceId = sourceId;
        FramesRequested = framesRequested;
        FramesReceived = framesReceived;
        MixerPositionSeconds = mixerPositionSeconds;
    }

    public Guid SourceId { get; }

    public int FramesRequested { get; }

    public int FramesReceived { get; }

    public double MixerPositionSeconds { get; }
}

