using System.Text.Json.Serialization;
using S.Media.Session;

namespace HaCue2.Core.Model;

/// <summary>One cue list. Every list keeps its own standby position (register item 5).</summary>
public sealed record CueList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<CueNode> Cues { get; set; } = [];

    /// <summary>
    /// Where this list's playhead sits. Multi-list transport is v1: the transport acts on the
    /// selected list and every list remembers its own position, so selecting another list does not
    /// rewind the one you were running.
    /// </summary>
    public Guid? StandbyCueId { get; set; }

    /// <summary>Depth-first over this list, groups included, in fire order.</summary>
    public IEnumerable<CueNode> Flatten() => Cues.SelectMany(Flatten);

    private static IEnumerable<CueNode> Flatten(CueNode cue)
    {
        yield return cue;
        if (cue is not GroupCueNode group)
            yield break;
        foreach (var child in group.Children.SelectMany(Flatten))
            yield return child;
    }
}

/// <summary>
/// Base of every cue kind. The discriminator is written as <c>kind</c>.
/// </summary>
/// <remarks>
/// <see cref="Number"/> is a <see cref="CueNumber"/> — dot-separated segments, stored as written and
/// compared numerically. It was a <c>decimal</c> until real HaPlay projects showed three-level numbers
/// (1.1.1, 1.2.1) throughout, which a decimal cannot represent and which would have made the app
/// unable to open a project from the app it replaces. See that type for the rest of the reasoning.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MediaCueNode), "media")]
[JsonDerivedType(typeof(GroupCueNode), "group")]
[JsonDerivedType(typeof(ActionCueNode), "action")]
[JsonDerivedType(typeof(FadeCueNode), "fade")]
[JsonDerivedType(typeof(JumpCueNode), "jump")]
[JsonDerivedType(typeof(VisualizerCueNode), "visualizer")]
[JsonDerivedType(typeof(PatchCueNode), "patch")]
[JsonDerivedType(typeof(CommentCueNode), "comment")]
[JsonDerivedType(typeof(TextCueNode), "text")]
public abstract record CueNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CueNumber Number { get; set; }
    public string Label { get; set; } = "";

    /// <summary>One Note per cue — and the whole of a comment cue. Stays writable under Lock.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// A disabled cue stays VISIBLE and is skipped by GO, by auto-follow and by compilation. Deleting
    /// a cue to drop it for one performance is how shows lose cues.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public CueTrigger Trigger { get; set; } = CueTrigger.Manual;
    public int PreWaitMs { get; set; }
    public int PostWaitMs { get; set; }

    /// <summary>
    /// A colour band on the cue's row: 0 is none, 1–8 index a fixed palette.
    /// </summary>
    /// <remarks>
    /// An INDEX rather than a colour, so the palette can be restyled with the theme and so a show does
    /// not carry hex codes that clash with whatever skin it is opened under. It is the fastest thing to
    /// read in a list of six hundred rows — an operator finds "the blue block" before they find Q412.
    /// </remarks>
    public int ColorTag { get; set; }

    /// <summary>
    /// Where this cue starts inside a TIMELINE group, in milliseconds from the group's own start.
    /// </summary>
    /// <remarks>
    /// Meaningless outside one — a playlist child follows its predecessor and an all-together child
    /// starts with the group — but it lives on the base because a cue can be dragged into and out of a
    /// timeline group without changing kind, and losing its position on the way in would be a data
    /// loss the operator did not ask for.
    /// <para>
    /// NOT called StartOffsetMs: HaPlay uses that name on a media cue for the trim INTO THE FILE
    /// (<see cref="MediaCueNode.TrimInMs"/>), and an importer that read one as the other would put
    /// every clip in the wrong place while trimming nothing.
    /// </para>
    /// </remarks>
    public int TimelineOffsetMs { get; set; }
}

public enum CueTrigger
{
    Manual,
    Follow,
    Continue,
}

