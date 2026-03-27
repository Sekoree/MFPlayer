namespace S.Media.Core.Mixing;

public sealed class AudioVideoMixerStateChangedEventArgs : EventArgs
{
    public AudioVideoMixerStateChangedEventArgs(AudioVideoMixerState previousState, AudioVideoMixerState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public AudioVideoMixerState PreviousState { get; }

    public AudioVideoMixerState CurrentState { get; }
}
