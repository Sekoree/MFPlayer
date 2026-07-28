using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>Bundle of every cue list in the cue player workspace, persisted in <c>.haplaycuelists</c> files.</summary>
public sealed record CueListsCollectionDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? Generator { get; init; }

    public List<CueList> CueLists { get; init; } = [];
}

/// <summary>
/// Cue-list root persisted in <c>.haplaycues</c> files. A cue list is a tree of groups and cues
/// plus its own list of compositions and outputs - the Cue Player is a self-contained playback
/// surface and does not borrow routing from MediaPlayer tabs.
/// </summary>
public sealed record CueList
{
    public string Schema { get; init; } = "HaPlayCueList/v3";

    public string Name { get; init; } = "Cue List";

    /// <summary>Legacy persisted standby-window cap. Ignored by the current cue runtime; all
    /// upcoming standby targets are prepared.</summary>
    public int PreRollCount { get; init; }

    /// <summary>Legacy persisted standby-decoder cap. Ignored by the current cue runtime; decoder
    /// preparation uses as many decoders as the standby window needs.</summary>
    public int MaxPreparedDecoders { get; init; }

    /// <summary>Trigger mode applied to cues created via the toolbar (Phase 5.8.2). Default
    /// <see cref="CueTriggerMode.Manual"/> so older lists load unchanged.</summary>
    public CueTriggerMode DefaultTriggerMode { get; init; } = CueTriggerMode.Manual;

    /// <summary>When true, the cue player re-runs the renumber pass after every insert/reorder
    /// so the operator's numbering stays sequential (Phase 5.8.2). Default off - preserves the
    /// pre-5.8 behavior where operators set numbers themselves.</summary>
    public bool AutoRenumberOnInsert { get; init; }

    /// <summary>Stop-fade length (ms) for the transport Stop on this list, applied to clips without
    /// their own <see cref="MediaCueNode.FadeOutMs"/>. Null (older files) = the app-settings default
    /// (<c>AppSettings.StopFadeMs</c>); 0 = hard cut.</summary>
    public int? StopFadeMs { get; init; }

    /// <summary>Gain curve for this list's stop fade. Default Linear so older files load unchanged.</summary>
    public CueFadeCurve StopFadeCurve { get; init; } = CueFadeCurve.Linear;

    /// <summary>Virtual canvases used by the cue player. Multiple video outputs may reference the
    /// same composition (fan-out: composition is rendered once, fed to every referencing output).</summary>
    public List<CueComposition> Compositions { get; init; } = new();

    /// <summary>Video output bindings - each pairs an output line id (from the shared
    /// <c>OutputManagementView</c> registry) with the composition that feeds it. Audio outputs
    /// are referenced directly by id from <see cref="CueAudioRoute"/> entries, so no per-cue-list
    /// audio binding is needed.</summary>
    public List<CueVideoOutputBinding> VideoOutputs { get; init; } = new();

    public List<CueNode> Nodes { get; init; } = new();
}

public sealed record CueComposition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int FrameRateNum { get; init; } = 60;

    public int FrameRateDen { get; init; } = 1;

    /// <summary>Optional composition-level video FX mapping applied to the full canvas before it
    /// fans out to output mappings. Null = no extra composition stage.</summary>
    public CueOutputMapping? VideoFx { get; init; }

    /// <summary>Whether <see cref="VideoFx"/> is active. Geometry is retained while disabled.</summary>
    public bool VideoFxEnabled { get; init; }

    /// <summary>Runs a projectM audio visualizer on this composition as a persistent full-canvas layer.
    /// Because a cue composition persists across every cue fire, the visualizer runs CONTINUOUSLY while the
    /// cue list plays - each fired clip's audio feeds it via a session tap. Absent in older projects
    /// (deserializes false).</summary>
    public bool VisualizerEnabled { get; init; }

    /// <summary>Optional *.milk preset folder for this composition's visualizer (null = built-in idle preset).</summary>
    public string? VisualizerPresetDirectory { get; init; }
}

public sealed record CueVideoOutputBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Id of an output line in the shared <c>OutputManagementView</c> registry
    /// (matches <c>OutputDefinition.Id</c>). Empty when not yet picked.</summary>
    public Guid OutputLineId { get; init; }

    /// <summary>Composition (from <see cref="CueList.Compositions"/>) that feeds this output.</summary>
    public Guid CompositionId { get; init; }

    /// <summary>Optional output mapping (warp sections) for this output - the composited canvas is
    /// cut into sections placed individually in output space (projection onto uneven/multi-panel
    /// surfaces). Null = no mapping stage (identical pipeline and cost to before the feature).
    /// See Doc/HaPlay-Output-Mapping-Plan.md.</summary>
    public CueOutputMapping? Mapping { get; init; }

    /// <summary>Whether <see cref="Mapping"/> is active. The geometry is retained when this is false so
    /// toggling mapping off then on restores the exact configured slice instead of losing it to a null
    /// mapping. Mapping applies only when this is <c>true</c> <em>and</em> <see cref="Mapping"/> is
    /// non-null. Defaults <c>true</c> so pre-flag saves (which stored a mapping only when they wanted it
    /// active) load unchanged.</summary>
    public bool MappingEnabled { get; init; } = true;
}

