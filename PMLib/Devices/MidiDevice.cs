using System.Runtime.InteropServices;
using PMLib.Types;

namespace PMLib.Devices;

/// <summary>
/// Base class for an open PortMidi device stream.
/// Call <see cref="Open"/> to open the device and <see cref="Dispose"/> (or <see cref="Close"/>)
/// to release it.
/// </summary>
public abstract class MidiDevice : IDisposable
{
    private bool _disposed;

    /// <summary>Native PortMidi stream handle. <see cref="nint.Zero"/> when not open.</summary>
    protected nint Stream;

    /// <summary>PortMidi device ID passed to the constructor.</summary>
    public int DeviceId { get; }

    /// <summary>Device name cached at construction time (safe after <c>Pm_Terminate</c>).</summary>
    public string? Name { get; }

    /// <summary>Underlying MIDI API cached at construction time (e.g. <c>"ALSA"</c>).</summary>
    public string? Interface { get; }

    /// <summary>Returns <see langword="true"/> when the stream is open.</summary>
    public bool IsOpen => Stream != nint.Zero;

    protected MidiDevice(int deviceId)
    {
        DeviceId = deviceId;

        // Eagerly copy the name strings so they remain valid after Pm_Terminate.
        var ptr = Native.Pm_GetDeviceInfo(deviceId);
        if (ptr != nint.Zero)
        {
            var info = Marshal.PtrToStructure<PmDeviceInfo>(ptr);
            Name      = info.Name;
            Interface = info.Interf;
        }
    }

    /// <summary>Opens the device stream. Returns <see cref="PmError.NoError"/> on success.</summary>
    public abstract PmError Open();

    /// <summary>Closes the device stream.</summary>
    public virtual PmError Close()
    {
        if (!IsOpen) return PmError.BadPtr;
        var err = Native.Pm_Close(Stream);
        Stream = nint.Zero;
        return err;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}