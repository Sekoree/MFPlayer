using HaCue2.Core.Model;
using HaCue2.Core.Validation;

namespace HaCue2.Machine;

/// <summary>
/// The status pass's questions, answered by asking this machine.
/// </summary>
/// <remarks>
/// The real counterpart to <see cref="FileSystemEnvironment"/>, which answers about files truthfully
/// and about devices as Unknown. Substituting this is what turns three rows of screen 14 from "not
/// checked" into an answer.
/// <para>
/// Video outputs stay Unknown for now: screen enumeration needs a windowing system, and this library
/// is deliberately reachable from a console. Saying so beats guessing — an output reported present
/// because nobody looked is the failure this whole seam exists to avoid.
/// </para>
/// </remarks>
public sealed class MachineEnvironment(AudioDevices devices) : IProjectEnvironment
{
    public AudioDevices Devices { get; } = devices;

    public bool MediaExists(string resolvedPath) =>
        FileSystemEnvironment.Instance.MediaExists(resolvedPath);

    public DeviceAvailability AudioLine(AudioLineDefinition line) => line.Kind switch
    {
        // Only host audio devices are enumerable here. An NDI sender or a record file is reachable or
        // not for reasons this class cannot see, so it does not pretend to know.
        AudioLineKind.LocalAudio => Devices.Match(line),
        _ => DeviceAvailability.Unknown,
    };

    public DeviceAvailability VideoOutput(VideoOutputDefinition output) => DeviceAvailability.Unknown;
}
