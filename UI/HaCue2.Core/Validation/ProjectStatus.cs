using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Validation;

/// <summary>How a named check came out.</summary>
public enum CheckOutcome
{
    Passed,
    Warning,
    Failed,

    /// <summary>Nobody could look — see <see cref="DeviceAvailability.Unknown"/>.</summary>
    NotChecked,
}

/// <summary>One row of the Project status view: what was checked, how it went, and what fixes it.</summary>
public sealed record StatusCheck(
    string Name,
    CheckOutcome Outcome,
    string Detail,
    string Fix,
    IReadOnlyList<ShowValidationIssue> Issues);

/// <summary>
/// The whole pass: every check, its outcome, and an exit code.
/// </summary>
/// <remarks>
/// Renderable as text (the "Copy report" button) and as JSON (a CI gate over a fixture project). Both
/// are INVARIANT-formatted — a report pasted from a comma-decimal machine must not read as a different
/// number, which is the same trap the audio bay's report serializer had to avoid.
/// </remarks>
public sealed record ProjectStatusReport(IReadOnlyList<StatusCheck> Checks, double ElapsedSeconds)
{
    public int Errors => Checks.Count(check => check.Outcome == CheckOutcome.Failed);
    public int Warnings => Checks.Count(check => check.Outcome == CheckOutcome.Warning);
    public int Passed => Checks.Count(check => check.Outcome == CheckOutcome.Passed);
    public int NotChecked => Checks.Count(check => check.Outcome == CheckOutcome.NotChecked);

    /// <summary>Non-zero while errors remain, so a script can gate on it.</summary>
    public int ExitCode => Errors > 0 ? 1 : 0;

    /// <summary>The status-bar summary: "2 errors · 2 warnings · 9 passed · 0.4 s".</summary>
    public string Summary =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Errors} error{S(Errors)} · {Warnings} warning{S(Warnings)} · {Passed} passed · "
            + $"{ElapsedSeconds:0.0} s");

    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"HaCue2 project status — {Summary}");
        text.AppendLine();

        foreach (var check in Checks)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"[{Mark(check.Outcome)}] {check.Name}: {check.Detail}");
            text.AppendLine();
            foreach (var issue in check.Issues)
            {
                text.Append(CultureInfo.InvariantCulture, $"      {issue.Severity}: {issue.Message}");
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(this, StatusJsonContext.Default.ProjectStatusReport);

    private static string Mark(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Passed => "ok",
        CheckOutcome.Warning => "warn",
        CheckOutcome.Failed => "FAIL",
        _ => "—",
    };

    private static string S(int count) => count == 1 ? "" : "s";
}

