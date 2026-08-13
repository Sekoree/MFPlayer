using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Presentation;

/// <summary>
/// Turns a timeline group into lanes and clips (screen 05).
/// </summary>
/// <remarks>
/// <para>
/// Every position here is a FRACTION of the VIEW, computed from each child's
/// <see cref="CueNode.TimelineOffsetMs"/> and its media duration. That is why the timeline needed a start
/// offset on the model at all: without one the shell could only draw clips where somebody had typed
/// pixel positions, and the drawing would stop agreeing with the show the first time a cue moved.
/// </para>
/// <para>
/// The VIEW is a window onto the group rather than the whole of it, which is what zooming is. Fractions
/// are of the window, so a clip outside it lands outside 0..1 and is simply not drawn - the alternative,
/// clamping everything into range, would pile every off-screen cue against the edges as a row of
/// slivers that look like real clips.
/// </para>
/// <para>
/// Effect lanes appear only for cues that HAVE them (register item 18: hidden until added), so an
/// unautomated group is a clean set of clip lanes rather than a wall of empty rows.
/// </para>
/// </remarks>
public static class TimelinePresentation
{
    /// <summary>The lanes for a group: one per child, plus one per automation track the child carries.</summary>
    public static IReadOnlyList<TimelineLane> Lanes(
        GroupCueNode group, HaCueProject project, ShowRuntime runtime, TimelineView? view = null)
    {
        var window = view ?? TimelineView.Whole(SpanMs(group, runtime));
        var lanes = new List<TimelineLane>();

        foreach (var child in group.Children)
        {
            var start = window.Fraction(child.TimelineOffsetMs);
            var durationMs = Duration(child, runtime);
            var width = durationMs / window.LengthMs;

            lanes.Add(new TimelineLane
            {
                Name = Prefix(child) + $"{CuePresentation.Number(child.Number)} · {child.Label}"
                     + (child.Enabled ? "" : " · disabled"),
                SubjectId = child.Id,
                IsGroup = child is GroupCueNode,
                Clips =
                [
                    new TimelineClip
                    {
                        SubjectId = child.Id,
                        Label = ClipLabel(child, project),
                        // Not clamped into 0..1: a clip outside the window keeps its out-of-range
                        // position and simply is not drawn. Clamping would pile every off-screen cue
                        // against the edges as a row of slivers that look like real clips.
                        Left = start,
                        Width = Math.Max(width, 0.004),
                        Kind = ClipKind(child),
                        IsDisabled = !child.Enabled,
                    },
                ],
            });

            foreach (var track in CueAutomation.Of(child))
            {
                var descriptor = AutomationPropertyCatalog.Get(track.Target.PropertyId);
                var knots = track.Keyframes
                    .Where(key => key.TimeMs >= 0)
                    .OrderBy(key => key.TimeMs)
                    .ThenBy(key => key.Id)
                    .Select(key => new CurveKnot(
                        Math.Clamp(key.TimeMs / durationMs, 0, 1),
                        Normalize(key.Value, descriptor),
                        key.Hold,
                        key.Curve.Law))
                    .ToList();
                lanes.Add(new TimelineLane
                {
                    Name = $"auto · {Name(track, descriptor, child)}",
                    SubjectId = child.Id,
                    IsEffect = true,
                    EffectLaneId = track.Id,
                    EffectKind = track.Target.PropertyId,
                    EffectLeft = start,
                    EffectWidth = Math.Max(width, 0.004),
                    Points =
                    [
                        .. knots.Select(knot => new CurvePoint(knot.X, 1 - knot.Y)),
                    ],
                    Shape = CurveLibrary.Shape(knots),
                    // Lane points are already fractions of the CUE; scale them into the group's span so
                    // an envelope sits over the clip it belongs to rather than across the whole row.
                    Envelope =
                    [
                        .. knots.Select(point => new CurvePoint(
                            Math.Clamp(start + (point.X * width), 0, 1),
                            // A lane's Y is a LEVEL; the row draws top-down, so it is inverted here
                            // rather than in the control, which knows nothing about levels.
                            Math.Clamp(1 - point.Y, 0, 1))),
                    ],
                });
            }
        }

        return lanes;
    }

