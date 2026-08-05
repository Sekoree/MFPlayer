using HaCue2.Core.Model;
using HaCue2.Core.Validation;

namespace HaCue2.Session;

/// <summary>
/// Combines facts available before the engine starts with the live presentation facts held by the shell.
/// </summary>
/// <remarks>
/// Local audio enumeration belongs to the machine layer; video outputs belong to the window/runtime.
/// Keeping those answers separate avoids discarding screen knowledge merely because audio enumeration
/// succeeded, while preserving <see cref="DeviceAvailability.Unknown"/> for device kinds the machine
/// layer deliberately cannot inspect.
/// </remarks>
public sealed class ShellProjectEnvironment(
    IProjectEnvironment? machine,
    IProjectEnvironment runtime) : IProjectEnvironment
{
    public bool MediaExists(string resolvedPath) =>
        machine?.MediaExists(resolvedPath) ?? runtime.MediaExists(resolvedPath);

    public DeviceAvailability AudioLine(AudioLineDefinition line) =>
        machine?.AudioLine(line) ?? runtime.AudioLine(line);

    public DeviceAvailability VideoOutput(VideoOutputDefinition output) =>
        runtime.VideoOutput(output);
}
