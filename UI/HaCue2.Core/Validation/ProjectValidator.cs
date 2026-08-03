using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Validation;

/// <summary>
/// The pure, headless pass over a loaded project: everything wrong that can be known without asking
/// the machine anything.
/// </summary>
/// <remarks>
/// <para>
/// This is one half of "Project status" (screen 14). The other half is environment-aware — missing
/// media files, absent devices, an unresolvable clock master — and needs to probe the machine, so it
/// lives beside this rather than inside it. Keeping them apart is what lets this one run in a script
/// over a committed fixture project and mean the same thing on any box.
/// </para>
/// <para>
/// Issues carry severity, subject kind and subject id because the status view needs all three per
/// row: to sort errors from warnings, and to jump to the thing the problem is about. Neither is
/// recoverable from prose, and parsing ids back out of a sentence works until somebody rewords a
/// message.
/// </para>
/// </remarks>
public static class ProjectValidator
{

    public static IReadOnlyList<ShowValidationIssue> Validate(HaCueProject project)
    {
        var issues = new List<ShowValidationIssue>();

        ValidateLogicalChannels(project, issues);
        ValidateGroups(project, issues);
        ValidatePatch(project, issues);
        ValidateSnapshots(project, issues);
        ValidateCompositionsAndOutputs(project, issues);
        ValidateCues(project, issues);
        ValidateTriggers(project, issues);

        return issues;
    }

    /// <summary>True when nothing would stop the show opening. Warnings do not.</summary>
    public static bool IsRunnable(IReadOnlyList<ShowValidationIssue> issues) =>
        !issues.Any(issue => issue.Severity == ShowValidationSeverity.Error);

