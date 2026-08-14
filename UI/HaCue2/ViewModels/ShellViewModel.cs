using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCue2.Core.Compile;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Core.Validation;
using HaCue2.Engine;
using HaCue2.Machine;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
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

    public ShellViewModel(
        HaCueProject project,
        MachineFacts machine,
        string path = "",
        AppSettings? settings = null)
    {
        Settings = settings ?? new AppSettings();
        Path = path;
        Journal = new ProjectJournal(project);
        _isLocked = project.Settings.OpenLocked;
        Journal.IsReadOnly = _isLocked;
        Machine = machine;

        // Real answers where this machine can give one, invented ones where it cannot - yet. Media
        // durations and broken files come from the PROBE now; sounding cues, meters and the bay still
        // come from the sample, because those need a running session and there is not one.
        Runtime = SampleRuntime.For(project);
        AdoptProbeResults();

        Environment = new ShellProjectEnvironment(
            machine.Environment,
            new RuntimeEnvironment(Runtime, project, () => ProjectPath),
            YouTubeRuntime.Availability);
        _youTubeContentRevision = YouTubeRuntime.Downloads.ContentRevision;

        Cues = new CuesViewModel(Journal, Runtime)
        {
            // The inspector's track lists come from the probe. A cue whose file has not been looked at
            // yet still shows the choice it already holds - opening a show on a machine without the
            // media must not look like the choice was lost.
            MediaFacts = media => machine.Media.Facts(MediaPaths.Resolve(project, media.MediaPath, ProjectPath)),
            ProjectPath = () => ProjectPath,
        };
        Cues.Inspector.CacheRoot = MediaCache.RootFor(Settings);
        Cues.Inspector.WaveformCacheBytes = MediaCache.ParseBudget(Settings.WaveformBudget);
        Cues.Timeline.CacheRoot = Cues.Inspector.CacheRoot;
        Cues.Timeline.WaveformCacheBytes = Cues.Inspector.WaveformCacheBytes;
        Cues.Timeline.ResolveMediaPath = Cues.Inspector.ResolveClipPath;
        // Resolve lazily through the current queue: changing the cache root replaces the shared queue,
        // and a method group captured here would keep the disposed old one for the rest of the project.
        Cues.Inspector.PreparedMediaPath = source => YouTubeRuntime.Downloads.PreparedAssetPath(source);
        Cues.Inspector.RememberTabs = Settings.RememberInspectorTab;
        Cues.FlatActiveList = Settings.FlatActiveList;
        Cues.Inspector.ProjectPath = () => ProjectPath;
        Cues.DoubleGoGuard = TimeSpan.FromMilliseconds(ParseDurationMilliseconds(Settings.DoubleGoGuard, 250));
        Cues.ConfirmStopAllThreshold = ParseCount(Settings.ConfirmStopAll, 3);
        Cues.AfterGo = SaveAfterGoAsync;
        Audio = new AudioViewModel(Journal, Runtime);
        Video = new VideoViewModel(project, Runtime, Journal) { Audition = Audio.Audition };
        Targets = new TargetsViewModel(project, Runtime, Journal, Settings);
        OutputInfo = new OutputInfoViewModel(Runtime);
        OutputInfo.ResetClips = ResetMeterClips;
        _isOutputInfoOpen = Settings.OpenDrawerOnLaunch;

        // A project as loaded is clean, whatever its contents. The dirty flag answers "does this
        // differ from the file", not "is anything wrong with it".
        Journal.MarkSaved();
        Journal.Changed += OnJournalChanged;

        _status = project.Settings.RunStatusChecksOnOpen
            ? ProjectStatus.Run(project, ProjectPath, Environment)
            : ProjectStatus.NotRun();

        // Fire and forget: the views draw now and the answers arrive later. Until a file has been
        // looked at its length reads "-", which is the truth rather than a guess.
        machine.Media.Changed += OnProbesLanded;
        machine.Media.Refresh(project, ProjectPath);
    }

    /// <summary>The machine seam: what this box has, and what its files turned out to be.</summary>
    public MachineFacts Machine { get; }

    /// <summary>The machine-scope settings, for the panes and windows that read them.</summary>
    public AppSettings Settings { get; init; } = new();

    private static int ParseCount(string value, int fallback)
    {
        var digits = new string([.. value.Where(char.IsAsciiDigit)]);
        return int.TryParse(digits, out var parsed) ? Math.Max(0, parsed) : fallback;
    }

    private static int ParseDurationMilliseconds(string value, int fallback)
    {
        var trimmed = value.Trim();
        var milliseconds = trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase);
        var number = trimmed.TrimEnd('s', 'S', 'm', 'M', ' ').Replace(',', '.');

        return double.TryParse(number, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var parsed)
               && parsed >= 0
            ? (int)Math.Clamp(Math.Round(milliseconds ? parsed : parsed * 1_000), 0, 60_000)
            : fallback;
    }

    private async Task SaveAfterGoAsync()
    {
        if (!Project.Settings.SaveOnGo)
            return;

        if (!HasPath)
        {
            FileMessage = "save on GO is enabled, but this project has not been saved yet";
            return;
        }

        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>Whether Space is an operator GO under the machine's hotkey policy.</summary>
    public bool AllowsSpaceGo(bool isTyping) => Settings.SpaceRule switch
    {
        "always GO" => true,
        "never" => false,
        _ => !isTyping,
    };

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
                FileMessage = "autosave could not be written - recovery is unavailable";
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
    /// A shell with no engine is a fully working EDITOR - which is also what the app is on a laptop.
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
        Host = await ShowHost.StartAsync(Project, backend, CompileContext()).ConfigureAwait(true);

        // Register item 3: external input is OFF when a project opens, unless the show says otherwise.
        // A show that starts answering MIDI the instant it loads fires cues during a get-in.
        IsExternalInputEnabled = !Project.Settings.ExternalInputOffOnOpen;

        if (IsExternalInputEnabled)
            await Host.Triggers.SetEnabledAsync(true).ConfigureAwait(true);
        Cues.Engine = Host;
        Video.Host = Host;
        Targets.Host = Host;
        Audio.Host = Host;
        Host.MachinePanicFadeMs = Settings.PanicFadeMs;

        // An edit the engine declined mid-show lands here, on the way into the next fire. Marshalled
        // because a fire can arrive from the MIDI/OSC thread or the remote API, while the reload reads
        // the document the UI is editing.
        Host.PendingEditFlush = () => Dispatcher.UIThread.InvokeAsync(FlushPendingEditAsync);
        Audio.NoteAudioStarted();

        _engine = new EngineRuntime(Host, Runtime, Project, Settings);
        _engine.Changed += () =>
        {
            Cues.Refresh();
            OnPropertyChanged(nameof(TransportHint));

            // What is sounding just changed, which is the ONLY thing that can turn a held edit into an
            // applicable one. Retrying here rather than on a timer is what keeps the deferral free: a
            // poll would have to recompile the document to find out, and doing that every couple of
            // seconds for the length of a track is the cost this mechanism exists to avoid.
            if (_editPending)
                _ = FlushPendingEditAsync();
        };

        // The Active panel's clocks move continuously; the tree does not. Two signals rather than one
        // keeps a 250 ms poll from rebuilding the cue tree under the operator's selection.
        _engine.Ticked += Cues.Tick;
        // Learn watches the same stream the wire monitor does, on the UI tick rather than the I/O
        // thread - the pane only has to know what arrived, not when.
        _engine.Ticked += () => Targets.Observe(Runtime.LastSignal);
        _engine.Ticked += OutputInfo.Refresh;
        // A recording that starts dropping frames does so quietly, so the readout is polled rather
        // than updated on arm and disarm - the drop count is the only warning before the file gaps.
        _engine.Ticked += Audio.RefreshRecorders;
        _engine.Ticked += Video.RefreshRecorders;
        _engine.Ticked += () => Diagnostics?.Refresh();

        // A patch or fade cue writes real cell gains that travel in the file. They are not undoable
        // and must not be, but the title bar has to stop claiming the project matches its file.
        Host.DocumentChangedByCue += change => Dispatcher.UIThread.Post(() =>
        {
            if (change.Patch is { } patch)
            {
                foreach (var destination in patch)
                {
                    var cell = Project.AudioPatch.Cells.FirstOrDefault(candidate =>
                        candidate.LogicalChannelId == destination.LogicalChannelId
                        && candidate.LineId == destination.LineId
                        && candidate.LineChannel == destination.LineChannel);
                    if (cell is null)
                        continue;

                    cell.GainDb = destination.GainDb;
                    cell.Muted = destination.Muted;
                }
            }

            if (change.AuditionLevelDb is { } auditionLevel)
                Project.Audition.LevelDb = auditionLevel;

            // The engine already executed this state. Marking it dirty must update the title without
            // scheduling a document reload into the middle of its own fade ramp.
            Journal.MarkDirty(documentChanged: false);
            Audio.Refresh();
            Refresh();
        });

        // Register item 24: the remote API is off unless the project asks for it. A cue player that
        // answers the network by default can be fired by anything on the venue wifi.
        await EnsureRemoteApiAsync().ConfigureAwait(true);

        Journal.Changed += ScheduleReload;
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(TransportHint));
    }

    /// <summary>
    /// Coalesces document edits into one engine reload.
    /// </summary>
    /// <remarks>
    /// A reload recompiles the whole document and hands it to the session. Doing that per COMMAND meant
    /// every keystroke in a cue label recompiled the show - the journal raises one change per character.
    /// The timer restarts on each edit, so a burst of typing costs one reload after it stops.
    /// </remarks>
    private DispatcherTimer? _reload;

    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The backstop for a held edit, behind the transport events that normally apply it.
    /// </summary>
    /// <remarks>
    /// A held edit is retried when what is SOUNDING changes and on the way into the next fire, which
    /// between them cover every way a cue can stop. This is the safety net for anything those miss, and
    /// it is deliberately slow: each attempt recompiles the document to ask whether it is safe yet, and
    /// doing that several times a second for a fifty-minute track would re-introduce, on the UI thread,
    /// exactly the cost this whole mechanism exists to remove.
    /// </remarks>
    private static readonly TimeSpan HeldEditRetry = TimeSpan.FromSeconds(15);

    /// <summary>An edit the engine has not adopted yet - see <see cref="ReloadEngineAsync"/>.</summary>
    private bool _editPending;

    private void ScheduleReload()
    {
        _editPending = true;
        _reload ??= new DispatcherTimer(ReloadDelay, DispatcherPriority.Background, (_, _) =>
        {
            _reload!.Stop();
            _ = ReloadEngineAsync();
        });

        _reload.Stop();
        _reload.Interval = ReloadDelay;
        _reload.Start();
    }

    /// <summary>Whether a session is running behind the transport buttons.</summary>
    public bool IsLive => Host is not null;

    /// <summary>True while an edit is waiting for the show to be idle before the engine adopts it.</summary>
    /// <remarks>
    /// Surfaced rather than hidden - the transport row says so. An operator who edits a playing cue's
    /// trim and hears nothing change is owed the reason; the alternative was to restart their cue,
    /// which is worse and was what the app used to do.
    /// </remarks>
    public bool HasHeldEdit => _heldEdit;

    private bool _heldEdit;

    private void NoteHeldEdit(bool held)
    {
        if (_heldEdit == held)
            return;

        _heldEdit = held;
        // The visible transport hint is the cue view's, which is the row an operator is actually
        // looking at while a show runs.
        Cues.HasHeldEdit = held;
        OnPropertyChanged(nameof(HasHeldEdit));
        OnPropertyChanged(nameof(TransportHint));
    }

    public string TransportHint => Host is null
        ? "GO always works - editing never blocks playback"
        : _heldEdit
            ? "live · an edit is waiting for this cue to end"
            : Host.Problems.Count == 0
                ? "live · editing never blocks playback"
                : $"live · {Host.Problems.Count} line(s) would not open";

    /// <summary>
    /// Hands the current document to the engine, unless doing so would interrupt the show.
    /// </summary>
    /// <remarks>
    /// The engine refuses a reload that would restart a playing voice (see
    /// <c>ShowHost.TryReloadAsync</c>). When it does the edit stays pending, and it is re-offered the
    /// moment what is sounding changes, on the way into the next fire, and on a slow backstop timer -
    /// so it lands as soon as the cue it would have cut is out of the way.
    /// </remarks>
    private async Task ReloadEngineAsync()
    {
        if (Host is not { } host)
            return;

        // One at a time. This is reached from the debounce, from the transport's own change signal and
        // from a fire on any thread; two overlapping attempts would compile the same document twice and
        // race each other's held-edit bookkeeping.
        if (_reloadInFlight)
            return;

        _reloadInFlight = true;

        try
        {
            // Durations go with the document: the compiler needs them for end offsets and for the
            // one-time conversion of an unresolved schema-1 normalized lane. Schema-2 automation is
            // already absolute and never stretches to a probe result.
            var adopted = await host.TryReloadAsync(Project, CompileContext()).ConfigureAwait(true);

            if (!adopted)
            {
                // Held, not lost. Re-armed here rather than left to the next edit: an operator who
                // makes one change and then watches the show would otherwise never see it applied.
                ScheduleHeldEditRetry();
                NoteHeldEdit(true);
                return;
            }

            _editPending = false;
            NoteHeldEdit(false);

            await EnsureRemoteApiAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(TransportHint));
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            FileMessage = $"engine reload failed - {failure.Message}";
        }
        finally
        {
            _reloadInFlight = false;
        }
    }

    private bool _reloadInFlight;

    private void ScheduleHeldEditRetry()
    {
        ScheduleReload();
        _reload!.Stop();
        _reload.Interval = HeldEditRetry;
        _reload.Start();
    }

    /// <summary>
    /// Applies a held edit before something needs the document to be current.
    /// </summary>
    /// <remarks>
    /// Called on the way into a fire and after a stop. A cue fired against a document the operator has
    /// since edited plays the old version of itself, which is the one failure this whole deferral must
    /// not introduce - so the moment a reload can be adopted, it is.
    /// </remarks>
    public async Task FlushPendingEditAsync()
    {
        if (!_editPending || Host is null)
            return;

        _reload?.Stop();
        await ReloadEngineAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Stops and restarts the audio engine, adopting a new mix rate or clock master.
    /// </summary>
    /// <remarks>
    /// A real stop and start, because the bus width and rate are fixed when the bay is built. Anything
    /// sounding goes silent - which is why this is a button rather than a consequence of editing a
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

    /// <summary>
    /// Arms or disarms a record or stream target, reporting what stopped it.
    /// </summary>
    /// <remarks>
    /// Refuses rather than pretends when no show is running: arming is opening a file and starting an
    /// encoder, and there is nothing to record from until the engine is up.
    /// </remarks>
    public async Task<string?> ToggleRecorderAsync(Guid id)
    {
        if (_engine is not { } engine)
            return "the show is not running - start it before arming a recording";

        return await engine.ToggleRecorderAsync(id).ConfigureAwait(true);
    }

    /// <summary>
    /// Flashes an output's name on it, reporting why it could not.
    /// </summary>
    /// <remarks>
    /// Refuses rather than pretends with no show running, like arming a recorder does: there is no
    /// window open to flash anything on, and a button that appeared to work over a dark projector
    /// would send somebody to check a cable.
    /// </remarks>
    public async Task<string?> IdentifyAsync(Guid outputId)
    {
        if (_engine is not { } engine)
            return "the show is not running - start it before identifying an output";

        return await engine.IdentifyAsync(outputId).ConfigureAwait(true);
    }

    public async Task<string?> SetCalibrationAsync(Guid outputId, bool enabled)
    {
        if (_engine is not { } engine)
            return "the show is not running - start it before calibrating an output";
        return await engine.SetCalibrationAsync(outputId, enabled).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs a timeline group from the sheet's playhead, reporting why it could not.
    /// </summary>
    /// <remarks>
    /// Refuses with no show running, like the other engine verbs. The transport's GO deliberately still
    /// works without one - it moves the cursor, which is the half that can be right with no devices -
    /// but "play this from here" has no half that is meaningful without something to play it on.
    /// </remarks>
    public async Task<string?> PlayTimelineFromAsync(GroupCueNode group, TimeSpan from)
    {
        if (_engine is not { } engine)
            return "the show is not running - start it before playing from the playhead";

        await engine.PlayTimelineFromAsync(group, from).ConfigureAwait(true);
        return null;
    }

    /// <summary>
    /// Stops every child of one timeline group.
    /// </summary>
    /// <remarks>
    /// Scoped to the group on purpose. Stop-all is a separate, deliberate verb in the transport bar;
    /// an operator pressing ⏹ inside a scene's timeline is asking for that scene to stop, not for the
    /// bed running under the whole act from another list.
    /// </remarks>
    public async Task<string?> StopTimelineAsync(GroupCueNode group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (Host is not { } host)
            return "the show is not running";

        // The whole SUBTREE, not only direct children: a timeline can hold a nested group, and firing
        // that group at its offset starts grandchildren the direct walk never reached - so the scene's
        // ⏹ left them playing. Depth-first, children before their group, so a nested playlist's
        // current item is released before anything could advance it.
        foreach (var child in Subtree(group))
            await host.StopCueAsync(child.Id).ConfigureAwait(true);

        return null;

        static IEnumerable<CueNode> Subtree(GroupCueNode parent)
        {
            foreach (var child in parent.Children)
            {
                if (child is GroupCueNode nested)
                    foreach (var descendant in Subtree(nested))
                        yield return descendant;

                yield return child;
            }
        }
    }

    /// <summary>
    /// Solos one logical output to the audition monitor, or clears the solo.
    /// </summary>
    /// <remarks>
    /// Monitoring, always allowed: it rewrites the MONITOR line's own patch row and nothing else, so it
    /// never reaches the program mix and never appears in the Active list (register item 13).
    /// </remarks>
    public string? SoloToMonitor(Guid channelId) =>
        _engine is { } engine
            ? engine.SoloToMonitor(channelId)
            : "the show is not running - start it before soloing an output";

    /// <summary>What the monitor is carrying, or null.</summary>
    public Guid? SoloedChannelId => _engine?.SoloedChannelId;

    /// <summary>The backend the shell was started with, kept so the engine can be restarted.</summary>
    private IAudioBackend? _backend;

    /// <summary>The remote API, when the project turned it on.</summary>
    public RemoteApiServer? Remote { get; private set; }

    private RemoteConfiguration? _remoteConfiguration;
    private readonly SemaphoreSlim _remoteLifecycle = new(1, 1);

    private sealed record RemoteConfiguration(int Port, bool LanAllowed, string Token);

    private RemoteConfiguration? EffectiveRemoteConfiguration()
    {
        if (Project.Settings.RemoteApi is { } project)
            return project.Enabled
                ? new RemoteConfiguration(project.Port, project.LanAllowed, Settings.EnsureRemoteToken())
                : null;

        if (!string.Equals(Settings.RemoteDefault, "on", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!int.TryParse(Settings.RemotePort, out var port) || port is < 1 or > 65_535)
        {
            FileMessage = $"remote API port “{Settings.RemotePort}” is invalid - use 1–65535";
            return null;
        }

        return new RemoteConfiguration(port, Settings.RemoteLanAllowed, Settings.EnsureRemoteToken());
    }

    /// <summary>Applies machine settings that affect an already-open transport.</summary>
    public void ApplyApplicationSettings()
    {
        Cues.DoubleGoGuard = TimeSpan.FromMilliseconds(ParseDurationMilliseconds(Settings.DoubleGoGuard, 250));
        Cues.ConfirmStopAllThreshold = ParseCount(Settings.ConfirmStopAll, 3);
        Cues.Inspector.RememberTabs = Settings.RememberInspectorTab;
        Cues.FlatActiveList = Settings.FlatActiveList;
        Cues.Tick();
        if (Host is { } host)
            host.MachinePanicFadeMs = Settings.PanicFadeMs;
        _ = ApplyRemoteSettingsAsync();
    }

    /// <summary>Clears the program-bus clip latch; invoked by meter clicks and Diagnostics.</summary>
    public void ResetMeterClips() => Host?.ResetMeterClips();

    private async Task ApplyRemoteSettingsAsync()
    {
        try
        {
            await EnsureRemoteApiAsync().ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            FileMessage = $"the remote API could not apply its settings - {failure.Message}";
        }
    }

    private async Task EnsureRemoteApiAsync()
    {
        await _remoteLifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            if (Host is not { } host)
                return;

            var wanted = EffectiveRemoteConfiguration();
            if (Equals(wanted, _remoteConfiguration)
                && (Remote is not null) == (wanted is not null))
                return;

            if (Remote is { } old)
            {
                await old.DisposeAsync().ConfigureAwait(true);
                Remote = null;
                Targets.Remote = null;
            }

            _remoteConfiguration = wanted;
            if (wanted is null)
                return;

            Remote = new RemoteApiServer(host, host.SnapshotProject, wanted.Token);
            Remote.Problem += problem => Dispatcher.UIThread.Post(() => FileMessage = problem);
            await Remote.StartAsync(wanted.Port, wanted.LanAllowed).ConfigureAwait(true);
            Targets.Remote = Remote;
            AppSettingsStore.Save(Settings);
        }
        finally
        {
            _remoteLifecycle.Release();
        }
    }

    /// <summary>Stops the show and releases the devices.</summary>
    public async ValueTask StopEngineAsync()
    {
        if (_engine is not { } engine)
            return;

        // Make queued settings notifications observe a stopped host before they enter the serialized
        // remote lifecycle and accidentally recreate a listener during shutdown.
        _engine = null;
        Host = null;
        Cues.Engine = null;
        Video.Host = null;
        Targets.Host = null;
        Audio.Host = null;

        // Nothing is holding the edit back any more, and StartEngineAsync loads the current document
        // outright - so a flag left set here would only buy the next session one pointless reload.
        _reload?.Stop();
        _editPending = false;
        NoteHeldEdit(false);

        await _remoteLifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            if (Remote is { } remote)
            {
                await remote.DisposeAsync().ConfigureAwait(true);
                Remote = null;
                Targets.Remote = null;
            }
            _remoteConfiguration = null;
        }
        finally
        {
            _remoteLifecycle.Release();
        }

        Journal.Changed -= ScheduleReload;
        _reload?.Stop();
        _reload = null;

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
            Status = ProjectStatus.Run(Project, ProjectPath, Environment);
            Refresh();

            // A probe that has just landed changes what the COMPILED document should say - an
            // out-point becomes convertible and an unresolved schema-1 lane can migrate - so the
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
        Runtime.MediaDurations = new Dictionary<Guid, TimeSpan>(Machine.Media.DurationsIn(Project, ProjectPath));
        Runtime.Broken = [.. Machine.Media.BrokenIn(Project, ProjectPath)];

        // Only a DEFINITE absence counts. A line whose devices nobody could enumerate stays present
        // here and is reported as unchecked by the status pass - a red row nobody verified is the
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
    /// Tracked here rather than on the document because it is not a property of the SHOW - the same
    /// file copied to a booth machine is the same show at a different path, and writing the path into
    /// the document would make every copy differ from its original for no reason.
    /// </remarks>
    public string Path { get; private set; } = "";

    private string? ProjectPath => HasPath ? Path : null;

    private ShowCompileContext CompileContext() => new()
    {
        ProjectPath = ProjectPath,
        Durations = Runtime.MediaDurations,
        Tracks = Machine.Media.TracksIn(Project, ProjectPath),
        PreparedSubtitlePaths = YouTubeRuntime.PreparedSubtitlePaths(Project),
    };

    public bool HasPath => Path.Length > 0;

    /// <summary>The title bar's project name, with the mockup's unsaved marker.</summary>
    public string ProjectFile =>
        (HasPath ? System.IO.Path.GetFileName(Path) : Project.Title + HaCueProjectFile.Extension)
        + (Journal.IsDirty ? " *" : "");

    /// <summary>
    /// The WINDOW's title - the file, its unsaved marker, then the app.
    /// </summary>
    /// <remarks>
    /// File first because that is what a taskbar truncates TO, and what an operator running two shows
    /// side by side is trying to tell apart. It was the literal "HaCue2", which meant nothing anywhere
    /// on screen said which file was open or whether it had been saved.
    /// </remarks>
    public string WindowTitle => $"{ProjectFile} - HaCue2";

    /// <summary>
    /// Where the project lives, in full - the answer to "where is this saved".
    /// </summary>
    /// <remarks>
    /// A tooltip rather than a field: it is long, it is asked for rarely, and it is the one thing the
    /// short name in the title bar cannot say.
    /// </remarks>
    public string ProjectLocation =>
        HasPath ? Path : "not saved yet - Save will ask where to put it";

    /// <summary>SAVE, or SAVE… when there is nowhere to save yet and it will have to ask.</summary>
    public string SaveLabel => HasPath ? "Save" : "Save…";

    /// <summary>
    /// What the File menu's recent list does when a row is chosen.
    /// </summary>
    /// <remarks>
    /// Handed in by the window rather than done here: opening another project closes this window and
    /// opens another, which is a decision about WINDOWS and belongs to the thing that owns them.
    /// </remarks>
    public Action<string>? OpenRecent { get; set; }

    [RelayCommand]
    private void OpenRecentProject(RecentProjectRow? recent)
    {
        if (recent is { IsMissing: false })
            OpenRecent?.Invoke(recent.Path);
    }

    /// <summary>What the operator has opened before, for the File menu's recent list.</summary>
    public IReadOnlyList<RecentProjectRow> Recents =>
    [
        .. Settings.Recents
            // The project already open is not somewhere to go.
            .Where(recent => !string.Equals(recent.Path, Path, StringComparison.OrdinalIgnoreCase))
            .Select(recent => new RecentProjectRow
            {
                Name = recent.Title.Length > 0
                    ? recent.Title
                    : System.IO.Path.GetFileNameWithoutExtension(recent.Path),
                Path = recent.Path,
                Contents = recent.Summary,
                IsMissing = !File.Exists(recent.Path),
            }),
    ];

    /// <summary>What the last file operation said, for the status bar.</summary>
    [ObservableProperty]
    private string _fileMessage = "";

    /// <summary>
    /// Saves to a known path, or reports that one is needed.
    /// </summary>
    /// <remarks>
    /// Returns false when there is nowhere to save YET, so the caller opens Save As rather than this
    /// silently doing nothing - a Ctrl+S that appears to work and did not is the worst outcome here.
    /// </remarks>
    public async Task<bool> SaveAsync()
    {
        if (!HasPath)
            return false;

        await SaveToAsync(Path).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Raised with the path a save actually landed on.
    /// </summary>
    /// <remarks>
    /// So the application can record it as a recent. The shell cannot: which files this MACHINE has
    /// opened is app-scope state, and a view-model over one document is the wrong owner for it.
    /// </remarks>
    public event Action<string>? Saved;

    /// <summary>One save at a time: a double Ctrl+S queues rather than interleaving two writes.</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>Saves to a path the operator chose, and adopts it.</summary>
    /// <remarks>
    /// The document is serialized HERE, on the UI thread, together with the journal revision - one
    /// atomic reading of "what is being saved". The UI stays editable while the bytes go to disk, so
    /// an edit can land mid-write; <see cref="ProjectJournal.MarkSavedIfCurrent"/> then keeps the
    /// document dirty (and its recovery copies alive) instead of calling it clean while it differs
    /// from the file - which is how a save that "worked" used to lose the edit made during it.
    /// </remarks>
    public async Task SaveToAsync(string path)
    {
        await _saveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var revision = Journal.Revision;
            var json = HaCueProjectFile.Serialize(Project);
            var result = await ProjectFiles.SaveSerializedAsync(json, ProjectFiles.WithExtension(path))
                .ConfigureAwait(true);

            FileMessage = result.Message;

            if (!result.Succeeded)
                return;

            Path = result.Path;
            Machine.Media.Refresh(Project, ProjectPath);

            // Clean means "matches the file", so it may only be recorded when the document still IS
            // the snapshot that reached the disk.
            if (Journal.MarkSavedIfCurrent(revision))
            {
                // The autosaves described a document that no longer differs from its file. Keeping
                // them would offer a recovery at the next launch for work that is already saved,
                // which teaches the operator to dismiss the banner without reading it.
                RecoveryStore.Clear(Path, Project.Title);
            }
            else
            {
                // The file is valid and holds the captured snapshot; the edits made during the write
                // are simply not in it yet. Dirty stays on, recovery stays armed, and the operator is
                // told why the title still shows the marker they expected to disappear.
                FileMessage = $"{result.Message} · an edit landed during the save - save again to include it";
            }

            Refresh();
            RaisePathProperties();
            Saved?.Invoke(Path);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>Adopts a path for a project that was just opened from it.</summary>
    public void AdoptPath(string path)
    {
        Path = path;
        RaisePathProperties();
    }

    /// <summary>Everything on screen that names WHERE the project is.</summary>
    private void RaisePathProperties()
    {
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(HasPath));
        OnPropertyChanged(nameof(ProjectFile));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ProjectLocation));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(Recents));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPane))]
    private string _selectedView = CuesView;

    /// <summary>
    /// The Lock latch (register item 2): read-only document and GO untouched. Projects whose own
    /// OpenLocked setting is on start in this state.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isLocked;

    public bool CanEdit => !IsLocked;

    partial void OnIsLockedChanged(bool value)
    {
        Journal.IsReadOnly = value;
        Refresh();
    }

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

    /// <summary>Register item 4 - hidden by default, F9 or the status-bar toggle summons it.</summary>
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
            : OutputChecks().Any(check => check.Outcome == CheckOutcome.NotChecked)
                ? "○ outputs not checked"
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

    /// <summary>Small persistent readout for work that outlives the YouTube authoring dialog.</summary>
    public string YouTubeDownloadSummary
    {
        get
        {
            var snapshot = YouTubeRuntime.Downloads.Snapshot(
                Project.AllCues().OfType<MediaCueNode>().Select(cue => cue.MediaPath));
            if (snapshot.Downloading > 0)
            {
                var phase = snapshot.Phase switch
                {
                    S.Media.Source.YouTube.YouTubePreparePhase.DownloadingVideo => "video",
                    S.Media.Source.YouTube.YouTubePreparePhase.DownloadingAudio => "audio",
                    S.Media.Source.YouTube.YouTubePreparePhase.DownloadingThumbnail => "thumbnail",
                    S.Media.Source.YouTube.YouTubePreparePhase.Remuxing => "assembling",
                    _ => "resolving",
                };
                return $"↓ YouTube {phase} {snapshot.Fraction * 100:0}%"
                       + (snapshot.Queued > 0 ? $" · {snapshot.Queued} queued" : "");
            }
            if (snapshot.Queued > 0)
                return $"↓ {snapshot.Queued} YouTube queued";
            return snapshot.Failed > 0
                ? $"▲ {snapshot.Failed} YouTube download{(snapshot.Failed == 1 ? "" : "s")} failed"
                : "";
        }
    }

    /// <summary>Progress-only refresh; deliberately does not recompile the show.</summary>
    public void RefreshYouTubeDownloadProgress() =>
        OnPropertyChanged(nameof(YouTubeDownloadSummary));

    /// <summary>A committed/deleted/failed asset changes preflight and can change playable subtitles.</summary>
    public void RefreshYouTubeReadiness()
    {
        Status = ProjectStatus.Run(Project, ProjectPath, Environment);
        OnPropertyChanged(nameof(YouTubeDownloadSummary));
        Cues.Inspector.RefreshPreparedMedia();
        var contentRevision = YouTubeRuntime.Downloads.ContentRevision;
        if (Host is not null && contentRevision != _youTubeContentRevision)
            ScheduleReload();
        _youTubeContentRevision = contentRevision;
    }

    private long _youTubeContentRevision;

    public string UnsavedSummary => Journal.IsDirty
        ? $"{Journal.Log.Count} unsaved edit{(Journal.Log.Count == 1 ? "" : "s")}"
        : "no unsaved edits";

    public string SavedSummary => Journal.IsDirty ? "unsaved" : "saved";

    /// <summary>The undo toast: what ⌘Z would take back, and which surface it would change.</summary>
    public string? UndoSummary => Journal.NextUndo is { } command
        ? $"undo: {command.Domain} - {command.Description}"
        : null;

    public bool CanUndo => !IsLocked && Journal.CanUndo;
    public bool CanRedo => !IsLocked && Journal.CanRedo;

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

    /// <summary>
    /// Re-reads everything that depends on the document, for every edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GUARDED, and that is the important part. This is the FIRST subscriber to
    /// <see cref="ProjectJournal.Changed"/> and <see cref="ScheduleReload"/> is a later one - and a
    /// multicast delegate stops at the first handler that throws. So anything escaping here did not
    /// merely lose a refresh: it stopped the ENGINE ever being told about that edit, or any edit after
    /// it, for the rest of the session. The document and what the rig was playing then diverged
    /// silently, which is how a cue that had just been edited stopped behaving like the cue on screen.
    /// </para>
    /// <para>
    /// Worse, the journal is usually driven from a binding setter, where Avalonia catches the throw and
    /// files it as a validation error - so the whole failure was invisible. Reported here instead.
    /// </para>
    /// </remarks>
    private void OnJournalChanged()
    {
        try
        {
            // An edit can ADD media, and until this the runtime only learned what a file was at load
            // and after a save - so a cue added at 19:50 read "-" for its length until the show was
            // saved. The adopt is a dictionary walk over what is already known (which is where a
            // source's own stated duration arrives), and the probe skips every path it has already
            // looked at.
            AdoptProbeResults();
            Machine.Media.Refresh(Project, ProjectPath);

            // The status pass is DEFERRED; the views are not. See ScheduleStatus for why.
            ScheduleStatus();
            Refresh();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Swallowed only in the sense that the OTHER subscribers still run: the operator is told,
            // and the engine still gets its reload. A stale panel is recoverable; a rig that stopped
            // hearing about edits is not.
            FileMessage = $"the project view could not be refreshed - {failure.Message}";
            MediaDiagnostics.LogError(failure, "HaCue2: refreshing the shell after an edit failed");
        }
    }

    private DispatcherTimer? _statusPass;

    /// <summary>
    /// Coalesces the project-status pass, which used to run on every single journal command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProjectStatus.Run"/> is not a cheap read: it COMPILES the whole show and runs the
    /// engine's document validator over the result, on top of touching the filesystem once per media
    /// reference. Running that synchronously per command meant a mesh-warp drag or a send-matrix drag -
    /// gestures that emit one command per pointer sample - compiled and validated the entire project
    /// tens of times a second on the UI thread, which is what made live editing crawl. Under workstation
    /// GC the resulting garbage was also heard, not just seen.
    /// </para>
    /// <para>
    /// Deferred rather than dropped: the status bar's counts are a few hundred milliseconds behind a
    /// burst of typing and exactly right the moment it stops, which is the same trade the engine reload
    /// beside it has always made. The pass still runs in full - nothing here decides that an edit
    /// "cannot" have changed the answer, because that judgement is precisely what a validator is for.
    /// </para>
    /// </remarks>
    private void ScheduleStatus()
    {
        _statusPass ??= new DispatcherTimer(StatusDelay, DispatcherPriority.Background, (_, _) =>
        {
            _statusPass!.Stop();
            RunStatus();
        });

        _statusPass.Stop();
        _statusPass.Start();
    }

    private static readonly TimeSpan StatusDelay = TimeSpan.FromMilliseconds(250);

    private void RunStatus()
    {
        try
        {
            Status = ProjectStatus.Run(Project, ProjectPath, Environment);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            FileMessage = $"the project checks could not be run - {failure.Message}";
            MediaDiagnostics.LogError(failure, "HaCue2: the project status pass failed");
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(ProjectFile));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(UnsavedSummary));
        OnPropertyChanged(nameof(SavedSummary));
        OnPropertyChanged(nameof(UndoSummary));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanEdit));
        Cues.Refresh();
        // One journal, every view: an undo made in the patch has to reach the cue tree too.
        Audio.Refresh();
        Video.Refresh();
        Targets.Refresh();
    }

    private IEnumerable<StatusCheck> OutputChecks() =>
        Status.Checks.Where(check =>
            check.Name is "Audio devices" or "Video outputs" or "Logical outputs");
}

/// <summary>The Output info drawer's content (screen 02b) - entirely runtime facts.</summary>
/// <remarks>
/// Every member reads THROUGH the runtime. Copied values would freeze at the moment the drawer was
/// built, which for a meter is the same as not having one - and this drawer exists precisely for the
/// "why is there no sound" moment, where a stale reading is worse than a blank one.
/// </remarks>
public sealed class OutputInfoViewModel(ShowRuntime runtime) : ObservableObject
{
    public Action? ResetClips { get; set; }

    public void ResetMeterClips() => ResetClips?.Invoke();

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
