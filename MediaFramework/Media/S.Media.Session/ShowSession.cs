using S.Media.Compositor;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.Routing;
using S.Media.Time;
using System.Text.Json;

namespace S.Media.Session;

/// <summary>Immutable per-group transport snapshot - a query result, never mutated by the caller (D5).</summary>
/// <param name="IsActive">True when the group currently holds a clip (playing, paused, or frozen) - the reliable
/// "is this cue still up" signal. Distinct from <paramref name="IsRunning"/> (the clock is advancing), which a
/// video-only held/text clip can report <c>false</c> for while still on screen.</param>
/// <param name="TimelineGeneration">The group's timeline DISCONTINUITY generation (NXT-04): bumped on every
/// seek, loop wrap, pause/resume, and clip replacement. Pollers compare it across ticks to distinguish "the
/// timeline jumped" from "playback progressed" - the authoritative signal that replaces transient-pause
/// heuristics (a value has no meaning on its own; only change does).</param>
public sealed record TransportSnapshot(
    string GroupId,
    TimeSpan SessionTime,
    TimeSpan ClipPosition,
    TimeSpan ClipDuration,
    bool IsRunning,
    bool IsActive = false,
    bool LiveSourceDisconnected = false,
    int AudioChannels = 0,
    int AudioSampleRate = 0,
    int TimelineGeneration = 0)
{
    /// <summary>
    /// The complete NXT-04 timeline view used by render/subtitle/output consumers. The positional properties
    /// above remain for UI/API compatibility; new timing-sensitive code should consume this contract.
    /// </summary>
    public TransportTimelineSnapshot Timeline { get; init; }

    /// <summary>
    /// How far <see cref="ClipPosition"/> leads what is actually AT THE SPEAKER right now, for a voice
    /// whose clock is mastered by its own producer. Zero for genlocked (silent/video-only) voices,
    /// whose position already reads in the on-air domain - their start was deferred by exactly this
    /// depth - and zero when the session has no program-audio target.
    /// </summary>
    /// <remarks>
    /// A producer-mastered clock anchors RELATIVELY at its start edge, so the producer clock's latency
    /// subtraction cancels and the position tracks content entering the mix bus - one downstream depth
    /// (master pump in-flight + device latency) ahead of the glass-and-speaker truth a panel wants to
    /// show. A display that mixes both kinds of voice must subtract this from the producer-mastered
    /// ones, or cues that ARE playing together read ~100-200 ms apart.
    /// </remarks>
    public TimeSpan AudibleLatency { get; init; }
}

