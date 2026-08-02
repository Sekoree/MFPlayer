using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Controls;
using HaCue2.Presentation;
using HaCue2.Sample;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>Screens 06–08b — Logical outputs · Patch · Devices · Audition, all from the document.</summary>
public partial class AudioViewModel : ObservableObject
{
    private readonly ProjectJournal _journal;
    private readonly ShowRuntime _runtime;

    public AudioViewModel(ProjectJournal journal, ShowRuntime runtime)
    {
        _journal = journal;
        _runtime = runtime;
        var project = journal.Project;

        // The counts in the tab labels are the real ones: add a logical output and the tab says so.
        OutputsTab = $"LOGICAL OUTPUTS · {project.AudioPatch.LogicalChannels.Count}";
        DevicesTab = $"DEVICES · {project.AudioLines.Count}";
        Tabs = [OutputsTab, PatchTab, DevicesTab, AuditionTab];
        _selectedTab = OutputsTab;

        Outputs = AudioPresentation.LogicalOutputs(project, runtime);
        _selectedOutput = Outputs.FirstOrDefault(row => row.PatchedTo.IsBad) ?? Outputs.FirstOrDefault();
        Lines = AudioPresentation.Lines(project, runtime);
        _selectedLine = Lines.FirstOrDefault(row => row.Kind.StartsWith("File", StringComparison.Ordinal))
                        ?? Lines.FirstOrDefault();
    }

    public const string PatchTab = "PATCH";
    public const string AuditionTab = "AUDITION";

