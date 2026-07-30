using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Routing;

namespace S.Media.Session;

/// <summary>
/// Everything that hangs an EXTRA consumer off a firing clip - session audio taps (visualizers, meters),
/// composition visualizers built on top of them, and the router-fault wiring that turns an audio-path
/// failure into a <see cref="ShowPlaybackAlert"/>. Plus the two <see cref="ClipSpec"/> builders the voice
/// and preview paths hand to <see cref="VoicePlayer"/>, which live here because they read the same
/// dispatcher-confined binding state the tap attach does.
/// <para>Split out of the root file (2026-07-30 review §3): this is a self-contained "observers of a clip"
/// concern that sat between the constructor and the dispatcher, in the middle of the transport story.</para>
/// </summary>
public sealed partial class ShowSession
{
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

}
