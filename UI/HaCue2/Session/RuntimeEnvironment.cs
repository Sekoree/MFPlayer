using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;

namespace HaCue2.Session;

/// <summary>
/// Answers the status pass's questions from what the shell currently knows about this machine.
/// </summary>
/// <remarks>
/// The adapter between <see cref="ShowRuntime"/> and the model's <see cref="IProjectEnvironment"/>.
/// It exists so <c>HaCue2.Core</c> never has to know what a session is, and so the same status pass
/// runs identically here and under <c>hacue2-check</c> — only the answers differ.
/// </remarks>
public sealed class RuntimeEnvironment(
    ShowRuntime runtime,
    HaCueProject project,
    Func<string?>? projectPath = null) : IProjectEnvironment
{
    /// <summary>
    /// Media presence comes from the runtime's broken set rather than from the disk.
    /// </summary>
    /// <remarks>
    /// In the shell the sample runtime decides; once media probing is real this reads the probe's
    /// result. Either way it is a machine answer, which is why it does not touch <c>File.Exists</c>
    /// here — a shell that hit the filesystem per row would stall the cue list on a slow mount.
    /// </remarks>
    public bool MediaExists(string resolvedPath) =>
        !MediaPaths.ReferencesIn(project)
            .Where(reference => runtime.Broken.Contains(Guid.TryParse(reference.SubjectId, out var id) ? id : Guid.Empty))
            .Any(reference => MediaPaths.Resolve(project, reference.Path, projectPath?.Invoke()) == resolvedPath);

    public DeviceAvailability AudioLine(AudioLineDefinition line) =>
        runtime.AbsentLines.Contains(line.Id) ? DeviceAvailability.Absent : DeviceAvailability.Present;

    public DeviceAvailability VideoOutput(VideoOutputDefinition output) =>
        runtime.AbsentVideoOutputs.Contains(output.Id)
            ? DeviceAvailability.Absent
            : DeviceAvailability.Present;
}
