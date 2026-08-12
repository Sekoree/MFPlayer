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

        // Placement id repair is cheap, idempotent and must hold for hand-edited files at any schema.
        foreach (var cue in project.AllCues())
            foreach (var placement in CuePlacements.Of(cue))
            {
                if (placement.Id == Guid.Empty)
                    placement.Id = Guid.NewGuid();
            }

        // Nothing to convert: no schema-1 lane anywhere, and no earlier pass left one waiting on probe
        // facts. Returning here matters because EVERY deserialize runs this - and ProjectSnapshot.Copy is a
        // serialize/deserialize round trip, so a runtime snapshot was paying for a full migration walk
        // (plus a re-stamp of the migration summary) each time one was taken.
        // The test is the LANES, not the stamped version: a project built in code carries the current
        // schema number by default and may still have legacy lanes hand-assigned onto it.
        if (project.LastAutomationMigration is not { UnresolvedLanes: > 0 }
            && !project.AllCues().Any(cue => LegacyLanes(cue).Count > 0))
        {
            project.SchemaVersion = HaCueProject.CurrentSchemaVersion;
            return new Result(0, 0, 0);
        }

        var counts = new Counts();
        foreach (var cue in project.AllCues())
        {
            // Track ids are unique PROJECT-wide, so a migrated lane may not collide with a track that is
            // already there - whether hand-authored or minted by an earlier partial migration pass.
            foreach (var existing in CueAutomation.Of(cue))
                counts.ClaimTrackId(existing.Id);
        }

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
            Id = counts.ClaimTrackId(lane.Id),
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
            // Keyframe times are whole milliseconds and must be strictly increasing (the validator rejects
            // duplicates outright, and the compiler drops them). Never subdivide a segment into more steps
            // than it has milliseconds, or a short Bezier collapses into a pile of same-time keys and takes
            // the whole project down with it.
            var spanMs = At(duration, to.X) - At(duration, from.X);
            var subdivisions = from.OutHandleX is not null && to.InHandleX is not null
                ? (int)Math.Clamp(Math.Min(32, spanMs), 1, 32)
                : 1;
            for (var step = 0; step < subdivisions; step++)
            {
                var x = from.X + ((to.X - from.X) * step / subdivisions);
                Append(new AutomationKeyframe
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
        Append(new AutomationKeyframe
        {
            TimeMs = At(duration, points[^1].X),
            Value = valueMap(points[^1].Y),
            Curve = new CurveSpec { Law = FadeCurve.Linear },
        });
        return sampled;

        // Rounding can still land two samples on one millisecond at the ends of a very short lane. The
        // LATER sample wins: it is the one nearer the segment's authored endpoint value.
        void Append(AutomationKeyframe key)
        {
            if (sampled.Count > 0 && sampled[^1].TimeMs >= key.TimeMs)
                sampled[^1] = key with { Id = sampled[^1].Id, TimeMs = sampled[^1].TimeMs };
            else
                sampled.Add(key);
        }
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

        /// <summary>Track ids already handed out by this migration pass. A legacy GROUP lane is merged
        /// into every descendant as the SAME <see cref="EffectLane"/> object, so reusing its id for each
        /// child's first track minted duplicates - which the project-wide identity map then rejected,
        /// leaving the whole migrated show un-runnable.</summary>
        private readonly HashSet<Guid> _claimedTrackIds = [];

        /// <summary>The lane's own id when it is still free (stable identity across the migration), else a
        /// fresh one.</summary>
        public Guid ClaimTrackId(Guid preferred) =>
            preferred != Guid.Empty && _claimedTrackIds.Add(preferred)
                ? preferred
                : NewClaimedId();

        private Guid NewClaimedId()
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            }
            while (!_claimedTrackIds.Add(id));
            return id;
        }
    }
}
