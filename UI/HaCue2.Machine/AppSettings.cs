using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaCue2.Machine;

/// <summary>
/// One project the operator opened, as the launcher lists it.
/// </summary>
/// <remarks>
/// The SUMMARY is stored rather than recomputed, because the launcher shows it before anything is
/// opened and reading every recent project to count its cues would make the launcher as slow as the
/// slowest file on a disconnected volume.
/// </remarks>
public sealed record RecentProject
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>"84 cues · 3 lists · 11 logical outs", as it was when last opened.</summary>
    public string Summary { get; set; } = "";

    public DateTimeOffset LastOpened { get; set; }
}

/// <summary>
/// The application scope: machine preferences that never travel with a show.
/// </summary>
/// <remarks>
/// <para>
/// Register item 26 splits settings hard. These live in <c>app-settings.json</c>, save immediately and
/// have no undo; the project half is journaled and travels in the file. A show that carried the
/// operator's font size to the next venue would be carrying the wrong thing.
/// </para>
/// <para>
/// <b>Every property uses <c>set</c>, never <c>init</c></b>, and defaults live on the property — the
/// same rule the document model follows, for the same reason: the JSON source generator assigns every
/// init-only property, so one absent from the file would be written as the CLR default and the
/// initializer beside it silently lost.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Bumped only for a change an older build could MISREAD. Additive fields do not.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // ── appearance & layout ───────────────────────────────────────────────────────────────────
    public string Theme { get; set; } = "booth dark";
    public string Density { get; set; } = "normal";
    public string RowSize { get; set; } = "26 px";
    public string FontScale { get; set; } = "100 %";
    /// <summary>
    /// Which library opens local sound devices: <c>portaudio</c> or <c>miniaudio</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A MACHINE setting, never a project one — the two see different devices on the same box (this one
    /// enumerates fifteen outputs through PortAudio and two through miniaudio), so a show carrying its
    /// own choice would arrive at a venue and change what that venue's rig looks like.
    /// </para>
    /// <para>
    /// The backend is chosen at the composition root, before a window exists, so changing it takes
    /// effect on the next start. Advanced on purpose: PortAudio is the default because it is what the
    /// status pass and HaPlay both check against, and miniaudio is the answer when a box's PortAudio
    /// build is the thing that is broken.
    /// </para>
    /// </remarks>
    public string AudioBackend { get; set; } = "portaudio";

    public string MeterBallistics { get; set; } = "PPM fast";
    public string ClipReset { get; set; } = "on click";
    public bool RememberInspectorTab { get; set; } = true;
    public bool RememberTimelineDock { get; set; } = true;
    public bool FlatActiveList { get; set; }
    public bool OpenDrawerOnLaunch { get; set; }

    // ── transport defaults ────────────────────────────────────────────────────────────────────
    public string SpaceRule { get; set; } = "GO unless typing";
    public string DoubleGoGuard { get; set; } = "250 ms";
    public string ConfirmStopAll { get; set; } = "3 cues";
    public bool StandbyFollowsClick { get; set; }

    /// <summary>
    /// The machine's panic fade, in milliseconds. A project may override it (register item 26).
    /// </summary>
    /// <remarks>
    /// A fade rather than a cut even here: a true hard cut through a big PA is a thump that can damage
    /// drivers, so "as fast as is safe" is the honest reading of panic and the number stays the
    /// operator's to set.
    /// </remarks>
    public int PanicFadeMs { get; set; } = 250;

    /// <summary>The machine's default STOP fade, in ms. A project may pin its own.</summary>
    public int StopFadeMs { get; set; } = 750;

    /// <summary>How long a meter holds its peak marker, in ms.</summary>
    public int PeakHoldMs { get; set; } = 1_500;

    /// <summary>What a NEW project's first audio line and mix rate are seeded with.</summary>
    public int NewProjectMixRate { get; set; } = 48_000;

    /// <summary>
    /// A new cue's default fade in/out, in ms. Zero: a cue fades because somebody asked it to.
    /// </summary>
    /// <remarks>
    /// The machine-scope seed for <c>ProjectSettings.DefaultFadeInMs</c>. Set them here to give every
    /// new project a house fade; leave them at zero and cues start hard, which is what a cue list of
    /// stings and stabs wants and what a butt-cut requires.
    /// </remarks>
    public int NewProjectFadeInMs { get; set; }

    public int NewProjectFadeOutMs { get; set; }

    // ── new project defaults (register item 20) ───────────────────────────────────────────────
    public bool AutoRenumberDefault { get; set; } = true;

    // ── remote API (the machine default; a project may override it) ───────────────────────────
    public string RemoteDefault { get; set; } = "off";
    public string RemotePort { get; set; } = "8420";
    public bool RemoteLanAllowed { get; set; }

    /// <summary>
    /// The shared secret every remote call must present.
    /// </summary>
    /// <remarks>
    /// Machine-scope, never in the project: a token that travelled in the show file would be a token
    /// published to everybody the show was ever sent to. Generated on first use rather than shipped
    /// with a default, because a default token is the same as none.
    /// </remarks>
    public string RemoteToken { get; set; } = "";

    /// <summary>The token, minting one the first time it is asked for.</summary>
    public string EnsureRemoteToken()
    {
        if (RemoteToken.Length == 0)
            RemoteToken = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        return RemoteToken;
    }

    // ── media cache & logging ─────────────────────────────────────────────────────────────────

    /// <summary>Empty means the shared framework cache root, which is the intended default.</summary>
    public string CacheRoot { get; set; } = "";

    public string WaveformBudget { get; set; } = "2.0 GB";
    public string ThumbnailBudget { get; set; } = "512 MB";
    public string FileLogLevel { get; set; } = "Information";

    /// <summary>Empty means <see cref="StoragePaths.LogRoot"/>.</summary>
    public string LogDirectory { get; set; } = "";

    public string LogRetention { get; set; } = "14 days";
    public bool CrashDumps { get; set; } = true;

    // ── the launcher's recents ────────────────────────────────────────────────────────────────
    public List<RecentProject> Recents { get; set; } = [];

    /// <summary>How many recents the launcher keeps. Beyond this the oldest are forgotten.</summary>
    public const int MaxRecents = 12;

    /// <summary>
    /// Records that a project was opened, moving it to the top.
    /// </summary>
    /// <remarks>
    /// Matched by PATH, case-insensitively on the platforms where that is right — reopening the same
    /// file must move its row rather than add a second one. A project that has never been saved has no
    /// path and is not recorded: there would be nothing to reopen.
    /// </remarks>
    public void NoteOpened(string path, string title, string summary, DateTimeOffset now)
    {
        if (path.Length == 0)
            return;

        var full = FullPath(path);
        Recents.RemoveAll(recent => SamePath(recent.Path, full));

        Recents.Insert(0, new RecentProject
        {
            Path = full,
            Title = title,
            Summary = summary,
            LastOpened = now,
        });

        if (Recents.Count > MaxRecents)
            Recents.RemoveRange(MaxRecents, Recents.Count - MaxRecents);
    }

    /// <summary>Forgets one recent — the row's own "remove", and what a discarded project deserves.</summary>
    public void Forget(string path) => Recents.RemoveAll(recent => SamePath(recent.Path, FullPath(path)));

    /// <summary>
    /// Absolute where possible, so the same file opened by two different relative paths is one row.
    /// </summary>
    /// <remarks>
    /// A path that cannot be made absolute (an invalid character, a length limit) is kept verbatim
    /// rather than dropped: it is still what the operator saw, and losing the row would be worse than
    /// listing one that may not resolve.
    /// </remarks>
    private static string FullPath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads and writes <c>app-settings.json</c>.