/// <summary>A media cue: the only kind that carries audio sends and a media file.</summary>
public sealed record MediaCueNode : CueNode
{
    /// <summary>
    /// A file, or a source URI.
    /// </summary>
    /// <remarks>
    /// A path — relative to the media root or absolute — for a file, and one of the registry's source
    /// schemes for everything else: <c>ndi://</c> a camera, <c>padev://</c> a capture device,
    /// <c>youtube://</c> a prepared video. <see cref="Media.SourceUri"/> tells them apart, and the
    /// difference matters everywhere something would otherwise treat this as a filename.
    /// </remarks>
    public string MediaPath { get; set; } = "";

    /// <summary>
    /// What the SOURCE said it runs for, in milliseconds; 0 when nothing said.
    /// </summary>
    /// <remarks>
    /// Durations are otherwise a machine fact, probed from the file and deliberately never written
    /// into the document. A URI source has no file here to probe: a YouTube video knows its length
    /// from the manifest at the moment it is added, and without somewhere to keep that the cue reads
    /// "—" in every list for the rest of the show's life. Live sources leave it zero, which is the
    /// honest answer for a camera.
    /// </remarks>
    public int SourceDurationMs { get; set; }

    public double LevelDb { get; set; }
    public bool Loop { get; set; }

    /// <summary>
    /// What happens when the cue reaches its trimmed end.
    /// </summary>
    /// <remarks>
    /// <see cref="Loop"/> stays for documents that already carry it and means the same thing as
    /// <see cref="CueEndBehavior.Loop"/>; either is honoured. The other two are what could not be said
    /// before: FREEZE holds the last frame, which is what a title card wants, and FADE OUT gives the
    /// cue its own out-ramp without a fade cue aimed at it.
    /// </remarks>
    public CueEndBehavior EndBehavior { get; set; } = CueEndBehavior.Stop;

    /// <summary>
    /// The overlap when a looping cue wraps, in milliseconds. Zero is a hard cut.
    /// </summary>
    /// <remarks>
    /// A seamless loop is made of this. Without it a bed loops with an audible seam at the join, and
    /// the only workaround is to author the crossfade into the file.
    /// </remarks>
    public int LoopCrossfadeMs { get; set; }

    /// <summary>
    /// Keep this cue out of pre-roll.
    /// </summary>
    /// <remarks>
    /// Pre-roll opens the next few cues' media so the next GO is instant, at the cost of a decoder
    /// held open per warmed cue. A few are worth exempting: a 4 K master that costs more to hold than
    /// it saves, or a source whose open starts something — a connection, a device claim — that nobody
    /// wants running early. The cue still fires normally; it simply opens at that moment.
    /// </remarks>
    public bool DisablePreRoll { get; set; }

    /// <summary>
    /// An explicit cue to fire when this media reaches its natural end. Null keeps the ordinary
    /// Follow/Continue rules. Playlist ownership takes precedence so a child cannot escape its group.
    /// </summary>
    public Guid? EndTargetCueId { get; set; }

    /// <summary>
    /// Include this cue in visualizer feeds that opt into selected media rather than the whole bus.
    /// </summary>
    public bool SendToVisualizer { get; set; }

    /// <summary>
    /// Where playback starts inside the file, in milliseconds. HaPlay's <c>startOffsetMs</c>.
    /// </summary>
    public int TrimInMs { get; set; }

    /// <summary>
    /// Where playback stops inside the file, in milliseconds; 0 means play to the end.
    /// </summary>
    /// <remarks>
    /// Zero rather than null for "to the end", matching HaPlay's <c>endOffsetMs</c> — a file cannot
    /// usefully end at 0 ms, so the sentinel costs nothing and keeps the two documents the same shape.
    /// </remarks>
    public int TrimOutMs { get; set; }

