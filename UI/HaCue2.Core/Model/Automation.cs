using System.Text.Json.Serialization;
using S.Media.Session;

namespace HaCue2.Core.Model;

/// <summary>Stable property ids persisted by automation tracks.</summary>
public static class AutomationPropertyIds
{
    public const string CueVolume = "cue.audio.volume";
    public const string PlacementOpacity = "video.placement.opacity";
    public const string GroupAudioTrim = "group.audio.trim";
    public const string GroupVideoOpacity = "group.video.opacity";
    public const string OscValue = "external.osc.value";
    public const string MidiControlValue = "external.midi.control-value";
}

/// <summary>One time-domain automation track owned by a cue.</summary>
public sealed record AutomationTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AutomationTargetRef Target { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public List<AutomationKeyframe> Keyframes { get; set; } = [];
}

/// <summary>
/// Durable address of an animatable property. <see cref="ObjectId"/> identifies a placement/effect
/// instance; endpoint fields are used only by external target descriptors.
/// </summary>
public sealed record AutomationTargetRef
{
    /// <summary>Controller-cue destination. Null on a track owned by the cue it animates.</summary>
    public Guid? CueId { get; set; }
    public string PropertyId { get; set; } = "";
    public Guid? ObjectId { get; set; }
    public Guid? EndpointId { get; set; }
    public string Address { get; set; } = "";
    public int SendRateHz { get; set; } = 25;
}

/// <summary>One stable, absolute cue-time keyframe.</summary>
public sealed record AutomationKeyframe
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long TimeMs { get; set; }
    public double Value { get; set; }
    public bool Hold { get; set; }

    /// <summary>Normalized easing of the segment beginning here.</summary>
    public CurveSpec Curve { get; set; } = new() { Law = FadeCurve.Linear };
}

public enum AutomationScale
{
    Linear,
    Decibels,
    Percentage,
    Midi7Bit,
}

public enum AutomationTargetKind
{
    Cue,
    Placement,
    Group,
    External,
    EffectInstance,
}

public enum AutomationDomain
{
    SessionAudio,
    SessionVideo,
    Host,
    External,
}

public enum AutomationComposition
{
    ReplaceAuthored,
    AddDecibels,
    Multiply,
}

public sealed record AutomationValueSpec(
    double Minimum,
    double Maximum,
    double Default,
    string Unit,
    AutomationScale Scale)
{
    public double Clamp(double value) => double.IsFinite(value)
        ? Math.Clamp(value, Minimum, Maximum)
        : Default;
}

/// <summary>Code-owned capability metadata. Projects persist only the descriptor's stable id.</summary>
public sealed record AutomationPropertyDescriptor(
    string Id,
    string DisplayName,
    AutomationValueSpec Value,
    AutomationTargetKind TargetKind,
    AutomationDomain Domain,
    AutomationComposition Composition,
    string Group,
    bool SupportsCueOwnedTrack = true,
    bool SupportsAutomationCue = true);

/// <summary>One concrete property offered to the editor for a selected cue.</summary>
public sealed record AutomationTargetOption(
    AutomationTargetRef Target,
    AutomationPropertyDescriptor Descriptor,
    string DisplayName,
    double AuthoredValue);

