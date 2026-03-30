using S.Media.Core.Runtime;

namespace S.Media.Core.Audio;

/// <summary>
/// Engine interface for audio subsystem management. Extends <see cref="IMediaEngine"/> with
/// audio-device enumeration and output creation.
/// </summary>
public interface IAudioEngine : IMediaEngine
{
    AudioEngineState State { get; }

    AudioEngineConfig Config { get; }

    int Initialize(AudioEngineConfig config);

    int Start();

    int Stop();

    // Terminate() and IsInitialized are inherited from IMediaEngine.

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
