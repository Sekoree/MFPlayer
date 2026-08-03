namespace HaCue2.Machine;

/// <summary>
/// The derived files a show accumulates, and getting rid of them.
/// </summary>
/// <remarks>
/// <para>
/// Waveforms, probe results and thumbnails are all re-derivable from the media, which is what makes
/// clearing them safe and what makes it worth offering: they are the one thing on a booth machine that
/// grows without bound and that nobody would miss.
/// </para>
/// <para>
/// The sizes are MEASURED. The settings pane used to state "waveforms 1.2 GB · probes 44 MB ·
/// thumbnails 180 MB" as a constant, so it read the same on a machine with an empty cache and on one
/// with a full disk — which is exactly the situation somebody opens that pane to find out about.
/// </para>
/// </remarks>
public static class MediaCache
{
    /// <summary>The folders each kind of derived file lives in, under a cache root.</summary>
    private static readonly (string Kind, string Folder)[] Parts =
    [
        ("waveforms", "waveforms"),
        ("probes", "probes"),
        ("thumbnails", "thumbnails"),
    ];

    /// <summary>Where derived files live, honouring a machine's override.</summary>
    public static string RootFor(AppSettings settings) =>
        settings is { CacheRoot.Length: > 0 } ? settings.CacheRoot : Path.Combine(StoragePaths.Root, "cache");

    /// <summary>What each part of the cache is using, as the settings pane words it.</summary>
    public static string Describe(AppSettings settings)
    {
        var root = RootFor(settings);

        var parts = Parts.Select(part => $"{part.Kind} {Size(Bytes(Path.Combine(root, part.Folder)))}");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Deletes one or more parts of the cache.
    /// </summary>
    /// <param name="kinds">Folder names from <see cref="Parts"/>; anything else is ignored.</param>
    /// <returns>What was freed, for the operator, or why nothing was.</returns>
    public static string Clear(AppSettings settings, params string[] kinds)
    {
        var root = RootFor(settings);
        long freed = 0;
        var failures = 0;

        foreach (var folder in kinds.Where(kind => Parts.Any(part => part.Folder == kind)))
        {
            var path = Path.Combine(root, folder);

            if (!Directory.Exists(path))
                continue;

            freed += Bytes(path);

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A file the OS still has open is the ordinary case, not an error worth a dialog: the
                // rest of the cache is still cleared and the count says what actually went.
                failures++;
            }
        }

        if (freed == 0 && failures == 0)
            return "nothing to clear";

        return failures == 0
            ? $"freed {Size(freed)}"
            : $"freed {Size(freed)} · {failures} folder(s) still in use";
    }

    /// <summary>Bytes under a folder, treating an unreadable one as empty rather than throwing.</summary>
    private static long Bytes(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Measuring a cache must never be the thing that takes the settings pane down.
            return 0;
        }
    }

    /// <summary>A byte count as the pane words it.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} kB",
        _ => $"{bytes} B",
    };
}
