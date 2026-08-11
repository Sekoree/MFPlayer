using S.Media.Compositor;
using S.Media.Core.Audio;
using S.Media.Routing;
using S.Media.Time;

namespace S.Media.Session;

/// <summary>
/// The transport-voice model (Ideas/Structural-Refactor-Plan-2026-07-29.md §A): a group owns a LIST of
/// voices, each with its own state machine, its own level and its own claims, so stop / trim / fade /
/// teardown iterate voices uniformly instead of special-casing a crossfade tail.
/// <para>Split out of the root <c>ShowSession</c> file (2026-07-30 review §3), which had grown to ~4 000
/// lines with these ~580 self-contained lines at the bottom of it. They are nested types rather than
/// top-level ones on purpose: a voice is meaningless outside the session that owns its dispatcher, and
/// nesting is what keeps the session's private state reachable without widening anything to internal.</para>
/// </summary>
public sealed partial class ShowSession
{
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
        public ShowClipBinding Binding { get; private set; } = binding;
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

        /// <summary>
        /// Adopts the authored binding that now describes this still-running voice after a successful
        /// hot edit. A later preserving document reload compares against this value; leaving the old
        /// geometry here would make that reload retire a voice whose live layer was already updated.
        /// </summary>
        public void AdoptBinding(ShowClipBinding binding) => Binding = binding;

