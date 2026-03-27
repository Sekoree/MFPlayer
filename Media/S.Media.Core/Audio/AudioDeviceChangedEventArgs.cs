namespace S.Media.Core.Audio;

public sealed class AudioDeviceChangedEventArgs : EventArgs
{
    public AudioDeviceChangedEventArgs(AudioDeviceInfo previousDevice, AudioDeviceInfo currentDevice)
    {
        PreviousDevice = previousDevice;
        CurrentDevice = currentDevice;
    }

    public AudioDeviceInfo PreviousDevice { get; }

    public AudioDeviceInfo CurrentDevice { get; }
}
