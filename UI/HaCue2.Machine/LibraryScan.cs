namespace HaCue2.Machine;

/// <summary>
/// Finds usable media in a directory tree.
/// </summary>
/// <remarks>
/// Deliberately conservative. A media library is full of things that are not media — editor peak
/// caches, artwork, playlists, project sidecars — and a fixture built by extension alone fills a cue
/// list with files no decoder will open, which teaches the operator that red rows are normal.
/// </remarks>
public static class LibraryScan
{
    /// <summary>Extensions worth putting in a cue. Audio first: these are what a cue list is mostly made of.</summary>
    public static IReadOnlyList<string> AudioExtensions { get; } =
        [".flac", ".wav", ".mp3", ".m4a", ".ogg", ".opus", ".aiff", ".aif"];

    public static IReadOnlyList<string> VideoExtensions { get; } =
        [".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v"];

    /// <summary>
    /// Files under a directory, smallest first, capped by size and count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Smallest first, and size-capped.</b> A library can hold a 119 GB camera master beside a
    /// 3 MB single; probing the master to build a fixture would take minutes and teach nothing the
    /// single does not. The cap is a parameter rather than a constant because "reasonable" depends on
    /// what the fixture is for.
    /// </para>
    /// <para>
    /// Enumerated with a callback for failures rather than throwing: one unreadable subdirectory in a
    /// large library must not lose the whole scan.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Find(
        string directory,
        IReadOnlyList<string> extensions,
        long maxBytes,
        int take,
        long minBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        if (!Directory.Exists(directory))
            return [];

        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var found = new List<(long Size, string Path)>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // A library legitimately contains hidden caches; walking them wastes time and finds
            // nothing playable.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 4,
        };

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", options))
            {
                if (!wanted.Contains(Path.GetExtension(path)))
                    continue;

                try
                {
                    var size = new FileInfo(path).Length;

                    if (size >= minBytes && size <= maxBytes)
                        found.Add((size, path));
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished between the listing and the stat. Skip it.
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return
        [
            .. found
                .OrderBy(entry => entry.Size)
                // Ordinal by path after size, so two runs over an unchanged library produce the same
                // fixture — a generator whose output depends on filesystem ordering is one whose
                // diffs are noise.
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .Take(take)
                .Select(entry => entry.Path),
        ];
    }

    /// <summary>The deepest directory both paths live under, for use as a media root.</summary>
    /// <remarks>
    /// Null when they share nothing — on Windows that is two different volumes, and there is then no
    /// root that makes both relative. The caller stores absolute paths in that case, which the document
    /// allows.
    /// </remarks>
    public static string? CommonRoot(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        string? common = null;

        foreach (var directory in directories)
        {
            var full = Path.GetFullPath(directory);

            if (common is null)
            {
                common = full;
                continue;
            }

            while (!IsUnder(full, common))
            {
                var parent = Path.GetDirectoryName(common);

                if (parent is null || parent == common)
                    return null;

                common = parent;
            }
        }

        return common;
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.Ordinal)
        || path.StartsWith(
            root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
}
