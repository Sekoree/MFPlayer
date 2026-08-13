using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Engine;
using HaCue2.Controls;
using HaCue2.Presentation;
using HaCue2.Sample;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>Screens 06–08b - Logical outputs · Patch · Devices · Audition, all from the document.</summary>
public partial class AudioViewModel : ObservableObject
{
    private readonly ProjectJournal _journal;
    private readonly ShowRuntime _runtime;

    /// <summary>Authoring is disabled by the shell's lock; live audition and recorder controls remain available.</summary>
    public bool CanAuthor => !_journal.IsReadOnly;

    /// <summary>The running show, used only to make a patch gain drag audible while it is happening.</summary>
    public ShowHost? Host { get; set; }

    public AudioViewModel(ProjectJournal journal, ShowRuntime runtime)
    {
        _journal = journal;
        _runtime = runtime;
        var project = journal.Project;

        // Keyed, so the labels can carry LIVE counts. They used to be plain strings that doubled as the
        // tabs' identity, which meant a count could never be rewritten without the selection losing the
        // tab it pointed at - so the numbers were frozen at construction and quietly went stale.
        OutputsTab = new SectionTab(OutputsKey, "LOGICAL OUTPUTS");
        PatchTab = new SectionTab(PatchKey, "PATCH");
        DevicesTab = new SectionTab(DevicesKey, "DEVICES");
        AuditionTab = new SectionTab(AuditionKey, "AUDITION");
        Tabs = [OutputsTab, PatchTab, DevicesTab, AuditionTab];
        _selectedTab = OutputsTab;
        CountTabs();

        Outputs = AudioPresentation.LogicalOutputs(project, runtime);
        _selectedOutput = Outputs.FirstOrDefault(row => row.PatchedTo.IsBad) ?? Outputs.FirstOrDefault();
        Lines = AudioPresentation.Lines(project, runtime);
        _selectedLine = Lines.FirstOrDefault(row => row.Kind.StartsWith("File", StringComparison.Ordinal))
                        ?? Lines.FirstOrDefault();

        Record = new RecordEditor(journal, project, runtime);
        ShowRecordPane();

        // One rig object, shared with the Video view's copy of the pane: it is a single thing, and two
        // view-models over it would drift the moment either was edited.
        Audition = new AuditionViewModel(journal);
    }

    public const string OutputsKey = "outputs";
    public const string PatchKey = "patch";
    public const string DevicesKey = "devices";
    public const string AuditionKey = "audition";

