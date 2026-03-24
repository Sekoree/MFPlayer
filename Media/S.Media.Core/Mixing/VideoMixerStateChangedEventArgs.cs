namespace S.Media.Core.Mixing;

public sealed class VideoMixerStateChangedEventArgs : EventArgs
{
    public VideoMixerStateChangedEventArgs(VideoMixerState previousState, VideoMixerState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public VideoMixerState PreviousState { get; }

    public VideoMixerState CurrentState { get; }
}

