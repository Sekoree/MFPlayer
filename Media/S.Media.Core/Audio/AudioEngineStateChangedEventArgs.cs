namespace S.Media.Core.Audio;

public sealed class AudioEngineStateChangedEventArgs : EventArgs
{
    public AudioEngineStateChangedEventArgs(AudioEngineState previousState, AudioEngineState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public AudioEngineState PreviousState { get; }

    public AudioEngineState CurrentState { get; }
}
