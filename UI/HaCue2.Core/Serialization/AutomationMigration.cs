using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Serialization;

/// <summary>Converts schema-1 normalized effect lanes into schema-2 absolute-time property tracks.</summary>
public static class AutomationMigration
{
    public readonly record struct Result(int TracksCreated, int KeyframesCreated, int UnresolvedLanes)
    {
        public bool Changed => TracksCreated > 0 || KeyframesCreated > 0;
        public bool IsComplete => UnresolvedLanes == 0;
    }

    public static Result Migrate(
        HaCueProject project,
        IReadOnlyDictionary<Guid, TimeSpan>? durations = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var counts = new Counts();

        foreach (var cue in project.AllCues())
            foreach (var placement in CuePlacements.Of(cue))
                if (placement.Id == Guid.Empty)
                    placement.Id = Guid.NewGuid();

        foreach (var list in project.CueLists)
            foreach (var cue in list.Cues)
                Walk(cue, [], durations, counts);

        project.SchemaVersion = HaCueProject.CurrentSchemaVersion;
        var result = new Result(counts.Tracks, counts.Keys, counts.Unresolved);
        if (result.Changed || result.UnresolvedLanes > 0)
        {
            var previous = project.LastAutomationMigration;
            project.LastAutomationMigration = new AutomationMigrationSummary(
                (previous?.TracksCreated ?? 0) + result.TracksCreated,
                (previous?.KeyframesCreated ?? 0) + result.KeyframesCreated,
                result.UnresolvedLanes);
        }
        else if (project.LastAutomationMigration is { UnresolvedLanes: > 0 } pending)
        {
            project.LastAutomationMigration = pending with { UnresolvedLanes = 0 };
        }
        return result;
    }

    private static bool Walk(
        CueNode cue,
        IReadOnlyList<EffectLane> inherited,
        IReadOnlyDictionary<Guid, TimeSpan>? durations,
        Counts counts)
    {
        var own = LegacyLanes(cue);
        var effective = Merge(own, inherited);

        if (cue is GroupCueNode group)
        {
            var complete = true;
            foreach (var child in group.Children)
                complete &= Walk(child, effective, durations, counts);
            if (complete)
                group.LegacyEffectLanes = null;
            return complete;
        }

        if (effective.Count == 0)
        {
            ClearLegacy(cue);
            return true;
        }

        var duration = DurationOf(cue, durations);
        var completeForCue = true;
        foreach (var lane in effective)
        {
            if (lane.Points.Count == 0)
                continue;
            if (duration is not { TotalMilliseconds: > 0 })
            {
                counts.Unresolved++;
                completeForCue = false;
                continue;
            }

            Convert(cue, lane, duration.Value, counts);
        }

        if (completeForCue)
            ClearLegacy(cue);
        return completeForCue;
    }

    private static void Convert(
        CueNode cue,
        EffectLane lane,
        TimeSpan duration,
        Counts counts)
    {
        switch (lane.Kind)
        {
            case EffectLaneKind.Volume when cue is MediaCueNode media:
                AddTrack(
                    cue,
                    lane,
                    new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
                    duration,
                    factor => VolumeAt(media.LevelDb, factor),
                    counts);
                break;

            case EffectLaneKind.Opacity:
                foreach (var placement in CuePlacements.Of(cue))
                    AddTrack(
                        cue,
                        lane,
                        new AutomationTargetRef
                        {
                            PropertyId = AutomationPropertyIds.PlacementOpacity,
                            ObjectId = placement.Id,
                        },
                        duration,
                        factor => Math.Clamp(placement.Opacity * factor, 0, 1),
                        counts);
                break;

            case EffectLaneKind.OscRamp:
                AddTrack(
                    cue,
                    lane,
                    new AutomationTargetRef
                    {
                        PropertyId = AutomationPropertyIds.OscValue,
                        EndpointId = lane.EndpointId,
                        Address = lane.Address,
                        SendRateHz = 25,
                    },
                    duration,
                    value => Math.Clamp(value, 0, 1),
                    counts);
                break;

            case EffectLaneKind.MidiRamp:
                AddTrack(
                    cue,
                    lane,
                    new AutomationTargetRef
                    {
                        PropertyId = AutomationPropertyIds.MidiControlValue,
                        EndpointId = lane.EndpointId,
                        Address = lane.Address,
                        SendRateHz = 25,
                    },
                    duration,
                    value => Math.Round(Math.Clamp(value, 0, 1) * 127, MidpointRounding.AwayFromZero),
                    counts);
                break;
        }
    }

