namespace S.Media.Core.Audio;

/// <summary>
/// Hardware audio output. Extends <see cref="IAudioSink"/> with device-selection APIs.
/// Use <see cref="IAudioSink"/> when device management is not required (e.g. network or file sinks).
/// </summary>
public interface IAudioOutput : IAudioSink
{
    AudioDeviceInfo Device { get; }

    int SetOutputDevice(AudioDeviceId deviceId);

    int SetOutputDeviceByName(string deviceName);

    int SetOutputDeviceByIndex(int deviceIndex);

    event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;
}