/// <summary>Output mapping for one composition→output binding (Doc/HaPlay-Output-Mapping-Plan.md §3).</summary>
public sealed record CueOutputMapping
{
    /// <summary>Sections drawn back-to-front onto the output. Empty = nothing drawn (all black);
    /// use a single full-canvas section for identity.</summary>
    public List<CueOutputMappingSection> Sections { get; init; } = new();

    /// <summary>Output canvas size; null = composition size.</summary>
    public int? OutputWidth { get; init; }

    public int? OutputHeight { get; init; }

    /// <summary>A fresh identity mapping: one full-canvas section.</summary>
    public static CueOutputMapping Identity() => new()
    {
        Sections = { CueOutputMappingSection.FullCanvas() },
    };
}

/// <summary>One mapping section: a normalized source slice of the composition canvas plus an
/// affine destination placement (output pixels, rotation around the destination center).</summary>
public sealed record CueOutputMappingSection
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>Source slice, normalized [0,1] canvas coordinates.</summary>
    public double SrcX { get; init; }

    public double SrcY { get; init; }

    public double SrcWidth { get; init; } = 1.0;

    public double SrcHeight { get; init; } = 1.0;

    /// <summary>Destination placement in output pixels. Width/height ≤ 0 = natural slice size.</summary>
    public double DestX { get; init; }

    public double DestY { get; init; }

    public double DestWidth { get; init; }

    public double DestHeight { get; init; }

    /// <summary>Rotation around the destination rect center, degrees clockwise.</summary>
    public double RotationDegrees { get; init; }

    /// <summary>Per-section alpha multiplier [0,1].</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>Per-section brightness [0,1] - panel brightness matching.</summary>
    public double Brightness { get; init; } = 1.0;

    /// <summary>Reserved for Phase 3 corner-pin (TL, TR, BR, BL in output pixels); ignored in Phase 1.</summary>
    public List<CuePoint>? Corners { get; init; }

    /// <summary>Mesh warp control grid columns (Phase 4 - projection onto non-flat surfaces);
    /// 0 = no mesh, otherwise ≥ 2 with <see cref="MeshRows"/> and a matching
    /// <see cref="MeshPoints"/> count.</summary>
    public int MeshColumns { get; init; }

    public int MeshRows { get; init; }

    /// <summary>Row-major mesh control points in normalized dest-rect space ((0,0) = the un-warped
    /// rect's TL, (1,1) = BR; values may overshoot [0,1]). Relative storage means moving/scaling/
    /// rotating the section carries its warp along. An identity grid renders as pure affine.</summary>
    public List<CuePoint>? MeshPoints { get; init; }

    /// <summary>The identity control grid for <paramref name="columns"/>×<paramref name="rows"/> -
    /// every point on its un-warped grid position.</summary>
    public static List<CuePoint> IdentityMeshPoints(int columns, int rows)
    {
        var points = new List<CuePoint>(columns * rows);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
                points.Add(new CuePoint(c / (double)(columns - 1), r / (double)(rows - 1)));
        }

        return points;
    }

    public static CueOutputMappingSection FullCanvas() => new() { Name = "Full canvas" };
}

public sealed record CuePoint(double X, double Y);

public enum CueLayerPosition
{
    Cover,
    Letterbox,
    Center,
    FillWidth,
    FillHeight,
    Stretch,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CueGroupNode), typeDiscriminator: "group")]
[JsonDerivedType(typeof(MediaCueNode), typeDiscriminator: "media")]
[JsonDerivedType(typeof(ActionCueNode), typeDiscriminator: "action")]
[JsonDerivedType(typeof(CommentCueNode), typeDiscriminator: "comment")]
[JsonDerivedType(typeof(JumpCueNode), typeDiscriminator: "jump")]
[JsonDerivedType(typeof(VisualizerCueNode), typeDiscriminator: "visualizer")]
[JsonDerivedType(typeof(FadeCueNode), typeDiscriminator: "fade")]
public abstract record CueNode
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Number { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public CueTriggerMode TriggerMode { get; init; } = CueTriggerMode.Manual;

    public int PreWaitMs { get; init; }

    public string? Notes { get; init; }

    /// <summary>Color tag index 0..7 - 0 = none, 1..7 map to a fixed palette in
    /// <c>CueColorTagPalette</c>. Default-safe for pre-5.8 files (loads as 0 / no tag).</summary>
    public int ColorTag { get; init; }

    /// <summary>Authored start offset (ms) on the parent group's plan epoch - meaningful only inside a
    /// <see cref="CueGroupFireMode.Timeline"/> group. Default 0 (the CLR default, so <c>init</c> is safe
    /// under the source-generated serializer) keeps pre-timeline files loading unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TimelineStartMs { get; init; }

    /// <summary>Optional wall-clock trigger (Ideas/CuePlayer-Enhancements.md §4). A schedule is an
    /// ADDITIONAL trigger on the cue, not a replacement for <see cref="TriggerMode"/> - a scheduled
    /// cue can still be fired manually. Null = no schedule; older files load (and re-save) unchanged
    /// (null is the CLR default, so <c>init</c> is safe under the source-generated serializer, and
    /// the context's WhenWritingNull policy keeps the field out of the JSON).</summary>
    public CueSchedule? Schedule { get; init; }

    /// <summary>Optional per-cue fire hotkey (Ideas/CuePlayer-Enhancements.md §6) - a
    /// <see cref="CueHotkeyGesture"/> text such as "F5" or "Ctrl+K", captured in the drawer's
    /// Triggers section. Fires the cue through the operator-selected GO path while cue edit mode is
    /// OFF (the schedule gate's reasoning); the configurable transport keys win a clash. Null = no
    /// hotkey - the CLR default, so <c>init</c> is safe under the source-generated serializer, and
    /// the context's WhenWritingNull policy keeps legacy cue JSON byte-identical.</summary>
    public string? HotkeyGesture { get; init; }
}