    public string OutputsTab { get; }
    public string DevicesTab { get; }
    public IReadOnlyList<string> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputsPane))]
    [NotifyPropertyChangedFor(nameof(IsPatchPane))]
    [NotifyPropertyChangedFor(nameof(IsDevicesPane))]
    [NotifyPropertyChangedFor(nameof(IsAuditionPane))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    private string _selectedTab;

    public bool IsOutputsPane => SelectedTab == OutputsTab;
    public bool IsPatchPane => SelectedTab == PatchTab;
    public bool IsDevicesPane => SelectedTab == DevicesTab;
    public bool IsAuditionPane => SelectedTab == AuditionTab;

    public string TabHint => SelectedTab switch
    {
        PatchTab => "rows: device channels · columns: logical outputs",
        AuditionTab => "same pane appears in Video · one audition rig",
        _ => $"mix {_project.AudioPatch.MixSampleRate:N0} Hz · clock master {ClockMasterName} · edits apply live",
    };

    private HaCueProject _project => _journal.Project;

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal;

    /// <summary>What a new group would take as members — the selected output, when there is one.</summary>
    public IReadOnlyList<Guid> SelectedOutputIds =>
        SelectedOutput is { } row ? [row.Id] : [];

    /// <summary>
    /// Patches the selected logical output onto a device line's channels.
    /// </summary>
    /// <remarks>
    /// Register item 8's "pick device channels" — the operator names the LINE and the first channel,
    /// and the cells land from there. Null when there is nothing selected or nowhere to patch to, so
    /// the button opens nothing rather than a modal that says "select something first".
    /// </remarks>
    public PromptViewModel? PatchSelectedToDevice()
    {
        if (SelectedOutput is not { } row
            || _project.FindChannel(row.Id) is not { } channel
            || _project.AudioLines.Count == 0)
            return null;

        var lines = _project.AudioLines;

        return new PromptViewModel(
            $"Patch “{channel.Name}”",
            "which line, and which channel on it",
            [
                new PromptField
                {
                    Label = "Line",
                    Kind = PromptFieldKind.Choice,
                    Options = [.. lines.Select(line => $"{line.Name} · {line.Channels}ch")],
                },
                new PromptField
                {
                    Label = "Channel",
                    Kind = PromptFieldKind.Number,
                    Value = "1",
                    Hint = "1-based, as the device numbers its outputs",
                },
            ],
            prompt =>
            {
                var line = lines[Math.Clamp(prompt["Line"].SelectedIndex, 0, lines.Count - 1)];
                var lineChannel = Math.Clamp(prompt["Channel"].Number(1) - 1, 0, line.Channels - 1);

                _journal.Do(new SetPatchCellCommand(
                    _project.AudioPatch, channel.Id, line.Id, lineChannel, 0, false,
                    $"patch {channel.Name} → {line.Name} {lineChannel + 1}"));
                _journal.CloseGroup();
            },
            confirm: "PATCH");
    }

    /// <summary>
    /// Points every absent line at a device that is here.
    /// </summary>
    /// <remarks>
    /// One prompt for all of them rather than one per line: an interface that changed name changed it
    /// for every line on it, and answering the same question five times is how an operator gives up
    /// halfway and leaves the show half-patched.
    /// </remarks>
    public PromptViewModel? RelinkAbsentLines()
    {
        var absent = _project.AudioLines
            .Where(line => _runtime.AbsentLines.Contains(line.Id))
            .ToList();

        if (absent.Count == 0)
            return null;

        return new PromptViewModel(
            "Relink absent lines",
            $"{absent.Count} line(s) are not on this machine",
            [
                new PromptField
                {
                    Label = "Device",
                    Value = "",
                    Hint = "the new device name · matched the same way as before",
                },
            ],
            prompt =>
            {
                var hint = prompt["Device"].Value.Trim();
                if (hint.Length == 0)
                    return;

                using var scope = _journal.Composite("relink absent lines", "audio");

                foreach (var line in absent)
                {
                    var target = line;
                    _journal.Do(new SetValueCommand<string>(
                        target.Id, "deviceHint", "audio",
                        () => target.DeviceHint, value => target.DeviceHint = value, hint,
                        $"relink “{target.Name}”"));
                }
            },
            confirm: "RELINK");
    }

    private string ClockMasterName =>
        _project.AudioPatch.ClockMasterLineId is { } id
            ? _project.FindLine(id)?.Name ?? "none"
            : "none";

    // ── 06 · logical outputs ──────────────────────────────────────────────────────────────────
    public IReadOnlyList<LogicalOutputRow> Outputs { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Senders))]
    [NotifyPropertyChangedFor(nameof(SelectedOutputName))]
    [NotifyPropertyChangedFor(nameof(SelectedOutputHint))]
    [NotifyPropertyChangedFor(nameof(IsSelectedOutputUnpatched))]
    private LogicalOutputRow? _selectedOutput;

    public string SelectedOutputName => SelectedOutput?.Name ?? "no output selected";

    public string SelectedOutputHint => SelectedOutput is null
        ? ""
        : SelectedOutput.HasGroup ? $"logical output · in group “{SelectedOutput.Group}”" : "logical output";

    public bool IsSelectedOutputUnpatched => SelectedOutput?.PatchedTo.IsBad ?? false;

    public IReadOnlyList<string> Senders => SelectedOutput is null
        ? []
        : AudioPresentation.Senders(_project, SelectedOutput.Id);

    // ── 07 · patch ────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<MatrixColumn> PatchColumns => AudioPresentation.PatchColumns(_project);
    public IReadOnlyList<MatrixRow> PatchRows => AudioPresentation.PatchRows(_project, _runtime);

    public IReadOnlyList<string> Snapshots =>
    [
        .. _project.PatchSnapshots.Select(snapshot =>
            $"▸ {snapshot.Name} · {snapshot.Cells.Count} cell{(snapshot.Cells.Count == 1 ? "" : "s")}"),
    ];

    // ── 08 · devices ──────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<AudioLineRow> Lines { get; private set; }

    /// <summary>A brand-new project has no lines until somebody adds one.</summary>
    public bool HasNoLines => Lines.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLineName))]
    private AudioLineRow? _selectedLine;

    public string SelectedLineName => SelectedLine?.Name ?? "no line selected";

    public string PatternTokens { get; } = SampleShow.RecordPatternTokens;

    public IReadOnlyList<string> MixRates { get; } = ["44 100 Hz", "48 000 Hz", "96 000 Hz"];

    public IReadOnlyList<string> ClockMasters =>
        [.. _project.AudioLines.Select(line => line.Name)];

    // ── editing the patch (register item 13) ──────────────────────────────────────────────────

    /// <summary>
    /// Applies one pointer gesture to the patch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Click toggles at unity, drag adjusts the gain, right-click mutes — the interaction the register
    /// specifies, and the same one the cue-sends matrix uses.
    /// </para>
    /// <para>
    /// A DRAG goes through <see cref="ProjectEdits.NudgeGroupGain"/> rather than writing the cell
    /// directly, so a member of an Output Group carries its partners with it by the same delta
    /// (register item 9). Toggling and muting stay per-cell: routing one side of a stereo pair
    /// somewhere is a deliberate act, not something to mirror behind the operator's back.
    /// </para>
    /// </remarks>
    public void ApplyPatchGesture(MatrixGesture gesture)
    {
        if (gesture.Row >= PatchRows.Count || gesture.Column >= PatchColumns.Count)
            return;

        var row = PatchRows[gesture.Row];
        var channelId = PatchColumns[gesture.Column].ChannelId;
        var patch = _project.AudioPatch;
        var existing = patch.Cells.FirstOrDefault(
            cell => cell.Matches(channelId, row.LineId, row.LineChannel));

        switch (gesture.Kind)
        {
            case MatrixGestureKind.Toggle:
                _journal.Do(new SetPatchCellCommand(
                    patch, channelId, row.LineId, row.LineChannel,
                    existing is null ? 0 : null,
                    existing is null ? false : null,
                    existing is null ? "route at unity" : "un-route cell"));
                // A click is a complete gesture, so it closes its own group; otherwise two clicks on
                // the same cell would collapse into one undo step.
                _journal.CloseGroup();
                break;

            case MatrixGestureKind.Adjust:
                _drag ??= _journal.Composite("adjust patch gain", "patch");
                ProjectEdits.NudgeGroupGain(
                    _journal, channelId, row.LineId, row.LineChannel, gesture.DeltaDb);
                break;

            case MatrixGestureKind.Mute when existing is not null:
                _journal.Do(new SetPatchCellCommand(
                    patch, channelId, row.LineId, row.LineChannel, existing.GainDb, !existing.Muted,
                    existing.Muted ? "unmute cell" : "mute cell"));
                _journal.CloseGroup();
                break;
        }

        Refresh();
    }

    /// <summary>Closes the drag, making the whole of it one undo step.</summary>
    public void EndPatchGesture()
    {
        _drag?.Dispose();
        _drag = null;
        _journal.CloseGroup();
        Refresh();
    }

    /// <summary>The open drag, if one is in progress. Null between gestures.</summary>
    private IDisposable? _drag;

    /// <summary>Re-reads the document after an edit here, or an undo from anywhere.</summary>
    public void Refresh()
    {
        Outputs = AudioPresentation.LogicalOutputs(_project, _runtime);
        Lines = AudioPresentation.Lines(_project, _runtime);

        OnPropertyChanged(nameof(Outputs));
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(PatchRows));
        OnPropertyChanged(nameof(PatchColumns));
        OnPropertyChanged(nameof(Senders));
        OnPropertyChanged(nameof(Snapshots));
    }

    // ── 08b · audition ────────────────────────────────────────────────────────────────────────
    public AuditionViewModel Audition { get; } = new();
}

/// <summary>The audition rig — an audio device plus a video surface, shared by the Audio and Video views.</summary>
public partial class AuditionViewModel : ObservableObject
{
    public IReadOnlyList<string> Devices { get; } =
        ["built-in headphones", "18i20 · Out 9/10", "Behringer UCA222"];

    [ObservableProperty] private string _device = "built-in headphones";
    [ObservableProperty] private string _level = "−12.0 dB";

    /// <summary>Ducks the monitor while the program is sounding — the booth's own ears, not the mix.</summary>
    [ObservableProperty] private bool _duckWhenProgramSounds = true;

    public IReadOnlyList<string> Surfaces { get; } = ["window", "screen 2", "none"];

    [ObservableProperty] private string _surface = "window";

    public string Size { get; } = "960×540 · follows composition aspect";
}
