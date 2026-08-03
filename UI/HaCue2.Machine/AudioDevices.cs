using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using S.Media.Core.Audio;

namespace HaCue2.Machine;

/// <summary>
/// The audio output devices this machine has, and whether a show's line matches one.
/// </summary>
/// <remarks>
/// The backend is INJECTED rather than chosen here: PortAudio and miniaudio see different devices on
/// the same box, and which one a show should be checked against is the app's decision, not this
/// library's. Passing none is legitimate and answers <see cref="DeviceAvailability.Unknown"/> for
/// everything — the same honest blank a headless check gives.
/// </remarks>
public sealed class AudioDevices
{
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;

    public AudioDevices(params IAudioBackend[] backends)
    {
        var found = new List<AudioDeviceInfo>();
        Enumerated = false;

        foreach (var backend in backends ?? [])
        {
            try
            {
                found.AddRange(backend.EnumerateOutputDevices());
                // Enumerating at all is what makes an answer possible. One backend succeeding is
                // enough: a device the other backend cannot see is still on the machine.
                Enumerated = true;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A backend that will not start is not an absent device — it is a machine nobody could
                // ask, which stays Unknown rather than turning every line red.
            }
        }

        _devices = found;
    }

    /// <summary>Whether anything could be enumerated. False means every answer is Unknown.</summary>
    public bool Enumerated { get; }

    /// <summary>Every output device found, in backend order.</summary>
    public IReadOnlyList<AudioDeviceInfo> Outputs => _devices;

    /// <summary>
    /// The driver families the devices came from, in the order they were enumerated.
    /// </summary>
    /// <remarks>
    /// Empty for a backend with no such concept, which is why a caller must treat "no host APIs" as
    /// "do not offer the filter" rather than "no devices". On a typical Linux box this is ALSA, JACK
    /// and OSS, and the SAME interface appears under two of them with different names — which is the
    /// whole reason a picker needs to group by it.
    /// </remarks>
    public IReadOnlyList<string> HostApis =>
        [.. _devices.Select(device => device.HostApi).Where(name => name.Length > 0).Distinct()];

    /// <summary>The outputs belonging to one driver family, or all of them when it is empty.</summary>
    public IReadOnlyList<AudioDeviceInfo> OutputsFor(string hostApi) =>
        hostApi.Length == 0
            ? _devices
            : [.. _devices.Where(device =>
                string.Equals(device.HostApi, hostApi, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Whether a line's device hint matches something here.
    /// </summary>
    /// <remarks>
    /// The hint is deliberately a HINT and not an identity (see <see cref="AudioLineDefinition"/>), so
    /// the match is by name, case-insensitively, and accepts a substring in either direction — device
    /// names pick up and lose suffixes between driver versions ("Scarlett 18i20 USB" vs
    /// "Scarlett 18i20"). A line with an empty hint means "the default device", which always exists
    /// once anything was enumerated.
    /// </remarks>
    public DeviceAvailability Match(AudioLineDefinition line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!Enumerated)
            return DeviceAvailability.Unknown;

        if (line.DeviceHint.Length == 0)
            return _devices.Count > 0 ? DeviceAvailability.Present : DeviceAvailability.Absent;

        return _devices.Any(device => Matches(device.Name, line.DeviceHint))
            ? DeviceAvailability.Present
            : DeviceAvailability.Absent;
    }

    private static bool Matches(string deviceName, string hint) =>
        deviceName.Contains(hint, StringComparison.OrdinalIgnoreCase)
        || hint.Contains(deviceName, StringComparison.OrdinalIgnoreCase);
}
