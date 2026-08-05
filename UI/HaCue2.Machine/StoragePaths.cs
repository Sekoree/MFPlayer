namespace HaCue2.Machine;

/// <summary>
/// Where HaCue2 keeps its machine-local files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-app settings, recovery, logs and unified derived-media cache.</b> HaCue2 keeps its waveform
/// and prepared YouTube data under one policy root so the Settings screen can measure, budget and clear
/// all of it truthfully. The cache itself can still be moved to a dedicated volume with the
/// machine-scope cache-root setting.
/// </para>
/// <para>
/// HaPlay proves the naming policy with its own resolver, and the architecture rules correctly forbid
/// HaCue2 from referencing it. This is the HaCue2-local equivalent rather than a shared helper,
/// because the two apps agree on the SHAPE and must not agree on the location.
/// </para>
/// <para>
/// Every path is overridable by environment variable, which is what makes the app testable without
/// writing into a developer's real profile — and what lets a booth machine put its state on a
/// different volume from the operator's home directory.
/// </para>
/// </remarks>
public static class StoragePaths
{
    /// <summary>The directory name under the platform's local application data.</summary>
    public const string AppName = "HaCue2";

    /// <summary>Overrides the whole machine-local root — settings, recovery and logs together.</summary>
    public const string RootVariable = "HACUE2_DATA_ROOT";

    /// <summary>Overrides just the settings file, for a test or a portable install.</summary>
    public const string SettingsVariable = "HACUE2_SETTINGS_PATH";

    /// <summary>
    /// The machine-local root for this app's own state.
    /// </summary>
    /// <remarks>
    /// Resolved on each call rather than cached in a static field: a test that sets the variable after
    /// the type was first touched would otherwise get the developer's real directory, and that failure
    /// mode depends on test ORDER, which makes it the worst kind to debug.
    /// </remarks>
    public static string Root
    {
        get
        {
            if (Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } overridden)
                return overridden;

            var local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                // DoNotVerify: the folder may not exist yet on a fresh profile, and the default
                // Verify behaviour returns an EMPTY STRING for a missing one — which would silently
                // put the app's settings in the working directory.
                Environment.SpecialFolderOption.DoNotVerify);

            // A profile with no local-app-data at all (some containers) still has a home directory.
            if (local.Length == 0)
                local = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

            return Path.Combine(local, AppName);
        }
    }

    /// <summary>The application-scope settings file (register item 26: machine, not journaled).</summary>
    public static string SettingsFile =>
        Environment.GetEnvironmentVariable(SettingsVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Root, "app-settings.json");

    /// <summary>Where autosave copies live, one directory per project.</summary>
    public static string RecoveryRoot => Path.Combine(Root, "recovery");

    /// <summary>The default log directory, when the operator has not chosen one.</summary>
    public static string LogRoot => Path.Combine(Root, "logs");

    /// <summary>
    /// Where a recording lands when its line names no folder of its own.
    /// </summary>
    /// <remarks>
    /// Under the data root rather than the user's Videos folder: a show's recordings belong with the
    /// show's own state, and a default that scattered files into a personal media library would be
    /// found by surprise. A line that wants them elsewhere says so.
    /// </remarks>
    public static string RecordingRoot => Path.Combine(Root, "recordings");

    /// <summary>Creates a directory if it is missing, and reports whether it can be written to.</summary>
    /// <remarks>
    /// Returns false rather than throwing. A read-only or full disk must not stop the app starting —
    /// it makes settings and autosave unavailable, which is worth saying out loud and surviving.
    /// </remarks>
    public static bool EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
