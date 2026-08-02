using System.Globalization;
using HaCue2.Core.Model;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Presentation;

/// <summary>
/// Turns cue documents into the rows the tree binds to.
/// </summary>
/// <remarks>
/// <para>
/// One place, so the cue tree, the scoped tree and the timeline all describe a cue the same way. When
/// the mockup and the model disagreed about a glyph or a label, the rule was decided here rather than
/// per screen.
/// </para>
/// <para>
/// <b>Document facts and runtime facts are separated deliberately.</b> A cue's number, label, level and
/// fade come from the document. Whether it is SOUNDING, whether its media is missing, and how long the
/// media runs are things only a machine or a session can answer, so they arrive through
/// <see cref="ShowRuntime"/> and never get invented here.
/// </para>
/// </remarks>
public static class CuePresentation
{
    /// <summary>
    /// A list's cues as a TREE: top-level rows, each group carrying its children.
    /// </summary>
    /// <remarks>
    /// Nested rather than flattened-with-a-depth-number because the tree control indents, expands and
    /// collapses from the shape itself. A flat list with an indent margin could only ever LOOK like a
    /// hierarchy, and a group was indistinguishable from a cue that happened to be indented.
    /// </remarks>
    public static IReadOnlyList<CueRow> Rows(CueList list, HaCueProject project, ShowRuntime runtime) =>
        [.. list.Cues.Select(cue => Row(cue, project, runtime, depth: 0))];

    /// <summary>The rows for one subtree — the scoped view (screen 03) narrows to exactly this.</summary>
    public static IReadOnlyList<CueRow> Subtree(CueNode root, HaCueProject project, ShowRuntime runtime) =>
        [Row(root, project, runtime, depth: 0)];

