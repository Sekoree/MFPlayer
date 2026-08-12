using System.Text.Json;
using System.Text.Json.Serialization;
using S.Media.Core.Audio;

namespace S.Media.Session;

/// <summary>What a clip does when it reaches its (trimmed) end. Mirrors the GUI cue end behaviour
/// (<c>CueEndBehavior</c>) and is honoured by the playback runtime (<see cref="ShowSession"/>'s clip
/// playback path resolves Loop / FreezeLastFrame / FadeOutAndStop against the clip's end window).</summary>
public enum ClipEndBehavior
{
    Stop,
    FreezeLastFrame,
    Loop,
    FadeOutAndStop,
}

/// <summary>A clip's appearance on its composition canvas - where its video sits and how it fits. Defaults
/// are full-canvas, opaque, Cover fit, upright (a clip with no placement composites exactly as before).
/// <paramref name="Fit"/> is the fit mode within the dest rect (Cover/Contain/Letterbox/Center/Stretch/
/// FillWidth/FillHeight; null = Cover). Maps to the compositor's <c>VideoPlacementSpec</c>.</summary>
public sealed record ShowVideoPlacement(
    double DestX = 0,
    double DestY = 0,
    double DestWidth = 1,
    double DestHeight = 1,
    double Opacity = 1,
    string? Fit = null,
    double RotationDegrees = 0,
    double CropLeft = 0,
    double CropTop = 0,
    double CropRight = 0,
    double CropBottom = 0,
    ClipOutputMappingSpec? VideoFx = null,
    // Optional chroma key ("green screen") for this placement's layer; null = disabled.
    Compositor.ChromaKeySettings? ChromaKey = null,
    // Optional brightness/contrast for this placement's layer; null = disabled.
    Compositor.Effects.BrightnessContrastSettings? ColorAdjust = null,
    // Stable authoring identities used by effect-parameter automation; settings remain null when bypassed.
    string? ChromaKeyInstanceId = null,
    string? ColorAdjustInstanceId = null);

/// <summary>One composition placement of a clip's video: which composition canvas (<paramref name="CompositionId"/>),
/// which layer (<paramref name="LayerIndex"/>), and where/how the frame sits on it (<paramref name="Placement"/>).
/// A cue may place the SAME decoded source onto several compositions/layers at once - picture-in-picture, the
/// same feed in two regions, or mirrored to a second canvas - so <see cref="ShowClipBinding.GetPlacements"/>
/// returns every placement and <c>PlayClipAsync</c> fans the one clip's video out to each (decoded once).</summary>
public sealed record ShowClipPlacement(
    string CompositionId,
    int LayerIndex = 0,
    ShowVideoPlacement? Placement = null);

/// <summary>Opacity automation for one exact video placement. The composition/layer pair is the runtime
/// identity used by live placement edits and remains stable when a clip fans one decode to several canvases.</summary>
public sealed record ShowPlacementEnvelope(
    string CompositionId,
    int LayerIndex,
    IReadOnlyList<ShowEnvelopePoint> Points,
    bool Absolute = false);

/// <summary>A numeric destination-geometry property which can be driven independently for one placement.</summary>
public enum ShowPlacementProperty
{
    DestX,
    DestY,
    DestWidth,
    DestHeight,
    RotationDegrees,
}

/// <summary>Absolute cue-time automation for one destination-geometry property on one placement.</summary>
public sealed record ShowPlacementPropertyEnvelope(
    string CompositionId,
    int LayerIndex,
    ShowPlacementProperty Property,
    IReadOnlyList<ShowEnvelopePoint> Points);

public enum ShowPlacementEffectProperty
{
    ChromaSimilarity,
    ChromaSmoothness,
    ChromaSpillReduction,
    ColorBrightness,
    ColorContrast,
}

/// <summary>Absolute automation for one parameter of one stable placement-effect instance.</summary>
public sealed record ShowPlacementEffectEnvelope(
    string CompositionId,
    int LayerIndex,
    string EffectInstanceId,
    ShowPlacementEffectProperty Property,
    IReadOnlyList<ShowEnvelopePoint> Points);