    /// <summary>The trimmed length, given what the file turned out to be. Null until something probed.</summary>
    public TimeSpan? TrimmedLength(TimeSpan? fileLength)
    {
        if (TrimOutMs > TrimInMs)
            return TimeSpan.FromMilliseconds(TrimOutMs - TrimInMs);

        return fileLength is { } length
            ? length - TimeSpan.FromMilliseconds(TrimInMs)
            : null;
    }

    /// <summary>
    /// CUE time (0 = this cue's in-point, what the operator reads and scrubs) → MEDIA time (0 = the
    /// start of the file, what the transport seeks in). Clamped to the trimmed window.
    /// </summary>
    /// <remarks>
    /// Kept ADJACENT to <see cref="CueTimeAt"/> on purpose. The two directions of this mapping were
    /// written independently and in different assemblies - the read half lived in the engine's snapshot,
    /// the write half did not exist at all - so a cue trimmed to start at 36:00 displayed correctly and
    /// then seeked to the wrong place: a scrub to 30:50 CUE time reached the player as 30:50 FILE time,
    /// landed before the cue's own in-point, and the display clamped the negative result to zero, which
    /// read as "it jumped to the beginning". Asymmetric mappings are the bug; putting both halves in one
    /// place, next to the length that uses the same numbers, is the fix.
    /// </remarks>
    public TimeSpan MediaTimeAt(TimeSpan cueTime)
    {
        if (cueTime < TimeSpan.Zero)
            cueTime = TimeSpan.Zero;

        var mediaTime = TimeSpan.FromMilliseconds(TrimInMs) + cueTime;
        if (TrimOutMs > TrimInMs)
        {
            var trimOut = TimeSpan.FromMilliseconds(TrimOutMs);
            if (mediaTime > trimOut)
                mediaTime = trimOut;
        }

        return mediaTime;
    }

    /// <summary>
    /// MEDIA time → CUE time: the inverse of <see cref="MediaTimeAt"/>, floored at zero (media before
    /// the in-point is not part of this cue).
    /// </summary>
    public TimeSpan CueTimeAt(TimeSpan mediaTime)
    {
        var cueTime = mediaTime - TimeSpan.FromMilliseconds(TrimInMs);
        return cueTime > TimeSpan.Zero ? cueTime : TimeSpan.Zero;
    }

    public int FadeInMs { get; set; }
    public CurveSpec FadeInCurve { get; set; } = new();
    public int FadeOutMs { get; set; }
    public CurveSpec FadeOutCurve { get; set; } = new();

    /// <summary>
    /// Which audio track to play, or null to let the decoder elect one.
    /// </summary>
    /// <remarks>
    /// A concert capture routinely carries several — a stereo mix, an isolated vocal, a room pair —
    /// and which one a cue plays is an authoring decision, not something to guess. Null is the honest
    /// default: "whatever the file says is the main one".
    /// </remarks>
    public int? AudioTrackIndex { get; set; }

    /// <summary>
    /// What the chosen audio track WAS, so a re-mux cannot silently swap it.
    /// </summary>
    /// <remarks>
    /// Stream indices are positional. Re-muxing a file keeps its tracks and can renumber them, and a
    /// stored index would then point at a different one — the commentary instead of the music, with
    /// nothing on screen to say so. The signature is compared on load and a mismatch falls back to
    /// automatic election, which is obviously automatic rather than quietly wrong.
    /// </remarks>
    public string AudioTrackSignature { get; set; } = "";

    /// <summary>Which video track; null elects one, −1 means play no video at all.</summary>
    public int? VideoTrackIndex { get; set; }

    public string VideoTrackSignature { get; set; } = "";

    /// <summary>Subtitle tracks to show. Empty means none — subtitles are never on by default.</summary>
    public List<SubtitleSelection> Subtitles { get; set; } = [];

    /// <summary>The N×V half: which source channel feeds which logical output, at what gain.</summary>
    public List<CueAudioSend> Sends { get; set; } = [];