/// <summary>Wall-clock schedule on a cue (Ideas/CuePlayer-Enhancements.md §4). Times are LOCAL
/// wall-clock - shows are local-time creatures. <see cref="TimeOfDay"/> is stored as local time;
/// the one-shot <see cref="At"/> is a <see cref="DateTimeOffset"/> so it survives timezone moves.
/// Recurring times resolve against local wall time each day, so a DST-skipped/duplicated hour
/// follows the OS clock (documented behavior - the scheduler does not fight it).
/// <para>GOTCHA: properties use <c>set</c>, not <c>init</c>, deliberately. The source-generated
/// serializer assigns EVERY init property through one object initializer, so fields absent from the
/// JSON would load as CLR defaults (GraceMs 0, Enabled false) instead of these property
/// initializers - a minimal <c>"schedule":{}</c> must keep GraceMs 5000 / Enabled true. See the
/// <see cref="FadeCueNode"/> doc note.</para></summary>
public sealed record CueSchedule
{
    /// <summary>What kind of occurrence this schedule produces.</summary>
    public CueScheduleKind Kind { get; set; }

    /// <summary>Local wall-clock time for <see cref="CueScheduleKind.TimeOfDay"/> (daily) and
    /// <see cref="CueScheduleKind.Recurring"/>; null for one-shots.</summary>
    public TimeOnly? TimeOfDay { get; set; }

    /// <summary>Absolute one-shot instant for <see cref="CueScheduleKind.DateTime"/>; null otherwise.</summary>
    public DateTimeOffset? At { get; set; }

    /// <summary>Days a <see cref="CueScheduleKind.Recurring"/> schedule fires on. None = never
    /// (an empty recurring schedule is inert, not "every day" - that is what TimeOfDay is for).</summary>
    public CueScheduleDays Days { get; set; }

    /// <summary>Late-fire window (ms): an occurrence due within [due, due+GraceMs] still fires once;
    /// anything older is skipped and logged (app sleep/suspend recovery - a backlog is never
    /// caught up).</summary>
    public int GraceMs { get; set; } = 5000;

    /// <summary>Per-schedule enable. Settings are retained while disabled (the VideoFx pattern).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Occurrence kind of a <see cref="CueSchedule"/>.</summary>
public enum CueScheduleKind
{
    /// <summary>Every day at <see cref="CueSchedule.TimeOfDay"/> (local).</summary>
    TimeOfDay,
    /// <summary>Once, at the absolute instant <see cref="CueSchedule.At"/>.</summary>
    DateTime,
    /// <summary>At <see cref="CueSchedule.TimeOfDay"/> on the days in <see cref="CueSchedule.Days"/>.</summary>
    Recurring,
}

/// <summary>Day-of-week mask for recurring schedules.</summary>
[Flags]
public enum CueScheduleDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
}

public sealed record CueGroupNode : CueNode
{
    public CueGroupFireMode FireMode { get; init; } = CueGroupFireMode.FirstCueOnly;

    /// <summary>Playlist options, used when <see cref="FireMode"/> is
    /// <see cref="CueGroupFireMode.Playlist"/> or <see cref="CueGroupFireMode.ArmedList"/>.
    /// Null (older files / never configured) = the <see cref="CuePlaylistOptions"/> defaults.
    /// Retained while the group is in another fire mode (the VideoFx retained-while-disabled
    /// pattern) so toggling modes never loses the operator's configuration.</summary>
    public CuePlaylistOptions? Playlist { get; init; }

    public List<CueNode> Children { get; init; } = new();
}

/// <summary>Playlist behavior for a <see cref="CueGroupNode"/> (Ideas/CuePlayer-Enhancements.md §3).
/// Playlist mode auto-advances on each child's natural end; ArmedList shares this runtime but only
/// advances on GO. All shuffle/pass state is session-only (never persisted) - loading a project
/// starts every playlist afresh.
/// <para>GOTCHA: properties use <c>set</c>, not <c>init</c>, deliberately. The source-generated
/// serializer assigns EVERY init property through one object initializer, so fields absent from the
/// JSON would load as CLR defaults (LoopCount 0, AvoidImmediateRepeat/ReshuffleEachPass false)
/// instead of these property initializers. See the <see cref="FadeCueNode"/> doc note.</para></summary>
public sealed record CuePlaylistOptions
{
    /// <summary>Draw children WITHOUT replacement from a bag (every child once per pass) instead of
    /// playing them in tree order.</summary>
    public bool Shuffle { get; set; }

