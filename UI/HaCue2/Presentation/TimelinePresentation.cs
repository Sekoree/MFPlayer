using HaCue2.Core.Model;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Presentation;

/// <summary>
/// Turns a timeline group into lanes and clips (screen 05).
/// </summary>
/// <remarks>
/// <para>
/// Every position here is a FRACTION of the group's span, computed from each child's
/// <see cref="CueNode.StartOffsetMs"/> and its media duration. That is why the timeline needed a start
/// offset on the model at all: without one the shell could only draw clips where somebody had typed
/// pixel positions, and the drawing would stop agreeing with the show the first time a cue moved.
/// </para>
/// <para>
/// Effect lanes appear only for cues that HAVE them (register item 18: hidden until added), so an
/// unautomated group is a clean set of clip lanes rather than a wall of empty rows.
/// </para>
/// </remarks>
public static class TimelinePresentation
{
    /// <summary>The lanes for a group: one per child, plus one per effect lane the child carries.</summary>
    public static IReadOnlyList<TimelineLane> Lanes(
        GroupCueNode group, HaCueProject project, ShowRuntime runtime)
    {
        var span = Span(group, runtime);
        var lanes = new List<TimelineLane>();

        foreach (var child in group.Children)
        {
            var start = child.StartOffsetMs / span;
            var width = Duration(child, runtime) / span;

            lanes.Add(new TimelineLane
            {
                Name = Prefix(child) + $"{CuePresentation.Number(child.Number)} · {child.Label}"
                     + (child.Enabled ? "" : " · disabled"),
                IsGroup = child is GroupCueNode,
                Clips =
                [
                    new TimelineClip
                    {
                        Label = ClipLabel(child, project),
                        Left = Math.Clamp(start, 0, 1),
                        Width = Math.Clamp(width, 0.01, 1 - Math.Clamp(start, 0, 1)),
                        Kind = ClipKind(child),
                        IsDisabled = !child.Enabled,
                    },
                ],
            });

            foreach (var lane in EffectLanes(child))
                lanes.Add(new TimelineLane
                {
                    Name = $"fx · {Name(lane.Kind)}",
                    IsEffect = true,
                    // Lane points are already fractions of the CUE; scale them into the group's span so
                    // an envelope sits over the clip it belongs to rather than across the whole row.
                    Envelope =
                    [
                        .. lane.Points.Select(point => new CurvePoint(
                            Math.Clamp(start + (point.X * width), 0, 1),
                            // A lane's Y is a LEVEL; the row draws top-down, so it is inverted here
                            // rather than in the control, which knows nothing about levels.
                            Math.Clamp(1 - point.Y, 0, 1))),
                    ],
                });
        }

        return lanes;
    }

    /// <summary>The ruler's tick labels, at a sensible interval for the group's length.</summary>
    public static IReadOnlyList<string> Ruler(GroupCueNode group, ShowRuntime runtime)
    {
        var span = TimeSpan.FromMilliseconds(Span(group, runtime));
        var step = Step(span);
        var labels = new List<string>();

        for (var at = TimeSpan.Zero; at <= span; at += step)
            labels.Add($"{(int)at.TotalMinutes}:{at.Seconds:00}");

        return labels;
    }

    /// <summary>A tick every 15 s for a short group, scaling up so the ruler never crowds.</summary>
    private static TimeSpan Step(TimeSpan span) => span.TotalSeconds switch
    {
        <= 60 => TimeSpan.FromSeconds(10),
        <= 180 => TimeSpan.FromSeconds(15),
        <= 600 => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// The group's length: the furthest a child reaches.
    /// </summary>
    /// <remarks>
    /// Never zero — a group whose media nobody has probed still has to draw, and dividing by its span
    /// must not produce infinities. One minute is an arbitrary but harmless floor for an empty group.
    /// </remarks>
    private static double Span(GroupCueNode group, ShowRuntime runtime)
    {
        var furthest = group.Children
            .Select(child => child.StartOffsetMs + Duration(child, runtime))
            .DefaultIfEmpty(0)
            .Max();

        return furthest <= 0 ? 60_000 : furthest;
    }

    private static double Duration(CueNode cue, ShowRuntime runtime) =>
        runtime.MediaDurations.TryGetValue(cue.Id, out var duration)
            ? duration.TotalMilliseconds
            // An unprobed cue gets a nominal width so it is visible and obviously not measured, rather
            // than collapsing to a hairline that reads as a rendering fault.
            : 8_000;

    private static IReadOnlyList<EffectLane> EffectLanes(CueNode cue) => cue switch
    {
        MediaCueNode media => media.EffectLanes,
        GroupCueNode group => group.EffectLanes,
        VisualizerCueNode visualizer => visualizer.EffectLanes,
        _ => [],
    };

    private static string Prefix(CueNode cue) => cue is GroupCueNode ? "▸ " : "▾ ";

    private static string ClipLabel(CueNode cue, HaCueProject project) => cue switch
    {
        MediaCueNode media => Path.GetFileName(media.MediaPath) is { Length: > 0 } file
            ? $"{file} · {CuePresentation.Db(media.LevelDb)}"
            : cue.Label,
        GroupCueNode group => $"collapsed group · {group.Children.Count} cues",
        _ => cue.Label,
    };

    private static string ClipKind(CueNode cue) => CuePresentation.KindOf(cue) switch
    {
        CueKind.Video or CueKind.Visualizer => "vi",
        CueKind.Group => "gr",
        CueKind.Media => "au",
        _ => "ac",
    };

    private static string Name(EffectLaneKind kind) => kind switch
    {
        EffectLaneKind.Volume => "volume",
        EffectLaneKind.Opacity => "opacity",
        EffectLaneKind.OscRamp => "OSC ramp",
        _ => "MIDI ramp",
    };
}