/// <summary>One audio output a clip plays on (GUI per-cue audio routing - a group of <c>CueAudioRoute</c>s to
/// the same output line). Unlike a per-group <see cref="ShowAudioOutput"/>, this is carried on the clip so a
/// cue plays on exactly its routed outputs. <see cref="ChannelMatrix"/> is the N→M <see cref="ChannelMap"/>
/// array (length = output channels, each entry = the source channel feeding it, -1 = silence); null = stereo.</summary>
public sealed record ShowClipAudioRoute(
    string? DeviceId = null,
    int[]? ChannelMatrix = null,
    float Gain = 1f,
    int? SampleRate = null)
{
    /// <summary>Optional full source→output gain matrix. When present it supersedes
    /// <see cref="ChannelMatrix"/> and preserves multiple source contributions to one output channel plus
    /// per-cell gain. Values are linear gains before the route-wide <see cref="Gain"/> envelope.</summary>
    public IReadOnlyList<ShowAudioMatrixCell>? MatrixCells { get; init; }

    /// <summary>Declared output channel count for <see cref="MatrixCells"/>. This keeps muted/unrouted trailing
    /// channels in the device format; null derives the count from the highest cell.</summary>
    public int? MatrixOutputChannels { get; init; }

    [JsonIgnore]
    public bool HasGainMatrix => MatrixCells is { Count: > 0 };

    public ChannelMap? ToChannelMap() => ChannelMatrix is { Length: > 0 } m ? new ChannelMap(m) : null;

    internal float[,] ToGainMatrix(float routeScale)
    {
        if (MatrixCells is not { Count: > 0 } cells)
            return new float[0, 0];
        var sourceChannels = cells.Max(c => c.InputChannel) + 1;
        var outputChannels = MatrixOutputChannels is > 0
            ? MatrixOutputChannels.Value
            : cells.Max(c => c.OutputChannel) + 1;
        var gains = new float[sourceChannels, outputChannels];
        foreach (var cell in cells)
        {
            if (cell.InputChannel < 0 || cell.OutputChannel < 0 || cell.OutputChannel >= outputChannels)
                throw new ArgumentException("audio matrix cell indices are outside the declared matrix.");
            gains[cell.InputChannel, cell.OutputChannel] += cell.Gain * routeScale;
        }
        return gains;
    }
}

public sealed record ShowAudioMatrixCell(int InputChannel, int OutputChannel, float Gain);

/// <summary>One cell of a clip's LOGICAL send matrix (HaCue two-matrix model): source channel
/// <paramref name="SourceChannel"/> feeds the project logical channel with the STABLE id
/// <paramref name="LogicalChannelId"/> at <paramref name="Gain"/>. Ids - not indices - so a send
/// survives logical-channel reorder; an id unknown to the session's program-audio target is logged
/// and skipped at fire time (the preflight validator owns authoring errors).</summary>
public sealed record ShowClipLogicalSend(int SourceChannel, string LogicalChannelId, float Gain = 1f);

