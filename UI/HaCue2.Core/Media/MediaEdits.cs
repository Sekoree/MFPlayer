using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;

namespace HaCue2.Core.Media;

/// <summary>How relink should look for a moved file.</summary>
public enum RelinkStrategy
{
    /// <summary>Same filename anywhere under the new root. Survives a reorganised media tree.</summary>
    ByFileName,

    /// <summary>Same relative sub-path under the new root. Survives a moved root, keeps structure.</summary>
    BySubPath,
}

/// <summary>What a relink or consolidate did, and what it could not do.</summary>
/// <remarks>
/// The unresolved list is the point. A relink that quietly fixed nine of ten files and said "done"
/// produces a show that fails on the tenth cue, mid-performance, with no record of which one.
/// </remarks>
public sealed record MediaEditResult(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Unresolved)
{
    public bool IsComplete => Unresolved.Count == 0;
}

/// <summary>Copying media, so consolidate can be tested without touching a real disk.</summary>
public interface IMediaStore
{
    bool Exists(string path);

    /// <summary>Files under a directory, recursively, for a by-filename relink search.</summary>
    IEnumerable<string> Enumerate(string directory);

    /// <summary>Copies a file, creating the destination directory. Returns false if it could not.</summary>
    bool Copy(string sourcePath, string destinationPath);
}

/// <summary>The real filesystem.</summary>
public sealed class FileSystemMediaStore : IMediaStore
{
    public static FileSystemMediaStore Instance { get; } = new();

    public bool Exists(string path) => File.Exists(path);

    public IEnumerable<string> Enumerate(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            : [];

    public bool Copy(string sourcePath, string destinationPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Reported to the caller, not thrown: a consolidate that aborts on the first unreadable
            // file leaves the operator with a half-copied folder and no list of what is in it.
            return false;
        }
    }
}

/// <summary>
/// Relink and consolidate — the two things "fixing the media" usually means.
/// </summary>
/// <remarks>
/// Both are single journaled commands, so each is one reviewable diff and one ⌘Z. A relink that landed
/// as forty separate undo steps would be impossible to back out of once the operator saw it had
/// matched the wrong take.
/// </remarks>
public static class MediaEdits
{
    /// <summary>
    /// Rebinds media that no longer resolves, against a new root.
    /// </summary>
    /// <remarks>
    /// Only MISSING references are touched. Rewriting paths that already resolve would relink files
    /// nobody asked about — and on a machine where the old root is still mounted, would silently move
    /// the show onto a different copy of the same media.
    /// </remarks>
    public static MediaEditResult Relink(
        ProjectJournal journal,
        string newRoot,
        RelinkStrategy strategy,
        string? projectPath = null,
        IMediaStore? store = null)
    {
        store ??= FileSystemMediaStore.Instance;
        var project = journal.Project;

        var missing = MediaPaths.ReferencesIn(project)
            .Where(reference => !store.Exists(MediaPaths.Resolve(project, reference.Path, projectPath)))
            .ToList();

        if (missing.Count == 0)
            return new MediaEditResult([], []);

        // One index of the new root, reused for every reference: a by-filename search per missing file
        // would walk the tree once per cue, which on a show with 400 cues is a visible stall.
        var byName = strategy == RelinkStrategy.ByFileName
            ? store.Enumerate(newRoot)
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase)
            : [];

        var changed = new List<string>();
        var unresolved = new List<string>();

        using (journal.Composite($"relink {missing.Count} media file{(missing.Count == 1 ? "" : "s")}", "cues"))
            foreach (var reference in missing)
            {
                var candidate = strategy switch
                {
                    RelinkStrategy.ByFileName =>
                        byName.GetValueOrDefault(Path.GetFileName(reference.Path) ?? ""),
                    _ => Path.GetFullPath(Path.Combine(newRoot, reference.Path)),
                };

                if (candidate is null || !store.Exists(candidate))
                {
                    unresolved.Add($"{reference.Describe} — {reference.Path}");
                    continue;
                }

                journal.Do(new RewriteMediaPathCommand(
                    reference,
                    MediaPaths.Store(project, candidate, projectPath),
                    $"relink {reference.Describe}"));
                changed.Add($"{reference.Describe} → {candidate}");
            }

        return new MediaEditResult(changed, unresolved);
    }

    /// <summary>
    /// Copies every referenced media file into one folder and rewrites the references to it.
    /// </summary>
    /// <remarks>
    /// So a show transports as one directory. What it cannot copy is REPORTED and left pointing where
    /// it already pointed — the alternative is a project that looks consolidated and half-works at the
    /// venue, which is the worst of both.
    /// </remarks>
    public static MediaEditResult Consolidate(
        ProjectJournal journal,
        string targetDirectory,
        string? projectPath = null,
        IMediaStore? store = null)
    {
        store ??= FileSystemMediaStore.Instance;
        var project = journal.Project;

        var references = MediaPaths.ReferencesIn(project);
        var changed = new List<string>();
        var unresolved = new List<string>();

        // Distinct destination names: two cues can legitimately reference "loop.wav" from different
        // folders, and flattening them into one directory would silently make the second play the
        // first's audio.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (journal.Composite($"consolidate {references.Count} media file{(references.Count == 1 ? "" : "s")}", "cues"))
            foreach (var reference in references)
            {
                var source = MediaPaths.Resolve(project, reference.Path, projectPath);

                if (!store.Exists(source))
                {
                    unresolved.Add($"{reference.Describe} — {reference.Path} was not found");
                    continue;
                }

                var name = UniqueName(Path.GetFileName(source), taken);
                var destination = Path.Combine(targetDirectory, name);

                if (!store.Copy(source, destination))
                {
                    unresolved.Add($"{reference.Describe} — could not copy {reference.Path}");
                    continue;
                }

                journal.Do(new RewriteMediaPathCommand(
                    reference, name, $"consolidate {reference.Describe}"));
                changed.Add(name);
            }

        return new MediaEditResult(changed, unresolved);
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        if (taken.Add(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem}-{suffix}{extension}";
            if (taken.Add(candidate))
                return candidate;
        }
    }
}

/// <summary>Points one media reference somewhere else, and back again.</summary>
internal sealed class RewriteMediaPathCommand : IProjectCommand
{
    private readonly MediaReference _reference;
    private readonly string _after;
    private string _before = "";

    public RewriteMediaPathCommand(MediaReference reference, string newPath, string description)
    {
        _reference = reference;
        _after = newPath;
        Description = description;
        Domain = "cues";
    }

    public string Description { get; }
    public string Domain { get; }

    public void Apply(HaCueProject project)
    {
        _before = _reference.Path;
        MediaPaths.Rewrite(project, _reference, _after);
    }

    public void Revert(HaCueProject project) => MediaPaths.Rewrite(project, _reference, _before);
}