    public SectionTab OutputsTab { get; }
    public SectionTab PatchTab { get; }
    public SectionTab DevicesTab { get; }
    public SectionTab AuditionTab { get; }
    public IReadOnlyList<SectionTab> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputsPane))]
    [NotifyPropertyChangedFor(nameof(IsPatchPane))]
    [NotifyPropertyChangedFor(nameof(IsDevicesPane))]
    [NotifyPropertyChangedFor(nameof(IsAuditionPane))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    private SectionTab _selectedTab;

    public bool IsOutputsPane => SelectedTab?.Key == OutputsKey;
    public bool IsPatchPane => SelectedTab?.Key == PatchKey;
    public bool IsDevicesPane => SelectedTab?.Key == DevicesKey;
    public bool IsAuditionPane => SelectedTab?.Key == AuditionKey;

    public string TabHint => SelectedTab?.Key switch
    {
        PatchKey => "rows: device channels · columns: logical outputs",
        AuditionKey => "same pane appears in Video · one audition rig",
        _ => $"mix {_project.AudioPatch.MixSampleRate:N0} Hz · clock master {ClockMasterName} · edits apply live",
    };

    /// <summary>Re-labels the counted tabs from the document. Called wherever the lists are re-read.</summary>
    private void CountTabs()
    {
        OutputsTab.Count("LOGICAL OUTPUTS", _project.AudioPatch.LogicalChannels.Count);
        DevicesTab.Count("DEVICES", _project.AudioLines.Count);
    }

    private HaCueProject _project => _journal.Project;

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal;

    /// <summary>What a new group would take as members - the selected output, when there is one.</summary>
    public IReadOnlyList<Guid> SelectedOutputIds =>
        SelectedOutput is { } row ? [row.Id] : [];

    /// <summary>
    /// Patches the selected logical output onto a device line's channels.
    /// </summary>
    /// <remarks>
    /// Register item 8's "pick device channels" - the operator names the LINE and the first channel,
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

        // WHERE IT ALREADY IS. The dialog used to open on line 1 / channel 1 whatever the patch said,
        // so re-patching a cell to the channel it was already on looked like a control that did
        // nothing - and there was no way to see where the output actually went without leaving.
        var current = _project.AudioPatch.Cells
            .FirstOrDefault(cell => cell.LogicalChannelId == channel.Id);

        var atLine = current is null
            ? 0
            : Math.Max(0, lines.ToList().FindIndex(line => line.Id == current.LineId));

        return new PromptViewModel(
            $"Patch “{channel.Name}”",
            current is null
                ? "which line, and which channel on it"
                : $"currently on {lines[atLine].Name} channel {current.LineChannel + 1}",
            [
                new PromptField
                {
                    Label = "Line",
                    Kind = PromptFieldKind.Choice,
                    Options = [.. lines.Select(line => $"{line.Name} · {line.Channels}ch")],
                    SelectedIndex = atLine,
                },
                new PromptField
                {
                    Label = "Channel",
                    Kind = PromptFieldKind.Number,
                    Value = ((current?.LineChannel ?? 0) + 1)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
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

    private LogicalOutputRow? _selectedOutput;

    /// <summary>
    /// The output the inspector beside the list is editing.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than generated for the guard alone. <see cref="Refresh"/> replaces
    /// <see cref="Outputs"/> on every edit, and a list control drops its selection whenever its source
    /// is replaced - so this arrived as null in the middle of a rename, emptied the inspector, and
    /// disabled the field being typed in, which takes keyboard focus with it. One character per rename,
    /// then out of the box. The refresh puts the selection back BY ID (the rows are new objects, so the
    /// old instance matches nothing even though the output is still there); the null in between is the
    /// control reacting to the rebuild, never the operator.
    /// </remarks>
    public LogicalOutputRow? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (_rebuilding)
                return;

            SelectOutput(value);
        }
    }

    private void SelectOutput(LogicalOutputRow? row)
    {
        if (ReferenceEquals(_selectedOutput, row))
            return;

        _selectedOutput = row;
        OnPropertyChanged(nameof(SelectedOutput));
        OnPropertyChanged(nameof(Senders));
        OnPropertyChanged(nameof(SelectedGroupId));
        OnPropertyChanged(nameof(HasGroupToRemove));
        OnPropertyChanged(nameof(HasSelectedOutput));
        OnPropertyChanged(nameof(SelectedOutputName));
        OnPropertyChanged(nameof(OutputName));
        OnPropertyChanged(nameof(SelectedOutputLines));
        OnPropertyChanged(nameof(SelectedOutputCarries));
        OnPropertyChanged(nameof(MeterInSummaryIndex));
        OnPropertyChanged(nameof(SelectedOutputHint));
        OnPropertyChanged(nameof(IsSelectedOutputUnpatched));
        // A refusal and a SOLO/CLEAR label are both about the output that was selected when they were
        // produced, so both follow the selection.
        OnPropertyChanged(nameof(SoloLabel));
    }

    public string SelectedOutputName => SelectedOutput?.Name ?? "no output selected";

    /// <summary>
    /// The selected logical output's name, editable in place.
    /// </summary>
    /// <remarks>
    /// The inspector's Name box was bound to <see cref="SelectedOutputName"/>, which has no setter - so
    /// it accepted typing and threw it away, and the only working rename was the footer button. A field
    /// that looks editable and is not is worse than no field.
    /// </remarks>
    public string OutputName
    {
        get => SelectedChannel?.Name ?? "";
        set
        {
            var name = (value ?? "").Trim();

            if (SelectedChannel is not { } channel || name.Length == 0 || channel.Name == name)
            {
                OnPropertyChanged(nameof(OutputName));
                return;
            }

            _journal.Do(new SetValueCommand<string>(
                channel.Id, "name", "outputs",
                () => channel.Name, text => channel.Name = text, name, $"rename to “{name}”"));
            _journal.CloseGroup();

            Refresh();
        }
    }

    // ── solo to the monitor (register item 13) ────────────────────────────────────────────────

    /// <summary>What the monitor is carrying instead of its own patch. Set by the shell after a press.</summary>
    private Guid? _soloed;

    /// <summary>
    /// The button's own label.
    /// </summary>
    /// <remarks>
    /// One button, two verbs, because it is one decision: an operator who has soloed an output presses
    /// the same place to stop. A second CLEAR button beside it would be a control that does nothing
    /// most of the time.
    /// </remarks>
    public string SoloLabel =>
        SelectedOutput is { } row && _soloed == row.Id
            ? "CLEAR SOLO - MONITOR BACK TO ITS OWN PATCH"
            : "SOLO THIS OUTPUT TO AUDITION";

    /// <summary>The last refusal from the solo button, or nothing.</summary>
    [ObservableProperty]
    private string _soloProblem = "";

    /// <summary>Records what the show answered, so the label and the refusal agree.</summary>
    public void NoteSolo(Guid? soloed, string? problem)
    {
        _soloed = soloed;
        SoloProblem = problem ?? "";
        OnPropertyChanged(nameof(SoloLabel));
    }

    /// <summary>The selected logical output's definition, or null when the selection is stale.</summary>
    private LogicalAudioChannel? SelectedChannel =>
        SelectedOutput is { } row
            ? _project.AudioPatch.LogicalChannels.FirstOrDefault(channel => channel.Id == row.Id)
            : null;

    /// <summary>
    /// Where the selected logical output is patched, line by line.
    /// </summary>
    /// <remarks>
    /// Was a literal "18i20 · Out 3" - a fixture's rig, shown over every project. This is the answer to
    /// "why can I not hear Lobby", so it has to be the truth about THIS document.
    /// </remarks>
    public string SelectedOutputLines
    {
        get
        {
            if (SelectedChannel is not { } channel)
                return "-";

            var cells = _project.AudioPatch.Cells
                .Where(cell => cell.LogicalChannelId == channel.Id)
                .Select(cell => $"{_project.FindLine(cell.LineId)?.Name ?? "?"} · {cell.LineChannel + 1}")
                .ToList();

            return cells.Count == 0 ? "not patched on this machine" : string.Join(" · ", cells);
        }
    }

    /// <summary>What the selected output carries, at what gain.</summary>
    public string SelectedOutputCarries
    {
        get
        {
            if (SelectedChannel is not { } channel)
                return "-";

            var cells = _project.AudioPatch.Cells
                .Where(cell => cell.LogicalChannelId == channel.Id)
                .ToList();

            if (cells.Count == 0)
                return "nothing - no device channel receives it";

            var muted = cells.Count(cell => cell.Muted);
            var loudest = cells.Max(cell => cell.GainDb);

            return muted == cells.Count
                ? "muted on every line"
                : $"{channel.Name} @ {loudest:+0.0;-0.0;0.0} dB"
                    + (muted > 0 ? $" · {muted} muted" : "");
        }
    }

    /// <summary>Register item: which outputs appear in the Output info drawer's compact row.</summary>
    public int MeterInSummaryIndex
    {
        get => SelectedChannel?.MeterInSummary == false ? 1 : 0;
        set
        {
            if (SelectedChannel is not { } channel)
                return;

            var wanted = value == 0;

            if (channel.MeterInSummary == wanted)
                return;

            _journal.Do(new SetValueCommand<bool>(
                channel.Id, "meterInSummary", "outputs",
                () => channel.MeterInSummary, flag => channel.MeterInSummary = flag, wanted,
                wanted ? $"“{channel.Name}” meters in summary" : $"“{channel.Name}” hidden from summary"));
            _journal.CloseGroup();

            OnPropertyChanged(nameof(MeterInSummaryIndex));
        }
    }

    public string SelectedOutputHint => SelectedOutput is null
        ? ""
        : SelectedOutput.HasGroup ? $"logical output · in group “{SelectedOutput.Group}”" : "logical output";

    /// <summary>The group the selected output belongs to, which is what "remove group" acts on.</summary>
    /// <remarks>
    /// Reached through the SELECTION rather than through a list of its own. Groups have no pane -
    /// membership is shown on the outputs that are in one - so the group an operator means is the one
    /// belonging to the row they right-clicked, and a second list to select from would be a second
    /// place to get the selection wrong.
    /// </remarks>
    public Guid? SelectedGroupId =>
        SelectedOutput is { } row ? _project.AudioPatch.GroupOf(row.Id)?.Id : null;

    public bool HasGroupToRemove => SelectedGroupId is not null;

    public bool HasSelectedOutput => SelectedOutput is not null;

    public bool IsSelectedOutputUnpatched => SelectedOutput?.PatchedTo.IsBad ?? false;

    public IReadOnlyList<string> Senders => SelectedOutput is null
        ? []
        : AudioPresentation.Senders(_project, SelectedOutput.Id);

    // ── 07 · patch ────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<MatrixColumn> PatchColumns => AudioPresentation.PatchColumns(_project);
    public IReadOnlyList<MatrixRow> PatchRows => AudioPresentation.PatchRows(_project, _runtime);

    public IReadOnlyList<SnapshotRow> Snapshots =>
    [
        .. _project.PatchSnapshots.Select(snapshot => new SnapshotRow(
            snapshot.Id,
            $"▸ {snapshot.Name} · {snapshot.Cells.Count} cell{(snapshot.Cells.Count == 1 ? "" : "s")}")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshot))]
    private SnapshotRow? _selectedSnapshot;

    public bool HasSnapshot => SelectedSnapshot is not null;

    /// <summary>
    /// Recalls the selected snapshot onto the live patch.
    /// </summary>
    /// <remarks>
    /// <b>Not journaled, and not a document edit in the undo sense</b> - the same rule a patch cue
    /// firing follows. Recall is an operator action on the patch; "undo" means un-edit my document,
    /// never un-recall my snapshot. What it DOES change is the document's cell values, so the project
    /// goes dirty and the change is saved with the show.
    /// <para>
    /// A cell the snapshot can no longer reach is reported by name rather than slid onto a neighbour,
    /// and the rest of the recall still lands: refusing the whole thing because one channel was
    /// renamed would leave the operator with neither the old state nor the new one.
    /// </para>
    /// </remarks>
    public void RecallSelected()
    {
        if (_journal.IsReadOnly || SelectedSnapshot is not { } row)
            return;

        var result = PatchOperations.Recall(_project, row.Id);

        RecallNote = result.IsClean
            ? $"recalled {result.CellsApplied} cell{(result.CellsApplied == 1 ? "" : "s")}"
            : $"recalled {result.CellsApplied} · {result.Broken.Count} could not be applied: "
              + string.Join("; ", result.Broken.Select(broken => broken.Reason).Distinct());

        _journal.MarkDirty();
        Refresh();
    }

    /// <summary>
    /// Re-captures the selected snapshot from the patch as it stands now.
    /// </summary>
    /// <remarks>
    /// Journaled, unlike recall: editing what a snapshot STORES is an ordinary document edit, and
    /// overwriting somebody's stored state without an undo would be the most expensive mistake this
    /// pane can make. Only the channels the snapshot already covers are re-captured - "update" means
    /// this snapshot as it is now, not "make it cover the whole console".
    /// </remarks>
    public void UpdateSelected()
    {
        if (SelectedSnapshot is not { } row
            || _project.PatchSnapshots.FirstOrDefault(item => item.Id == row.Id) is not { } snapshot)
            return;

        var covered = snapshot.Cells.Select(cell => cell.LogicalChannelId).Distinct().ToHashSet();
        var captured = PatchOperations.Capture(_project, covered.Count > 0 ? covered : null);
        var target = snapshot;

        _journal.Do(new SetValueCommand<List<PatchCell>>(
            snapshot.Id, "snapshotCells", "patch",
            () => target.Cells, cells => target.Cells = cells, captured,
            $"update snapshot “{snapshot.Name}”"));
        _journal.CloseGroup();

        RecallNote = $"stored {captured.Count} cell{(captured.Count == 1 ? "" : "s")}";
        Refresh();
    }

    /// <summary>What the last recall or update did, for the pane's own line.</summary>
    [ObservableProperty]
    private string _recallNote = "";

    // ── 08 · devices ──────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<AudioLineRow> Lines { get; private set; }

    /// <summary>A brand-new project has no lines until somebody adds one.</summary>
    public bool HasNoLines => Lines.Count == 0;

    /// <summary>
    /// Whether there is anything to patch a logical output ONTO.
    /// </summary>
    /// <remarks>
    /// A new project has logical outputs and no lines, deliberately - a line is one machine's sound
    /// card and does not belong in a travelling document. That made the patch button a dead end: it
    /// opened nothing, said nothing, and looked broken. It is disabled with a reason now.
    /// </remarks>
    public bool CanPatchToDevice => _project.AudioLines.Count > 0;

    public string PatchHint =>
        CanPatchToDevice
            ? ""
            : "no audio lines yet - add one under DEVICES, then patch this output onto its channels";

    private AudioLineRow? _selectedLine;

    /// <summary>The line whose recording settings the pane below the list is editing.</summary>
    /// <remarks>Guarded and restored by id for the same reason as <see cref="SelectedOutput"/>.</remarks>
    public AudioLineRow? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (_rebuilding)
                return;

            SelectLine(value);
        }
    }

    private void SelectLine(AudioLineRow? row)
    {
        if (ReferenceEquals(_selectedLine, row))
            return;

        _selectedLine = row;
        OnPropertyChanged(nameof(SelectedLine));
        OnPropertyChanged(nameof(SelectedLineName));
        OnPropertyChanged(nameof(HasSelectedLine));
        ShowRecordPane();
    }

    public string SelectedLineName => SelectedLine?.Name ?? "no line selected";

    public bool HasSelectedLine => SelectedLine is not null;

    // ── 08 · the selected line's recording ────────────────────────────────────────────────────

    /// <summary>
    /// The record pane for the selected line (register item 30).
    /// </summary>
    /// <remarks>
    /// Shared with the Video view rather than reimplemented: an audio line and a video output hold the
    /// same recording block, and two copies of this pane would drift into a recording that behaved
    /// differently depending on which view configured it.
    /// </remarks>
    public RecordEditor Record { get; }

    private AudioLineDefinition? SelectedDefinition =>
        SelectedLine is { } row ? _project.AudioLines.FirstOrDefault(line => line.Id == row.Id) : null;

    /// <summary>Points the record pane at the selection, or at nothing when it is not a recorder.</summary>
    private void ShowRecordPane()
    {
        if (SelectedDefinition is not { Kind: AudioLineKind.FileRecord or AudioLineKind.Stream } line)
        {
            Record.Show(null);
            return;
        }

        Record.Show(new RecordSubject(
            line.Id,
            line.Name,
            () => line.Record,
            () => line.Record ??= new RecordTarget(),
            CarriesVideo: false,
            IsStream: line.Kind == AudioLineKind.Stream,
            line.Channels));
    }

    /// <summary>Re-announces what only the running show knows, on every tick.</summary>
    public void RefreshRecorders() => Record.RefreshRunning();

    public IReadOnlyList<string> MixRates { get; } = ["44 100 Hz", "48 000 Hz", "96 000 Hz"];

    private static readonly int[] MixRateValues = [44_100, 48_000, 96_000];

    /// <summary>
    /// The project mix rate. Every producer submits at it and the clock master must run there natively.
    /// </summary>
    /// <remarks>
    /// Journaled, and deliberately NOT applied to a running bay: the bus width and rate are fixed when
    /// the bay opens, so this takes effect on the next start. Register item 14 makes that an explicit
    /// "Apply &amp; restart audio" rather than a silent rebuild under a running show.
    /// </remarks>
    public int MixRateIndex
    {
        get => Array.IndexOf(MixRateValues, _project.AudioPatch.MixSampleRate);
        set
        {
            if (value < 0 || value >= MixRateValues.Length
                || MixRateValues[value] == _project.AudioPatch.MixSampleRate)
                return;

            var patch = _project.AudioPatch;

            _journal.Do(new SetValueCommand<int>(
                Guid.Empty, "mixRate", "audio",
                () => patch.MixSampleRate, rate => patch.MixSampleRate = rate, MixRateValues[value],
                $"set mix rate {MixRates[value]}"));
            _journal.CloseGroup();

            OnPropertyChanged(nameof(MixRateIndex));
            OnPropertyChanged(nameof(TabHint));
            OnPropertyChanged(nameof(NeedsAudioRestart));
        }
    }

    /// <summary>
    /// Lines eligible to pace the bay, plus an explicit "none".
    /// </summary>
    /// <remarks>
    /// A line that does not run natively at the mix rate is excluded rather than shown and refused:
    /// the bay wraps it in a resampler, and a resampled master would drift the show clock against
    /// itself. "None" is a real answer - it means the wall-clock fallback, which is what a rig with no
    /// audio interface actually has.
    /// </remarks>
    public IReadOnlyList<string> ClockMasters =>
    [
        "none · wall clock",
        .. EligibleMasters().Select(line => line.Name),
    ];

    private List<AudioLineDefinition> EligibleMasters() =>
    [
        .. _project.AudioLines.Where(line =>
            line.Kind == AudioLineKind.LocalAudio
            && (line.SampleRate is null || line.SampleRate == _project.AudioPatch.MixSampleRate)),
    ];

    public int ClockMasterIndex
    {
        get
        {
            if (_project.AudioPatch.ClockMasterLineId is not { } id)
                return 0;

            var at = EligibleMasters().FindIndex(line => line.Id == id);
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            var eligible = EligibleMasters();
            var chosen = value <= 0 || value > eligible.Count ? (Guid?)null : eligible[value - 1].Id;
            var patch = _project.AudioPatch;

            if (chosen == patch.ClockMasterLineId)
                return;

            _journal.Do(new SetValueCommand<Guid?>(
                Guid.Empty, "clockMaster", "audio",
                () => patch.ClockMasterLineId, id => patch.ClockMasterLineId = id, chosen,
                chosen is null ? "clear clock master" : $"clock master {eligible[value - 1].Name}"));
            _journal.CloseGroup();

            OnPropertyChanged(nameof(ClockMasterIndex));
            OnPropertyChanged(nameof(TabHint));
            OnPropertyChanged(nameof(NeedsAudioRestart));
        }
    }

    /// <summary>
    /// Whether the rate or the clock master has been changed since the bay opened.
    /// </summary>
    /// <remarks>
    /// Set by editing either, cleared by a restart. It drives the "Apply &amp; restart audio" button's
    /// enabled state so the operator can see that the show is not yet running what the document says -
    /// which is the one thing a silent deferral would hide.
    /// </remarks>
    public bool NeedsAudioRestart =>
        _appliedRate is { } rate
        && (rate != _project.AudioPatch.MixSampleRate
            || _appliedMaster != _project.AudioPatch.ClockMasterLineId
            || _appliedOrder != BusOrder()
            || _appliedLines != OpenableLines());

    private int? _appliedRate;
    private Guid? _appliedMaster;
    private string? _appliedOrder;
    private string? _appliedLines;

    /// <summary>
    /// The lines the bay WOULD open right now, as a signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This mirrors the predicate in <c>ProjectPatchBay.Open</c> exactly, because the question it answers
    /// is "would opening the bay again produce a different set of devices". A line opens only when it
    /// addresses a device (record and stream lines are encode sessions, armed later) AND at least one
    /// patch cell routes to it - an unpatched line is not opened, so patching one that had no cells adds
    /// a device just as surely as creating the line does.
    /// </para>
    /// <para>
    /// Without this the button stayed disabled through the entire first setup of a NEW project: the bay
    /// opens with the engine, before there are any lines to open, and adding the first output afterwards
    /// changed neither the rate, the clock master nor the bus - so nothing said the device was not open
    /// and the show simply played silence. Reopening the saved file worked, because then the lines were
    /// there before the bay was built, which is what made it look like a loading bug.
    /// </para>
    /// <para>
    /// Name is deliberately excluded, for the reason <see cref="BusOrder"/> gives: a rename changes
    /// nothing the bay opened. Everything the open path reads - kind, device hint, width, rate - is here.
    /// </para>
    /// </remarks>
    private string OpenableLines()
    {
        var patch = _project.AudioPatch;
        return string.Join(
            "|",
            _project.AudioLines
                .Where(line => line.Kind is not (AudioLineKind.FileRecord or AudioLineKind.Stream))
                .Where(line => patch.Cells.Any(cell => cell.LineId == line.Id))
                .OrderBy(line => line.Id)
                .Select(line =>
                    $"{line.Id}:{line.Kind}:{line.DeviceHint}:{line.Channels}:{line.SampleRate?.ToString() ?? "-"}"));
    }

    /// <summary>
    /// The bus as an ordered list of ids.
    /// </summary>
    /// <remarks>
    /// Order AND membership: adding an output widens the bus and reordering renumbers it, and both are
    /// fixed when the bay opens. Comparing the names would miss a rename that changed nothing and catch
    /// one that did.
    /// </remarks>
    private string BusOrder() =>
        string.Join(
            "|",
            _project.AudioPatch.LogicalChannels
                .OrderBy(channel => channel.SortOrder)
                .Select(channel => channel.Id));

    /// <summary>Records what the running bay was opened with, so a later edit can be seen to differ.</summary>
    public void NoteAudioStarted()
    {
        _appliedRate = _project.AudioPatch.MixSampleRate;
        _appliedMaster = _project.AudioPatch.ClockMasterLineId;
        _appliedOrder = BusOrder();
        _appliedLines = OpenableLines();
        OnPropertyChanged(nameof(NeedsAudioRestart));
    }

    // ── editing the patch (register item 13) ──────────────────────────────────────────────────

    /// <summary>
    /// Applies one pointer gesture to the patch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Click toggles at unity, drag adjusts the gain, right-click mutes - the interaction the register
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
                // QUIET, and pushed straight at the bay instead. A gain drag emits a command per
                // pointer sample; announcing each one ran the shell's entire edit reaction - a media
                // re-probe, four view-model refreshes, and a compile - per pixel, and still gave the
                // operator NOTHING to hear, because every sample restarted the reload debounce behind
                // it. The patch is not part of the compiled document, so it can be reconciled under
                // running voices directly: the drag is now audible as it happens and costs one shell
                // refresh, on release.
                _drag ??= _journal.Composite("adjust patch gain", "patch", quiet: true);
                ProjectEdits.NudgeGroupGain(
                    _journal, channelId, row.LineId, row.LineChannel, gesture.DeltaDb);
                PushPatchLive();
                OnPropertyChanged(nameof(PatchRows));
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
        PushPatchLive();
        Refresh();
    }

    /// <summary>
    /// Reconciles the live bay's cells with the document's, under whatever is playing.
    /// </summary>
    /// <remarks>
    /// Best-effort and deliberately silent on failure: the authored value stands either way, and the
    /// reload that follows the gesture is the recovery path. A patch drag must never be able to throw
    /// out of a pointer handler.
    /// </remarks>
    private void PushPatchLive()
    {
        if (Host is not { } host)
            return;

        try
        {
            host.ApplyPatch(_journal.Project);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Live patch update failed: {failure.GetType().Name}: {failure.Message}");
        }
    }

    /// <summary>The open drag, if one is in progress. Null between gestures.</summary>
    private IDisposable? _drag;

    /// <summary>Re-reads the document after an edit here, or an undo from anywhere.</summary>
    /// <summary>True while the row lists are being replaced - see <see cref="SelectedOutput"/>.</summary>
    private bool _rebuilding;

    public void Refresh()
    {
        var output = SelectedOutput?.Id;
        var line = SelectedLine?.Id;

        Outputs = AudioPresentation.LogicalOutputs(_project, _runtime);
        Lines = AudioPresentation.Lines(_project, _runtime);
        CountTabs();

        // Quiet across the announcement: the lists tell their controls to drop their selections, and
        // the operator's place is restored below rather than being lost and re-found.
        _rebuilding = true;

        try
        {
            OnPropertyChanged(nameof(Outputs));
            OnPropertyChanged(nameof(Lines));
        }
        finally
        {
            _rebuilding = false;
        }

        // By ID: every row is a new object, so the old instance no longer matches anything even though
        // the output or line it stood for is still there.
        SelectOutput(Outputs.FirstOrDefault(row => row.Id == output));
        SelectLine(Lines.FirstOrDefault(row => row.Id == line));

        OnPropertyChanged(nameof(HasNoLines));
        OnPropertyChanged(nameof(CanPatchToDevice));
        OnPropertyChanged(nameof(PatchHint));
        OnPropertyChanged(nameof(PatchRows));
        OnPropertyChanged(nameof(PatchColumns));
        OnPropertyChanged(nameof(Senders));
        OnPropertyChanged(nameof(Snapshots));
        OnPropertyChanged(nameof(OutputName));
        OnPropertyChanged(nameof(SelectedGroupId));
        OnPropertyChanged(nameof(HasGroupToRemove));
        OnPropertyChanged(nameof(SelectedOutputLines));
        OnPropertyChanged(nameof(SelectedOutputCarries));
        OnPropertyChanged(nameof(CanAuthor));

        // Adding a line, removing one, or patching the first cell to it all change which devices the bay
        // would open, and all arrive here. Raising it anywhere narrower missed the case that matters most
        // - the first output of a NEW project, added while the bay is already open and empty.
        OnPropertyChanged(nameof(NeedsAudioRestart));
        Audition.Refresh();
    }

    // ── 08b · audition ────────────────────────────────────────────────────────────────────────
    public AuditionViewModel Audition { get; }
}

