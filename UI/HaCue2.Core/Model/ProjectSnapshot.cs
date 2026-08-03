using HaCue2.Core.Serialization;

namespace HaCue2.Core.Model;

/// <summary>Creates a detached, serialization-equivalent project for background/runtime readers.</summary>
public static class ProjectSnapshot
{
    /// <summary>
    /// Copies through the project format so the runtime sees exactly what a save/reopen would see,
    /// including every polymorphic cue kind, without sharing mutable lists with the editor.
    /// </summary>
    public static HaCueProject Copy(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));
    }
}
