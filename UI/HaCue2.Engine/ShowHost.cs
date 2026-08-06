using System.Diagnostics;
using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Core.Patch;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Present.SDL3;
using S.Media.Decode.FFmpeg;
using S.Media.Decode.FFmpeg.Audio;
using S.Control;
using S.Media.Routing;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>One cue that is holding a voice, and how far into it the show is.</summary>
/// <param name="Elapsed">
/// The transport's own playhead — where the clip actually IS. It used to be wall time since the fire,
/// which is a different number the moment anything is paused, seeked, trimmed or loops.
/// </param>
/// <param name="Length">
/// What the transport says the clip runs for, or null when the cue holds no transport (a visualizer) or
/// none has been reported yet. Preferred over the file probe's answer because it already accounts for
/// the trim, and because it exists on a machine that has not probed the file.
/// </param>
/// <param name="IsFading">Whether something has asked it to come down.</param>
public sealed record ActiveCueState(
    Guid CueId, Guid ListId, TimeSpan Elapsed, TimeSpan? Length, bool IsFading);

/// <summary>What the show is doing right now, as one snapshot.</summary>
/// <param name="Sounding">Cue ids currently playing.</param>
/// <param name="Standby">Per cue-list, the cue GO would fire next.</param>
/// <param name="Active">The same cues as <paramref name="Sounding"/>, with their clocks.</param>
public sealed record ShowState(
    IReadOnlySet<Guid> Sounding,
    IReadOnlyDictionary<Guid, Guid> Standby,
    IReadOnlyList<ActiveCueState> Active,
    bool IsPaused,
    IReadOnlyList<string> Problems)
{
    public static ShowState Idle { get; } = new(
        new HashSet<Guid>(), new Dictionary<Guid, Guid>(), [], false, []);
}

/// <summary>One patch-cell destination written by a performance cue.</summary>
public sealed record RuntimePatchChange(
    Guid LogicalChannelId,
    Guid LineId,
    int LineChannel,
    double GainDb,
    bool Muted);

/// <summary>A runtime action that also changes values persisted in the authoring document.</summary>
public sealed record RuntimeDocumentChange(
    IReadOnlyList<RuntimePatchChange>? Patch = null,
    double? AuditionLevelDb = null);

/// <summary>
/// The running show: a session, the project patch bay, and the document that joins them.
/// </summary>
/// <remarks>
/// <para>
/// One object the app starts and stops. Everything thread-affine and device-holding lives behind it,
/// and the UI never touches <c>ShowSession</c> directly — it asks for a <see cref="ShowState"/> and
/// calls transport verbs.
/// </para>
/// <para>
/// <b>Reloads preserve.</b> Every edit recompiles and reloads the whole document, so
/// <c>preserveActiveGroups</c> and <c>preserveMatchingCompositions</c> are ON: a group whose voices
/// are all still described unchanged keeps playing, and GO cursors survive regardless.
/// </para>
/// <para>
/// <b>What a cue MEANS is decided here.</b> The compiled document carries a <c>CueDefinition</c> for
/// every cue so the session's cursor can stand on any of them, but only media and visualizer cues have
/// anything to play. Groups, jumps, fades, patches, actions and comments are resolved by this class
/// when they fire — the session has no vocabulary for them and should not grow one.
/// </para>
/// <para>
/// <b>Split across partials by what each part TALKS TO</b>, not by size: this file owns the lifecycle
/// (devices in, document in, everything back out again), <c>Transport</c> the operator's verbs and
/// what is sounding, <c>Execution</c> the <see cref="ICueExecutionHost"/> surface the executor drives,
/// <c>Audition</c> the monitor rig, and <c>Triggers</c> the external-input side. They are one class
/// because they are one object's worth of state — a session, a bay and a set of windows that have to
/// be opened and closed together — and separate files because a reader arriving with a question about
/// one of them should not have to walk the other four.
/// </para>
/// </remarks>
public sealed partial class ShowHost : ICueExecutionHost, IRemoteApiTransport, IAsyncDisposable
{
    private readonly MediaRegistry _registry;
    private readonly ProjectPatchBay _bay;
    private readonly ProjectVideoOutputs _screens;
    private readonly HashSet<Guid> _calibrationOutputs = [];
    private readonly ShowSession _session;
    private readonly HashSet<string> _attached = [];
    private readonly ActionSender _actions = new();
    private readonly OutboundEffects _outbound;
    private readonly TriggerInputs _triggers;
    private readonly ParameterRegistry _parameters;
    private readonly ProjectRecorders _recorders;
    private readonly ProjectVisualizers _visualizers;

    private readonly List<string> _runtimeProblems = [];

    /// <summary>Which transport group each cue lands on, from the last compile. Guarded by the gate.</summary>
    private readonly Dictionary<Guid, string> _cueGroups = [];

    private readonly Lock _gate = new();