/// <summary>One stored snapshot, as the pane lists it. The id is what RECALL and UPDATE act on.</summary>
public sealed record SnapshotRow(Guid Id, string Text);

/// <summary>
/// The audition rig - one audio line plus one video surface, shared by the Audio and Video views.
/// </summary>
/// <remarks>
/// <para>
/// The audio side lists the project's own LINES rather than raw devices (D8): the rig is an output
/// like any other, so it takes that line's channel count, travels with the show, and goes absent on a
/// machine that lacks it. Its settings are journaled like any other project edit.
/// </para>
/// <para>
/// Every field here was a hardcoded string until the rig existed in the model - the pane described a
/// booth that only appeared in the mockup.
/// </para>
/// </remarks>
public partial class AuditionViewModel : ObservableObject
{
    private readonly ProjectJournal? _journal;

    /// <summary>The preview-only rig, for a designer preview with no document behind it.</summary>
    public AuditionViewModel()
    {
    }

    public AuditionViewModel(ProjectJournal journal) => _journal = journal;

    private HaCueProject? Project => _journal?.Project;

    private AuditionRig? Rig => Project?.Audition;

    /// <summary>
    /// The lines the rig can monitor through, plus the bay's own default.
    /// </summary>
    /// <remarks>
    /// "Default monitor line" is a real answer, not a placeholder: it is the first line the bay opened,
    /// which on a one-interface rig is the right one and is why audition works before anybody configures
    /// it.
    /// </remarks>
    public IReadOnlyList<string> Devices =>
        Project is not { } project
            ? ["default monitor line"]
            : ["default monitor line", .. project.AudioLines.Select(
                line => $"{line.Name} · {line.Channels}ch")];

