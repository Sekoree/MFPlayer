namespace S.Media.Core;

/// <summary>
/// Where the framework keeps machine-local derived data that is expensive to rebuild but safe to lose:
/// prepared YouTube assets, baked MMD physics, and anything similar added later.
/// </summary>
/// <remarks>
/// <para>
/// One root shared by every app, because the contents are keyed by source material rather than by whoever
/// asked for them - two apps on one machine preparing the same video should hit the same cache, not pay for
/// it twice. That is why this lives beside the media code and not in an app's storage helper.
/// </para>
/// <para>
/// <see cref="RootOverrideVariable"/> exists mainly so a test run cannot touch the real user cache. Both
/// call sites used to compute this path inline from <see cref="Environment.SpecialFolder.LocalApplicationData"/>,
/// which meant no override could reach them: HaPlay's own cache redirect covered settings, recovery and
/// scripts, and silently missed these two - so the suite wrote prepared assets and baked physics into the
/// developer's actual cache directory.
/// </para>
/// <para>
/// Read fresh on every access rather than cached in a static field. These are consulted when a cache
/// directory is resolved, not in any hot path, and a value captured at type-initialisation time would be
/// decided by whichever type happened to be touched first - exactly the ordering trap that makes an
/// override look like it works until a test runs in a different order.
/// </para>
/// </remarks>
public static class MediaCachePaths
{
    /// <summary>Environment variable that redirects <see cref="Root"/> wholesale.</summary>
    public const string RootOverrideVariable = "MFPLAYER_CACHE_ROOT";

    /// <summary>The shared cache root. Never empty; falls back to the current directory if the platform
    /// reports no local-application-data folder (which some minimal containers do).</summary>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Directory.GetCurrentDirectory();
            return Path.Combine(localAppData, "mfplayer");
        }
    }

    /// <summary>A named sub-cache under <see cref="Root"/>, e.g. <c>"youtube-cache"</c>.</summary>
    /// <param name="name">A single path segment - not a nested or rooted path.</param>
    public static string For(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // A caller that passes "../.." or an absolute path is not asking for a sub-cache, and silently
        // honouring it would put the override back in the position of not sandboxing anything.
        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar) || name.Contains(".."))
        {
            throw new ArgumentException(
                $"cache name '{name}' must be a single path segment.", nameof(name));
        }

        return Path.Combine(Root, name);
    }
}
