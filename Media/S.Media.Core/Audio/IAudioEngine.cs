namespace S.Media.Core.Audio;

public interface IAudioEngine : IDisposable
{
    AudioEngineState State { get; }

    AudioEngineConfig Config { get; }

    int Initialize(AudioEngineConfig config);

    int Start();

    int Stop();

    int Terminate();

    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    IReadOnlyList<AudioHostApiInfo> GetHostApis();

    AudioDeviceInfo? GetDefaultOutputDevice();

    AudioDeviceInfo? GetDefaultInputDevice();

    int CreateOutput(AudioDeviceId deviceId, out IAudioOutput? output);

    int CreateOutputByName(string deviceName, out IAudioOutput? output);

    int CreateOutputByIndex(int deviceIndex, out IAudioOutput? output);

    IReadOnlyList<IAudioOutput> Outputs { get; }

    event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;
}