/// <summary>
/// A playable clip: an id plus the media it plays. When something fires the clip, <see cref="MediaPath"/> is
/// opened through the session's <c>IMediaRegistry</c> (a bare path or a <c>scheme:</c> URI - D2) and played
/// on the addressed group.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClipId"/> is an opaque join key, not a cue reference. It is named that way because the engine
/// has two customers and only one of them has cues: a cue list binds a cue id to a clip of the same id,
/// while HaPlay's deck addresses clips directly and never had a cue in the first place (it used to invent
/// one). The core does not know which it is talking to, and should not.
/// </para>
/// <para>
/// The wire name stays <c>"CueId"</c>. This is a rename, not a change of meaning, and the sidecar format has
/// an external C ABI consumer plus every show already saved to disk - so the JSON must not move (§10.3:
/// never change the format unless the meaning of a member changes).
/// </para>
/// </remarks>
/// <param name="AudioStreamIndex">Audio track selection (03 §6 multi-track): <c>null</c> = automatic,
/// <c>-1</c> (<see cref="S.Media.Players.MediaPlayerOpenOptions.DisabledStreamIndex"/>) = no audio, otherwise
/// the chosen stream index. Lets a multi-track clip (e.g. language stems) pick which track this cue plays.</param>
/// <param name="SubtitlePath">Backward-compatible single sidecar subtitle path. Prefer <paramref name="Subtitles"/>
/// for explicit none/one/many selection and embedded stream selection.</param>
/// <param name="Subtitles">Selected subtitle tracks. An empty/null list means none unless <paramref name="SubtitlePath"/>
/// is set. A null selection path uses <see cref="MediaPath"/> as the container; <see cref="ShowSubtitleSelection.StreamIndex"/>
/// selects an embedded stream.</param>
public sealed record ShowClipBinding(
    [property: JsonPropertyName("CueId")] string ClipId,
    string MediaPath,
    string? CompositionId = null,
    int LayerIndex = 0,
    int? AudioStreamIndex = null,
    string? SubtitlePath = null,
    IReadOnlyList<ShowSubtitleSelection>? Subtitles = null)
{
    /// <summary>Video track selection: <c>null</c> = automatic election (which skips attached
    /// pictures), <c>-1</c> = no video, otherwise the chosen container stream index. An explicit index
    /// CAN select an attached-picture stream (embedded thumbnail / cover art).</summary>
    public int? VideoStreamIndex { get; init; }

    public IReadOnlyList<ShowSubtitleSelection> GetSubtitleSelections()
    {
        if (Subtitles is { Count: > 0 })
            return Subtitles;
        return string.IsNullOrWhiteSpace(SubtitlePath)
            ? []
            : [new ShowSubtitleSelection(SubtitlePath)];
    }

    // --- Clip playback parameters -----------------------------------------------------------------
    // A GUI media cue maps losslessly onto a ShowDocument. The playback runtime honours these:
    // ShowSession opens the clip with StartOffset/EndOffset as its trim window, drives FadeIn/FadeOut on
    // the route, and resolves Loop/EndBehavior against the end window. All are validated at load
    // (ShowDocumentValidator: non-negative offsets/fades, DOC-01). Values are immutable per document.

    /// <summary>Trim from the source start (GUI <c>MediaCueNode.StartOffsetMs</c>). Zero = from the head.</summary>
    public TimeSpan StartOffset { get; init; }

    /// <summary>Trim from the source end (GUI <c>MediaCueNode.EndOffsetMs</c>). Zero = through the probed duration.</summary>
    public TimeSpan EndOffset { get; init; }

    /// <summary>Fade-in at clip start (GUI <c>FadeInMs</c>).</summary>
    public TimeSpan FadeIn { get; init; }

    /// <summary>Gain curve for <see cref="FadeIn"/> (GUI <c>FadeInCurve</c>). Linear = pre-curve behavior.</summary>
    public FadeCurve FadeInCurve { get; init; } = FadeCurve.Linear;

    /// <summary>Optional user-drawn shape for the fade-in, overriding <see cref="FadeInCurve"/> when set.
    /// <para>Additive and nullable on purpose: a reader that does not know about it ignores the field and
    /// falls back to the enum law. Extending <see cref="FadeCurve"/> instead would be a silent breaking
    /// change, because enums round-trip numerically and the document's external reader (the C ABI host)
    /// would decode an unknown member as some other valid law.</para></summary>
    public CustomFadeCurve? FadeInShape { get; init; }

    /// <summary>Fade-out at clip end (GUI <c>FadeOutMs</c>).</summary>
    public TimeSpan FadeOut { get; init; }

    /// <summary>Gain curve for <see cref="FadeOut"/> - the natural fade-out AND the stop fade whenever the
    /// clip's own <see cref="FadeOut"/> wins the stop-duration precedence.</summary>
    public FadeCurve FadeOutCurve { get; init; } = FadeCurve.Linear;

    /// <summary>Optional user-drawn shape for the fade-out; see <see cref="FadeInShape"/>.</summary>
    public CustomFadeCurve? FadeOutShape { get; init; }

    /// <summary>Loop the trimmed clip (GUI <c>MediaCueNode.Loop</c>, also implied by <see cref="ClipEndBehavior.Loop"/>).</summary>
    public bool Loop { get; init; }

    /// <summary>Loop-with-crossfade window (GUI <c>MediaCueNode.LoopCrossfadeMs</c>; dual-voice design doc §3):
    /// when positive on a looping clip, each loop wrap is a dual-voice crossfade instead of the seek-back butt
    /// splice - within this window of the (trimmed) end the session re-fires the SAME binding as a fresh incoming
    /// voice through the crossfade replacement path, so the tail of one pass overlaps the head of the next
    /// (ambient beds). Must be shorter than the trimmed pass length; zero (the default) keeps the seamless-seek
    /// loop unchanged. Ignored for non-looping clips.</summary>
    public TimeSpan LoopCrossfade { get; init; }

    /// <summary>What happens when the clip reaches its (trimmed) end (GUI <c>MediaCueNode.EndBehavior</c>).</summary>
    public ClipEndBehavior EndBehavior { get; init; } = ClipEndBehavior.Stop;

    /// <summary>
    /// Keep this clip OUT of pre-roll (GUI <c>MediaCueNode.DisablePreRoll</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ShowSession.WarmUpcomingAsync"/> opens the next few cues' media so the next GO is
    /// instant. That is a decoder held open per warmed cue, and a few clips are worth exempting: a
    /// 4 K master on a slow disk that costs more to hold than it saves, a live/network source whose
    /// open starts a connection nobody wants running early, or a device a warm would take exclusively
    /// long before the show needs it.
    /// </para>
    /// <para>
    /// It excludes the clip from the WARM only. The cue still fires normally — it simply opens at that
    /// moment, which is where every cue was before pre-roll existed.
    /// </para>
    /// </remarks>
    public bool DisablePreRoll { get; init; }

    /// <summary>Run the end-of-clip monitor purely off the reported <see cref="StartOffset"/>→duration window even
    /// for a plain <see cref="ClipEndBehavior.Stop"/> with no trim/fade/loop. Set for a <em>held</em> source (a
    /// rendered text or still cue) that never signals EOF on its own: the clip is stopped at its duration by the
    /// time-based monitor instead of by source exhaustion, so a resize/live-edit re-read can't end it early.</summary>
    public bool EndAtDuration { get; init; }

    /// <summary>Monitor a plain <see cref="ClipEndBehavior.Stop"/> file clip with no trim/fade/loop for its
    /// natural end: release the clip and raise <c>ShowSession.ClipNaturallyEnded</c> when it plays through -
    /// at the duration out-point, or when its (finite, audio-clocked) playback stalls at source EOF short of
    /// the metadata duration. Opt-in per clip: set it for real file cues that drive cue auto-follow; leave it
    /// off for held/live sources (their clock legitimately idles while the clip is up) and for hosts that
    /// poll and advance themselves (the media-player deck).</summary>
    public bool NotifyNaturalEnd { get; init; }

    /// <summary>Pre-end notification window (dual-voice crossfade, <c>Ideas/Dual-Voice-Crossfade-Design.md</c>):
    /// when positive, the end-of-clip monitor raises <c>ShowSession.ClipApproachingEnd</c> once when the clip is
    /// within this window of its natural out-point - the host's "fire the next item early" hook (HaPlay's
    /// playlist <c>CrossfadeMs</c>). One-shot per committed clip (a later backwards seek does not re-arm it);
    /// never raised for looping/freezing clips. Zero (the default) raises nothing - behavior unchanged.</summary>
    public TimeSpan PreEndNotify { get; init; }

    /// <summary>Where/how this clip's video sits on its <see cref="CompositionId"/> canvas (GUI
    /// <c>CueVideoPlacement</c>). Null ⇒ full-canvas, opaque, Cover (the prior hardcoded placement).</summary>
    public ShowVideoPlacement? Placement { get; init; }

    /// <summary>Composition placements <em>beyond</em> the primary (<see cref="CompositionId"/>/<see cref="LayerIndex"/>/
    /// <see cref="Placement"/>). When set, the clip's one decoded video is fanned to every placement here as well as
    /// the primary - each its own composition layer. Empty/null ⇒ the clip appears only on the primary composition.
    /// Use <see cref="GetPlacements"/> for the layer-ordered effective set.</summary>
    public IReadOnlyList<ShowClipPlacement>? ExtraPlacements { get; init; }

    /// <summary>The layer-ordered effective set of composition placements for this clip: the primary
    /// (<see cref="CompositionId"/>) plus any <see cref="ExtraPlacements"/>, sorted by layer index. Empty when the
    /// clip targets no composition (audio-only). The one decoded source is fanned to every entry at commit time.</summary>
    public IReadOnlyList<ShowClipPlacement> GetPlacements()
    {
        var primary = CompositionId is { } id ? new ShowClipPlacement(id, LayerIndex, Placement) : null;
        if (ExtraPlacements is not { Count: > 0 })
            return primary is null ? [] : [primary];
        var all = new List<ShowClipPlacement>(ExtraPlacements.Count + 1);
        if (primary is not null)
            all.Add(primary);
        all.AddRange(ExtraPlacements);
        all.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return all;
    }

    /// <summary>Per-clip audio output routing (GUI per-cue <c>CueAudioRoute</c>s, one entry per output line).
    /// Non-empty plays on exactly these outputs; an empty list is explicitly silent; <see langword="null"/>
    /// inherits the show/group outputs (including the standalone session's implicit master fallback).</summary>
    public IReadOnlyList<ShowClipAudioRoute>? AudioRoutes { get; init; }

    /// <summary>Logical program sends (HaCue two-matrix model): the clip's N×V matrix onto the
    /// project's logical channels, applied when the session has an <see cref="IShowProgramAudioTarget"/>.
    /// Takes precedence over <see cref="AudioRoutes"/> there; an empty list is explicitly silent.
    /// Null - or a session with no program-audio target - falls back to the v1 direct-route adapter
    /// (<see cref="AudioRoutes"/> / group outputs) unchanged.</summary>
    public IReadOnlyList<ShowClipLogicalSend>? LogicalSends { get; init; }

    /// <summary>Volume-automation keyframes, sorted by time.
    /// Times are CLIP positions (post-<see cref="StartOffset"/>), so the envelope survives seeks and
    /// restarts on every loop pass. The envelope factor MULTIPLIES the fade level (fade-in/out, fade
    /// cue, stop fade) - it never replaces it. Null/empty = no automation (and no runner started).</summary>
    public IReadOnlyList<ShowEnvelopePoint>? VolumeEnvelope { get; init; }

    /// <summary>Legacy opacity-factor keyframes for all of this clip's video layers, sorted by time. Times are CLIP
    /// positions on the same basis as <see cref="VolumeEnvelope"/>, and the factor composes the same way:
    /// it MULTIPLIES each layer's authored opacity and whatever a fade has reached, so automation, fades
    /// and live placement edits coexist instead of overwriting one another.
    /// <para>Levels are clamped to [0, 1] - unlike gain there is no headroom above full, so a point above
    /// 1 is a clamp rather than an error. Null/empty = no automation (and no runner started).</para></summary>
    public IReadOnlyList<ShowEnvelopePoint>? OpacityEnvelope { get; init; }

    /// <summary>Placement-addressed opacity automation. Each envelope declares whether its sampled level
    /// is the absolute authored opacity or a legacy multiplier. This supersedes <see cref="OpacityEnvelope"/>,
    /// which remains readable for version-1 documents and applies its one factor to every placement.</summary>
    public IReadOnlyList<ShowPlacementEnvelope>? PlacementOpacityEnvelopes { get; init; }

    /// <summary>Placement-addressed destination geometry. Each property has an independent slot, so
    /// animating X cannot reset an authored/live-edited Y, size, rotation, opacity, crop, or effect.</summary>
    public IReadOnlyList<ShowPlacementPropertyEnvelope>? PlacementTransformEnvelopes { get; init; }

    public IReadOnlyList<ShowPlacementEffectEnvelope>? PlacementEffectEnvelopes { get; init; }
}

