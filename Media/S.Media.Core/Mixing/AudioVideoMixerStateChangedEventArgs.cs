namespace S.Media.Core.Mixing;

public sealed class AVMixerStateChangedEventArgs : EventArgs
{
    public AVMixerStateChangedEventArgs(AVMixerState previousState, AVMixerState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public AVMixerState PreviousState { get; }

    public AVMixerState CurrentState { get; }
}