    /// <summary>Never open a pass with the child that just closed the previous pass when another
    /// child is available (the reshuffled-pass-boundary guard).</summary>
    public bool AvoidImmediateRepeat { get; set; } = true;

    /// <summary>Number of passes to play: 0 = infinite, N = that many passes.</summary>
    public int LoopCount { get; set; } = 1;

    /// <summary>Play only this many items per pass (a subset); null = every child each pass.</summary>
    public int? PlayCount { get; set; }

    /// <summary>Reshuffle the bag on every pass boundary; false keeps the first pass's shuffled
    /// order for all passes. Only meaningful when <see cref="Shuffle"/> is on.</summary>
    public bool ReshuffleEachPass { get; set; } = true;

    /// <summary>Crossfade window (ms) between consecutive Playlist items: the next pick fires this
    /// long BEFORE the current item's natural end and both clips overlap - the outgoing one fades
    /// out under the incoming one (the framework's dual-voice crossfade,
    /// <c>Ideas/Dual-Voice-Crossfade-Design.md</c>). 0 (the default, and every older file) = butt
    /// splice - the historical advance-on-natural-end path, unchanged. Playlist mode only; an
    /// ArmedList advances on GO and ignores it.</summary>
    public int CrossfadeMs { get; set; }

    /// <summary>What happens when the final pass completes (Playlist mode's auto-run end).</summary>
    public CuePlaylistEndBehavior EndBehavior { get; set; }
}

/// <summary>End-of-run behavior for a playlist group.</summary>
public enum CuePlaylistEndBehavior
{
    /// <summary>Stop: nothing further fires; standby returns to the group so GO restarts it.</summary>
    Stop,
    /// <summary>Standby (and Auto-Follow, per the normal boundary gating) the next cue after the group.</summary>
    AdvancePastGroup,
    /// <summary>Leave the transport exactly where it is (held/freeze-frame clips keep showing).</summary>
    Hold,
}

/// <summary>
/// One subtitle track selected for a media cue. Exactly one of <see cref="StreamIndex"/> (an embedded container
/// subtitle stream - see <c>MediaStreamInfo.Index</c>) or <see cref="Path"/> (a sidecar file) identifies the
/// source. The optional overrides apply to text formats that support styling (ASS, or any format FFmpeg decodes
/// to ASS events); they are ignored for bitmap (PGS/DVB) subtitles.
/// </summary>
public sealed record CueSubtitleSelection
{
    /// <summary>Embedded container stream index; <c>null</c> for a sidecar selection.</summary>
    public int? StreamIndex { get; init; }

    /// <summary>Sidecar subtitle file path; <c>null</c> for an embedded selection.</summary>
    public string? Path { get; init; }

    /// <summary>Display label for the picker (language / title / codec).</summary>
    public string? Label { get; init; }

    /// <summary>Override font family (libass fallback family); <c>null</c> keeps the document's styling.</summary>
    public string? FontFamily { get; init; }

    /// <summary>Font size scale (1.0 = document default); <c>null</c> keeps the document's sizing.</summary>
    public double? FontScale { get; init; }

    /// <summary>libass numpad alignment 1–9 (e.g. 2 = bottom-center); <c>null</c> keeps the document's alignment.</summary>
    public int? Alignment { get; init; }

    /// <summary>True for an embedded container stream, false for a sidecar file.</summary>
    public bool IsEmbedded => StreamIndex.HasValue;
}

public sealed record MediaCueNode : CueNode
{
    /// <summary>FX send (#26): when a selective-feed visualizer cue is active, this media cue's audio
    /// also drives it (in addition to the visualizer cue's own FeedCueIds). Absent in older files.</summary>
    public bool SendToVisualizer { get; init; }

    /// <summary>On natural end, fire THIS cue (stable id; null = default behaviour: the next cue's
    /// Auto-Follow trigger, if set). Lets a chain jump anywhere - "after this song, go to Q12".</summary>
    public Guid? EndTargetCueId { get; init; }

    public PlaylistItem? Source { get; init; }

    public int DurationMs { get; init; }

    /// <summary>Cached probe result - whether the source has a decodable video stream. Defaults
    /// false; older saved cues (pre-Phase 5.1) load with this unset and the Video tab still shows
    /// until the operator re-probes by re-browsing the source.</summary>
    public bool HasVideo { get; init; }

    /// <summary>Cached probe result - whether the source has a decodable audio stream.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Source channel count probed once on add. 0 when unknown / no audio.</summary>
    public int AudioChannels { get; init; }

    /// <summary>
    /// Explicit audio track for multi-track sources (container stream index, see
    /// <c>MediaStreamInfo.Index</c>). <c>null</c> = automatic election. The demuxer falls back to
    /// automatic when the index is stale, so an old choice can never make a cue unplayable.
    /// </summary>
    public int? AudioTrackIndex { get; init; }

