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

    // ── Input factory (5.1) ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a capture input for the device with the given ID and registers it with the engine.
    /// Pass <see cref="AudioDeviceId"/> from <see cref="GetInputDevices"/> or
    /// <see cref="GetDefaultInputDevice"/>.
    /// </summary>
    int CreateInput(AudioDeviceId deviceId, out IAudioInput? input);

    /// <summary>Creates a capture input by device name (case-insensitive).</summary>
    int CreateInputByName(string deviceName, out IAudioInput? input);

    /// <summary>
    /// Creates a capture input by index in the engine's input-device list.
    /// Pass <c>-1</c> to use the default input device.
    /// </summary>
    int CreateInputByIndex(int deviceIndex, out IAudioInput? input);

    /// <summary>
    /// Removes <paramref name="input"/> from the engine's tracked input list and disposes it.
    /// Returns <see cref="S.Media.Core.Errors.MediaResult.Success"/> if removed, or
    /// <see cref="S.Media.Core.Errors.MediaErrorCode.PortAudioDeviceNotFound"/> if not tracked.
    /// </summary>
    int RemoveInput(IAudioInput input);

    /// <summary>All inputs currently created by this engine (including stopped ones).</summary>
    IReadOnlyList<IAudioInput> Inputs { get; }

    // ── Device monitoring ─────────────────────────────────────────────────────

    /// <summary>
    /// Suppresses device-change handling (e.g. hot-plug refresh) until
    /// <see cref="ResumeDeviceMonitoring"/> is called.
    /// Useful for live-performance scenarios where device-change notifications
    /// during playback can trigger unwanted device switches.
    /// Calls nest — each <c>Pause</c> must be balanced by a matching <c>Resume</c>.
    /// </summary>
    void PauseDeviceMonitoring();

    /// <summary>
    /// Re-enables device-change handling after a previous <see cref="PauseDeviceMonitoring"/> call.
    /// If monitoring was paused and changes occurred, implementations may optionally
    /// refresh the device list on resume.
    /// </summary>
    void ResumeDeviceMonitoring();

    /// <summary>
    /// <c>true</c> when device monitoring is currently paused via
    /// <see cref="PauseDeviceMonitoring"/>.
    /// </summary>
    bool IsDeviceMonitoringPaused { get; }

    event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;
}
