using HaCue2.Core.Model;

namespace HaCue2.Core.Validation;

/// <summary>Something in the project that points at the thing you asked about.</summary>
/// <param name="SubjectKind">What the referrer is — "cue", "snapshot", "patchCell", "outputGroup"…</param>
/// <param name="SubjectId">Its id, so the UI can navigate to it.</param>
/// <param name="Description">What the reference is, in the operator's words.</param>
public sealed record ProjectReference(string SubjectKind, string SubjectId, string Description);

/// <summary>
/// The "what targets this?" query, uniformly over every referable thing in a project.
/// </summary>
/// <remarks>
/// <para>
/// This is the delete-safety machinery the plan requires for logical channels, generalised — because
/// the equivalent question about cues is currently unanswered anywhere: a jump or fade cue targeting
/// a deleted cue is silently orphaned, and nothing tells the operator before they delete it.
/// </para>
/// <para>
/// It is also the reference count beside a logical output ("fed by 6 cues") and the inspector that
/// makes an automatic delete-cleanup safe to press: the operator can see what will be cleaned before
/// pressing it, which is the thing the old cancel/remove/rebind dialog could not give them.
/// </para>
/// </remarks>
public static class ProjectReferences
{
    public const string LogicalOutput = "logicalOutput";
    public const string Cue = "cue";
    public const string Snapshot = "snapshot";
    public const string Composition = "composition";
    public const string Endpoint = "endpoint";
    public const string AudioLine = "audioLine";
    public const string CurvePreset = "curvePreset";

    /// <summary>Everything pointing at <paramref name="id"/>, whatever kind of thing it is.</summary>
    public static IReadOnlyList<ProjectReference> To(HaCueProject project, string kind, Guid id) =>
        kind switch
        {
            LogicalOutput => ToLogicalOutput(project, id),
            Cue => ToCue(project, id),
            Snapshot => ToSnapshot(project, id),
            Composition => ToComposition(project, id),
            Endpoint => ToEndpoint(project, id),
            AudioLine => ToAudioLine(project, id),
            CurvePreset => ToCurvePreset(project, id),
            _ => [],
        };

    public static int CountTo(HaCueProject project, string kind, Guid id) => To(project, kind, id).Count;

    /// <summary>How many cues feed a logical output — the "fed by" column on screen 06.</summary>
    public static int CuesFeeding(HaCueProject project, Guid channelId) =>
        project.AllCues().OfType<MediaCueNode>()
            .Count(cue => cue.Sends.Any(send => send.LogicalChannelId == channelId));

    private static List<ProjectReference> ToLogicalOutput(HaCueProject project, Guid id)
    {
        var found = new List<ProjectReference>();

        foreach (var cue in project.AllCues())
            switch (cue)
            {
                case MediaCueNode media when media.Sends.Any(send => send.LogicalChannelId == id):
                    found.Add(CueRef(cue, "sends audio here"));
                    break;
                case PatchCueNode patch when patch.Levels.Any(level => level.LogicalChannelId == id):
                    found.Add(CueRef(cue, "changes its level"));
                    break;
                case FadeCueNode fade when fade.TargetChannelIds.Contains(id):
                    found.Add(CueRef(cue, "fades it"));
                    break;
            }

        foreach (var snapshot in project.PatchSnapshots
                     .Where(snapshot => snapshot.Cells.Any(cell => cell.LogicalChannelId == id)))
            found.Add(new ProjectReference(Snapshot, snapshot.Id.ToString(),
                $"snapshot “{snapshot.Name}” stores a cell for it"));

        foreach (var group in project.AudioPatch.Groups.Where(group => group.MemberIds.Contains(id)))
            found.Add(new ProjectReference("outputGroup", group.Id.ToString(),
                $"output group “{group.Name}” contains it"));

        var cells = project.AudioPatch.Cells.Count(cell => cell.LogicalChannelId == id);
        if (cells > 0)
            found.Add(new ProjectReference("patchCell", id.ToString(),
                $"the patch routes it to {cells} device channel{(cells == 1 ? "" : "s")}"));

        return found;
    }

    private static List<ProjectReference> ToCue(HaCueProject project, Guid id)
    {
        var found = new List<ProjectReference>();

        // The gap this query exists to close: today a deleted cue leaves every jump and fade pointing
        // at it silently orphaned, and the operator finds out when the jump does nothing.
        foreach (var cue in project.AllCues())
            switch (cue)
            {
                case JumpCueNode jump when jump.TargetCueIds.Contains(id):
                    found.Add(CueRef(cue, "jumps to it"));
                    break;
                case FadeCueNode fade when fade.TargetCueIds.Contains(id):
                    found.Add(CueRef(cue, "fades it"));
                    break;
                case MediaCueNode media when media.EndTargetCueId == id:
                    found.Add(CueRef(cue, "fires it at media end"));
                    break;
                case VisualizerCueNode visualizer when visualizer.FeedCueIds.Contains(id):
                    found.Add(CueRef(cue, "uses its audio in the visualizer feed"));
                    break;
                case AutomationCueNode automation when automation.AutomationTracks.Any(track => track.Target.CueId == id):
                    found.Add(CueRef(cue, "automates it"));
                    break;
            }

        foreach (var list in project.CueLists.Where(list => list.StandbyCueId == id))
            found.Add(new ProjectReference("cueList", list.Id.ToString(),
                $"“{list.Name}” has standby on it"));

        foreach (var input in project.TriggerInputs)
            foreach (var binding in input.Bindings.Where(binding => binding.TargetCueId == id))
                found.Add(new ProjectReference("triggerInput", input.Id.ToString(),
                    $"“{input.Name}” fires it from {binding.Input}"));

        return found;
    }