/// </summary>
/// <remarks>
/// <b>Nothing here throws.</b> Settings are a convenience: a corrupt or unreadable file must give the
/// operator a working app with defaults, not a start-up failure. A save that cannot be written is
/// reported through the return value so a caller can say so, and the app keeps running either way.
/// </remarks>
public static class AppSettingsStore
{
    /// <summary>Loads the settings, or defaults when there are none this build can read.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= StoragePaths.SettingsFile;

        try
        {
            if (!File.Exists(path))
                return new AppSettings();

            var settings = JsonSerializer.Deserialize(
                File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings);

            // A file from a NEWER build is read as far as it goes rather than refused. Unlike a show
            // document, losing a preference costs nothing — and refusing to start because the settings
            // are too new would be the worst possible trade.
            return settings ?? new AppSettings();
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes the settings atomically.
    /// </summary>
    /// <returns>False when they could not be written — a read-only profile, or a full disk.</returns>
    public static bool Save(AppSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= StoragePaths.SettingsFile;

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (directory is { Length: > 0 } && !StoragePaths.EnsureDirectory(directory))
                return false;

            // Temp-then-move, in the same directory, for the same reason the show file does it: a
            // crash mid-write leaves the previous settings intact rather than a truncated file that
            // reads as "no settings at all".
            var temp = path + ".tmp";
            File.WriteAllText(
                temp, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
