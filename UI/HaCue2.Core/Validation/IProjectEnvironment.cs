using HaCue2.Core.Model;

namespace HaCue2.Core.Validation;

/// <summary>
/// Whether a piece of hardware is here.
/// </summary>
/// <remarks>
/// <b><see cref="Unknown"/> is the important member.</b> A status pass run where devices cannot be
/// enumerated — a CI box, a headless check over a fixture project — must say "not checked", never
/// "absent". Reporting an interface as missing because nobody looked is the failure mode that trains
/// operators to ignore the whole screen, and it is the same reason the audio bay's terminal states
/// deliberately have no "absent": device presence is a host fact, and inventing it is wrong exactly
/// when it matters.
/// </remarks>
public enum DeviceAvailability
{
    /// <summary>Nobody could look. Reported as unchecked, never as a fault.</summary>
    Unknown,

    Present,

    Absent,
}

/// <summary>
/// What the status pass needs to ask the machine.
/// </summary>
/// <remarks>
/// Injected rather than called directly so the pass is testable without hardware, and so
/// <c>HaCue2.Core</c> keeps no dependency on PortAudio, NDI or a windowing system — which is what
/// lets the whole check run from a script.
/// </remarks>
public interface IProjectEnvironment
{
    /// <summary>Whether a resolved media path exists.</summary>
    bool MediaExists(string resolvedPath);

    /// <summary>Whether this machine has the line the show wants.</summary>
    DeviceAvailability AudioLine(AudioLineDefinition line);

    /// <summary>Whether this machine has the screen or sink the show wants.</summary>
    DeviceAvailability VideoOutput(VideoOutputDefinition output);
}

/// <summary>
/// Answers about files truthfully and about devices honestly: it does not know, and says so.
/// </summary>
/// <remarks>
/// The default for a headless run. The app substitutes an implementation backed by real device
/// enumeration; everything else — CI, a fixture check, a project opened on a laptop to do paperwork —
/// gets the media half checked and the device half explicitly unchecked.
/// </remarks>
public sealed class FileSystemEnvironment : IProjectEnvironment
{
    public static FileSystemEnvironment Instance { get; } = new();

    public bool MediaExists(string resolvedPath) =>
        !string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath);

    public DeviceAvailability AudioLine(AudioLineDefinition line) => DeviceAvailability.Unknown;

    public DeviceAvailability VideoOutput(VideoOutputDefinition output) => DeviceAvailability.Unknown;
}
