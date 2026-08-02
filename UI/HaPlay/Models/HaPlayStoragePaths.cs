namespace HaPlay.Models;

/// <summary>
/// Central resolver for the running app's per-machine cache/config root
/// (<c>…/LocalApplicationData/{AppName}</c>).
/// </summary>
/// <remarks>
/// <para>
/// The empty-special-folder fallback (NativeAOT can return an empty
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> in minimal/service environments) was
/// previously copy-pasted across <see cref="AppSettings"/>, the recent-projects store and the Control
/// workspace's script scratch cache; everything now shares this one copy.
/// </para>
/// <para>
/// <see cref="AppName"/> exists so a second app on the same machine gets its own root instead of writing
/// settings, recent-projects, recovery folders and script scratch into HaPlay's. It is NOT the shared media
/// cache - prepared assets and baked physics are keyed by source material rather than by app and live under
/// <c>S.Media.Core.MediaCachePaths</c>, deliberately outside this root.
/// </para>
/// </remarks>
public static class HaPlayStoragePaths
{
    private static string _appName = "HaPlay";
    private static bool _resolved;

    /// <summary>
    /// The app this process stores as. Set once at startup, before anything reads a path.
    /// </summary>
    /// <remarks>
    /// Assigning after a path has already been resolved throws rather than quietly taking effect: the
    /// damage from a late change is files written under two different roots in one run, which surfaces
    /// later as "my settings vanished" and is near-impossible to trace back to the assignment.
    /// </remarks>
    public static string AppName
    {
        get => _appName;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"app name '{value}' is not a valid folder name.", nameof(value));
            if (_resolved && !string.Equals(value, _appName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"storage paths were already resolved under '{_appName}'; set AppName before first use.");
            }

            _appName = value;
        }
    }

    /// <summary>The environment variable that redirects <see cref="LocalAppRoot"/> for this app,
    /// e.g. <c>HAPLAY_CACHE_ROOT</c>. Derived from <see cref="AppName"/> so a second app gets its own.</summary>
    public static string RootOverrideVariable => AppName.ToUpperInvariant() + "_CACHE_ROOT";

    /// <summary>The app folder under the user's local application data (created on demand by callers).
    /// Honors <see cref="RootOverrideVariable"/> (used to sandbox the whole cache under a temp dir in
    /// tests), and otherwise falls back to a user-scoped path when the special folder resolves empty so we
    /// never write into the process working directory.</summary>
    public static string LocalAppRoot
    {
        get
        {
            _resolved = true;
            return Environment.GetEnvironmentVariable(RootOverrideVariable) is { Length: > 0 } sandbox
                ? sandbox
                : Path.Combine(ResolveLocalBase(), AppName);
        }
    }

    /// <summary>Root under which crashed-session recovery folders live
    /// (<c>…/{AppName}/recovery/{sessionId}</c>).</summary>
    public static string RecoveryRoot => Path.Combine(LocalAppRoot, "recovery");

    /// <summary>Tests only: forgets that a path was resolved, so <see cref="AppName"/> can be reassigned.</summary>
    internal static void ResetForTests() => _resolved = false;

    private static string ResolveLocalBase()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            return local;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), AppName + "-user")
            : Path.Combine(home, ".local", "share");
    }
}