    private static List<ProjectReference> ToSnapshot(HaCueProject project, Guid id) =>
    [
        .. project.AllCues().OfType<PatchCueNode>()
            .Where(cue => cue.SnapshotId == id)
            .Select(cue => CueRef(cue, "recalls it")),
    ];

    private static List<ProjectReference> ToComposition(HaCueProject project, Guid id)
    {
        var found = new List<ProjectReference>();

        foreach (var cue in project.AllCues())
        {
            if (CuePlacements.Of(cue).Any(placement => placement.CompositionId == id))
                found.Add(CueRef(cue, "is placed on it"));
        }

        foreach (var output in project.VideoOutputs.Where(output => output.CompositionId == id))
            found.Add(new ProjectReference("videoOutput", output.Id.ToString(),
                $"“{output.Name}” shows it"));

        return found;
    }

    private static List<ProjectReference> ToEndpoint(HaCueProject project, Guid id)
    {
        var found = new List<ProjectReference>();

        foreach (var cue in project.AllCues())
        {
            if (cue is ActionCueNode action && action.EndpointId == id)
                found.Add(CueRef(cue, "sends to it"));

            if (CueAutomation.Of(cue).Any(track => track.Target.EndpointId == id))
                found.Add(CueRef(cue, "automates a value on it"));
            if (LegacyLanes(cue).Any(lane => lane.EndpointId == id))
                found.Add(CueRef(cue, "ramps a value on it"));
        }

        return found;
    }

    private static List<ProjectReference> ToAudioLine(HaCueProject project, Guid id)
    {
        var found = new List<ProjectReference>();

        var cells = project.AudioPatch.Cells.Count(cell => cell.LineId == id);
        if (cells > 0)
            found.Add(new ProjectReference("patchCell", id.ToString(),
                $"the patch uses {cells} of its channel{(cells == 1 ? "" : "s")}"));

        foreach (var snapshot in project.PatchSnapshots
                     .Where(snapshot => snapshot.Cells.Any(cell => cell.LineId == id)))
            found.Add(new ProjectReference(Snapshot, snapshot.Id.ToString(),
                $"snapshot “{snapshot.Name}” stores a cell on it"));

        // A patch cue can target a whole LINE ("everything Fold L feeds on the 18i20"), which is a
        // reference to the line and not only to the channel.
        foreach (var cue in project.AllCues().OfType<PatchCueNode>()
                     .Where(cue => cue.Levels.Any(level => level.LineId == id)))
            found.Add(CueRef(cue, "changes levels on it"));

        if (project.AudioPatch.ClockMasterLineId == id)
            found.Add(new ProjectReference("document", null!, "it is the clock master"));

        // The rig monitors THROUGH a line, so deleting that line silently sends audition back to the
        // bay's default — which is a different pair of speakers, discovered mid-show.
        if (project.Audition.AudioLineId == id)
            found.Add(new ProjectReference("document", null!, "the audition rig monitors through it"));

        return found;
    }

    private static List<ProjectReference> ToCurvePreset(HaCueProject project, Guid id)
    {
        var references = project.AllCues()
            .Where(cue => CurvesOf(cue).Any(curve => curve?.PresetId == id))
            .Select(cue => CueRef(cue, "uses it"))
            .ToList();
        if (project.Settings.StopFadeCurve.PresetId == id)
            references.Add(new ProjectReference("document", null!, "the project stop fade uses it"));
        return references;
    }

    /// <summary>Every curve a cue can reference - fades AND the per-segment shapes of its automation
    /// keyframes. Omitting the keyframes made "what uses this preset?" report zero references for a preset
    /// a track depended on, so deleting it looked safe and then raised a hard validator error afterwards.
    /// </summary>
    private static IEnumerable<CurveSpec?> CurvesOf(CueNode cue) =>
    [
        .. cue switch
        {
            MediaCueNode media => (CurveSpec?[])[media.FadeInCurve, media.FadeOutCurve],
            TextCueNode text => [text.FadeInCurve, text.FadeOutCurve],
            GroupCueNode group => [group.CrossfadeCurve],
            FadeCueNode fade => [fade.Curve],
            PatchCueNode patch => [patch.FadeCurve],
            _ => [],
        },
        .. CueAutomation.Of(cue).SelectMany(track => track.Keyframes.Select(key => (CurveSpec?)key.Curve)),
    ];

    private static IReadOnlyList<EffectLane> LegacyLanes(CueNode cue) => cue switch
    {
        MediaCueNode media => media.LegacyEffectLanes ?? [],
        TextCueNode text => text.LegacyEffectLanes ?? [],
        GroupCueNode group => group.LegacyEffectLanes ?? [],
        VisualizerCueNode visualizer => visualizer.LegacyEffectLanes ?? [],
        _ => [],
    };

    private static ProjectReference CueRef(CueNode cue, string what) =>
        new(Cue, cue.Id.ToString(), $"Q{cue.Number} {cue.Label} {what}".Replace("  ", " "));
}