/// <summary>The explicit, AOT-safe registry of properties HaCue2 can currently animate.</summary>
public static class AutomationPropertyCatalog
{
    private static readonly IReadOnlyDictionary<string, AutomationPropertyDescriptor> Descriptors =
        new Dictionary<string, AutomationPropertyDescriptor>(StringComparer.Ordinal)
        {
            [AutomationPropertyIds.CueVolume] = new(
                AutomationPropertyIds.CueVolume,
                "Volume",
                new AutomationValueSpec(GainRange.SilenceFloorDb, 12, 0, "dB", AutomationScale.Decibels),
                AutomationTargetKind.Cue,
                AutomationDomain.SessionAudio,
                AutomationComposition.ReplaceAuthored,
                "Audio"),
            [AutomationPropertyIds.PlacementOpacity] = new(
                AutomationPropertyIds.PlacementOpacity,
                "Opacity",
                new AutomationValueSpec(0, 1, 1, "%", AutomationScale.Percentage),
                AutomationTargetKind.Placement,
                AutomationDomain.SessionVideo,
                AutomationComposition.ReplaceAuthored,
                "Video"),
            [AutomationPropertyIds.GroupAudioTrim] = new(
                AutomationPropertyIds.GroupAudioTrim,
                "Group audio trim",
                new AutomationValueSpec(GainRange.SilenceFloorDb, 0, 0, "dB", AutomationScale.Decibels),
                AutomationTargetKind.Group,
                AutomationDomain.Host,
                AutomationComposition.AddDecibels,
                "Group",
                SupportsCueOwnedTrack: false),
            [AutomationPropertyIds.GroupVideoOpacity] = new(
                AutomationPropertyIds.GroupVideoOpacity,
                "Group video opacity",
                new AutomationValueSpec(0, 1, 1, "%", AutomationScale.Percentage),
                AutomationTargetKind.Group,
                AutomationDomain.Host,
                AutomationComposition.Multiply,
                "Group",
                SupportsCueOwnedTrack: false),
            [AutomationPropertyIds.OscValue] = new(
                AutomationPropertyIds.OscValue,
                "OSC value",
                new AutomationValueSpec(0, 1, 0, "", AutomationScale.Linear),
                AutomationTargetKind.External,
                AutomationDomain.External,
                AutomationComposition.ReplaceAuthored,
                "External"),
            [AutomationPropertyIds.MidiControlValue] = new(
                AutomationPropertyIds.MidiControlValue,
                "MIDI control value",
                new AutomationValueSpec(0, 127, 0, "", AutomationScale.Midi7Bit),
                AutomationTargetKind.External,
                AutomationDomain.External,
                AutomationComposition.ReplaceAuthored,
                "External"),
        };

    public static IReadOnlyCollection<AutomationPropertyDescriptor> All => [.. Descriptors.Values];

    public static bool TryGet(string propertyId, out AutomationPropertyDescriptor descriptor) =>
        Descriptors.TryGetValue(propertyId, out descriptor!);

    public static AutomationPropertyDescriptor? Get(string propertyId) =>
        Descriptors.GetValueOrDefault(propertyId);

    /// <summary>Concrete internal targets which actually exist on this cue.</summary>
    public static IReadOnlyList<AutomationTargetOption> ForCue(CueNode cue)
    {
        var targets = new List<AutomationTargetOption>();
        if (cue is MediaCueNode media)
        {
            targets.Add(new AutomationTargetOption(
                new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
                Descriptors[AutomationPropertyIds.CueVolume],
                "Volume",
                media.LevelDb));
        }

        foreach (var placement in CuePlacements.Of(cue))
        {
            targets.Add(new AutomationTargetOption(
                new AutomationTargetRef
                {
                    PropertyId = AutomationPropertyIds.PlacementOpacity,
                    ObjectId = placement.Id,
                },
                Descriptors[AutomationPropertyIds.PlacementOpacity],
                $"Opacity · layer {placement.LayerIndex}",
                placement.Opacity));
        }

        if (cue is GroupCueNode)
        {
            targets.Add(new AutomationTargetOption(
                new AutomationTargetRef { PropertyId = AutomationPropertyIds.GroupAudioTrim },
                Descriptors[AutomationPropertyIds.GroupAudioTrim],
                "Group audio trim",
                0));
            targets.Add(new AutomationTargetOption(
                new AutomationTargetRef { PropertyId = AutomationPropertyIds.GroupVideoOpacity },
                Descriptors[AutomationPropertyIds.GroupVideoOpacity],
                "Group video opacity",
                1));
        }

        return targets;
    }
}

