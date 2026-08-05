namespace HaCue2.Machine;

/// <summary>
/// The derived files a show accumulates, and getting rid of them.
/// </summary>
/// <remarks>
/// <para>
/// Waveforms and prepared YouTube assets are re-derivable from the media, which is what makes clearing
/// them safe and what makes it worth offering: they are the one thing on a booth machine that grows
/// without bound and that nobody would miss.
/// </para>
/// <para>
/// The sizes are MEASURED. The settings pane used to state invented waveform/probe/thumbnail sizes, so
/// it read the same on a machine with an empty cache and on one with a full disk — exactly the situation
/// somebody opens that pane to find out about. Probe facts remain in memory and curve thumbnails are
/// drawn controls, so neither is advertised here as a disk cache.
/// </para>
/// </remarks>
public static class MediaCache
{
    /// <summary>The folders each kind of derived file lives in, under a cache root.</summary>
    private static readonly (string Kind, string Folder)[] Parts =
    [
        ("waveforms", "waveforms"),
        ("youtube", "youtube"),
    ];

    /// <summary>Where derived files live, honouring a machine's override.</summary>
    public static string RootFor(AppSettings settings)
    {
        var fallback = Path.Combine(StoragePaths.Root, "cache");
        try
        {
            return Path.GetFullPath(settings is { CacheRoot.Length: > 0 }
                ? Environment.ExpandEnvironmentVariables(settings.CacheRoot)
                : fallback);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.GetFullPath(fallback);
        }
    }

    /// <summary>The YouTube preparer's part of the same app cache.</summary>
    public static string YouTubeRootFor(AppSettings settings) => Path.Combine(RootFor(settings), "youtube");

    /// <summary>Parses the human-readable budget fields used by Settings.</summary>
    public static long? ParseBudget(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim().Replace(',', '.');
        var split = trimmed.IndexOfAny([' ', '\t']);
        var number = split < 0 ? new string([.. trimmed.TakeWhile(c => char.IsDigit(c) || c == '.')]) : trimmed[..split];
        var unit = (split < 0 ? trimmed[number.Length..] : trimmed[(split + 1)..]).Trim().ToUpperInvariant();

        if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
            return null;

        var multiplier = unit switch
        {
            "TB" or "TIB" => 1L << 40,
            "GB" or "GIB" => 1L << 30,
            "MB" or "MIB" => 1L << 20,
            "KB" or "KIB" => 1L << 10,
            "B" or "" => 1L,
            _ => 0,
        };

        if (multiplier == 0 || value > long.MaxValue / (double)multiplier)
            return null;

        return (long)Math.Round(value * multiplier);
    }

    /// <summary>What each part of the cache is using, as the settings pane words it.</summary>
    public static string Describe(AppSettings settings)
        => DescribeRoot(RootFor(settings));

    /// <summary>Measures a known active root, even when Settings already points at next launch's root.</summary>
    public static string DescribeRoot(string root)
    {
        var parts = Parts.Select(part => $"{part.Kind} {Size(Bytes(Path.Combine(root, part.Folder)))}");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Deletes one or more parts of the cache.
    /// </summary>
    /// <param name="kinds">Folder names from <see cref="Parts"/>; anything else is ignored.</param>
    /// <returns>What was freed, for the operator, or why nothing was.</returns>
    public static string Clear(AppSettings settings, params string[] kinds)
        => ClearRoot(RootFor(settings), kinds);

    /// <summary>Clears parts beneath an explicit, already-resolved cache root.</summary>
    public static string ClearRoot(string root, params string[] kinds)
    {
        long freed = 0;
        var failures = 0;

        foreach (var folder in kinds.Where(kind => Parts.Any(part => part.Folder == kind)))
        {
            var path = Path.Combine(root, folder);

            if (!Directory.Exists(path))
                continue;

            var before = Bytes(path);

            try
            {
                Directory.Delete(path, recursive: true);
                freed += before;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A file the OS still has open is the ordinary case, not an error worth a dialog: the
                // rest of the cache is still cleared and the count says what actually went.
                failures++;
                freed += Math.Max(0, before - Bytes(path));
            }
        }

        if (freed == 0 && failures == 0)
            return "nothing to clear";

        return failures == 0
            ? $"freed {Size(freed)}"
            : $"freed {Size(freed)} · {failures} folder(s) still in use";
    }

    /// <summary>Evicts oldest completed files until a derived-data folder fits its configured cap.</summary>
    public static void EnforceBudget(string cacheRoot, string kind, long? maxBytes, string? keep = null)
    {
        if (maxBytes is not > 0 || !Parts.Any(part => part.Folder == kind))
            return;

        var folder = Path.Combine(Path.GetFullPath(cacheRoot), kind);
        if (!Directory.Exists(folder))
            return;

        try
        {
            var keepPath = keep is null ? null : Path.GetFullPath(keep);
            var files = new DirectoryInfo(folder)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                               && !file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            var bytes = files.Sum(file => file.Length);

            foreach (var file in files)
            {
                if (bytes <= maxBytes)
                    break;
                if (keepPath is not null && string.Equals(file.FullName, keepPath, StringComparison.Ordinal))
                    continue;

                var length = file.Length;
                try
                {
                    file.Delete();
                    bytes -= length;
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // Another scan may still be committing the file. A later write retries cleanup.
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Cache policy must never make media authoring fail.
        }
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