/// <summary>One volume-envelope keyframe: the clip-relative <paramref name="Time"/>, the LINEAR gain
/// factor <paramref name="Level"/> (0 = silence; may exceed 1 up to +12 dB - the GUI/mapper converts dB),
/// and the curve shaping the segment from this point to the next (<see cref="VolumeEnvelopes.Sample"/>).</summary>
public sealed record ShowEnvelopePoint(TimeSpan Time, float Level, FadeCurve CurveToNext = FadeCurve.Linear);

/// <summary>A selected subtitle source. <paramref name="Path"/> null means the clip's media container;
/// <paramref name="StreamIndex"/> <c>-1</c> selects the best subtitle stream.</summary>
public sealed record ShowSubtitleSelection(string? Path = null, int StreamIndex = -1);

/// <summary>A composition canvas a clip's video can be placed onto (maps to a <c>ClipCompositionRuntime</c>).
/// <paramref name="OutputMapping"/> cuts the composited canvas into placed sections for the output (projector
/// keystone / multi-panel tiling) - affine sections composite headless on the CPU backend; mesh warp is GL.</summary>
public sealed record ShowComposition(
    string Id,
    string Name,
    int Width,
    int Height,
    int FrameRateNum = 30,
    int FrameRateDen = 1,
    ClipOutputMappingSpec? OutputMapping = null);

