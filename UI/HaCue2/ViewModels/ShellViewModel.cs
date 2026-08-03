using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Core.Validation;
using HaCue2.Engine;
using HaCue2.Machine;
using S.Media.Core.Audio;
using HaCue2.Sample;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// The one window: CUES · AUDIO · VIDEO · TARGETS, the title-bar latches, the status bar, and the
/// Output info drawer (register items 1–4).
/// </summary>
/// <remarks>
/// It owns the two things every view needs: the <see cref="ProjectJournal"/> (the document and its
/// undo history) and the <see cref="ShowRuntime"/> (what a running session would be saying). Views
/// read the document through projections and write it through the journal, so "what is on screen" and
/// "what would be saved" cannot drift apart.
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    public const string CuesView = "CUES";
    public const string AudioView = "AUDIO";
    public const string VideoView = "VIDEO";
    public const string TargetsView = "TARGETS";

    public ShellViewModel() : this(SampleProject.Create(), MachineFacts.Nothing)
    {
    }

    public ShellViewModel(HaCueProject project) : this(project, MachineFacts.Nothing)
    {
    }

    public ShellViewModel(HaCueProject project, MachineFacts machine)
    {
        Journal = new ProjectJournal(project);
        Machine = machine;

        // Real answers where this machine can give one, invented ones where it cannot — yet. Media
        // durations and broken files come from the PROBE now; sounding cues, meters and the bay still
        // come from the sample, because those need a running session and there is not one.
        Runtime = SampleRuntime.For(project);
        AdoptProbeResults();

        Environment = machine.Environment ?? new RuntimeEnvironment(Runtime, project);

        Cues = new CuesViewModel(Journal, Runtime)
        {
            // The inspector's track lists come from the probe. A cue whose file has not been looked at
            // yet still shows the choice it already holds — opening a show on a machine without the
            // media must not look like the choice was lost.
            MediaFacts = media => machine.Media.Facts(MediaPaths.Resolve(project, media.MediaPath, null)),
        };
        Audio = new AudioViewModel(Journal, Runtime);
        Video = new VideoViewModel(project, Runtime, Journal) { Audition = Audio.Audition };
        Targets = new TargetsViewModel(project, Runtime, Journal);
        OutputInfo = new OutputInfoViewModel(Runtime);

        // A project as loaded is clean, whatever its contents. The dirty flag answers "does this
        // differ from the file", not "is anything wrong with it".
        Journal.MarkSaved();
        Journal.Changed += OnJournalChanged;

        _status = ProjectStatus.Run(project, environment: Environment);

        // Fire and forget: the views draw now and the answers arrive later. Until a file has been
        // looked at its length reads "—", which is the truth rather than a guess.
        machine.Media.Changed += OnProbesLanded;
        machine.Media.Refresh(project);
    }

    /// <summary>The machine seam: what this box has, and what its files turned out to be.</summary>
    public MachineFacts Machine { get; }

    /// <summary>The machine-scope settings, for the panes and windows that read them.</summary>
    public AppSettings Settings { get; init; } = new();

    // ── autosave and recovery ─────────────────────────────────────────────────────────────────

    private DispatcherTimer? _autosave;

    /// <summary>
    /// Starts writing recovery copies on the project's own cadence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An autosave is written BESIDE the show, never over it: the operator's file must stay exactly
    /// what they last chose to write, and recovery is an offer made at the next launch rather than
    /// something that happened to their show while they were not looking.
    /// </para>
    /// <para>
    /// Only when the journal is dirty. A show sitting untouched through an interval would otherwise
    /// rewrite the same bytes every thirty seconds and roll its own history off the end.
    /// </para>
    /// </remarks>
    public void StartAutosave()
    {
        var seconds = Math.Clamp(Project.Settings.AutosaveSeconds, 5, 600);

        _autosave?.Stop();
        _autosave = new DispatcherTimer(
            TimeSpan.FromSeconds(seconds), DispatcherPriority.Background, (_, _) => _ = AutosaveAsync());

        _autosave.Start();
    }

    public void StopAutosave()
    {
        _autosave?.Stop();
        _autosave = null;
    }

    private bool _autosaving;

    private async Task AutosaveAsync()
    {
        // One at a time: a large show on a slow disk can take longer than the cadence, and stacking
        // writes would turn a 30 s timer into an unbounded queue of them.
        if (_autosaving || !Journal.IsDirty)
            return;

        _autosaving = true;

        try
        {
            var written = await RecoveryStore.SaveAsync(
                Project, Path, Journal.Log.Count, Project.Settings.RecoveryCopies, DateTimeOffset.Now)
                .ConfigureAwait(true);

            if (!written)
            {
                // Said once, then left alone: a read-only recovery directory fails on every tick, and
                // repeating it every thirty seconds would bury everything else in the status bar.
                FileMessage = "autosave could not be written — recovery is unavailable";
                StopAutosave();
            }
        }
        finally
        {
            _autosaving = false;
        }
    }

    /// <summary>The running show, once one has been started. Null until then, and on a machine
    /// that has no engine to start.</summary>
    public ShowHost? Host { get; private set; }

    private EngineRuntime? _engine;

    /// <summary>
    /// Starts the engine for this project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT in the constructor. Opening devices and a decoder is slow and can fail, and a
    /// view-model that cannot be constructed without hardware is one no test and no preview can build.
    /// A shell with no engine is a fully working EDITOR — which is also what the app is on a laptop.
    /// </para>
    /// <para>
    /// Every journal change reloads the compiled document. The session's preservation flags are what
    /// make that safe to do on a keystroke: a group whose voices are all still described unchanged
    /// keeps playing, and the GO cursors survive regardless.
    /// </para>
    /// </remarks>
    public async Task StartEngineAsync(IAudioBackend? backend)
    {
        if (Host is not null)
            return;

        _backend = backend;
        Host = await ShowHost.StartAsync(Project, backend, Runtime.MediaDurations).ConfigureAwait(true);

        // Register item 3: external input is OFF when a project opens, unless the show says otherwise.
        // A show that starts answering MIDI the instant it loads fires cues during a get-in.
        IsExternalInputEnabled = !Project.Settings.ExternalInputOffOnOpen;

        if (IsExternalInputEnabled)
            await Host.Triggers.SetEnabledAsync(true).ConfigureAwait(true);
        Cues.Engine = Host;
        Audio.NoteAudioStarted();

        _engine = new EngineRuntime(Host, Runtime, Project);
        _engine.Changed += () =>
        {
            Cues.Refresh();
            OnPropertyChanged(nameof(TransportHint));
        };

        // The Active panel's clocks move continuously; the tree does not. Two signals rather than one
        // keeps a 250 ms poll from rebuilding the cue tree under the operator's selection.
        _engine.Ticked += Cues.Tick;
        _engine.Ticked += OutputInfo.Refresh;
        _engine.Ticked += () => Diagnostics?.Refresh();

        // A patch or fade cue writes real cell gains that travel in the file. They are not undoable
        // and must not be, but the title bar has to stop claiming the project matches its file.
        Host.DocumentChangedByCue += () => Dispatcher.UIThread.Post(() =>
        {
            Journal.MarkDirty();
            Audio.Refresh();
        });

        Journal.Changed += ScheduleReload;
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(TransportHint));
    }

    /// <summary>
    /// Coalesces document edits into one engine reload.
    /// </summary>
    /// <remarks>
    /// A reload recompiles the whole document and hands it to the session. Doing that per COMMAND meant
    /// every keystroke in a cue label recompiled the show — the journal raises one change per character.
    /// The timer restarts on each edit, so a burst of typing costs one reload after it stops.
    /// </remarks>
    private DispatcherTimer? _reload;

    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    private void ScheduleReload()
    {
        _reload ??= new DispatcherTimer(ReloadDelay, DispatcherPriority.Background, (_, _) =>
        {
            _reload!.Stop();
            ReloadEngine();
        });

        _reload.Stop();
        _reload.Start();
    }

    /// <summary>Whether a session is running behind the transport buttons.</summary>
    public bool IsLive => Host is not null;

    public string TransportHint => Host is null
        ? "GO always works — editing never blocks playback"
        : Host.Problems.Count == 0
            ? "live · editing never blocks playback"
            : $"live · {Host.Problems.Count} line(s) would not open";

    private async void ReloadEngine()
    {
        if (Host is { } host)
        {
            // Durations go with the document: without them the compiler cannot convert an out-point
            // into the engine's end-offset, and an effect lane on an untrimmed cue has no length to
            // stretch over and is dropped.
            await host.ReloadAsync(Project, Runtime.MediaDurations).ConfigureAwait(true);
            OnPropertyChanged(nameof(TransportHint));
        }
    }

    /// <summary>
    /// Stops and restarts the audio engine, adopting a new mix rate or clock master.
    /// </summary>
    /// <remarks>
    /// A real stop and start, because the bus width and rate are fixed when the bay is built. Anything
    /// sounding goes silent — which is why this is a button rather than a consequence of editing a
    /// combo box, and why the operator is told so before they press it.
    /// </remarks>
    public async Task RestartAudioAsync()
    {
        if (_backend is not { } backend || Host is null)
        {
            // No engine to restart: adopting the new values is all there is to do, and the button
            // stops offering a restart that would do nothing.
            Audio.NoteAudioStarted();
            return;
        }

        await StopEngineAsync().ConfigureAwait(true);
        await StartEngineAsync(backend).ConfigureAwait(true);
    }

    /// <summary>The backend the shell was started with, kept so the engine can be restarted.</summary>
    private IAudioBackend? _backend;

    /// <summary>Stops the show and releases the devices.</summary>
    public async ValueTask StopEngineAsync()
    {
        if (_engine is not { } engine)
            return;

        Journal.Changed -= ScheduleReload;
        _reload?.Stop();
        _reload = null;
        _engine = null;
        Host = null;
        Cues.Engine = null;

        // Dropped rather than kept: it captured the host it was built with, and a Diagnostics window
        // reporting a session that no longer exists is worse than one saying there is none.
        Diagnostics = null;

        await engine.DisposeAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsLive));
    }

    private void OnProbesLanded()
    {
        // A probe finishes on a worker; everything below touches view-models bound to the UI.
        Dispatcher.UIThread.Post(() =>
        {
            AdoptProbeResults();
            Status = ProjectStatus.Run(Project, environment: Environment);
            Refresh();

            // A probe that has just landed changes what the COMPILED document should say — an
            // out-point becomes convertible and an effect lane gains the length it needs — so the
            // engine has to be told, even though nobody edited anything.
            if (Host is not null)
                ScheduleReload();
        });
    }

    /// <summary>
    /// Copies what the probe now knows into the runtime the views already hold.
    /// </summary>
    /// <remarks>
    /// Mutated in place rather than replaced: every view-model captured this instance when it was
    /// built, so a new object would leave all of them reading the old answers.
    /// </remarks>
    private void AdoptProbeResults()
    {
        Runtime.MediaDurations = new Dictionary<Guid, TimeSpan>(Machine.Media.DurationsIn(Project, null));
        Runtime.Broken = [.. Machine.Media.BrokenIn(Project, null)];

        // Only a DEFINITE absence counts. A line whose devices nobody could enumerate stays present
        // here and is reported as unchecked by the status pass — a red row nobody verified is the
        // failure this seam exists to avoid.
        if (Machine.Environment is { } environment)
        {
            Runtime.AbsentLines =
            [
                .. Project.AudioLines
                    .Where(line => environment.AudioLine(line) == DeviceAvailability.Absent)
                    .Select(line => line.Id),
            ];
        }
    }

    public ProjectJournal Journal { get; }
    public ShowRuntime Runtime { get; }
    public IProjectEnvironment Environment { get; }
    public HaCueProject Project => Journal.Project;

    public CuesViewModel Cues { get; }
    public AudioViewModel Audio { get; }
    public VideoViewModel Video { get; }
    public TargetsViewModel Targets { get; }
    public OutputInfoViewModel OutputInfo { get; }

    /// <summary>
    /// The Diagnostics window's view-model, once one has been opened.
    /// </summary>
    /// <remarks>
    /// Held here rather than by the window so the engine tick can reach it: the window is opened and
    /// closed at will, and its counters have to keep moving for as long as it is on screen.
    /// </remarks>
    public DiagnosticsViewModel? Diagnostics { get; private set; }

    /// <summary>Builds the Diagnostics view-model, or hands back the one already ticking.</summary>
    public DiagnosticsViewModel OpenDiagnostics() => Diagnostics ??= new DiagnosticsViewModel(Runtime, Host);

    public IReadOnlyList<string> Views { get; } = [CuesView, AudioView, VideoView, TargetsView];

    /// <summary>
    /// Where this project lives on disk, or empty when it has never been saved.
    /// </summary>
    /// <remarks>
    /// Tracked here rather than on the document because it is not a property of the SHOW — the same
    /// file copied to a booth machine is the same show at a different path, and writing the path into
    /// the document would make every copy differ from its original for no reason.
    /// </remarks>
    public string Path { get; private set; } = "";

    public bool HasPath => Path.Length > 0;

    /// <summary>The title bar's project name, with the mockup's unsaved marker.</summary>
    public string ProjectFile =>
        (HasPath ? System.IO.Path.GetFileName(Path) : Project.Title + HaCueProjectFile.Extension)
        + (Journal.IsDirty ? " *" : "");

    /// <summary>What the last file operation said, for the status bar.</summary>
    [ObservableProperty]
    private string _fileMessage = "";

    /// <summary>
    /// Saves to a known path, or reports that one is needed.
    /// </summary>
    /// <remarks>
    /// Returns false when there is nowhere to save YET, so the caller opens Save As rather than this
    /// silently doing nothing — a Ctrl+S that appears to work and did not is the worst outcome here.
    /// </remarks>
    public async Task<bool> SaveAsync()
    {
        if (!HasPath)
            return false;

        await SaveToAsync(Path).ConfigureAwait(true);
        return true;
    }

    /// <summary>Saves to a path the operator chose, and adopts it.</summary>
    public async Task SaveToAsync(string path)
    {
        var result = await ProjectFiles.SaveAsync(Project, ProjectFiles.WithExtension(path))
            .ConfigureAwait(true);

        FileMessage = result.Message;

        if (!result.Succeeded)
            return;

        Path = result.Path;
        // Clean means "matches the file", so this is the ONE place the flag may be cleared: after a
        // write that actually happened.
        Journal.MarkSaved();

        // The autosaves described a document that no longer differs from its file. Keeping them would
        // offer a recovery at the next launch for work that is already saved, which teaches the
        // operator to dismiss the banner without reading it.
        RecoveryStore.Clear(Path, Project.Title);
        Refresh();
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(HasPath));
    }

    /// <summary>Adopts a path for a project that was just opened from it.</summary>
    public void AdoptPath(string path)
    {
        Path = path;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(HasPath));
        OnPropertyChanged(nameof(ProjectFile));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPane))]
    private string _selectedView = CuesView;

    /// <summary>
    /// The Lock latch (register item 2): opt-in, never the launch state, read-only document, and GO
    /// untouched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    private bool _isLocked;

    /// <summary>
    /// One master toggle over MIDI/OSC/hotkey triggers, wall-clock schedules and MTC chase
    /// (register item 3). Off when a project opens; it never gates GO.
    /// </summary>
    [ObservableProperty]
    private bool _isExternalInputEnabled;

    /// <summary>
    /// Opens or closes the input devices to match the toggle.
    /// </summary>
    /// <remarks>
    /// The devices are opened only while the toggle is on rather than opened once and filtered: an app
    /// holding a MIDI port it is deliberately ignoring is a port another program cannot use, which is
    /// a rude thing to do to somebody's rig during a get-in.
    /// </remarks>
    partial void OnIsExternalInputEnabledChanged(bool value)
    {
        if (Host is { } host)
            _ = host.Triggers.SetEnabledAsync(value);
    }

    /// <summary>Register item 4 — hidden by default, F9 or the status-bar toggle summons it.</summary>
    [ObservableProperty]
    private bool _isOutputInfoOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary))]
    [NotifyPropertyChangedFor(nameof(IssueSummary))]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    private ProjectStatusReport _status;

    public object CurrentPane => SelectedView switch
    {
        AudioView => Audio,
        VideoView => Video,
        TargetsView => Targets,
        _ => Cues,
    };

    public string ModeLabel => IsLocked ? "LOCKED" : "EDITING";

    // ── status bar ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one-token health summary that replaced the permanent output strip (register item 4).
    /// </summary>
    /// <remarks>
    /// It reports on the OUTPUT checks only. A dangling jump target is a real problem but not an
    /// output problem, and a token that went red for everything would stop meaning anything.
    /// </remarks>
    public string OutputSummary => OutputChecks().Any(check => check.Outcome == CheckOutcome.Failed)
        ? "● outputs failing"
        : OutputChecks().Any(check => check.Outcome == CheckOutcome.Warning)
            ? "● outputs degraded"
            : "● outputs ok";

    public bool HasIssues => Status.Errors + Status.Warnings > 0;

    public string IssueSummary
    {
        get
        {
            var count = Status.Errors + Status.Warnings;
            return count == 0
                ? "no issues"
                : $"▲ {count} issue{(count == 1 ? "" : "s")} · Project status";
        }
    }

    public string UnsavedSummary => Journal.IsDirty
        ? $"{Journal.Log.Count} unsaved edit{(Journal.Log.Count == 1 ? "" : "s")}"
        : "no unsaved edits";

    public string SavedSummary => Journal.IsDirty ? "unsaved" : "saved";

    /// <summary>The undo toast: what ⌘Z would take back, and which surface it would change.</summary>
    public string? UndoSummary => Journal.NextUndo is { } command
        ? $"undo: {command.Domain} — {command.Description}"
        : null;

    public bool CanUndo => Journal.CanUndo;
    public bool CanRedo => Journal.CanRedo;

    public void Undo()
    {
        if (Journal.Undo())
            Refresh();
    }

    public void Redo()
    {
        if (Journal.Redo())
            Refresh();
    }

    private void OnJournalChanged()
    {
        // Re-running the status pass on every keystroke would be wasteful on a big show; it is cheap
        // here and the honest behaviour, and the real app debounces it the same way it debounces save.
        Status = ProjectStatus.Run(Project, environment: Environment);
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(ProjectFile));
        OnPropertyChanged(nameof(UnsavedSummary));
        OnPropertyChanged(nameof(SavedSummary));
        OnPropertyChanged(nameof(UndoSummary));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        Cues.Refresh();
        // One journal, every view: an undo made in the patch has to reach the cue tree too.
        Audio.Refresh();
        Video.Refresh();
    }

    private IEnumerable<StatusCheck> OutputChecks() =>
        Status.Checks.Where(check =>
            check.Name is "Audio devices" or "Video outputs" or "Logical outputs");
}

/// <summary>The Output info drawer's content (screen 02b) — entirely runtime facts.</summary>
/// <remarks>
/// Every member reads THROUGH the runtime. Copied values would freeze at the moment the drawer was
/// built, which for a meter is the same as not having one — and this drawer exists precisely for the
/// "why is there no sound" moment, where a stale reading is worse than a blank one.
/// </remarks>
public sealed class OutputInfoViewModel(ShowRuntime runtime) : ObservableObject
{
    public IReadOnlyList<ProgramMeter> Meters => runtime.Meters;
    public IReadOnlyList<OutputLineChip> Lines => runtime.LineChips;
    public string BaySummary => runtime.BaySummary.Length > 0 ? runtime.BaySummary : "no session";
    public string BayClock => runtime.BayClock;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Meters));
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(BaySummary));
        OnPropertyChanged(nameof(BayClock));
    }
}
