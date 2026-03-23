using System.Runtime.InteropServices;
using System.Text;
using PMLib.Types;

namespace PMLib;

/// <summary>
/// Static utility helpers for common PortMidi operations.
/// These are thin wrappers around the raw <see cref="Native"/> P/Invoke calls and do not
/// require a <see cref="PMManager"/> instance.
/// </summary>
public static class PMUtil
{
    // ── Device info ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a managed copy of the <see cref="PmDeviceInfo"/> for device
    /// <paramref name="id"/>, or <see langword="null"/> if the ID is out of range or
    /// the device has been deleted.
    /// </summary>
    public static PmDeviceInfo? GetDeviceInfo(int id)
    {
        var ptr = Native.Pm_GetDeviceInfo(id);
        return ptr == nint.Zero ? null : Marshal.PtrToStructure<PmDeviceInfo>(ptr);
    }

    /// <summary>
    /// Enumerates all known MIDI devices as <c>(id, info)</c> pairs.
    /// </summary>
    public static IEnumerable<(int Id, PmDeviceInfo Info)> GetAllDevices()
    {
        int count = Native.Pm_CountDevices();
        for (int i = 0; i < count; i++)
        {
            var ptr = Native.Pm_GetDeviceInfo(i);
            if (ptr != nint.Zero)
                yield return (i, Marshal.PtrToStructure<PmDeviceInfo>(ptr));
        }
    }

    /// <summary>Enumerates only devices that support MIDI input.</summary>
    public static IEnumerable<(int Id, PmDeviceInfo Info)> GetInputDevices()
        => GetAllDevices().Where(d => d.Info.Input != 0);

    /// <summary>Enumerates only devices that support MIDI output.</summary>
    public static IEnumerable<(int Id, PmDeviceInfo Info)> GetOutputDevices()
        => GetAllDevices().Where(d => d.Info.Output != 0);

    // ── Error text ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a managed <see cref="string"/> describing <paramref name="error"/>.
    /// The string is a static constant owned by PortMidi and never needs to be freed.
    /// </summary>
    public static string? GetErrorText(PmError error)
        => Marshal.PtrToStringUTF8(Native.Pm_GetErrorText(error));

    /// <summary>
    /// Returns and clears the pending host-level error as a managed <see cref="string"/>.
    /// Returns an empty string if there is no pending error.
    /// </summary>
    public static string GetHostErrorText()
    {
        Span<byte> buffer = stackalloc byte[256]; // PM_HOST_ERROR_MSG_LEN
        Native.Pm_GetHostErrorText(buffer, (uint)buffer.Length);
        int nullIdx = buffer.IndexOf((byte)0);
        var slice = nullIdx >= 0 ? buffer[..nullIdx] : buffer;
        return Encoding.UTF8.GetString(slice);
    }

    // ── Channel mask ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a bitmask for a single MIDI channel (0–15) for use with
    /// <see cref="Native.Pm_SetChannelMask"/>.
    /// OR multiple calls together to allow several channels simultaneously.
    /// </summary>
    /// <example>
    /// <code>
    /// // Allow only channels 1 and 10 (0-indexed: 0 and 9)
    /// Native.Pm_SetChannelMask(stream, PMUtil.ChannelMask(0) | PMUtil.ChannelMask(9));
    /// </code>
    /// </example>
    public static int ChannelMask(int channel) => 1 << channel;
}