    /// <summary>
    /// Where this cue's video appears. Empty for an audio-only cue.
    /// </summary>
    /// <remarks>
    /// A LIST, because one cue can appear in several places at once — the same feed mirrored to a
    /// second canvas, or two regions of one canvas — and the engine fans ONE decoded source to all of
    /// them (<c>ShowClipBinding.ExtraPlacements</c>: "decoded once"). Playing the file twice to get a
    /// mirror would double the decode cost and drift the two copies apart.
    /// </remarks>
    public List<LayerPlacement> Placements { get; set; } = [];

    public List<EffectLane> EffectLanes { get; set; } = [];
}

/// <summary>A group: several cues fired together, as a playlist, or on a timeline.</summary>
public sealed record GroupCueNode : CueNode
{
    public GroupFireMode FireMode { get; set; } = GroupFireMode.AllTogether;
    public List<CueNode> Children { get; set; } = [];

    public bool Shuffle { get; set; }
    public bool ReshuffleEachPass { get; set; }

    /// <summary>
    /// Never open a pass with the item that closed the previous one.
    /// </summary>
    /// <remarks>
    /// The difference between a shuffle that sounds random and one that sounds broken: without it a
    /// shuffled bed can play the same track twice across a pass boundary, which in front of an
    /// audience reads as a fault rather than as chance.
    /// </remarks>
    public bool AvoidImmediateRepeat { get; set; } = true;

    /// <summary>
    /// How many passes the playlist makes. Zero is forever.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AtEnd"/>, which says what happens AFTER the last pass. "Play this
    /// twice and then hold" needs both, and could not be said with either alone.
    /// </remarks>
    public int LoopCount { get; set; } = 1;

    /// <summary>Items selected from each ordered/shuffled pass; null plays every enabled child.</summary>
    public int? PlayCount { get; set; }

    /// <summary>
    /// The playlist's default crossfade. Per-transition overrides live on the CHILD
    /// (<see cref="MediaCueNode.FadeInMs"/> and its own crossfade), so "one integer for the whole
    /// group" is a default rather than a ceiling.
    /// </summary>
    public int CrossfadeMs { get; set; }

    public CurveSpec CrossfadeCurve { get; set; } = new();
    public AtListEnd AtEnd { get; set; } = AtListEnd.Hold;

    /// <summary>Group-level lanes scale everything inside; a child's same-kind lane overrides them.</summary>
    public List<EffectLane> EffectLanes { get; set; } = [];
}

public enum GroupFireMode
{
    AllTogether,
    Playlist,
    Timeline,
    FirstCueOnly,
    ArmedList,
}

