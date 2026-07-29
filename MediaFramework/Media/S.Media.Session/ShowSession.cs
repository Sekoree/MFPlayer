using S.Media.Compositor;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.Routing;
using S.Media.Time;

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
}

/// <summary>A soundboard voice's playhead - for the UI's per-tile progress/countdown.</summary>
public readonly record struct VoiceProgress(string VoiceId, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// The headless home of a show (D5): cues, clips, and transport groups behind an internal dispatcher.
/// Commands marshal onto the session thread and queries return immutable snapshots - the API never assumes
/// a UI thread (the <c>ui_thread_observable_property_sets</c> bug class is structurally impossible here).
/// One <see cref="SessionClock"/> per transport group (D4); clips open through <see cref="IMediaRegistry"/>
/// (D6); when an <see cref="IAudioBackend"/> is supplied, each group plays on a master output (D11).
/// </summary>
public sealed class ShowSession : IAsyncDisposable
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
    private volatile CueGraph _cueGraph = new();
    private readonly Dictionary<string, TransportGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime> _compositions = new(StringComparer.Ordinal);
    // Lock-free view of the compositions for the UI health poll: republished (on the dispatcher) whenever
    // _compositions changes, so GetCompositionStats can read it - and the runtime's own thread-safe GetStats -
    // off any thread without marshaling (mirrors _groupViews / SnapshotAsync).
    private volatile IReadOnlyDictionary<string, ClipCompositionRuntime> _compositionsView =
        new Dictionary<string, ClipCompositionRuntime>(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime.LayerSlot> _testPatternSlots = new(StringComparer.Ordinal);
    private IReadOnlyList<OutputPatchRoute> _routes = [];
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
    // Opens + warms clips (seek-to-Start trim-in, standby pre-roll). Clips arm through here instead of a
    // direct MediaGraph build so the show can pre-roll upcoming cues (8b convergence). All access is on the
    // serial dispatcher; the engine is also internally thread-safe.
    private readonly ClipStandbyEngine _standby = new();
    // Cue id → its clip binding (built on load) so the standby pre-roll can look up upcoming cues' media.
    private IReadOnlyDictionary<string, ShowClipBinding> _clipsByCue =
        new Dictionary<string, ShowClipBinding>(StringComparer.Ordinal);
    // Soundboard voices + the cue preview - playback outside the transport groups, split along its ownership
    // seam (review Part-5 #2). Owns the voice/preview registries and monitors; this session's public
    // voice/preview API delegates to it.
    private readonly VoicePlayer _voicePlayer;
    private readonly ShowSessionMetadataPublisher _metadataPublisher;
    private readonly SessionCompletionMonitor _completionMonitor;
    // The level/stop bus: every sounding source registers with its program/monitoring classification, and
    // master trim, stop-all and Panic all drive its ONE enumeration. Dispatcher-confined like _groups.
    private readonly SoundingSourceRegistry _sounding = new();
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

    /// <summary>A composition layer the active clip's video is fanned to, tagged by its composition + layer index
    /// so a live placement edit can target the right one when a clip is placed onto more than one layer.</summary>
    private readonly record struct PlacedLayer(
        string CompositionId, int LayerIndex, ClipCompositionRuntime.IPlacedClipLayer Slot);

    // Fire sequencing (NXT-03): the fire-lock + in-flight fire cancellation live on the orchestrator (split
    // along its ownership seam - review Part-5 #2); this session's public fire/GO API delegates to it.
    // _showGeneration is bumped on every load so a fire whose open straddled a reload discards its (now-stale)
    // clip at commit instead of corrupting the newer show.
    private readonly CueFireOrchestrator _fires;
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
        _metadataPublisher = new ShowSessionMetadataPublisher(MetadataHub, metadataProbe);
        _standby.StandbyStatesChanged += states => PreparedCuesChanged?.Invoke(states);

        _visualizers = new ShowSessionVisualizerService(
            RegisterVisualizerTap, RemoveTapFromActiveClips, ReleaseVisualizerTapRegistration, MetadataHub);
        _fires = new CueFireOrchestrator(this);
        _voicePlayer = new VoicePlayer(this, _standby, audioBackend, ResolveFallbackOutputDeviceId, BuildPreviewSpec, BuildVoiceSpec);
        _voicePlayer.VoiceEnded += id => VoiceEnded?.Invoke(id);
        _voicePlayer.PreviewEnded += id => PreviewEnded?.Invoke(id);
        _completionMonitor = new SessionCompletionMonitor(
            EndMonitorPollInterval, PollCompletionWorkFromBackgroundAsync);
    }

    // --- effect buses & metadata (Phase 4) -----------------------------------

    // Session-registered audio taps (visualizers, meters): attached as an extra router output+route on
    // every clip that fires while registered. Dispatcher-confined list; the tap objects themselves are
    // thread-safe outputs (their Submit runs on the router pump thread).
    private readonly List<AudioTapRegistration> _audioTaps = [];

    /// <summary>An audio tap fed by fired clips. <see cref="Filter"/> (cue id → bool) makes the feed
    /// selective. Fixed-rate taps cache one borrowed-inner adapter per clip/router format.</summary>
    private sealed class AudioTapRegistration(
        Guid id, IAudioOutput tap, float gain, Func<string, bool>? filter = null)
    {
        private readonly Dictionary<AudioFormat, IAudioOutput> _adaptedOutputs = [];

        public Guid Id { get; } = id;
        public IAudioOutput Tap { get; } = tap;
        public float Gain { get; } = gain;
        public Func<string, bool>? Filter { get; } = filter;

        /// <summary>This tap's entry on the session's level/stop bus. A tap is an ANALYSIS feed - a
        /// visualizer's audio face, a meter - never program output, so it registers as monitoring: the
        /// master fader must not duck a visualizer's reaction and Panic must not silence a meter.</summary>
        public Guid SoundingId { get; set; }

        public IAudioOutput ResolveForRouter(IMediaRegistry registry, int sampleRate)
        {
            var routerFormat = new AudioFormat(sampleRate, Tap.Format.Channels);
            if (routerFormat == Tap.Format)
                return Tap;
            if (_adaptedOutputs.TryGetValue(routerFormat, out var existing))
                return existing;

            var adapted = registry.CreateResamplingOutput(Tap, routerFormat)
                ?? throw new InvalidOperationException(
                    $"no audio output resampler is registered for {sampleRate} Hz → {Tap.Format.SampleRate} Hz");
            if (adapted.Format != routerFormat)
            {
                (adapted as IDisposable)?.Dispose();
                throw new InvalidOperationException(
                    $"audio output resampler advertised {adapted.Format}, expected router format {routerFormat}");
            }

            _adaptedOutputs.Add(routerFormat, adapted);
            return adapted;
        }

        public void DisposeAdapters()
        {
            foreach (var adapter in _adaptedOutputs.Values)
                if (adapter is IDisposable disposable)
                    MediaDiagnostics.SwallowDisposeErrors(disposable.Dispose, "ShowSession: audio tap resampler");
            _adaptedOutputs.Clear();
        }
    }

    /// <summary>The session's metadata blackboard: item metadata published on every clip fire, frame
    /// stats published by probe effects. Attach visualizer/effect sinks here.</summary>
    public S.Media.Core.Buses.BusMetadataHub MetadataHub { get; } = new();

    // Visualizer slot lifecycle (attach/fade/preserve/reattach) - dispatcher-confined service (P2-6).
    private readonly ShowSessionVisualizerService _visualizers;

    /// <summary>
    /// Registers an audio tap (e.g. a visualizer's audio face): from now on every fired clip's audio
    /// router feeds it a stereo-mapped copy of the clip's source, alongside the real outputs. The tap
    /// is automatically adapted when its fixed sample rate differs from the clip's native mix rate.
    /// Takes effect for clips fired AFTER registration.
    /// </summary>
    public Task<Guid> RegisterAudioTapAsync(IAudioOutput tap, float gain = 1f)
    {
        ArgumentNullException.ThrowIfNull(tap);
        var id = Guid.NewGuid();
        return InvokeAsync(() =>
        {
            AddAudioTap(new AudioTapRegistration(id, tap, gain));
            return Task.FromResult(id);
        });
    }

    /// <summary>Unregisters a tap immediately: active routes are detached and session-owned sample-rate
    /// adapters are disposed. The caller still owns the original tap.</summary>
    public Task UnregisterAudioTapAsync(Guid id) =>
        InvokeAsync(() =>
        {
            if (_audioTaps.FirstOrDefault(t => t.Id == id) is { } tap)
            {
                RemoveTapFromActiveClips(id);
                RemoveAudioTap(tap);
            }
            return Task.CompletedTask;
        });

    /// <summary>Adds a tap to the tap list AND to the level/stop bus as monitoring (dispatcher).</summary>
    private void AddAudioTap(AudioTapRegistration tap)
    {
        _audioTaps.Add(tap);
        tap.SoundingId = _sounding.RegisterMonitoring(
            $"tap:{tap.Id:N}", () => _audioTaps.Contains(tap), () => tap.Gain);
    }

    /// <summary>Retires a tap from the bus, the tap list and its session-owned rate adapters (dispatcher).</summary>
    private void RemoveAudioTap(AudioTapRegistration tap)
    {
        _sounding.Unregister(tap.SoundingId);
        _audioTaps.Remove(tap);
        tap.DisposeAdapters();
    }

    /// <summary>
    /// Attaches (or, with null, removes) a visualizer on a composition: the source's GL layer surface
    /// renders as a held full-canvas layer just under the test-pattern layer, its audio face registers
    /// as a session tap (fed by the CURRENT clip immediately and every later fire), and - when the
    /// source is a metadata sink - it joins <see cref="MetadataHub"/>. Returns false when the
    /// composition doesn't exist or its compositor can't host GL surfaces (CPU fallback).
    /// <paramref name="disposeSourceOnRemove"/>: true (default) = the session owns the source's disposal
    /// on replace/remove/reload; false = the caller owns its lifetime.
    /// <paramref name="preserveAcrossDocumentReload"/> recreates only the surface when the composition
    /// itself must be rebuilt, retaining the source, preset state, audio tap/filter and metadata feed.
    /// </summary>
    /// <param name="audioFeedFilter">Cue-id filter for the visualizer's audio feed (null = every
    /// clip): a visualizer cue can listen only to selected media cues (#26 routing).</param>
    /// <param name="placements">Multiple sections of the canvas showing the same source (#26
    /// multi-placement). When non-empty this supersedes <paramref name="placement"/>; one surface layer
    /// is created per entry and they share the slot's audio tap and lifetime.</param>
    public Task<bool> SetCompositionVisualizerAsync(
        string compositionId, S.Media.Core.Buses.IAudioVisualSource? source, bool disposeSourceOnRemove = true,
        VideoPlacementSpec? placement = null, Func<string, bool>? audioFeedFilter = null,
        bool preserveAcrossDocumentReload = false, IReadOnlyList<VideoPlacementSpec>? placements = null) =>
        InvokeAsync(() =>
        {
            if (source is null)
            {
                _visualizers.Remove(compositionId);
                return Task.FromResult(true);
            }

            if (!_compositions.TryGetValue(compositionId, out var composition)
                || !composition.SupportsSurfaceLayers
                || source is not Compositor.ILayerSurfaceVideoSource surfaceSource)
            {
                return Task.FromResult(false);
            }

            // Surface layers render above frame-backed content (so cover art remains below the
            // visualizer) and order among themselves by LayerIndex. A caller-provided
            // placement (#26: visualizer-as-a-cue) renders the visualizer into a SECTION of the canvas
            // (dest rect/opacity, same semantics as media placements); default = full-canvas stretch.
            IReadOnlyList<VideoPlacementSpec> resolvedPlacements = placements is { Count: > 0 }
                ? placements
                : [placement ?? new VideoPlacementSpec(compositionId, int.MaxValue - 1, Placement: "stretch")];

            _visualizers.Attach(
                compositionId, composition, surfaceSource, source, resolvedPlacements,
                disposeSourceOnRemove, audioFeedFilter, preserveAcrossDocumentReload);
            return Task.FromResult(true);
        });

    /// <summary>True when <paramref name="compositionId"/> currently has a visualizer attached. A caller that
    /// reloads with <c>preserveMatchingCompositions</c> uses this to tell whether its persistent visualizer
    /// carried over (preserved) or was dropped (composition rebuilt) so it can decide whether to re-attach.</summary>
    public Task<bool> HasCompositionVisualizerAsync(string compositionId) =>
        InvokeAsync(() => Task.FromResult(_visualizers.Has(compositionId)));

    /// <summary>Hot-updates one running visualizer surface's destination/opacity/crop/rotation without
    /// replacing its projectM source. <paramref name="placementIndex"/> addresses the layer within the
    /// composition's visualizer slot in the order the placements were attached (#26 multi-placement;
    /// 0 = the only layer for single-placement attaches). Returns false when the composition has no
    /// attached visualizer or the index is out of range.</summary>
    public Task<bool> UpdateCompositionVisualizerPlacementAsync(
        string compositionId, VideoPlacementSpec placement, int placementIndex = 0) =>
        InvokeAsync(() => Task.FromResult(_visualizers.UpdatePlacement(compositionId, placement, placementIndex)));

    /// <summary>Creates + registers a visualizer's audio tap and feeds it from already-playing clips
    /// (dispatcher). The service owns WHEN this happens; the tap list itself stays session-owned.</summary>
    private Guid RegisterVisualizerTap(S.Media.Core.Buses.IAudioVisualSource source, Func<string, bool>? audioFeedFilter)
    {
        var tapId = Guid.NewGuid();
        var tap = new AudioTapRegistration(tapId, source, 1f, audioFeedFilter);
        AddAudioTap(tap);
        AttachTapToActiveClips(tap);
        return tapId;
    }

    /// <summary>Removes a visualizer tap's registration and disposes its cached rate adapters
    /// (dispatcher). Active-clip detachment is a separate step (RemoveTapFromActiveClips).</summary>
    private void ReleaseVisualizerTapRegistration(Guid tapId)
    {
        if (_audioTaps.FirstOrDefault(t => t.Id == tapId) is { } tap)
            RemoveAudioTap(tap);
    }

    /// <summary>Feeds a newly-registered tap from the clips that are ALREADY playing (dispatcher).</summary>
    private void AttachTapToActiveClips(AudioTapRegistration tap)
    {
        foreach (var voice in _groups.Values.SelectMany(group => group.Voices))
        {
            if (voice.Player is not { AudioRouter: not null, AudioSourceId: not null } player)
                continue;
            if (tap.Filter is not null && !tap.Filter(voice.Binding.CueId))
                continue; // selective feed: this clip is not in the tap's listen set
            try
            {
                var output = tap.ResolveForRouter(_registry, player.AudioRouter!.SampleRate);
                player.AttachAudioOutput(output, $"tap-{tap.Id:N}", map: null, gain: tap.Gain);
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning("ShowSession: visualizer tap could not attach to a playing clip ({0}).", ex.Message);
            }
        }
    }

    private void RemoveTapFromActiveClips(Guid tapId)
    {
        foreach (var voice in _groups.Values.SelectMany(group => group.Voices))
        {
            if (voice.Player.AudioRouter is not { } router)
                continue;
            try
            {
                router.RemoveOutput($"tap-{tapId:N}");
            }
            catch
            {
                // the clip may have re-fired without the tap - nothing to remove
            }
        }
    }

    /// <summary>Attaches the registered taps to a freshly-committed clip's router (dispatcher).</summary>
    private void AttachAudioTaps(S.Media.Players.MediaPlayer player, string? cueId = null)
    {
        if (_audioTaps.Count == 0 || player.AudioRouter is null || player.AudioSourceId is null)
            return; // no audio side (video-only clip) - nothing to tap
        foreach (var tap in _audioTaps)
        {
            if (tap.Filter is not null && cueId is not null && !tap.Filter(cueId))
                continue; // selective feed (#26): this clip is not in the tap's listen set
            try
            {
                var output = tap.ResolveForRouter(_registry, player.AudioRouter.SampleRate);
                player.AttachAudioOutput(output, $"tap-{tap.Id:N}", map: null, gain: tap.Gain);
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning(
                    "ShowSession: audio tap {0} could not attach ({1}); the clip plays without it.", tap.Id, ex.Message);
            }
        }
    }

    // Routers already wired for alert forwarding: a router lives and dies with its clip's player, so
    // entries expire with the router; the table only guards against double-subscribing the same
    // router if one armed clip ever commits twice.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _alertWiredRouters = new();

    /// <summary>Forwards a freshly-committed clip's audio-router failure events to
    /// <see cref="PlaybackAlert"/> (review §2.3: the backends latch callback faults and the pumps raise
    /// <c>OutputErrored</c>, but without a session-level subscriber the app saw silence and a frozen
    /// clock with no error anywhere). Handlers fire on audio/pump threads; the subscription lifetime is
    /// the router's own (it is disposed with the player when the clip releases).</summary>
    private void WireRouterAlerts(S.Media.Players.MediaPlayer player, string cueId)
    {
        if (player.AudioRouter is not { } router)
            return;
        if (!_alertWiredRouters.TryAdd(router, router))
            return; // already forwarding for this router
        router.OutputErrored += (_, e) => RaisePlaybackAlert(new ShowPlaybackAlert(
            cueId, e.OutputId,
            $"audio output '{e.OutputId}' failed mid-clip: {e.Exception.Message}", e.Exception));
        router.Faulted += (_, e) => RaisePlaybackAlert(new ShowPlaybackAlert(
            cueId, OutputId: null,
            $"audio router faulted mid-clip (pacing output lost?): {e.Exception.Message}", e.Exception));
    }

    private void RaisePlaybackAlert(ShowPlaybackAlert alert)
    {
        try
        {
            PlaybackAlert?.Invoke(alert);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "ShowSession.PlaybackAlert handler threw");
        }
    }

    /// <summary>Publishes what's playing to <see cref="MetadataHub"/>: an immediate filename-derived
    /// entry, refined by the host's metadata probe (tags + cover art) off the dispatcher when set.</summary>
    private void PublishItemMetadata(ShowClipBinding binding) =>
        _metadataPublisher.Publish(binding.MediaPath, binding.CueId);

    /// <summary>The preview instance's spec for a loaded cue (a variant key distinct from the GO-prepared
    /// clip so an audition never consumes it), or null when the cue has no clip binding. Dispatcher-read.</summary>
    private ClipSpec? BuildPreviewSpec(string cueId) =>
        _clipsByCue.TryGetValue(cueId, out var binding) ? BuildClipSpec(binding, "preview") : null;

    /// <summary>A soundboard voice's spec: a fresh unbounded file open at the target device's backend rate
    /// (JACK graphs reject the media's own rate - the same resolution every clip spec gets). Dispatcher-read.</summary>
    private ClipSpec BuildVoiceSpec(string outputId, string mediaPath, string? deviceId)
    {
        // Null deviceId → the cache's own default-else-first resolution (same rule as the fallback device),
        // evaluated fresh instead of a construction-time snapshot.
        var targetAudioRate = ResolveBackendSampleRate(deviceId);
        return new ClipSpec(
            outputId,
            ClipMediaSource.File(
                _registry,
                mediaPath,
                S.Media.Players.MediaPlayerOpenOptions.Default with
                {
                    TargetAudioSampleRate = targetAudioRate,
                }),
            S.Media.Core.ClipWindow.Unbounded,
            $"{outputId}|rate:{targetAudioRate?.ToString() ?? "source"}");
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
    public Task LoadDocumentAsync(ShowDocument document, bool preserveMatchingCompositions = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        _fires.CancelActiveFire(); // a reload must not wait behind a long in-flight fire (NXT-03)
        return InvokeAsync(() => LoadDocumentCoreAsync(document, preserveMatchingCompositions));
    }

    private async Task LoadDocumentCoreAsync(ShowDocument document, bool preserveMatchingCompositions = false)
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
        var preservedIds = new HashSet<string>(StringComparer.Ordinal);
        if (preserveMatchingCompositions)
        {
            foreach (var comp in document.Compositions)
            {
                if (_compositions.TryGetValue(comp.Id, out var live)
                    && live.CanvasFormat.Width == comp.Width
                    && live.CanvasFormat.Height == comp.Height
                    && live.CanvasFormat.FrameRate.Numerator == comp.FrameRateNum
                    && live.CanvasFormat.FrameRate.Denominator == comp.FrameRateDen)
                {
                    preservedIds.Add(comp.Id);
                }
            }
        }

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

        var newClipsByCue = document.Clips.ToDictionary(c => c.CueId, StringComparer.Ordinal);
        var newCueGraph = new CueGraph();
        foreach (var cue in document.Cues.OrderBy(c => c.Number))
        {
            var groupId = cue.GroupId ?? DefaultGroup;
            var binding = newClipsByCue.GetValueOrDefault(cue.Id);
            // A cue without a clip currently has no executable session action. Do not report it as Fired: that
            // produced a successful no-op and made HaPlay briefly mark a stale/unbound media cue as playing.
            // Future control/stop cues need their own action binding rather than relying on an empty clip.
            newCueGraph.AddCue(
                cue,
                ct => PlayClipAsync(
                    groupId, binding, ct, waitForStartBarrier: null, crossfade: TakePendingFireCrossfade()),
                binding is null ? static () => false : null);
        }

        // Commit (atomic on the dispatcher): retire the running show, then swap in the staged graph. Nothing
        // below can fail, so the swap can't leave a half-built replacement.
        // Disposing the groups tears down the outgoing clips - this also disposes their layer slots, so a
        // PRESERVED composition is left with only its persistent surface layers (e.g. the visualizer).
        await DisposeGroupsAsync().ConfigureAwait(false);

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

        _cueGraph = newCueGraph;
        _clipsByCue = newClipsByCue;
        _routes = document.Routes;
        _audioOutputs = document.AudioOutputs;
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
        (TimeSpan Duration, FadeCurve Curve)? crossfade = null)
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
            // fan onto several - the group + generation are all the pre-open setup needs.
            return Task.FromResult((generation, group: grp));
        }).ConfigureAwait(false);

        // --- OPEN (OFF the dispatcher): arm the clip through the standby engine - it opens via the registry
        // (auto-wiring adaptive-rate drift correction) and seeks to the trim-in (Window.Start), reusing a warm
        // prepared clip when present. The long part; the dispatcher loop stays free throughout (NXT-03).
        var armed = await _standby.ArmAsync(BuildClipSpec(binding), cancellationToken).ConfigureAwait(false);

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

        // --- COMMIT (on the dispatcher): swap the armed clip in, or discard it if the show was reloaded / the
        // fire was cancelled (STOP) during the open (NXT-03).
        await InvokeAsync(() => CommitClipAsync(groupId, binding, cancellationToken, setup, armed, crossfade))
            .ConfigureAwait(false);
    }

    /// <summary>Test-only fault seam (§A): raised on the dispatcher immediately AFTER a clip has committed to
    /// its group - i.e. after a crossfade handoff has moved the displaced voice into its release. The
    /// orphaned-tail case is only reachable by faulting exactly there, so the seam is deliberately one
    /// narrow point rather than a general hook. Null in production.</summary>
    internal Action<string>? PostCommitFault { get; set; }

    /// <summary>The on-dispatcher commit half of <see cref="PlayClipAsync"/> (NXT-03): attaches the freshly-armed
    /// clip's outputs, masters the composition, and swaps it in - unless the show was reloaded (generation moved)
    /// or the fire was cancelled while the clip opened off the dispatcher, in which case it discards the now-stale
    /// clip without touching the live show.</summary>
    private async Task CommitClipAsync(
        string groupId,
        ShowClipBinding binding,
        CancellationToken cancellationToken,
        (int generation, TransportGroup group) setup,
        IArmedClip armed,
        (TimeSpan Duration, FadeCurve Curve)? crossfade = null)
    {
        if (cancellationToken.IsCancellationRequested || _showGeneration != setup.generation || _disposed)
        {
            await armed.ReleaseAsync().ConfigureAwait(false);
            return;
        }

        var group = setup.group;
        // A crossfade only means something when it actually replaces a live clip: a fire onto an idle
        // group (or a non-positive window) is the plain butt-splice path, unchanged. Resolved here on
        // the dispatcher - the incoming clip has already opened, so an open failure never reaches this
        // point and the old Active stays untouched (design doc: open failure = no fade).
        if (crossfade is not { Duration.Ticks: > 0 } || group.ActiveVoice is null)
            crossfade = null;
        var player = armed.Player;
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
        var fadeInCurve = binding.FadeIn > TimeSpan.Zero ? binding.FadeInCurve : crossfade?.Curve ?? FadeCurve.Linear;
        // Retained for the active clip so both fade-in and every stop path ramp each route relative to its
        // configured gain rather than assuming unity.
        var routeTargets = new List<AudioRouteTarget>();
        // Device-tagged routed outputs (OutputId → device) for the per-line audio-health poll.
        var audioPumps = new List<(string OutputId, string DeviceId)>();
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
                if (player.VideoSource is ILayerSurfaceVideoSource surfaceSource
                    && placements.Count == 1
                    && _compositions.TryGetValue(placements[0].CompositionId, out var surfaceComp)
                    && surfaceComp.SupportsSurfaceLayers)
                {
                    var placement = placements[0];
                    var surfaceSlot = surfaceComp.AddSurfaceLayer(
                        surfaceSource.CreateLayerSurface(),
                        BuildVideoPlacementSpec(placement.CompositionId, placement.LayerIndex, placement.Placement));
                    layers.Add(new PlacedLayer(placement.CompositionId, placement.LayerIndex, surfaceSlot));
                    timelineClaims.Add(surfaceComp.AcquireTransportTimeline(group.Timeline));
                    MediaDiagnostics.LogInformation(
                        "clip {CueId}: video composites as a GPU layer surface on {Composition} (NXT-10)",
                        binding.CueId, placement.CompositionId);
                }
                else
                {
                    foreach (var placement in placements)
                    {
                        if (!_compositions.TryGetValue(placement.CompositionId, out var comp))
                            continue;
                        var slot = comp.AddLayer(
                            videoSource.Format,
                            BuildVideoPlacementSpec(placement.CompositionId, placement.LayerIndex, placement.Placement));
                        layers.Add(new PlacedLayer(placement.CompositionId, placement.LayerIndex, slot));
                        player.AttachVideoOutput(slot.Output, id: $"comp{fanoutIndex++}"); // unique id ⇒ router fans out
                        if (mastered.Add(placement.CompositionId))
                            timelineClaims.Add(comp.AcquireTransportTimeline(group.Timeline));
                    }
                }
            }

            // Video half of a fade-in (and of a crossfade's incoming voice): like the audio routes attach
            // silent (gain 0) below, the layers attach BLACK (opacity 0) so no full-opacity frame can
            // composite before the ramp's first step - StartFadeIn lifts them to the authored opacities,
            // which are preserved as the group's BaseLayerOpacities anchor (the commit capture would
            // otherwise record the zeroed values and break fade cues' upward ramps).
            IReadOnlyList<float>? authoredLayerOpacities = null;
            if (fadeIn && layers.Count > 0)
            {
                authoredLayerOpacities = layers.Select(placed => placed.Slot.Opacity).ToArray();
                foreach (var placed in layers)
                    placed.Slot.Opacity = 0f;
            }

            if (_audioBackend is not null && player.AudioRouter is not null)
            {
                var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                if (binding.AudioRoutes is { } clipRoutes)
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
                                gain: fadeIn ? 0f : route.Gain, outputs, route))
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
                                gain: fadeIn ? 0f : 1f, outputs))
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
                            overlay, group.Timeline, nextLayerIndex++));
                    }
                }
            }

            // Effect buses (Phase 4): registered audio taps ride along on every fired clip, and the
            // metadata hub learns what's playing (visualizers/overlays pick both up).
            AttachAudioTaps(player, binding.CueId);
            PublishItemMetadata(binding);
            WireRouterAlerts(player, binding.CueId);

            armed.Start();
            // Commit: the displaced voice moves to its release (ramp armed inside the handoff) or is
            // butt-spliced away, and this voice becomes the group's Active.
            await CommitVoiceAsync(groupId, group, voice, crossfade).ConfigureAwait(false);
            PostCommitFault?.Invoke(groupId);
            voice.SetFadeMetadata(routeTargets, fadeIn ? 0f : 1f, authoredLayerOpacities);
            // Master trim: routes attach at the full authored gain, so a clip fired while the
            // session-wide trim is below unity needs one ApplyAudioScale pass to fold the trim in
            // (with a fade-in the ramp writes through the same path every step anyway).
            if (voice.Level.Master != 1f)
                voice.ApplyAudioScale(voice.RouteTargets, voice.ClipLevel);
            // Publish the device-tagged audio outputs for the line-health poll (CommitVoiceAsync republished
            // the group views before these were set, so refresh once more now they're known).
            voice.SetAudioPumps(audioPumps);
            PublishGroupViews();

            // Background per-clip work - the fade-in ramp + the end-of-clip (loop/trim-out/freeze) monitor -
            // shares one cancellation, cancelled when the voice leaves the transport. Both gated, so a plain
            // play-to-end cue with no fade starts nothing. End handling needs a known duration (live = 0).
            var end = player.Duration - binding.EndOffset;
            var endHandling = (binding.Loop || binding.EndBehavior != ClipEndBehavior.Stop
                               || binding.EndOffset > TimeSpan.Zero || binding.FadeOut > TimeSpan.Zero
                               || binding.EndAtDuration || binding.NotifyNaturalEnd
                               || binding.PreEndNotify > TimeSpan.Zero)
                && player.Duration > TimeSpan.Zero
                && end > binding.StartOffset;
            var hasEnvelope = binding.VolumeEnvelope is { Count: > 0 } && routeTargets.Count > 0;
            if (fadeIn || endHandling || hasEnvelope)
            {
                var clipCts = new CancellationTokenSource();
                voice.SetClipWorkCts(clipCts);
                if (fadeIn && (routeTargets.Count > 0 || layers.Count > 0))
                    StartFadeIn(
                        groupId, voice, routeTargets, fadeInDuration, fadeInCurve,
                        fadesVideo: layers.Count > 0, clipCts.Token);
                if (hasEnvelope)
                    StartEnvelopeRunner(groupId, voice, binding.VolumeEnvelope!, clipCts.Token);
                if (endHandling)
                    StartEndMonitor(groupId, voice, end, clipCts.Token);
            }
        }
        catch
        {
            // One teardown for both halves of the commit. An ARMING voice was never handed to the group, so
            // it releases on its own; a voice that already committed goes back through the group, which
            // clears the transport binding and drops its bus registration - and leaves any tail it displaced
            // to finish on the ramp the handoff armed.
            if (voice.State == VoiceState.Arming)
                await voice.ReleaseAsync().ConfigureAwait(false);
            else
                await ReleaseVoiceAsync(group, voice).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Builds the standby <see cref="ClipSpec"/> for a clip binding - identical on the pre-roll
    /// (prepare) and fire (arm) paths, so a warmed clip is found by its key (cue id + media path).</summary>
    private ClipSpec BuildClipSpec(ShowClipBinding binding, string? variant = null)
    {
        var targetAudioRate = binding.AudioRoutes switch
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
        var window = binding.StartOffset > TimeSpan.Zero
            ? new S.Media.Core.ClipWindow(binding.StartOffset, TimeSpan.Zero, TimeSpan.Zero, HasKnownEnd: false)
            : S.Media.Core.ClipWindow.Unbounded;
        // A non-null variant (e.g. "preview") gives a distinct standby key so this arms a FRESH instance
        // instead of consuming GO's prepared clip.
        return new ClipSpec(
            variant is null ? binding.CueId : $"{binding.CueId}:{variant}",
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
                ColorAdjust: p.ColorAdjust);

    /// <summary>Live-edit the active cue's composition placement while it plays (the GUI's
    /// <c>UpdateActiveCueVideoPlacement</c>) - repositions / re-opacities its layer. Returns false when the
    /// cue isn't the active clip on any group (or has no composition layer).</summary>
    public Task<bool> UpdateActivePlacementAsync(string cueId, string compositionId, int layerIndex, ShowVideoPlacement placement) =>
        InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { } voice)
                return Task.FromResult(voice.UpdatePlacement(
                    compositionId, layerIndex, BuildVideoPlacementSpec(compositionId, layerIndex, placement)));
            return Task.FromResult(false);
        });

    /// <summary>Hot-attaches an output lease to a LIVE composition so a playing clip starts fanning its
    /// composited video to a newly-selected line WITHOUT a re-fire (the GUI's <c>TryAddOutput</c> under the
    /// ShowSession path). Returns false when the composition isn't currently loaded. The lease carries the same
    /// borrowed/owned ownership contract as the fire-path video leases (a borrowed host output declares
    /// <see cref="ClipCompositionOutputLease.DisposeOutputOnRuntimeDispose"/> = false).</summary>
    public Task<bool> AddCompositionOutputAsync(string compositionId, ClipCompositionOutputLease lease)
    {
        ArgumentException.ThrowIfNullOrEmpty(compositionId);
        ArgumentNullException.ThrowIfNull(lease);
        return InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition) && composition.AddOutput(lease)));
    }

    /// <summary>Hot-detaches an output (by its lease <c>OutputId</c>) from a LIVE composition - the GUI's
    /// <c>TryRemoveOutput</c> under the ShowSession path. Returns false when the composition isn't loaded or had
    /// no such output. The detached output is NOT disposed here (the host that leased it owns its lifetime).</summary>
    public Task<bool> RemoveCompositionOutputAsync(string compositionId, string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(compositionId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        return InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition) && composition.RemoveOutput(outputId)));
    }

    /// <summary>Live-edit the active cue's audio routing matrix (source channels → <paramref name="outputId"/>'s
    /// channels) while it plays (the GUI's <c>UpdateActiveCueAudioRoutes</c>). Returns false when the cue isn't
    /// the active clip on any group (or has no audio router). Applies on the clip's source→output route.</summary>
    public Task<bool> ApplyActiveAudioMatrixAsync(string cueId, string outputId, float[,] gains) =>
        InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { Player: { AudioRouter: { } router, AudioSourceId: { } sourceId } })
            {
                router.ApplyMatrix(sourceId, outputId, gains);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });

    /// <summary>Live-edit the active cue's audio routing by re-applying its per-output-line routes (each line's
    /// channel map/full gain matrix + gain) while it plays - the GUI's <c>UpdateActiveCueAudioRoutes</c> under the
    /// ShowSession path. Each route <c>i</c> replaces every route for the clip's <c>clip{i}</c> output, then installs
    /// either its legacy channel map or its per-cell matrix. Returns false when
    /// the cue isn't the active clip on any group. If the live clip-output count no longer matches the edited route
    /// count (a line was added/removed/muted mid-playback, which reorders the positional <c>clip{i}</c> ids), the
    /// live apply is skipped so nothing is mis-patched - that change lands cleanly on the next fire instead.</summary>
    public Task<bool> ApplyActiveAudioRoutesAsync(string cueId, IReadOnlyList<ShowClipAudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { Player: { AudioRouter: { } router, AudioSourceId: { } sourceId } } voice)
            {
                // Count the clip's contiguous clip0..clipN outputs; only live-apply when that count matches the
                // edited routes (stable composition - the common level/channel tweak). A count change reorders
                // the positional ids, so defer it to the next fire rather than mis-patch a live output.
                var ids = router.GetRegisteredOutputIds().ToHashSet(StringComparer.Ordinal);
                var liveClipOutputs = 0;
                while (ids.Contains($"clip{liveClipOutputs}"))
                    liveClipOutputs++;
                if (liveClipOutputs != routes.Count)
                    return Task.FromResult(true); // composition changed → applies on the next fire

                // Install the edited routes at the clip's CURRENT composed level, not at the fade level
                // alone: EffectiveAudioLevel is the single source of truth (fade × envelope × master
                // trim) that ApplyAudioScale writes with, and a live edit must not resurrect the
                // untrimmed/un-enveloped gain. Reading the composed product here (rather than
                // re-deriving it) also means the reconciling pass below writes identical values, so a
                // slider drag never blips through an untrimmed gain.
                var level = voice.EffectiveAudioLevel;
                var updatedTargets = new List<AudioRouteTarget>(routes.Count);
                for (var i = 0; i < routes.Count; i++)
                {
                    var map = routes[i].ToChannelMap();
                    var outputId = $"clip{i}";
                    if (!routes[i].HasGainMatrix && map is null)
                    {
                        // A fully-unrouted line carries no map - nothing to re-apply. Its previously
                        // installed route keeps playing, so keep its OLD target too: dropping it from the
                        // rebuilt list would exempt that line from stop-fades/scale rides (hard cut).
                        if (voice.RouteTargets.FirstOrDefault(t => t.OutputId == outputId) is { } kept)
                            updatedTargets.Add(kept);
                        continue;
                    }
                    var old = voice.RouteTargets.FirstOrDefault(t => t.OutputId == outputId);
                    var switchedKinds = old is null || old.Route?.HasGainMatrix != routes[i].HasGainMatrix;
                    try
                    {
                        // Same-kind updates reconcile in place (matrix cells ramp atomically; legacy route id
                        // replaces in place). Only a matrix↔legacy mode switch needs all pair routes removed.
                        if (switchedKinds)
                            router.RemoveRoute(sourceId, outputId);
                        if (routes[i].HasGainMatrix)
                            router.ApplyMatrix(sourceId, outputId,
                                routes[i].ToGainMatrix(routes[i].Gain * level));
                        else
                            router.AddRoute(sourceId, outputId, map!.Value,
                                routes[i].Gain * level);
                        updatedTargets.Add(new AudioRouteTarget(outputId, routes[i].Gain, routes[i]));
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        // channel count mismatch vs the live output - lands on the next fire
                        if (old is not null)
                        {
                            if (switchedKinds && old.Route is { } oldRoute)
                            {
                                try
                                {
                                    if (oldRoute.HasGainMatrix)
                                        router.ApplyMatrix(sourceId, outputId,
                                            oldRoute.ToGainMatrix(old.TargetGain * level));
                                    else if (oldRoute.ToChannelMap() is { } oldMap)
                                        router.AddRoute(sourceId, outputId, oldMap,
                                            old.TargetGain * level);
                                }
                                catch (Exception rollbackEx) when (
                                    rollbackEx is ArgumentException or InvalidOperationException)
                                {
                                    // The output changed underneath both edits; the next rebuild/fire owns it.
                                }
                            }
                            updatedTargets.Add(old); // keep stop/fade ownership of the still-installed route
                        }
                    }
                }

                voice.SetRouteTargets(updatedTargets);
                // One composition pass over the NEW target set, through the single place route gains
                // are written (fade × envelope × master trim). Value-wise a no-op after the installs
                // above, but it is what makes the level composition - not this method - authoritative,
                // and it covers the rolled-back/kept targets uniformly.
                voice.ApplyAudioScale(voice.RouteTargets, voice.ClipLevel);

                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
    }

    /// <summary>REBUILDS the active cue's audio outputs from a fresh route set while it plays - the count-change
    /// counterpart of <see cref="ApplyActiveAudioRoutesAsync"/> (which only re-applies in place for a stable
    /// count). Removes EVERY current <c>clip{i}</c> output from the router (its <c>_audio_discard</c>
    /// negotiation-lead sink stays, so the router keeps running - the clip plays on even with ZERO device
    /// outputs, on the wall clock), then re-adds one output per route. Used by the deck's hot output add/remove so
    /// unrouting an output keeps playback going and re-routing re-attaches at the live position. Returns false
    /// when the cue isn't the active clip on any group.</summary>
    public Task<bool> RebuildActiveClipAudioOutputsAsync(string cueId, IReadOnlyList<ShowClipAudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { Player: { AudioRouter: { } router, AudioSourceId: not null } } voice)
            {
                // 1) Drop every current clip{i} output from the router FIRST (before releasing the tracked sinks,
                //    so no route dangles to a released output). The discard sink is left, so the router keeps pacing.
                foreach (var id in router.GetRegisteredOutputIds()
                             .Where(id => id.StartsWith("clip", StringComparison.Ordinal)).ToList())
                    router.RemoveOutput(id);

                // 2) Re-add one output per route (mirrors CommitClipAsync's per-clip audio block). Per-route
                //    isolation is CRITICAL here: step 1 already removed every output, so without it one
                //    un-openable device (e.g. a fixed-rate JACK graph rejecting the clip's mix rate) faulted the
                //    whole rebuild and left the clip totally silent instead of playing its remaining routes.
                var rate = voice.Player.SampleRate > 0 ? voice.Player.SampleRate : 48_000;
                // Re-attach at the clip's CURRENT composed level (fade × envelope × master trim), never at
                // the raw authored gain: the rebuild can land while the clip sits under a master trim, mid
                // fade-in, or mid stop-fade, and attaching at unity would jump the cue to full level for
                // the gap before the reconciling pass below (an audible pop, and permanent for a clip with
                // no fade/envelope running to rewrite it). Read from the one place the product is defined.
                var level = voice.EffectiveAudioLevel;
                var newOutputs = new List<ClipAudioOutput>(routes.Count);
                var audioPumps = new List<(string OutputId, string DeviceId)>();
                var routeTargets = new List<AudioRouteTarget>();
                for (var i = 0; i < routes.Count; i++)
                {
                    var route = routes[i];
                    var outputId = $"clip{i}";
                    if (!TryAttachRouteOutput(
                            voice.Player, outputId, route.DeviceId, route.ToChannelMap(), rate,
                            gain: route.Gain * level, newOutputs, route))
                        continue;
                    routeTargets.Add(new AudioRouteTarget(outputId, route.Gain, route));
                    if (route.DeviceId is { } dev)
                        audioPumps.Add((outputId, dev));
                }

                // 3) Swap the voice's tracked set, release the OLD one per ownership, refresh route targets + pumps.
                foreach (var o in voice.SwapAudioOutputs(newOutputs))
                    ReleaseClipAudioOutput(o);
                voice.SetRouteTargets(routeTargets);
                // 4) One level-composition pass over the rebuilt targets - the same thing the fire path does
                //    after attaching (CommitClipAsync). The rebuilt routes are the ONLY ones the voice's fade
                //    ride now knows about, so this is what keeps a trimmed/faded clip at its real level.
                voice.ApplyAudioScale(voice.RouteTargets, voice.ClipLevel);
                voice.SetAudioPumps(audioPumps);
                PublishGroupViews();
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
    }

    /// <summary>Live-swap the active cue's held video frame - a text / still cue whose content was edited while it
    /// plays - with no reload or re-fire. Finds the cue's active clip and, if its source supports it
    /// (<see cref="IReplaceableFrameSource"/>, e.g. a rendered text source), replaces the displayed frame in place.
    /// Returns false when the cue isn't the active clip on any group or its source can't be swapped; the session
    /// owns <paramref name="frame"/> after this call (disposed if not applied).</summary>
    public Task<bool> UpdateActiveClipFrameAsync(string cueId, VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { Player.VideoSource: IReplaceableFrameSource replaceable })
            {
                replaceable.ReplaceFrame(frame);
                return Task.FromResult(true);
            }

            frame.Dispose(); // not applied → don't leak the caller's frame
            return Task.FromResult(false);
        });
    }

    /// <summary>Previews a loaded cue's clip on a separate (preview / headphones) device, independent of the
    /// transport groups (the GUI's <c>PreviewCue</c>). Opens a FRESH instance (not the standby-prepared clip),
    /// plays it on <paramref name="previewDeviceId"/> (or the default device), and fires
    /// <see cref="PreviewEnded"/> at its natural end. Replaces any current preview. Returns false when the cue
    /// has no clip binding, or when the preview was preempted (stopped/replaced) while its media was opening.
    /// The open runs OFF the serial dispatcher (NXT-19) so a slow audition open never parks transport, and
    /// <see cref="StopPreviewAsync"/> / a replacing preview cancels it mid-open.</summary>
    public Task<bool> PreviewCueAsync(string cueId, string? previewDeviceId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.PreviewCueAsync(cueId, previewDeviceId);
    }

    /// <summary>Stops the current preview, if any (the GUI's <c>StopPreview</c>) - including one still opening
    /// (NXT-19). Does not raise <see cref="PreviewEnded"/>.</summary>
    public Task StopPreviewAsync() => _voicePlayer.StopPreviewAsync();

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
        foreach (var cue in _cueGraph.Cues
                     .Where(c => (c.GroupId ?? DefaultGroup) == groupId && c.Number > group.LastFiredNumber)
                     .OrderBy(c => c.Number)
                     .Take(count))
        {
            if (_clipsByCue.TryGetValue(cue.Id, out var binding))
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

    private static readonly TimeSpan FadeStepInterval = FadeRamp.DefaultStepInterval;

    /// <summary>Ramps each route's gain from silence up to its configured target over <paramref name="duration"/>
    /// (the clip was attached silent). The ramp fraction multiplies each route's <c>TargetGain</c>, so a route
    /// set below or above unity fades up to exactly that level rather than to a hardcoded 1.0 (NXT-07).
    /// With <paramref name="fadesVideo"/> the clip's composition layers ride the SAME ramp from black
    /// (they attached at opacity 0) up to their authored opacities (<see cref="TransportGroup.BaseLayerOpacities"/>)
    /// - the incoming half of a dual-voice crossfade, and parity with every downward fade (stop, natural,
    /// outgoing tail), which always ramped opacity alongside audio; a plain per-cue FadeIn on a placed clip
    /// gets the same audio+video ramp (the audio-only behavior before this was an omission, not a choice -
    /// only the fade CUE keeps an explicit video opt-out, <c>AlsoFadeVideoOpacity</c>).
    /// A <see cref="FadeRamp"/>; cancelled when the clip is replaced. The ramp holds the group's clip-fade
    /// slot: a Fade cue fired during the fade-in window preempts it via <see cref="TransportGroup.BeginClipFade"/>
    /// and composes from whatever level the fade-in reached, and a claimed fade-out (operator stop/natural end)
    /// stops it - without either, two 25 ms ramps would alternately overwrite the clip level and the fade-in's
    /// final full-level step would destroy the fade cue's result.</summary>
    private void StartFadeIn(string groupId, TransportVoice voice,
        IReadOnlyList<AudioRouteTarget> routes, TimeSpan duration, FadeCurve curve, bool fadesVideo,
        CancellationToken ct)
    {
        var player = voice.Player;
        if (player.AudioSourceId is null && !fadesVideo)
            return;

        // Dispatcher-confined caller (the fire path); a fresh voice can't have an in-flight fade cue,
        // so this claim never cancels anything.
        var slotToken = voice.BeginClipFade();
        FadeRamp.Start(FadeStepInterval, ct, elapsed => InvokeAsync<bool>(() =>
        {
            if (ct.IsCancellationRequested ||
                slotToken.IsCancellationRequested || // a Fade cue took the clip's level over
                !_groups.ContainsKey(groupId) ||
                voice.State != VoiceState.Active ||   // replaced, or handed off to a crossfade tail
                voice.IsFadeOutClaimed ||            // a stop/natural fade-out owns the level now
                (player.AudioRouter is null && !fadesVideo))
            {
                voice.EndClipFade(slotToken);
                return Task.FromResult(true);
            }
            var frac = FadeRamp.LevelUp(elapsed, duration, curve);
            // Audio leg = ApplyAudioScale(frac), exactly as before; the opacity leg ramps each layer
            // from 0 toward its authored value (base × frac) - the mirror of the stop fade's ramp down.
            voice.ApplyFadeLevel(routes, 1f, voice.BaseLayerOpacities, frac);
            if (frac < 1f)
                return Task.FromResult(false);
            voice.EndClipFade(slotToken);
            return Task.FromResult(true);
        }));
    }

    /// <summary>Per-clip volume-envelope runner: a <see cref="FadeRamp"/>-style loop (same 25 ms step as
    /// every session fade) that samples the envelope at the clip's CURRENT position - the group timeline's
    /// <see cref="TransportTimelineSnapshot.CueTime"/>, i.e. post-StartOffset clip time, so the automation
    /// survives seeks and restarts each loop pass - and writes the factor through the group's one
    /// level-composition path (<see cref="TransportGroup.ApplyEnvelopeLevel"/> → <c>ApplyAudioScale</c>,
    /// where fade × envelope multiply). Runs for the clip's whole life: cancelled on replacement (the
    /// shared clip-work cts) or ended by its identity guard. Never started for an empty envelope.</summary>
    private void StartEnvelopeRunner(
        string groupId,
        TransportVoice voice,
        IReadOnlyList<ShowEnvelopePoint> envelope,
        CancellationToken ct)
    {
        if (voice.Player.AudioSourceId is null)
            return;

        FadeRamp.Start(FadeStepInterval, ct, _ => InvokeAsync<bool>(() =>
        {
            // Active only: the group timeline the envelope samples follows the ACTIVE voice, so a voice
            // handed off to a crossfade tail keeps the level its automation had reached.
            if (ct.IsCancellationRequested ||
                _groups.GetValueOrDefault(groupId) is not { } group ||
                !ReferenceEquals(group.ActiveVoice, voice) ||
                voice.Player.AudioRouter is null)
                return Task.FromResult(true);
            var clipPosition = group.Timeline.GetSnapshot().CueTime;
            voice.ApplyEnvelopeLevel(VolumeEnvelopes.Sample(envelope, clipPosition));
            return Task.FromResult(false); // no target to reach - the envelope rides the clip until release
        }));
    }

    internal static readonly TimeSpan EndMonitorPollInterval = TimeSpan.FromMilliseconds(100); // also the voice/preview monitors' rate
    private static readonly TimeSpan EndMonitorGuard = TimeSpan.FromMilliseconds(120);

    /// <summary>Consecutive end-monitor ticks a NotifyNaturalEnd clip's audio clock must sit stopped (not
    /// host-paused) before the stall is treated as source EOF - 500 ms at the 100 ms poll, comfortably past a
    /// coordinated seek's transient clock pause (~100–300 ms) while still snappy for cue auto-follow.</summary>
    private const int EndMonitorStallTicks = 5;

    /// <summary>Registers one active clip with the session's consolidated completion loop. Position checks run
    /// on the dispatcher, so pause/seek/replace state stays serialized with transport commands.</summary>
    private void StartEndMonitor(
        string groupId,
        TransportVoice voice,
        TimeSpan end,
        CancellationToken ct)
    {
        if (_groups.GetValueOrDefault(groupId) is not { } group || !ReferenceEquals(group.ActiveVoice, voice))
            return;
        group.EndMonitor = new ClipEndMonitorState(voice, end, ct);
        NotifyCompletionWorkAvailable();
    }

    internal void NotifyCompletionWorkAvailable() => _completionMonitor.NotifyWorkAvailable();

    private async Task<bool> PollCompletionWorkFromBackgroundAsync()
    {
        if (_disposed)
            return false;
        try
        {
            return await _dispatcher.InvokeAsync(PollCompletionWorkAsync).ConfigureAwait(false);
        }
        catch (SessionDispatcherOverloadedException)
        {
            return true; // bounded overload is transient; retry on the next shared tick
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>One dispatcher command checks every transport clip, preview, and soundboard voice.</summary>
    private async Task<bool> PollCompletionWorkAsync()
    {
        if (_disposed)
            return false;

        foreach (var (groupId, group) in _groups)
        {
            if (group.EndMonitor is not { } monitor)
                continue;
            if (await PollClipEndAsync(groupId, group, monitor).ConfigureAwait(false))
                group.ClearEndMonitor(monitor);
        }

        var voicesRemain = await _voicePlayer.PollCompletionsAsync().ConfigureAwait(false);
        return voicesRemain || _groups.Values.Any(g => g.EndMonitor is not null);
    }

    /// <summary>Applies one tick of loop/trim-out/freeze/natural-end behavior. Returns true when this monitor is done.</summary>
    private async Task<bool> PollClipEndAsync(
        string groupId,
        TransportGroup group,
        ClipEndMonitorState monitor)
    {
        var voice = monitor.Voice;
        var binding = voice.Binding;
        var player = voice.Player;
        if (monitor.CancellationToken.IsCancellationRequested || !ReferenceEquals(group.ActiveVoice, voice))
            return true;

        var loops = binding.Loop || binding.EndBehavior == ClipEndBehavior.Loop;
        var freezes = binding.EndBehavior == ClipEndBehavior.FreezeLastFrame;
        var position = player.Position;
        var remaining = monitor.End - position;

        // A timeline discontinuity (seek/pause/resume) restarts the EOF-stall persistence window.
        var generation = group.Timeline.Generation;
        if (generation != monitor.LastTimelineGeneration)
        {
            monitor.LastTimelineGeneration = generation;
            monitor.StalledTicks = 0;
        }

        if (binding.NotifyNaturalEnd && !loops && !freezes
            && player.SampleRate > 0
            && !group.PausedByHost
            && !player.IsRunning
            && position > TimeSpan.Zero)
        {
            if (++monitor.StalledTicks >= EndMonitorStallTicks)
            {
                await ReleaseActiveVoiceAsync(group).ConfigureAwait(false);
                ClipNaturallyEnded?.Invoke(binding.CueId);
                return true;
            }
        }
        else
        {
            monitor.StalledTicks = 0;
        }

        // Pre-end notify (dual-voice crossfade): one-shot per committed clip, so a crossfading host fires
        // the next item exactly once even though the poll keeps ticking inside the window - and a backwards
        // seek after the notification does not re-fire it (the host has already advanced).
        if (!monitor.PreEndNotified
            && binding.PreEndNotify > TimeSpan.Zero
            && !loops && !freezes
            && remaining > TimeSpan.Zero && remaining <= binding.PreEndNotify)
        {
            monitor.PreEndNotified = true;
            ClipApproachingEnd?.Invoke(binding.CueId);
        }

        var naturalFade = binding.FadeOut > TimeSpan.Zero
            ? binding.FadeOut
            : binding.EndBehavior == ClipEndBehavior.FadeOutAndStop
                ? DefaultStopFade
                : TimeSpan.Zero;
        if (!loops && !freezes && naturalFade > TimeSpan.Zero
            && remaining > TimeSpan.Zero && remaining <= naturalFade
            && voice.TryClaimFadeOut())
        {
            StartNaturalFadeOut(
                groupId,
                voice,
                voice.RouteTargets,
                voice.ClipLevel,
                voice.CaptureLayerOpacities(),
                remaining,
                binding.FadeOutCurve,
                monitor.CancellationToken);
            return true;
        }

        // Loop-with-crossfade (dual-voice design §3): inside the window, re-fire the SAME binding as a
        // fresh incoming voice through the crossfade replacement path - the current pass becomes the
        // outgoing tail and the next pass fades in over it. One-shot per committed clip (the incoming
        // commit replaces this monitor; a failed re-open clears the flag below so the butt-splice wrap
        // resumes). Only when the window is shorter than the trimmed pass, else an instant re-fire per
        // pass would churn voices continuously - such a clip falls back to the seamless-seek loop.
        if (loops && binding.LoopCrossfade > TimeSpan.Zero
            && !monitor.LoopCrossfadePending
            && binding.LoopCrossfade < monitor.End - binding.StartOffset
            && remaining > TimeSpan.Zero && remaining <= binding.LoopCrossfade)
        {
            monitor.LoopCrossfadePending = true;
            // Same fire-and-forget discipline as the warm-up launch: suppress ExecutionContext flow so
            // the dispatcher's AsyncLocal identity never leaks into the re-fire's continuations (NXT-22).
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(() => LoopCrossfadeReplaceAsync(groupId, binding, monitor));
        }

        if (position < monitor.End - EndMonitorGuard)
            return false;
        if (loops)
        {
            // The incoming voice owns this wrap - keep polling instead of seeking back, so the tail
            // plays out under the crossfade. If the re-open failed, the cleared flag re-enables the
            // butt-splice wrap on the next tick (degraded but still looping).
            if (monitor.LoopCrossfadePending)
                return false;
            var masterBeforeLoop = group.Timeline.GetSnapshot().MasterTime;
            SeekCoordinatedRestoringPlayState(
                player, binding.StartOffset, group, masterBeforeLoop, resume: true);
            return false;
        }
        if (freezes)
        {
            player.Pause();
            group.Timeline.MarkDiscontinuity();
            return true;
        }

        await ReleaseActiveVoiceAsync(group).ConfigureAwait(false);
        ClipNaturallyEnded?.Invoke(binding.CueId);
        return true;
    }

    private sealed class ClipEndMonitorState(
        TransportVoice voice,
        TimeSpan end,
        CancellationToken cancellationToken)
    {
        /// <summary>The voice this monitor watches - the group's Active at the time it was armed. Every tick
        /// re-checks that identity, so a monitor whose voice was replaced (or handed off to a crossfade tail)
        /// ends instead of emitting a second natural end for a clip nothing is playing.</summary>
        public TransportVoice Voice { get; } = voice;
        public TimeSpan End { get; } = end;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int StalledTicks { get; set; }
        public int LastTimelineGeneration { get; set; } = -1;

        /// <summary>Whether this clip's one-shot <see cref="ShowClipBinding.PreEndNotify"/> notification
        /// (<see cref="ClipApproachingEnd"/>) already fired.</summary>
        public bool PreEndNotified { get; set; }

        /// <summary>Whether this pass's loop-with-crossfade re-fire is in flight (or committed). While set,
        /// the monitor's butt-splice wrap is suppressed - the incoming voice owns the loop boundary. Cleared
        /// (on the dispatcher) if the re-open fails, restoring the seek-back wrap as the fallback.</summary>
        public bool LoopCrossfadePending { get; set; }
    }

    /// <summary>The loop-with-crossfade re-fire (<see cref="ShowClipBinding.LoopCrossfade"/>): opens a FRESH
    /// instance of the same binding (via the standby engine - a warm prepared clip makes the open cheap, a
    /// cold one simply re-decodes) and commits it through the crossfade replacement path, so the finishing
    /// pass moves to the group's Outgoing slot and plays its tail under the incoming pass. Runs off the
    /// dispatcher like any fire; the OLD clip's monitor token scopes it, so a stop/replace during the open
    /// discards the armed clip at commit instead of resurrecting the cue. A crossfaded wrap is still one
    /// loop pass: no cue event fires (this bypasses the cue graph on purpose - loop wraps never re-trigger
    /// auto-follow/fault policies), and <c>ClipNaturallyEnded</c> stays reserved for the real natural end.
    /// EqualPower, like every constant-power program crossfade (HaPlay's playlist advance).</summary>
    private async Task LoopCrossfadeReplaceAsync(
        string groupId, ShowClipBinding binding, ClipEndMonitorState monitor)
    {
        try
        {
            await PlayClipAsync(
                    groupId, binding, monitor.CancellationToken,
                    crossfade: (binding.LoopCrossfade, FadeCurve.EqualPower))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                MediaDiagnostics.LogWarning(
                    "ShowSession: loop-crossfade re-open failed for cue '{0}'; falling back to the butt-splice wrap ({1}).",
                    binding.CueId, ex.Message);
            // Re-arm the seek-back wrap on the dispatcher (monitor state is dispatcher-confined). If the
            // clip was stopped/replaced meanwhile the monitor is dead and the flag is inert either way.
            try
            {
                await InvokeAsync(() =>
                {
                    monitor.LoopCrossfadePending = false;
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch
            {
                // session torn down / dispatcher saturated mid-flight - nothing left to re-arm (a live
                // monitor only stays wedged until its clip is stopped or the show reloads)
            }
        }
    }

    /// <summary>Runs a natural-end audio/video fade without occupying the session dispatcher between steps
    /// (a <see cref="FadeRamp"/>), then releases the clip if it is still active.</summary>
    private void StartNaturalFadeOut(
        string groupId,
        TransportVoice voice,
        IReadOnlyList<AudioRouteTarget> routeTargets,
        float startAudioScale,
        IReadOnlyList<float> startLayerOpacities,
        TimeSpan duration,
        FadeCurve curve,
        CancellationToken ct)
    {
        FadeRamp.Start(
            FadeStepInterval, ct,
            step: elapsed => InvokeAsync<bool>(() =>
            {
                if (ct.IsCancellationRequested
                    || _groups.GetValueOrDefault(groupId) is not { } group
                    || !ReferenceEquals(group.ActiveVoice, voice))
                    return Task.FromResult(true);
                var scale = FadeRamp.LevelDown(elapsed, duration, curve);
                voice.ApplyFadeLevel(routeTargets, startAudioScale, startLayerOpacities, scale);
                return Task.FromResult(scale <= 0f);
            }),
            onCompleted: () => InvokeAsync(async () =>
            {
                if (_groups.GetValueOrDefault(groupId) is { } group
                    && ReferenceEquals(group.ActiveVoice, voice))
                {
                    var endedCueId = voice.Clip.Spec.Id;
                    await ReleaseActiveVoiceAsync(group).ConfigureAwait(false);
                    ClipNaturallyEnded?.Invoke(endedCueId); // natural fade-out completed
                }
            }));
    }

    /// <summary>Runs a voice's release ramp without occupying the session dispatcher between steps (a
    /// <see cref="FadeRamp"/>, like every session fade): the voice's routes and layers ramp from the level
    /// and opacities it held at the handoff down to silence over the window, then it releases through the
    /// same teardown every other path uses. Armed by the handoff itself
    /// (<see cref="TransportGroup.StartReleaseRamp"/>), so a voice can never be left releasing with nothing
    /// to bring it down. The ramp start is the voice's CURRENT fade level, never its composed level - the
    /// live master trim keeps multiplying through <see cref="SoundingLevel.Effective"/> on every step, so
    /// capturing the composed value here would apply the trim twice. A stop claim
    /// (<see cref="TransportVoice.TryClaimFadeOut"/>) or a hard release cancels the ramp and owns the
    /// release instead.</summary>
    private void StartVoiceReleaseRamp(
        string groupId, TransportVoice voice, TimeSpan duration, FadeCurve curve)
    {
        var ct = voice.BeginReleaseRamp();
        var startLevel = voice.ClipLevel;
        var startOpacities = voice.CaptureLayerOpacities();
        FadeRamp.Start(
            FadeStepInterval, ct,
            step: elapsed => InvokeAsync<bool>(() =>
            {
                if (ct.IsCancellationRequested
                    || voice.State != VoiceState.Releasing
                    || voice.IsFadeOutClaimed)
                    return Task.FromResult(true);
                var scale = FadeRamp.LevelDown(elapsed, duration, curve);
                voice.ApplyFadeLevel(voice.RouteTargets, startLevel, startOpacities, scale);
                return Task.FromResult(scale <= 0f);
            }),
            onCompleted: () => InvokeAsync(async () =>
            {
                if (_groups.GetValueOrDefault(groupId) is { } group
                    && voice.State == VoiceState.Releasing
                    && !voice.IsFadeOutClaimed)
                    await ReleaseVoiceAsync(group, voice).ConfigureAwait(false);
            }));
    }

    /// <summary>Resolves the N→M channel map for this clip's source→output route, or null for the source-derived default.</summary>
    private ChannelMap? ResolveOutputChannelMap(ShowClipBinding binding, string outputId)
    {
        var sourceId = binding.CueId;
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

    // --- transport commands (marshaled - D5) -------------------------------------------------------

    /// <summary>Fires a specific cue by id (PreWait/PostWait/AutoContinue honoured by the cue graph). Runs OFF the
    /// serial dispatcher (NXT-03), so its pre-wait + media open don't park the loop - STOP/seek/load/queries stay
    /// responsive and can abort it.</summary>
    public Task<CueExecutionStatus> FireCueAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCueAsync(cueId);
    }

    /// <summary>Fires a cue with a dual-voice crossfade window (Ideas/Dual-Voice-Crossfade-Design.md): when
    /// the cue's group already holds an active clip, that clip moves to the group's OUTGOING slot - keeping
    /// its outputs/routes/layers - and ramps to silence over <paramref name="crossfade"/> while the incoming
    /// clip fades in over the same window (an implied fade-in when the binding has none; a configured per-cue
    /// FadeIn wins). The outgoing releases at ramp end, or earlier on stop/panic/the next replacement. A null
    /// or non-positive <paramref name="crossfade"/> (or an idle group) is exactly
    /// <see cref="FireCueAsync(string)"/> - the butt-splice path, byte for byte. Transport (pause/seek/
    /// end-monitor) targets the incoming clip only; the outgoing tail is fire-and-forget. An incoming open
    /// failure leaves the current clip untouched (fail loud via the returned status).</summary>
    public Task<CueExecutionStatus> FireCueAsync(
        string cueId, TimeSpan? crossfade, FadeCurve crossfadeCurve = FadeCurve.Linear)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCueAsync(
            cueId, crossfade is { } duration && duration > TimeSpan.Zero ? (duration, crossfadeCurve) : null);
    }

    /// <summary>Fires one media cue on a caller-owned transport group instead of the group encoded in the
    /// show document. This is the manual-override path used by HaPlay: different children of one authored
    /// group can then play concurrently, while re-firing the same child replaces only its own manual slot.</summary>
    public async Task<CueExecutionStatus> FireCueIndependentAsync(
        string cueId,
        string independentGroupId,
        CancellationToken cancellationToken = default)
        => await FireCueIndependentCoreAsync(
                cueId, independentGroupId, waitForStartBarrier: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The coordinated-batch form: identical independent-group semantics, but the armed clip waits at the
    /// caller's barrier before it commits. Kept internal so the public batch API remains the only owner of barrier
    /// participant accounting.</summary>
    internal Task<CueExecutionStatus> FireCueIndependentAtBarrierAsync(
        string cueId,
        string independentGroupId,
        Func<Task> waitForStartBarrier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waitForStartBarrier);
        return FireCueIndependentCoreAsync(cueId, independentGroupId, waitForStartBarrier, cancellationToken);
    }

    private async Task<CueExecutionStatus> FireCueIndependentCoreAsync(
        string cueId,
        string independentGroupId,
        Func<Task>? waitForStartBarrier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(independentGroupId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_cueGraph.TryGetCue(cueId, out var cue))
            throw new ArgumentException($"cue '{cueId}' is not registered", nameof(cueId));
        if (!cue.Enabled)
            return CueExecutionStatus.SkippedDisabled;
        if (!cue.Armed)
            return CueExecutionStatus.SkippedNotArmed;
        if (!_clipsByCue.TryGetValue(cueId, out var binding))
            return CueExecutionStatus.NotReady;

        try
        {
            if (cue.PreWait > TimeSpan.Zero)
                await Task.Delay(cue.PreWait, cancellationToken).ConfigureAwait(false);
            await PlayClipAsync(independentGroupId, binding, cancellationToken, waitForStartBarrier).ConfigureAwait(false);
            if (cue.PostWait > TimeSpan.Zero)
                await Task.Delay(cue.PostWait, cancellationToken).ConfigureAwait(false);
            return CueExecutionStatus.Fired;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CueExecutionStatus.Failed;
        }
    }

    // The one in-flight explicit crossfade fire's window: published by FireOnGraphAsync just before the
    // graph fire runs (the orchestrator holds the fire lock, so at most one exists) and consumed exactly
    // once by the fired cue's clip action - the graph action closures are fixed at load time, so the
    // window rides beside the fire rather than through the CueGraph signature. Interlocked because the
    // set (fire worker) and consume (dispatcher, inside the fire's PlayClipAsync) are different threads.
    private Tuple<TimeSpan, FadeCurve>? _pendingFireCrossfade;

    /// <summary>Takes (and clears) the pending crossfade window for the clip action of an in-flight
    /// explicit crossfade fire. Null for every other fire - the plain path is untouched.</summary>
    private (TimeSpan Duration, FadeCurve Curve)? TakePendingFireCrossfade() =>
        Interlocked.Exchange(ref _pendingFireCrossfade, null) is { } pending
            ? (pending.Item1, pending.Item2)
            : null;

    /// <summary>Runs the current cue graph's fire - the <see cref="CueFireOrchestrator"/>'s state seam. Reads
    /// <see cref="_cueGraph"/> off-dispatcher exactly as the fire core always has (the graph reference swaps
    /// atomically on load; the show-generation guard makes a straddling fire discard its stale clip at commit).</summary>
    internal async Task<CueExecutionStatus> FireOnGraphAsync(
        string cueId, CancellationToken token, (TimeSpan Duration, FadeCurve Curve)? crossfade = null)
    {
        if (crossfade is { } window)
            Interlocked.Exchange(ref _pendingFireCrossfade, Tuple.Create(window.Duration, window.Curve));
        try
        {
            return await _cueGraph.FireAsync(cueId, token).ConfigureAwait(false);
        }
        finally
        {
            // A skipped/failed/cancelled fire may never reach its clip action - the unconsumed window
            // must not leak into a later plain fire.
            if (crossfade is not null)
                Interlocked.Exchange(ref _pendingFireCrossfade, null);
        }
    }

    /// <summary>Fires several cues together with a coordinated start - the fire-time counterpart of the seek/pause
    /// barriers (NXT-04 start skew / old-engine <c>FireGroupAsync</c> parity). Every cue's clip opens
    /// <em>concurrently</em> instead of each open serializing behind the previous cue's start, so a simultaneous
    /// cue group (the UI's coordinated trigger step) starts together rather than staggered by the sum of the opens
    /// - the cue graph is thread-safe for concurrent fires. All share ONE cancellation source, so a
    /// STOP/LOAD/DISPOSE aborts the whole group. Returns the per-cue statuses in
    /// input order (a cancelled cue reports <see cref="CueExecutionStatus.Failed"/>). Runs OFF the serial
    /// dispatcher (NXT-03) and holds the fire-lock for the whole group so no GO/fire interleaves.</summary>
    public Task<IReadOnlyList<CueExecutionStatus>> FireCuesAsync(IReadOnlyList<string> cueIds)
    {
        ArgumentNullException.ThrowIfNull(cueIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCuesAsync(cueIds);
    }

    /// <summary>Fires several clip-bound cues concurrently on distinct caller-owned runtime groups. Unlike
    /// <see cref="FireCuesAsync"/>, this deliberately does not use each cue's authored <see cref="CueDefinition.GroupId"/>:
    /// callers use it for simultaneously-fired siblings that share one authored group but must each retain an active
    /// clip. The batch holds the normal fire lock, shares cancellation, and returns statuses in input order.</summary>
    public Task<IReadOnlyList<CueExecutionStatus>> FireCuesIndependentAsync(
        IReadOnlyList<(string CueId, string RuntimeGroupId)> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var (cueId, runtimeGroupId) in targets)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cueId, nameof(targets));
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeGroupId, nameof(targets));
        }

        return _fires.FireCuesIndependentAsync(targets, cancellationToken);
    }

    /// <summary>GO - fires the next armed and enabled cue in <paramref name="groupId"/> after the cursor. A
    /// disabled or unarmed cue is skipped (never fired); the cursor advances only when the chosen cue actually
    /// ran or faulted, so a cue that was momentarily not fireable can still be reached by a later GO (NXT-07).</summary>
    public Task<CueExecutionStatus> GoAsync(string groupId = DefaultGroup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.GoAsync(groupId);
    }

    /// <summary>GO's cue selection (dispatcher): the next armed+enabled cue in <paramref name="groupId"/> after
    /// the group's cursor, plus the show generation it was read under (so the matching cursor advance can no-op
    /// when a reload swapped the show in between).</summary>
    internal Task<(CueDefinition? Next, int Generation)> SelectNextGoCueAsync(string groupId) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            var next = _cueGraph.Cues
                .Where(c => (c.GroupId ?? DefaultGroup) == groupId && c.Number > group.LastFiredNumber
                            && c.Armed && c.Enabled)
                .OrderBy(c => c.Number)
                .FirstOrDefault();
            return Task.FromResult((next, _showGeneration));
        });

    /// <summary>GO's cursor advance (dispatcher). A no-op when <paramref name="generation"/> no longer matches -
    /// a reload swapped the show between selection and advance, and the fresh show's cursor must not inherit the
    /// old one's progress (the pre-split code got the same outcome by writing to the orphaned group).</summary>
    internal Task AdvanceGoCursorAsync(string groupId, int number, int generation) =>
        InvokeAsync(() =>
        {
            if (_showGeneration == generation)
                GetOrAddGroup(groupId).LastFiredNumber = number;
            return Task.CompletedTask;
        });

    /// <summary>Seeks the active clip on <paramref name="groupId"/> (coordinated A/V seek).</summary>
    public Task SeekAsync(TimeSpan position, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            if (group.ActiveVoice is { } voice)
            {
                // SeekCoordinated pauses+seeks but does NOT resume, so preserve the pre-seek play state: a
                // scrub while playing must keep playing, not freeze. Without this the clip is left paused
                // (IsRunning=false) after every seek, and the media-player deck's poll reads that as "ended"
                // and tears the deck down - i.e. seek "stops playback" (matches SeekManyAsync's resume).
                var wasRunning = voice.Player.IsRunning;
                var masterBeforeSeek = group.Timeline.GetSnapshot().MasterTime;
                SeekCoordinatedRestoringPlayState(voice.Player, position, group, masterBeforeSeek, resume: wasRunning);
            }
            return Task.CompletedTask;
        });

    /// <summary>
    /// Coordinated seek that can never strand the clip paused: <c>SeekCoordinated</c> pauses BEFORE it
    /// seeks, so a decode/demux fault thrown by the seek (observed live: <c>avcodec_send_packet</c>
    /// EINVAL on some codecs) used to skip the resume AND the discontinuity mark - the clip sat frozen
    /// with no error shown, the deck's poll read it as ended, and every later seek failed the same way.
    /// On failure this restores the pre-seek play state (best effort - the demux stays wherever the
    /// failed seek left it) and still marks the discontinuity (the source position may have partially
    /// moved), THEN rethrows so the caller's task surfaces the fault.
    /// </summary>
    private static void SeekCoordinatedRestoringPlayState(
        S.Media.Players.MediaPlayer player,
        TimeSpan position,
        TransportGroup group,
        TimeSpan masterBeforeSeek,
        bool resume)
    {
        try
        {
            player.SeekCoordinated(position);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, $"ShowSession: coordinated seek to {position} failed; restoring play state");
            try
            {
                if (resume && !player.IsRunning)
                    player.Play();
            }
            catch (Exception resumeEx)
            {
                MediaDiagnostics.LogError(resumeEx, "ShowSession: resume after a failed seek also failed");
            }
            group.Timeline.MarkDiscontinuity(masterBeforeSeek);
            throw;
        }

        if (resume)
            player.Play();
        group.Timeline.MarkDiscontinuity(masterBeforeSeek); // source jumps; master stays monotonic (NXT-04)
    }

    /// <summary>Seeks several groups together behind one shared epoch - the group-seek barrier (NXT-04 /
    /// old-engine <c>group_seek_barrier</c> parity). Every target group is paused first so its clock freezes,
    /// each is seeked (coordinated), then the ones that were running resume together - so a multi-cue seek lands
    /// atomically instead of each group seeking (and drifting while the others keep advancing) in turn. Runs as
    /// one dispatcher operation, so no other transport command interleaves between the seeks. Groups with no
    /// active clip are skipped; a repeated group id just re-seeks its one active player (last position wins).</summary>
    public Task SeekManyAsync(IReadOnlyList<(string GroupId, TimeSpan Position)> seeks)
    {
        ArgumentNullException.ThrowIfNull(seeks);
        if (seeks.Count == 0)
            return Task.CompletedTask;
        return InvokeAsync(() =>
        {
            // 1) Freeze every target's clock (shared epoch) so a slow demux seek on one group can't let another
            //    group's playhead run on past it. Remember which were running so paused cues stay paused.
            var targets = new List<(
                TransportGroup Group,
                S.Media.Players.MediaPlayer Player,
                TimeSpan Position,
                bool Resume,
                TimeSpan MasterBeforeSeek)>(seeks.Count);
            List<Exception>? errors = null;
            foreach (var (groupId, position) in seeks)
            {
                var group = GetOrAddGroup(groupId);
                if (group.ActiveVoice is not { } voice)
                    continue;
                var wasRunning = voice.Player.IsRunning;
                var masterBeforeSeek = group.Timeline.GetSnapshot().MasterTime;
                if (wasRunning)
                {
                    try
                    {
                        voice.Player.Pause();
                    }
                    catch (Exception ex)
                    {
                        // Skip this group's seek (its clock never froze) but keep the barrier for the rest.
                        MediaDiagnostics.LogError(ex, $"ShowSession: group seek pause failed for group '{groupId}'; skipping its seek");
                        (errors ??= []).Add(ex);
                        continue;
                    }
                }
                targets.Add((group, voice.Player, position, wasRunning, masterBeforeSeek));
            }

            // 2) Seek all with clocks frozen, then 3) release the running ones together from the shared epoch.
            // A failing seek must not break the barrier: the other groups still seek, and EVERY paused group
            // still resumes (a faulted one from its pre-seek position) - a fault used to leave every
            // not-yet-seeked group stranded paused with no error surfaced.
            foreach (var (_, player, position, _, _) in targets)
            {
                try
                {
                    player.SeekCoordinated(position);
                }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, $"ShowSession: group seek to {position} failed; the clip resumes from its pre-seek position");
                    (errors ??= []).Add(ex);
                }
            }
            foreach (var (group, player, _, resume, masterBeforeSeek) in targets)
            {
                if (resume)
                {
                    try
                    {
                        player.Play();
                    }
                    catch (Exception ex)
                    {
                        MediaDiagnostics.LogError(ex, "ShowSession: group seek resume failed");
                        (errors ??= []).Add(ex);
                    }
                }
                group.Timeline.MarkDiscontinuity(masterBeforeSeek); // all masters preserve the shared pre-seek epoch
            }

            if (errors is not null)
            {
                if (errors.Count == 1)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
                throw new AggregateException("One or more group seeks failed (play state was restored).", errors);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Soft-stops and releases the active clip on <paramref name="groupId"/>. The cue's configured
    /// fade-out is used, falling back to <paramref name="fadeDuration"/> and then <see cref="DefaultStopFade"/>.
    /// Cancels any in-flight cue fire first so STOP never waits behind a long pre-wait/open (NXT-03). The fade
    /// ramp itself runs OFF the serial dispatcher (NXT-18) - only short gain/opacity steps and the final release
    /// marshal onto it - so other commands (pause, seek, load, a following GO's commit) never queue behind a
    /// long configured fade. The returned task still completes only after the clip is released ("stopped means
    /// stopped").</summary>
    /// <param name="fade">When true (the cue Stop/Panic default) the group's audio + layers ramp to silence over
    /// its <see cref="ShowClipBinding.FadeOut"/> or the stop fade first; when false the clip is
    /// cut immediately (the media-player deck stops hard - it has no soft-stop fade).</param>
    /// <param name="fadeDuration">Stop-fade override for clips with no configured <see cref="ShowClipBinding.FadeOut"/>
    /// (a configured clip fade-out always wins - per-cue > host stop setting). Null = <see cref="DefaultStopFade"/>;
    /// a non-positive value hard-cuts like <paramref name="fade"/> false.</param>
    /// <param name="curve">Gain curve for the stop fade. A clip whose own <see cref="ShowClipBinding.FadeOut"/>
    /// wins the duration precedence also keeps its own <see cref="ShowClipBinding.FadeOutCurve"/>.</param>
    public Task StopAsync(
        string groupId = DefaultGroup,
        bool fade = true,
        TimeSpan? fadeDuration = null,
        FadeCurve curve = FadeCurve.Linear)
    {
        _fires.CancelActiveFire();
        // An explicit non-positive duration hard-cuts even past a configured clip FadeOut (Panic
        // semantics), matching StopAllAsync and this method's own contract.
        if (fadeDuration is { } explicitDuration && explicitDuration <= TimeSpan.Zero)
            fade = false;
        // Every voice in the group: the clip transport points at AND any tail still fading under it. A group
        // stop is "take this group down", so the tail rides the same stop clock rather than playing on.
        return StopVoicesCoreAsync(() => VoicesOf(GetOrAddGroup(groupId)), fade, fadeDuration, curve);
    }

    /// <summary>Stops every PROGRAM source together - the HaPlay Stop/Panic entry point. Both drive this one
    /// method (Panic differs only by resolving a 0 ms fade), so they have identical reach by construction:
    /// whatever the master fader covers, both stop. It walks the same level/stop bus enumeration the fader
    /// does, so transport cues AND soundboard voices come down; the cue preview and audio taps are monitoring
    /// and keep running (killing the operator's audition on Panic would blind the person driving). Per-source
    /// stops run concurrently and each computes its level from its own duration, exactly as the single stop
    /// clock did for several groups.</summary>
    /// <param name="fadeDuration">Same precedence as <see cref="StopAsync"/>: per-clip fade-out > this override >
    /// <see cref="DefaultStopFade"/>. A non-positive value is a hard cut (Panic with a 0 ms setting) - clips
    /// release and visualizers detach without a ramp.</param>
    public async Task StopAllAsync(TimeSpan? fadeDuration = null, FadeCurve curve = FadeCurve.Linear)
    {
        _fires.CancelActiveFire();
        var request = new SoundingStopRequest(fadeDuration ?? DefaultStopFade, curve);
        IReadOnlyList<SoundingSourceRegistration> program;
        try
        {
            program = await InvokeAsync(() =>
            {
                // Panic also means "nothing new starts": a voice whose media is still opening is not on the
                // bus yet (nothing sounds), so the enumeration alone would let it begin playing right after
                // the stop. Cue fires are already preempted by CancelActiveFire above.
                _voicePlayer.CancelPendingVoiceOpens();
                return Task.FromResult(_sounding.ProgramSources());
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // disposed before the stop was claimed - disposal releases every source itself
        }

        // Visualizers are persistent composition surfaces rather than sounding sources. Fade them on the same
        // stop clock instead of detaching them immediately: otherwise an opaque/partly-opaque visualizer vanishes
        // in one frame and exposes the still-fading media layer beneath it as a visible brightness flash.
        var stops = program
            .Select(source => StopSoundingSourceAsync(source, request))
            .Append(FadeOutAndRemoveVisualizersAsync(
                compositionId: null, request.Fade ? request.FadeDuration : TimeSpan.Zero, curve));
        await Task.WhenAll(stops).ConfigureAwait(false);
    }

    /// <summary>Runs one program source's stop, surfacing a failure through <see cref="PlaybackAlert"/> instead
    /// of faulting the whole stop: under Panic the operator needs every OTHER source to still come down, and a
    /// silent swallow is what made a stuck source invisible before.</summary>
    private async Task StopSoundingSourceAsync(SoundingSourceRegistration source, SoundingStopRequest request)
    {
        try
        {
            await source.Stop!(request).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // session disposed mid-stop - disposal owns the teardown
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, $"ShowSession: stop-all/panic failed for sounding source '{source.Label}'");
            RaisePlaybackAlert(new ShowPlaybackAlert(
                source.Label, OutputId: null,
                $"stop-all/panic could not stop '{source.Label}': {ex.Message}", ex));
        }
    }

    /// <summary>Soft-stops the persistent visualizer on one composition. Visualizer Stop cues use the
    /// same fade clock as global Stop/Panic instead of detaching the surface in a single frame.</summary>
    /// <param name="fadeDuration">Fade length for this surface (a Stop cue passes its own transition time).
    /// Null = <see cref="DefaultStopFade"/>; non-positive detaches without a ramp.</param>
    public Task FadeOutCompositionVisualizerAsync(string compositionId, TimeSpan? fadeDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compositionId);
        return FadeOutAndRemoveVisualizersAsync(compositionId, fadeDuration ?? DefaultStopFade);
    }

    /// <summary>Stops the cue with <paramref name="cueId"/> wherever it is playing - as the group's active
    /// clip OR as a crossfade tail still fading out under a newer cue (per-cue stop / cancel - the GUI's
    /// <c>CancelCueCallback</c>). It targets the cue's own VOICES, so stopping the tail leaves the incoming
    /// clip playing and vice versa. No-op when that cue isn't currently sounding anywhere.</summary>
    public Task StopCueAsync(string cueId)
    {
        _fires.CancelActiveFire();
        return StopVoicesCoreAsync(
            () => [.. _groups.Values.SelectMany(
                group => group.Voices
                    .Where(voice => string.Equals(voice.Clip.Spec.Id, cueId, StringComparison.Ordinal))
                    .Select(voice => (group, voice)))],
            fade: true);
    }

    /// <summary>Every voice of one group as stop targets - the group-wide stop's selection.</summary>
    private static IReadOnlyList<(TransportGroup Group, TransportVoice Voice)> VoicesOf(TransportGroup group) =>
        [.. group.Voices.Select(voice => (group, voice))];

    /// <summary>What one stop claimed, captured on the dispatcher at claim time: the voice (the ONLY voice
    /// this stop may release) and - when the fade claim succeeded - its ramp. A voice whose fade claim lost
    /// to an in-flight fade still gets released: a STOP preempts.</summary>
    private sealed record VoiceStopClaim(TransportGroup Group, TransportVoice Voice, VoiceStopFade? Fade);

    /// <summary>One claimed voice's stop ramp: it runs from the level and opacities the voice held AT CLAIM
    /// TIME, so claiming a voice mid-ramp (a fade cue, or a crossfade tail part-way down) composes on top of
    /// what that ramp reached instead of popping the voice back up to full level.</summary>
    private sealed record VoiceStopFade(
        TransportVoice Voice,
        TimeSpan Duration,
        FadeCurve Curve,
        float StartAudioScale,
        IReadOnlyList<float> StartLayerOpacities,
        IReadOnlyList<AudioRouteTarget> RouteTargets);

    /// <summary>The shared stop path (NXT-18): resolves the target VOICES and claims their fades ON the
    /// dispatcher, ramps OFF it (<see cref="RunStopFadeAsync"/>), then releases each claimed voice back ON the
    /// dispatcher - identity-guarded by the voice's own state, so a cue fired DURING the fade survives (a stop
    /// only releases the voices it saw at claim time). A voice whose fade claim lost to an in-flight natural
    /// fade-out (or to a concurrent stop) skips the ramp - that fade owns the levels - but is still released
    /// here, and the release is idempotent so the two stops cannot fight. <paramref name="selectVoices"/> runs
    /// on the dispatcher.</summary>
    private async Task StopVoicesCoreAsync(
        Func<IReadOnlyList<(TransportGroup Group, TransportVoice Voice)>> selectVoices,
        bool fade,
        TimeSpan? fadeDuration = null,
        FadeCurve curve = FadeCurve.Linear)
    {
        var claims = await InvokeAsync(() =>
        {
            var targets = selectVoices();
            var list = new List<VoiceStopClaim>(targets.Count);
            foreach (var (group, voice) in targets)
            {
                VoiceStopFade? stopFade = null;
                if (fade && voice.TryClaimFadeOut())
                {
                    // Duration precedence: per-clip FadeOut > the caller's stop-fade override > session
                    // default. A clip fading on its OWN duration keeps its own curve too.
                    var clipFadeWins = voice.Binding.FadeOut > TimeSpan.Zero;
                    stopFade = new VoiceStopFade(
                        voice,
                        clipFadeWins ? voice.Binding.FadeOut : fadeDuration ?? DefaultStopFade,
                        clipFadeWins ? voice.Binding.FadeOutCurve : curve,
                        voice.ClipLevel,
                        voice.CaptureLayerOpacities(),
                        voice.RouteTargets);
                }

                list.Add(new VoiceStopClaim(group, voice, stopFade));
            }

            return Task.FromResult<IReadOnlyList<VoiceStopClaim>>(list);
        }).ConfigureAwait(false);

        if (claims.Count == 0)
            return; // nothing was sounding in the selection (an idle group's bus stop lands here)

        var fades = claims.Where(c => c.Fade is not null).Select(c => c.Fade!).ToArray();
        if (fades.Length > 0)
            await RunStopFadeAsync(fades).ConfigureAwait(false);

        try
        {
            await InvokeAsync(async () =>
            {
                foreach (var claim in claims)
                    await ReleaseVoiceAsync(claim.Group, claim.Voice).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The session was disposed mid-stop - disposal releases every voice itself.
        }
    }

    /// <summary>Ramps the claimed stop fades to silence OFF the dispatcher (an awaited <see cref="FadeRamp"/>):
    /// each step marshals one short gain/opacity commit onto it, so the serial loop is never parked for the
    /// fade duration (NXT-18). All fades advance from one clock, so Panic fades every claimed voice
    /// concurrently - each computing its own level from its own duration - and a crossfade tail claimed
    /// alongside its group's active clip comes down on the SAME stop clock. Exits early when every claimed
    /// voice has retired (nothing left to fade).</summary>
    private async Task RunStopFadeAsync(IReadOnlyList<VoiceStopFade> fades)
    {
        var maxDuration = TimeSpan.Zero;
        foreach (var fade in fades)
            if (fade.Duration > maxDuration)
                maxDuration = fade.Duration;
        if (maxDuration <= TimeSpan.Zero)
            return;

        try
        {
            await FadeRamp.RunAsync(FadeStepInterval, CancellationToken.None, elapsed => InvokeAsync(() =>
            {
                var applied = false;
                foreach (var fade in fades)
                {
                    if (fade.Voice.State == VoiceState.Retired)
                        continue; // released during the fade - nothing left to ramp
                    fade.Voice.ApplyFadeLevel(
                        fade.RouteTargets, fade.StartAudioScale, fade.StartLayerOpacities,
                        FadeRamp.LevelDown(elapsed, fade.Duration, fade.Curve));
                    applied = true;
                }

                return Task.FromResult(!applied || elapsed >= maxDuration);
            })).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // session disposed mid-fade - disposal owns the teardown
        }
    }

    /// <summary>One voice claimed by <see cref="FadeClipAsync"/>: the group, the voice that was active at
    /// claim time (the only one this fade may touch), its levels THEN (the ramp's start - consecutive fades
    /// compose), the authored full-level opacities (a fade UP knows where "level 1" is), and the claim's
    /// token (cancelled when a newer fade cue takes the same voice over).</summary>
    private sealed record ClipFade(
        TransportGroup Group,
        TransportVoice Voice,
        IReadOnlyList<AudioRouteTarget> RouteTargets,
        float StartLevel,
        IReadOnlyList<float> StartLayerOpacities,
        IReadOnlyList<float> BaseLayerOpacities,
        CancellationToken Token);

    /// <summary>Interpolates one fade-cue step between two arbitrary levels - the shared
    /// <see cref="FadeCurves.LevelBetween"/> (also the envelope sampler's segment interpolation), so
    /// target 0 matches the stop fade exactly and a fade up eases the same way a fade-in does.</summary>
    private static float LevelBetween(
        float start, float target, TimeSpan elapsed, TimeSpan duration, FadeCurve curve) =>
        FadeCurves.LevelBetween(start, target, elapsed, duration, curve);

    /// <summary>The persistent fade level (1 = full) of the active clip playing <paramref name="cueId"/>,
    /// or null when that cue is not an active clip. This is the level consecutive
    /// <see cref="FadeClipAsync"/> calls compose from.</summary>
    public Task<float?> GetClipFadeLevelAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() => Task.FromResult(
            ActiveVoiceOf(cueId) is { } voice ? (float?)voice.ClipLevel : null));
    }

    /// <summary>The live audio levels of the active clip playing <paramref name="cueId"/>, or null when
    /// that cue is not an active clip. <see cref="ClipAudioLevels.EffectiveLevel"/> is the exact product
    /// the route gains are written with (fade × envelope × master trim), read from the same group state.</summary>
    public Task<ClipAudioLevels?> GetClipAudioLevelsAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() => Task.FromResult(
            ActiveVoiceOf(cueId) is { } voice
                ? new ClipAudioLevels(voice.ClipLevel, voice.EnvelopeLevel, voice.EffectiveAudioLevel)
                : null));
    }

    /// <summary>Everything the session is currently feeding audio to, with the classification the master
    /// fader, stop-all and Panic key off: transport groups and soundboard voices as
    /// <see cref="SoundingSourceRole.Program"/>, the cue preview and audio taps as
    /// <see cref="SoundingSourceRole.Monitoring"/>. A host status panel reads it; it is also the only way
    /// to observe that monitoring is registered on the bus yet excluded from all three.</summary>
    public Task<IReadOnlyList<SoundingSourceInfo>> GetSoundingSourcesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() => Task.FromResult(_sounding.Snapshot()));
    }

    // --- master trim --------------------------------------------------------------------------------

    /// <summary>The current session-wide master trim (1 = unity). Written only on the session
    /// dispatcher by <see cref="SetMasterTrimAsync"/> and read from any thread (a host polls it to
    /// draw the fader), so both ends go through <see cref="Volatile"/>: a float write is atomic, but
    /// atomicity is not VISIBILITY - a plain read may be hoisted out of a polling loop and show a
    /// stale value indefinitely.</summary>
    public float MasterTrim => Volatile.Read(ref _masterTrim);

    private float _masterTrim = 1f;

    /// <summary>
    /// Sets the session-wide manual master trim - the live "bring the show down by hand" fader
    /// (Ideas/CuePlayer-Enhancements.md §6). One clamped scalar [0, 1] that MULTIPLIES every PROGRAM
    /// source's routed audio through the same <see cref="SoundingLevel"/> composition as fades and
    /// envelopes, so it composes with (never overwrites) the levels those mechanisms ride:
    /// <c>master × source × fade × envelope</c>. It walks the session's level/stop bus, so it reaches
    /// transport cues and soundboard voices in ONE enumeration; the cue preview and audio taps register
    /// as monitoring and are deliberately untouched (the operator's audition path must not duck when the
    /// show is pulled down). Session-level state, not per-source - sources that start AFTER a trim change
    /// inherit it, and a crossfade's outgoing tail rides it too (unless a stop fade already claimed that
    /// tail and owns its levels). This is a manual trim, not a stop: it is never persisted anywhere.
    /// </summary>
    public Task SetMasterTrimAsync(float scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InvokeAsync(() =>
        {
            var clamped = float.IsNaN(scale) ? 1f : Math.Clamp(scale, 0f, 1f);
            if (clamped == _masterTrim) // dispatcher-confined write ⇒ a plain read is fine HERE
                return Task.CompletedTask;
            Volatile.Write(ref _masterTrim, clamped);
            foreach (var source in _sounding.ProgramSources())
                source.ApplyMasterTrim!(clamped);
            return Task.CompletedTask;
        });
    }

    /// <summary>Fades the cue with <paramref name="cueId"/> (wherever it is the active clip) to
    /// <paramref name="targetLevel"/> over <paramref name="duration"/> - the Fade-cue entry point.
    /// The ramp starts from the clip's CURRENT level (<see cref="TransportGroup.ClipLevel"/>), so
    /// consecutive fades compose (fade to −10 dB then to silence starts at −10 dB) and a fade UP from a
    /// reduced level works. Identity-guarded like the stop fade: a clip replaced mid-ramp is left alone.
    /// A newer fade on the same clip preempts this ramp and takes over from its level; an operator
    /// stop/natural fade-out (the <see cref="TransportVoice.TryClaimFadeOut"/> claim) also preempts it.
    /// The returned task completes when the ramp ends (and, for a silent stop, the clip is released) or
    /// when the fade was preempted. No-op when the cue isn't playing.</summary>
    /// <param name="targetLevel">Absolute level [0,1] (1 = the clip's full authored gain/opacity).</param>
    /// <param name="stopWhenSilent">With target 0: release the clip at ramp end (the stop-release path);
    /// false keeps it running silently.</param>
    /// <param name="alsoFadeVideo">Ramp the clip's composition-layer opacities in step (toward the
    /// authored opacity × target level); false fades audio only.</param>
    public async Task FadeClipAsync(
        string cueId,
        float targetLevel,
        TimeSpan duration,
        FadeCurve curve = FadeCurve.Linear,
        bool stopWhenSilent = true,
        bool alsoFadeVideo = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(cueId);
        targetLevel = Math.Clamp(targetLevel, 0f, 1f);

        IReadOnlyList<ClipFade> claims;
        try
        {
            // Claim on the dispatcher: capture each targeted clip's current level as the ramp start and
            // take the group's clip-fade slot (cancelling a previous fade cue's ramp on the same clip).
            claims = await InvokeAsync(() =>
            {
                var list = new List<ClipFade>();
                foreach (var group in _groups.Values)
                {
                    // A fade cue targets the voice transport points at, deliberately: a tail is already on
                    // its way out under a ramp of its own (StopCueAsync is the path that reaches one).
                    if (group.ActiveVoice is not { } voice
                        || !string.Equals(voice.Clip.Spec.Id, cueId, StringComparison.Ordinal)
                        || voice.IsFadeOutClaimed) // a stop/natural fade-out owns this voice's levels
                        continue;
                    list.Add(new ClipFade(
                        group, voice, voice.RouteTargets, voice.ClipLevel,
                        voice.CaptureLayerOpacities(), voice.BaseLayerOpacities, voice.BeginClipFade()));
                }

                return Task.FromResult<IReadOnlyList<ClipFade>>(list);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // session disposed while the fade was queued - disposal owns the teardown
        }

        if (claims.Count == 0)
            return;

        try
        {
            await FadeRamp.RunAsync(FadeStepInterval, CancellationToken.None, elapsed => InvokeAsync(() =>
            {
                var applied = false;
                foreach (var fade in claims)
                {
                    if (fade.Token.IsCancellationRequested            // a newer fade cue took this voice over
                        || !ReferenceEquals(fade.Group.ActiveVoice, fade.Voice) // replaced/ended mid-ramp
                        || fade.Voice.IsFadeOutClaimed)              // an operator stop preempts the fade cue
                        continue;
                    var audioLevel = LevelBetween(fade.StartLevel, targetLevel, elapsed, duration, curve);
                    float[]? opacities = null;
                    if (alsoFadeVideo)
                    {
                        opacities = new float[fade.StartLayerOpacities.Count];
                        for (var i = 0; i < opacities.Length; i++)
                        {
                            var baseOpacity = i < fade.BaseLayerOpacities.Count ? fade.BaseLayerOpacities[i] : 0f;
                            opacities[i] = LevelBetween(
                                fade.StartLayerOpacities[i], baseOpacity * targetLevel, elapsed, duration, curve);
                        }
                    }

                    fade.Voice.ApplyClipFadeLevel(fade.RouteTargets, audioLevel, opacities);
                    applied = true;
                }

                return Task.FromResult(!applied || elapsed >= duration);
            })).ConfigureAwait(false);

            await InvokeAsync(async () =>
            {
                foreach (var fade in claims)
                {
                    fade.Voice.EndClipFade(fade.Token);
                    if (fade.Token.IsCancellationRequested
                        || !ReferenceEquals(fade.Group.ActiveVoice, fade.Voice)
                        || fade.Voice.IsFadeOutClaimed)
                        continue; // preempted - the newer fade / the stop owns the voice from here
                    // Silent stop: reuse the stop-release path, claiming the fade-out slot first so a
                    // concurrent stop can't start a second (inaudible) ramp on the released voice. Only THIS
                    // voice goes: a tail still fading beside it keeps its own ramp (releasing the active
                    // voice used to hard-cut the tail with it - an audible click).
                    if (stopWhenSilent && targetLevel <= 0f && fade.Voice.TryClaimFadeOut())
                        await ReleaseVoiceAsync(fade.Group, fade.Voice).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // session disposed mid-fade - disposal owns the teardown
        }
    }

    /// <summary>Soft-stops persistent visualizer layers (all, or one composition's) over
    /// <paramref name="duration"/> - non-positive detaches without a ramp. The captured slot identity makes the
    /// final detach safe when a new visualizer is fired onto the same composition while the old one is fading.</summary>
    private async Task FadeOutAndRemoveVisualizersAsync(
        string? compositionId, TimeSpan duration, FadeCurve curve = FadeCurve.Linear)
    {
        try
        {
            var fades = await InvokeAsync(() =>
                    Task.FromResult(_visualizers.CaptureForFade(compositionId)))
                .ConfigureAwait(false);
            if (fades.Count == 0)
                return;

            await FadeRamp.RunAsync(FadeStepInterval, CancellationToken.None, elapsed => InvokeAsync(() =>
            {
                var level = FadeRamp.LevelDown(elapsed, duration, curve);
                var applied = _visualizers.ApplyFadeLevel(fades, level);
                return Task.FromResult(!applied || level <= 0f);
            })).ConfigureAwait(false);

            await InvokeAsync(() =>
            {
                _visualizers.FinalizeFade(fades);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Session disposal owns every remaining surface and tap.
        }
    }

    /// <summary>Pauses or resumes the active clip on <paramref name="groupId"/> - a seamless toggle (codec
    /// pipelines are not flushed, so resume continues from the same frame, matching the GUI engine's
    /// <c>SkipFlush</c> pause). On pause the player's playhead - and therefore the group's session clock +
    /// transport-snapshot position - freezes; resume continues from there (the playback-clock freeze
    /// contract). No-op when the group has no active clip.</summary>
    public Task SetPausedAsync(bool paused, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            if (group.ActiveVoice is { } voice)
            {
                group.PausedByHost = paused; // end-monitor stall detection must not read a host pause as EOF
                // Announce the state change BEFORE applying it: resume's Play() prefills + starts audio
                // hardware, holding IsRunning=false for a while - lock-free snapshot consumers (the deck's
                // 250 ms end poll) key their debounce off the generation, so bumping it up front resets
                // their window at op START instead of only after the (potentially slow) apply.
                group.Timeline.MarkDiscontinuity();
                if (paused)
                    voice.Player.Pause();
                else
                    voice.Player.Play();
                group.Timeline.MarkDiscontinuity(); // rate/state change re-anchors the contract (NXT-04)
            }

            return Task.CompletedTask;
        });

    /// <summary>Pauses or resumes EVERY active transport group together - the all-groups form the UI drives, and
    /// the pause parity of <see cref="StopAllAsync"/>. The single-group <see cref="SetPausedAsync"/> only touches
    /// one group, so a multi-group cue show (cues fired onto several groups) would leave the other groups running
    /// when paused. Runs as one dispatcher operation so the groups toggle behind one epoch.</summary>
    public Task SetAllPausedAsync(bool paused) =>
        InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.ActiveVoice is { } voice)
                {
                    group.PausedByHost = paused; // see SetPausedAsync - keeps the end monitor's stall check honest
                    group.Timeline.MarkDiscontinuity(); // announce BEFORE the slow apply (see SetPausedAsync)
                    if (paused)
                        voice.Player.Pause();
                    else
                        voice.Player.Play();
                    group.Timeline.MarkDiscontinuity(); // see SetPausedAsync (NXT-04)
                }

            return Task.CompletedTask;
        });

    // --- queries (immutable snapshots - D5) --------------------------------------------------------

    /// <summary>An immutable snapshot of each transport group's session time, clip position, and run state.
    /// Lock-free (NXT-16): reads the published group view and pulls live position/run-state off the captured
    /// clock/player without marshaling, so it never queues behind a long-running command on the dispatcher.</summary>
    public Task<IReadOnlyList<TransportSnapshot>> SnapshotAsync() => Task.FromResult(Snapshot());

    /// <summary>The synchronous, lock-free form of <see cref="SnapshotAsync"/> - safe to call from any thread
    /// (e.g. a 250 ms UI position poll) even while the session dispatcher is busy with a long command.</summary>
    public IReadOnlyList<TransportSnapshot> Snapshot()
    {
        var views = _groupViews; // single volatile read of the published view
        var snaps = new TransportSnapshot[views.Count];
        for (var i = 0; i < views.Count; i++)
        {
            var v = views[i];
            // The captured player/clock may be torn down concurrently by a transport command; a racing read
            // just yields a stale/zero value for one poll tick rather than throwing across the query.
            TimeSpan now = TimeSpan.Zero, pos = TimeSpan.Zero, dur = TimeSpan.Zero;
            var running = false;
            var liveDisconnected = false;
            var audioChannels = 0;
            var audioSampleRate = 0;
            var timeline = v.Group.Timeline.GetSnapshot();
            var active = v.Player is not null; // has a clip (playing/paused/frozen) - independent of the clock
            try
            {
                now = timeline.MasterTime;
                if (v.Player is { } p)
                {
                    pos = p.Position;
                    dur = p.Duration;
                    running = p.IsRunning;
                    liveDisconnected = p.IsLiveSourceExhausted; // live input dropped (router may still report running)
                    if (p.AudioSource is { } audio)
                    {
                        audioChannels = audio.Format.Channels;
                        audioSampleRate = audio.Format.SampleRate;
                    }
                }
            }
            catch { /* concurrent teardown - leave zeros for this tick */ }
            snaps[i] = new TransportSnapshot(
                v.GroupId, now, pos, dur, running, active, liveDisconnected, audioChannels, audioSampleRate,
                timeline.Generation)
            {
                Timeline = timeline,
            };
        }
        return snaps;
    }

    /// <summary>An immutable snapshot of the loaded cue definitions, ordered by cue number.</summary>
    public Task<IReadOnlyList<CueDefinition>> GetCueDefinitionsAsync()
    {
        // Lock-free (NXT-16 residue): the graph reference is volatile and CueGraph is internally locked, so
        // this UI/fire-failure query never queues behind the dispatcher (a long command would stall it).
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(_cueGraph.Cues);
    }

    /// <summary>The cue ids whose clips are currently prepared (warm) in the standby engine - a UI "ready"
    /// indicator, and how a test confirms the pre-roll ran.</summary>
    public Task<IReadOnlyList<string>> GetPreparedCueIdsAsync()
    {
        // Lock-free (NXT-16 residue): the standby engine is internally locked - no dispatcher round-trip.
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult<IReadOnlyList<string>>(_standby.PreparedKeys.Select(k => k.Id).ToArray());
    }

    /// <summary>
    /// Attach a live <see cref="IVideoOutput"/> (e.g. a UI preview surface) to a loaded composition's pump - the
    /// composited canvas starts flowing to it on the next pump tick. Returns false if no composition has that id.
    /// The caller owns the output's lifetime; it is not disposed with the runtime.
    /// </summary>
    public Task<bool> AttachCompositionOutputAsync(string compositionId, IVideoOutput output, string outputId = "preview") =>
        InvokeAsync(() =>
            _compositions.TryGetValue(compositionId, out var composition)
                ? Task.FromResult(composition.AddOutput(new ClipCompositionOutputLease(outputId, outputId, output)))
                : Task.FromResult(false));

    /// <summary>An immutable snapshot of the cue execution log.</summary>
    public Task<IReadOnlyList<CueExecutionLogEntry>> GetCueExecutionLogAsync() =>
        InvokeAsync(() => Task.FromResult(_cueGraph.ExecutionLog));

    /// <summary>A composition's pump stats (frames submitted to its layers + composited), or null when no
    /// composition with that id is loaded - proves the cue→clip→layer→composite path ran (headless).</summary>
    public Task<ClipCompositionRuntimeStats?> GetCompositionStatsAsync(string compositionId) =>
        InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition)
                ? composition.GetStats()
                : (ClipCompositionRuntimeStats?)null));

    /// <summary>Applies (or clears, with <see langword="null"/>) a composition's output mapping at runtime -
    /// projector keystone / multi-panel tiling. Returns false when no composition with that id is loaded.</summary>
    public Task<bool> ApplyCompositionMappingAsync(string compositionId, ClipOutputMappingSpec? mapping) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
                return Task.FromResult(false);
            composition.UpdateCompositionMapping(mapping);
            return Task.FromResult(true);
        });

    /// <summary>Shows (<paramref name="frame"/> non-null) or hides (null) a mapping-calibration test pattern on a
    /// composition - held in a top-most, full-canvas layer so the operator can align one output's warp against the
    /// live grid. The host renders the grid frame (it owns the mapping/section masking) and hands it here; the
    /// session owns the frame after this call. Returns false when the composition id is unknown.</summary>
    public Task<bool> SetCompositionTestPatternAsync(string compositionId, VideoFrame? frame) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
            {
                frame?.Dispose();
                return Task.FromResult(false);
            }

            if (frame is null)
            {
                if (_testPatternSlots.Remove(compositionId, out var slot))
                    slot.Dispose(); // removes the top layer from the composition
                return Task.FromResult(true);
            }

            var canvas = composition.CanvasFormat;
            if (!_testPatternSlots.TryGetValue(compositionId, out var existing))
            {
                existing = composition.AddLayer(
                    canvas,
                    new VideoPlacementSpec(compositionId, int.MaxValue, Placement: "stretch"));
                _testPatternSlots[compositionId] = existing;
            }

            existing.Output.Configure(canvas);
            existing.Output.Submit(frame); // Submit takes ownership of the frame
            composition.EnsurePumpStarted();
            return Task.FromResult(true);
        });

    /// <summary>Applies (or clears) the mapping for one physical output of a composition. The output id is
    /// supplied by the host's <see cref="ClipCompositionOutputLease"/> and remains stable across live edits.</summary>
    public Task<bool> ApplyOutputMappingAsync(
        string compositionId, string outputId, ClipOutputMappingSpec? mapping) =>
        InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition)
            && composition.UpdateOutputMapping(outputId, mapping)));

    /// <summary>The ACTIVE voice playing <paramref name="cueId"/> on any group, or null. The live-edit and
    /// level-query APIs address "the active clip of cue X" deliberately: a tail is already on its way out
    /// under its own ramp, and re-patching or re-levelling it would fight that ramp. Dispatcher-confined.</summary>
    private TransportVoice? ActiveVoiceOf(string cueId) =>
        _groups.Values
            .Select(group => group.ActiveVoice)
            .FirstOrDefault(voice =>
                voice is not null && string.Equals(voice.Clip.Spec.Id, cueId, StringComparison.Ordinal));

    private TransportGroup GetOrAddGroup(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            // The group itself is not a sounding source - its VOICES are, one bus registration each
            // (CommitVoiceAsync). An idle group therefore has no registration to keep in sync: a voice
            // stamps the live master trim when it is created, so a trim set while the group was idle is
            // already folded into its next fire.
            _groups[groupId] = group = new TransportGroup
            {
                // Looked up by id rather than captured, exactly like every other deferred ramp here.
                StartReleaseRamp = (voice, duration, curve) =>
                    StartVoiceReleaseRamp(groupId, voice, duration, curve),
                VoiceRetired = voice => _sounding.Unregister(voice.SoundingId),
            };
            PublishGroupViews();
        }
        return group;
    }

    /// <summary>Retires every transport group - each group's own teardown releases its voices, and every
    /// release drops that voice's bus registration first. Shared by the document reload and disposal so a
    /// voice can never outlive its registration in one path and not the other.</summary>
    private async ValueTask DisposeGroupsAsync()
    {
        foreach (var group in _groups.Values)
            await group.DisposeAsync().ConfigureAwait(false);

        _groups.Clear();
        PublishGroupViews();
    }

    /// <summary>Republishes the lock-free query view (NXT-16). Called on the dispatcher after any change to the
    /// group set or a group's active clip, so <see cref="Snapshot"/> reads never round-trip the dispatcher.</summary>
    private void PublishGroupViews()
    {
        _groupViews = _groups
            .Select(kv => new GroupClockView(kv.Key, kv.Value.ActiveVoice?.Player, kv.Value))
            .ToArray();

        // Audio-pump view: every active clip's device-tagged routed outputs (skips default-device routes, which
        // can't be line-correlated). GetActiveAudioPumpStatsByDevice reads this lock-free.
        var pumps = new List<ActiveAudioPump>();
        foreach (var kv in _groups)
        {
            if (kv.Value.ActiveVoice is not { } active || active.Player.AudioRouter is not { } router)
                continue;
            foreach (var (outputId, deviceId) in active.AudioPumps)
                pumps.Add(new ActiveAudioPump(router, outputId, deviceId));
        }
        _audioPumpsView = pumps;
    }

    /// <summary>Lock-free per-device audio-pump stats (enqueued/dropped chunks) summed across the active cues'
    /// routed outputs - the audio analogue of <see cref="GetCompositionStats"/> for the outputs-panel line-health
    /// poll. Keyed by the PortAudio device id a cue routed audio to; a UI output line maps its device id into this.
    /// Reads a volatile snapshot (republished on fire/stop) then each router's own thread-safe pump stats - no
    /// dispatcher marshaling. Empty when no active cue routes device-addressed audio.</summary>
    public IReadOnlyDictionary<string, (long Enqueued, long Dropped)> GetActiveAudioPumpStatsByDevice()
    {
        var view = _audioPumpsView;
        var result = new Dictionary<string, (long Enqueued, long Dropped)>(StringComparer.Ordinal);
        foreach (var pump in view)
        {
            try
            {
                var st = pump.Router.GetPumpStats(pump.OutputId);
                var cur = result.TryGetValue(pump.DeviceId, out var v) ? v : default;
                result[pump.DeviceId] = (cur.Enqueued + st.Enqueued, cur.Dropped + st.Dropped);
            }
            catch (ArgumentException) { /* output retired between snapshot publish and read */ }
        }

        return result;
    }

    /// <summary>Allocation-free single-device variant of <see cref="GetActiveAudioPumpStatsByDevice"/> for the
    /// per-line 1 Hz health polls (each wants exactly one device id): walks the same lock-free view and sums
    /// only matching pumps instead of building the whole dictionary per poll.</summary>
    public bool TryGetActiveAudioPumpStats(string deviceId, out (long Enqueued, long Dropped) stats)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        long enqueued = 0, dropped = 0;
        var found = false;
        foreach (var pump in _audioPumpsView)
        {
            if (!string.Equals(pump.DeviceId, deviceId, StringComparison.Ordinal))
                continue;
            try
            {
                var st = pump.Router.GetPumpStats(pump.OutputId);
                enqueued += st.Enqueued;
                dropped += st.Dropped;
                found = true;
            }
            catch (ArgumentException) { /* output retired between snapshot publish and read */ }
        }

        stats = (enqueued, dropped);
        return found;
    }

    /// <summary>One active clip's full pipeline snapshot for the debug-stats poll: the transport group it
    /// plays in, the cue id, and the player's <see cref="S.Media.Players.MediaPlayerMetrics"/> (decode
    /// timing, jitter-buffer depth, router mix timing, per-output pump queues/drops/submit timing).</summary>
    public sealed record ActiveClipPipelineMetrics(
        string GroupId,
        string? CueId,
        S.Media.Players.MediaPlayerMetrics Metrics);

    /// <summary>Lock-free pipeline metrics for every group's active clip - the debug-stats analogue of
    /// <see cref="GetActiveAudioPumpStatsByDevice"/>. Walks the published group view (no dispatcher
    /// marshaling) and reads each player's own thread-safe counters. Empty when nothing is playing.</summary>
    public IReadOnlyList<ActiveClipPipelineMetrics> GetActiveClipPipelineMetrics()
    {
        var views = _groupViews; // single volatile read of the published view
        if (views.Count == 0)
            return [];
        var result = new List<ActiveClipPipelineMetrics>(views.Count);
        foreach (var view in views)
        {
            if (view.Player is not { } player)
                continue;
            try
            {
                result.Add(new ActiveClipPipelineMetrics(
                    view.GroupId,
                    view.Group.ActiveBinding?.CueId,
                    player.GetMetrics()));
            }
            catch (ObjectDisposedException) { /* clip retired between snapshot publish and read */ }
        }

        return result;
    }

    /// <summary>Lock-free stats for every loaded composition (id → runtime stats) - the multi-composition
    /// variant of <see cref="GetCompositionStats"/> for the debug-stats poll.</summary>
    public IReadOnlyList<ClipCompositionRuntimeStats> GetAllCompositionStats()
    {
        var view = _compositionsView;
        if (view.Count == 0)
            return [];
        var result = new List<ClipCompositionRuntimeStats>(view.Count);
        foreach (var runtime in view.Values)
        {
            try { result.Add(runtime.GetStats()); }
            catch (ObjectDisposedException) { /* retired between snapshot publish and read */ }
        }

        return result;
    }

    /// <summary>Republishes the lock-free composition view after a load/dispose changes <see cref="_compositions"/>.
    /// Call on the dispatcher (the only place <see cref="_compositions"/> is mutated).</summary>
    private void PublishCompositionsView() =>
        _compositionsView = new Dictionary<string, ClipCompositionRuntime>(_compositions, StringComparer.Ordinal);

    /// <summary>Lock-free per-composition stats for a UI health poll - no dispatcher marshaling (mirrors
    /// <see cref="SnapshotAsync"/>). Reads a volatile snapshot of the compositions republished on load, then the
    /// runtime's own thread-safe <c>GetStats</c>. Null when no such composition exists (or it is mid-teardown).</summary>
    public ClipCompositionRuntimeStats? GetCompositionStats(string compositionId)
    {
        if (!_compositionsView.TryGetValue(compositionId, out var runtime))
            return null;
        try { return runtime.GetStats(); }
        catch (ObjectDisposedException) { return null; } // retired between snapshot publish and read
    }

    /// <summary>Commits a voice as its group's Active, registers it on the level/stop bus as its OWN program
    /// source, and republishes the query view so a position/state poll sees the new run-state without waiting
    /// behind the dispatcher. One registration per VOICE (not per group) is what gives the master fader,
    /// stop-all and Panic the same reach over a crossfade tail as over the clip transport points at.</summary>
    private async ValueTask CommitVoiceAsync(
        string groupId, TransportGroup group, TransportVoice voice,
        (TimeSpan Duration, FadeCurve Curve)? crossfade)
    {
        await group.ActivateAsync(voice, crossfade).ConfigureAwait(false);
        voice.SoundingId = _sounding.RegisterProgram(
            $"cue:{groupId}:{voice.Clip.Spec.Id}",
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
        _fires.CancelActiveFire(); // unblock the dispatcher so disposal isn't stuck behind a long in-flight fire (NXT-03)
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

    /// <summary>What one voice IS to its group right now (Ideas/Structural-Refactor-Plan-2026-07-29.md §A).
    /// A group owns a LIST of voices and every level/stop/fade path iterates it uniformly; the state says
    /// which of them transport commands target, and who owns the voice's teardown.</summary>
    private enum VoiceState
    {
        /// <summary>Wired (outputs, routes, layers, timeline claims) but not yet committed to its group. The
        /// commit path alone owns it, so a failure there releases exactly what it wired without touching the
        /// live show.</summary>
        Arming,

        /// <summary>THE voice transport commands target - seek, pause, the end monitor, fade cues and the
        /// live route/placement edits. At most one per group.</summary>
        Active,

        /// <summary>Playing out its own tail on its own ramp (a crossfade handoff, or a stop that claimed
        /// it): it keeps its outputs, routes and layers, nothing targets its transport, and its ramp
        /// releases it. The only exit is retirement.</summary>
        Releasing,

        /// <summary>Torn down and off the level/stop bus. Terminal - and the ONE identity guard every
        /// deferred level write consults, so no ramp step can touch a replaced clip.</summary>
        Retired,
    }

    /// <summary>One voice of a transport group: its clip plus everything that clip owns (audio outputs,
    /// route targets, composition layers, timeline claims, subtitles) and its own level/fade state. A
    /// crossfade tail is the SAME kind of object as the clip transport points at - only its
    /// <see cref="State"/> differs - which is what lets stop, trim, fade and teardown iterate voices
    /// uniformly instead of special-casing a slot.</summary>
    private sealed class TransportVoice(IArmedClip clip, ShowClipBinding binding, float masterTrim)
    {
        public IArmedClip Clip { get; } = clip;
        public ShowClipBinding Binding { get; } = binding;
        public S.Media.Players.MediaPlayer Player => Clip.Player;
        public VoiceState State { get; private set; } = VoiceState.Arming;

        /// <summary>This voice's entry on the session's level/stop bus - ONE program source per voice, so
        /// the master fader, stop-all and Panic reach a tail exactly as they reach the active clip.</summary>
        public Guid SoundingId { get; set; }

        /// <summary>This voice's slice of the session's ONE level composition (master × source × fade ×
        /// envelope). Source stays at unity here - a transport clip's authored gains live per route on
        /// <see cref="AudioRouteTarget"/>. The trim is stamped at creation, so a voice fired under a reduced
        /// fader starts trimmed and no idle-group bookkeeping has to be kept in sync for it.</summary>
        public SoundingLevel Level { get; } = new() { Master = masterTrim };

        // Owned resources. The commit path fills these BEFORE the voice is committed, so one teardown covers
        // both a failed commit and every later release (the old catch block re-derived them from locals and,
        // past the commit point, released resources the group already owned).
        public List<ClipAudioOutput> Outputs { get; } = [];
        public List<PlacedLayer> Layers { get; } = [];
        public List<IDisposable> TimelineClaims { get; } = [];
        public List<IDisposable> Subtitles { get; } = [];

        /// <summary>Retained so both fade-in and every stop path ramp each route relative to its configured
        /// gain rather than assuming unity (NXT-07).</summary>
        public IReadOnlyList<AudioRouteTarget> RouteTargets { get; private set; } = [];

        public void SetRouteTargets(IReadOnlyList<AudioRouteTarget> routeTargets) => RouteTargets = [.. routeTargets];

        /// <summary>Device-tagged routed audio outputs (OutputId → the device it plays on) for the per-line
        /// audio-health poll. Only device-addressed routes are tracked.</summary>
        public IReadOnlyList<(string OutputId, string DeviceId)> AudioPumps { get; private set; } = [];

        public void SetAudioPumps(IReadOnlyList<(string OutputId, string DeviceId)> pumps) => AudioPumps = [.. pumps];

        /// <summary>The authored full-level opacity of each composition layer, captured when the clip
        /// committed - what a fade UP ramps toward (opacity at level 1).</summary>
        public IReadOnlyList<float> BaseLayerOpacities { get; private set; } = [];

        /// <summary>The clip's persistent current fade level, default 1. Full level is always audio scale 1
        /// (route TargetGains carry the authored gains), so the audio scale doubles as the level every fade
        /// path writes through - a fade cue composes from it (fade to -10 dB then to silence starts at -10)
        /// and a stop fade's capture keeps composing on top.</summary>
        public float ClipLevel => Level.Fade;

        /// <summary>The clip's current volume-envelope factor (1 = no automation). Held SEPARATE from the
        /// fade level so the envelope multiplies fades instead of polluting the level fades compose from.</summary>
        public float EnvelopeLevel => Level.Envelope;

        /// <summary>The audio level actually written to the routes: master × fade × envelope - the product
        /// <see cref="ApplyAudioScale"/> computes in the ONE place route gains are set.</summary>
        public float EffectiveAudioLevel => Level.Effective;

        // --- state machine -------------------------------------------------------------------------

        public void MarkActive() => State = VoiceState.Active;

        /// <summary>Moves this voice off the transport and onto its own tail. Its authored level freezes in
        /// the only sense that matters: nothing writes <see cref="SoundingLevel.Fade"/> or
        /// <see cref="SoundingLevel.Envelope"/> again except the release ramp, which multiplies the FADE
        /// component. The live master trim keeps riding through <see cref="SoundingLevel.Effective"/>, so it
        /// can never be captured into a ramp start and applied twice - the rule the old handoff had to state
        /// by hand (freeze the pre-master product, never the composed one) now holds by construction.
        /// <para>VIDEO half of the handoff: the group's timeline is about to follow the INCOMING clip, and
        /// the composition pump feeds that source time to every master-aligned slot on the canvas. This
        /// voice's own frames would then look far in the future (A at 3:12 under B at 0:00), master alignment
        /// would reject them all, and the tail would fade out as a FROZEN STILL. Nothing targets its
        /// transport again, so its layers take the free-running selection: a FRAME layer goes latest-wins on
        /// the player's own paced submissions; a GPU SURFACE layer has no frame queue - it renders at
        /// whatever instant it is handed - so it gets this voice's own source clock, captured ONCE here so
        /// the tail never re-reads a player field that its release nulls.</para></summary>
        public void BeginRelease()
        {
            State = VoiceState.Releasing;
            var ownClock = Player.PlayClock;
            var ownSourceTime = () => ownClock.CurrentPosition;
            foreach (var placed in Layers)
                placed.Slot.DetachFromMasterAlignment(ownSourceTime);
        }

        /// <summary>Tears the voice down: subtitles → layers → timeline claims → clip → outputs. The clip
        /// goes before its outputs (the player feeds them) and the timeline claim after every layer using it
        /// (so another group's waiting claim takes over cleanly). Idempotent, and the FIRST thing it does is
        /// retire the voice, so a racing ramp step is already a no-op when it lands.</summary>
        public async ValueTask ReleaseAsync()
        {
            if (State == VoiceState.Retired)
                return;
            State = VoiceState.Retired;
            CancelClipWork();
            CancelClipFade();
            CancelReleaseRamp();

            foreach (var attachment in Subtitles)
                attachment.Dispose();
            foreach (var placed in Layers)
                placed.Slot.Dispose();
            foreach (var claim in TimelineClaims)
                claim.Dispose();
            await Clip.ReleaseAsync().ConfigureAwait(false);
            foreach (var output in Outputs)
                ReleaseClipAudioOutput(output);

            Subtitles.Clear();
            Layers.Clear();
            TimelineClaims.Clear();
            Outputs.Clear();
            RouteTargets = [];
        }

        // --- claims --------------------------------------------------------------------------------

        private int _fadeOutClaimed;

        /// <summary>True once a stop / natural fade-out claimed this voice. A fade-cue ramp and the fade-in
        /// check it every step, so an operator stop preempts them.</summary>
        public bool IsFadeOutClaimed => Volatile.Read(ref _fadeOutClaimed) != 0;

        /// <summary>Claims this voice's levels for a stop or natural fade-out - at most once per voice, and
        /// never after it retires. Cancels any release ramp: from the claim on, the claiming stop owns both
        /// the levels and the release. This ONE claim replaces the Active slot's <c>TryBeginFadeOut</c> and
        /// the Outgoing slot's <c>TryClaimOutgoingForStop</c>, which is what makes concurrent stops safe -
        /// one wins the ramp, and the loser's release is idempotent rather than racing it.</summary>
        public bool TryClaimFadeOut()
        {
            if (State == VoiceState.Retired || Interlocked.Exchange(ref _fadeOutClaimed, 1) != 0)
                return false;
            _releaseRampCts?.Cancel();
            return true;
        }

        // The one in-flight fade-cue ramp per voice: a newer fade cue on the same clip cancels the previous
        // ramp and takes over from the clip's current level.
        private CancellationTokenSource? _clipFadeCts;

        /// <summary>Claims the clip-fade slot for a new fade-cue ramp: cancels the previous ramp (if any)
        /// and returns the new ramp's token. Dispatcher-confined.</summary>
        public CancellationToken BeginClipFade()
        {
            _clipFadeCts?.Cancel();
            _clipFadeCts?.Dispose();
            _clipFadeCts = new CancellationTokenSource();
            return _clipFadeCts.Token;
        }

        /// <summary>Releases the clip-fade slot when the ramp holding <paramref name="token"/> ends - only
        /// its own claim (a newer fade's slot stays). Dispatcher-confined.</summary>
        public void EndClipFade(CancellationToken token)
        {
            if (_clipFadeCts is { } cts && cts.Token == token)
            {
                cts.Dispose();
                _clipFadeCts = null;
            }
        }

        public void CancelClipFade()
        {
            _clipFadeCts?.Cancel();
            _clipFadeCts?.Dispose();
            _clipFadeCts = null;
        }

        // The voice's background work - the fade-in ramp, the volume-envelope runner and the end-of-clip
        // monitor - under one cancellation, cancelled when the voice leaves the transport.
        private CancellationTokenSource? _clipWorkCts;

        public void SetClipWorkCts(CancellationTokenSource cts) => _clipWorkCts = cts;

        public void CancelClipWork()
        {
            _clipWorkCts?.Cancel();
            _clipWorkCts?.Dispose();
            _clipWorkCts = null;
        }

        private CancellationTokenSource? _releaseRampCts;

        /// <summary>Issues the release ramp's cancellation token (cancelled by a stop claim, a hard release
        /// or disposal). Dispatcher-confined; called once, by the handoff that starts the ramp.</summary>
        public CancellationToken BeginReleaseRamp()
        {
            _releaseRampCts?.Cancel();
            _releaseRampCts?.Dispose();
            _releaseRampCts = new CancellationTokenSource();
            return _releaseRampCts.Token;
        }

        private void CancelReleaseRamp()
        {
            _releaseRampCts?.Cancel();
            _releaseRampCts?.Dispose();
            _releaseRampCts = null;
        }

        // --- levels --------------------------------------------------------------------------------

        /// <summary>Records what the fade paths need after the clip commits. <paramref name="baseLayerOpacities"/>
        /// carries the authored full-level opacities when the layers were attached BLACK for a fade-in
        /// (capturing them from the slots would record the zeroed values and break fade cues' upward ramps);
        /// null captures the slots as they stand.</summary>
        public void SetFadeMetadata(
            IReadOnlyList<AudioRouteTarget> routeTargets,
            float initialAudioScale,
            IReadOnlyList<float>? baseLayerOpacities)
        {
            SetRouteTargets(routeTargets);
            Level.Fade = Math.Clamp(initialAudioScale, 0f, 1f);
            BaseLayerOpacities = baseLayerOpacities ?? CaptureLayerOpacities();
        }

        /// <summary>Captures every placement's own opacity. A cue may deliberately use different opacities
        /// on different compositions, so a stop fade must scale each from its own value rather than snapping
        /// all layers to the first placement's opacity on the first ramp step.</summary>
        public IReadOnlyList<float> CaptureLayerOpacities() => [.. Layers.Select(placed => placed.Slot.Opacity)];

        /// <summary>The ONE place this voice's route gains are computed: master × source × fade × envelope
        /// (the authored per-route gain rides on top). Every fade path (fade-in, fade cue, natural/stop fade,
        /// the release ramp), the envelope runner and the master fader write through here, so the mechanisms
        /// compose by construction instead of overwriting each other. A retired voice is skipped - THAT is
        /// the identity guard the slot-era code spelled as "is this still the group's Active player".</summary>
        public void ApplyAudioScale(IReadOnlyList<AudioRouteTarget> routeTargets, float scale)
        {
            if (State == VoiceState.Retired)
                return;
            Level.Fade = Math.Clamp(scale, 0f, 1f);
            var level = Level.Effective;
            var player = Player;
            if (player.AudioRouter is { } router && player.AudioSourceId is { } sourceId)
                foreach (var target in routeTargets)
                {
                    if (target.Route is { HasGainMatrix: true } matrixRoute)
                        router.ApplyMatrix(
                            sourceId, target.OutputId,
                            matrixRoute.ToGainMatrix(target.TargetGain * level));
                    else
                        router.SetRouteGain(sourceId, target.OutputId, target.TargetGain * level);
                }
        }

        /// <summary>Applies one ramp step: the audio level scales from <paramref name="startAudioScale"/> and
        /// each layer's opacity from its own captured start. The ONE ramp shape - fade-in, natural fade-out,
        /// stop fade and the crossfade release all use it, on the active voice and on a tail alike.</summary>
        public void ApplyFadeLevel(
            IReadOnlyList<AudioRouteTarget> routeTargets,
            float startAudioScale,
            IReadOnlyList<float> startLayerOpacities,
            float scale)
        {
            if (State == VoiceState.Retired)
                return;
            scale = Math.Clamp(scale, 0f, 1f);
            ApplyAudioScale(routeTargets, startAudioScale * scale);
            // All placements share the timing ramp but retain their individual authored/live-edited opacity.
            for (var i = 0; i < Layers.Count; i++)
            {
                var startOpacity = i < startLayerOpacities.Count ? startLayerOpacities[i] : 0f;
                Layers[i].Slot.Opacity = startOpacity * scale;
            }
        }

        /// <summary>Applies one fade-cue step: the absolute audio level (which writes through
        /// <see cref="ClipLevel"/>) plus optional absolute per-layer opacities (null = audio-only fade).</summary>
        public void ApplyClipFadeLevel(
            IReadOnlyList<AudioRouteTarget> routeTargets, float audioLevel, IReadOnlyList<float>? layerOpacities)
        {
            if (State == VoiceState.Retired)
                return;
            ApplyAudioScale(routeTargets, audioLevel);
            for (var i = 0; layerOpacities is not null && i < Layers.Count && i < layerOpacities.Count; i++)
                Layers[i].Slot.Opacity = Math.Clamp(layerOpacities[i], 0f, 1f);
        }

        /// <summary>Applies one envelope-automation step: stores the factor and rewrites the route gains
        /// through <see cref="ApplyAudioScale"/> so the fade × envelope product stays in that one place. An
        /// unchanged factor (flat segment) skips the router writes entirely. Audio-only - layer opacities
        /// belong to the fades.</summary>
        public void ApplyEnvelopeLevel(float level)
        {
            if (State == VoiceState.Retired)
                return;
            level = Math.Clamp(level, 0f, VolumeEnvelopes.MaxLevel);
            if (level == Level.Envelope)
                return;
            Level.Envelope = level;
            ApplyAudioScale(RouteTargets, Level.Fade);
        }

        /// <summary>This voice's level/stop-bus trim hook: stores the new session trim and rewrites whatever
        /// it is currently sounding at through the same composition every fade uses. Applies to a tail as
        /// readily as to the active clip - the fader multiplies the level the release ramp has reached.</summary>
        public void ApplyMasterTrim(float master)
        {
            if (State == VoiceState.Retired)
                return;
            Level.Master = master;
            ApplyAudioScale(RouteTargets, Level.Fade);
        }

        /// <summary>Replaces the tracked audio outputs for a LIVE rebuild (hot add/remove of a deck output).
        /// Returns the previous set so the caller releases each per its ownership AFTER removing it from the
        /// router - releasing an owned sink while its route still exists would dangle.</summary>
        public IReadOnlyList<ClipAudioOutput> SwapAudioOutputs(IReadOnlyList<ClipAudioOutput> newOutputs)
        {
            var old = Outputs.ToArray();
            Outputs.Clear();
            Outputs.AddRange(newOutputs);
            return old;
        }

        /// <summary>Live-repositions the composition layer identified by <paramref name="compositionId"/>/
        /// <paramref name="layerIndex"/> (the placement the GUI edited). Falls back to the primary layer when
        /// the clip has a single placement. False if the clip has no matching layer.</summary>
        public bool UpdatePlacement(string compositionId, int layerIndex, VideoPlacementSpec spec)
        {
            if (Layers.Count == 0)
                return false;
            var target = Layers.FirstOrDefault(l => l.CompositionId == compositionId && l.LayerIndex == layerIndex);
            // A single-placement clip predates per-placement addressing: update it regardless of the passed key.
            if (target.Slot is null && Layers.Count == 1)
                target = Layers[0];
            if (target.Slot is null)
                return false;
            target.Slot.UpdatePlacement(spec);
            return true;
        }
    }

    /// <summary>One transport group: its session clock (D4), the LIST of voices playing in it (§A), and the
    /// one authoritative <see cref="TransportTimeline"/> every composition it feeds follows. Exactly one
    /// voice is <see cref="ActiveVoice"/> - what transport commands target and what the clock/timeline are
    /// bound to; the rest are tails playing out their own release ramps. Nothing here special-cases a tail:
    /// stop, trim, fade and teardown all iterate <see cref="Voices"/>.</summary>
    private sealed class TransportGroup : IAsyncDisposable
    {
        public SessionClock Clock { get; } = new(new MonotonicWallClock(start: false));
        public TransportTimeline Timeline { get; }

        public TransportGroup() => Timeline = new TransportTimeline(Clock);

        private readonly List<TransportVoice> _voices = [];

        /// <summary>Every voice in the group, oldest first. Dispatcher-confined, like the group itself.</summary>
        public IReadOnlyList<TransportVoice> Voices => _voices;

        /// <summary>The voice transport commands target: seek, pause, the end monitor, fade cues and the
        /// live route/placement edits. Null when the group holds no clip - it may still have tails.</summary>
        public TransportVoice? ActiveVoice { get; private set; }

        /// <summary>The binding of the clip transport points at - the debug-metrics view's cue label.</summary>
        public ShowClipBinding? ActiveBinding => ActiveVoice?.Binding;

        /// <summary>Arms a displaced voice's release ramp. Wired by the session at group creation and invoked
        /// INSIDE the handoff, so a voice can never sit in <see cref="VoiceState.Releasing"/> with no ramp:
        /// that window (a commit-path fault between the handoff and a later ramp start) is what used to
        /// orphan a tail at a frozen level with nothing left able to reach it.</summary>
        public required Action<TransportVoice, TimeSpan, FadeCurve> StartReleaseRamp { get; init; }

        /// <summary>Drops a voice's level/stop-bus registration. Invoked as the voice retires, BEFORE its
        /// player goes away, so the bus can never write to a dead player.</summary>
        public required Action<TransportVoice> VoiceRetired { get; init; }

        /// <summary>How many tails one group keeps. A SCOPING policy, not an architectural limit: the list
        /// holds N and every path already iterates it, so raising this is a one-line change. One tail is what
        /// the cue player needs (a triple overlap is a DJ-mixer feature) and it bounds the decoder/device
        /// cost of a fast GO sequence.</summary>
        private const int MaxReleasingVoices = 1;

        public int LastFiredNumber { get; set; } = int.MinValue;

        /// <summary>True while the host holds this group paused (Set(All)PausedAsync). The end monitor's
        /// stall-at-EOF check reads it so a paused clip's stopped clock is never mistaken for a natural end.
        /// Dispatcher-confined; cleared when the active voice changes.</summary>
        public bool PausedByHost { get; set; }

        public ClipEndMonitorState? EndMonitor { get; set; }

        public void ClearEndMonitor(ClipEndMonitorState expected)
        {
            if (ReferenceEquals(EndMonitor, expected))
                EndMonitor = null;
        }

        // --- committing and releasing voices --------------------------------------------------------

        /// <summary>Commits a freshly-armed voice as the group's Active. With no crossfade the displaced
        /// voice is released here (the historical butt splice, byte for byte); with one it moves to
        /// <see cref="VoiceState.Releasing"/> with its ramp armed in the SAME step, and only the tails beyond
        /// <see cref="MaxReleasingVoices"/> are hard-released.</summary>
        public async ValueTask ActivateAsync(TransportVoice voice, (TimeSpan Duration, FadeCurve Curve)? crossfade)
        {
            var displaced = DetachActiveVoice();
            var handoff = false;
            if (displaced is { } tail && crossfade is { } window)
            {
                tail.BeginRelease();
                StartReleaseRamp(tail, window.Duration, window.Curve);
                handoff = true;
            }

            // Bind the transport to the incoming voice and install it BEFORE the displaced one is released:
            // this runs on the serial dispatcher, so the brief new-in / old-not-yet-released window is never
            // observed.
            BindTransport(voice);
            _voices.Add(voice);
            ActiveVoice = voice;
            voice.MarkActive();

            if (handoff)
            {
                foreach (var beyondCap in ReleasingBeyondCap())
                    await ReleaseVoiceCoreAsync(beyondCap).ConfigureAwait(false);
            }
            else if (displaced is not null)
            {
                await ReleaseVoiceCoreAsync(displaced).ConfigureAwait(false);
            }
        }

        /// <summary>Releases the group's active voice and leaves the group idle. Tails are deliberately NOT
        /// touched: a tail is owned by its own ramp (and by a stop, or the group's teardown), so a natural
        /// end or a fade cue's silent stop can no longer hard-cut a still-fading tail mid-ramp.</summary>
        public async ValueTask ReleaseActiveVoiceAsync()
        {
            if (DetachActiveVoice() is not { } voice)
                return;
            BindTransport(null);
            await ReleaseVoiceCoreAsync(voice).ConfigureAwait(false);
        }

        /// <summary>Releases ONE voice, whichever state it is in. Idempotent: two stops may claim the same
        /// voice and both run their release.</summary>
        public ValueTask ReleaseVoiceAsync(TransportVoice voice) =>
            ReferenceEquals(ActiveVoice, voice) ? ReleaseActiveVoiceAsync() : ReleaseVoiceCoreAsync(voice);

        /// <summary>Releases every voice - the group's own teardown (disposal, document reload). A tail is
        /// fire-and-forget, so besides its ramp and a stop this is the only thing that reaches it.</summary>
        public async ValueTask ReleaseAllVoicesAsync()
        {
            DetachActiveVoice();
            BindTransport(null);
            foreach (var voice in _voices.ToArray())
                await ReleaseVoiceCoreAsync(voice).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ReleaseAllVoicesAsync();

        private async ValueTask ReleaseVoiceCoreAsync(TransportVoice voice)
        {
            if (voice.State == VoiceState.Retired)
                return;
            _voices.Remove(voice);
            if (ReferenceEquals(ActiveVoice, voice))
                ActiveVoice = null;
            VoiceRetired(voice); // off the level/stop bus before the player goes away
            await voice.ReleaseAsync().ConfigureAwait(false);
        }

        /// <summary>Takes the group off its active voice - the per-active group state (end monitor, host
        /// pause) and that voice's background work - and hands the voice back so the caller decides its
        /// fate (release it, or hand it to a release ramp).</summary>
        private TransportVoice? DetachActiveVoice()
        {
            var voice = ActiveVoice;
            ActiveVoice = null;
            EndMonitor = null;
            PausedByHost = false;
            // A fade-cue or fade-in ramp on the displaced voice must not go on writing its levels: the
            // retired/releasing guard would skip anyway, but cancelling ENDS the ramp instead of letting it
            // idle to completion.
            voice?.CancelClipWork();
            voice?.CancelClipFade();
            return voice;
        }

        /// <summary>Points the group's clock + timeline at <paramref name="voice"/> (null = idle). Only the
        /// ACTIVE voice drives them - which is exactly why a tail's layers leave master alignment.</summary>
        private void BindTransport(TransportVoice? voice)
        {
            Clock.SetReference(voice is null
                ? new MonotonicWallClock(start: false)
                : new PlayheadPlaybackClock(voice.Player.PlayClock));

            if (voice is null)
            {
                Timeline.Clear();
                return;
            }

            var trimStart = voice.Binding.StartOffset;
            TimeSpan? trimEnd = voice.Player.Duration > TimeSpan.Zero
                ? voice.Player.Duration - voice.Binding.EndOffset
                : null;
            if (trimEnd is { } knownEnd && knownEnd < trimStart)
                trimEnd = trimStart;
            Timeline.BindSource(
                voice.Player.PlayClock.AsPlayhead(), trimStart, trimEnd, isLive: voice.Player.IsLive);
        }

        /// <summary>The tails beyond <see cref="MaxReleasingVoices"/>, oldest first - hard-released by the
        /// handoff that pushed the count over the policy.</summary>
        private IReadOnlyList<TransportVoice> ReleasingBeyondCap()
        {
            var releasing = _voices.Where(v => v.State == VoiceState.Releasing).ToArray();
            return releasing.Length <= MaxReleasingVoices ? [] : releasing[..^MaxReleasingVoices];
        }
    }

    private sealed class PlayheadPlaybackClock(IPlayhead playhead) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => playhead.CurrentPosition;
        public bool IsAdvancing => playhead.IsRunning;
        // Forwarded: a seek/loop-wrap moves the playhead discontinuously, and a slaved MediaClock must see
        // that as an epoch boundary rather than as a same-epoch regression it is required to hold through.
        public long EpochId => playhead.PositionEpoch;
        public ClockReading Read() => playhead.ReadPosition();
    }

}

/// <summary>An active clip's live audio levels (<see cref="ShowSession.GetClipAudioLevelsAsync"/>):
/// the persistent fade level (what fade cues compose from), the volume-envelope factor, and the
/// effective level the route gains are actually written with - fade × envelope × the session-wide
/// <see cref="ShowSession.MasterTrim"/>.</summary>
public sealed record ClipAudioLevels(float FadeLevel, float EnvelopeLevel, float EffectiveLevel);

/// <summary>A mid-show audio-path failure surfaced by <see cref="ShowSession.PlaybackAlert"/>: the cue
/// whose clip hit it, the router output id when one specific output errored (null = the clip's whole
/// audio router faulted, i.e. its pacing clock/primary output died), and the underlying exception.</summary>
public sealed record ShowPlaybackAlert(string CueId, string? OutputId, string Message, Exception Exception);