    /// <summary>Content signature of the chosen audio track at pick time (codec/language/channels).
    /// Guards <see cref="AudioTrackIndex"/> against re-muxed files whose stream indices shifted -
    /// on mismatch the engine re-resolves by signature or falls back to automatic.</summary>
    public string? AudioTrackSignature { get; init; }

    /// <summary>
    /// Explicit video track for multi-stream sources (container stream index). <c>null</c> =
    /// automatic election (which skips attached pictures). An explicit index CAN select an
    /// attached-picture stream - e.g. a YouTube asset's embedded thumbnail or MP3 cover art.
    /// The demuxer falls back to automatic when the index is stale.
    /// </summary>
    public int? VideoTrackIndex { get; init; }

    /// <summary>Content signature of the chosen video track at pick time (codec/resolution) -
    /// same re-mux guard as <see cref="AudioTrackSignature"/>.</summary>
    public string? VideoTrackSignature { get; init; }

    /// <summary>True when the source's only video is an attached picture (e.g. MP3 with cover art).
    /// The Video tab still shows so the cover art can be placed into a composition, but with a
    /// hint that it's a still image.</summary>
    public bool VideoIsAttachedPicture { get; init; }

    /// <summary>Subtitle tracks to render over this cue's video - none / one / many. Each is an embedded
    /// container stream (<see cref="CueSubtitleSelection.StreamIndex"/>) or a sidecar file
    /// (<see cref="CueSubtitleSelection.Path"/>), with optional font/placement overrides. Empty = no subtitles.</summary>
    public IReadOnlyList<CueSubtitleSelection> Subtitles { get; init; } = [];

    /// <summary>Probed source frame rate (numerator / denominator). 0/0 when unknown or no video.</summary>
    public int SourceFrameRateNum { get; init; }

    public int SourceFrameRateDen { get; init; }

    /// <summary>Probed source video pixel dimensions. 0 when unknown / no video. Used to size a new
    /// composition placement to the source (actual size, scaled down to fit the canvas).</summary>
    public int SourceVideoWidth { get; init; }

    public int SourceVideoHeight { get; init; }

    public bool Loop { get; init; }

    public int StartOffsetMs { get; init; }

    /// <summary>Amount trimmed from the end of the source. 0 means play through the probed duration.</summary>
    public int EndOffsetMs { get; init; }

    public CueEndBehavior EndBehavior { get; init; } = CueEndBehavior.Stop;

    public int FadeInMs { get; init; }

    public int FadeOutMs { get; init; }

    /// <summary>Gain curve for <see cref="FadeInMs"/>. Default Linear - older files load unchanged.</summary>
    public CueFadeCurve FadeInCurve { get; init; } = CueFadeCurve.Linear;

    /// <summary>Gain curve for <see cref="FadeOutMs"/> - also used when this cue's fade-out wins the
    /// stop-fade precedence (per-cue &gt; list <see cref="CueList.StopFadeMs"/> &gt; app default).</summary>
    public CueFadeCurve FadeOutCurve { get; init; } = CueFadeCurve.Linear;

    /// <summary>Per-cue master level (dB, default 0 = unity so older files load unchanged). Multiplies
    /// EVERY audio route of the cue on top of the per-route <see cref="CueAudioRoute.GainDb"/> - the
    /// "how loud is this cue overall" trim the review's §6 asked for, and the anchor fade cues and
    /// envelopes compose against (they multiply the routed gains, which carry this level). Clamped to
    /// −60..+12 at edit time; at or below −60 dB the cue is routed silent.</summary>
    public double LevelDb { get; init; }

    /// <summary>Legacy persisted per-cue pre-roll opt-out. Ignored by the current cue runtime.</summary>
    public bool DisablePreRoll { get; init; }

    /// <summary>Per-source-channel audio routing - picks a cue audio output + a device channel
    /// directly. Replaces the previous virtual-output + route-override model.</summary>
    public List<CueAudioRoute> AudioRoutes { get; init; } = new();

    /// <summary>Per-composition appearance - layer index, position preset, opacity.</summary>
    public List<CueVideoPlacement> VideoPlacements { get; init; } = new();

    /// <summary>Volume-automation keyframes (Ideas/CuePlayer-Timeline-Editor.md Phase B), sorted by
    /// time. Times are CLIP-relative (post-<see cref="StartOffsetMs"/>) so the envelope survives seeks
    /// and restarts per loop pass; at runtime the envelope MULTIPLIES the fades, never replaces them.
    /// Empty = no automation - older files load (and behave) unchanged.
    /// <para>GOTCHA: <c>set</c>, not <c>init</c>, deliberately - the non-null <c>[]</c> default is a
    /// non-CLR default, and the source-generated serializer assigns EVERY init property (JSON-absent
    /// init fields load as CLR defaults, here null). See the <see cref="FadeCueNode"/> doc note.</para></summary>
    public IReadOnlyList<CueAutomationPoint> VolumeEnvelope { get; set; } = [];
}