    public int DeviceIndex
    {
        get
        {
            if (Project is not { } project || Rig?.AudioLineId is not { } id)
                return 0;

            var at = project.AudioLines.FindIndex(line => line.Id == id);
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (_journal is null || Project is not { } project || Rig is not { } rig || value < 0)
                return;

            var chosen = value == 0 || value > project.AudioLines.Count
                ? (Guid?)null
                : project.AudioLines[value - 1].Id;

            if (chosen == rig.AudioLineId)
                return;

            _journal.Do(new SetValueCommand<Guid?>(
                Guid.Empty, "auditionLine", "audio",
                () => rig.AudioLineId, id => rig.AudioLineId = id, chosen, "set audition line"));
            _journal.CloseGroup();

            OnPropertyChanged(nameof(DeviceIndex));
            OnPropertyChanged(nameof(Width));
        }
    }

    /// <summary>
    /// How wide the audition path will be - read from the chosen line, never assumed.
    /// </summary>
    /// <remarks>
    /// D8's whole point, said on screen: a rig on an 8-channel interface auditions in 8 channels. The
    /// two candidate answers this replaced were "always stereo" and "the project's logical width",
    /// and neither survives auditioning through a multichannel interface.
    /// </remarks>
    public string Width
    {
        get
        {
            if (Project is not { } project)
                return "";

            if (Rig?.AudioLineId is not { } id)
                return "follows the bay's default monitor line";

            return project.FindLine(id) is { } line
                ? $"{line.Channels} channel(s), from the line itself"
                : "that line is no longer in this project";
        }
    }