    /// <summary>
    /// Serializes reloads.
    /// </summary>
    /// <remarks>
    /// The app debounces edits by 300 ms, which is NOT a guarantee: a reload that attaches screens or
    /// loads a large document can outlast the interval, and a continuous edit stream — dragging a
    /// matrix cell, dragging a placement — keeps re-arming the timer behind it. Two overlapping
    /// reloads would interleave <c>_project</c>, the attached-output set and the trigger reload.
    /// </remarks>
    private readonly SemaphoreSlim _reloading = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private HaCueProject _project;

    /// <summary>
    /// What the probe said, from the last reload.
    /// </summary>
    /// <remarks>
    /// Kept rather than only compiled with, because "does this clip straddle the playhead" is a
    /// question about the FILE's length, and the document does not carry one.
    /// </remarks>
    private IReadOnlyDictionary<Guid, TimeSpan>? _durations;
    private ShowCompileContext _compileContext = new();
    private bool _standbyRestored;

    private ShowHost(
        MediaRegistry registry,
        ProjectPatchBay bay,
        ProjectVideoOutputs screens,
        ShowSession session,
        HaCueProject project)
    {
        _registry = registry;
        _bay = bay;
        _screens = screens;
        _session = session;
        _project = project;
        _outbound = new OutboundEffects(_actions, () => _project, Report, _life.Token);
        _triggers = new TriggerInputs(project);
        _recorders = new ProjectRecorders(
            project, bay, screens.Recorders, StoragePaths.RecordingRoot);
        _visualizers = new ProjectVisualizers(session);

        // The registry holds delegates, so a fader always compares itself against what is true NOW.
        // A cached value would latch against something the show had already moved past.
        _parameters = ShowParameters.Build(
            () => _masterTrimDb,
            db =>
            {
                _masterTrimDb = db;
                // The session takes a linear scale; the operator's parameter is decibels, and the
                // conversion belongs here rather than in the registry, which is unit-agnostic.
                _ = _session.SetMasterTrimAsync(
                    db <= GainRange.SilenceFloorDb ? 0f : (float)Math.Pow(10, db / 20));
            },
            () => _project,
            db =>
            {
                _project.Audition.LevelDb = db;
                DocumentChangedByCue?.Invoke(new RuntimeDocumentChange(AuditionLevelDb: db));
            });
    }

    /// <summary>The values a control surface may ride (register item 24).</summary>
    public ParameterRegistry Parameters => _parameters;

    /// <summary>
    /// The machine's own panic fade, used when the project does not override it.
    /// </summary>
    /// <remarks>
    /// Set by the shell from app settings. The engine cannot read them itself — machine preferences
    /// are the app's, and a session that reached for them would make the two scopes one.
    /// </remarks>
    public int MachinePanicFadeMs { get; set; } = ProjectSettings.DefaultPanicFadeMs;

    /// <summary>
    /// The external-input sources, and the one master gate over them (register item 3).
    /// </summary>
    /// <remarks>
    /// Exposed rather than hidden because the toggle lives in the transport bar and the wire monitor
    /// lives in Targets — both are the app's, and neither belongs behind a transport verb.
    /// </remarks>
    public TriggerInputs Triggers => _triggers;

