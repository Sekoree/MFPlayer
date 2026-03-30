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

    /// <summary>
    /// Removes <paramref name="output"/> from the engine's tracked output list and disposes it.
    /// Returns <see cref="S.Media.Core.Errors.MediaResult.Success"/> if removed, or
    /// <see cref="S.Media.Core.Errors.MediaErrorCode.PortAudioDeviceNotFound"/> if not tracked.
    /// </summary>
    int RemoveOutput(IAudioOutput output);

    /// <summary>
    /// Re-enumerates available audio devices without tearing down active streams.
    /// Useful for responding to hot-plug events after <see cref="Initialize"/> has been called.
    /// Returns <see cref="S.Media.Core.Errors.MediaResult.Success"/> on success.
    /// </summary>
    int RefreshDevices();

    IReadOnlyList<IAudioOutput> Outputs { get; }

    event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;
}
