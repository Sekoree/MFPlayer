using HaCue2.Core.Model;

namespace HaCue2.Core.Media;

/// <summary>One media reference in a project, and the thing that holds it.</summary>
/// <param name="Path">As stored in the document — relative to the media root, or absolute.</param>
/// <param name="SubjectKind">What holds it, for the status view's navigation target.</param>
/// <param name="SubjectId">That thing's id.</param>
/// <param name="Describe">How to name it to an operator: "Q15 Interval music".</param>
public sealed record MediaReference(string Path, string SubjectKind, string SubjectId, string Describe);

/// <summary>
/// Resolving the document's media paths against the project's media root.
/// </summary>
/// <remarks>
/// Paths are stored RELATIVE to the media root where they can be, so a show consolidated into one
/// directory transports without rewriting. Absolute paths are legal too — register item 26 allows
/// media outside the root, warns when one is added, and offers move/copy rather than refusing it.
/// A project that could only reference files it owned would make "add the band's stem from the USB
/// stick" impossible five minutes before doors.
/// </remarks>
public static class MediaPaths
{
    /// <summary>Turns a stored path into one the filesystem can answer for.</summary>
    /// <param name="projectPath">The <c>.hacue2proj</c> file, or null when it has never been saved.</param>
    public static string Resolve(HaCueProject project, string storedPath, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || Path.IsPathRooted(storedPath))
            return storedPath;

        var root = RootOf(project, projectPath);
        return root is null ? storedPath : Path.GetFullPath(Path.Combine(root, storedPath));
    }

    /// <summary>Stores a path relative to the media root when it lives under it, absolute otherwise.</summary>
    public static string Store(HaCueProject project, string absolutePath, string? projectPath)
    {
        var root = RootOf(project, projectPath);
        if (root is null)
            return absolutePath;

        var relative = Path.GetRelativePath(root, absolutePath);

        // "../.." means the file is outside the root. Keeping it relative would make the reference
        // depend on where the project file happens to sit, which is exactly what breaks on transport.
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? absolutePath
            : relative;
    }

    /// <summary>
    /// The media root as an absolute directory, or null when the project has neither a root nor a home.
    /// </summary>
    public static string? RootOf(HaCueProject project, string? projectPath)
    {
        var root = project.Settings.MediaRoot;

        if (!string.IsNullOrWhiteSpace(root))
            return Path.IsPathRooted(root)
                ? root
                : projectPath is null ? null : Path.GetFullPath(Path.Combine(Directory(projectPath), root));

        // No configured root: a saved project's own directory is the sensible default, because that is
        // where a consolidated show puts its media.
        return projectPath is null ? null : Directory(projectPath);
    }

    /// <summary>Every media file the project refers to, wherever it is held.</summary>
    public static IReadOnlyList<MediaReference> ReferencesIn(HaCueProject project)
    {
        var found = new List<MediaReference>();

        foreach (var cue in project.AllCues().OfType<MediaCueNode>())
            if (!string.IsNullOrWhiteSpace(cue.MediaPath))
                found.Add(new MediaReference(cue.MediaPath, "cue", cue.Id.ToString(),
                    $"Q{cue.Number} {cue.Label}".TrimEnd()));

        foreach (var composition in project.Compositions)
            if (!string.IsNullOrWhiteSpace(composition.IdleImagePath))
                found.Add(new MediaReference(composition.IdleImagePath, "composition",
                    composition.Id.ToString(), $"“{composition.Name}” idle image"));

        foreach (var output in project.VideoOutputs)
            if (!string.IsNullOrWhiteSpace(output.IdleFallbackPath))
                found.Add(new MediaReference(output.IdleFallbackPath, "videoOutput",
                    output.Id.ToString(), $"“{output.Name}” idle fallback"));

        return found;
    }

    /// <summary>Rewrites whichever reference the given subject holds. Used by relink and consolidate.</summary>
    internal static bool Rewrite(HaCueProject project, MediaReference reference, string newPath)
    {
        switch (reference.SubjectKind)
        {
            case "cue" when project.FindCue(Guid.Parse(reference.SubjectId)) is MediaCueNode cue:
                cue.MediaPath = newPath;
                return true;

            case "composition":
                var composition = project.Compositions
                    .FirstOrDefault(item => item.Id.ToString() == reference.SubjectId);
                if (composition is null)
                    return false;
                composition.IdleImagePath = newPath;
                return true;

            case "videoOutput":
                var output = project.VideoOutputs
                    .FirstOrDefault(item => item.Id.ToString() == reference.SubjectId);
                if (output is null)
                    return false;
                output.IdleFallbackPath = newPath;
                return true;

            default:
                return false;
        }
    }

    private static string Directory(string projectPath) =>
        Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
}
