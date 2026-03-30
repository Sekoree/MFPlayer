namespace S.Media.Core.Audio;

/// <summary>
/// Live audio capture source with device-selection APIs.
/// Extends <see cref="IAudioSource"/> with input-specific lifecycle
/// (<see cref="Start(AudioInputConfig)"/>) and device management.
/// </summary>
/// <remarks>
/// <see cref="DurationSeconds"/> returns <see cref="double.NaN"/> and
/// <see cref="IAudioSource.Seek"/> returns
/// <see cref="S.Media.Core.Errors.MediaErrorCode.MediaSourceNonSeekable"/> — this is a
/// live capture source with no known total duration.
/// </remarks>
public interface IAudioInput : IAudioSource
{
    /// <summary>Current capture configuration (snapshot from the last <see cref="Start(AudioInputConfig)"/> call).</summary>
    AudioInputConfig Config { get; }

    /// <summary>The device this input is capturing from.</summary>
    AudioDeviceInfo Device { get; }

    /// <summary>Starts capture with an explicit configuration.</summary>
    int Start(AudioInputConfig config);

    /// <summary>Selects the capture device by ID and restarts the stream if already running.</summary>
    int SetInputDevice(AudioDeviceId deviceId);

    /// <summary>Selects the capture device by name (case-insensitive) and restarts the stream if already running.</summary>
    int SetInputDeviceByName(string deviceName);

    /// <summary>
    /// Selects the capture device by index in the engine's input-device list.
    /// Pass <c>-1</c> to select the system default input device.
    /// </summary>
    int SetInputDeviceByIndex(int deviceIndex);

    /// <summary>Fired when the active capture device changes.</summary>
    event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;
}

