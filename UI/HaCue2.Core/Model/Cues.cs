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
/// <see cref="Number"/> is a <see cref="decimal"/>, not an <see cref="int"/>: the show's own numbering
/// is "12, 12.5, 13, 13.1" and an operator inserts between two cues by naming the gap. Decimal also
/// compares exactly, so 13.1 sorts after 13 without the rounding surprises a double would bring to a
/// renumber. (The engine's <c>CueDefinition.Number</c> is an int; mapping the two is the compiler's
/// job, not the document's.)
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
public abstract record CueNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public decimal Number { get; set; }
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
    /// Where this cue starts inside a TIMELINE group, in milliseconds from the group's own start.
    /// </summary>
    /// <remarks>
    /// Meaningless outside one — a playlist child follows its predecessor and an all-together child
    /// starts with the group — but it lives on the base because a cue can be dragged into and out of a
    /// timeline group without changing kind, and losing its position on the way in would be a data
    /// loss the operator did not ask for.
    /// </remarks>
    public int StartOffsetMs { get; set; }
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
    public string MediaPath { get; set; } = "";
    public double LevelDb { get; set; }
    public bool Loop { get; set; }

    public int FadeInMs { get; set; }
    public CurveSpec FadeInCurve { get; set; } = new();
    public int FadeOutMs { get; set; }
    public CurveSpec FadeOutCurve { get; set; } = new();

    /// <summary>The N×V half: which source channel feeds which logical output, at what gain.</summary>
    public List<CueAudioSend> Sends { get; set; } = [];

    /// <summary>Null for an audio-only cue.</summary>
    public LayerPlacement? Placement { get; set; }

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
    public LayerPlacement? Placement { get; set; }
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
}

public enum LayerFit
{
    Contain,
    Cover,
    Stretch,
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

public readonly record struct LanePoint(double X, double Y);

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
