using System.Text.Json.Serialization;
using S.Media.Core.Effects;
using S.Media.Session;

namespace HaCue2.Core.Model;

/// <summary>Stable property ids persisted by automation tracks.</summary>
public static class AutomationPropertyIds
{
    public const string CueVolume = "cue.audio.volume";
    public const string PlacementOpacity = "video.placement.opacity";
    public const string PlacementX = "video.placement.x";
    public const string PlacementY = "video.placement.y";
    public const string PlacementWidth = "video.placement.width";
    public const string PlacementHeight = "video.placement.height";
    public const string PlacementRotation = "video.placement.rotation";
    public const string ChromaSimilarity = "video.effect.chroma-key.similarity";
    public const string ChromaSmoothness = "video.effect.chroma-key.smoothness";
    public const string ChromaSpillReduction = "video.effect.chroma-key.spill-reduction";
    public const string ColorBrightness = "video.effect.color-adjust.brightness";
    public const string ColorContrast = "video.effect.color-adjust.contrast";
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
    public AutomationInterruption Interruption { get; set; } = AutomationInterruption.Freeze;
    public List<AutomationKeyframe> Keyframes { get; set; } = [];
}

public enum AutomationInterruption
{
    /// <summary>Leave the target at the value sampled when STOP/PANIC interrupted the track.</summary>
    Freeze,

    /// <summary>Explicitly send the track's final key when interrupted.</summary>
    LandFinal,
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
            [AutomationPropertyIds.PlacementX] = PlacementDescriptor(
                AutomationPropertyIds.PlacementX, "Position X",
                new AutomationValueSpec(-1, 2, 0, "%", AutomationScale.Percentage)),
            [AutomationPropertyIds.PlacementY] = PlacementDescriptor(
                AutomationPropertyIds.PlacementY, "Position Y",
                new AutomationValueSpec(-1, 2, 0, "%", AutomationScale.Percentage)),
            [AutomationPropertyIds.PlacementWidth] = PlacementDescriptor(
                AutomationPropertyIds.PlacementWidth, "Width",
                new AutomationValueSpec(0.001, 2, 1, "%", AutomationScale.Percentage)),
            [AutomationPropertyIds.PlacementHeight] = PlacementDescriptor(
                AutomationPropertyIds.PlacementHeight, "Height",
                new AutomationValueSpec(0.001, 2, 1, "%", AutomationScale.Percentage)),
            [AutomationPropertyIds.PlacementRotation] = PlacementDescriptor(
                AutomationPropertyIds.PlacementRotation, "Rotation",
                new AutomationValueSpec(-360, 360, 0, "°", AutomationScale.Linear)),
            [AutomationPropertyIds.ChromaSimilarity] = EffectDescriptor(
                AutomationPropertyIds.ChromaSimilarity, "Chroma similarity",
                new AutomationValueSpec(0, 1, .4, "%", AutomationScale.Percentage), "Chroma key"),
            [AutomationPropertyIds.ChromaSmoothness] = EffectDescriptor(
                AutomationPropertyIds.ChromaSmoothness, "Chroma smoothness",
                new AutomationValueSpec(0, 1, .1, "%", AutomationScale.Percentage), "Chroma key"),
            [AutomationPropertyIds.ChromaSpillReduction] = EffectDescriptor(
                AutomationPropertyIds.ChromaSpillReduction, "Spill reduction",
                new AutomationValueSpec(0, 1, .1, "%", AutomationScale.Percentage), "Chroma key"),
            [AutomationPropertyIds.ColorBrightness] = EffectDescriptor(
                AutomationPropertyIds.ColorBrightness, "Brightness",
                new AutomationValueSpec(-1, 1, 0, "%", AutomationScale.Percentage), "Colour adjust"),
            [AutomationPropertyIds.ColorContrast] = EffectDescriptor(
                AutomationPropertyIds.ColorContrast, "Contrast",
                new AutomationValueSpec(0, 4, 1, "×", AutomationScale.Linear), "Colour adjust"),
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

    private static AutomationPropertyDescriptor PlacementDescriptor(
        string id, string name, AutomationValueSpec value) =>
        new(
            id,
            name,
            value,
            AutomationTargetKind.Placement,
            AutomationDomain.SessionVideo,
            AutomationComposition.ReplaceAuthored,
            "Transform");

    private static AutomationPropertyDescriptor EffectDescriptor(
        string id, string name, AutomationValueSpec value, string group) =>
        new(
            id,
            name,
            value,
            AutomationTargetKind.EffectInstance,
            AutomationDomain.SessionVideo,
            AutomationComposition.ReplaceAuthored,
            group);