/// <summary>Model helpers shared by compiler, validation, journal, and presentation.</summary>
public static class CueAutomation
{
    public static IReadOnlyList<AutomationTrack> Of(CueNode cue) => cue switch
    {
        MediaCueNode media => media.AutomationTracks,
        TextCueNode text => text.AutomationTracks,
        GroupCueNode group => group.AutomationTracks,
        VisualizerCueNode visualizer => visualizer.AutomationTracks,
        AutomationCueNode automation => automation.AutomationTracks,
        _ => [],
    };

    public static List<AutomationTrack>? ListOf(CueNode cue) => cue switch
    {
        MediaCueNode media => media.AutomationTracks,
        TextCueNode text => text.AutomationTracks,
        GroupCueNode group => group.AutomationTracks,
        VisualizerCueNode visualizer => visualizer.AutomationTracks,
        AutomationCueNode automation => automation.AutomationTracks,
        _ => null,
    };

    public static bool SameTarget(AutomationTargetRef left, AutomationTargetRef right) =>
        left.CueId == right.CueId
        && string.Equals(left.PropertyId, right.PropertyId, StringComparison.Ordinal)
        && left.ObjectId == right.ObjectId
        && left.EndpointId == right.EndpointId
        && string.Equals(left.Address, right.Address, StringComparison.Ordinal);
}

/// <summary>Pure keyframe evaluator shared by internal and outbound lowering.</summary>
public static class AutomationEvaluator
{
    public static double Sample(
        AutomationTrack? track,
        HaCueProject project,
        long timeMs,
        double authoredValue)
    {
        if (track is not { Enabled: true, Keyframes.Count: > 0 })
            return authoredValue;

        var descriptor = AutomationPropertyCatalog.Get(track.Target.PropertyId);
        var keys = track.Keyframes
            .Where(key => key.TimeMs >= 0 && double.IsFinite(key.Value))
            .OrderBy(key => key.TimeMs)
            .ThenBy(key => key.Id)
            .ToList();
        if (keys.Count == 0)
            return authoredValue;
        if (timeMs <= keys[0].TimeMs)
            return descriptor?.Value.Clamp(keys[0].Value) ?? keys[0].Value;
        if (timeMs >= keys[^1].TimeMs)
            return descriptor?.Value.Clamp(keys[^1].Value) ?? keys[^1].Value;

        var low = 0;
        var high = keys.Count - 1;
        while (low + 1 < high)
        {
            var middle = (low + high) / 2;
            if (keys[middle].TimeMs <= timeMs)
                low = middle;
            else
                high = middle;
        }

        var from = keys[low];
        var to = keys[high];
        if (from.Hold || to.TimeMs <= from.TimeMs)
            return descriptor?.Value.Clamp(from.Value) ?? from.Value;

        var progress = Math.Clamp((double)(timeMs - from.TimeMs) / (to.TimeMs - from.TimeMs), 0, 1);
        double shaped;
        try
        {
            var shape = from.Curve.Resolve(project);
            shaped = shape.Custom?.Evaluate(progress) ?? FadeCurves.ShapeProgress(progress, shape.Law);
        }
        catch (ArgumentException)
        {
            shaped = progress;
        }

        var value = from.Value + ((to.Value - from.Value) * shaped);
        return descriptor?.Value.Clamp(value) ?? value;
    }
}

// Schema-1 reader types. New code never creates these; AutomationMigration consumes and clears them.
public sealed record EffectLane
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public EffectLaneKind Kind { get; set; } = EffectLaneKind.Volume;
    public List<LanePoint> Points { get; set; } = [];
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

public readonly record struct LanePoint(
    double X,
    double Y,
    FadeCurve CurveToNext = FadeCurve.Linear,
    double? OutHandleX = null,
    double? OutHandleY = null,
    double? InHandleX = null,
    double? InHandleY = null);
