namespace S.Media.FFmpeg.Common;

/// <summary>
/// One-time FFmpeg native-binding initialization. Safe to call repeatedly. The first successful call
/// wins; a later disagreeing <paramref name="rootPath"/> is ignored (logged once).
/// </summary>
/// <remarks>
/// FFmpeg.AutoGen 8.x routes every <c>av_*</c> call through a function-pointer table that must be
/// populated up-front (<see cref="DynamicallyLoadedBindings.Initialize"/>); without it every API call
/// throws <see cref="NotSupportedException"/>. On Linux/macOS <see cref="ffmpeg.RootPath"/> is empty so
/// the platform loader finds system libraries. On Windows a complete FFmpeg installation in System32
/// or PATH is selected before the application-local native bundle.
/// <para>
/// This used to also install the old static <c>MediaFrameworkPlugins</c> capability slots; those now go
/// through the media registry in <c>FFmpegModule.Register</c> (P2). This type is just native init.
/// </para>
/// </remarks>
public static class FFmpegRuntime
{
    private static readonly Lock Gate = new();
    private static volatile bool _initialized;
    private static int _ignoredRootPathLogged;
    private static volatile string? _unavailableReason;

    /// <summary>
    /// Null when the native bindings are usable; otherwise an operator-facing explanation of why not.
    /// </summary>
    public static string? UnavailableReason
    {
        get
        {
            TryEnsureInitialized();
            return _unavailableReason;
        }
    }

    /// <summary>Whether FFmpeg calls will actually work. Initializes on first use.</summary>
    public static bool IsAvailable => UnavailableReason is null;

    /// <summary>
    /// Initializes the dynamic bindings, optionally overriding the native lookup path, and throws with a
    /// diagnosable message when the natives are missing or the wrong ABI major.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The bindings loaded no usable native FFmpeg. The message names the required and the found sonames.
    /// </exception>
    public static void EnsureInitialized(string? rootPath = null)
    {
        TryEnsureInitialized(rootPath);
        ThrowIfUnavailable();
    }

