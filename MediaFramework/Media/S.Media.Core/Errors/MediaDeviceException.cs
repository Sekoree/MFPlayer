namespace S.Media.Core.Errors;

/// <summary>
/// Thrown when opening, starting or communicating with a physical device fails
/// (PortAudio stream open, NDI receiver connect, SDL3 window create, …).
/// </summary>
/// <remarks>
/// Closes review finding <b>EL1</b>: replaces <see cref="System.InvalidOperationException"/>
/// at device-facing endpoint boundaries.
/// </remarks>
public class MediaDeviceException : MediaException
{
    /// <summary>Device name or identifier that failed (may be null).</summary>
    public string? DeviceName { get; }

    public MediaDeviceException() { }
    public MediaDeviceException(string message) : base(message) { }
    public MediaDeviceException(string message, Exception inner) : base(message, inner) { }
    public MediaDeviceException(string message, string? deviceName) : base(message) { DeviceName = deviceName; }
    public MediaDeviceException(string message, string? deviceName, Exception inner) : base(message, inner) { DeviceName = deviceName; }
}

