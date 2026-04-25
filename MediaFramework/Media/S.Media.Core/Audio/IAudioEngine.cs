namespace S.Media.Core.Audio;

/// <summary>
/// Manages the audio subsystem lifetime and provides device / host-API enumeration.
/// One instance per application.
/// </summary>
public interface IAudioEngine : IDisposable
{
    bool IsInitialized { get; }

    /// <summary>Initialises the underlying audio subsystem (e.g. Pa_Initialize).</summary>
    void Initialize();

    /// <summary>Terminates the underlying audio subsystem (e.g. Pa_Terminate).</summary>
    void Terminate();

    IReadOnlyList<AudioHostApiInfo> GetHostApis();
    IReadOnlyList<AudioDeviceInfo>  GetDevices();
    AudioDeviceInfo?                GetDefaultOutputDevice();
    AudioDeviceInfo?                GetDefaultInputDevice();
}