    /// <summary>The ruler's tick labels, at a sensible interval for what is on screen.</summary>
    /// <remarks>
    /// The interval follows the VIEW rather than the group: zoomed into ten seconds of a ten-minute
    /// show, a tick every five minutes is one tick, and the ruler stops being a ruler.
    /// </remarks>
    public static IReadOnlyList<string> Ruler(
        GroupCueNode group, ShowRuntime runtime, TimelineView? view = null)
    {
        var window = view ?? TimelineView.Whole(SpanMs(group, runtime));
        var step = Step(TimeSpan.FromMilliseconds(window.LengthMs)).TotalMilliseconds;
        var labels = new List<string>();

        // Snapped to the step, so the labels are round numbers wherever the window happens to start -
        // a ruler reading 3:07, 3:17, 3:27 is one nobody can place anything against.
        var first = Math.Floor(window.StartMs / step) * step;

        for (var at = first; at <= window.StartMs + window.LengthMs; at += step)
        {
            var moment = TimeSpan.FromMilliseconds(Math.Max(0, at));
            labels.Add($"{(int)moment.TotalMinutes}:{moment.Seconds:00}");
        }

        return labels;
    }

    /// <summary>A tick every half-second when zoomed right in, scaling up so the ruler never crowds.</summary>
    private static TimeSpan Step(TimeSpan span) => span.TotalSeconds switch
    {
        <= 5 => TimeSpan.FromSeconds(0.5),
        <= 15 => TimeSpan.FromSeconds(1),
        <= 40 => TimeSpan.FromSeconds(5),
        <= 60 => TimeSpan.FromSeconds(10),
        <= 180 => TimeSpan.FromSeconds(15),
        <= 600 => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// The group's length: the furthest a child reaches.
    /// </summary>
    /// <remarks>
    /// Never zero - a group whose media nobody has probed still has to draw, and dividing by its span
    /// must not produce infinities. One minute is an arbitrary but harmless floor for an empty group.
    /// </remarks>
    public static double SpanMs(GroupCueNode group, ShowRuntime runtime) => Span(group, runtime);

    private static double Span(GroupCueNode group, ShowRuntime runtime)
    {
        var furthest = group.Children
            .Select(child => child.TimelineOffsetMs + Duration(child, runtime))
            .DefaultIfEmpty(0)
            .Max();

        return furthest <= 0 ? 60_000 : furthest;
    }

    /// <summary>
    /// How long a child occupies the timeline: its TRIMMED length, not the file's.
    /// </summary>
    /// <remarks>
    /// A clip drawn at the file's full length after somebody trimmed it is a picture of a show that
    /// will not happen. Probing is a machine fact, so an unprobed cue with no explicit trim-out gets a
    /// nominal width - visible and obviously not measured, rather than a hairline that reads as a
    /// rendering fault.
    /// </remarks>
    private static double Duration(CueNode cue, ShowRuntime runtime)
    {
        var probed = runtime.MediaDurations.TryGetValue(cue.Id, out var duration)
            ? duration
            : (TimeSpan?)null;

        if (cue is MediaCueNode media)
            return media.TrimmedLength(probed)?.TotalMilliseconds ?? 8_000;
        if (cue is TextCueNode { DurationMs: > 0 } text)
            return text.DurationMs;

        return probed?.TotalMilliseconds ?? 8_000;
    }

    private static string Prefix(CueNode cue) => cue is GroupCueNode ? "▸ " : "▾ ";

    private static string ClipLabel(CueNode cue, HaCueProject project) => cue switch
    {
        MediaCueNode media when SourceUri.IsSource(media.MediaPath) => SourceUri.Describe(media.MediaPath),
        MediaCueNode media => Path.GetFileName(media.MediaPath) is { Length: > 0 } file
            ? $"{file} · {CuePresentation.Db(media.LevelDb)}"
            : cue.Label,
        GroupCueNode group => $"collapsed group · {group.Children.Count} cues",
        _ => cue.Label,
    };

    private static string ClipKind(CueNode cue) => CuePresentation.KindOf(cue) switch
    {
        CueKind.Video or CueKind.Visualizer or CueKind.Text => "vi",
        CueKind.Group => "gr",
        CueKind.Media => "au",
        _ => "ac",
    };

    /// <summary>A property value as a 0..1 lane height. Shared with the timeline's in-flight drag draft,
    /// so a dragged lane projects on exactly the same basis the committed one does.</summary>
    internal static double Normalize(double value, AutomationPropertyDescriptor? descriptor)
    {
        if (descriptor is null || descriptor.Value.Maximum <= descriptor.Value.Minimum)
            return Math.Clamp(value, 0, 1);
        return Math.Clamp(
            (value - descriptor.Value.Minimum) / (descriptor.Value.Maximum - descriptor.Value.Minimum), 0, 1);
    }

    private static string Name(
        AutomationTrack track, AutomationPropertyDescriptor? descriptor, CueNode cue)
    {
        var name = descriptor?.DisplayName ?? track.Target.PropertyId;
        if (track.Target.ObjectId is { } objectId
            && CuePlacements.Of(cue).FirstOrDefault(placement => placement.Id == objectId) is { } placement)
            name += $" · layer {placement.LayerIndex}";
        return name;
    }
}

/// <summary>
/// The window of a timeline that is on screen: where it starts and how much of it is shown.
/// </summary>
/// <remarks>
/// <para>
/// A VIEW state, not a document one. Where somebody has scrolled and how far in they have zoomed is
/// how they are working right now, and carrying it to the next venue inside a show file would be
/// carrying the wrong thing - the same reasoning as the appearance pane and the snap toggle.
/// </para>
/// <para>
/// Immutable, so every zoom produces a NEW window rather than mutating one under a draw that is
/// already halfway through reading it.
/// </para>
/// </remarks>
public sealed record TimelineView(double StartMs, double LengthMs)
{
    /// <summary>
    /// The shortest window worth showing.
    /// </summary>
    /// <remarks>
    /// Half a second is already the snap grid, so zooming past it buys nothing an operator can act on
    /// and takes the ruler somewhere its labels stop distinguishing one tick from the next.
    /// </remarks>
    public const double MinimumLengthMs = 500;

    /// <summary>The whole group - what FIT means, and what the sheet opens on.</summary>
    public static TimelineView Whole(double spanMs) => new(0, Math.Max(MinimumLengthMs, spanMs));

    /// <summary>Where a moment sits in the window, as a fraction. Outside 0..1 means off screen.</summary>
    public double Fraction(double milliseconds) => (milliseconds - StartMs) / LengthMs;

    /// <summary>The moment a fraction of the window's width points at.</summary>
    public double At(double fraction) => StartMs + (fraction * LengthMs);

    /// <summary>
    /// Zooms about the CENTRE of what is on screen.
    /// </summary>
    /// <remarks>
    /// The centre rather than the start, because the thing an operator is looking at is in the middle
    /// of the window - zooming about the left edge walks it off the right of the screen.
    /// </remarks>
    public TimelineView Zoom(double factor, double spanMs)
    {
        var limit = Math.Max(MinimumLengthMs, spanMs);
        var centre = StartMs + (LengthMs / 2);
        var length = Math.Clamp(LengthMs * factor, MinimumLengthMs, limit);

        return new TimelineView(
            Math.Clamp(centre - (length / 2), 0, Math.Max(0, limit - length)), length);
    }
}