    public static IReadOnlyCollection<AutomationPropertyDescriptor> All =>
    [
        .. Descriptors.Values,
        .. LayerEffectCatalog.All
            .SelectMany(effect => effect.Parameters.Select(parameter => DynamicEffectDescriptor(effect, parameter)))
            .Where(descriptor => !Descriptors.ContainsKey(descriptor.Id)),
        .. AudioEffectCatalog.All
            .SelectMany(effect => effect.Parameters.Select(parameter => DynamicAudioEffectDescriptor(effect, parameter))),
    ];

    public static bool TryGet(string propertyId, out AutomationPropertyDescriptor descriptor)
    {
        if (Descriptors.TryGetValue(propertyId, out descriptor!))
            return true;
        if (LayerEffectCatalog.TryResolveProperty(propertyId, out var effect, out var parameter))
        {
            descriptor = DynamicEffectDescriptor(effect, parameter);
            return true;
        }
        if (AudioEffectCatalog.TryResolveProperty(propertyId, out var audioEffect, out var audioParameter))
        {
            descriptor = DynamicAudioEffectDescriptor(audioEffect, audioParameter);
            return true;
        }
        descriptor = null!;
        return false;
    }

    public static AutomationPropertyDescriptor? Get(string propertyId) =>
        TryGet(propertyId, out var descriptor) ? descriptor : null;

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
            foreach (var effect in media.AudioEffects)
            {
                if (AudioEffectCatalog.Get(effect.EffectTypeId) is not { } definition)
                    continue;
                foreach (var parameter in definition.Parameters)
                    targets.Add(new AutomationTargetOption(
                        new AutomationTargetRef
                        {
                            PropertyId = AudioEffectCatalog.PropertyId(effect.EffectTypeId, parameter.Id),
                            ObjectId = effect.Id,
                        },
                        DynamicAudioEffectDescriptor(definition, parameter),
                        $"{definition.DisplayName} · {parameter.DisplayName}",
                        effect.Read(parameter.Id, parameter.Default)));
            }
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
            targets.Add(PlacementOption(
                placement, AutomationPropertyIds.PlacementX, "Position X", placement.X));
            targets.Add(PlacementOption(
                placement, AutomationPropertyIds.PlacementY, "Position Y", placement.Y));
            targets.Add(PlacementOption(
                placement, AutomationPropertyIds.PlacementWidth, "Width", placement.Width));
            targets.Add(PlacementOption(
                placement, AutomationPropertyIds.PlacementHeight, "Height", placement.Height));
            targets.Add(PlacementOption(
                placement, AutomationPropertyIds.PlacementRotation, "Rotation", placement.RotationDegrees));
            foreach (var effect in LayerEffectRack.Effective(placement))
            {
                if (LayerEffectCatalog.Get(effect.EffectTypeId) is not { } definition)
                    continue;
                foreach (var parameter in definition.Parameters)
                    targets.Add(EffectOption(
                        placement,
                        effect.Id,
                        LayerEffectCatalog.PropertyId(effect.EffectTypeId, parameter.Id),
                        $"{definition.DisplayName} · {parameter.DisplayName}",
                        effect.Read(parameter.Id, parameter.Default)));
            }
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

    private static AutomationTargetOption PlacementOption(
        LayerPlacement placement, string propertyId, string name, double authoredValue) =>
        new(
            new AutomationTargetRef { PropertyId = propertyId, ObjectId = placement.Id },
            Descriptors[propertyId],
            $"{name} · layer {placement.LayerIndex}",
            authoredValue);

    private static AutomationTargetOption EffectOption(
        LayerPlacement placement, Guid effectId, string propertyId, string name, double authoredValue) =>
        new(
            new AutomationTargetRef { PropertyId = propertyId, ObjectId = effectId },
            Get(propertyId)!,
            $"{name} · layer {placement.LayerIndex}",
            authoredValue);

    private static AutomationPropertyDescriptor DynamicEffectDescriptor(
        LayerEffectDefinition effect,
        EffectParameterDescriptor parameter) =>
        EffectDescriptor(
            LayerEffectCatalog.PropertyId(effect.TypeId, parameter.Id),
            $"{effect.DisplayName} · {parameter.DisplayName}",
            new AutomationValueSpec(
                parameter.Minimum,
                parameter.Maximum,
                parameter.Default,
                parameter.Unit,
                parameter.Scale switch
                {
                    EffectParameterScale.Decibels => AutomationScale.Decibels,
                    EffectParameterScale.Percentage => AutomationScale.Percentage,
                    _ => AutomationScale.Linear,
                }),
            effect.DisplayName);