/// <summary>One volume-automation keyframe on a media cue. <see cref="TimeMs"/> is CLIP-relative
/// (post-StartOffset); <see cref="LevelDb"/> is clamped to −60..+12 at edit time (at or below −60 dB =
/// silence); <see cref="CurveToNext"/> shapes the segment from this point to the next. All property
/// defaults are CLR defaults (0 / Linear = 0), so <c>init</c> is safe under the source-generated
/// serializer - add any non-CLR default with <c>set</c> (see the <see cref="FadeCueNode"/> doc note).</summary>
public sealed record CueAutomationPoint
{
    /// <summary>The silence floor: levels at or below this map to zero gain.</summary>
    public const double SilenceLevelDb = -60;

    /// <summary>The boost ceiling levels are clamped to.</summary>
    public const double MaxLevelDb = 12;

    public int TimeMs { get; init; }

    public double LevelDb { get; init; }

    public CueFadeCurve CurveToNext { get; init; } = CueFadeCurve.Linear;
}

public sealed record ActionCueNode : CueNode
{
    public CueActionKind ActionKind { get; init; } = CueActionKind.OSCOut;

    public Guid? EndpointId { get; init; }

    public string AddressOrMessage { get; init; } = string.Empty;

    public List<string> Arguments { get; init; } = new();
}

public sealed record CommentCueNode : CueNode
{
    public string Text { get; init; } = string.Empty;
}

/// <summary>Visualizer control cue (#26): firing it STARTS (or stops) the projectM visualizer as a
/// placeable LAYER on a composition - a section of the frame, not just a full-canvas background. The
/// layer persists across subsequent cue fires until a Stop visualizer cue (or an edit reload) removes
/// it. Executes at the HaPlay transport layer (no ShowDocument mapping).</summary>
public sealed record VisualizerCueNode : CueNode
{
    /// <summary>Composition the layer renders on (from <see cref="CueList.Compositions"/>).</summary>
    public Guid CompositionId { get; init; }

    /// <summary>False = this cue STOPS the composition's visualizer instead of starting one.</summary>
    public bool StartVisualizer { get; init; } = true;

    /// <summary>Optional *.milk preset folder (null = built-in idle preset).</summary>
    public string? PresetDirectory { get; init; }

    /// <summary>Placements onto compositions - the SAME editor/model as media cues (#26 v3): position,
    /// size, opacity, rotation, fit. Older files carry the legacy Dest*/Opacity fields instead; they are
    /// migrated to one placement at load.</summary>
    public List<CueVideoPlacement> VideoPlacements { get; init; } = new();

    /// <summary>Timeline occupancy like an image slide: 0 = infinite (runs until a Stop cue; the
    /// chain advances immediately), &gt;0 = the next Auto-Follow cue fires after this many ms (the
    /// visualizer itself keeps running as a layer either way).</summary>
    public int DurationMs { get; init; }

    /// <summary>projectM render resolution/fps (its internal FBO). 0 = follow the composition.</summary>
    public int RenderWidth { get; init; }

    public int RenderHeight { get; init; }

    public int RenderFps { get; init; }

    /// <summary>Seconds before the visualizer automatically advances to another preset.</summary>
    public double PresetDurationSeconds { get; init; } = 30;

    /// <summary>Whether automatic/manual advances choose a random preset instead of the next one.</summary>
    public bool ShufflePresets { get; init; } = true;

    /// <summary>projectM beat sensitivity (0..5; the library default is 1).</summary>
    public double BeatSensitivity { get; init; } = 1;

    /// <summary>Seconds used to cross-fade between presets.</summary>
    public double TransitionSeconds { get; init; } = 2;

    /// <summary>Legacy single-rect placement (pre-v3 files); migrated to <see cref="VideoPlacements"/>.</summary>
    public double DestX { get; init; }

    public double DestY { get; init; }

    public double DestWidth { get; init; } = 1.0;

    public double DestHeight { get; init; } = 1.0;

    public double Opacity { get; init; } = 1.0;

    /// <summary>Audio feed: true = every playing media cue drives the visualizer; false = only the
    /// cues in <see cref="FeedCueIds"/> plus media cues flagged <see cref="MediaCueNode.SendToVisualizer"/>.</summary>
    public bool FeedAll { get; init; } = true;

    /// <summary>Selected feed sources (stable cue IDs) when <see cref="FeedAll"/> is false.</summary>
    public List<Guid> FeedCueIds { get; init; } = new();
}

/// <summary>Control-flow cue: firing it moves the playhead to a TARGET cue (loops, section repeats,
/// shuffle blocks). Targets are stable cue IDs - never numbers - so renumbering/reordering (incl.
/// auto-renumber) can never silently retarget a jump. With several targets, <see cref="RandomTarget"/>
/// picks one at random; otherwise the first live target wins. Executes at the HaPlay transport layer
/// (the ActionCueNode precedent) - no ShowDocument mapping.</summary>
public sealed record JumpCueNode : CueNode
{
    // set (not init) so a minimal/legacy "jump" node keeps these defaults: the source-generated
    // serializer assigns EVERY init property through one object initializer, so JSON-absent fields
    // would load as CLR defaults (TargetCueIds null → NRE at fire time, FireTargetOnJump false).
    // See the FadeCueNode doc note.
    public List<Guid> TargetCueIds { get; set; } = new();