/// <summary>A soundboard voice's playhead - for the UI's per-tile progress/countdown.</summary>
public readonly record struct VoiceProgress(string VoiceId, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// The headless home of a show (D5): cues, clips, and transport groups behind an internal dispatcher.
/// Commands marshal onto the session thread and queries return immutable snapshots - the API never assumes
/// a UI thread (the <c>ui_thread_observable_property_sets</c> bug class is structurally impossible here).
/// One <see cref="SessionClock"/> per transport group (D4); clips open through <see cref="IMediaRegistry"/>
/// (D6); when an <see cref="IAudioBackend"/> is supplied, each group plays on a master output (D11).
///
/// <para><b>Where things live.</b> Split into partials 2026-07-30 (review §3), when this file reached
/// ~4 000 lines. THIS file keeps the session's own lifecycle: construction and shared plumbing, the
/// dispatcher, document loading, the clip open/commit path (<c>PlayClipAsync</c>/<c>CommitClipAsync</c> -
/// the thing every other file is downstream of), the soundboard/preview delegation to
/// <see cref="VoicePlayer"/>, and disposal. Everything else is one concern per file:</para>
/// <list type="table">
/// <item><term><c>ShowSession.Transport.cs</c></term><description>fire / GO / seek.</description></item>
/// <item><term><c>ShowSession.Stops.cs</c></term><description>stop, stop-all, Panic, per-cue stop, and the
///   claim/ramp machinery they share (protocol in <see cref="SoundingStopClaim"/>).</description></item>
/// <item><term><c>ShowSession.Completion.cs</c></term><description>clips ending on their OWN - end monitor,
///   completion tick, natural fade-out, release ramp, loop crossfade.</description></item>
/// <item><term><c>ShowSession.Levels.cs</c></term><description>master trim, fade cues, fade-in, the volume
///   envelope, pause - everything composing through <see cref="SoundingLevel"/>.</description></item>
/// <item><term><c>ShowSession.LiveEdits.cs</c></term><description>editing a cue's placement, outputs,
///   matrix, routes and held frame WHILE it plays.</description></item>
/// <item><term><c>ShowSession.Queries.cs</c></term><description>snapshots, metrics, and the lock-free
///   published views (plus the group registry that keeps them fresh).</description></item>
/// <item><term><c>ShowSession.Taps.cs</c></term><description>audio taps, composition visualizers, playback
///   alerts, and the voice/preview <see cref="ClipSpec"/> builders.</description></item>
/// <item><term><c>ShowSession.TransportVoices.cs</c></term><description>the <c>TransportVoice</c> /
///   <c>TransportGroup</c> model itself (§A).</description></item>
/// </list>
/// </summary>
public sealed partial class ShowSession : IAsyncDisposable, ISessionPreviewHost, ICueRunnerHost
{
    /// <summary>The implicit group cues fall into when <see cref="CueDefinition.GroupId"/> is null.</summary>
    public const string DefaultGroup = "main";

    /// <summary>The output id of a transport group's master audio output - target it from an
    /// <see cref="OutputPatchRoute"/> (<c>OutputId</c>) to apply an N→M channel remap when clips play (03 §6).</summary>
    public const string MasterOutputId = "_master";

    /// <summary>The soft-stop fade used when neither the clip's <see cref="ShowClipBinding.FadeOut"/> nor an
    /// explicit <c>fadeDuration</c> supplies one - also the <see cref="ClipEndBehavior.FadeOutAndStop"/>
    /// natural-end fallback. Defaults to the legacy HaPlay 750 ms; hosts set it once at wiring time
    /// (a 64-bit write - no torn read on the fade paths that read it off the dispatcher).</summary>
    public TimeSpan DefaultStopFade { get; set; } = TimeSpan.FromMilliseconds(750);

    private readonly IMediaRegistry _registry;
    private readonly IAudioBackend? _audioBackend;
    // Swapped atomically on load (NXT-12 transactional load); volatile because fires and the lock-free cue-
    // definition query read it OFF the dispatcher (the graph itself is internally locked).
    private readonly Dictionary<string, TransportGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime> _compositions = new(StringComparer.Ordinal);
    private Dictionary<string, ShowComposition> _compositionDefinitions = new(StringComparer.Ordinal);
    // Lock-free view of the compositions for the UI health poll: republished (on the dispatcher) whenever
    // _compositions changes, so GetCompositionStats can read it - and the runtime's own thread-safe GetStats -
    // off any thread without marshaling (mirrors _groupViews / SnapshotAsync).
    private volatile IReadOnlyDictionary<string, ClipCompositionRuntime> _compositionsView =
        new Dictionary<string, ClipCompositionRuntime>(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime.LayerSlot> _testPatternSlots = new(StringComparer.Ordinal);
    private IReadOnlyList<OutputPatchRoute> _routes = [];

    /// <summary>
    /// Each group's GO cursor: the number of the last cue fired there, or <see cref="int.MinValue"/> for
    /// "nothing yet". Dispatcher-confined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held here rather than on <c>TransportGroup</c> because it is a per-LIST position, not a property of a
    /// live playing object. On the group it died whenever the group did - and a reload disposes every group
    /// that is not actively sounding, so a list you had GO'd partway through and then stopped silently
    /// rewound to the top on the next edit. Edits are the app's normal path (debounced ~300 ms after any
    /// structural change), which made that the common case rather than an edge one.
    /// </para>
    /// <para>Cursors persist across a preserving reload and are cleared by a non-preserving one - the same
    /// opt-in that distinguishes "this is an edit of the running show" from "this is a different show".</para>
    /// </remarks>
    private readonly Dictionary<string, int> _goCursors = new(StringComparer.Ordinal);

    /// <summary>This group's GO cursor. <see cref="int.MinValue"/> when nothing has fired there yet.</summary>
    private int GetGoCursor(string groupId) => _goCursors.GetValueOrDefault(groupId, int.MinValue);
    private IReadOnlyList<ShowAudioOutput> _audioOutputs = [];
    private readonly SessionDispatcher _dispatcher;
    private readonly Func<string, int, int, int, IVideoOverlaySource?>? _subtitleFactory;
    // Host video-output factory (compositionId, name, width, height) → the leases a composition renders to
    // (NDI/SDL/local). The HOST owns each returned output's lifetime and declares it on the lease - a borrowed
    // host output is returned with DisposeOutputOnRuntimeDispose=false (+ an optional release callback), so the
    // session NEVER disposes it (NXT-01: disposing a borrowed SDL/NDI output is a use-after-reload defect).
    // Null ⇒ headless discard. Lets the GUI surface composited video to its output lines.
    // RELOAD ORDERING CONTRACT (NXT-20): during a document reload the factory is invoked for the NEW graph
    // while the OLD compositions still hold their leases - staging before teardown is what keeps a failed
    // load from destroying the running show (NXT-12). A host with exclusive/single-holder output lines must
    // therefore hand a still-bound line over (keep the existing acquisition and return the same output, as
    // HaPlay's hold-across-reload does) rather than release-then-reacquire, and must detach a dropped line
    // from the live compositions before releasing it.
    private readonly Func<string, string, int, int, IReadOnlyList<ClipCompositionOutputLease>>? _videoOutputFactory;
    // Host compositor factory (canvas format → compositor). Null ⇒ the default CPU compositor; a host that has a
    // GL context can inject a GPU/warp compositor so session compositions use the intended zero-copy GPU path
    // instead of building full-BGRA CPU canvases (NXT-11). Threaded into every ClipCompositionRuntime at load.
    private readonly Func<VideoFormat, ClipCompositionCompositor>? _compositorFactory;
    // Host audio-output factory (route deviceId, format) → a borrowed sink for that device, or null to let the
    // IAudioBackend create it. Mirrors _videoOutputFactory: a returned lease with DisposeOutputOnRuntimeDispose
    // = false is NEVER disposed by the session (the host owns it - e.g. an NDI sender's audio side sharing the
    // carrier that also emits the composition's video). Null ⇒ every route uses the backend device.
    private readonly Func<string, AudioFormat, ClipAudioOutputLease?>? _audioOutputFactory;
    // The program-audio collaborator (HaCue plan, "ShowSession redesign"): owns real outputs, the
    // project patch and device clocks. Null = the v1 direct-route adapter (AudioRoutes/group
    // outputs on the session's own backend) - HaPlay unchanged.
    private readonly IShowProgramAudioTarget? _programAudio;
    // Opens + warms clips (seek-to-Start trim-in, standby pre-roll). Clips arm through here instead of a
    // direct MediaGraph build so the show can pre-roll upcoming cues (8b convergence). All access is on the
    // serial dispatcher; the engine is also internally thread-safe.
    private readonly ClipStandbyEngine _standby = new();
    // Cue id → its clip binding (built on load) so the standby pre-roll can look up upcoming cues' media.
    private IReadOnlyDictionary<string, ShowClipBinding> _clipsById =
        new Dictionary<string, ShowClipBinding>(StringComparer.Ordinal);
    // Soundboard voices + the cue preview - playback outside the transport groups, split along its ownership
    // seam (review Part-5 #2). Owns the voice/preview registries and monitors; this session's public
    // voice/preview API delegates to it.
    private readonly SoundboardVoicePlayer _voicePlayer;
    private readonly CuePreviewPlayer _previewPlayer;

    // ISessionVoiceHost / ISessionPreviewHost, explicitly: these members are internal to the assembly and
    // stay that way - the interface exists to narrow what the independent-player surfaces can reach, not to
    // widen the session's public API. (InvokeAsync and MasterTrim are already public and bind implicitly.)
    SoundingSourceRegistry ISessionVoiceHost.SoundingSources => _sounding;
    void ISessionVoiceHost.NotifyCompletionWorkAvailable() => NotifyCompletionWorkAvailable();
    bool ISessionVoiceHost.IsDisposed => IsDisposed;
    ClipCompositionRuntime? ISessionPreviewHost.AuditionComposition => _auditionComposition;

    // ICueRunnerHost, explicitly, for the same reason: these stay internal. (WarmUpcomingAsync is public and
    // binds implicitly - it is a host-facing pre-roll hint, not part of the cue seam alone.)
    string ICueRunnerHost.DefaultGroupId => DefaultGroup;

    ValueTask ICueRunnerHost.PlayClipAsync(
        string groupId,
        ShowClipBinding? binding,
        CancellationToken cancellationToken,
        Func<Task>? waitForStartBarrier,
        (TimeSpan Duration, FadeShape Curve)? crossfade) =>
        PlayClipAsync(groupId, binding, cancellationToken, waitForStartBarrier, crossfade: crossfade);

    Task<CueExecutionStatus> ICueRunnerHost.FireCueIndependentAtBarrierAsync(
        string cueId, string independentGroupId, Func<Task>? waitForStartBarrier, Func<Task>? waitForStartEdge,
        CancellationToken cancellationToken, TimeSpan? initialPosition) =>
        FireCueIndependentAtBarrierAsync(
            cueId, independentGroupId, waitForStartBarrier, waitForStartEdge, cancellationToken, initialPosition);

    Task<(int Cursor, int Generation)> ICueRunnerHost.ReadGoCursorAsync(string groupId) =>
        InvokeAsync(() => Task.FromResult((GetGoCursor(groupId), _showGeneration)));

    Task ICueRunnerHost.AdvanceGoCursorAsync(string groupId, int number, int generation) =>
        AdvanceGoCursorAsync(groupId, number, generation);
    private readonly ShowSessionMetadataPublisher _metadataPublisher;
    private readonly SessionCompletionMonitor _completionMonitor;
    // The level/stop bus: every sounding source registers with its program/monitoring classification, and
    // master trim, stop-all and Panic all drive its ONE enumeration. Dispatcher-confined like _groups.
    private readonly SoundingSourceRegistry _sounding = new();
    // Uniquifier for transport-voice bus labels (a cue can have two voices on the bus at once - a loop
    // crossfade overlaps a cue with itself). Dispatcher-confined, like the registry it labels.
    private int _soundingSequence;
    private volatile bool _disposed;

    /// <summary>The session's level/stop bus, for the components that own their own sounding sources
    /// (<see cref="VoicePlayer"/>'s soundboard voices and cue preview). Dispatcher-confined.</summary>
    internal SoundingSourceRegistry SoundingSources => _sounding;

    /// <summary>Current bounded command-queue health for host monitoring and overload diagnostics.</summary>
    public SessionDispatcherDiagnostics DispatcherDiagnostics => _dispatcher.Diagnostics;

    // Lock-free query view (NXT-16): a volatile snapshot of each group's clock + active player, republished on
    // the dispatcher whenever the group set or active clip changes. Snapshot() reads THIS and pulls live
    // (thread-safe) position/duration/run-state off the captured references, so a position/state poll never
    // serializes behind a long-running command on the dispatcher.
    private volatile IReadOnlyList<GroupClockView> _groupViews = [];

    private sealed record GroupClockView(string GroupId, S.Media.Players.MediaPlayer? Player, TransportGroup Group);

    // Lock-free per-device audio-pump view for the outputs-panel line-health poll (the audio analogue of
    // _compositionsView): republished on the dispatcher whenever the active clips change, so a UI poll sums a
    // routed line's enqueued/dropped chunks off-thread without marshaling. Keyed to the device the cue routed to.
    private volatile IReadOnlyList<ActiveAudioPump> _audioPumpsView = [];

    private readonly record struct ActiveAudioPump(AudioRouter Router, string OutputId, string DeviceId);

    // A clip's attached audio output plus its ownership. The session disposes it on clip replace only when
    // DisposeOnRelease (a backend-created device it owns); a host lease (e.g. an NDI carrier's audio) is
    // BORROWED - never disposed, only its Release hook is invoked so the host can drop its reference.
    private readonly record struct ClipAudioOutput(IAudioOutput Output, bool DisposeOnRelease, Action? Release);
    private sealed record AudioRouteTarget(string OutputId, float TargetGain, ShowClipAudioRoute? Route = null);

    /// <summary>The route-less fallback output device (backend default, else first), resolved through the
    /// 5 s device cache at every point of use - never frozen at construction, so a device plugged in after
    /// app start becomes the fallback on the next cache refresh. Null without a backend or devices.</summary>
    private string? ResolveFallbackOutputDeviceId()
    {
        var devices = _deviceCache.EnumerateOutputDevices();
        return (devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault())?.Id;
    }

    /// <summary>Resolves a route's device to a sink: the host audio factory first (a borrowed lease it owns),
    /// else the session's <see cref="IAudioBackend"/> creates one it owns. Called only when a backend exists.</summary>
    private ClipAudioOutput ResolveAudioOutput(string? deviceId, AudioFormat format)
    {
        if (deviceId is { } id && _audioOutputFactory?.Invoke(id, format) is { } lease)
            return new ClipAudioOutput(lease.Output, lease.DisposeOutputOnRuntimeDispose, lease.Release);
        return new ClipAudioOutput(_audioBackend!.CreateOutput(deviceId, format), DisposeOnRelease: true, Release: null);
    }

    /// <summary>Teardown for one attached audio output: run the host's release hook (if any), then dispose the
    /// sink only when the session owns it.</summary>
    private static void ReleaseClipAudioOutput(ClipAudioOutput o)
    {
        o.Release?.Invoke();
        if (o.DisposeOnRelease)
            (o.Output as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The scheduling lead a session voice's video player gets so its frame delivery never races the
    /// compositor slot's timestamp selection: half the source frame period, capped at 20 ms (a
    /// low-rate source needs only a few ms of margin, not half its period). An unknown rate still
    /// gets a small margin - the race exists regardless.
    /// </summary>
    internal static TimeSpan VideoDeliveryLead(Rational frameRate)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            return TimeSpan.FromMilliseconds(8);
        var halfPeriodTicks = TimeSpan.TicksPerSecond * frameRate.Denominator / (frameRate.Numerator * 2L);
        return TimeSpan.FromTicks(Math.Min(halfPeriodTicks, TimeSpan.FromMilliseconds(20).Ticks));
    }

    /// <summary>Resolves + attaches ONE audio route's output with per-route error isolation: a device that
    /// cannot be opened (fixed-rate JACK graph rejecting the clip's mix rate, unplugged hardware) or attached is
    /// logged and skipped so the clip still plays on its remaining routes - instead of one bad device faulting
    /// the whole cue fire or (worse) a mid-play rebuild that has already detached every output. On success the
    /// output is appended to <paramref name="outputs"/> (the caller's ownership-tracked set).</summary>
    private bool TryAttachRouteOutput(
        S.Media.Players.MediaPlayer player,
        string outputId,
        string? deviceId,
        ChannelMap? channelMap,
        int rate,
        float gain,
        List<ClipAudioOutput> outputs,
        ShowClipAudioRoute? route = null)
    {
        ClipAudioOutput o;
        try
        {
            var channels = route is { HasGainMatrix: true }
                ? route.MatrixOutputChannels ?? route.MatrixCells!.Max(c => c.OutputChannel) + 1
                : channelMap?.OutputChannels ?? 2;
            o = ResolveAudioOutput(deviceId ?? ResolveFallbackOutputDeviceId(), new AudioFormat(rate, channels));
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning(
                "ShowSession: audio route '{0}' → device '{1}' could not open ({2}); the clip plays without it.",
                outputId, deviceId ?? "(default)", ex.Message);
            return false;
        }

        try
        {
            if (route is { HasGainMatrix: true }
                && player.AudioRouter is { } router
                && player.AudioSourceId is { } sourceId)
            {
                router.AddOutput(o.Output, outputId);
                try
                {
                    router.ApplyMatrix(sourceId, outputId, route.ToGainMatrix(gain));
                }
                catch
                {
                    router.RemoveOutput(outputId);
                    throw;
                }
            }
            else
            {
                player.AttachAudioOutput(o.Output, outputId, map: channelMap, gain: gain);
            }
        }
        catch (Exception ex)
        {
            ReleaseClipAudioOutput(o);
            MediaDiagnostics.LogWarning(
                "ShowSession: audio route '{0}' → device '{1}' could not attach ({2}); the clip plays without it.",
                outputId, deviceId ?? "(default)", ex.Message);
            return false;
        }

        outputs.Add(o);
        return true;
    }

    /// <summary>Acquires + attaches a voice's PROGRAM input (HaCue logical sends): one V-wide lease from the
    /// program-audio target on the clip's router, carrying the cue's N×V send matrix - realized as a synthetic
    /// matrix route so every existing level path (<c>ApplyAudioScale</c>: fades, envelope, master trim) rides
    /// the logical sends without touching a real device. Same error isolation as a device route: a target that
    /// rejects the voice (foreign rate with no bridge) is logged and the clip plays without audio rather than
    /// faulting the fire. Sends naming a logical channel the target does not have are logged and skipped (the
    /// preflight validator owns authoring errors); declared-but-empty resolvable sends attach nothing
    /// (explicitly silent). The lease is released through the voice's normal output teardown.</summary>
    private bool TryAttachProgramInput(
        S.Media.Players.MediaPlayer player,
        IShowProgramAudioTarget target,
        IReadOnlyList<ShowClipLogicalSend> sends,
        int rate,
        float attachLevel,
        List<ClipAudioOutput> outputs,
        List<AudioRouteTarget> routeTargets,
        string cueId)
    {
        const string outputId = "_program";
        if (player.AudioRouter is not { } router || player.AudioSourceId is not { } sourceId)
            return false;

        var channelIds = target.LogicalChannelIds;
        var cells = new List<ShowAudioMatrixCell>(sends.Count);
        foreach (var send in sends)
        {
            var busChannel = -1;
            for (var i = 0; i < channelIds.Count; i++)
            {
                if (string.Equals(channelIds[i], send.LogicalChannelId, StringComparison.Ordinal))
                {
                    busChannel = i;
                    break;
                }
            }

            if (busChannel < 0 || send.SourceChannel < 0)
            {
                MediaDiagnostics.LogWarning(
                    "ShowSession: clip '{0}' sends source channel {1} to unknown logical channel '{2}'; the send is skipped.",
                    cueId, send.SourceChannel, send.LogicalChannelId);
                continue;
            }

            cells.Add(new ShowAudioMatrixCell(send.SourceChannel, busChannel, send.Gain));
        }

        if (cells.Count == 0)
            return false; // silent by authoring (empty/unresolvable sends) - nothing to attach

        var route = new ShowClipAudioRoute { MatrixCells = cells, MatrixOutputChannels = channelIds.Count };
        ProgramAudioInputLease lease;
        try
        {
            lease = target.AcquireInput(cueId, new AudioFormat(rate, channelIds.Count));
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning(
                "ShowSession: clip '{0}' could not acquire a program input ({1}); the clip plays without audio.",
                cueId, ex.Message);
            return false;
        }

        try
        {
            router.AddOutput(lease.Output, outputId);
            try
            {
                router.ApplyMatrix(sourceId, outputId, route.ToGainMatrix(attachLevel));
            }
            catch
            {
                router.RemoveOutput(outputId);
                throw;
            }
        }
        catch (Exception ex)
        {
            lease.Dispose();
            MediaDiagnostics.LogWarning(
                "ShowSession: clip '{0}' could not attach its program input ({1}); the clip plays without audio.",
                cueId, ex.Message);
            return false;
        }

        // BORROWED like a host audio lease: the voice's teardown runs the release hook, never a dispose.
        outputs.Add(new ClipAudioOutput(lease.Output, DisposeOnRelease: false, Release: lease.Dispose));
        routeTargets.Add(new AudioRouteTarget(outputId, 1f, route));
        return true;
    }

    /// <summary>A composition layer the active clip's video is fanned to, tagged by its composition + layer index
    /// so a live placement edit can target the right one when a clip is placed onto more than one layer.</summary>
    private readonly record struct PlacedLayer(
        string CompositionId, int LayerIndex, ClipCompositionRuntime.IPlacedClipLayer Slot);

    /// <summary>Resolves the placement-addressed representation used by the runtime. A legacy document's
    /// one opacity lane is expanded over every placement, preserving its original all-layers meaning.</summary>
    private static IReadOnlyList<ShowPlacementEnvelope> PlacementOpacityEnvelopes(ShowClipBinding binding)
    {
        if (binding.PlacementOpacityEnvelopes is { Count: > 0 } addressed)
            return addressed;
        if (binding.OpacityEnvelope is not { Count: > 0 } legacy)
            return [];
        return
        [
            .. binding.GetPlacements().Select(placement => new ShowPlacementEnvelope(
                placement.CompositionId,
                placement.LayerIndex,
                legacy)),
        ];
    }

    // Fire sequencing (NXT-03): the fire-lock + in-flight fire cancellation live on the orchestrator (split
    // along its ownership seam - review Part-5 #2); this session's public fire/GO API delegates to it.
    // _showGeneration is bumped on every load so a fire whose open straddled a reload discards its (now-stale)
    // clip at commit instead of corrupting the newer show.
    private readonly CueRunner _fires;
    private volatile int _showGeneration;

    /// <param name="audioBackend">Optional. When supplied, each transport group plays its active clip on a
    /// master output created on this backend (D11). Null runs the cue/transport mechanics with no device.</param>
    /// <param name="subtitleFactory">Optional host-wired factory (path + stream index + canvas width/height → overlay
    /// source). When set, a composition-bound clip's selected subtitles auto-attach as
    /// top layer. Keeps the session renderer-agnostic - see <c>S.Media.Subtitles.SubtitleSourceFactory.FromFile</c>.</param>
    public ShowSession(
        IMediaRegistry registry,
        IAudioBackend? audioBackend = null,
        Func<string, int, int, int, IVideoOverlaySource?>? subtitleFactory = null,
        Func<string, string, int, int, IReadOnlyList<ClipCompositionOutputLease>>? videoOutputFactory = null,
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null,
        Func<string, AudioFormat, ClipAudioOutputLease?>? audioOutputFactory = null,
        Func<string, S.Media.Core.Buses.MediaItemMetadata?>? metadataProbe = null,
        IShowProgramAudioTarget? programAudioTarget = null,
        int dispatcherCapacity = SessionDispatcher.DefaultCapacity)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = new SessionDispatcher("show-session", dispatcherCapacity);
        _audioBackend = audioBackend;
        _deviceCache = new AudioOutputDeviceCache(audioBackend);
        _subtitleFactory = subtitleFactory;
        _videoOutputFactory = videoOutputFactory;
        _compositorFactory = compositorFactory;
        _audioOutputFactory = audioOutputFactory;
        _programAudio = programAudioTarget;
        _metadataPublisher = new ShowSessionMetadataPublisher(MetadataHub, metadataProbe);
        _standby.StandbyStatesChanged += states => PreparedCuesChanged?.Invoke(states);

        _visualizers = new ShowSessionVisualizerService(
            RegisterVisualizerTap, RemoveTapFromActiveClips, ReleaseVisualizerTapRegistration, MetadataHub);
        _fires = new CueRunner(this);
        // Two independent surfaces, not one class with two halves: a voice is a raw path with no cue,
        // document or canvas anywhere in reach, and saying so in the constructor signatures is what stops
        // a soundboard from quietly becoming part of what "the playback engine" means.
        _voicePlayer = new SoundboardVoicePlayer(
            this, _standby, audioBackend, ResolveFallbackOutputDeviceId, BuildVoiceSpec);
        _previewPlayer = new CuePreviewPlayer(
            this, _standby, audioBackend, programAudioTarget, ResolveFallbackOutputDeviceId, BuildPreviewSpec);
        _voicePlayer.VoiceEnded += id => VoiceEnded?.Invoke(id);
        _previewPlayer.PreviewEnded += id => PreviewEnded?.Invoke(id);
        _completionMonitor = new SessionCompletionMonitor(
            EndMonitorPollInterval, PollCompletionWorkFromBackgroundAsync);
    }

    /// <summary>The registry clips open through (frozen capabilities - D6).</summary>
    public IMediaRegistry Registry => _registry;

    /// <summary>Raised when the standby engine's prepared-clip set changes (the GUI's
    /// <c>PreparedCueStatesChanged</c> - a per-cue "ready" indicator). Forwards
    /// <see cref="ClipStandbyEngine"/>'s states; the UI handler marshals to the UI thread, as it does today.</summary>
    public event Action<IReadOnlyList<ClipPreparationStatus>>? PreparedCuesChanged;

    /// <summary>Raised (with the cue id) when a preview started by <see cref="PreviewCueAsync"/> ends on its
    /// own (the GUI's <c>PreviewEnded</c>). Not raised on an explicit <see cref="StopPreviewAsync"/>.</summary>
    public event Action<string>? PreviewEnded;

    /// <summary>Raised (with the voice id) when a soundboard voice started by <see cref="FireVoiceAsync"/> ends
    /// on its own. Not raised on an explicit <see cref="StopVoiceAsync"/> / <see cref="FadeVoiceAsync"/>.</summary>
    public event Action<string>? VoiceEnded;

    /// <summary>Raised (with the cue id) when a transport-group clip reaches its NATURAL end and is released by
    /// the end-of-clip machinery - the trimmed/duration out-point's plain stop, or a natural fade-out completing.
    /// Never raised for an operator stop/cancel/reload, a loop (keeps running), or a freeze (stays up) - it is
    /// the host's cue auto-follow trigger (the legacy engine's <c>NaturalEnd</c>). Raised from the session
    /// dispatcher; marshal in the handler.</summary>
    public event Action<string>? ClipNaturallyEnded;

    /// <summary>Raised (with the cue id) when a monitored clip enters its binding's
    /// <see cref="ShowClipBinding.PreEndNotify"/> window before its natural out-point - the host's "fire the
    /// next item early" hook for dual-voice playlist crossfades (the pre-end companion of
    /// <see cref="ClipNaturallyEnded"/>, sharing the same end monitor rather than a second timer). At most
    /// once per committed clip (a later backwards seek does not re-arm it); never raised for looping/freezing
    /// clips, or when the clip is stopped/replaced first. Raised from the session dispatcher; marshal in the
    /// handler. Never raised when <see cref="ShowClipBinding.PreEndNotify"/> is zero (the default).</summary>
    public event Action<string>? ClipApproachingEnd;

    /// <summary>Raised when an active clip's audio path fails mid-show: an output's Submit threw (a latched
    /// backend callback fault, a dying device stream) or the clip's whole audio router faulted (its pacing
    /// clock failed - primary output lost). Without a subscriber these were silent - the operator saw a
    /// frozen playhead and no sound with nothing in the log. Raised from audio/pump threads; marshal in the
    /// handler. Best-effort diagnostics: the clip is NOT auto-stopped (video may still be running).</summary>
    public event Action<ShowPlaybackAlert>? PlaybackAlert;

    /// <summary>Whether the session is disposed - for owned components' commit-time staleness checks
    /// (<see cref="VoicePlayer"/>); the public API throws via <see cref="ObjectDisposedException.ThrowIf"/>.</summary>
    internal bool IsDisposed => _disposed;

    // --- dispatcher (D5) ---------------------------------------------------------------------------

    /// <summary>Fire-and-forget a command on the session thread (runs inline if already on it).</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dispatcher.IsOnDispatcherThread)
        {
            action();
            return;
        }

        if (!_dispatcher.Post(action))
        {
            if (_dispatcher.IsDisposed)
                throw new ObjectDisposedException(nameof(ShowSession));
            throw new SessionDispatcherOverloadedException(_dispatcher.Name, _dispatcher.Capacity);
        }
    }

    /// <summary>Marshals <paramref name="func"/> onto the session thread and awaits its result. A reentrant
    /// call (already on the session thread) runs inline to avoid self-deadlock.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dispatcher.IsOnDispatcherThread)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        return _dispatcher.InvokeAsync(func);
    }

    /// <summary>Marshals a non-returning command onto the session thread.</summary>
    public Task InvokeAsync(Func<Task> func) =>
        InvokeAsync(async () =>
        {
            await func().ConfigureAwait(false);
            return true;
        });

    // --- show loading ------------------------------------------------------------------------------

    /// <summary>
    /// Builds the cue graph from <paramref name="document"/>: each clip-bound cue, when fired, opens its
    /// media through the registry and plays it on the cue's transport group. Call before firing cues.
    /// </summary>
    public void LoadDocument(ShowDocument document) =>
        LoadDocumentAsync(document).GetAwaiter().GetResult();

    /// <summary>Asynchronously loads a show document on the session dispatcher.</summary>
    /// <param name="preserveMatchingCompositions">
    /// Opt-in: when true, a composition in the new document whose id + width + height + frame rate matches
    /// a currently-live one is REUSED in place - its <see cref="ClipCompositionRuntime"/> (GL thread /
    /// context) and any attached visualizer (surface + audio tap + source) are kept alive across the reload
    /// instead of being disposed and rebuilt. The outgoing clip's layers still clear (its group is torn
    /// down) and the incoming clip re-masters the composition, so content swaps while a persistent
    /// visualizer keeps running. Default false ⇒ the historical full teardown/rebuild (cue-player behaviour
    /// is unchanged unless it opts in).
    /// </param>
    /// <param name="preserveActiveGroups">
    /// Opt-in: when true, a transport group whose live voices are ALL still described unchanged by the
    /// incoming document keeps playing across the reload, with its voices and its GO cursor intact; every
    /// other group is torn down exactly as before. A group is retained only when every one of its voices
    /// (active and crossfade tails alike) maps to a clip binding that is equal in every field, so a reload
    /// can never leave a voice playing content the document no longer describes.
    /// <para>This is what makes "editing never stops unrelated playback" possible: hosts reload the whole
    /// merged document after any structural edit, so without it an edit to one cue list tears down every
    /// playing cue in every list - and per-list transport positions cannot survive an edit either. Default
    /// false ⇒ the historical full teardown.</para>
    /// </param>
    public Task LoadDocumentAsync(
        ShowDocument document,
        bool preserveMatchingCompositions = false,
        bool preserveActiveGroups = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        _fires.CancelAllFires(); // a reload must not wait behind a long in-flight fire (NXT-03), and prepared batches hold pre-reload clips
        return InvokeAsync(() =>
            LoadDocumentCoreAsync(document, preserveMatchingCompositions, preserveActiveGroups));
    }

    /// <summary>
    /// The groups whose live voices are all still described, unchanged, by the incoming document - the only
    /// ones that may keep playing across a reload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is deliberately conservative: a group survives only when EVERY voice it holds (active and
    /// tails alike) maps to a clip binding in the new document that is equal in every field. If anything at
    /// all changed about what a voice is playing, that group is torn down exactly as it always was, so a
    /// reload can never leave a voice playing content the document no longer describes. Being strict here is
    /// what makes the feature safe to turn on: the failure mode of being too eager is a show that keeps
    /// playing something you just edited away, which is far worse than a restarted cue.
    /// </para>
    /// <para>
    /// This is what lets an edit to one cue list stop tearing down another. HaPlay (and HaCue2) reload the
    /// whole merged document after any structural edit, so before this every edit stopped every playing cue
    /// in every list - the reason "editing never blocks playback" and per-list transport positions were
    /// mutually exclusive with the app's normal editing loop.
    /// </para>
    /// <para>
    /// GO cursors are no longer tied to this decision at all - they live on the session (<c>_goCursors</c>)
    /// and survive any preserving reload, playing or not.
    /// </para>
    /// </remarks>
    private HashSet<string> RetainableGroupIds(
        IReadOnlyDictionary<string, ShowClipBinding> newClipsByCue,
        ShowDocument document,
        IReadOnlySet<string> preservedCompositionIds)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        if (_groups.Count == 0)
            return retained;

        // A group only means anything if the incoming document still routes cues to it.
        var incomingGroupIds = new HashSet<string>(
            document.Cues.Select(c => c.GroupId ?? DefaultGroup), StringComparer.Ordinal);
        var inheritedRoutingChanged = !SameRoutes(_routes, document.Routes)
                                      || !_audioOutputs.SequenceEqual(document.AudioOutputs);

        foreach (var (groupId, group) in _groups)
        {
            if (!incomingGroupIds.Contains(groupId))
                continue;

            var voices = group.Voices;
            if (voices.Count == 0)
                continue; // nothing to preserve; let the normal teardown reclaim it

            var allUnchanged = true;
            foreach (var voice in voices)
            {
                var incoming = newClipsByCue.GetValueOrDefault(voice.Binding.ClipId);
                if (incoming is null || !SameClipBinding(voice.Binding, incoming))
                {
                    allUnchanged = false;
                    break;
                }

                // A retained video voice still owns live LayerSlots. Its compositions must therefore
                // be the exact runtimes retained above; otherwise the old runtime would be disposed
                // under a voice that continues to submit to it.
                if (voice.Binding.GetPlacements().Any(
                        p => !preservedCompositionIds.Contains(p.CompositionId)))
                {
                    allUnchanged = false;
                    break;
                }

                // Null direct routes inherit the show/group patch. If that patch changed, keeping the
                // voice would preserve stale physical output leases despite an equal clip binding.
                if (inheritedRoutingChanged
                    && voice.Binding.AudioRoutes is null
                    && voice.Binding.LogicalSends is null)
                {
                    allUnchanged = false;
                    break;
                }
            }

            if (allUnchanged)
                retained.Add(groupId);
        }

        return retained;
    }

    /// <summary>
    /// Whether two clip bindings describe the same playback in every respect.
    /// </summary>
    /// <remarks>
    /// The source-generated wire form is a convenient canonical deep representation: it compares nested
    /// arrays/lists by value, includes newly-added fields automatically, and deliberately distinguishes null
    /// from an empty AudioRoutes/LogicalSends list because those have different routing semantics.
    /// </remarks>
    private static bool SameClipBinding(ShowClipBinding a, ShowClipBinding b) =>
        JsonSerializer.Serialize(a, ShowDocumentJsonContext.Default.ShowClipBinding)
        == JsonSerializer.Serialize(b, ShowDocumentJsonContext.Default.ShowClipBinding);

    private static bool SameComposition(ShowComposition a, ShowComposition b) =>
        JsonSerializer.Serialize(a with { Name = string.Empty }, ShowDocumentJsonContext.Default.ShowComposition)
        == JsonSerializer.Serialize(b with { Name = string.Empty }, ShowDocumentJsonContext.Default.ShowComposition);

    private static bool SameRoutes(
        IReadOnlyList<OutputPatchRoute> a,
        IReadOnlyList<OutputPatchRoute> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] with { ChannelMatrix = null } != b[i] with { ChannelMatrix = null })
                return false;
            if ((a[i].ChannelMatrix is null) != (b[i].ChannelMatrix is null))
                return false;
            if (a[i].ChannelMatrix is { } am && !am.AsSpan().SequenceEqual(b[i].ChannelMatrix!))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether loading <paramref name="document"/> would leave every playing voice exactly as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For hosts that reload the whole merged document after any edit. Such a host has only two blunt
    /// choices otherwise: reload always — which restarts any cue whose binding the edit touched, heard
    /// as a pop and seen as a clip jumping back to its in-point — or defer every reload while anything
    /// is playing, which is safe but leaves ordinary edits (a label, a cue nobody is playing) waiting
    /// on a fifty-minute track.
    /// </para>
    /// <para>
    /// This is the third answer, and it is exactly the load's own rule rather than an approximation of
    /// it: the same <see cref="RetainableGroupIds"/> that decides what survives, asked in advance and
    /// with nothing torn down. True means "adopt it now, nothing playing will notice"; false means the
    /// caller should hold the edit until the show is idle.
    /// </para>
    /// <para>
    /// Answered against the preserving flags, because a host that reloads per edit is the caller that
    /// passes them. A caller that would load with preservation off gets no useful answer from this and
    /// should not ask.
    /// </para>
    /// </remarks>
    public Task<bool> WouldPreservePlaybackAsync(ShowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return InvokeAsync(() => Task.FromResult(WouldPreservePlaybackCore(document)));
    }

    private bool WouldPreservePlaybackCore(ShowDocument document)
    {
        // The same normalization the load does. Without it a document whose optional arrays are null
        // would throw here rather than answering, and the caller would treat that as "unsafe" forever.
        document = document with
        {
            Cues = document.Cues ?? [],
            Clips = document.Clips ?? [],
            Compositions = document.Compositions ?? [],
            Routes = document.Routes ?? [],
            AudioOutputs = document.AudioOutputs ?? [],
        };

        var playing = _groups
            .Where(entry => entry.Value.Voices.Count > 0)
            .Select(entry => entry.Key)
            .ToList();

        // Nothing is holding a voice, so nothing can be interrupted.
        if (playing.Count == 0)
            return true;

        var retained = RetainableGroupIds(
            document.Clips.ToDictionary(clip => clip.ClipId, StringComparer.Ordinal),
            document,
            PreservableCompositionIds(document));

        return playing.All(retained.Contains);
    }

    /// <summary>
    /// The live compositions an incoming document describes identically, and which may therefore be
    /// kept in place rather than rebuilt.
    /// </summary>
    /// <remarks>
    /// Shared by the load and by <see cref="WouldPreservePlaybackAsync"/> so the prediction cannot
    /// drift from what the load actually does — the whole value of the prediction is that it is the
    /// same rule.
    /// </remarks>
    private HashSet<string> PreservableCompositionIds(ShowDocument document)
    {
        var preserved = new HashSet<string>(StringComparer.Ordinal);

        foreach (var comp in document.Compositions)
        {
            if (_compositions.TryGetValue(comp.Id, out var live)
                && _compositionDefinitions.TryGetValue(comp.Id, out var previous)
                && SameComposition(previous, comp)
                && live.CanvasFormat.Width == comp.Width
                && live.CanvasFormat.Height == comp.Height
                && live.CanvasFormat.FrameRate.Numerator == comp.FrameRateNum
                && live.CanvasFormat.FrameRate.Denominator == comp.FrameRateDen)
            {
                preserved.Add(comp.Id);
            }
        }

        return preserved;
    }

    private async Task LoadDocumentCoreAsync(
        ShowDocument document,
        bool preserveMatchingCompositions = false,
        bool preserveActiveGroups = false)
    {
        // Normalize null collections FIRST (NXT-12): a minimal/older JSON simply omits arrays the document
        // gained later, and source-gen leaves missing positional params null. Every consumer below (and at
        // fire time) assumes non-null lists, so a partial document must never smuggle a null past the load.
        document = document with
        {
            Cues = document.Cues ?? [],
            Clips = document.Clips ?? [],
            Compositions = document.Compositions ?? [],
            Routes = document.Routes ?? [],
            AudioOutputs = document.AudioOutputs ?? [],
        };

        // Validate BEFORE any teardown - a malformed document (bad version, duplicate ids/numbers, dangling
        // references, a cyclic auto-continue chain) must never destroy the running show (NXT-12 / NXT-07).
        ShowDocumentValidator.ThrowIfInvalid(document);

        // Preservation (opt-in): a live composition whose id + raster + rate match an incoming one is kept
        // alive across the reload (its GL thread/context + any attached visualizer). We reuse it instead of
        // building a replacement, and skip disposing it below.
        var preservedIds = preserveMatchingCompositions
            ? PreservableCompositionIds(document)
            : new HashSet<string>(StringComparer.Ordinal);

        // Stage the replacement graph in locals. If a composition fails to construct (or the host video factory
        // throws), dispose only the partially-built NEW compositions and rethrow - the live show is untouched.
        var newCompositions = new Dictionary<string, ClipCompositionRuntime>(StringComparer.Ordinal);
        try
        {
            foreach (var comp in document.Compositions)
            {
                if (preservedIds.Contains(comp.Id))
                    continue; // reuse the live runtime - do not build a replacement

                var definition = new ClipCompositionDefinition(
                    comp.Id, comp.Name, comp.Width, comp.Height, comp.FrameRateNum, comp.FrameRateDen);
                // Host-provided leases (the GUI's NDI/SDL/local lines for this composition). The host owns each
                // output's lifetime and declares dispose/release on its lease - the session must NOT dispose a
                // borrowed host output (NXT-01). When the factory is absent or returns none, a session-owned
                // discarding lease keeps the CPU pump composing headless.
                var hostLeases = _videoOutputFactory?.Invoke(comp.Id, comp.Name, comp.Width, comp.Height);
                var leases = hostLeases is { Count: > 0 }
                    ? hostLeases
                    : [new ClipCompositionOutputLease(
                        $"{comp.Id}_out", comp.Name, new DiscardingVideoOutput(), DisposeOutputOnRuntimeDispose: true)];
                newCompositions[comp.Id] = new ClipCompositionRuntime(
                    definition, leases, compositorFactory: _compositorFactory, compositionMapping: comp.OutputMapping);
            }
        }
        catch
        {
            foreach (var built in newCompositions.Values)
                built.Dispose();
            throw; // running show left intact - fields are not mutated until the commit below
        }

        var newClipsByCue = document.Clips.ToDictionary(c => c.ClipId, StringComparer.Ordinal);
        // Staged, not installed: the cue layer builds its replacement graph here and it becomes live only at
        // the commit below, so a load that throws part-way leaves the running show untouched.
        var newCueGraph = _fires.StageCues(
            document.Cues, id => newClipsByCue.GetValueOrDefault(id), DefaultGroup);

        // Which running groups may survive this reload (opt-in; see RetainableGroupIds). Computed BEFORE the
        // teardown because it reads the live voices' bindings.
        var retainedGroupIds = preserveActiveGroups
            ? RetainableGroupIds(newClipsByCue, document, preservedIds)
            : new HashSet<string>(StringComparer.Ordinal);

        // Commit (atomic on the dispatcher): retire the running show, then swap in the staged graph. Nothing
        // below can fail, so the swap can't leave a half-built replacement.
        // Disposing the groups tears down the outgoing clips - this also disposes their layer slots, so a
        // PRESERVED composition is left with only its persistent surface layers (e.g. the visualizer).
        if (retainedGroupIds.Count == 0)
            await DisposeGroupsAsync().ConfigureAwait(false);
        else
            await DisposeGroupsExceptAsync(retainedGroupIds).ConfigureAwait(false);

        // Test-pattern + visualizer slots die with their compositions - EXCEPT on preserved compositions,
        // whose slots (and, for the visualizer, its audio tap + source) are kept alive.
        var visualizersToReattach = RetainSlotsForPreservedCompositionsOnly(preservedIds, newCompositions);
        foreach (var (id, composition) in _compositions)
        {
            if (!preservedIds.Contains(id))
                composition.Dispose();
        }

        // Rebuild the map: preserved live runtimes stay, everything else is the freshly-built set. A preserved
        // composition releases its clock master so the INCOMING clip re-masters it (the pump free-runs - and
        // keeps rendering its visualizer - in between).
        var preserved = _compositions.Where(kv => preservedIds.Contains(kv.Key)).ToList();
        _compositions.Clear();
        foreach (var (id, runtime) in preserved)
        {
            runtime.ResetClockMaster();
            _compositions[id] = runtime;
        }
        foreach (var (id, runtime) in newCompositions)
            _compositions[id] = runtime;
        ReattachPersistentVisualizers(visualizersToReattach);
        PublishCompositionsView(); // refresh the lock-free health-poll view for the new composition set

        // A non-preserving load is "a different show", so its lists start at the top; a preserving load is an
        // edit of the running show and must not rewind every list's playhead.
        if (!preserveActiveGroups)
            _goCursors.Clear();
        _fires.Commit(newCueGraph);
        _clipsById = newClipsByCue;
        _routes = document.Routes;
        _audioOutputs = document.AudioOutputs;
        _compositionDefinitions = document.Compositions.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _showGeneration++; // a fire whose open straddled this reload bails at commit (NXT-03 off-dispatcher)

        // Background pre-roll of the first cues so the first GO arms instantly. Launched with ExecutionContext
        // flow SUPPRESSED (NXT-22): we are ON the dispatcher here, and a plain fire-and-forget would carry the
        // dispatcher's AsyncLocal identity into the warm task's continuations - a future InvokeAsync from such a
        // continuation would run inline OFF the real loop and race transport commands (the same trap the
        // monitors guard against with SuppressFlow).
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => WarmUpcomingAsync());
    }

    private async ValueTask PlayClipAsync(
        string groupId,
        ShowClipBinding? binding,
        CancellationToken cancellationToken,
        Func<Task>? waitForStartBarrier = null,
        Func<Task>? waitForStartEdge = null,
        (TimeSpan Duration, FadeShape Curve)? crossfade = null,
        TimeSpan? initialPosition = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (binding is null)
            return; // a control/stop cue with no media of its own

        // --- SETUP (on the dispatcher): capture the show generation, group and composition. The layer is
        // intentionally NOT created yet: its placement transform needs the media's actual negotiated source
        // dimensions, which are unknown until ArmAsync opens the clip below.
        var setup = await InvokeAsync(() =>
        {
            var generation = _showGeneration;
            var grp = GetOrAddGroup(groupId);
            // Compositions are resolved per-placement in CommitClipAsync (also on the dispatcher) so a clip can
            // fan onto several - the group, generation and open ticket are all the pre-open setup needs.
            return Task.FromResult((generation, group: grp, ticket: grp.NextOpenTicket()));
        }).ConfigureAwait(false);

        // --- OPEN (OFF the dispatcher): arm the clip through the standby engine - it opens via the registry
        // (auto-wiring adaptive-rate drift correction) and seeks to the trim-in (Window.Start), reusing a warm
        // prepared clip when present. The long part; the dispatcher loop stays free throughout (NXT-03).
        var armPosition = initialPosition is { } requestedPosition
            ? TimeSpan.FromTicks(Math.Max(binding.StartOffset.Ticks, requestedPosition.Ticks))
            : binding.StartOffset;
        var cueElapsed = armPosition - binding.StartOffset;
        var armVariant = initialPosition is not null ? $"scheduled-{armPosition.Ticks}" : null;
        var armed = await _standby.ArmAsync(
                BuildClipSpec(binding, armVariant, armPosition), cancellationToken)
            .ConfigureAwait(false);

        if (waitForStartBarrier is not null)
        {
            try
            {
                // Group fire: do not let a fast-opening sibling commit/start while a slower decoder is still
                // arming. Once every sibling is ready, their short commits queue together on the dispatcher.
                await waitForStartBarrier().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await armed.ReleaseAsync().ConfigureAwait(false);
                throw;
            }
        }

        // --- STAGE (on the dispatcher): build the voice and attach everything it owns (layers black, audio
        // routes at their start level), or discard the armed clip if the show was reloaded / the fire was
        // cancelled (STOP) during the open (NXT-03). Deliberately does NOT prepare or start anything - this
        // turn is milliseconds.
        var staged = await InvokeAsync(() => StageClipCommitAsync(
                groupId, binding, cancellationToken, setup, armed, crossfade,
                deferStart: waitForStartEdge is not null,
                cueElapsed: cueElapsed))
            .ConfigureAwait(false);

        // --- PREPARE (OFF the dispatcher): decode spin-up, audio buffer wait, sync present - the slow half,
        // up to 250 ms for a video clip and bounded by the video-buffer cap. This used to run inside the
        // commit's dispatcher turn, which parked the serial loop for its whole duration: a manual GO on a
        // video cue ~200 ms before a timeline edge made the edge's starters queue behind the present-sync
        // and the whole aligned group started late. The staged voice is NOT yet committed, so no stop, seek
        // or live edit can reach this player while it prepares - the only rollback owner is this method.
        // A dedicated thread because PrepareStart BLOCKS (buffer waits): an all-together batch prepares its
        // siblings concurrently, and parking N pool threads on waits would starve unrelated continuations.
        if (staged is not null)
        {
            try
            {
                await Task.Factory.StartNew(
                        staged.Prepare, cancellationToken,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Not committed yet, so the voice releases on its own: layers, routes and the armed clip
                // (prepared = no longer pristine, so the clip disposes rather than returning to standby).
                await InvokeAsync(() => staged.Voice.ReleaseAsync().AsTask()).ConfigureAwait(false);
                throw;
            }
        }

        // --- COMMIT (on the dispatcher): swap the prepared voice in as the group's Active. A discarded
        // stage (reload/STOP/superseded during the open) skips this but still waits at the edge below so
        // the barrier's participant count stays truthful. For a single fire the clocks start inside this
        // same turn - prepare is already paid, so the turn is short either way.
        var startVoice = staged is null
            ? null
            : await InvokeAsync(() => FinalizeClipCommitAsync(staged, cancellationToken)).ConfigureAwait(false);

        if (waitForStartEdge is null)
            return;

        // --- START EDGE (group fires only): every sibling has committed and fully prepared before any
        // clock starts, so the group starts on one edge instead of staggering behind the serialized
        // commits (a video sibling's present-sync alone is up to 250 ms). The starters queue on the
        // dispatcher but each is a few thread-starts - microseconds, not milliseconds.
        // A discarded commit (reload/STOP/superseded during the open) still waits at the edge so the
        // barrier's participant count stays truthful; it just has nothing to start.
        try
        {
            await waitForStartEdge().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (startVoice is not null)
                await InvokeAsync(() =>
                {
                    try
                    {
                        startVoice.Start();
                    }
                    catch (ObjectDisposedException)
                    {
                        // A STOP tore the committed voice down while it waited at the edge. Starting a
                        // dead voice is moot, and the stop already owns the teardown.
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch
        {
            // The voice is already committed while it waits at an absolute edge. Cancellation therefore
            // has to roll the exact voice back through its group; releasing only the armed clip would leave
            // a hidden active layer and a program-bus lease behind.
            if (startVoice is not null)
                await InvokeAsync(() => ReleaseVoiceAsync(startVoice.Group, startVoice.Voice).AsTask())
                    .ConfigureAwait(false);
            throw;
        }
    }

    private sealed record DeferredClipStart(TransportGroup Group, TransportVoice Voice, Action Start);

    /// <summary>Test-only fault seam (§A): raised on the dispatcher immediately AFTER a clip has committed to
    /// its group - i.e. after a crossfade handoff has moved the displaced voice into its release. The
    /// orphaned-tail case is only reachable by faulting exactly there, so the seam is deliberately one
    /// narrow point rather than a general hook. Null in production.</summary>
    internal Action<string>? PostCommitFault { get; set; }

    /// <summary>Test-only access to the cue runner, for tuning the wedge bounds
    /// (<see cref="CueRunner.BatchPreparationTimeout"/>) without a half-minute test.</summary>
    internal CueRunner FireRunnerForTest => _fires;

    /// <summary>Everything the finalize turn needs from the stage turn, carried across the off-dispatcher
    /// preparation between them (see <see cref="PlayClipAsync"/>'s STAGE/PREPARE/COMMIT phases). The voice is
    /// staged but NOT committed while this exists - no session command can reach it - so the record's owner
    /// (the one fire that built it) is the only code that may release it.</summary>
    private sealed class StagedClipCommit
    {
        public required string GroupId { get; init; }
        public required ShowClipBinding Binding { get; init; }
        public required (int generation, TransportGroup group, long ticket) Setup { get; init; }
        public required IArmedClip Armed { get; init; }
        public required TransportGroup Group { get; init; }
        public required TransportVoice Voice { get; init; }
        public required (TimeSpan Duration, FadeShape Curve)? Crossfade { get; init; }
        public required bool DeferStart { get; init; }
        public required List<AudioRouteTarget> RouteTargets { get; init; }
        public required List<(string OutputId, string DeviceId)> AudioPumps { get; init; }
        public required bool FadeIn { get; init; }
        public required float FadeInStartLevel { get; init; }
        public required IReadOnlyList<float>? FadeInFullLevels { get; init; }
        public required IPlaybackClock? VideoOnlyMaster { get; init; }
        public required Action BeginClipWork { get; init; }

        /// <summary>Deferred GPU-surface attach, run by the finalize turn AFTER the timeline re-anchor so
        /// the pull-rendered surface can never composite against the previous clip's coordinate. Null on
        /// the frame path. Returns the authored full fade level it attached black, or null.</summary>
        public required Func<IReadOnlyList<float>?>? AttachSurfaceLayer { get; init; }

        /// <summary>The clock starter produced by <see cref="Prepare"/>. Written on the preparation thread,
        /// read on the dispatcher strictly after the preparation task completed - the await is the fence.</summary>
        public Action? Starter { get; private set; }

        /// <summary>The slow half (decode spin-up, buffer wait, sync present), run OFF the dispatcher. The
        /// armed clip is marked started from here on, so a discard after this point disposes it rather than
        /// returning it to warm standby.</summary>
        public void Prepare() => Starter = Armed.PrepareStart(videoOnlyMaster: VideoOnlyMaster);
    }

    /// <summary>The on-dispatcher STAGE half of <see cref="PlayClipAsync"/> (NXT-03): builds the voice and
    /// attaches everything it owns (composition layers, audio routes, subtitles, taps) - unless the show was
    /// reloaded (generation moved) or the fire was cancelled while the clip opened off the dispatcher, in
    /// which case it discards the now-stale clip without touching the live show and returns null. Deliberately
    /// starts NOTHING: preparation runs off the dispatcher afterwards and <see cref="FinalizeClipCommitAsync"/>
    /// swaps the voice in, so this turn stays milliseconds no matter how heavy the media is.</summary>
    /// <param name="deferStart">Group fires: the caller will hold the prepared voice at a start edge, so audio
    /// attaches silent and layers black regardless of fades - the starter restores levels at the edge. False
    /// (single fires) attaches at the live level and the finalize turn starts the clocks itself.</param>
    private async Task<StagedClipCommit?> StageClipCommitAsync(
        string groupId,
        ShowClipBinding binding,
        CancellationToken cancellationToken,
        (int generation, TransportGroup group, long ticket) setup,
        IArmedClip armed,
        (TimeSpan Duration, FadeShape Curve)? crossfade = null,
        bool deferStart = false,
        TimeSpan cueElapsed = default)
    {
        // Superseded by a later open on the same group (see TransportGroup.OpenSequence), cancelled, or
        // straddling a reload - discard without touching the live show.
        if (cancellationToken.IsCancellationRequested
            || _showGeneration != setup.generation
            || setup.group.OpenSequence != setup.ticket
            || _disposed)
        {
            await armed.ReleaseAsync().ConfigureAwait(false);
            return null;
        }

        var group = setup.group;
        // A crossfade only means something when it actually replaces a live clip: a fire onto an idle
        // group (or a non-positive window) is the plain butt-splice path, unchanged. Resolved here on
        // the dispatcher - the incoming clip has already opened, so an open failure never reaches this
        // point and the old Active stays untouched (design doc: open failure = no fade).
        if (crossfade is not { Duration.Ticks: > 0 } || group.ActiveVoice is null)
            crossfade = null;
        var player = armed.Player;
        // The venue's A/V trim rides every committed voice; live changes are pushed separately by
        // the property setter, so this is only the "voices born after the knob moved" half.
        if (player.HasVideo)
        {
            player.Video.PlayheadOffset = VideoPlayheadOffset;
            // Session video is consumed exclusively by compositor layer slots (voice.Outputs is
            // audio-only), which SELECT frames by timestamp at the canvas master time. Emit each
            // frame half a source period early so delivery and selection stop racing on the same
            // grid line - see VideoPlayer.DeliveryLead for the stutter this removes. Half a period
            // clears the ±EarlyTolerance arrival band completely; capped so a low-rate source
            // (a slideshow's 1 fps) does not lead by half a second for a race that any few-ms
            // margin already wins.
            player.Video.DeliveryLead = VideoDeliveryLead(player.Video.Format.FrameRate);
        }
        // The incoming VOICE exists from here on and owns everything wired below, so the failure path is
        // one teardown (its own) whether the fault lands before or after the commit - instead of a catch
        // block that re-released resources the group had already taken over.
        var voice = new TransportVoice(armed, binding, MasterTrim);
        var layers = voice.Layers;
        var timelineClaims = voice.TimelineClaims;
        var outputs = voice.Outputs;
        var subtitleAttachments = voice.Subtitles;
        // A crossfade implies a fade-in over the same window/curve when the binding has none - the
        // incoming half of the cross; a configured per-cue FadeIn (and its curve) always wins.
        var fadeIn = binding.FadeIn > TimeSpan.Zero || crossfade is not null;
        var fadeInDuration = binding.FadeIn > TimeSpan.Zero ? binding.FadeIn : crossfade?.Duration ?? TimeSpan.Zero;
        // A user-drawn shape on the binding wins over the built-in law; a crossfade-implied fade-in has
        // no binding of its own, so it keeps the crossfade's law.
        FadeShape fadeInCurve = binding.FadeIn > TimeSpan.Zero
            ? new FadeShape(binding.FadeInCurve, binding.FadeInShape)
            : crossfade?.Curve ?? FadeCurve.Linear;
        cueElapsed = cueElapsed < TimeSpan.Zero ? TimeSpan.Zero : cueElapsed;
        var opacityEnvelopes = PlacementOpacityEnvelopes(binding);
        var transformEnvelopes = binding.PlacementTransformEnvelopes ?? [];
        var effectEnvelopes = binding.PlacementEffectEnvelopes ?? [];
        var fadeInStartLevel = fadeIn
            ? FadeCurves.LevelUp(cueElapsed, fadeInDuration, fadeInCurve)
            : 1f;
        // Retained for the active clip so both fade-in and every stop path ramp each route relative to its
        // configured gain rather than assuming unity.
        var routeTargets = new List<AudioRouteTarget>();
        // Device-tagged routed outputs (OutputId → device) for the per-line audio-health poll.
        var audioPumps = new List<(string OutputId, string DeviceId)>();
        // Deferred GPU-surface attach (see the surface branch below) - runs in the finalize turn, after
        // the timeline re-anchor; returns the authored full fade level it zeroed, or null.
        Func<IReadOnlyList<float>?>? attachSurfaceLayer = null;
        try
        {
            if (player.VideoSource is { } videoSource)
            {
                // A cue may place its ONE decoded source onto several composition layers at once - PiP, the same
                // feed in two regions, or mirrored to a second canvas. Fan the player's video out to each: one
                // LayerSlot per placement, all fed by the same VideoRouter input through a unique output id.
                // PlacementResolver scales source pixels into the normalized destination rectangle; passing the
                // negotiated source format (not the canvas) keeps a clip smaller than the canvas correctly sized
                // rather than identity-stretched.
                //
                // NXT-04: every DISTINCT composition follows the group's one authoritative TransportTimeline.
                // Its master coordinate drives cadence/output target time; its source coordinate selects frames;
                // cue-local origin/trim/rate/live-correlation remain available on the same generation. This also
                // closes the former live free-run exception: a live clip is correlated to the group and composed
                // against that contract rather than using an unrelated latest-frame clock.
                var mastered = new HashSet<string>(StringComparer.Ordinal);
                var fanoutIndex = 0;
                var placements = binding.GetPlacements();

                // GPU surface path (NXT-10): a single-placement clip whose source can render itself as a
                // compositor layer surface, on a surface-hosting compositor, composites GPU-side - no CPU
                // frame fan-out at all. The surface renders at the pump's SOURCE-time coordinate (the same
                // TransportTimeline that selects decoded frames), so transport (seek/pause/trim/end
                // monitoring) behaves identically to the frame path. Multi-placement clips keep the frame
                // path: one decoded stream fans out cheaply, N independent GPU renders would not.
                //
                // Attached from the FINALIZE turn, after CommitVoiceAsync re-anchors the group timeline -
                // not here. A surface is PULL-rendered: the moment it is on the canvas the pump renders it
                // at the timeline's source coordinate, and between stage and commit that timeline still
                // describes the PREVIOUS clip (or an anchor that does not exist yet), so an early attach
                // composited the new surface against the wrong coordinate for the whole preparation window.
                // Frame layers below have no such problem (nothing selects until frames are submitted) and
                // MUST attach now - the preparation's sync present parks frame 0 into their slots.
                if (player.VideoSource is ILayerSurfaceVideoSource surfaceSource
                    && placements.Count == 1
                    && _compositions.TryGetValue(placements[0].CompositionId, out var surfaceComp)
                    && surfaceComp.SupportsSurfaceLayers)
                {
                    var placement = placements[0];
                    attachSurfaceLayer = () =>
                    {
                        var surfaceSlot = surfaceComp.AddSurfaceLayer(
                            surfaceSource.CreateLayerSurface(),
                            BuildVideoPlacementSpec(placement.CompositionId, placement.LayerIndex, placement.Placement));
                        layers.Add(new PlacedLayer(placement.CompositionId, placement.LayerIndex, surfaceSlot));
                        timelineClaims.Add(surfaceComp.AcquireTransportTimeline(group.Timeline));
                        surfaceSlot.TimeSelectionOffset = -VideoPlayheadOffset;
                        if (opacityEnvelopes.FirstOrDefault(candidate =>
                                string.Equals(candidate.CompositionId, placement.CompositionId, StringComparison.Ordinal)
                                && candidate.LayerIndex == placement.LayerIndex) is { } surfaceLane)
                            surfaceSlot.SetAutomationLevel(
                                Math.Clamp(VolumeEnvelopes.Sample(surfaceLane.Points, cueElapsed), 0f, 1f),
                                surfaceLane.Absolute);
                        SeedPlacementTransformEnvelopes(
                            surfaceSlot,
                            placement.CompositionId,
                            placement.LayerIndex,
                            transformEnvelopes,
                            cueElapsed);
                        SeedPlacementEffectEnvelopes(
                            surfaceSlot,
                            placement.CompositionId,
                            placement.LayerIndex,
                            effectEnvelopes,
                            cueElapsed);
                        MediaDiagnostics.LogInformation(
                            "clip {CueId}: video composites as a GPU layer surface on {Composition} (NXT-10)",
                            binding.ClipId, placement.CompositionId);
                        // The same attach-black rule the staged layers follow, applied at the deferred
                        // attach point; the authored full level goes back through BaseLayerFadeLevels.
                        if (!fadeIn && !deferStart)
                            return null;
                        var fullLevel = surfaceSlot.FadeLevel;
                        surfaceSlot.FadeLevel = 0f;
                        return [fullLevel];
                    };
                }
                else
                {
                    foreach (var placement in placements)
                    {
                        if (!_compositions.TryGetValue(placement.CompositionId, out var comp))
                            continue;
                        var slot = comp.AddLayer(
                            videoSource.Format,
                            BuildVideoPlacementSpec(placement.CompositionId, placement.LayerIndex, placement.Placement),
                            alignmentTimeline: group.Timeline);
                        layers.Add(new PlacedLayer(placement.CompositionId, placement.LayerIndex, slot));
                        player.AttachVideoOutput(slot.Output, id: $"comp{fanoutIndex++}"); // unique id ⇒ router fans out
                        if (mastered.Add(placement.CompositionId))
                            timelineClaims.Add(comp.AcquireTransportTimeline(group.Timeline));
                    }
                }
            }

            // PlayheadOffset is subtracted by VideoPlayer. Apply the inverse at the composition's own
            // PTS selection/render boundary as well, otherwise the player queues look-ahead frames that
            // a per-layer alignment clock deliberately refuses to select.
            foreach (var placed in layers)
                placed.Slot.TimeSelectionOffset = -VideoPlayheadOffset;

            // Video half of a fade-in (and of a crossfade's incoming voice): like the audio routes attach
            // silent (gain 0) below, the layers attach BLACK (fade 0) so no full-opacity frame can composite
            // before the ramp's first step - StartFadeIn lifts them back to full, which is preserved as the
            // group's BaseLayerFadeLevels anchor (the commit capture would otherwise record the zeroed
            // levels and break fade cues' upward ramps). Only the FADE component is zeroed; each layer's
            // authored opacity stays on its slot and multiplies underneath.
            // Seed the opacity lane before anything composites - the runner's first step does not land until
            // the end of the commit, so a clip whose lane opens below full would flash at full until then
            // (the video twin of the envelope seeding below, and the same clip-relative time basis).
            if (opacityEnvelopes.Count > 0 && layers.Count > 0)
            {
                foreach (var placed in layers)
                    if (opacityEnvelopes.FirstOrDefault(candidate =>
                            string.Equals(candidate.CompositionId, placed.CompositionId, StringComparison.Ordinal)
                            && candidate.LayerIndex == placed.LayerIndex) is { } seedLane)
                        placed.Slot.SetAutomationLevel(
                            Math.Clamp(VolumeEnvelopes.Sample(seedLane.Points, cueElapsed), 0f, 1f),
                            seedLane.Absolute);
            }
            if (transformEnvelopes.Count > 0 && layers.Count > 0)
                foreach (var placed in layers)
                    SeedPlacementTransformEnvelopes(
                        placed.Slot,
                        placed.CompositionId,
                        placed.LayerIndex,
                        transformEnvelopes,
                        cueElapsed);
            if (effectEnvelopes.Count > 0 && layers.Count > 0)
                foreach (var placed in layers)
                    SeedPlacementEffectEnvelopes(
                        placed.Slot,
                        placed.CompositionId,
                        placed.LayerIndex,
                        effectEnvelopes,
                        cueElapsed);

            IReadOnlyList<float>? fadeInFullLevels = null;
            if ((fadeIn || deferStart) && layers.Count > 0)
            {
                fadeInFullLevels = layers.Select(placed => placed.Slot.FadeLevel).ToArray();
                foreach (var placed in layers)
                    placed.Slot.FadeLevel = 0f;
            }

            // Program-audio sends need no session backend (a HaCue host's bay owns the devices); the
            // v1 direct-route adapter below still requires one.
            var programSends = _programAudio is not null ? binding.LogicalSends : null;
            if (player.AudioRouter is not null && (_audioBackend is not null || programSends is not null))
            {
                var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                // Seed the envelope component from the automation's value at the clip's FIRST frame, before
                // anything attaches. StartEnvelopeRunner does not start until the end of the commit, so a clip
                // whose automation begins below unity would otherwise attach at unity and burst until the
                // runner's first tick - loud on a cue authored quiet. The runner then simply keeps writing the
                // same component as the clip plays on.
                voice.Level.Envelope = VolumeEnvelopes.Sample(binding.VolumeEnvelope, cueElapsed);
                // The level every route attaches at, so the FIRST buffer the device sees is already at the
                // composed level: 0 for a fade-in (the ramp lifts it), else the voice's own composition -
                // the master trim it stamped at construction, times the envelope seeded just above.
                // Attaching at the authored gain instead used to let a GO under a lowered fader push
                // FULL-LEVEL program audio to the device for the whole window between Start() below and the
                // reconciling pass after the commit (which awaits the displaced clip's teardown). Every other
                // route write in the session - the voice attach, the hot rebuild, every ramp - already goes
                // through the composition; this was the one outlier.
                var attachLevel = fadeIn || deferStart ? 0f : voice.EffectiveAudioLevel;
                if (programSends is not null)
                {
                    // HaCue logical sends: the voice plays into the project's program bus through ONE
                    // V-wide input lease; real devices, the V×R patch and clocks live behind the
                    // target. Precedence over the direct-route adapter, mirroring its explicit-empty
                    // semantics (empty sends = silent clip, not fallback).
                    TryAttachProgramInput(
                        player, _programAudio!, programSends, rate, attachLevel, outputs, routeTargets, binding.ClipId);
                }
                else if (binding.AudioRoutes is { } clipRoutes)
                {
                    // Per-clip routing (GUI per-cue audio): the clip plays on exactly its routed outputs/devices,
                    // each with its own N→M channel map + static gain. An explicitly empty collection means silent;
                    // only null inherits the show's group/default routing. The first route is the master/clock;
                    // the rest auto-slave. With a fade-in the route attaches silent and ramps up to its target.
                    for (var i = 0; i < clipRoutes.Count; i++)
                    {
                        var route = clipRoutes[i];
                        var outputId = $"clip{i}";
                        if (!TryAttachRouteOutput(
                                player, outputId, route.DeviceId, route.ToChannelMap(), rate,
                                gain: route.Gain * attachLevel, outputs, route))
                            continue; // one un-openable device must not fault the whole cue - play the rest
                        routeTargets.Add(new AudioRouteTarget(outputId, route.Gain, route));
                        if (route.DeviceId is { } clipDevice)
                            audioPumps.Add((outputId, clipDevice));
                    }
                }
                else
                {
                    // D11 per-group outputs: attach the clip's audio to each output the group declares (the first
                    // is the master/clock; the rest auto-slave with adaptive-rate). Each output's N→M channel
                    // matrix (03 §6) comes from the matching source→output route - it remaps the source channels +
                    // sets the channel count; an output with no route for this clip is plain stereo.
                    foreach (var outDef in ResolveGroupOutputs(groupId))
                    {
                        // Fade-in: attach silent (gain 0) and ramp the route gain up to unity over FadeIn after Start.
                        if (!TryAttachRouteOutput(
                                player, outDef.Id, outDef.DeviceId, ResolveOutputChannelMap(binding, outDef.Id), rate,
                                gain: attachLevel, outputs))
                            continue;
                        routeTargets.Add(new AudioRouteTarget(outDef.Id, 1f));
                        if (outDef.DeviceId is { } groupDevice)
                            audioPumps.Add((outDef.Id, groupDevice));
                    }
                }
            }

            if (layers.Count > 0
                && binding.CompositionId is { } subtitleCompositionId
                && _compositions.TryGetValue(subtitleCompositionId, out var subtitleComposition)
                && _subtitleFactory is { } subtitleFactory)
            {
                var selections = binding.GetSubtitleSelections();
                var nextLayerIndex = int.MaxValue - selections.Count;
                foreach (var selection in selections)
                {
                    var path = string.IsNullOrWhiteSpace(selection.Path) ? binding.MediaPath : selection.Path!;
                    var overlay = subtitleFactory(
                        path, selection.StreamIndex,
                        subtitleComposition.CanvasFormat.Width, subtitleComposition.CanvasFormat.Height);
                    if (overlay is not null)
                    {
                        subtitleAttachments.Add(subtitleComposition.AttachSubtitleOverlay(
                            overlay,
                            group.Timeline,
                            nextLayerIndex++,
                            positionOffsetProvider: () => -VideoPlayheadOffset));
                    }
                }
            }

            // Effect buses (Phase 4): registered audio taps ride along on every fired clip, and the
            // metadata hub learns what's playing (visualizers/overlays pick both up).
            AttachAudioTaps(player, binding.ClipId);
            PublishItemMetadata(binding);
            WireRouterAlerts(player, binding.ClipId);

            // Background per-clip work - the fade-in ramp + the end-of-clip (loop/trim-out/freeze) monitor -
            // shares one cancellation, cancelled when the voice leaves the transport. Both gated, so a plain
            // play-to-end cue with no fade starts nothing. End handling needs a known duration (live = 0).
            // Built HERE (the stage turn) as a closure because every input is a stage-time fact; it runs
            // from the finalize turn or the start edge, strictly after the voice committed.
            var end = player.Duration - binding.EndOffset;

            // Armed for EVERY clip with a known end, not only for clips that authored something to
            // happen there.
            //
            // It used to also require one of loop / non-Stop end behaviour / end offset / fade-out /
            // EndAtDuration / NotifyNaturalEnd / pre-end notify - "so a plain play-to-end cue with no
            // fade starts nothing". But noticing that a clip ENDED is not an optional end behaviour: the
            // release path lives here and nowhere else, so a plain cue that ran out simply never
            // released. It stayed its group's ActiveVoice holding a decoder, its outputs, its routes and
            // its producer lease until something re-fired or stopped the group, and it never raised
            // ClipNaturallyEnded - so a host's "what is sounding" list kept it forever (reported live:
            // ended cues sat in the Active panel with their readout counting up, because the panel falls
            // back to wall time for a listed cue whose transport is gone). A follow chain hung off the
            // same event, so a Follow cue with no fade and no trim never followed either.
            //
            // The optional behaviours are still conditional - they are decided inside PollClipEndAsync,
            // which is where they belong. The cost of arming this more widely is nil: completion polling
            // is consolidated, so one dispatcher command already walks every group per tick.
            var endHandling = player.Duration > TimeSpan.Zero && end > binding.StartOffset;
            var hasEnvelope = binding.VolumeEnvelope is { Count: > 0 } && routeTargets.Count > 0;
            void BeginClipWork()
            {
                // layers is the LIVE voice list, read at run time rather than captured as a count: a
                // GPU-surface layer attaches in the finalize turn, after this closure was built.
                var hasOpacityLane = opacityEnvelopes.Count > 0 && layers.Count > 0;
                var hasTransformLane = transformEnvelopes.Count > 0 && layers.Count > 0;
                var hasEffectLane = effectEnvelopes.Count > 0 && layers.Count > 0;
                if (!fadeIn && !endHandling && !hasEnvelope && !hasOpacityLane
                    && !hasTransformLane && !hasEffectLane)
                    return;

                var clipCts = new CancellationTokenSource();
                voice.SetClipWorkCts(clipCts);
                if (fadeIn && cueElapsed < fadeInDuration && (routeTargets.Count > 0 || layers.Count > 0))
                    StartFadeIn(
                        groupId, voice, routeTargets, fadeInDuration, fadeInCurve,
                        fadesVideo: layers.Count > 0, clipCts.Token, cueElapsed);
                if (hasEnvelope)
                    StartEnvelopeRunner(groupId, voice, binding.VolumeEnvelope!, clipCts.Token);
                if (hasOpacityLane)
                    StartOpacityLaneRunner(groupId, voice, opacityEnvelopes, clipCts.Token);
                if (hasTransformLane)
                    StartPlacementTransformRunner(groupId, voice, transformEnvelopes, clipCts.Token);
                if (hasEffectLane)
                    StartPlacementEffectRunner(groupId, voice, effectEnvelopes, clipCts.Token);
                if (endHandling)
                    StartEndMonitor(groupId, voice, end, clipCts.Token);
            }

            // One show, one time base: the master clock a silent-video voice genlocks to is a dispatcher-
            // owned fact, captured now so the off-dispatcher preparation never reads session state.
            return new StagedClipCommit
            {
                GroupId = groupId,
                Binding = binding,
                Setup = setup,
                Armed = armed,
                Group = group,
                Voice = voice,
                Crossfade = crossfade,
                DeferStart = deferStart,
                RouteTargets = routeTargets,
                AudioPumps = audioPumps,
                FadeIn = fadeIn,
                FadeInStartLevel = fadeInStartLevel,
                FadeInFullLevels = fadeInFullLevels,
                VideoOnlyMaster = _programAudio?.MasterClock,
                BeginClipWork = BeginClipWork,
                AttachSurfaceLayer = attachSurfaceLayer,
            };
        }
        catch
        {
            // The voice was never handed to the group (commit happens in the finalize turn), so it releases
            // on its own: layers, routes, claims and the armed clip.
            await voice.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The on-dispatcher COMMIT half of <see cref="PlayClipAsync"/>: re-checks that the show did not
    /// move underneath the off-dispatcher preparation (reload, STOP, a later fire's ticket), then swaps the
    /// prepared voice in as the group's Active. The stale case discards exactly like the stage turn would
    /// have - the prepared clip disposes rather than returning to warm standby, which
    /// <see cref="IArmedClip.PrepareStart"/> already guarantees by marking it started.</summary>
    private async Task<DeferredClipStart?> FinalizeClipCommitAsync(
        StagedClipCommit staged, CancellationToken cancellationToken)
    {
        var (groupId, group, voice) = (staged.GroupId, staged.Group, staged.Voice);
        if (cancellationToken.IsCancellationRequested
            || _showGeneration != staged.Setup.generation
            || staged.Setup.group.OpenSequence != staged.Setup.ticket
            || _disposed)
        {
            await voice.ReleaseAsync().ConfigureAwait(false);
            return null;
        }

        try
        {
            // The crossfade was normalized at stage time, but its partner may have stopped during the
            // off-dispatcher preparation - degrade to a butt-splice rather than handing CommitVoiceAsync a
            // crossfade with nobody to fade out (the incoming voice still ramps in from its silent attach).
            var crossfade = staged.Crossfade;
            if (crossfade is not null && group.ActiveVoice is null)
                crossfade = null;

            // Single fires: start the clocks NOW, before the handoff, so the displaced voice's release ramp
            // and the incoming voice's first samples share the commit turn exactly as the old inline
            // armed.Start() did. Preparation is already paid, so this is thread-starts - microseconds.
            if (!staged.DeferStart)
                staged.Starter!();

            // Commit: the displaced voice moves to its release (ramp armed inside the handoff) or is
            // butt-spliced away, and this voice becomes the group's Active.
            await CommitVoiceAsync(groupId, group, voice, crossfade).ConfigureAwait(false);
            PostCommitFault?.Invoke(groupId);
            // GPU-surface clips attach their layer NOW - the timeline above already describes this clip,
            // so the surface's very first pull-render is on the right coordinate (see the stage turn's
            // surface branch for why attaching earlier composited against the outgoing clip's time).
            var fadeInFullLevels = staged.FadeInFullLevels;
            if (staged.AttachSurfaceLayer is { } attachSurface)
                fadeInFullLevels = attachSurface() ?? fadeInFullLevels;
            var committedLevel = staged.DeferStart ? 0f : staged.FadeInStartLevel;
            voice.SetFadeMetadata(staged.RouteTargets, committedLevel, fadeInFullLevels);
            // One composition pass over the committed targets. The routes already attached AT this level, so
            // it is value-identical by construction - it is here so the level composition, not the attach
            // site, stays the authority (the same reason the live re-apply and the hot rebuild end with it).
            voice.ApplyAudioScale(voice.RouteTargets, voice.ClipLevel);
            // Publish the device-tagged audio outputs for the line-health poll (CommitVoiceAsync republished
            // the group views before these were set, so refresh once more now they're known).
            voice.SetAudioPumps(staged.AudioPumps);
            PublishGroupViews();

            if (!staged.DeferStart)
            {
                staged.BeginClipWork();
                return null;
            }

            return new DeferredClipStart(group, voice, () =>
            {
                // Prepared audio is attached silent and prepared video is black. Restore the authored
                // instantaneous level at the very same dispatcher turn that starts the voice clock; a
                // rehearsal start in the middle of a fade therefore joins the curve in progress.
                if (staged.FadeIn)
                    voice.ApplyFadeLevel(staged.RouteTargets, 1f, voice.BaseLayerFadeLevels, staged.FadeInStartLevel);
                else
                    voice.ApplyClipFadeLevel(staged.RouteTargets, 1f, voice.BaseLayerFadeLevels);
                staged.Starter!();
                staged.BeginClipWork();
            });
        }
        catch
        {
            // One teardown whichever side of the commit the fault landed on. An ARMING voice was never
            // handed to the group, so it releases on its own; a voice that already committed goes back
            // through the group, which clears the transport binding and drops its bus registration - and
            // leaves any tail it displaced to finish on the ramp the handoff armed.
            if (voice.State == VoiceState.Arming)
                await voice.ReleaseAsync().ConfigureAwait(false);
            else
                await ReleaseVoiceAsync(group, voice).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Builds the standby <see cref="ClipSpec"/> for a clip binding - identical on the pre-roll
    /// (prepare) and fire (arm) paths, so a warmed clip is found by its key (cue id + media path).</summary>
    private ClipSpec BuildClipSpec(
        ShowClipBinding binding,
        string? variant = null,
        TimeSpan? initialPosition = null)
    {
        // A clip that plays into the PROGRAM BUS must decode at the project's mix rate, not at any
        // device's. Its clip router submits into a bay producer, and the bay bridges a foreign rate by
        // wrapping that producer in a resampler - which hides the producer's IClockedOutput surface, so
        // the router is refused it as a pacing primary (AudioRouter.Playback.cs) and free-runs on its
        // own stopwatch instead. Nothing then ties the voice to the device that actually consumes it:
        // the two crystals drift, the producer ring fills, and the bus drops oldest frames for the rest
        // of the show. Matching the rate keeps the producer clocked, which genlocks every voice to the
        // master terminal. Falling back to ResolveBackendSampleRate(null) here was doubly wrong - it
        // reads the backend's DEFAULT device, which need not be a device the show plays on at all.
        var targetAudioRate = binding.LogicalSends is not null && _programAudio is { } programTarget
            ? programTarget.SampleRate
            : binding.AudioRoutes switch
            {
                { Count: 0 } => null, // explicitly silent: do not infer anything from the default hardware device
                { } routes => routes.Select(route => route.SampleRate).FirstOrDefault(rate => rate is > 0)
                              ?? ResolveBackendSampleRate(routes.FirstOrDefault()?.DeviceId),
                null => ResolveBackendSampleRate(null), // standalone/group-routing compatibility (cache resolves default-else-first fresh)
            };
        // Multi-track select (03 §6): null indices are the automatic default, so one shape covers both.
        var options = S.Media.Players.MediaPlayerOpenOptions.Default with
        {
            AudioStreamIndex = binding.AudioStreamIndex,
            VideoStreamIndex = binding.VideoStreamIndex,
            TargetAudioSampleRate = targetAudioRate,
            FileVideoDecodeQueueCapacity = 16,
        };
        var windowStart = initialPosition is { } requestedPosition
            ? TimeSpan.FromTicks(Math.Max(binding.StartOffset.Ticks, requestedPosition.Ticks))
            : binding.StartOffset;
        var window = windowStart > TimeSpan.Zero
            ? new S.Media.Core.ClipWindow(windowStart, TimeSpan.Zero, TimeSpan.Zero, HasKnownEnd: false)
            : S.Media.Core.ClipWindow.Unbounded;
        // A non-null variant (e.g. "preview") gives a distinct standby key so this arms a FRESH instance
        // instead of consuming GO's prepared clip.
        return new ClipSpec(
            variant is null ? binding.ClipId : $"{binding.ClipId}:{variant}",
            ClipMediaSource.File(_registry, binding.MediaPath, options),
            window,
            cacheKey: $"{binding.MediaPath}|audio:{binding.AudioStreamIndex?.ToString() ?? "auto"}" +
                      $"|video:{binding.VideoStreamIndex?.ToString() ?? "auto"}" +
                      $"|rate:{targetAudioRate?.ToString() ?? "source"}" +
                      (variant is null ? string.Empty : $"#{variant}"));
    }

    // NXT-24: backend device enumeration is not free (PortAudio walks the host API's device table, and a flaky
    // ALSA setup makes it worse) and the spec builder runs on EVERY fire / warm / voice. SESSION-01: the caching
    // + backend-rate resolution now lives in AudioOutputDeviceCache; the session just holds one.
    private readonly AudioOutputDeviceCache _deviceCache;

    private int? ResolveBackendSampleRate(string? deviceId) => _deviceCache.ResolveBackendSampleRate(deviceId);

    private static VideoPlacementSpec BuildVideoPlacementSpec(string compositionId, int layerIndex, ShowVideoPlacement? p) =>
        p is null
            ? new VideoPlacementSpec(compositionId, layerIndex, DestWidth: 1, DestHeight: 1)
            : new VideoPlacementSpec(
                compositionId, layerIndex,
                Opacity: p.Opacity, Placement: p.Fit,
                DestX: p.DestX, DestY: p.DestY, DestWidth: p.DestWidth, DestHeight: p.DestHeight,
                CropLeft: p.CropLeft, CropTop: p.CropTop,
                CropRight: p.CropRight, CropBottom: p.CropBottom,
                RotationDegrees: p.RotationDegrees,
                VideoFx: p.VideoFx,
                ChromaKey: p.ChromaKey,
                ColorAdjust: p.ColorAdjust,
                ChromaKeyInstanceId: p.ChromaKeyInstanceId,
                ColorAdjustInstanceId: p.ColorAdjustInstanceId);

    private static void SeedPlacementTransformEnvelopes(
        ClipCompositionRuntime.IPlacedClipLayer slot,
        string compositionId,
        int layerIndex,
        IReadOnlyList<ShowPlacementPropertyEnvelope> lanes,
        TimeSpan cueTime)
    {
        foreach (var lane in lanes)
            if (string.Equals(lane.CompositionId, compositionId, StringComparison.Ordinal)
                && lane.LayerIndex == layerIndex)
                slot.SetPlacementAutomation(
                    lane.Property,
                    VolumeEnvelopes.Sample(lane.Points, cueTime));
    }

    private static void SeedPlacementEffectEnvelopes(
        ClipCompositionRuntime.IPlacedClipLayer slot,
        string compositionId,
        int layerIndex,
        IReadOnlyList<ShowPlacementEffectEnvelope> lanes,
        TimeSpan cueTime)
    {
        foreach (var lane in lanes)
            if (string.Equals(lane.CompositionId, compositionId, StringComparison.Ordinal)
                && lane.LayerIndex == layerIndex)
                slot.SetEffectAutomation(
                    lane.EffectInstanceId,
                    lane.Property,
                    VolumeEnvelopes.Sample(lane.Points, cueTime));
    }
    /// <see cref="StopPreviewAsync"/> / a replacing preview cancels it mid-open.</summary>
    public Task<bool> PreviewCueAsync(
        string cueId, string? previewDeviceId = null, float gain = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _previewPlayer.PreviewCueAsync(cueId, previewDeviceId, gain);
    }

    /// <summary>Stops the current preview, if any (the GUI's <c>StopPreview</c>) - including one still opening
    /// (NXT-19). Does not raise <see cref="PreviewEnded"/>.</summary>
    public Task StopPreviewAsync() => _previewPlayer.StopPreviewAsync();

    // --- soundboard voices (task #10) --------------------------------------------------------------

    /// <summary>Fires a soundboard voice: opens <paramref name="mediaPath"/> as a fresh player on
    /// <paramref name="deviceId"/> (or the default) at <paramref name="volume"/> and tracks it under
    /// <paramref name="voiceId"/>. Polyphonic across ids; re-firing the same id replaces its voice (including a
    /// still-opening one). Raises <see cref="VoiceEnded"/> at the voice's natural end. The media open runs OFF
    /// the serial dispatcher (NXT-19) - a slow open never parks transport - and
    /// <see cref="StopVoiceAsync"/>/<see cref="StopAllVoicesAsync"/>/a re-fire/dispose preempt it; a preempted
    /// fire completes without error (the voice simply never started). (Loop is a later refinement.)</summary>
    public Task FireVoiceAsync(string voiceId, string mediaPath, string? deviceId = null, float volume = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.FireVoiceAsync(voiceId, mediaPath, deviceId, volume);
    }

    /// <summary>Stops one soundboard voice (no <see cref="VoiceEnded"/>).</summary>
    public Task StopVoiceAsync(string voiceId) => _voicePlayer.StopVoiceAsync(voiceId);

    /// <summary>Stops every soundboard voice (the GUI's StopAllSounds) - including any still opening (NXT-19).</summary>
    public Task StopAllVoicesAsync() => _voicePlayer.StopAllVoicesAsync();

    /// <summary>Live-sets a voice's output gain (linear). No-op when the voice isn't playing.</summary>
    public Task SetVoiceVolumeAsync(string voiceId, float volume) => _voicePlayer.SetVoiceVolumeAsync(voiceId, volume);

    /// <summary>Fades a voice's gain to silence over <paramref name="duration"/>, then stops it (the GUI's
    /// FadeOutSound). No <see cref="VoiceEnded"/>. A zero/negative duration stops immediately.</summary>
    public Task FadeVoiceAsync(string voiceId, TimeSpan duration) => _voicePlayer.FadeVoiceAsync(voiceId, duration);

    /// <summary>Whether a soundboard voice is currently playing (a lock-free view read - NXT-16 residue).</summary>
    public Task<bool> IsVoicePlayingAsync(string voiceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.IsVoicePlayingAsync(voiceId);
    }

    /// <summary>Per-voice playhead (id, position, duration) for every currently-playing soundboard voice - the
    /// source for the UI's per-tile progress/countdown (a lock-free view read - NXT-16 residue). Empty when
    /// nothing is playing.</summary>
    public Task<IReadOnlyList<VoiceProgress>> GetVoiceProgressAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(GetVoiceProgress());
    }

    /// <summary>Synchronous lock-free form for UI polling; avoids allocating a completed task per tick.</summary>
    public IReadOnlyList<VoiceProgress> GetVoiceProgress()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.GetVoiceProgress();
    }

    /// <summary>The clip specs for the next <paramref name="count"/> clip-bound cues after the last fired in
    /// <paramref name="groupId"/>. Reads cue/clip state, so call on the dispatcher.</summary>
    private List<ClipSpec> BuildUpcomingSpecs(string groupId, int count)
    {
        var group = GetOrAddGroup(groupId);
        var specs = new List<ClipSpec>();
        // Which cues come next is the cue layer's call; turning them into openable specs is the engine's.
        foreach (var cueId in _fires.UpcomingCueIds(groupId, DefaultGroup, GetGoCursor(groupId), count))
        {
            // A clip that opts OUT is skipped rather than counted and dropped: pre-roll's depth is
            // "how many decoders may be held", and spending one of them on a clip that declined would
            // shorten the warm for the cues that did want it.
            if (_clipsById.TryGetValue(cueId, out var binding) && !binding.DisablePreRoll)
                specs.Add(BuildClipSpec(binding));
        }

        return specs;
    }

    /// <summary>Pre-warms (opens + seeks-to-Start, holds ready) the next <paramref name="count"/> cues after
    /// the last fired in <paramref name="groupId"/> so the next GO arms instantly. Best-effort - a warm
    /// failure is swallowed and never affects transport. Awaitable for the UI/tests; <see cref="GoAsync"/>
    /// fires it without awaiting so the opens run in the background while the current cue plays. Only the
    /// cue/group state read marshals onto the dispatcher - the standby refresh (which OPENS media) runs OFF
    /// it (NXT-19/NXT-22): awaiting warm opens inside a dispatcher work item would park the loop for their
    /// whole duration (a blocked pre-roll open would freeze every transport command behind it).</summary>
    public async Task WarmUpcomingAsync(string groupId = DefaultGroup, int count = 2)
    {
        try
        {
            var specs = await InvokeAsync(() => Task.FromResult(BuildUpcomingSpecs(groupId, count)))
                .ConfigureAwait(false);
            if (specs.Count > 0)
                await _standby.RefreshStandbyAsync(
                        specs,
                        new ClipStandbyPolicy(MaxPreparedDecoders: count, Window: count))
                    .ConfigureAwait(false);
        }
        catch
        {
            // best-effort pre-roll - a failed warm just means the next GO opens on demand
        }
    }

    /// <summary>Resolves the N→M channel map for this clip's source→output route, or null for the source-derived default.</summary>
    private ChannelMap? ResolveOutputChannelMap(ShowClipBinding binding, string outputId)
    {
        var sourceId = binding.ClipId;
        foreach (var route in _routes)
            if (route.Enabled
                && string.Equals(route.SourceId, sourceId, StringComparison.Ordinal)
                && string.Equals(route.OutputId, outputId, StringComparison.Ordinal))
                return route.ToChannelMap();
        return null;
    }

    /// <summary>The audio outputs a group plays on: its declared <see cref="ShowAudioOutput"/>s, or a single
    /// implicit master (<see cref="MasterOutputId"/>) on the default device when the show declares none.</summary>
    private IReadOnlyList<ShowAudioOutput> ResolveGroupOutputs(string groupId)
    {
        var declared = _audioOutputs.Where(o => string.Equals(o.GroupId, groupId, StringComparison.Ordinal)).ToArray();
        return declared.Length > 0 ? declared : [new ShowAudioOutput(MasterOutputId, GroupId: groupId)];
    }




    /// <summary>Commits a voice as its group's Active, registers it on the level/stop bus as its OWN program
    /// source, and republishes the query view so a position/state poll sees the new run-state without waiting
    /// behind the dispatcher. One registration per VOICE (not per group) is what gives the master fader,
    /// stop-all and Panic the same reach over a crossfade tail as over the clip transport points at.</summary>
    private async ValueTask CommitVoiceAsync(
        string groupId, TransportGroup group, TransportVoice voice,
        (TimeSpan Duration, FadeShape Curve)? crossfade)
    {
        await group.ActivateAsync(voice, crossfade).ConfigureAwait(false);
        voice.SoundingId = _sounding.RegisterProgram(
            // Per-VOICE label, not per cue: a loop crossfade (and any same-cue re-fire with a window) has TWO
            // voices of one cue on the bus for the overlap, so "cue:<group>:<cue>" alone put two identical
            // labels on it - which silently broke the duplicate-label check that is how a registration
            // lingering past its player is meant to fail loudly. The counter keeps it readable for the status
            // surface (cue:main:c#7) while making it unique for the lifetime of the session.
            $"cue:{groupId}:{voice.Clip.Spec.Id}#{++_soundingSequence}",
            // Operator-facing subject: the CUE, so a failed stop can be named ("Cue 3 - Walk-in music")
            // instead of showing a bus label the operator has never seen.
            subjectId: voice.Clip.Spec.Id,
            // A registered voice is sounding by definition - it leaves the bus as it retires, so there is no
            // idle transport entry left to filter (and no idle group to keep a stale trim in sync for).
            isSounding: () => voice.State != VoiceState.Retired,
            level: () => voice.EffectiveAudioLevel,
            applyMasterTrim: voice.ApplyMasterTrim,
            // Self-filtering stop hook (B): a voice that retired between the enumeration and the stop selects
            // nothing, so the stop returns without claiming or ramping a dead player. Per-voice claims are
            // also what make two concurrent stop-alls safe - one wins the ramp, both releases are idempotent.
            stop: request => StopVoicesCoreAsync(
                () => voice.State != VoiceState.Retired ? [(group, voice)] : [],
                request.Fade, request.FadeDuration, request.Curve));
        PublishGroupViews();
    }

    /// <summary>Releases a group's active voice (leaving any tail on its own ramp) and republishes the query
    /// view. The natural end, a fade cue's silent stop and the stop path all funnel through here.</summary>
    private async ValueTask ReleaseActiveVoiceAsync(TransportGroup group)
    {
        await group.ReleaseActiveVoiceAsync().ConfigureAwait(false);
        PublishGroupViews();
    }

    /// <summary>Releases ONE voice of a group, whichever state it is in, and republishes the query view.</summary>
    private async ValueTask ReleaseVoiceAsync(TransportGroup group, TransportVoice voice)
    {
        await group.ReleaseVoiceAsync(voice).ConfigureAwait(false);
        PublishGroupViews();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _fires.CancelAllFires(); // unblock the dispatcher so disposal isn't stuck behind a long in-flight fire (NXT-03)
        _completionMonitor.Stop();

        if (_dispatcher.IsOnDispatcherThread)
        {
            await DisposeStateAsync().ConfigureAwait(false);
            _dispatcher.Dispose();
            return;
        }

        await _completionMonitor.Completion.ConfigureAwait(false);

        try
        {
            await _dispatcher.InvokeAsync(DisposeStateAsync).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeStateAsync()
    {
        _metadataPublisher.Dispose();
        await _previewPlayer.ReleaseAllAsync().ConfigureAwait(false);
        await _voicePlayer.ReleaseAllAsync().ConfigureAwait(false);
        await DisposeGroupsAsync().ConfigureAwait(false);
        _testPatternSlots.Clear(); // slots are owned by their compositions (disposed below); drop stale refs
        _visualizers.Clear();
        // Generic (non-visualizer) taps are caller-owned, but their cached rate adapters are session-owned.
        foreach (var tap in _audioTaps.ToArray())
            RemoveAudioTap(tap);
        foreach (var composition in _compositions.Values)
            composition.Dispose();
        _compositions.Clear();
        // The audition canvas outlives document loads by design, so nothing else has retired it by now.
        _auditionComposition?.Dispose();
        _auditionComposition = null;
        PublishCompositionsView(); // drop the health-poll view's references to the retired compositions
        await _standby.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Reload-time slot cleanup that SPARES the preserved compositions (see
    /// <see cref="ShowSessionVisualizerService.RetainForPreservedCompositionsOnly"/> for the visualizer
    /// semantics). Test-pattern slots belonging to compositions about to be disposed are dropped here.</summary>
    private List<ShowSessionVisualizerService.Reattachment> RetainSlotsForPreservedCompositionsOnly(
        HashSet<string> preservedIds,
        IReadOnlyDictionary<string, ClipCompositionRuntime> replacementCompositions)
    {
        // Test-pattern slots: drop refs only for compositions that are going away.
        if (preservedIds.Count == 0)
        {
            _testPatternSlots.Clear();
        }
        else
        {
            foreach (var id in _testPatternSlots.Keys.Where(k => !preservedIds.Contains(k)).ToList())
                _testPatternSlots.Remove(id);
        }

        return _visualizers.RetainForPreservedCompositionsOnly(preservedIds, replacementCompositions);
    }

    private void ReattachPersistentVisualizers(
        IReadOnlyList<ShowSessionVisualizerService.Reattachment> reattachments) =>
        _visualizers.ReattachPersistent(reattachments);

}

/// <summary>An active clip's live audio levels (<see cref="ShowSession.GetClipAudioLevelsAsync"/>):
/// the persistent fade level (what fade cues compose from), the cue-owned volume-envelope factor,
/// the controller/group modifier, and the effective level the route gains are actually written with -
/// fade × envelope × modifier × the session-wide <see cref="ShowSession.MasterTrim"/>.</summary>
public sealed record ClipAudioLevels(
    float FadeLevel,
    float EnvelopeLevel,
    float ModifierLevel,
    float EffectiveLevel);

/// <summary>A mid-show audio-path failure surfaced by <see cref="ShowSession.PlaybackAlert"/>: the cue
/// whose clip hit it, the router output id when one specific output errored (null = the clip's whole
/// audio router faulted, i.e. its pacing clock/primary output died), and the underlying exception.</summary>
public sealed record ShowPlaybackAlert(string CueId, string? OutputId, string Message, Exception Exception);