    private static void AddTrack(
        CueNode cue,
        EffectLane lane,
        AutomationTargetRef target,
        TimeSpan duration,
        Func<double, double> valueMap,
        Counts counts)
    {
        if (CueAutomation.ListOf(cue) is not { } tracks
            || tracks.Any(track => CueAutomation.SameTarget(track.Target, target)))
            return;

        var points = lane.Points
            .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .OrderBy(point => point.X)
            .ToList();
        if (points.Count == 0)
            return;

        var track = new AutomationTrack
        {
            Id = tracks.Count == 0 ? lane.Id : Guid.NewGuid(),
            Target = target,
            // Schema 1 always landed outbound lanes on their terminal value when stopped. Preserve
            // that observable behavior while new schema-2 tracks use the safer Freeze default.
            Interruption = target.PropertyId is AutomationPropertyIds.OscValue
                or AutomationPropertyIds.MidiControlValue
                    ? AutomationInterruption.LandFinal
                    : AutomationInterruption.Freeze,
            Keyframes = HasBezier(points)
                ? SampleBezier(points, duration, valueMap)
                : [.. points.Select(point => new AutomationKeyframe
                {
                    TimeMs = At(duration, point.X),
                    Value = valueMap(point.Y),
                    Curve = new CurveSpec { Law = point.CurveToNext },
                })],
        };

        tracks.Add(track);
        counts.Tracks++;
        counts.Keys += track.Keyframes.Count;
    }

    private static List<AutomationKeyframe> SampleBezier(
        IReadOnlyList<LanePoint> points,
        TimeSpan duration,
        Func<double, double> valueMap)
    {
        CustomFadeCurve curve;
        try
        {
            curve = new CustomFadeCurve([
                .. points.Select(point => new FadeCurvePoint(
                    point.X,
                    point.Y,
                    CurveToNext: point.CurveToNext,
                    OutHandleX: point.OutHandleX,
                    OutHandleLevel: point.OutHandleY,
                    InHandleX: point.InHandleX,
                    InHandleLevel: point.InHandleY)),
            ]);
        }
        catch (ArgumentException)
        {
            return [.. points.Select(point => new AutomationKeyframe
            {
                TimeMs = At(duration, point.X),
                Value = valueMap(point.Y),
                Curve = new CurveSpec { Law = point.CurveToNext },
            })];
        }

        var sampled = new List<AutomationKeyframe>();
        for (var index = 0; index + 1 < points.Count; index++)
        {
            var from = points[index];
            var to = points[index + 1];
            var subdivisions = from.OutHandleX is not null && to.InHandleX is not null ? 32 : 1;
            for (var step = 0; step < subdivisions; step++)
            {
                var x = from.X + ((to.X - from.X) * step / subdivisions);
                sampled.Add(new AutomationKeyframe
                {
                    TimeMs = At(duration, x),
                    Value = valueMap(curve.Evaluate(x)),
                    Curve = new CurveSpec
                    {
                        Law = subdivisions == 1 ? from.CurveToNext : FadeCurve.Linear,
                    },
                });
            }
        }
        sampled.Add(new AutomationKeyframe
        {
            TimeMs = At(duration, points[^1].X),
            Value = valueMap(points[^1].Y),
            Curve = new CurveSpec { Law = FadeCurve.Linear },
        });
        return sampled;
    }

    private static bool HasBezier(IReadOnlyList<LanePoint> points) =>
        points.Any(point => point.OutHandleX is not null || point.InHandleX is not null);

    private static long At(TimeSpan duration, double fraction) =>
        Math.Max(0, (long)Math.Round(duration.TotalMilliseconds * Math.Clamp(fraction, 0, 1)));

    private static double VolumeAt(double baseDb, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0.001)
            return GainRange.SilenceFloorDb;
        return Math.Clamp(baseDb + (20 * Math.Log10(factor)), GainRange.SilenceFloorDb, 12);
    }

    private static TimeSpan? DurationOf(
        CueNode cue,
        IReadOnlyDictionary<Guid, TimeSpan>? durations) => cue switch
    {
        MediaCueNode media => media.TrimmedLength(
            durations?.GetValueOrDefault(media.Id)
            ?? (media.SourceDurationMs > 0 ? TimeSpan.FromMilliseconds(media.SourceDurationMs) : null)),
        TextCueNode { DurationMs: > 0 } text => TimeSpan.FromMilliseconds(text.DurationMs),
        VisualizerCueNode { HoldMs: > 0 } visualizer => TimeSpan.FromMilliseconds(visualizer.HoldMs),
        _ => null,
    };

    private static IReadOnlyList<EffectLane> LegacyLanes(CueNode cue) => cue switch
    {
        MediaCueNode media => media.LegacyEffectLanes ?? [],
        TextCueNode text => text.LegacyEffectLanes ?? [],
        GroupCueNode group => group.LegacyEffectLanes ?? [],
        VisualizerCueNode visualizer => visualizer.LegacyEffectLanes ?? [],
        _ => [],
    };

    private static void ClearLegacy(CueNode cue)
    {
        switch (cue)
        {
            case MediaCueNode media: media.LegacyEffectLanes = null; break;
            case TextCueNode text: text.LegacyEffectLanes = null; break;
            case GroupCueNode group: group.LegacyEffectLanes = null; break;
            case VisualizerCueNode visualizer: visualizer.LegacyEffectLanes = null; break;
        }
    }

    private static IReadOnlyList<EffectLane> Merge(
        IReadOnlyList<EffectLane> nearest,
        IReadOnlyList<EffectLane> inherited) =>
        [
            .. nearest,
            .. inherited.Where(parent => nearest.All(lane => lane.Kind != parent.Kind)),
        ];

    private sealed class Counts
    {
        public int Tracks;
        public int Keys;
        public int Unresolved;
    }
}