    private static AutomationPropertyDescriptor DynamicAudioEffectDescriptor(
        LayerEffectDefinition effect,
        EffectParameterDescriptor parameter) =>
        new(
            AudioEffectCatalog.PropertyId(effect.TypeId, parameter.Id),
            $"{effect.DisplayName} · {parameter.DisplayName}",
            new AutomationValueSpec(
                parameter.Minimum,
                parameter.Maximum,
                parameter.Default,
                parameter.Unit,
                parameter.Scale switch
                {
                    EffectParameterScale.Decibels => AutomationScale.Decibels,
                    EffectParameterScale.Percentage => AutomationScale.Percentage,
                    _ => AutomationScale.Linear,
                }),
            AutomationTargetKind.EffectInstance,
            AutomationDomain.SessionAudio,
            AutomationComposition.ReplaceAuthored,
            effect.DisplayName);
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
    /// <summary>Samples a track directly. Convenient for editors, tests and one-shot reads; a driver
    /// sampling the same track repeatedly should <see cref="AutomationCurve.Prepare"/> once instead.</summary>
    public static double Sample(
        AutomationTrack? track,
        HaCueProject project,
        long timeMs,
        double authoredValue) =>
        AutomationCurve.Prepare(track, project) is { } curve
            ? curve.Sample(timeMs, authoredValue)
            : authoredValue;
}

/// <summary>
/// One track prepared for repeated sampling: keys sorted and filtered once, each segment's shape resolved
/// once, the descriptor looked up once.
/// </summary>
/// <remarks>
/// The drivers sample every track 40 times a second. Doing that straight off the document meant, per
/// sample: a <c>Where/OrderBy/ThenBy/ToList</c> over every keyframe (thousands, on a long cue), a linear
/// scan of the project's curve presets, construction of a fresh <c>CustomFadeCurve</c> - whose constructor
/// re-validates every point - and, for a malformed inline curve, a thrown-and-caught exception. None of
/// that changes between ticks: an authored edit goes through the journal and recompiles.
/// <para>A prepared curve is immutable and does not observe later edits to the track. Prepare a new one
/// when the document changes.</para>
/// </remarks>
public sealed class AutomationCurve
{
    private readonly AutomationKeyframe[] _keys;

    /// <summary>The resolved outgoing shape of the segment starting at each key. Parallel to
    /// <see cref="_keys"/>; the last entry is unused.</summary>
    private readonly FadeShape[] _shapes;

    private readonly AutomationValueSpec? _value;

    private AutomationCurve(AutomationKeyframe[] keys, FadeShape[] shapes, AutomationValueSpec? value)
    {
        _keys = keys;
        _shapes = shapes;
        _value = value;
    }

    public int KeyframeCount => _keys.Length;

    /// <summary>Null when the track is absent, disabled, or has no usable keyframes - the caller then uses
    /// its authored value.</summary>
    public static AutomationCurve? Prepare(AutomationTrack? track, HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (track is not { Enabled: true, Keyframes.Count: > 0 })
            return null;

        var keys = track.Keyframes
            .Where(key => key.TimeMs >= 0 && double.IsFinite(key.Value))
            .OrderBy(key => key.TimeMs)
            .ThenBy(key => key.Id)
            .ToArray();
        if (keys.Length == 0)
            return null;

        var shapes = new FadeShape[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            // A malformed inline curve degrades to linear ONCE here rather than throwing on every sample.
            try { shapes[index] = keys[index].Curve.Resolve(project); }
            catch (ArgumentException) { shapes[index] = FadeCurve.Linear; }
        }

        return new AutomationCurve(
            keys, shapes, AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Value);
    }

    /// <summary>The track's value at <paramref name="timeMs"/>. Allocation-free.</summary>
    public double Sample(long timeMs, double authoredValue)
    {
        if (_keys.Length == 0)
            return authoredValue;
        if (timeMs <= _keys[0].TimeMs)
            return Clamp(_keys[0].Value);
        if (timeMs >= _keys[^1].TimeMs)
            return Clamp(_keys[^1].Value);

        var low = 0;
        var high = _keys.Length - 1;
        while (low + 1 < high)
        {
            var middle = (low + high) / 2;
            if (_keys[middle].TimeMs <= timeMs)
                low = middle;
            else
                high = middle;
        }

        var from = _keys[low];
        var to = _keys[high];
        if (from.Hold || to.TimeMs <= from.TimeMs)
            return Clamp(from.Value);

        var progress = Math.Clamp((double)(timeMs - from.TimeMs) / (to.TimeMs - from.TimeMs), 0, 1);
        return Clamp(FadeCurves.Interpolate(from.Value, to.Value, progress, _shapes[low]));
    }

    private double Clamp(double value) => _value?.Clamp(value) ?? value;
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