    /// <summary>Pick a random target from <see cref="TargetCueIds"/> instead of the first live one.</summary>
    public bool RandomTarget { get; set; }

    /// <summary>When randomly choosing, avoid the target picked by this Jump cue last time whenever
    /// another live target is available. Runtime choice history is intentionally not persisted.</summary>
    public bool AvoidImmediateRepeat { get; set; }

    /// <summary>Fire the target on arrival (default). False = arm it as standby only (next GO fires it).</summary>
    public bool FireTargetOnJump { get; set; } = true;
}

/// <summary>Fade cue (QLab's Fade-cue precedent): firing it ramps its target cues' audio level (and
/// optionally video opacity) toward <see cref="TargetLevelDb"/> over <see cref="DurationMs"/>. It occupies
/// the chain like an Action cue (instant - the ramp runs in the background). Targets are stable cue IDs
/// like Jump targets; executes at the HaPlay transport layer (no ShowDocument mapping). All fields are
/// default-safe so older files load unchanged.
/// <para>GOTCHA: these properties use <c>set</c>, not <c>init</c>, deliberately. The source-generated
/// serializer builds init-only types through one object initializer that assigns EVERY init property, so
/// fields absent from the JSON would get CLR defaults (null list, 0 ms, false) instead of these property
/// initializers - a minimal/legacy <c>"fade"</c> node must load as the documented safe fade-out.</para></summary>
public sealed record FadeCueNode : CueNode
{
    /// <summary>The silence floor: levels at or below this fade to zero gain.</summary>
    public const double SilenceLevelDb = -60;

    /// <summary>Target cues (media cues and/or groups - a group fades its descendant media cues).</summary>
    public List<Guid> TargetCueIds { get; set; } = new();

    /// <summary>Ignore <see cref="TargetCueIds"/> and fade every currently playing cue.</summary>
    public bool TargetAllPlaying { get; set; }

    /// <summary>Target level in dB (0 = full). At or below <see cref="SilenceLevelDb"/> (incl. -inf) =
    /// fade to silence. Default silence - a plain new Fade cue is "fade out".</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double TargetLevelDb { get; set; } = SilenceLevelDb;

    public int DurationMs { get; set; } = 3000;

    public CueFadeCurve Curve { get; set; } = CueFadeCurve.Linear;

    /// <summary>Release the clip when the fade reached silence (else it keeps running silently).</summary>
    public bool StopWhenSilent { get; set; } = true;

    /// <summary>Ramp the targets' composition-layer opacity in step with the audio.</summary>
    public bool AlsoFadeVideoOpacity { get; set; } = true;
}

public sealed record CueAudioRoute
{
    public int SourceChannel { get; init; }

    /// <summary>Id of an audio-capable output line in the shared <c>OutputManagementView</c> registry
    /// (matches <c>OutputDefinition.Id</c>). Empty when no output picked yet.</summary>
    public Guid OutputLineId { get; init; }

    public int OutputChannel { get; init; }

    public double GainDb { get; init; }

    public bool Muted { get; init; }
}

public sealed record CueVideoPlacement
{
    public Guid CompositionId { get; init; }

    public int LayerIndex { get; init; }

    /// <summary>Fit of the (cropped) source within its destination rectangle.</summary>
    public CueLayerPosition Position { get; init; } = CueLayerPosition.Cover;

    public double Opacity { get; init; } = 1.0;

    /// <summary>Destination rectangle on the composition canvas, normalized to [0,1].
    /// Defaults to the full canvas - older cues load unchanged.</summary>
    public double DestX { get; init; }

    public double DestY { get; init; }

    public double DestWidth { get; init; } = 1.0;

    public double DestHeight { get; init; } = 1.0;

    /// <summary>Per-edge source crop insets as fractions [0,1). Default 0 = no trim.</summary>
    public double CropLeft { get; init; }

    public double CropTop { get; init; }

    public double CropRight { get; init; }

    public double CropBottom { get; init; }

    /// <summary>Clockwise rotation (degrees) of this layer about its destination-rect centre. Default 0
    /// = upright (older cues load unchanged). The rotated image overflows its dest rect, as expected.</summary>
    public double RotationDegrees { get; init; }

    /// <summary>Optional per-placement video FX mapping. Sections sample this source video and are
    /// then placed inside the normal destination rectangle.</summary>
    public CueOutputMapping? VideoFx { get; init; }

    /// <summary>Whether <see cref="VideoFx"/> is active. Geometry is retained while disabled.</summary>
    public bool VideoFxEnabled { get; init; }

    /// <summary>Optional chroma key ("green screen") settings for this layer. Follows the
    /// <see cref="VideoFx"/> pattern: settings are retained while disabled. Null on older cues.</summary>
    public CueChromaKey? ChromaKey { get; init; }

    /// <summary>Whether <see cref="ChromaKey"/> is active.</summary>
    public bool ChromaKeyEnabled { get; init; }

    /// <summary>Optional brightness/contrast for this layer. Same retained-while-disabled pattern
    /// as <see cref="ChromaKey"/>. Null on older cues.</summary>
    public CueColorAdjust? ColorAdjust { get; init; }

