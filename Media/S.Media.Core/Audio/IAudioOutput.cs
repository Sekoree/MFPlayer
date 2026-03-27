namespace S.Media.Core.Audio;

public interface IAudioOutput : IDisposable
{
    Guid Id { get; }

    AudioOutputState State { get; }

    AudioDeviceInfo Device { get; }

    int Start(AudioOutputConfig config);

    int Stop();

    int SetOutputDevice(AudioDeviceId deviceId);

    int SetOutputDeviceByName(string deviceName);

    int SetOutputDeviceByIndex(int deviceIndex);

    event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;

    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex);

    int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount);
}
