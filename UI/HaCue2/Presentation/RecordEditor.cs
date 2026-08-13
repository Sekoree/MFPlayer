using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Session;

namespace HaCue2.Presentation;

/// <summary>
/// What is being recorded, as the pane needs it.
/// </summary>
/// <remarks>
/// An audio line and a video output are different model types with the same recording block, so this
/// adapter is what lets one editor serve both rather than the Audio and Video views each growing their
/// own copy of it - the copies would drift, and the difference between them would be a recording that
/// behaved differently depending on which pane configured it.
/// </remarks>
/// <param name="Channels">Audio channel count; zero for a video output, which records a picture.</param>
public readonly record struct RecordSubject(
    Guid Id,
    string Name,
    Func<RecordTarget?> Read,
    Func<RecordTarget> Ensure,
    bool CarriesVideo,
    bool IsStream,
    int Channels);

/// <summary>
/// The record pane, for whichever line or output is selected (register item 30).
/// </summary>
/// <remarks>
/// <para>
/// Every value here reaches the document through the journal, so a recording setting is as undoable as
/// any other edit. The pane was drawn complete long before any of it did - a directory, a pattern, an
/// insert-token dropdown and a format line, all literals in the markup - and looked finished in a
/// screenshot while editing nothing.
/// </para>
/// <para>
/// The FORMAT is the pattern's own extension, so the preview, the format line and the file written all
/// come from one statement in the document. There is no separate container picker to contradict the
/// name the operator typed.
/// </para>
/// </remarks>
public sealed partial class RecordEditor(ProjectJournal journal, HaCueProject project, ShowRuntime runtime)
    : ObservableObject
{
    private RecordSubject? _subject;

    /// <summary>Points the pane at a line or output, or at nothing.</summary>
    public void Show(RecordSubject? subject)
    {
        _subject = subject;

        // A refusal describes one attempt on one target; carrying it to the next would be read as a
        // fresh failure of something that has not been tried.
        RecorderProblem = "";
        Refresh();
    }

    /// <summary>Whether the pane applies at all.</summary>
    /// <remarks>
    /// Hidden rather than disabled for an ordinary line or screen: a directory and a filename pattern
    /// are not dimmed-out properties of a sound card, they are meaningless for one.
    /// </remarks>
    public bool IsRecording => _subject is not null;

    public bool IsStream => _subject?.IsStream == true;

    public bool IsFile => _subject is { IsStream: false };

    private RecordTarget? Target => _subject?.Read();

    public string Directory
    {
        get => Target?.Directory ?? "";
        set => Edit(target => target.Directory, (target, text) => target.Directory = text, value,
            "recordDirectory", "recording folder");
    }

    public string Pattern
    {
        get => Target?.Pattern ?? "";
        set => Edit(target => target.Pattern, (target, text) => target.Pattern = text, value,
            "recordPattern", "recording pattern");
    }

    public string Url
    {
        get => Target?.Url ?? "";
        set => Edit(target => target.Url, (target, text) => target.Url = text, value,
            "streamUrl", "stream URL");
    }

    /// <summary>Continuous (0) or content-only (1) - the archive/reel choice.</summary>
    public int ModeIndex
    {
        get => Target?.Continuous == true ? 0 : 1;
        set => Toggle(target => target.Continuous, (target, flag) => target.Continuous = flag, value == 0,
            "recordContinuous", value == 0 ? "records continuously" : "records content only");
    }

    public bool ArmWithShow
    {
        get => Target?.ArmWithShow == true;
        set => Toggle(target => target.ArmWithShow, (target, flag) => target.ArmWithShow = flag, value,
            "armWithShow", value ? "arms with the show" : "waits to be armed");
    }

    /// <summary>
    /// The name the pattern will actually produce.
    /// </summary>
    /// <remarks>
    /// Live rather than a fixed example, and rendered through the recorder's own expander: the tokens
    /// are unguessable, watching the name change as you type is what teaches them, and a preview
    /// written separately could promise a name the recorder would not write.
    /// </remarks>
    public string Preview
    {
        get
        {
            if (Target is not { } target)
                return "";

            return RecordPattern.Example(
                    Path.GetFileNameWithoutExtension(target.Pattern), project.Title, DateTimeOffset.Now)
                + Path.GetExtension(target.Pattern);
        }
    }

    /// <summary>Why this will not record, or empty when it will.</summary>
    public string Problem =>
        IsFile && Target is { } target
            ? RecordFormatNames.Problem(target.Pattern, _subject?.CarriesVideo == true) ?? ""
            : "";

    public bool HasProblem => Problem.Length > 0;

    /// <summary>The format the extension resolved to, and what it will be fed.</summary>
    public string FormatSummary
    {
        get
        {
            if (IsStream)
                return "follows the stream's protocol";

            if (Target is not { } target || target.Pattern.Length == 0)
                return "-";

            if (RecordFormatNames.Describe(target.Pattern) is not { } summary)
                return "unavailable";

            return _subject?.CarriesVideo == true
                ? summary
                : $"{summary} · {project.AudioPatch.MixSampleRate:N0} · {_subject?.Channels ?? 0}ch";
        }
    }

    /// <summary>Every insert token, for the dropdown and the help popover.</summary>
    public IReadOnlyList<RecordPattern.RecordToken> Tokens { get; } = RecordPattern.Tokens;

    /// <summary>The help popover's body: every token with what it stands for.</summary>
    public string TokenHelp { get; } = string.Join(
        "\n", RecordPattern.Tokens.Select(token => $"{token.Token} - {token.Meaning}"));

    /// <summary>Adds a token to the pattern, before its extension so the name stays writable.</summary>
    public void InsertToken(string? token)
    {
        if (token is null || Target is not { } target)
            return;

        // Before the EXTENSION rather than at the end: appending "{n}" after ".mkv" would produce
        // "show.mkv{n}", which names no format at all and would be refused on the next arm.
        var extension = Path.GetExtension(target.Pattern);
        var stem = target.Pattern.Length > 0 ? target.Pattern[..^extension.Length] : "recording";

        Pattern = stem + token + extension;
    }

    // ── what only the running show knows ──────────────────────────────────────────────────────

    private RecorderStatus? State =>
        _subject is { } subject ? runtime.Recorders.FirstOrDefault(row => row.Id == subject.Id) : null;

    public bool IsArmed => State?.Armed == true;

    public string ArmLabel => IsArmed ? "DISARM" : "ARM";

    /// <summary>Why the last arm did not happen, kept until the settings change or it is tried again.</summary>
    [ObservableProperty]
    private string _recorderProblem = "";

    /// <summary>Where it is writing and how it fares, or why it is not.</summary>
    public string Readout
    {
        get
        {
            if (RecorderProblem.Length > 0)
                return RecorderProblem;

            if (State is not { } state)
                return IsRecording ? "idle" : "";

            if (!state.Armed)
                return state.Problem ?? "idle";

            var size = state.BytesWritten >= 1_048_576
                ? $"{state.BytesWritten / 1_048_576.0:N1} MB"
                : $"{state.BytesWritten / 1024.0:N0} kB";

            // Drops are named because they are the only warning an operator gets before the file gaps.
            return $"{state.Destination} · {size}{(state.Dropped > 0 ? $" · {state.Dropped} dropped" : "")}";
        }
    }

    /// <summary>Records a refusal so it survives the click that produced it.</summary>
    public void NoteProblem(string problem)
    {
        RecorderProblem = problem;
        RefreshRunning();
    }

    /// <summary>Re-announces what only the running show knows. Polled, because drops arrive quietly.</summary>
    public void RefreshRunning()
    {
        OnPropertyChanged(nameof(IsArmed));
        OnPropertyChanged(nameof(ArmLabel));
        OnPropertyChanged(nameof(Readout));
    }

    /// <summary>Re-announces everything the pane derives from the document.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsStream));
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(Directory));
        OnPropertyChanged(nameof(Pattern));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(ModeIndex));
        OnPropertyChanged(nameof(ArmWithShow));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(FormatSummary));
        RefreshRunning();
    }

    private void Edit(
        Func<RecordTarget, string> read,
        Action<RecordTarget, string> write,
        string value,
        string field,
        string what)
    {
        if (_subject is not { } subject)
            return;

        var target = subject.Ensure();

        if (read(target) == value)
            return;

        journal.Do(new SetValueCommand<string>(
            subject.Id, field, "audio",
            () => read(target), text => write(target, text), value,
            $"“{subject.Name}” {what}"));
        journal.CloseGroup();

        RecorderProblem = "";
        Refresh();
    }

    private void Toggle(
        Func<RecordTarget, bool> read,
        Action<RecordTarget, bool> write,
        bool value,
        string field,
        string what)
    {
        if (_subject is not { } subject)
            return;

        var target = subject.Ensure();

        if (read(target) == value)
            return;

        journal.Do(new SetValueCommand<bool>(
            subject.Id, field, "audio",
            () => read(target), flag => write(target, flag), value,
            $"“{subject.Name}” {what}"));
        journal.CloseGroup();

        RecorderProblem = "";
        Refresh();
    }
}