    /// <summary>
    /// Lines that would not open, and anything else the operator should be told.
    /// </summary>
    /// <remarks>
    /// Two sources, one list: what the bay could not open when the show started, and what a cue could
    /// not do since. A patch cue whose snapshot lost a channel and an action cue that could not reach
    /// its desk are both things nobody finds out about from a cue list that looks like it fired.
    /// </remarks>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_gate)
                return [.. _bay.Failures, .. _runtimeProblems];
        }
    }

    /// <summary>
    /// Raised when a cue changed the DOCUMENT — a patch cue's cells, or a fade cue's target levels.
    /// </summary>
    /// <remarks>
    /// These writes are deliberately not journaled: firing a cue during a show is not an edit, and an
    /// undo stack full of "the show changed the patch" would bury every real change the operator made.
    /// But they do travel in the file, so the shell has to learn that the project now differs from it —
    /// a document that changed and still reports itself clean is how a night's patch work is lost.
    /// </remarks>
    public event Action<RuntimeDocumentChange>? DocumentChangedByCue;

    /// <summary>A detached document for remote/status readers that may run off the UI thread.</summary>
    public HaCueProject SnapshotProject() => ProjectSnapshot.Copy(_project);

    /// <summary>Forgets the runtime half of <see cref="Problems"/> — the Diagnostics reset.</summary>
    public void ClearProblems()
    {
        lock (_gate)
            _runtimeProblems.Clear();
    }

    /// <summary>
    /// The bay's own counters: terminals, leases, clock and per-logical-output levels.
    /// </summary>
    /// <remarks>
    /// A snapshot, taken on demand. The Diagnostics window and the Output info drawer both read it,
    /// and both get the same numbers because there is one source rather than two collectors that can
    /// disagree about what "dropped" means.
    /// </remarks>
    public AudioPatchBayDiagnostics Diagnostics() => _bay.Bay.SnapshotDiagnostics();

    /// <summary>Clears sticky program clip indicators without changing audio or transport.</summary>
    public void ResetMeterClips() => _bay.Bay.ProgramMeter?.ResetClip();

    /// <summary>The show's recorders and streams — what is armed, where it is writing, and how it fares.</summary>
    public ProjectRecorders Recorders => _recorders;

    /// <summary>
    /// The visualizer cues that are rendering.
    /// </summary>
    /// <remarks>
    /// Exposed because "is projectM even on this machine" is a question the Project status pass asks
    /// before any cue has fired, and answering it from a running host beats a second probe that could
    /// disagree with the one the engine uses.
    /// </remarks>
    public ProjectVisualizers Visualizers => _visualizers;

    /// <summary>
    /// Video outputs that are not showing anything on this machine.
    /// </summary>
    /// <remarks>
    /// Live, because outputs are opened and closed by every reload rather than only at start-up. It
    /// used to be described as fixed, and the shell copied it once — so an output added mid-show read
    /// "live" whether or not a window had opened for it, and one that failed to open never turned red.
    /// </remarks>
    public IReadOnlySet<Guid> AbsentVideoOutputs => _screens.Unopened;

    /// <summary>
    /// Flashes an output's own name on it, so an operator can tell which screen is which.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-OUTPUT test pattern rather than a composition-wide one. The composition-wide layer appears
    /// on every output bound to that canvas, which lights up the lobby TV and the stream while you are
    /// trying to work out which projector is “Projector A” — the exact opposite of identifying one.
    /// </para>
    /// <para>
    /// It sits upstream of the output's mapping stage, so a warped output shows the pattern warped —
    /// which is what makes it useful for checking the warp as well as the wiring.
    /// </para>
    /// </remarks>
    /// <returns>Null when it was shown; the reason otherwise.</returns>
    public async Task<string?> IdentifyAsync(Guid outputId, TimeSpan duration)
    {
        if (_project.VideoOutputs.FirstOrDefault(item => item.Id == outputId) is not { } output)
            return "that output is no longer in this show";

        if (output.CompositionId is not { } compositionId
            || _project.Compositions.FirstOrDefault(item => item.Id == compositionId) is not { } composition)
            return $"“{output.Name}” shows no composition, so there is nothing to flash it on";

        if (_screens.Unopened.Contains(outputId))
            return $"“{output.Name}” is not open on this machine";

        var id = compositionId.ToString();
        var lease = outputId.ToString("N");

        if (!await _session.SetOutputTestPatternAsync(
                id, lease, IdentifyPattern.Render(output.Name, composition.Width, composition.Height))
            .ConfigureAwait(false))
            return $"“{output.Name}” could not show a pattern";

        try
        {
            await Task.Delay(duration, _life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The show is going down. Fall through and clear it anyway — a projector left showing a
            // blue card is worse than one showing nothing.
        }

        bool persistent;
        lock (_gate)
            persistent = _calibrationOutputs.Contains(outputId);
        await _session.SetOutputTestPatternAsync(
            id, lease, persistent
                ? IdentifyPattern.Render(output.Name, composition.Width, composition.Height, output.Mapping)
                : null).ConfigureAwait(false);
        return null;
    }

    /// <summary>Shows or clears a persistent, mapped calibration grid on one physical output.</summary>
    public async Task<string?> SetCalibrationAsync(Guid outputId, bool enabled)
    {
        if (!enabled)
        {
            lock (_gate)
                _calibrationOutputs.Remove(outputId);
            if (_project.VideoOutputs.FirstOrDefault(item => item.Id == outputId) is not { } existing
                || existing.CompositionId is not { } existingComposition)
                return null;
            await _session.SetOutputTestPatternAsync(
                existingComposition.ToString(), outputId.ToString("N"), null).ConfigureAwait(false);
            return null;
        }

        if (_project.VideoOutputs.FirstOrDefault(item => item.Id == outputId) is not { } output)
            return "that output is no longer in this show";
        if (output.CompositionId is not { } compositionId
            || _project.Compositions.FirstOrDefault(item => item.Id == compositionId) is not { } composition)
            return $"“{output.Name}” shows no composition, so there is nothing to calibrate";
        if (_screens.Unopened.Contains(outputId))
            return $"“{output.Name}” is not open on this machine";

        if (!await _session.SetOutputTestPatternAsync(
                compositionId.ToString(), outputId.ToString("N"),
                IdentifyPattern.Render(output.Name, composition.Width, composition.Height, output.Mapping))
            .ConfigureAwait(false))
            return $"“{output.Name}” could not show a calibration pattern";

        lock (_gate)
            _calibrationOutputs.Add(outputId);
        return null;
    }

    /// <summary>What each action endpoint was last successfully sent, and when.</summary>
    public IReadOnlyDictionary<Guid, string> LastSent => _actions.LastSent;

    /// <summary>
    /// Per-composition render telemetry — frames, layers, lateness.
    /// </summary>
    /// <remarks>
    /// Lock-free and safe to call on the UI sweep. The ACHIEVED frame rate is deliberately not in here:
    /// it is a delta of <c>FramesComposited</c> over wall time, and only the caller knows how long it
    /// has been since it last looked.
    /// </remarks>
    public IReadOnlyList<ClipCompositionRuntimeStats> CompositionStats() =>
        _session.GetAllCompositionStats();

    /// <summary>The whole bay as plain text — what "Copy report" puts on the clipboard.</summary>
    public string Report() =>
        AudioPatchBayReport.Render(Diagnostics(), $"HaCue2 · {_project.Title}")
        + (Problems.Count == 0 ? "" : "\nProblems\n  " + string.Join("\n  ", Problems) + "\n");

    private void Report(string problem)
    {
        lock (_gate)
        {
            // Newest first and bounded: an endpoint that is down fails on every fire, and an unbounded
            // list would grow all night and bury the first, most diagnostic occurrence.
            _runtimeProblems.Remove(problem);
            _runtimeProblems.Insert(0, problem);

            if (_runtimeProblems.Count > 32)
                _runtimeProblems.RemoveRange(32, _runtimeProblems.Count - 32);
        }
    }

    /// <summary>
    /// Starts a session for a project.
    /// </summary>
    /// <remarks>
    /// The backend is injected for the same reason the device enumerator's is: which one to open is a
    /// composition-root decision, and passing null gives a session that can still be driven — useful
    /// for a test, and honest on a machine with no audio at all.
    /// </remarks>
    /// <param name="headless">
    /// No display: video outputs are reported as unopened rather than attempted. What a CI box, a
    /// preview and a booth machine with the projector unplugged all are.
    /// </param>
    public static async Task<ShowHost> StartAsync(
        HaCueProject project,
        IAudioBackend? backend,
        IReadOnlyDictionary<Guid, TimeSpan>? durations = null,
        bool headless = false) =>
        await StartAsync(
            project,
            backend,
            new ShowCompileContext { Durations = durations },
            headless).ConfigureAwait(false);

    /// <summary>Starts a session with resolved paths and probed stream identities for this machine.</summary>
    public static async Task<ShowHost> StartAsync(
        HaCueProject project,
        IAudioBackend? backend,
        ShowCompileContext context,
        bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        var runtimeProject = ProjectSnapshot.Copy(project);
        var registry = BuildRegistry(backend);
        var bay = ProjectPatchBay.Open(runtimeProject, backend);
        var screens = ProjectVideoOutputs.OpenAll(runtimeProject, headless);

        var target = new PatchBayShowProgramAudioTarget(
            bay.Bay,
            bay.LogicalChannelIds,
            // WITHOUT THIS a 44.1 kHz file in a 48 kHz show throws on the first GO. Media arrives at
            // whatever rate it was made at and the bus mixes at one rate by definition, so the two
            // meeting is the normal case and not an edge one — the target refuses rather than guessing,
            // and this is the caller supplying the answer.
            ResamplingAudioOutput.Wrap,
            defaultMonitorTerminalId: bay.MonitorTerminalId);

        var session = new ShowSession(
            registry,
            backend,
            ShowSessionWiring.CreateSubtitleOverlay,
            programAudioTarget: target,
            compositorFactory: ShowSessionWiring.CreateCompositor);
        var host = new ShowHost(registry, bay, screens, session, runtimeProject);
        host.SetActiveCueList(runtimeProject.CueLists.FirstOrDefault()?.Id);

        // External input drives the show through the SAME verbs the buttons do — a triggered GO is a
        // GO, not a second code path that can drift from the one an operator tested with.
        host._triggers.Triggered += action => _ = host.ApplyAsync(action);
        host._triggers.Problem += host.Report;

        foreach (var failure in screens.Failures)
            host.Report(failure);

        // A rig that records every performance says so in the document; everything else waits for the
        // operator. Arming here rather than in ReloadAsync means an edit mid-show never re-arms a
        // recording the operator deliberately stopped.
        host._recorders.ArmStartupTargets();

        foreach (var row in host._recorders.Status().Where(row => row.Problem is not null))
            host.Report($"“{row.Name}”: {row.Problem}");

        // Sounding is tracked HERE rather than queried: the session's sounding bus is keyed by label,
        // and the events that matter carry the cue id. A fire adds, a natural end removes, and a stop
        // clears — which is the whole life of a cue as far as the cue list is concerned.
        session.ClipNaturallyEnded += id => host.OnClipNaturallyEnded(id);
        session.ClipApproachingEnd += id => host.OnClipApproachingEnd(id);
        session.VoiceEnded += id => host.Forget(id);

        // A preview that runs to its end releases itself, so the host has to stop claiming it is
        // auditioning — otherwise the button stays lit over a rig that is already silent.
        session.PreviewEnded += id =>
        {
            if (!Guid.TryParse(id, out var previewed))
                return;

            lock (host._gate)
            {
                if (host._previewing == previewed)
                    host._previewing = Guid.Empty;
            }
        };

        await host.ReloadAsync(runtimeProject, context, alreadyDetached: true).ConfigureAwait(false);

        // Before the operator has pressed anything: the FIRST go is the one most likely to be the
        // performance's first, and the one that would otherwise pay for an open.
        host.WarmAllStandby();
        return host;
    }

    /// <summary>
    /// The registry a show opens its media through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was FFmpeg alone, which meant a cue could only ever play a FILE: the <c>ndi:</c>,
    /// <c>padev:</c>, <c>youtube:</c> and <c>text:</c> providers all exist and were simply never
    /// registered, so those URIs fell through to FFmpeg and failed to open.
    /// </para>
    /// <para>
    /// Each optional module is registered inside its own try. NDI in particular is frequently absent —
    /// no runtime, or a CPU it does not support — and its module throws at REGISTRATION rather than at
    /// first open, deliberately. A booth without NDI must still be able to run the show; the absence is
    /// reported once and every other source keeps working.
    /// </para>
    /// </remarks>
    private static MediaRegistry BuildRegistry(IAudioBackend? backend) =>
        MediaRegistry.Build(builder =>
        {
            builder.Use(new FFmpegModule());

            // A capture device is opened through the SAME backend the bay plays out of: two backends
            // in one process see different device tables, and a cue would capture from a device the
            // operator never picked.
            if (backend is S.Media.Audio.PortAudio.PortAudioBackend)
                Optional(builder, () => new S.Media.Audio.PortAudio.PortAudioModule(), "PortAudio");

            Optional(builder, () => new S.Media.NDI.NDIModule(), "NDI");
            // Over the SHARED preparer: the add dialog downloads into that cache, and a second one
            // here would open onto an empty one and report every prepared cue as unprepared.
            Optional(builder, YouTubeRuntime.Module, "YouTube");
            Optional(builder, () => new S.Media.Source.Text.TextSourceModule(), "Text");
        });

    /// <summary>Registers a module, or notes that this machine cannot provide it.</summary>
    private static void Optional(IMediaRegistryBuilder builder, Func<IMediaModule> create, string name)
    {
        try
        {
            builder.Use(create());
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Logged rather than reported to the operator: an absent NDI runtime is a property of the
            // BOX, not a fault in the show, and a modal about it at every start-up is a modal nobody
            // reads. A cue that actually needs it fails by name when it fires.
            MediaDiagnostics.LogInformation(
                "HaCue2: the {Module} source is not available on this machine — {Reason}",
                name,
                failure.Message);
        }
    }

    private void OnClipNaturallyEnded(string cueId)
    {
        if (!Guid.TryParse(cueId, out var id))
            return;

        _ = ObserveLifecycleAsync(Executor.OnNaturalEndAsync(id), "natural-end follow");
    }

    private void OnClipApproachingEnd(string cueId)
    {
        if (!Guid.TryParse(cueId, out var id))
            return;

        _ = ObserveLifecycleAsync(Executor.OnApproachingEndAsync(id), "playlist crossfade");
    }

    private async Task ObserveLifecycleAsync(Task operation, string action)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Report($"{action} failed — {failure.Message}");
        }
    }

    /// <summary>
    /// Recompiles the project and hands it to the session, without stopping what is unaffected.
    /// </summary>
    /// <remarks>
    /// Called after every edit. The preservation flags are what make that safe: a group survives only
    /// when every voice it holds still maps to an identical clip binding, so a reload can never leave
    /// something playing content the document no longer describes.
    /// <para>
    /// The PATCH is pushed separately, because it is not in the document: the second matrix belongs to
    /// the rig. Pushing it here is what makes a patch edit audible without reopening anything —
    /// <see cref="ProjectPatchBay.Apply"/> reconciles cells under running voices.
    /// </para>
    /// </remarks>
    public async Task ReloadAsync(
        HaCueProject project, IReadOnlyDictionary<Guid, TimeSpan>? durations = null)
        => await ReloadAsync(
            project,
            new ShowCompileContext
            {
                ProjectPath = _compileContext.ProjectPath,
                Durations = durations,
                Tracks = _compileContext.Tracks,
                PreparedSubtitlePaths = _compileContext.PreparedSubtitlePaths,
            }).ConfigureAwait(false);

    /// <summary>Recompiles using the current machine's resolved media context.</summary>
    public async Task ReloadAsync(HaCueProject project, ShowCompileContext context)
        => await ReloadAsync(project, context, alreadyDetached: false).ConfigureAwait(false);

    /// <summary>
    /// Recompiles, but only adopts the result when doing so cannot interrupt anything playing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the shell calls after an edit. A reload restarts any group whose voices the edit changed —
    /// re-opening the media, re-seeking it, and taking the audio with it, which on a show layering two
    /// 1080p60 ProRes clips over eleven stems is heard as a pop and a stutter and seen as the picture
    /// jumping back to its in-point. Doing that 300 ms after every drag, keystroke and matrix click is
    /// what made editing during playback feel broken.
    /// </para>
    /// <para>
    /// The overwhelming majority of edits cannot disturb anything: a label, a note, an idle cue, an
    /// output's crop, a patch cell. Those are adopted immediately, exactly as before. Only an edit that
    /// would actually restart a playing voice is refused here, and the shell holds it until the show is
    /// idle — geometry and level edits reach the running picture and the running mix through the live
    /// paths meanwhile, so the operator sees their edit either way.
    /// </para>
    /// </remarks>
    /// <returns>False when the edit was NOT adopted and must be offered again later.</returns>
    public Task<bool> TryReloadAsync(HaCueProject project, ShowCompileContext context)
        => ReloadAsync(project, context, alreadyDetached: false, onlyIfUndisruptive: true);

    private async Task ReloadAsync(
        HaCueProject project, ShowCompileContext context, bool alreadyDetached)
        => await ReloadAsync(project, context, alreadyDetached, onlyIfUndisruptive: false)
            .ConfigureAwait(false);

    private async Task<bool> ReloadAsync(
        HaCueProject project,
        ShowCompileContext context,
        bool alreadyDetached,
        bool onlyIfUndisruptive)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        await _reloading.WaitAsync().ConfigureAwait(false);

        try
        {
            var previous = _project;
            var next = alreadyDetached ? project : ProjectSnapshot.Copy(project);
            var nextContext = context.Durations is null
                ? context with { Durations = _durations }
                : context;
            var document = ShowCompiler.Compile(next, nextContext);

            // Asked BEFORE anything is torn down, and asked of the session rather than guessed at here:
            // "would loading this keep every playing voice" is the load's own retention rule, and a
            // second implementation of it in the host would drift from the one that actually decides.
            if (onlyIfUndisruptive
                && !await _session.WouldPreservePlaybackAsync(document).ConfigureAwait(false))
                return false;

            var cueGroups = document.Cues
                .Where(cue => Guid.TryParse(cue.Id, out _) && cue.GroupId is { Length: > 0 })
                .ToDictionary(cue => Guid.Parse(cue.Id), cue => cue.GroupId!);

            // Compile and load before swapping the host's document. A malformed edit must leave the
            // running project, cue-to-transport map and screens describing the same document rather
            // than half of the old one and half of the failed new one.
            await _session.LoadDocumentAsync(
                document,
                preserveMatchingCompositions: true,
                preserveActiveGroups: true).ConfigureAwait(false);

            _project = next;
            _compileContext = nextContext;
            _durations = nextContext.Durations;
            ForgetDetachedScreens(previous, next);

            // The compiled document is the ONE place that knows which transport a cue lands on — the
            // rule is not simple (a timeline's children get one each, everything else shares its
            // outermost group's), and re-deriving it here would be a second implementation of it. The
            // Active panel's playhead and the seek verb both address a group.
            lock (_gate)
            {
                _cueGroups.Clear();
                foreach (var (id, group) in cueGroups)
                    _cueGroups[id] = group;
            }

            if (!_standbyRestored)
            {
                foreach (var list in next.CueLists.Where(list => list.StandbyCueId is not null))
                    await _session.SetStandbyCueAsync(
                        list.StandbyCueId!.Value.ToString(), ShowCompiler.GroupId(list)).ConfigureAwait(false);
                _standbyRestored = true;
            }

            foreach (var failure in _bay.Apply(next))
                Report(failure);

            // Outputs the operator has just ADDED open here, and ones they removed close. Opening only
            // at start-up meant a screen added mid-session stayed dark with nothing saying why.
            foreach (var failure in _screens.Sync(next))
                Report(failure);

            await AttachScreensAsync().ConfigureAwait(false);
            await ApplyIdleFramesAsync(next, context.ProjectPath).ConfigureAwait(false);
            await RestoreCalibrationPatternsAsync().ConfigureAwait(false);
            await RetireDeletedVisualizersAsync(next).ConfigureAwait(false);
            await _triggers.ReloadAsync(next).ConfigureAwait(false);
            _recorders.Adopt(next);
            _actions.Adopt(next);
            return true;
        }
        finally
        {
            _reloading.Release();
        }
    }

    private async Task RestoreCalibrationPatternsAsync()
    {
        Guid[] active;
        lock (_gate)
            active = [.. _calibrationOutputs];
        foreach (var outputId in active)
        {
            if (_project.VideoOutputs.All(output => output.Id != outputId))
            {
                lock (_gate)
                    _calibrationOutputs.Remove(outputId);
                continue;
            }
            await SetCalibrationAsync(outputId, true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attaches each open window to the composition it shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after every load, because <c>preserveMatchingCompositions</c> preserves the ones whose
    /// definition is unchanged and rebuilds the rest — an output attached to a rebuilt composition is
    /// no longer attached to anything, and its window would sit black for the rest of the show with
    /// nothing to say why.
    /// </para>
    /// <para>
    /// Idempotent: an output already attached to a surviving composition is not re-added; only its
    /// inexpensive mapping spec is refreshed, so geometry edits take effect without replacing the
    /// window or its compositor output.
    /// </para>
    /// </remarks>
    private async Task AttachScreensAsync()
    {
        // An output whose canvas CHANGED comes off the old one first. Without this, assigning a
        // composition to an output that was already on screen did nothing at all: the host still
        // believed it was attached where it used to be, and skipped it forever.
        foreach (var moved in _screens.Retargeted)
        {
            if (moved.From is { } previous)
                await _session.RemoveCompositionOutputAsync(previous.ToString(), moved.OutputId)
                    .ConfigureAwait(false);

            _attached.Remove(moved.OutputId);
        }

        foreach (var (compositionId, lease) in _screens.Leases(_project))
        {
            if (_attached.Contains(lease.OutputId))
            {
                // The output object survives document reloads, but its mapping is authored state and
                // may have changed. Skipping the whole lease here left an already-open projector on its
                // start-up crop forever even while the editor showed the new source slice.
                await _session.ApplyOutputMappingAsync(compositionId, lease.OutputId, lease.Mapping)
                    .ConfigureAwait(false);
                continue;
            }

            if (await _session.AddCompositionOutputAsync(compositionId, lease).ConfigureAwait(false))
            {
                _attached.Add(lease.OutputId);
                continue;
            }

            Report($"“{lease.DisplayName}” could not be attached to its composition");
        }

        PaintUnattachedScreens();
    }

    /// <summary>Moves an active cue's already-attached composition layer without re-firing it.</summary>
    public Task<bool> UpdateActivePlacementAsync(Guid cueId, LayerPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return UpdateActivePlacementAsync(
            cueId,
            placement.CompositionId,
            placement.LayerIndex,
            ShowCompiler.VideoPlacement(placement));
    }

    /// <summary>
    /// Publishes an immutable placement snapshot. Pointer editors use this overload so an update queued
    /// behind the session dispatcher cannot observe a later mutation of the project object.
    /// </summary>
    public Task<bool> UpdateActivePlacementAsync(
        Guid cueId,
        Guid compositionId,
        int layerIndex,
        ShowVideoPlacement placement) =>
        _session.UpdateActivePlacementAsync(
            cueId.ToString(), compositionId.ToString(), layerIndex, placement);

    /// <summary>
    /// Re-applies a sounding cue's logical sends, so a send or level edit is heard immediately.
    /// </summary>
    /// <remarks>
    /// The audio counterpart of the live placement push. A send edit DOES change the cue's clip
    /// binding, so a reload would restart the cue to apply it — on an eleven-stem group that is a pop
    /// and eleven re-opened files, for a fader move. The session reconciles the matrix on the running
    /// voice instead, as a click-free ramp, and the document reload happens later when it is free.
    /// Returns false when the cue is not the active voice on any group, which is the ordinary case for
    /// an idle cue and not a failure.
    /// </remarks>
    public Task<bool> ApplyActiveSendsAsync(Guid cueId, IReadOnlyList<ShowClipLogicalSend> sends)
    {
        ArgumentNullException.ThrowIfNull(sends);
        return _session.ApplyActiveLogicalSendsAsync(cueId.ToString(), sends);
    }

    /// <summary>
    /// Pushes the project's patch cells onto the live bay without reloading the document.
    /// </summary>
    /// <remarks>
    /// The audio counterpart of <see cref="UpdateActivePlacementAsync"/> and
    /// <see cref="ApplyOutputMappingAsync"/>. The patch is not part of the compiled document — the
    /// second matrix belongs to the rig — so it can be reconciled under running voices at any time, and
    /// a gain drag has no reason to wait for the debounced reload behind it. It used to: every sample
    /// restarted that debounce, so the operator heard nothing at all until they let go.
    /// </remarks>
    public void ApplyPatch(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        foreach (var failure in _bay.Apply(project))
            Report(failure);
    }

    /// <summary>Applies an output's current crop/warp to its open composition immediately.</summary>
    public Task<bool> ApplyOutputMappingAsync(
        VideoOutputDefinition output,
        CompositionDefinition composition)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(composition);
        return ApplyOutputMappingAsync(
            composition.Id,
            output.Id,
            OutputMapping.Spec(output, composition.Width, composition.Height));
    }

    /// <summary>
    /// Applies an immutable mapping snapshot. Pointer publishers use this overload so a delayed update
    /// cannot observe a project object that has already moved on to a later pointer sample.
    /// </summary>
    public Task<bool> ApplyOutputMappingAsync(
        Guid compositionId,
        Guid outputId,
        ClipOutputMappingSpec? mapping) =>
        _session.ApplyOutputMappingAsync(
            compositionId.ToString(), outputId.ToString("N"), mapping);

    /// <summary>
    /// Paints black on every window that is open but shows no canvas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A local screen opens as soon as it exists, before any composition is assigned to it — an
    /// operator who has just added a projector needs to see WHERE it landed. Nothing in the session
    /// will paint it, because a composition is what submits frames and this output is on none, so the
    /// host does it: one black frame, which the window then holds.
    /// </para>
    /// <para>
    /// Also on the way BACK. Taking a composition off an output leaves whatever it last composited
    /// frozen on the glass, which in front of an audience is the last frame of the previous cue.
    /// </para>
    /// </remarks>
    private void PaintUnattachedScreens()
    {
        foreach (var open in _screens.Unattached)
        {
            if (!_darkened.Add(open.OutputId))
                continue;

            try
            {
                var frame = IdleFrames.Black(1280, 720);
                open.Output.Configure(frame.Format);
                open.Output.Submit(frame);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                Report($"a video output showing no composition could not be blacked out — {failure.Message}");
            }
        }

        // Anything that has since gained a canvas has to be forgotten, or taking that canvas away
        // again would find it "already black" and leave the last composited frame on the glass.
        var dark = _screens.Unattached.Select(open => open.OutputId).ToHashSet(StringComparer.Ordinal);
        _darkened.RemoveWhere(id => !dark.Contains(id));
    }

    /// <summary>Windows that have already been painted black, so it is done once rather than per reload.</summary>
    private readonly HashSet<string> _darkened = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes down visualizers whose cue the operator has deleted.
    /// </summary>
    /// <remarks>
    /// A visualizer holds a renderer rather than a voice, so none of the session's preservation rules
    /// reach it: deleting the cue would otherwise leave projectM rendering onto a canvas with nothing
    /// in the show pointing at it, and no way to stop it short of restarting.
    /// <para>
    /// A visualizer whose cue still exists is left alone, even when its placements or preset pack have
    /// been edited. Restarting a running visualizer on every keystroke would be visible on the canvas;
    /// the edit takes effect on the next fire, which is the same rule the preset pack follows.
    /// </para>
    /// </remarks>
    private async Task RetireDeletedVisualizersAsync(HaCueProject project)
    {
        foreach (var cueId in _visualizers.Running)
        {
            if (project.FindCue(cueId) is not VisualizerCueNode)
            {
                await _visualizers.StopAsync(cueId).ConfigureAwait(false);
                Forget(cueId.ToString());
            }
        }
    }

    /// <summary>
    /// Forgets attachments whose composition the session no longer has.
    /// </summary>
    /// <remarks>
    /// A reload rebuilds any composition whose definition changed, which silently detaches its
    /// outputs. Without this the host would believe they were still attached and never re-add them —
    /// so resizing a composition would blank its projector permanently.
    /// </remarks>
    private void ForgetDetachedScreens(HaCueProject previous, HaCueProject current)
    {
        foreach (var open in _screens.Open)
        {
            // An output showing nothing has no attachment to lose; PaintUnattachedScreens owns it.
            if (open.CompositionId is not { } canvas)
                continue;

            var before = previous.Compositions.FirstOrDefault(item => item.Id == canvas);
            var after = current.Compositions.FirstOrDefault(item => item.Id == canvas);

            // Size and rate are what the session keys "unchanged" on; a renamed composition is
            // preserved and keeps its outputs.
            if (before is null || after is null
                || before.Width != after.Width
                || before.Height != after.Height
                || Math.Abs(before.FramesPerSecond - after.FramesPerSecond) > 0.001)
                _attached.Remove(open.OutputId);
        }
    }


    public async ValueTask DisposeAsync()
    {
        // Cancelled FIRST: scheduled timeline cues and in-flight pre-waits have to stop reaching for a
        // session that is about to go away, or disposal races a fire.
        await _life.CancelAsync().ConfigureAwait(false);

        // Before the session goes: the window is attached to it, and detaching afterwards would be
        // asking a disposed session to release something.
        if (_auditionWindow is not null)
        {
            try
            {
                await TearDownAuditionSurfaceAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (_auditionWindow as IDisposable)?.Dispose();
                _auditionWindow = null;
            }
        }

        // Before the session: a trigger arriving mid-teardown would reach for a disposed transport.
        await _triggers.DisposeAsync().ConfigureAwait(false);

        // Before the session they are attached to: taking a visualizer down is a call ON the session,
        // and a renderer detached after it had gone would leak a GL thread.
        await _visualizers.DisposeAsync().ConfigureAwait(false);

        // Before the bay and the session that feed them: disarming flushes each encoder and writes the
        // file's trailer, and a recording finalized after its audio terminal had gone would be short its
        // last seconds. This is the one part of teardown worth waiting for.
        await _recorders.DisposeAsync().ConfigureAwait(false);
        await _outbound.DisposeAsync().ConfigureAwait(false);

        await _session.DisposeAsync().ConfigureAwait(false);
        _actions.Dispose();
        _bay.Dispose();
        // After the session, because the leases declare DisposeOutputOnRuntimeDispose:false — the host
        // owns these windows and closes them itself, once the session has stopped submitting to them.
        _screens.Dispose();
        _registry.Dispose();
        _life.Dispose();
        _reloading.Dispose();
    }
}
