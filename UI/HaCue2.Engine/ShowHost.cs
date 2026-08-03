using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>What the show is doing right now, as one snapshot.</summary>
/// <param name="Sounding">Cue ids currently playing.</param>
/// <param name="Standby">Per cue-list, the cue GO would fire next.</param>
public sealed record ShowState(
    IReadOnlySet<Guid> Sounding,
    IReadOnlyDictionary<Guid, Guid> Standby,
    IReadOnlyList<string> Problems)
{
    public static ShowState Idle { get; } = new(
        new HashSet<Guid>(), new Dictionary<Guid, Guid>(), []);
}

/// <summary>
/// The running show: a session, the project patch bay, and the document that joins them.
/// </summary>
/// <remarks>
/// <para>
/// One object the app starts and stops. Everything thread-affine and device-holding lives behind it,
/// and the UI never touches <c>ShowSession</c> directly — it asks for a <see cref="ShowState"/> and
/// calls transport verbs. That is the same seam <c>ShowRuntime</c> already had; this fills it with
/// facts instead of inventions.
/// </para>
/// <para>
/// <b>Reloads preserve.</b> Every edit recompiles and reloads the whole document, so
/// <c>preserveActiveGroups</c> and <c>preserveMatchingCompositions</c> are ON: a group whose voices
/// are all still described unchanged keeps playing, and GO cursors survive regardless. Without them
/// every keystroke in the inspector would stop the show — which is precisely what
/// "editing never blocks playback" promises it will not do.
/// </para>
/// </remarks>
public sealed class ShowHost : IAsyncDisposable
{
    private readonly MediaRegistry _registry;
    private readonly ProjectPatchBay _bay;
    private readonly ShowSession _session;
    private readonly HashSet<Guid> _sounding = [];
    private readonly Lock _gate = new();
    private HaCueProject _project;

    private ShowHost(
        MediaRegistry registry, ProjectPatchBay bay, ShowSession session, HaCueProject project)
    {
        _registry = registry;
        _bay = bay;
        _session = session;
        _project = project;
    }

    /// <summary>Lines that would not open, and anything else the operator should be told.</summary>
    public IReadOnlyList<string> Problems => _bay.Failures;

    /// <summary>
    /// Starts a session for a project.
    /// </summary>
    /// <remarks>
    /// The backend is injected for the same reason the device enumerator's is: which one to open is a
    /// composition-root decision, and passing null gives a session that can still be driven — useful
    /// for a test, and honest on a machine with no audio at all.
    /// </remarks>
    public static async Task<ShowHost> StartAsync(HaCueProject project, IAudioBackend? backend)
    {
        ArgumentNullException.ThrowIfNull(project);

        var registry = MediaRegistry.Build(builder => builder.Use(new FFmpegModule()));
        var bay = ProjectPatchBay.Open(project, backend);

        var target = new PatchBayShowProgramAudioTarget(
            bay.Bay,
            bay.LogicalChannelIds,
            defaultMonitorTerminalId: bay.MonitorTerminalId);

        var session = new ShowSession(registry, backend, programAudioTarget: target);
        var host = new ShowHost(registry, bay, session, project);

        // Sounding is tracked HERE rather than queried: the session's sounding bus is keyed by label,
        // and the events that matter carry the cue id. A fire adds, a natural end removes, and a stop
        // clears — which is the whole life of a cue as far as the cue list is concerned.
        session.ClipNaturallyEnded += id => host.Forget(id);
        session.VoiceEnded += id => host.Forget(id);

        await host.ReloadAsync(project).ConfigureAwait(false);
        return host;
    }

    /// <summary>
    /// Recompiles the project and hands it to the session, without stopping what is unaffected.
    /// </summary>
    /// <remarks>
    /// Called after every edit. The preservation flags are what make that safe: a group survives only
    /// when every voice it holds still maps to an identical clip binding, so a reload can never leave
    /// something playing content the document no longer describes.
    /// </remarks>
    public async Task ReloadAsync(HaCueProject project)
    {
        _project = project;

        await _session.LoadDocumentAsync(
            ShowCompiler.Compile(project),
            preserveMatchingCompositions: true,
            preserveActiveGroups: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Fires the next cue in a list and advances its cursor.
    /// </summary>
    /// <remarks>
    /// The cue that WAS standby is the one that fired, so it is read before the call — afterwards the
    /// cursor has already moved on and would name the next one.
    /// </remarks>
    public async Task<Guid?> GoAsync(CueList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var group = ShowCompiler.GroupId(list);
        var standby = await _session.GetStandbyCueAsync(group).ConfigureAwait(false);
        var status = await _session.GoAsync(group).ConfigureAwait(false);

        if (status != CueExecutionStatus.Fired || standby is null || !Guid.TryParse(standby.Id, out var id))
            return null;

        Remember(id);
        return id;
    }

    /// <summary>Puts a list's cursor on a cue without firing it.</summary>
    public Task<bool> StandbyAsync(CueList list, Guid? cueId)
    {
        ArgumentNullException.ThrowIfNull(list);
        return _session.SetStandbyCueAsync(cueId?.ToString(), ShowCompiler.GroupId(list));
    }

    /// <summary>Fires one cue by id, whatever the cursor is doing.</summary>
    public async Task<bool> FireAsync(Guid cueId)
    {
        var status = await _session.FireCueAsync(cueId.ToString()).ConfigureAwait(false);

        if (status != CueExecutionStatus.Fired)
            return false;

        Remember(cueId);
        return true;
    }

    public Task PauseAsync(bool paused) => _session.SetPausedAsync(paused);

    /// <summary>Stops everything. The PANIC button, and what closing a show does.</summary>
    public async Task StopAsync()
    {
        await _session.StopAsync().ConfigureAwait(false);

        lock (_gate)
            _sounding.Clear();
    }

    private void Remember(Guid cueId)
    {
        lock (_gate)
            _sounding.Add(cueId);
    }

    private void Forget(string cueId)
    {
        if (!Guid.TryParse(cueId, out var id))
            return;

        lock (_gate)
            _sounding.Remove(id);
    }

    /// <summary>
    /// What the show is doing, for the views.
    /// </summary>
    /// <remarks>
    /// Pulled rather than pushed: the session raises events on its own thread and the UI wants a
    /// consistent picture at a moment of its choosing, not a stream of edges to reassemble.
    /// </remarks>
    public async Task<ShowState> SnapshotAsync()
    {
        HashSet<Guid> sounding;

        lock (_gate)
            sounding = [.. _sounding];

        var standby = new Dictionary<Guid, Guid>();

        foreach (var list in _project.CueLists)
        {
            var cue = await _session.GetStandbyCueAsync(ShowCompiler.GroupId(list)).ConfigureAwait(false);

            if (cue is not null && Guid.TryParse(cue.Id, out var id))
                standby[list.Id] = id;
        }

        return new ShowState(sounding, standby, Problems);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync().ConfigureAwait(false);
        _bay.Dispose();
        _registry.Dispose();
    }
}
