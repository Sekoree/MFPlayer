using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;

namespace HaCue2.Machine;

/// <summary>What an autosave knows about the project it came from.</summary>
/// <param name="OriginalPath">Where the project itself lives. Empty for a show never saved to disk.</param>
/// <param name="Edits">How many unsaved edits the journal held when this copy was written.</param>
public sealed record RecoveryMeta
{
    public string OriginalPath { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public int Edits { get; set; }
}

/// <summary>An autosave that is newer than the project file it belongs to.</summary>
/// <param name="CopyPath">The autosaved document, ready to load.</param>
public sealed record RecoveryCandidate(
    string CopyPath, string OriginalPath, string Title, DateTimeOffset SavedAt, int Edits)
{
    /// <summary>The banner's sentence — what was found, and how much is at stake.</summary>
    public string Notice =>
        $"{Title} has an autosave newer than its file ({SavedAt.ToLocalTime():HH:mm}, "
        + $"+{Edits} edit{(Edits == 1 ? "" : "s")})";
}

/// <summary>
/// Autosave copies, and the question the launcher asks about them.
/// </summary>
/// <remarks>
/// <para>
/// One directory per project, named from a hash of its path so it is stable across sessions and
/// contains no characters a filesystem objects to. Inside it, timestamped copies and one
/// <c>meta.json</c> describing the newest.
/// </para>
/// <para>
/// <b>An autosave is never written over the project itself.</b> The whole point is to survive a crash
/// between two deliberate saves, so the operator's own file must be exactly what they last chose to
/// write — recovery is an OFFER made at the next launch, never something that happened to their show
/// while they were not looking.
/// </para>
/// <para>
/// Nothing here throws. An autosave that cannot be written is worth reporting and surviving; one that
/// took the show down with it would be worse than not having autosave at all.
/// </para>
/// </remarks>
public static class RecoveryStore
{
    private const string MetaFile = "meta.json";

    /// <summary>
    /// Writes an autosave copy of a project.
    /// </summary>
    /// <param name="keepCopies">
    /// How many historical copies to retain (the project's own recovery-copies setting). Older ones
    /// are pruned oldest-first, so a long rehearsal cannot fill a disk.
    /// </param>
    /// <returns>False when nothing could be written.</returns>
    public static async Task<bool> SaveAsync(
        HaCueProject project,
        string originalPath,
        int edits,
        int keepCopies,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var directory = DirectoryFor(originalPath, project.Title);

        if (!StoragePaths.EnsureDirectory(directory))
            return false;

        try
        {
            // Sortable and second-resolution: the newest copy is the last by name, which is what makes
            // pruning and "which one do we offer" plain string work rather than stat calls.
            var copy = Path.Combine(directory, $"{now.UtcDateTime:yyyyMMdd-HHmmss}{HaCueProjectFile.Extension}");
            await HaCueProjectFile.SaveAsync(project, copy, ct).ConfigureAwait(false);

            var meta = new RecoveryMeta
            {
                OriginalPath = originalPath,
                Title = project.Title,
                SavedAt = now,
                Edits = edits,
            };

            await File.WriteAllTextAsync(
                Path.Combine(directory, MetaFile),
                JsonSerializer.Serialize(meta, RecoveryJsonContext.Default.RecoveryMeta),
                ct).ConfigureAwait(false);

            Prune(directory, Math.Max(1, keepCopies));
            return true;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every autosave that is newer than the project file it belongs to, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Newer than the file" is the whole question. An autosave older than the project means the
    /// operator saved after it was written and there is nothing to recover — offering it anyway would
    /// invite them to overwrite good work with stale work.
    /// </para>
    /// <para>
    /// A project whose file has since been DELETED still counts: the autosave may be all that is left
    /// of it, which is the case where recovery matters most.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RecoveryCandidate> Scan()
    {
        var root = StoragePaths.RecoveryRoot;

        if (!Directory.Exists(root))
            return [];

        var found = new List<RecoveryCandidate>();

        foreach (var directory in SafeDirectories(root))
        {
            if (ReadMeta(directory) is not { } meta)
                continue;

            if (NewestCopy(directory) is not { } copy)
                continue;

            // A saved project whose file is newer than the autosave has nothing outstanding.
            if (meta.OriginalPath.Length > 0
                && File.Exists(meta.OriginalPath)
                && File.GetLastWriteTimeUtc(meta.OriginalPath) >= meta.SavedAt.UtcDateTime)
                continue;

            found.Add(new RecoveryCandidate(
                copy,
                meta.OriginalPath,
                meta.Title.Length > 0 ? meta.Title : Path.GetFileNameWithoutExtension(meta.OriginalPath),
                meta.SavedAt,
                meta.Edits));
        }

        return [.. found.OrderByDescending(candidate => candidate.SavedAt)];
    }

    /// <summary>Forgets a project's autosaves — the DISCARD answer.</summary>
    /// <remarks>
    /// Deletes the whole per-project directory rather than one copy: "discard" means the operator has
    /// decided the file on disk is the truth, and leaving older copies behind would make the banner
    /// reappear at the next launch, which reads as the app ignoring them.
    /// </remarks>
    public static bool Discard(RecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            if (Path.GetDirectoryName(candidate.CopyPath) is { Length: > 0 } directory
                && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);

            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Clears the autosaves for a project the operator has just saved deliberately.</summary>
    public static void Clear(string originalPath, string title)
    {
        try
        {
            var directory = DirectoryFor(originalPath, title);

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A recovery copy that outlives its save is offered once more and then discarded by hand.
            // Failing the SAVE over it would be the wrong way round.
        }
    }

    /// <summary>
    /// The per-project directory: a short hash of the path, plus a readable name.
    /// </summary>
    /// <remarks>
    /// Hashed because a project path contains separators, spaces and characters a directory name
    /// cannot hold; named as well because somebody looking in this folder during a support call should
    /// be able to tell whose autosave is whose without opening any of them.
    /// </remarks>
    private static string DirectoryFor(string originalPath, string title)
    {
        var key = originalPath.Length > 0 ? originalPath : $"untitled:{title}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

        return Path.Combine(StoragePaths.RecoveryRoot, $"{Sanitize(title)}-{hash}");
    }

    private static string Sanitize(string title)
    {
        var safe = new string([
            .. title.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'),
        ]).Trim('-');

        return safe.Length == 0 ? "untitled" : safe[..Math.Min(40, safe.Length)];
    }

    private static void Prune(string directory, int keep)
    {
        var copies = Directory
            .GetFiles(directory, $"*{HaCueProjectFile.Extension}")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

        foreach (var stale in copies)
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A copy that will not delete is a wasted megabyte, not a reason to stop autosaving.
            }
        }
    }

    private static string? NewestCopy(string directory)
    {
        try
        {
            return Directory
                .GetFiles(directory, $"*{HaCueProjectFile.Extension}")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static RecoveryMeta? ReadMeta(string directory)
    {
        try
        {
            var path = Path.Combine(directory, MetaFile);

            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), RecoveryJsonContext.Default.RecoveryMeta)
                : null;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RecoveryMeta))]
internal sealed partial class RecoveryJsonContext : JsonSerializerContext;
