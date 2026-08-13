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
/// everything - the same honest blank a headless check gives.
/// </remarks>
public sealed class AudioDevices
{
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;
    private readonly IReadOnlyList<AudioDeviceInfo> _inputs;

    public AudioDevices(params IAudioBackend[] backends)
    {
        var found = new List<AudioDeviceInfo>();
        var captures = new List<AudioDeviceInfo>();
        Enumerated = false;

        foreach (var backend in backends ?? [])
        {
            if (backend is IAudioDeviceSnapshotProvider snapshot)
            {
                try
                {
                    var devices = snapshot.EnumerateDevices();
                    found.AddRange(devices.Outputs);
                    captures.AddRange(devices.Inputs);
                    Enumerated = true;
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    // One failed native catalog is unknown, not an assertion that no devices exist.
                }

                continue;
            }

            try
            {
                found.AddRange(backend.EnumerateOutputDevices());
                // Enumerating at all is what makes an answer possible. One backend succeeding is
                // enough: a device the other backend cannot see is still on the machine.
                Enumerated = true;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A backend that will not start is not an absent device - it is a machine nobody could
                // ask, which stays Unknown rather than turning every line red.
            }

            try
            {
                captures.AddRange(backend.EnumerateInputDevices());
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Separately, because a backend can list outputs and refuse inputs - a box with no
                // capture hardware at all is one of them, and that must not blank the output list.
            }
        }

        _devices = found;
        _inputs = captures;
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
    /// and OSS, and the SAME interface appears under two of them with different names - which is the
    /// whole reason a picker needs to group by it.
    /// </remarks>
    public IReadOnlyList<string> HostApis =>
        [.. _devices.Select(device => device.HostApi).Where(name => name.Length > 0).Distinct()];

    /// <summary>
    /// Every CAPTURE device found - what a live-input cue plays.
    /// </summary>
    /// <remarks>
    /// A separate list rather than a flag on the other one: a device is an input, an output, or both,
    /// and a picker that offered the outputs would let an operator point a cue at the speakers.
    /// </remarks>
    public IReadOnlyList<AudioDeviceInfo> Inputs => _inputs;

    /// <summary>The driver families the CAPTURE devices came from. Same discriminator, different list.</summary>
    public IReadOnlyList<string> InputHostApis =>
        [.. _inputs.Select(device => device.HostApi).Where(name => name.Length > 0).Distinct()];

    /// <summary>The inputs belonging to one driver family, or all of them when it is empty.</summary>
    public IReadOnlyList<AudioDeviceInfo> InputsFor(string hostApi) =>
        hostApi.Length == 0
            ? _inputs
            : [.. _inputs.Where(device =>
                string.Equals(device.HostApi, hostApi, StringComparison.OrdinalIgnoreCase))];

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
    /// the match is by name, case-insensitively, and accepts a substring in either direction - device
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

    /// <summary>
    /// The BACKEND ID for a line's device hint, or null to take the backend's own default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hint is a NAME and a backend wants its own id - PortAudio's is a global device index, and
    /// handing it "Scarlett 2i2 3rd Gen Pro" makes it refuse the line outright. Every device on the
    /// rig then fails to open, the bay ends up with no clock master, and the first cue throws.
    /// </para>
    /// <para>
    /// It resolves through <see cref="Matches"/>, the same rule <see cref="Match"/> reports presence
    /// with, so a line the status pass calls PRESENT is one this can actually open. Two rules here
    /// would mean a green row and a silent output.
    /// </para>
    /// </remarks>
    public static string? DeviceIdFor(IReadOnlyList<AudioDeviceInfo> devices, string hint)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (hint.Length == 0)
            return null;

        // An exact name first: two devices can each contain the other's name as a substring, and the
        // one the operator picked from the list is the one they meant.
        var exact = devices.FirstOrDefault(device =>
            string.Equals(device.Name, hint, StringComparison.OrdinalIgnoreCase));

        return exact?.Id
               ?? devices.FirstOrDefault(device => Matches(device.Name, hint))?.Id;
    }

    private static bool Matches(string deviceName, string hint) =>
        deviceName.Contains(hint, StringComparison.OrdinalIgnoreCase)
        || hint.Contains(deviceName, StringComparison.OrdinalIgnoreCase);
}