        /// <summary>The authored full-level opacity of each composition layer, captured when the clip
        /// committed - what a fade UP ramps toward (opacity at level 1).</summary>
        public IReadOnlyList<float> BaseLayerFadeLevels { get; private set; } = [];

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
            // Cancel, not just drop: an in-flight stop ramp must stop stepping against a retired voice.
            // This used to Dispose without cancelling, so a claimed ramp kept marshalling no-op steps onto
            // the dispatcher for the rest of its fade duration (2026-07-30 review §3).
            StopClaim.Cancel();
            // LAST: a stop awaiting this voice may return the moment it is signalled, and its caller is
            // entitled to treat that as "the voice is gone" - so nothing may still be pending here.
            StopClaim.MarkReleased();
        }

        // --- claims --------------------------------------------------------------------------------

        /// <summary>This voice's slice of the shared stop protocol - the claim, the release signal and the
        /// bounded wait a losing stop performs. <see cref="SoundingStopClaim"/> carries the rule; a
        /// soundboard voice holds the identical object, which is the point of it existing once.</summary>
        public SoundingStopClaim StopClaim { get; } = new();

        /// <summary>True once a stop / natural fade-out claimed this voice - see
        /// <see cref="SoundingStopClaim.IsClaimed"/>. Checked by the fade-cue ramp and the fade-in every
        /// step, so an operator stop preempts them.</summary>
        public bool IsFadeOutClaimed => StopClaim.IsClaimed;

        /// <summary>Completes when this voice has actually been torn down.</summary>
        public Task Released => StopClaim.Released;

        /// <summary>Claims this voice's levels for a stop or natural fade-out - <see cref="SoundingStopClaim.TryClaim"/>
        /// plus the one thing only a transport voice has: a release ramp that must yield to the stop that
        /// claimed it. Replaces the Active slot's <c>TryBeginFadeOut</c> and the Outgoing slot's
        /// <c>TryClaimOutgoingForStop</c>. Dispatcher-confined.</summary>
        public CancellationToken? TryClaimFadeOut(DateTime deadline)
        {
            if (State == VoiceState.Retired)
                return null;
            if (StopClaim.TryClaim(deadline) is not { } token)
                return null;
            _releaseRampCts?.Cancel(); // a claimed tail's release ramp yields to the stop that claimed it
            return token;
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

        /// <summary>Records what the fade paths need after the clip commits.
        /// <paramref name="baseLayerFadeLevels"/> carries the fade levels a ramp should treat as "full" when
        /// the layers were attached BLACK for a fade-in (capturing them from the slots would record the
        /// zeroed levels and break fade cues' upward ramps); null captures the slots as they stand.
        /// <para>These are FADE components, not rendered opacities: each layer's authored opacity lives on
        /// its slot as <c>BaseOpacity</c> and multiplies underneath, so a live placement edit changes what
        /// the layer looks like without disturbing a ramp in flight.</para></summary>
        public void SetFadeMetadata(
            IReadOnlyList<AudioRouteTarget> routeTargets,
            float initialAudioScale,
            IReadOnlyList<float>? baseLayerFadeLevels)
        {
            SetRouteTargets(routeTargets);
            Level.Fade = Math.Clamp(initialAudioScale, 0f, 1f);
            BaseLayerFadeLevels = baseLayerFadeLevels ?? CaptureLayerFadeLevels();
        }

        /// <summary>Captures every placement's current fade level. A stop fade must scale each layer from
        /// wherever its own ramp had reached rather than snapping them all to the first layer's value on the
        /// first step. (Authored per-composition opacity differences are handled underneath by each slot's
        /// <c>BaseOpacity</c>, so they no longer need capturing here.)</summary>
        public IReadOnlyList<float> CaptureLayerFadeLevels() => [.. Layers.Select(placed => placed.Slot.FadeLevel)];

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
        /// each layer's FADE component from its own captured start (the authored opacity multiplies
        /// underneath). The ONE ramp shape - fade-in, natural fade-out, stop fade and the crossfade release
        /// all use it, on the active voice and on a tail alike.</summary>
        public void ApplyFadeLevel(
            IReadOnlyList<AudioRouteTarget> routeTargets,
            float startAudioScale,
            IReadOnlyList<float> startLayerFadeLevels,
            float scale)
        {
            if (State == VoiceState.Retired)
                return;
            scale = Math.Clamp(scale, 0f, 1f);
            ApplyAudioScale(routeTargets, startAudioScale * scale);
            // All placements share the timing ramp but retain their individual authored/live-edited opacity.
            for (var i = 0; i < Layers.Count; i++)
            {
                var startOpacity = i < startLayerFadeLevels.Count ? startLayerFadeLevels[i] : 0f;
                Layers[i].Slot.FadeLevel = startOpacity * scale;
            }
        }

        /// <summary>Applies one fade-cue step: the absolute audio level (which writes through
        /// <see cref="ClipLevel"/>) plus optional per-layer fade levels (null = audio-only fade).
        /// <para>A fade cue's "50%" means half of what the layer was authored at, exactly as the audio side's
        /// level composes over the clip's own gain - not an absolute opacity that would discard it.</para></summary>
        public void ApplyClipFadeLevel(
            IReadOnlyList<AudioRouteTarget> routeTargets, float audioLevel, IReadOnlyList<float>? layerOpacities)
        {
            if (State == VoiceState.Retired)
                return;
            ApplyAudioScale(routeTargets, audioLevel);
            for (var i = 0; layerOpacities is not null && i < Layers.Count && i < layerOpacities.Count; i++)
                Layers[i].Slot.FadeLevel = Math.Clamp(layerOpacities[i], 0f, 1f);
        }

        /// <summary>Applies one opacity-automation step: stores the factor on each layer's level and lets the
        /// slot recompose <c>authored x fade x automation</c>. Nothing else in the chain is touched, which is
        /// the entire point - a lane, a fade and a live placement edit can all be in flight at once.</summary>
        public void ApplyOpacityAutomation(float level)
        {
            if (State == VoiceState.Retired)
                return;
            level = Math.Clamp(level, 0f, 1f);
            foreach (var placed in Layers)
                placed.Slot.AutomationLevel = level;
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
        public required Action<TransportVoice, TimeSpan, FadeShape> StartReleaseRamp { get; init; }

        /// <summary>Drops a voice's level/stop-bus registration. Invoked as the voice retires, BEFORE its
        /// player goes away, so the bus can never write to a dead player.</summary>
        public required Action<TransportVoice> VoiceRetired { get; init; }

        /// <summary>
        /// How many tails one group keeps. A SCOPING policy, not an architectural limit: the list holds N
        /// and every path already iterates it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Raised from 1 to 3 (HaCue2 framework audit, decision D7). At one tail, a rapid GO sequence -
        /// fire A, then B, then C in quick succession - hard-cut A the instant C started, because B had
        /// already taken the single release slot. A tail cut that early is still near full level, so the
        /// artifact was an audible click with no authored cause, and rapid sequences are normal operating
        /// for a cue player rather than an edge case.
        /// </para>
        /// <para>
        /// The bound stays because each tail holds a decoder and a producer lease, so an unbounded list
        /// would let a stuck GO exhaust resources. Three covers the realistic "GO GO GO" case while
        /// sitting far inside the plan's budget of 8 simultaneous program voices. The residual hard cut
        /// at the cap is much quieter than it was: a voice only reaches it after three later crossfades,
        /// by which point its own ramp has taken it well down.
        /// </para>
        /// <para>
        /// This bound is PER GROUP, and with per-list transport each cue list is its own group - so the
        /// real ceiling is this value times the number of simultaneously active lists, and that product
        /// is what the voice budget has to be checked against, not this constant alone.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Monotonic counter of opens started against this group; a commit whose ticket is no longer the
        /// latest discards its clip.
        /// </summary>
        /// <remarks>
        /// Gives "the last clip ASKED for is the one that plays" regardless of how long each open took. The
        /// cue path never needed it because the fire-lock serialises fires, but the cue-free
        /// <c>PlayClipAsync</c> has no such lock - so without this, two rapid opens on one group race, and a
        /// slow FIRST open could commit after a fast second one and leave the wrong clip playing. Ordering by
        /// request rather than by open duration is also the better answer: it is what a deck's rapid track
        /// changes mean.
        /// </remarks>
        public long OpenSequence { get; private set; }

        /// <summary>Takes the next open ticket. Dispatcher-confined, like every other group mutation.</summary>
        public long NextOpenTicket() => ++OpenSequence;

        private const int MaxReleasingVoices = 3;


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
        public async ValueTask ActivateAsync(TransportVoice voice, (TimeSpan Duration, FadeShape Curve)? crossfade)
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
            // The playhead IS the reference now - IPlayhead extends IPlaybackClock, so the adapter that
            // used to sit here (PlayheadPlaybackClock) has nothing left to translate.
            Clock.SetReference(voice is null
                ? new MonotonicWallClock(start: false)
                : voice.Player.PlayClock);

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
}