/// <summary>
/// Project status: the pure document pass plus everything that needs to ask this machine.
/// </summary>
/// <remarks>
/// <para>
/// Two passes, one report. The document half (<see cref="ProjectValidator"/>) means the same thing on
/// any box and can gate a fixture project in CI; the environment half — missing media, absent devices
/// — only means anything where the show is about to run. Merging them into one pass would make the CI
/// gate depend on the CI machine's sound card.
/// </para>
/// <para>
/// Severity follows register item 25: unpatched-but-fed is an error, patched-but-unfed and absent
/// devices are warnings — EXCEPT a device flagged <see cref="AudioLineDefinition.Required"/>, whose
/// absence is an error. The flag is inverted from the obvious design so a show can say "this cannot
/// run without the main PA" instead of asking every optional output to excuse itself.
/// </para>
/// </remarks>
public static class ProjectStatus
{
    public static ProjectStatusReport Run(
        HaCueProject project,
        string? projectPath = null,
        IProjectEnvironment? environment = null)
    {
        environment ??= FileSystemEnvironment.Instance;
        var started = Stopwatch.GetTimestamp();

        // One document pass, shared: the video-output check needs both what the machine says and what
        // the document says, and running the validator twice would let the two halves disagree.
        var documentIssues = ProjectValidator.Validate(project);

        var checks = new List<StatusCheck>
        {
            CheckMedia(project, projectPath, environment),
            CheckAudioLines(project, environment),
            CheckVideoOutputs(project, environment, documentIssues),
        };

        checks.AddRange(DocumentChecks(documentIssues));

        return new ProjectStatusReport(checks, Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    private static StatusCheck CheckMedia(
        HaCueProject project, string? projectPath, IProjectEnvironment environment)
    {
        var references = MediaPaths.ReferencesIn(project);
        var missing = new List<ShowValidationIssue>();

        foreach (var reference in references)
        {
            var resolved = MediaPaths.Resolve(project, reference.Path, projectPath);
            if (!environment.MediaExists(resolved))
                missing.Add(new ShowValidationIssue(
                    ShowValidationSeverity.Error,
                    $"{reference.Describe} — {reference.Path} was not found.",
                    reference.SubjectKind,
                    reference.SubjectId));
        }

        if (references.Count == 0)
            return new StatusCheck("Media files", CheckOutcome.Passed, "no media referenced", "", []);

        return missing.Count == 0
            ? new StatusCheck("Media files", CheckOutcome.Passed,
                $"{references.Count} file{(references.Count == 1 ? "" : "s")}, all resolve", "", [])
            : new StatusCheck("Media files", CheckOutcome.Failed,
                $"{missing.Count} missing", "Relink ›", missing);
    }

    private static StatusCheck CheckAudioLines(HaCueProject project, IProjectEnvironment environment)
    {
        var issues = new List<ShowValidationIssue>();
        var unchecked_ = 0;

        foreach (var line in project.AudioLines)
            switch (environment.AudioLine(line))
            {
                case DeviceAvailability.Unknown:
                    unchecked_++;
                    break;

                case DeviceAvailability.Absent:
                    var carries = project.AudioPatch.Cells.Count(cell => cell.LineId == line.Id);
                    issues.Add(new ShowValidationIssue(
                        // Required makes an absence fatal; otherwise the show runs with that line silent.
                        line.Required ? ShowValidationSeverity.Error : ShowValidationSeverity.Warning,
                        $"“{line.Name}” is not on this machine"
                        + (carries > 0 ? $" — {carries} patch cell{(carries == 1 ? "" : "s")} silent." : ".")
                        + (line.Required ? " It is marked required." : ""),
                        "audioLine",
                        line.Id.ToString()));
                    break;
            }

        return Summarise("Audio devices", issues, unchecked_, project.AudioLines.Count,
            "Relink ›", "line");
    }

    private static StatusCheck CheckVideoOutputs(
        HaCueProject project,
        IProjectEnvironment environment,
        IReadOnlyList<ShowValidationIssue> documentIssues)
    {
        // Document problems about an output belong on the SAME row as its absence — an operator reading
        // "Video outputs" wants one answer about them, not two rows that have to be cross-referenced.
        var issues = documentIssues.Where(issue => issue.SubjectKind == "videoOutput").ToList();
        var unchecked_ = 0;

        foreach (var output in project.VideoOutputs)
            switch (environment.VideoOutput(output))
            {
                case DeviceAvailability.Unknown:
                    unchecked_++;
                    break;

                case DeviceAvailability.Absent:
                    issues.Add(new ShowValidationIssue(
                        output.Required ? ShowValidationSeverity.Error : ShowValidationSeverity.Warning,
                        $"“{output.Name}” is not on this machine"
                        + (output.Required ? " and is marked required." : " — the output holds and shows idle."),
                        "videoOutput",
                        output.Id.ToString()));
                    break;
            }

        return Summarise("Video outputs", issues, unchecked_, project.VideoOutputs.Count,
            "Relink ›", "output");
    }

    /// <summary>
    /// Buckets the pure validator's issues into named rows.
    /// </summary>
    /// <remarks>
    /// By SUBJECT KIND, never by reading the message text. A status view that grouped rows by matching
    /// words in a sentence would regroup itself the next time somebody reworded one.
    /// </remarks>
    private static IEnumerable<StatusCheck> DocumentChecks(IReadOnlyList<ShowValidationIssue> issues)
    {
        foreach (var (name, kinds, fix) in DocumentBuckets)
        {
            var mine = issues.Where(issue => kinds.Contains(issue.SubjectKind ?? "document")).ToList();

            yield return mine.Count == 0
                ? new StatusCheck(name, CheckOutcome.Passed, "ok", "", [])
                : new StatusCheck(
                    name,
                    mine.Any(issue => issue.Severity == ShowValidationSeverity.Error)
                        ? CheckOutcome.Failed
                        : CheckOutcome.Warning,
                    Describe(mine),
                    fix,
                    mine);
        }
    }

    private static readonly (string Name, string[] Kinds, string Fix)[] DocumentBuckets =
    [
        ("Logical outputs", ["logicalOutput", "outputGroup", "patchCell"], "Patch ›"),
        ("Patch snapshots", ["snapshot"], "Show ›"),
        ("Cues", ["cue"], "Show ›"),
        ("Cue lists", ["cueList"], "Show ›"),
        ("Compositions", ["composition"], "Show ›"),
        ("Trigger inputs", ["triggerInput"], "Show ›"),
        ("Clock master and mix", ["audioLine", "document"], "Devices ›"),
    ];

    private static StatusCheck Summarise(
        string name,
        IReadOnlyList<ShowValidationIssue> issues,
        int notChecked,
        int total,
        string fix,
        string noun)
    {
        if (issues.Count > 0)
            return new StatusCheck(
                name,
                issues.Any(issue => issue.Severity == ShowValidationSeverity.Error)
                    ? CheckOutcome.Failed
                    : CheckOutcome.Warning,
                Describe(issues),
                fix,
                issues);

        // Saying "not checked" rather than "ok" is the point of DeviceAvailability.Unknown: a green row
        // nobody actually verified is worse than an honest blank one.
        if (notChecked > 0 && notChecked == total)
            return new StatusCheck(name, CheckOutcome.NotChecked,
                $"{total} {noun}{(total == 1 ? "" : "s")} — this machine's devices were not enumerated",
                "", []);

        return new StatusCheck(name, CheckOutcome.Passed,
            total == 0 ? $"no {noun}s" : $"{total} {noun}{(total == 1 ? "" : "s")} present", "", []);
    }

    private static string Describe(IReadOnlyList<ShowValidationIssue> issues)
    {
        var errors = issues.Count(issue => issue.Severity == ShowValidationSeverity.Error);
        var warnings = issues.Count - errors;

        return (errors, warnings) switch
        {
            (0, _) => $"{warnings} warning{(warnings == 1 ? "" : "s")}",
            (_, 0) => $"{errors} error{(errors == 1 ? "" : "s")}",
            _ => $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}",
        };
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProjectStatusReport))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;