    public string Level
    {
        get => Rig is { } rig ? CuePresentation.Db(rig.LevelDb) : "−12.0";
        set
        {
            if (_journal is null || Rig is not { } rig)
                return;

            var text = value.Replace('−', '-').Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();

            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var db))
                return;

            _journal.Do(new SetValueCommand<double>(
                Guid.Empty, "auditionLevel", "audio",
                () => rig.LevelDb, level => rig.LevelDb = level, Math.Clamp(db, GainRange.SilenceFloorDb, 12),
                "set audition level"));
            _journal.CloseGroup();

            OnPropertyChanged(nameof(Level));
        }
    }

    public bool DuckWhenProgramSounds
    {
        get => Rig is not { DuckWhenProgramSounds: false };
        set
        {
            if (_journal is null || Rig is not { } rig || value == rig.DuckWhenProgramSounds)
                return;

            _journal.Do(new SetValueCommand<bool>(
                Guid.Empty, "auditionDuck", "audio",
                () => rig.DuckWhenProgramSounds, on => rig.DuckWhenProgramSounds = on, value,
                value ? "duck the monitor" : "do not duck the monitor"));
            _journal.CloseGroup();

            OnPropertyChanged(nameof(DuckWhenProgramSounds));
        }
    }

    public IReadOnlyList<string> Surfaces { get; } = ["none", "window"];

    public int SurfaceIndex
    {
        get => Rig is { } rig ? (int)rig.Surface : 0;
        set
        {
            if (_journal is null || Rig is not { } rig || value < 0 || value >= Surfaces.Count
                || (AuditionSurface)value == rig.Surface)
                return;

            _journal.Do(new SetValueCommand<AuditionSurface>(
                Guid.Empty, "auditionSurface", "audio",
                () => rig.Surface, surface => rig.Surface = surface, (AuditionSurface)value,
                $"audition surface: {Surfaces[value]}"));
            _journal.CloseGroup();

            OnPropertyChanged(nameof(SurfaceIndex));
            OnPropertyChanged(nameof(Size));
        }
    }

    /// <summary>What the audition canvas will be, once one is opened.</summary>
    public string Size
    {
        get
        {
            if (Rig is not { Surface: AuditionSurface.Window })
                return "audio only - no window is opened";

            if (Rig is { SurfaceWidth: > 0, SurfaceHeight: > 0 } sized)
                return $"{sized.SurfaceWidth}×{sized.SurfaceHeight}";

            // The monitor should not be smaller than the thing it is monitoring.
            var width = Project?.Compositions.Select(item => item.Width).DefaultIfEmpty(1280).Max() ?? 1280;
            var height = Project?.Compositions.Select(item => item.Height).DefaultIfEmpty(720).Max() ?? 720;

            return $"{width}×{height} · follows the largest composition";
        }
    }

    /// <summary>Re-reads the rig after an edit elsewhere, or an undo.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(DeviceIndex));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(DuckWhenProgramSounds));
        OnPropertyChanged(nameof(SurfaceIndex));
        OnPropertyChanged(nameof(Size));
    }
}