    /// <summary>Whether <see cref="ColorAdjust"/> is active.</summary>
    public bool ColorAdjustEnabled { get; init; }
}

/// <summary>Brightness/contrast settings on a video placement. Brightness is an additive offset in
/// [-1, 1] (0 = unchanged); contrast multiplies around mid-gray (1 = unchanged, up to 4).</summary>
public sealed record CueColorAdjust
{
    public double Brightness { get; init; }

    public double Contrast { get; init; } = 1.0;
}

/// <summary>Chroma-key ("green screen") settings on a video placement. Semantics (and defaults)
/// mirror the framework's <c>ChromaKeySettings</c> / OBS's chroma-key filter: pixels near the key
/// color's chroma turn transparent, smoothness widens the alpha ramp, spill suppression
/// desaturates key-colored light bleeding onto the subject.</summary>
public sealed record CueChromaKey
{
    /// <summary>Key color RGB, each [0, 1]. Default = pure green.</summary>
    public double KeyR { get; init; }

    public double KeyG { get; init; } = 1.0;

    public double KeyB { get; init; }

    public double Similarity { get; init; } = 0.4;

    public double Smoothness { get; init; } = 0.08;

    public double SpillSuppression { get; init; } = 0.1;
}

public enum CueTriggerMode
{
    Manual,
    AutoFollow,
    AutoContinue,
}

public enum CueGroupFireMode
{
    FirstCueOnly,
    FireAllSimultaneously,
    ArmedList,
    /// <summary>Fire every child at its authored <see cref="CueNode.TimelineStartMs"/> on the group's
    /// plan epoch. An old build opening a Timeline group degrades to first-cue-only (unknown enum).</summary>
    Timeline,
    /// <summary>Auto-advancing playlist: each child's natural end fires the next pick per the group's
    /// <see cref="CueGroupNode.Playlist"/> options (shuffle bag, passes, subset). An old build opening
    /// a Playlist group degrades to first-cue-only (unknown enum).</summary>
    Playlist,
}

public enum CueEndBehavior
{
    Stop,
    FreezeLastFrame,
    Loop,
    FadeOutAndStop,
}

/// <summary>GUI mirror of the framework's <c>S.Media.Session.FadeCurve</c> (models stay
/// framework-type-free, like <see cref="CueEndBehavior"/> ↔ <c>ClipEndBehavior</c>). The mapper
/// converts by name; Linear = 0 keeps pre-curve files loading unchanged.</summary>
public enum CueFadeCurve
{
    Linear,
    EqualPower,
    Exponential,
    SCurve,
}

public enum CueActionKind
{
    OSCOut,
    MIDIOut,
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CueList))]
[JsonSerializable(typeof(CueNode))]
[JsonSerializable(typeof(CueGroupNode))]
[JsonSerializable(typeof(CuePlaylistOptions))]
[JsonSerializable(typeof(MediaCueNode))]
[JsonSerializable(typeof(CueSubtitleSelection))]
[JsonSerializable(typeof(ActionCueNode))]
[JsonSerializable(typeof(CommentCueNode))]
[JsonSerializable(typeof(JumpCueNode))]
[JsonSerializable(typeof(VisualizerCueNode))]
[JsonSerializable(typeof(FadeCueNode))]
[JsonSerializable(typeof(CueComposition))]
[JsonSerializable(typeof(CueVideoOutputBinding))]
[JsonSerializable(typeof(CueAudioRoute))]
[JsonSerializable(typeof(CueVideoPlacement))]
[JsonSerializable(typeof(CueChromaKey))]
[JsonSerializable(typeof(CueColorAdjust))]
[JsonSerializable(typeof(CueSchedule))]
[JsonSerializable(typeof(CueAutomationPoint))]
[JsonSerializable(typeof(PlaylistItem))]
[JsonSerializable(typeof(FilePlaylistItem))]
[JsonSerializable(typeof(NDIInputPlaylistItem))]
[JsonSerializable(typeof(PortAudioInputPlaylistItem))]
[JsonSerializable(typeof(ImagePlaylistItem))]
[JsonSerializable(typeof(SubtitlePlaylistItem))]
[JsonSerializable(typeof(TextPlaylistItem))]
[JsonSerializable(typeof(YouTubePlaylistItem))]
[JsonSerializable(typeof(MMDPlaylistItem))]
[JsonSerializable(typeof(CueListsCollectionDocument))]
[JsonSerializable(typeof(CueCompositionsDocument))]
[JsonSerializable(typeof(List<CueList>))]
[JsonSerializable(typeof(CueClipboardDocument))]
internal partial class CueListJsonContext : JsonSerializerContext;

/// <summary>
/// Clipboard envelope for cue copy/paste (Ctrl+C / Ctrl+V in the cue tree). Serialized as plain
/// text on the OS clipboard so cues transfer across lists, projects, and app instances; the
/// version stamp makes foreign clipboard text and future format changes fail closed on paste.
/// </summary>
public sealed record CueClipboardDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public List<CueNode> Cues { get; init; } = [];
}
