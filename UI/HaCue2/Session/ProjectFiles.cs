using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Machine;

namespace HaCue2.Session;

/// <summary>What happened to a project file, and what to say about it.</summary>
/// <param name="Path">Where it now lives, or empty when nothing was written.</param>
public readonly record struct ProjectFileResult(bool Succeeded, string Path, string Message)
{
    public static ProjectFileResult Cancelled => new(false, "", "");

    public static ProjectFileResult Failed(string message) => new(false, "", message);
}

/// <summary>
/// Opening, saving and creating project files.
/// </summary>
/// <remarks>
/// <para>
/// A thin seam over <see cref="HaCueProjectFile"/> so the view-models never touch the disk directly
/// and so a failure has ONE shape everywhere. Every operation reports rather than throws: a show that
/// cannot be saved is something the operator has to be told about and then keep working in, not an
/// unhandled exception that loses the edits it was trying to protect.
/// </para>
/// <para>
/// Recovery and autosave live in Settings and are deliberately not here — this is the part the
/// operator drives by hand.
/// </para>
/// </remarks>
public static class ProjectFiles
{
    /// <summary>What the file picker should offer.</summary>
    public const string Extension = HaCueProjectFile.Extension;

    /// <summary>
    /// A new project, seeded the way the New-project defaults say (register item 20).
    /// </summary>
    /// <remarks>
    /// Seeded with a Main L/R pair and one cue list, not empty: a genuinely empty project cannot have
    /// a cue added to it without visiting three other screens first, which is a bad first minute.
    /// </remarks>
    public static HaCueProject Create(string title, string mediaRoot = "") =>
        Create(title, mediaRoot, new AppSettings());

    /// <summary>
    /// Creates a project from the operator's application defaults.
    /// </summary>
    /// <remarks>
    /// The machine is deliberately NOT consulted for an output. A new show gets logical outputs, which
    /// are portable, and no audio LINE, which would not be.
    /// </remarks>
    public static HaCueProject Create(string title, string mediaRoot, AppSettings app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var mixRate = Math.Clamp(app.NewProjectMixRate, 8_000, 384_000);
        var project = new HaCueProject
        {
            Title = title,
            Settings = new ProjectSettings
            {
                MediaRoot = mediaRoot,
                DefaultFadeInMs = Math.Max(0, app.NewProjectFadeInMs),
                DefaultFadeOutMs = Math.Max(0, app.NewProjectFadeOutMs),
                AutoRenumberOnInsert = app.AutoRenumberDefault,
                ClickMovesStandby = app.StandbyFollowsClick,
            },
            AudioPatch = new ProjectAudioPatch { MixSampleRate = mixRate },
        };

        var left = new LogicalAudioChannel { Name = "Main L", SortOrder = 0 };
        var right = new LogicalAudioChannel { Name = "Main R", SortOrder = 1 };

        project.AudioPatch.LogicalChannels.Add(left);
        project.AudioPatch.LogicalChannels.Add(right);
        project.AudioPatch.Groups.Add(new OutputGroup
        {
            Name = "Main",
            MemberIds = [left.Id, right.Id],
        });

        // NO AUDIO LINE. Main L/R are what the show CALLS its destinations and are portable; a line is
        // a device on ONE machine, and adopting whatever this laptop happened to have would put the
        // authoring box's sound card into a document that then travels to the venue. The operator
        // patches to the rig they are actually on, which is the one decision this cannot guess.
        project.CueLists.Add(new CueList { Name = "Cue list 1" });

        return project;
    }

    /// <summary>Reads a project, or says why it could not.</summary>
    public static async Task<(HaCueProject? Project, ProjectFileResult Result)> OpenAsync(string path)
    {
        if (path.Length == 0)
            return (null, ProjectFileResult.Cancelled);

        try
        {
            var project = await HaCueProjectFile.LoadAsync(path).ConfigureAwait(false);
            return (project, new ProjectFileResult(true, path, $"opened {Path.GetFileName(path)}"));
        }
        catch (Exception failure) when (
            failure is IOException
                or UnauthorizedAccessException
                or System.Text.Json.JsonException
                // "written by a newer HaCue2" — the version gate, which is a refusal, not a crash.
                or InvalidOperationException)
        {
            // Named exceptions only: an OutOfMemory or a cancellation is not "this file is bad", and
            // swallowing one here would hide a real fault behind a friendly message.
            return (null, ProjectFileResult.Failed($"could not open {Path.GetFileName(path)} — {failure.Message}"));
        }
    }

    /// <summary>Writes a project, or says why it could not.</summary>
    public static async Task<ProjectFileResult> SaveAsync(HaCueProject project, string path)
    {
        if (path.Length == 0)
            return ProjectFileResult.Cancelled;

        try
        {
            await HaCueProjectFile.SaveAsync(project, path).ConfigureAwait(false);
            return new ProjectFileResult(true, path, $"saved {Path.GetFileName(path)}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return ProjectFileResult.Failed($"could not save — {failure.Message}");
        }
    }

    /// <summary>Adds the project's own extension when the operator did not type one.</summary>
    public static string WithExtension(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? path : path + Extension;
}
