namespace HaCue2.Presentation;

/// <summary>The machine-scope command map used by both Settings and the shell key router.</summary>
public static class AppHotkeys
{
    public const string Go = "go";
    public const string StandbyUp = "standbyUp";
    public const string StandbyDown = "standbyDown";
    public const string Preview = "preview";
    public const string StopPreview = "stopPreview";
    public const string OutputInfo = "outputInfo";
    public const string Undo = "undo";
    public const string Redo = "redo";
    public const string Save = "save";
    public const string SaveAs = "saveAs";
    public const string NewProject = "newProject";
    public const string OpenProject = "openProject";

    public sealed record Command(string Id, string Name, string Group);

    public static IReadOnlyList<string> Profiles { get; } = ["Cue standard", "Laptop"];

    public static IReadOnlyList<Command> Commands { get; } =
    [
        new(Go, "GO", "transport"),
        new(StandbyUp, "Standby up", "transport"),
        new(StandbyDown, "Standby down", "transport"),
        new(Preview, "Preview on audition", "cue"),
        new(StopPreview, "Stop preview", "cue"),
        new(OutputInfo, "Output info drawer", "shell"),
        new(Undo, "Undo", "edit"),
        new(Redo, "Redo", "edit"),
        new(Save, "Save", "file"),
        new(SaveAs, "Save as", "file"),
        new(NewProject, "New project", "file"),
        new(OpenProject, "Open project", "file"),
    ];

    public static string Gesture(HaCue2.Machine.AppSettings settings, string commandId)
    {
        if (settings.HotkeyBindings.TryGetValue(commandId, out var edited))
            return edited.Trim();

        var profile = string.Equals(settings.HotkeyProfile, "Laptop", StringComparison.OrdinalIgnoreCase)
            ? Laptop
            : Standard;
        return profile.GetValueOrDefault(commandId, "");
    }

    public static bool Matches(HaCue2.Machine.AppSettings settings, string commandId, string gesture) =>
        gesture.Length > 0
        && string.Equals(Gesture(settings, commandId), gesture, StringComparison.OrdinalIgnoreCase);

    public static void Reset(HaCue2.Machine.AppSettings settings) => settings.HotkeyBindings.Clear();

    private static readonly IReadOnlyDictionary<string, string> Standard =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Go] = "Space",
            [StandbyUp] = "Up",
            [StandbyDown] = "Down",
            [Preview] = "Ctrl+P",
            [StopPreview] = "Ctrl+Shift+P",
            [OutputInfo] = "F9",
            [Undo] = "Ctrl+Z",
            [Redo] = "Ctrl+Shift+Z",
            [Save] = "Ctrl+S",
            [SaveAs] = "Ctrl+Shift+S",
            [NewProject] = "Ctrl+N",
            [OpenProject] = "Ctrl+O",
        };

    private static readonly IReadOnlyDictionary<string, string> Laptop =
        new Dictionary<string, string>(Standard, StringComparer.OrdinalIgnoreCase)
        {
            [StandbyUp] = "Ctrl+Up",
            [StandbyDown] = "Ctrl+Down",
            [Preview] = "F8",
            [StopPreview] = "Shift+F8",
        };
}