/// <summary>An audio output endpoint a transport group plays on (D11 per-group outputs; declare more than one
/// for a multi-output / multi-device group). <paramref name="DeviceId"/> null = the backend's default device;
/// the per-output N→M channel remap comes from the matching <see cref="OutputPatchRoute"/> (<c>OutputId</c> ==
/// <see cref="Id"/>, <c>SourceId</c> == the clip binding's cue id). The first output of a group is its master
/// (drives the clock); the rest auto-slave.</summary>
public sealed record ShowAudioOutput(
    string Id,
    string? DeviceId = null,
    string GroupId = "main"); // = ShowSession.DefaultGroup

/// <summary>
/// The headless, serializable definition of a show - cues, the media each cue plays, and the output patch.
/// Source-generated (<see cref="ShowDocumentJsonContext"/>) so it loads with no reflection (D10, AOT-safe),
/// and carries no Avalonia/UI state (the UI persists view-state separately on top of this).
/// </summary>
public sealed record ShowDocument(
    int Version,
    IReadOnlyList<CueDefinition> Cues,
    IReadOnlyList<ShowClipBinding> Clips,
    IReadOnlyList<ShowComposition> Compositions,
    IReadOnlyList<OutputPatchRoute> Routes)
{
    /// <summary>Per-group audio output endpoints (D11). Empty ⇒ each group plays on one implicit master
    /// output (<see cref="ShowSession.MasterOutputId"/>) on the backend default device; declare entries to
    /// drive several outputs/devices per group (each with its own N→M route).</summary>
    public IReadOnlyList<ShowAudioOutput> AudioOutputs { get; init; } = [];

    /// <summary>An empty version-1 show.</summary>
    public static ShowDocument Empty { get; } = new(1, [], [], [], []);

    /// <summary>Serializes to indented JSON via the source-generated context (no reflection - D10).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ShowDocumentJsonContext.Default.ShowDocument);

    /// <summary>Loads a show from JSON via the source-generated context (headless, AOT-safe - D10).</summary>
    public static ShowDocument FromJson(string json) =>
        JsonSerializer.Deserialize(json, ShowDocumentJsonContext.Default.ShowDocument)
        ?? throw new InvalidOperationException("show document JSON was empty or invalid.");
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ShowDocument))]
[JsonSerializable(typeof(ShowClipBinding))]
[JsonSerializable(typeof(ShowPlacementEnvelope))]
[JsonSerializable(typeof(ShowComposition))]
// Standalone root for the geometry-effect registry factory (OutputMappingGeometryEffect.FromJson).
[JsonSerializable(typeof(ClipOutputMappingSpec))]
internal partial class ShowDocumentJsonContext : JsonSerializerContext;
