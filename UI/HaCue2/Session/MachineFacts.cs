using HaCue2.Core.Validation;
using HaCue2.Machine;

namespace HaCue2.Session;

/// <summary>
/// Everything the app knows about the box it is running on.
/// </summary>
/// <remarks>
/// <para>
/// One object rather than several constructor parameters, because these travel together and because
/// there has to be a single obvious answer to "what does this machine actually contribute". Today
/// that is the media probe and the audio-device list; the running session joins them in Phase 5.
/// </para>
/// <para>
/// <see cref="Nothing"/> is the version for a preview, a test, or a headless capture: it probes files
/// (which needs no hardware) and reports every device as Unknown. It is a real object, not a null -
/// the difference between "no devices" and "nobody looked" is the whole point of the seam, and a null
/// would collapse them.
/// </para>
/// </remarks>
public sealed class MachineFacts
{
    public MachineFacts(AudioDevices? devices = null, MediaFactsCache? media = null)
    {
        Devices = devices;
        Media = media ?? new MediaFactsCache();
        Environment = devices is null ? null : new MachineEnvironment(devices);
    }

    /// <summary>No hardware asked. Files are still probed; devices report Unknown.</summary>
    public static MachineFacts Nothing { get; } = new();

    /// <summary>Null when no backend was supplied - every device answer is then Unknown.</summary>
    public AudioDevices? Devices { get; }

    public MediaFactsCache Media { get; }

    /// <summary>The status pass's machine half, or null to fall back to the runtime's own answers.</summary>
    public IProjectEnvironment? Environment { get; }

    /// <summary>What screen 08 lists: the output devices this box actually has.</summary>
    public IReadOnlyList<string> OutputDeviceNames =>
        Devices is null ? [] : [.. Devices.Outputs.Select(device => device.Name)];

    /// <summary>Whether anything could be enumerated at all - "not checked" versus an answer.</summary>
    public bool DevicesEnumerated => Devices is { Enumerated: true };
}