    private static void ValidateLogicalChannels(HaCueProject project, List<ShowValidationIssue> issues)
    {
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in project.AudioPatch.LogicalChannels)
        {
            var id = channel.Id.ToString();

            if (!seenIds.Add(channel.Id))
                issues.Add(Error("logicalOutput", id, $"Duplicate logical output id {channel.Id}."));

            if (string.IsNullOrWhiteSpace(channel.Name))
                issues.Add(Error("logicalOutput", id, "A logical output has no name."));
            // Case-insensitively unique: "Main L" and "main l" on the same patch sheet is a mistake
            // waiting to be made under pressure, not a feature.
            else if (!seenNames.Add(channel.Name))
                issues.Add(Error("logicalOutput", id,
                    $"More than one logical output is called “{channel.Name}”."));
        }
    }

    private static void ValidateGroups(HaCueProject project, List<ShowValidationIssue> issues)
    {
        var claimed = new Dictionary<Guid, string>();

        foreach (var group in project.AudioPatch.Groups)
        {
            var id = group.Id.ToString();

            if (string.IsNullOrWhiteSpace(group.Name))
                issues.Add(Error("outputGroup", id, "An output group has no name."));

            foreach (var memberId in group.MemberIds)
            {
                if (project.FindChannel(memberId) is null)
                {
                    issues.Add(Error("outputGroup", id,
                        $"Group “{group.Name}” contains a logical output that no longer exists."));
                    continue;
                }

                // One channel, one group: linked-delta editing has no defined answer when two groups
                // both claim a channel and are nudged in opposite directions.
                if (claimed.TryGetValue(memberId, out var other))
                    issues.Add(Error("outputGroup", id,
                        $"“{project.FindChannel(memberId)!.Name}” is in both “{other}” and “{group.Name}”."));
                else
                    claimed[memberId] = group.Name;
            }
        }
    }

    private static void ValidatePatch(HaCueProject project, List<ShowValidationIssue> issues)
    {
        foreach (var cell in project.AudioPatch.Cells)
            ValidateCell(project, cell, "patchCell", issues);

        if (project.AudioPatch.ClockMasterLineId is { } masterId)
        {
            var master = project.FindLine(masterId);
            if (master is null)
                issues.Add(Error("document", null, "The clock master line no longer exists."));
            else if (master.SampleRate is { } rate && rate != project.AudioPatch.MixSampleRate)
                // Not an error: the environment pass decides whether the device can actually open at
                // the mix rate. This is only the document disagreeing with itself.
                issues.Add(Warn("audioLine", master.Id.ToString(),
                    $"Clock master “{master.Name}” declares {rate} Hz but the mix runs at "
                    + $"{project.AudioPatch.MixSampleRate} Hz; it must run natively at the mix rate."));
        }

        var fed = project.AllCues().OfType<MediaCueNode>()
            .SelectMany(cue => cue.Sends)
            .Select(send => send.LogicalChannelId)
            .ToHashSet();

        var patched = project.AudioPatch.Cells.Select(cell => cell.LogicalChannelId).ToHashSet();

        foreach (var channel in project.AudioPatch.LogicalChannels)
        {
            var id = channel.Id.ToString();

            // The two states screen 06 exists to catch. Fed-but-unpatched is an ERROR because the
            // sound silently vanishes; patched-but-unfed is a WARNING because a dead channel wastes
            // an output but nothing is lost (register item 25).
            if (fed.Contains(channel.Id) && !patched.Contains(channel.Id))
                issues.Add(Error("logicalOutput", id,
                    $"“{channel.Name}” is fed by cues but patched to nothing — those cues run silent."));
            else if (!fed.Contains(channel.Id) && patched.Contains(channel.Id))
                issues.Add(Warn("logicalOutput", id,
                    $"“{channel.Name}” is patched to hardware but no cue sends to it."));
        }
    }

    private static void ValidateSnapshots(HaCueProject project, List<ShowValidationIssue> issues)
    {
        foreach (var snapshot in project.PatchSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Name))
                issues.Add(Error("snapshot", snapshot.Id.ToString(), "A patch snapshot has no name."));

            foreach (var cell in snapshot.Cells)
                ValidateCell(project, cell, "snapshot", issues, snapshot.Id.ToString(),
                    $"Snapshot “{snapshot.Name}”");
        }
    }

    /// <summary>
    /// Checks one cell's references and gain.
    /// </summary>
    /// <remarks>
    /// A cell naming a deleted channel or a missing line is a BROKEN BINDING: reported, never silently
    /// dropped, and never applied to a neighbouring cell. Sliding it onto the next channel is how a
    /// recall ends up feeding the wrong speaker.
    /// </remarks>
    private static void ValidateCell(
        HaCueProject project,
        PatchCell cell,
        string subjectKind,
        List<ShowValidationIssue> issues,
        string? subjectId = null,
        string? prefix = null)
    {
        subjectId ??= cell.LogicalChannelId.ToString();
        var where = prefix is null ? "" : prefix + ": ";

        if (project.FindChannel(cell.LogicalChannelId) is null)
            issues.Add(Error(subjectKind, subjectId,
                $"{where}a cell targets a logical output that no longer exists."));

        var line = project.FindLine(cell.LineId);
        if (line is null)
            issues.Add(Error(subjectKind, subjectId, $"{where}a cell targets an audio line that no longer exists."));
        else if (cell.LineChannel < 0 || cell.LineChannel >= line.Channels)
            issues.Add(Error(subjectKind, subjectId,
                $"{where}a cell targets channel {cell.LineChannel + 1} of “{line.Name}”, which has "
                + $"{line.Channels}."));

        ValidateGain(cell.GainDb, subjectKind, subjectId, where + "cell", issues);
    }

    private static void ValidateGain(
        double gainDb, string subjectKind, string? subjectId, string what, List<ShowValidationIssue> issues)
    {
        if (!GainRange.IsStorable(gainDb))
        {
            issues.Add(Error(subjectKind, subjectId, $"A {what} gain is not a finite number."));
            return;
        }

        // A warning, not an error: the runtime is safe for any validated value, and refusing to open a
        // show because someone typed +14 dB would be worse than telling them.
        if (!GainRange.IsUsual(gainDb))
            issues.Add(Warn(subjectKind, subjectId,
                $"A {what} gain of {gainDb:0.#} dB is outside the usual {GainRange.SilenceFloorDb:0} to "
                + $"+{GainRange.MaximumDb:0} dB range."));
    }

    private static void ValidateCompositionsAndOutputs(HaCueProject project, List<ShowValidationIssue> issues)
    {
        foreach (var composition in project.Compositions)
        {
            var id = composition.Id.ToString();
            if (string.IsNullOrWhiteSpace(composition.Name))
                issues.Add(Error("composition", id, "A composition has no name."));
            if (composition.Width <= 0 || composition.Height <= 0)
                issues.Add(Error("composition", id, $"“{composition.Name}” has a zero or negative size."));
            if (composition.FramesPerSecond <= 0)
                issues.Add(Error("composition", id, $"“{composition.Name}” has a zero or negative frame rate."));
        }

        foreach (var output in project.VideoOutputs)
        {
            var id = output.Id.ToString();
            if (output.CompositionId is { } compositionId
                && project.Compositions.All(composition => composition.Id != compositionId))
                issues.Add(Error("videoOutput", id,
                    $"“{output.Name}” shows a composition that no longer exists."));
        }
    }

    private static void ValidateCues(HaCueProject project, List<ShowValidationIssue> issues)
    {
        var cueIds = project.AllCues().Select(cue => cue.Id).ToHashSet();
        var seenIds = new HashSet<Guid>();

        foreach (var list in project.CueLists)
        {
            if (string.IsNullOrWhiteSpace(list.Name))
                issues.Add(Error("cueList", list.Id.ToString(), "A cue list has no name."));

            var numbers = new HashSet<CueNumber>();
            foreach (var cue in list.Flatten())
            {
                var id = cue.Id.ToString();

                if (!seenIds.Add(cue.Id))
                    issues.Add(Error("cue", id, $"Duplicate cue id {cue.Id}."));

                if (!cue.Number.IsEmpty && !numbers.Add(cue.Number))
                    issues.Add(Error("cue", id,
                        $"“{list.Name}” has more than one cue numbered {cue.Number}."));

                // A media cue with no file is an unfinished cue, not a broken one — it is how every
                // cue starts. A warning, so it is visible without failing the checks, and named here
                // rather than left to the engine, which can only say "a clip had no path".
                if (cue is MediaCueNode { MediaPath.Length: 0 })
                    issues.Add(Warn("cue", id, $"Q{cue.Number} has no media file yet."));

                // A warning, matching the engine: a show with an unlabelled cue should open.
                if (string.IsNullOrWhiteSpace(cue.Label) && cue is not CommentCueNode)
                    issues.Add(Warn("cue", id, $"Q{cue.Number} has no label."));

                ValidateCue(project, cue, cueIds, issues);
            }

            if (list.StandbyCueId is { } standbyId && list.Flatten().All(cue => cue.Id != standbyId))
                issues.Add(Warn("cueList", list.Id.ToString(),
                    $"“{list.Name}” has standby on a cue that is no longer in it."));
        }
    }

    private static void ValidateCue(
        HaCueProject project, CueNode cue, HashSet<Guid> cueIds, List<ShowValidationIssue> issues)
    {
        var id = cue.Id.ToString();

        switch (cue)
        {
            case MediaCueNode media:
                foreach (var send in media.Sends)
                {
                    if (project.FindChannel(send.LogicalChannelId) is null)
                        issues.Add(Error("cue", id,
                            $"Q{cue.Number} sends to a logical output that no longer exists."));
                    ValidateGain(send.GainDb, "cue", id, $"Q{cue.Number} send", issues);
                }

                foreach (var placement in media.Placements)
                    ValidatePlacement(project, placement, cue, issues);
                ValidateLanes(media.EffectLanes, cue, issues);
                ValidateCurve(project, media.FadeInCurve, cue, issues);
                ValidateCurve(project, media.FadeOutCurve, cue, issues);
                break;

            case GroupCueNode group:
                ValidateLanes(group.EffectLanes, cue, issues);
                ValidateCurve(project, group.CrossfadeCurve, cue, issues);
                break;

            case ActionCueNode action when action.EndpointId is { } endpointId
                                           && project.ActionEndpoints.All(e => e.Id != endpointId):
                issues.Add(Error("cue", id, $"Q{cue.Number} sends to an endpoint that no longer exists."));
                break;

            // A MIDI message that will not parse is found HERE, on a laptop with no interface in it,
            // rather than at the moment the cue fires — which is the one moment nobody can act on it.
            case ActionCueNode action when action.EndpointId is { } endpointId
                                           && project.ActionEndpoints.FirstOrDefault(e => e.Id == endpointId)
                                               is { Kind: EndpointKind.MidiOut }
                                           && MidiActions.TryParse(
                                               action.Address, action.Arguments, out _) is { } wrong:
                issues.Add(Error("cue", id, $"Q{cue.Number} sends {wrong}"));
                break;

            case FadeCueNode fade:
                foreach (var targetId in fade.TargetCueIds.Where(target => !cueIds.Contains(target)))
                    issues.Add(Error("cue", id, $"Q{cue.Number} fades a cue that is no longer in the show."));
                foreach (var channelId in fade.TargetChannelIds
                             .Where(channel => project.FindChannel(channel) is null))
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} fades a logical output that no longer exists."));
                ValidateCurve(project, fade.Curve, cue, issues);
                break;

            // A jump with nowhere to go is an error rather than a quiet no-op: mid-show it looks
            // exactly like a cue that did not fire.
            case JumpCueNode jump when jump.TargetCueIds.Count == 0:
                issues.Add(Error("cue", id, $"Q{cue.Number} is a jump with no target."));
                break;

            case JumpCueNode jump:
                foreach (var targetId in jump.TargetCueIds.Where(target => !cueIds.Contains(target)))
                    issues.Add(Error("cue", id, $"Q{cue.Number} jumps to a cue that is no longer in the show."));
                break;

            case VisualizerCueNode visualizer:
                foreach (var placement in visualizer.Placements)
                    ValidatePlacement(project, placement, cue, issues);
                ValidateLanes(visualizer.EffectLanes, cue, issues);
                break;

            case PatchCueNode patchCue:
                if (patchCue.SnapshotId is { } snapshotId
                    && project.PatchSnapshots.All(snapshot => snapshot.Id != snapshotId))
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} recalls a snapshot that no longer exists."));

                foreach (var change in patchCue.Levels)
                {
                    if (project.FindChannel(change.LogicalChannelId) is null)
                        issues.Add(Error("cue", id,
                            $"Q{cue.Number} changes a logical output that no longer exists."));

                    if (change.LineId is { } lineId && project.FindLine(lineId) is null)
                        issues.Add(Error("cue", id, $"Q{cue.Number} changes a line that no longer exists."));

                    ValidateGain(change.GainDb, "cue", id, $"Q{cue.Number} level change", issues);
                }

                ValidateCurve(project, patchCue.FadeCurve, cue, issues);
                break;
        }
    }

    private static void ValidatePlacement(
        HaCueProject project, LayerPlacement? placement, CueNode cue, List<ShowValidationIssue> issues)
    {
        if (placement is null)
            return;

        if (project.Compositions.All(composition => composition.Id != placement.CompositionId))
            issues.Add(Error("cue", cue.Id.ToString(),
                $"Q{cue.Number} is placed on a composition that no longer exists."));
    }

    private static void ValidateLanes(
        IReadOnlyList<EffectLane> lanes, CueNode cue, List<ShowValidationIssue> issues)
    {
        var id = cue.Id.ToString();

        foreach (var lane in lanes)
        {
            // Points must advance. A lane whose X went backwards would evaluate differently depending
            // on how it was walked, which is the kind of ambiguity that shows up once, live.
            var previousX = double.NegativeInfinity;
            foreach (var point in lane.Points)
            {
                if (point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1)
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} has a {lane.Kind} lane point outside the 0–1 range."));
                if (point.X < previousX)
                    issues.Add(Error("cue", id, $"Q{cue.Number} has an out-of-order {lane.Kind} lane."));
                previousX = point.X;
            }

            if (lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp && lane.EndpointId is null)
                issues.Add(Error("cue", id,
                    $"Q{cue.Number} has an outbound {lane.Kind} lane with no endpoint."));
        }
    }

    private static void ValidateCurve(
        HaCueProject project, CurveSpec? curve, CueNode cue, List<ShowValidationIssue> issues)
    {
        if (curve?.PresetId is not { } presetId)
            return;

        if (project.CurvePresets.All(preset => preset.Id != presetId))
            issues.Add(Error("cue", cue.Id.ToString(),
                $"Q{cue.Number} uses a curve preset that no longer exists."));
    }

    private static void ValidateTriggers(HaCueProject project, List<ShowValidationIssue> issues)
    {
        foreach (var input in project.TriggerInputs)
            foreach (var binding in input.Bindings)
            {
                // A clock binding whose time will not parse is a cue that can never fire, and unlike a
                // wire binding there is no device to blame for it — so it is found here, while the show
                // is being written, rather than by its absence on the night.
                if (TriggerTimes.Refuse(input.Kind, binding.Input) is { } wrong)
                    issues.Add(Error("triggerInput", input.Id.ToString(),
                        $"“{input.Name}” has a binding on {wrong}"));

                if (binding.Target != TriggerTarget.Cue)
                    continue;

                if (binding.TargetCueId is not { } cueId || project.FindCue(cueId) is null)
                    issues.Add(Error("triggerInput", input.Id.ToString(),
                        $"“{input.Name}” has a binding on {binding.Input} with no live cue."));
            }
    }

    private static ShowValidationIssue Error(string kind, string? id, string message) =>
        new(ShowValidationSeverity.Error, message, kind, id);

    private static ShowValidationIssue Warn(string kind, string? id, string message) =>
        new(ShowValidationSeverity.Warning, message, kind, id);
}