    /// <summary>
    /// Initializes without throwing, for callers that must survive a machine with no usable FFmpeg
    /// (module registration, capability probes). Check <see cref="IsAvailable"/> afterwards.
    /// </summary>
    public static void TryEnsureInitialized(string? rootPath = null)
    {
        if (_initialized)
        {
            MaybeLogIgnoredRootPath(rootPath);
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                MaybeLogIgnoredRootPath(rootPath);
                return;
            }

            ffmpeg.RootPath = rootPath ?? ResolveDefaultRootPath();

            DynamicallyLoadedBindings.Initialize();
            _initialized = true;
            _unavailableReason = ProbeBindings();

            if (_unavailableReason is not null)
                MediaDiagnostics.LogWarning("FFmpegRuntime: {0}", _unavailableReason);
        }
    }

    /// <summary>Throws when the natives are unusable; no-op otherwise.</summary>
    public static void ThrowIfUnavailable()
    {
        if (_unavailableReason is { } reason)
            throw new InvalidOperationException(reason);
    }

    /// <summary>
    /// Calls one trivial FFmpeg function to find out whether the bindings actually resolved.
    /// </summary>
    /// <remarks>
    /// <see cref="DynamicallyLoadedBindings.Initialize"/> does NOT fail when the natives are absent or the
    /// wrong ABI major - it populates a function-pointer table lazily and leaves the entries throwing
    /// <see cref="NotSupportedException"/>. Every call site then dies with "Specified method is not
    /// supported", which names nothing: no library, no version, no path. That message is how a machine
    /// with the wrong FFmpeg ends up looking like a project full of missing media, because a probe that
    /// throws is indistinguishable from a file that will not open. One cheap call here converts that into
    /// a statement of what is wrong.
    /// </remarks>
    private static string? ProbeBindings()
    {
        try
        {
            _ = ffmpeg.av_version_info();
            return null;
        }
        catch (Exception ex) when (ex is NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return DescribeMissingNatives(ex);
        }
    }

    private static string DescribeMissingNatives(Exception cause)
    {
        var required = string.Join(
            ", ",
            ffmpeg.LibraryVersionMap
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}-{entry.Value}"));

        var searched = string.IsNullOrEmpty(ffmpeg.RootPath)
            ? "the system library path"
            : $"'{ffmpeg.RootPath}'";

        var found = DescribeFoundVersions();

        return $"FFmpeg native libraries are not loadable, so no media can be opened or probed. "
               + $"This build binds FFmpeg {required} and looked in {searched}. "
               + $"Found: {found}. "
               + "Install a matching FFmpeg (the ABI major must match exactly - a newer or older FFmpeg "
               + "will not load), or point the app at one. "
               + $"Underlying failure: {cause.GetType().Name}.";
    }

    /// <summary>
    /// What FFmpeg majors ARE present, so the message says "found 63, need 62" rather than only "missing".
    /// Best-effort: an empty answer is reported as such rather than guessed at.
    /// </summary>
    private static string DescribeFoundVersions()
    {
        try
        {
            var pattern = OperatingSystem.IsWindows() ? "avcodec-*.dll" : "libavcodec.so.*";
            var directories = OperatingSystem.IsWindows()
                ? WindowsSystemDirectories()
                : ["/usr/lib", "/usr/lib64", "/usr/local/lib", "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu"];

            var hits = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                    continue;
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                    hits.Add(Path.GetFileName(file));
            }

            return hits.Count == 0 ? "no libavcodec at all" : string.Join(", ", hits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "could not enumerate installed versions";
        }
    }

    internal static string ResolveDefaultRootPath()
    {
        // dlopen/dyld resolve AutoGen's versioned bare library names through the configured
        // system loader paths. The shipped FFmpeg.GPL fallback is Windows-only.
        if (!OperatingSystem.IsWindows())
            return "";

        var requiredFiles = ffmpeg.LibraryVersionMap
            .Select(entry => $"{entry.Key}-{entry.Value}.dll")
            .ToArray();

        return FindCompleteNativeDirectory(
                   WindowsSystemDirectories(), requiredFiles, AppContext.BaseDirectory)
               ?? AppContext.BaseDirectory;
    }

    /// <summary>Returns the first directory containing one coherent native-library set.</summary>
    /// <remarks>
    /// Requiring the entire FFmpeg ABI set prevents mixing a system avcodec with bundled avutil (or
    /// vice versa), which is unsafe even when the individual major versions appear compatible.
    /// </remarks>
    internal static string? FindCompleteNativeDirectory(
        IEnumerable<string> directories,
        IReadOnlyCollection<string> requiredFiles,
        string? excludedDirectory = null)
    {
        if (requiredFiles.Count == 0)
            return null;

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var excluded = NormalizeDirectory(excludedDirectory);
        var seen = new HashSet<string>(comparer);

        foreach (var directory in directories)
        {
            var normalized = NormalizeDirectory(directory);
            if (normalized is null || comparer.Equals(normalized, excluded) || !seen.Add(normalized))
                continue;
            if (requiredFiles.All(file => File.Exists(Path.Combine(normalized, file))))
                return normalized;
        }

        return null;
    }

    private static IEnumerable<string> WindowsSystemDirectories()
    {
        if (!string.IsNullOrWhiteSpace(Environment.SystemDirectory))
            yield return Environment.SystemDirectory;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            yield break;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return directory;
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        try
        {
            return Path.GetFullPath(directory.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void MaybeLogIgnoredRootPath(string? requested)
    {
        if (requested is null)
            return;

        var current = ffmpeg.RootPath ?? "";
        if (string.Equals(current, requested, StringComparison.Ordinal))
            return;

        if (Interlocked.Exchange(ref _ignoredRootPathLogged, 1) != 0)
            return;

        MediaDiagnostics.LogWarning(
            "FFmpegRuntime.EnsureInitialized: bindings already initialized (RootPath '{0}'); ignoring requested rootPath '{1}'. Use a new process to load a different native FFmpeg build.",
            current,
            requested);
    }
}