/// <summary>An outbound message to another system.</summary>
public sealed record ActionCueNode : CueNode
{
    public Guid? EndpointId { get; set; }
    public string Address { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>A fade applied to running cues and/or logical outputs.</summary>
public sealed record FadeCueNode : CueNode
{
    public List<Guid> TargetCueIds { get; set; } = [];
    public List<Guid> TargetChannelIds { get; set; } = [];
    /// <summary>Defaults to the silence floor — see <see cref="GainRange.SilenceFloorDb"/>.</summary>
    public double ToLevelDb { get; set; } = GainRange.SilenceFloorDb;
    public int DurationMs { get; set; } = 2_000;
    public CurveSpec Curve { get; set; } = new();
    public bool FadeEverythingSounding { get; set; }
    public bool StopTargetsWhenComplete { get; set; } = true;
}

/// <summary>A move of the playhead.</summary>
public sealed record JumpCueNode : CueNode
{
    public List<Guid> TargetCueIds { get; set; } = [];
    public JumpCondition Condition { get; set; } = JumpCondition.Always;
    /// <summary>For CountThenContinue: how many visits jump before the next visit falls through.</summary>
    public int JumpCount { get; set; } = 1;
    public bool PickAtRandom { get; set; }
    public bool FireOnArrival { get; set; } = true;
}

public enum JumpCondition
{
    Always,
    WhileTriggerHeld,
    CountThenContinue,
}

/// <summary>
/// A visualizer. Its settings live HERE and nowhere else — a composition carries no visualizer flag
/// (register item 21); the visualizer's presence on a canvas is an ordinary placement.
/// </summary>
public sealed record VisualizerCueNode : CueNode
{
    public string PresetPack { get; set; } = "";
    public int HoldMs { get; set; } = 24_000;
    public int BlendMs { get; set; } = 3_000;
    public bool LockPreset { get; set; }

    /// <summary>Listen to every sounding cue. False uses <see cref="FeedCueIds"/> plus media opt-ins.</summary>
    public bool FeedAll { get; set; } = true;

    /// <summary>Media cues explicitly included when <see cref="FeedAll"/> is false.</summary>
    public List<Guid> FeedCueIds { get; set; } = [];

    /// <summary>Where the visualizer appears. A list, for the same reason a media cue's is.</summary>
    public List<LayerPlacement> Placements { get; set; } = [];

    public List<EffectLane> EffectLanes { get; set; } = [];
}

/// <summary>
/// Recalls part of the project patch, and leaves it recalled.
/// </summary>
/// <remarks>
/// It writes patch CELL gains only — the same values the operator edits in the patch pane. It never
/// touches a cue's sends, a voice's fade level or the master trim, so the gain-composition chain
/// still composes exactly once. Stopping the cue undoes nothing: returning to a prior state is
/// another patch cue, which is what a board operator expects.
/// </remarks>
public sealed record PatchCueNode : CueNode
{
    public Guid? SnapshotId { get; set; }
    public List<PatchLevelChange> Levels { get; set; } = [];
    public int FadeMs { get; set; }
    public CurveSpec FadeCurve { get; set; } = new();
}

/// <summary>A cue that is only its note — a marker in the list.</summary>
public sealed record CommentCueNode : CueNode;

/// <summary>
/// Words on a canvas: a title card, a caption, a holding slate.
/// </summary>
/// <remarks>
/// <para>
/// The document stores the WORDS and how they should look, and compiles to a <c>text:</c> URI that
/// carries the whole render spec. The framework's own text source draws it — so a card needs no file
/// anywhere, no cache to invalidate, and nothing from the app at all. The words travel with the show
/// and each machine draws them with the faces it has.
/// </para>
/// <para>
/// It behaves as a still from there on: one held frame, placed like any other picture. Placements and
/// fades apply, because by the time the engine sees it there is nothing to distinguish it from any
/// other single-frame clip.
/// </para>
/// </remarks>
public sealed record TextCueNode : CueNode
{
    public string Text { get; set; } = "";

    /// <summary>
    /// The face to draw with, or empty for the app's own.
    /// </summary>
    /// <remarks>
    /// A HINT, matched the way an audio line's device name is: a booth machine may not have the face a
    /// show was authored with, and falling back to something readable beats refusing to draw. Empty is
    /// the honest default — the app embeds one face precisely so a show that names none looks the same
    /// everywhere.
    /// </remarks>
    public string FontFamily { get; set; } = "";

    /// <summary>Cap height as a fraction of the canvas, so the card survives a resize.</summary>
    /// <remarks>
    /// Not points. A card authored at 72 pt on a 1280×720 canvas is unreadable on a 4 K one, and every
    /// other geometry in this document is already a fraction for the same reason.
    /// </remarks>
    public double FontScale { get; set; } = 0.12;

    public bool Bold { get; set; }
    public bool Italic { get; set; }

    /// <summary>Ink and ground as "#RRGGBB"; the ground may be "" for a transparent card.</summary>
    public string Foreground { get; set; } = "#FFFFFF";

    public string Background { get; set; } = "";

    /// <summary>An outline behind the ink, in fractions of the canvas height. Zero draws none.</summary>
    /// <remarks>
    /// What makes a caption readable over picture. Without one, white words over a bright shot are
    /// words nobody in the room can read, and the usual fix — a background band — covers the shot.
    /// </remarks>
    public double OutlineWidth { get; set; }

    public string Outline { get; set; } = "#000000";

    public TextAlign Align { get; set; } = TextAlign.Center;
    public TextAnchor Anchor { get; set; } = TextAnchor.Middle;

    /// <summary>
    /// How long the card is held, in milliseconds. Zero holds it until something stops it.
    /// </summary>
    /// <remarks>
    /// The text source emits a held frame for exactly this long and then exhausts, so a card with a
    /// duration ends on its own and can auto-follow. Zero is the title-card answer: up until the
    /// operator takes it down.
    /// </remarks>
    public int DurationMs { get; set; }

    /// <summary>Where the card sits, like any other cue's picture.</summary>
    public List<LayerPlacement> Placements { get; set; } = [];

    public int FadeInMs { get; set; }
    public CurveSpec FadeInCurve { get; set; } = new();
    public int FadeOutMs { get; set; }
    public CurveSpec FadeOutCurve { get; set; } = new();

    public List<EffectLane> EffectLanes { get; set; } = [];

    // There is no render key here any more. It was the cache key for the app-side card renderer,
    // and that renderer is gone: a card compiles to a `text:` URI carrying its whole spec, so the
    // framework's own text source draws it and there is nothing left to cache or invalidate. The
    // key had also drifted out of step with the record — it never covered the outline or the
    // duration, so two cards differing only in those would have shared one entry.
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

public enum TextAnchor
{
    Top,
    Middle,
    Bottom,
}

/// <summary>Where a cue's picture sits on a composition.</summary>
public sealed record LayerPlacement
{
    public Guid CompositionId { get; set; }
    public int LayerIndex { get; set; }

    /// <summary>Fractions of the composition, so a placement survives a size change.</summary>
    public double X { get; set; }

    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;

    public LayerFit Fit { get; set; } = LayerFit.Contain;

    /// <summary>The AUTHORED opacity. Fades and automation multiply over it; they never replace it.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>
    /// Per-edge source crop, as fractions of the SOURCE. Zero on every edge is no crop.
    /// </summary>
    /// <remarks>
    /// A crop and a destination rectangle answer different questions and are both needed: the crop says
    /// which part of the picture to use, the rectangle says where to put it. Letterbox bars baked into a
    /// 4:3 transfer come off with a crop; the same clip still has to be placed. Cropping by shrinking the
    /// destination instead would move the picture as well as trim it.
    /// </remarks>
    public double CropLeft { get; set; }

    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }

    /// <summary>
    /// Clockwise rotation about the destination rectangle's centre, in degrees.
    /// </summary>
    /// <remarks>
    /// A rotated layer OVERFLOWS its rectangle rather than being trimmed to it — which is what a
    /// portrait screen hung sideways needs, and the opposite of what the unrotated fit does.
    /// </remarks>
    public double RotationDegrees { get; set; }

    /// <summary>
    /// A mapping applied to this LAYER's own picture, before it is placed.
    /// </summary>
    /// <remarks>
    /// The same shape as an output's mapping and for a different stage: an output's mapping warps what
    /// reaches one screen, and this warps one clip before it joins the canvas. Splitting a single video
    /// across two halves of a set piece is this, not the output's.
    /// </remarks>
    public List<MappingSection> VideoFx { get; set; } = [];

    /// <summary>
    /// Whether <see cref="VideoFx"/> is in force. Geometry is kept while it is off.
    /// </summary>
    /// <remarks>
    /// The rule the whole app follows for effects: off is not delete. An operator checking a warp
    /// against an unwarped feed must not have to author it again to get it back.
    /// </remarks>
    public bool VideoFxEnabled { get; set; } = true;

    /// <summary>Green-screen keying for this layer, or null when it has none.</summary>
    public ChromaKeySpec? ChromaKey { get; set; }

    public bool ChromaKeyEnabled { get; set; } = true;

    /// <summary>Brightness and contrast for this layer, or null when it has none.</summary>
    public ColorAdjustSpec? ColorAdjust { get; set; }

    public bool ColorAdjustEnabled { get; set; } = true;

    /// <summary>Whether the layer carries a mapping the renderer will actually apply.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVideoFx => VideoFxEnabled && VideoFx.Count > 0;
}

/// <summary>
/// Green-screen settings for one layer.
/// </summary>
/// <remarks>
/// Defaults are the framework's own, so a key added and left alone behaves the way the compositor's
/// documentation describes rather than the way whoever typed this file guessed.
/// </remarks>
public sealed record ChromaKeySpec
{
    /// <summary>The key colour, as 0–1 red/green/blue. Default is a broadcast green.</summary>
    public double Red { get; set; }

    public double Green { get; set; } = 1;
    public double Blue { get; set; }

    /// <summary>How far from the key colour still counts as background.</summary>
    public double Similarity { get; set; } = 0.4;

    /// <summary>How soft the edge between kept and keyed is.</summary>
    public double Smoothness { get; set; } = 0.1;

    /// <summary>How much of the key colour is pulled out of what remains — the green fringe.</summary>
    public double SpillReduction { get; set; } = 0.1;
}

/// <summary>Brightness and contrast for one layer. Brightness is an offset, contrast a multiplier.</summary>
public sealed record ColorAdjustSpec
{
    public double Brightness { get; set; }

    public double Contrast { get; set; } = 1;
}

/// <summary>
/// One subtitle track a cue shows.
/// </summary>
/// <remarks>
/// Either a track inside the media (<see cref="StreamIndex"/>) or a sidecar file
/// (<see cref="Path"/>) — a show routinely uses both, an embedded track for one language and a
/// hand-corrected .srt for another, so this is a list rather than a single choice.
/// </remarks>
public sealed record SubtitleSelection
{
    /// <summary>A sidecar file, or empty for a track inside the media.</summary>
    public string Path { get; set; } = "";

    /// <summary>The container stream index; −1 selects the best track the decoder can find.</summary>
    public int StreamIndex { get; set; } = -1;

    /// <summary>What the chosen track was, for the same re-mux reason as the audio one.</summary>
    public string Signature { get; set; } = "";
}

/// <summary>Reading a cue's placements without caring which kind it is.</summary>
public static class CuePlacements
{
    /// <summary>Every canvas this cue appears on. Empty for a cue with no video.</summary>
    public static IReadOnlyList<LayerPlacement> Of(CueNode cue) => cue switch
    {
        MediaCueNode media => media.Placements,
        VisualizerCueNode visualizer => visualizer.Placements,
        TextCueNode text => text.Placements,
        _ => [],
    };

    /// <summary>The mutable list, for an edit. Null for a cue that cannot be placed at all.</summary>
    public static List<LayerPlacement>? ListOf(CueNode cue) => cue switch
    {
        MediaCueNode media => media.Placements,
        VisualizerCueNode visualizer => visualizer.Placements,
        TextCueNode text => text.Placements,
        _ => null,
    };
}

/// <summary>
/// What a media cue does when it reaches its trimmed end.
/// </summary>
/// <remarks>
/// Mirrors the framework's <c>ClipEndBehavior</c> by NAME, so the compiler converts without a table
/// that could drift. It was not expressible at all before: every cue stopped.
/// </remarks>
public enum CueEndBehavior
{
    Stop,

    /// <summary>Hold the last frame. What a title card or a holding slate wants.</summary>
    FreezeLastFrame,

    /// <summary>Back to the trim-in point, over <see cref="MediaCueNode.LoopCrossfadeMs"/>.</summary>
    Loop,

    /// <summary>Ramp itself down over the cue's own fade-out, then stop.</summary>
    FadeOutAndStop,
}

/// <summary>
/// How a layer's picture fills its destination rectangle.
/// </summary>
/// <remarks>
/// Six, matching what the compositor can actually do — it was three, so half the fits the renderer
/// supports could not be authored. <see cref="Contain"/> and <see cref="Center"/> both letterbox; they
/// differ in whether the picture is scaled to the rectangle at all.
/// </remarks>
public enum LayerFit
{
    /// <summary>Fit inside, letterboxing. The safe default: nothing is lost.</summary>
    Contain,

    /// <summary>Fill the rectangle, cropping the overflow. What a full-screen backdrop wants.</summary>
    Cover,

    /// <summary>Fill the rectangle exactly, ignoring the aspect ratio.</summary>
    Stretch,

    /// <summary>Placed at its own size in the middle, neither scaled up nor cropped.</summary>
    Center,

    /// <summary>Match the rectangle's width; the height falls where it falls.</summary>
    FillWidth,

    /// <summary>Match the rectangle's height; the width falls where it falls.</summary>
    FillHeight,
}

/// <summary>
/// An automation lane, added per cue or per group and hidden until added (register item 18).
/// </summary>
/// <remarks>
/// One concept, two editors: the inspector and the timeline edit the same points. This replaces the
/// media cue's separate volume envelope rather than sitting beside it — two envelope concepts is how
/// an operator ends up with a level nobody can explain.
/// <para>
/// An OUTBOUND lane is not undone when its cue stops, the opposite rule from the internal ones,
/// because it owns a value in another system that HaCue2 does not get to decide has ended.
/// </para>
/// </remarks>
public sealed record EffectLane
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public EffectLaneKind Kind { get; set; } = EffectLaneKind.Volume;

    /// <summary>Normalized: X is 0..1 of the cue's length, Y is 0..1 of the lane's range.</summary>
    public List<LanePoint> Points { get; set; } = [];

    /// <summary>Outbound lanes only: where the ramp is sent.</summary>
    public Guid? EndpointId { get; set; }

    public string Address { get; set; } = "";
}

public enum EffectLaneKind
{
    Volume,
    Opacity,
    OscRamp,
    MidiRamp,
}

/// <summary>One automation keyframe. <paramref name="CurveToNext"/> shapes the segment beginning at
/// this point; linear is the backward-compatible document default.</summary>
public readonly record struct LanePoint(
    double X,
    double Y,
    FadeCurve CurveToNext = FadeCurve.Linear,
    double? OutHandleX = null,
    double? OutHandleY = null,
    double? InHandleX = null,
    double? InHandleY = null);

/// <summary>
/// How a fade is shaped: a built-in law, a project preset, or an inline custom curve.
/// </summary>
/// <remarks>
/// <b>A custom curve is never a new <see cref="FadeCurve"/> member.</b> Enums round-trip numerically
/// and the sidecar format's other reader is the C ABI host, which would decode an unknown member as a
/// different valid law and quietly play the wrong shape. A nullable companion field is the only
/// additive-safe route — the same rule the framework's <c>FadeShape</c> follows.
/// </remarks>
public sealed record CurveSpec
{
    public FadeCurve Law { get; set; } = FadeCurve.EqualPower;

    /// <summary>A curve saved as a project preset; wins over <see cref="Points"/> when set.</summary>
    public Guid? PresetId { get; set; }

    /// <summary>An inline custom curve that was never named.</summary>
    public List<FadeCurvePoint>? Points { get; set; }

    /// <summary>Resolves to the shape the engine evaluates, following preset → inline → law.</summary>
    public FadeShape Resolve(HaCueProject project)
    {
        if (PresetId is { } presetId &&
            project.CurvePresets.FirstOrDefault(preset => preset.Id == presetId) is { } found)
            return new FadeShape(Law, new CustomFadeCurve(found.Points));

        if (Points is { Count: > 1 })
            return new FadeShape(Law, new CustomFadeCurve(Points));

        return new FadeShape(Law);
    }
}

/// <summary>A custom curve saved for reuse across the show. The preset row itself is the library.</summary>
public sealed record CurvePreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<FadeCurvePoint> Points { get; set; } = [];
}