    /// <summary>Every row of a tree, in fire order — what a flat operation walks.</summary>
    public static IEnumerable<CueRow> Flatten(IEnumerable<CueRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;

            foreach (var child in Flatten(row.Children))
                yield return child;
        }
    }

    private static CueRow Row(CueNode cue, HaCueProject project, ShowRuntime runtime, int depth)
    {
        var standby = project.CueLists.Any(list => list.StandbyCueId == cue.Id);

        return new CueRow
        {
            Id = cue.Id,
            Number = Number(cue.Number),
            Label = cue.Label,
            Kind = KindOf(cue),
            Source = Source(cue, project, runtime),
            Fade = Fade(cue),
            Length = Length(cue, runtime),
            Level = Level(cue),
            Badges = Badges(cue, project, runtime),
            Depth = depth,
            Children = cue is GroupCueNode group
                ? [.. group.Children.Select(child => Row(child, project, runtime, depth + 1))]
                : [],
            IsRunning = runtime.Sounding.Contains(cue.Id),
            IsStandby = standby,
            IsBroken = runtime.Broken.Contains(cue.Id),
            IsDisabled = !cue.Enabled,
        };
    }

    /// <summary>
    /// "12", "12.5", "13.1" — trailing zeros trimmed.
    /// </summary>
    /// <remarks>
    /// Invariant-formatted: a cue number is an identifier an operator calls over comms, and "13,1" on a
    /// German machine is a different thing to say out loud than "13.1".
    /// </remarks>
    public static string Number(CueNumber number) => number.Text;

    /// <summary>
    /// A media cue carrying a placement IS the mockup's video cue.
    /// </summary>
    /// <remarks>
    /// The model has no separate video kind on purpose — a cue with a picture and a cue with sound are
    /// the same thing with different members, and splitting them would mean two code paths for a cue
    /// that has both.
    /// </remarks>
    public static CueKind KindOf(CueNode cue) => cue switch
    {
        MediaCueNode { Placement: not null } => ViewModels.CueKind.Video,
        MediaCueNode => ViewModels.CueKind.Media,
        GroupCueNode => ViewModels.CueKind.Group,
        ActionCueNode => ViewModels.CueKind.Action,
        FadeCueNode => ViewModels.CueKind.Fade,
        JumpCueNode => ViewModels.CueKind.Jump,
        VisualizerCueNode => ViewModels.CueKind.Visualizer,
        PatchCueNode => ViewModels.CueKind.Patch,
        _ => ViewModels.CueKind.Comment,
    };

    private static string Source(CueNode cue, HaCueProject project, ShowRuntime runtime) => cue switch
    {
        // A missing file replaces the path with why it matters, because on this row the path is no
        // longer the useful fact.
        MediaCueNode media when runtime.Broken.Contains(cue.Id) =>
            $"media offline · {Path.GetFileName(media.MediaPath)}",
        MediaCueNode media => media.MediaPath,

        GroupCueNode group =>
            $"{group.FireMode.ToString().ToLowerInvariant()} group · {group.Children.Count}",

        ActionCueNode action =>
            $"{EndpointLabel(project, action.EndpointId)} {action.Address}".Trim(),

        FadeCueNode fade => FadeSource(fade, project),

        JumpCueNode { TargetCueIds.Count: 0 } => "no target",
        JumpCueNode jump =>
            $"→ Q{Number(project.FindCue(jump.TargetCueIds[0])?.Number ?? CueNumber.Empty)}"
            + (jump.Condition == JumpCondition.WhileTriggerHeld ? " · while held" : ""),

        VisualizerCueNode visualizer => $"projectM · {visualizer.PresetPack}",

        PatchCueNode patch => patch.SnapshotId is { } id
            ? $"snapshot “{project.PatchSnapshots.FirstOrDefault(s => s.Id == id)?.Name ?? "?"}”"
            : $"{patch.Levels.Count} level change{(patch.Levels.Count == 1 ? "" : "s")}",

        _ => "comment",
    };

    private static string FadeSource(FadeCueNode fade, HaCueProject project)
    {
        var names = fade.TargetChannelIds
            .Select(id => project.FindChannel(id)?.Name)
            .OfType<string>()
            .ToList();

        if (names.Count == 0)
            return fade.FadeEverythingSounding ? "everything sounding" : "no target";

        // Two names read; five do not. Past two it is a count.
        var where = names.Count <= 2 ? string.Join(" + ", names) : $"{names.Count} outputs";
        return $"{where} · to {Db(fade.ToLevelDb)}";
    }

    private static string EndpointLabel(HaCueProject project, Guid? endpointId)
    {
        if (endpointId is not { } id)
            return "";

        var endpoint = project.ActionEndpoints.FirstOrDefault(item => item.Id == id);
        return endpoint?.Kind switch
        {
            EndpointKind.OscOut => "OSC",
            EndpointKind.MidiOut => "MIDI",
            _ => "",
        };
    }

    private static string Fade(CueNode cue) => cue switch
    {
        MediaCueNode { FadeInMs: > 0 } media => Seconds(media.FadeInMs),
        FadeCueNode fade => Seconds(fade.DurationMs),
        PatchCueNode { FadeMs: > 0 } patch => Seconds(patch.FadeMs),
        VisualizerCueNode visualizer => Seconds(visualizer.BlendMs),
        _ => "—",
    };

    /// <summary>
    /// A media file's duration is a MACHINE fact — it comes from probing the file, not from the
    /// document — so it arrives through the runtime and reads "—" until something has looked.
    /// </summary>
    private static string Length(CueNode cue, ShowRuntime runtime) =>
        runtime.MediaDurations.TryGetValue(cue.Id, out var duration)
            ? duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture)
            : "—";

    private static string Level(CueNode cue) => cue switch
    {
        MediaCueNode media => Db(media.LevelDb),
        _ => "—",
    };

    private static IReadOnlyList<Badge> Badges(CueNode cue, HaCueProject project, ShowRuntime runtime)
    {
        var badges = new List<Badge>();

        switch (cue)
        {
            case MediaCueNode media:
                if (media.Loop)
                    badges.Add(new Badge("loop"));
                if (media.Placement is { } placement)
                    badges.Add(new Badge(CompositionName(project, placement)));
                foreach (var lane in media.EffectLanes)
                    badges.Add(LaneBadge(lane));
                break;

            case GroupCueNode group:
                badges.Add(new Badge(group.FireMode.ToString().ToLowerInvariant()));
                break;

            case ActionCueNode action:
                var endpoint = project.ActionEndpoints.FirstOrDefault(item => item.Id == action.EndpointId);
                badges.Add(endpoint?.Kind == EndpointKind.MidiOut
                    ? new Badge("MIDI", Gel.Congo)
                    : new Badge("OSC", Gel.Steel));
                break;

            case VisualizerCueNode visualizer:
                if (visualizer.Placement is { } visualizerPlacement)
                    badges.Add(new Badge(CompositionName(project, visualizerPlacement)));
                break;

            case PatchCueNode:
                badges.Add(new Badge("patch"));
                break;

            case JumpCueNode:
                badges.Add(new Badge("MIDI", Gel.Congo));
                break;
        }

        if (!cue.Enabled)
            badges.Add(new Badge("disabled"));

        if (runtime.Broken.Contains(cue.Id))
            badges.Add(new Badge("offline", Gel.Red));

        return badges;
    }

    private static Badge LaneBadge(EffectLane lane) => lane.Kind switch
    {
        EffectLaneKind.Volume => new Badge($"env {lane.Points.Count}"),
        EffectLaneKind.Opacity => new Badge($"opac {lane.Points.Count}"),
        _ => new Badge(lane.Kind == EffectLaneKind.OscRamp ? "OSC ramp" : "MIDI ramp", Gel.Steel),
    };

    private static string CompositionName(HaCueProject project, LayerPlacement placement) =>
        project.Compositions.FirstOrDefault(item => item.Id == placement.CompositionId)?.Name ?? "?";

    /// <summary>Seconds to one place: "3.0", "0.5".</summary>
    public static string Seconds(int milliseconds) =>
        (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>A level with its sign, as a console shows it: "−6.0", "+2.0", "0.0".</summary>
    public static string Db(double value)
    {
        if (value <= GainRange.SilenceFloorDb)
            return "−inf";

        // U+2212 MINUS SIGN, not a hyphen: it aligns with digits in a tabular column, which a hyphen
        // does not, and every number in this app sits in one.
        var text = Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture);
        return value switch
        {
            > 0 => "+" + text,
            < 0 => "−" + text,
            _ => "0.0",
        };
    }
}
