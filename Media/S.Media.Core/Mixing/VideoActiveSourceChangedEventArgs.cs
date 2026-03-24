namespace S.Media.Core.Mixing;

public sealed class VideoActiveSourceChangedEventArgs : EventArgs
{
    public VideoActiveSourceChangedEventArgs(Guid? previousSourceId, Guid? currentSourceId)
    {
        PreviousSourceId = previousSourceId;
        CurrentSourceId = currentSourceId;
    }

    public Guid? PreviousSourceId { get; }

    public Guid? CurrentSourceId { get; }
}

