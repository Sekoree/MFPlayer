namespace S.Media.Core.Mixing;

public sealed class AudioMixerStateChangedEventArgs : EventArgs
{
    public AudioMixerStateChangedEventArgs(AudioMixerState previousState, AudioMixerState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public AudioMixerState PreviousState { get; }

    public AudioMixerState CurrentState { get; }
}

