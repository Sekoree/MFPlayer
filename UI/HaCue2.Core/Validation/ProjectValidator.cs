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

        ValidateDefinitions(project, issues);
        ValidateLogicalChannels(project, issues);
        ValidateGroups(project, issues);
        ValidatePatch(project, issues);
        ValidateSnapshots(project, issues);
        ValidateCompositionsAndOutputs(project, issues);
        ValidateCues(project, issues);
        ValidateTriggers(project, issues);

        return issues;
    }

    private static void ValidateDefinitions(HaCueProject project, List<ShowValidationIssue> issues)
    {
        var identities = new Dictionary<Guid, string>();
        void Identity(Guid id, string kind, string? subjectId = null)
        {
            if (id == Guid.Empty)
                issues.Add(Error(kind, subjectId, $"A {kind} has an empty id."));
            else if (!identities.TryAdd(id, kind))
                issues.Add(Error(kind, subjectId ?? id.ToString(),
                    $"The id {id} is shared by a {identities[id]} and a {kind}."));
        }

        foreach (var list in project.CueLists)
            Identity(list.Id, "cueList", list.Id.ToString());
        foreach (var cue in project.AllCues())
        {
            Identity(cue.Id, "cue", cue.Id.ToString());
            var lanes = cue switch
            {
                MediaCueNode media => media.EffectLanes,
                TextCueNode text => text.EffectLanes,
                VisualizerCueNode visualizer => visualizer.EffectLanes,
                GroupCueNode group => group.EffectLanes,
                _ => [],
            };
            foreach (var lane in lanes)
                Identity(lane.Id, "effectLane", cue.Id.ToString());
        }
        foreach (var channel in project.AudioPatch.LogicalChannels)
            Identity(channel.Id, "logicalOutput", channel.Id.ToString());
        foreach (var group in project.AudioPatch.Groups)
            Identity(group.Id, "outputGroup", group.Id.ToString());
        foreach (var line in project.AudioLines)
        {
            Identity(line.Id, "audioLine", line.Id.ToString());
            if (string.IsNullOrWhiteSpace(line.Name))
                issues.Add(Error("audioLine", line.Id.ToString(), "An audio line has no name."));
            if (line.Channels is < 1 or > 64)
                issues.Add(Error("audioLine", line.Id.ToString(),
                    $"Audio line “{line.Name}” has {line.Channels} channels; use 1–64."));
            if (line.SampleRate is <= 0)
                issues.Add(Error("audioLine", line.Id.ToString(),
                    $"Audio line “{line.Name}” has a zero or negative sample rate."));
        }
        foreach (var snapshot in project.PatchSnapshots)
            Identity(snapshot.Id, "snapshot", snapshot.Id.ToString());
        foreach (var composition in project.Compositions)
            Identity(composition.Id, "composition", composition.Id.ToString());
        foreach (var output in project.VideoOutputs)
        {
            Identity(output.Id, "videoOutput", output.Id.ToString());
            foreach (var section in output.Mapping)
                Identity(section.Id, "mappingSection", output.Id.ToString());
        }

        var endpointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in project.ActionEndpoints)
        {
            Identity(endpoint.Id, "endpoint", endpoint.Id.ToString());
            if (string.IsNullOrWhiteSpace(endpoint.Name))
                issues.Add(Error("endpoint", endpoint.Id.ToString(), "An action endpoint has no name."));
            else if (!endpointNames.Add(endpoint.Name))
                issues.Add(Error("endpoint", endpoint.Id.ToString(),
                    $"More than one action endpoint is called “{endpoint.Name}”."));
            if (endpoint.Kind == EndpointKind.OscOut
                && (endpoint.Port is < 1 or > 65_535 || string.IsNullOrWhiteSpace(endpoint.Host)))
                issues.Add(Error("endpoint", endpoint.Id.ToString(),
                    $"OSC endpoint “{endpoint.Name}” needs a host and a port from 1–65535."));
        }

        foreach (var input in project.TriggerInputs)
        {
            Identity(input.Id, "triggerInput", input.Id.ToString());
            if (string.IsNullOrWhiteSpace(input.Name))
                issues.Add(Error("triggerInput", input.Id.ToString(), "A trigger input has no name."));
            if (input.Kind == TriggerInputKind.OscIn && input.Port is < 1 or > 65_535)
                issues.Add(Error("triggerInput", input.Id.ToString(),
                    $"OSC input “{input.Name}” needs a port from 1–65535."));
            foreach (var binding in input.Bindings)
                Identity(binding.Id, "triggerBinding", input.Id.ToString());
        }

        foreach (var preset in project.CurvePresets)
        {
            Identity(preset.Id, "curvePreset", preset.Id.ToString());
            if (string.IsNullOrWhiteSpace(preset.Name))
                issues.Add(Error("curvePreset", preset.Id.ToString(), "A curve preset has no name."));
            ValidateCurvePoints(preset.Points, "curvePreset", preset.Id.ToString(), preset.Name, issues);
        }

        if (project.AudioPatch.MixSampleRate is < 8_000 or > 384_000)
            issues.Add(Error("document", null,
                $"The mix sample rate {project.AudioPatch.MixSampleRate} Hz is outside 8,000–384,000 Hz."));
        if (project.Settings.RemoteApi is { Port: < 1 or > 65_535 } remote)
            issues.Add(Error("document", null, $"The project remote API port {remote.Port} is invalid."));
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
            else if (!double.IsFinite(composition.FramesPerSecond))
                issues.Add(Error("composition", id, $"“{composition.Name}” has a non-finite frame rate."));
        }

        foreach (var output in project.VideoOutputs)
        {
            var id = output.Id.ToString();
            if (string.IsNullOrWhiteSpace(output.Name))
                issues.Add(Error("videoOutput", id, "A video output has no name."));
            if (output.CompositionId is { } compositionId
                && project.Compositions.All(composition => composition.Id != compositionId))
                issues.Add(Error("videoOutput", id,
                    $"“{output.Name}” shows a composition that no longer exists."));

            if (output.MappingWidth is < 0 or > 16384 || output.MappingHeight is < 0 or > 16384)
                issues.Add(Error("videoOutput", id,
                    $"“{output.Name}” has an out-of-range output raster "
                    + $"{output.MappingWidth}×{output.MappingHeight}."));

            foreach (var section in output.Mapping)
            {
                var values = new[]
                {
                    section.SourceX, section.SourceY, section.SourceWidth, section.SourceHeight,
                    section.TargetX, section.TargetY, section.TargetWidth, section.TargetHeight,
                    section.RotationDegrees, section.Opacity, section.Brightness,
                };
                if (values.Any(value => !double.IsFinite(value)))
                    issues.Add(Error("videoOutput", id,
                        $"Mapping section “{section.Name}” contains a non-finite number."));
                if (section.SourceWidth <= 0 || section.SourceHeight <= 0
                    || section.TargetWidth <= 0 || section.TargetHeight <= 0)
                    issues.Add(Error("videoOutput", id,
                        $"Mapping section “{section.Name}” has a zero or negative size."));
                if (section.Opacity is < 0 or > 1 || section.Brightness is < 0 or > 2)
                    issues.Add(Error("videoOutput", id,
                        $"Mapping section “{section.Name}” opacity must be in 0–1 and brightness in 0–2."));
                if (section.MeshColumns is < 0 or > 32 || section.MeshRows is < 0 or > 32)
                    issues.Add(Error("videoOutput", id,
                        $"Mapping section “{section.Name}” has an unsupported warp mesh "
                        + $"{section.MeshColumns}×{section.MeshRows}."));
                var wanted = section.MeshPointCount * 2;
                if (section.WarpOffsets.Count is not 0 && section.WarpOffsets.Count != wanted)
                    issues.Add(Error("videoOutput", id,
                        $"Mapping section “{section.Name}” has {section.WarpOffsets.Count} warp values; expected {wanted}."));
                if (section.WarpOffsets.Any(value => !double.IsFinite(value)))
                    issues.Add(Error("videoOutput", id,
                        $"Mapping section “{section.Name}” contains a non-finite warp offset."));
            }
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
                if (cue is TextCueNode text && string.IsNullOrWhiteSpace(text.Text))
                    issues.Add(Warn("cue", id, $"Q{cue.Number} has no words yet."));

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
                ValidateGain(media.LevelDb, "cue", id, $"Q{cue.Number}", issues);
                if (media.EndTargetCueId == media.Id)
                    issues.Add(Error("cue", id, $"Q{cue.Number} targets itself at media end."));
                else if (media.EndTargetCueId is { } endTarget)
                {
                    if (!cueIds.Contains(endTarget))
                        issues.Add(Error("cue", id,
                            $"Q{cue.Number} has an end target that is no longer in the show."));
                    else if (project.FindCue(endTarget) is CommentCueNode)
                        issues.Add(Error("cue", id,
                            $"Q{cue.Number} targets a comment at media end; comments cannot be fired."));
                }
                foreach (var send in media.Sends)
                {
                    if (send.SourceChannel < 0)
                        issues.Add(Error("cue", id,
                            $"Q{cue.Number} has a send with a negative source channel."));
                    if (project.FindChannel(send.LogicalChannelId) is null)
                        issues.Add(Error("cue", id,
                            $"Q{cue.Number} sends to a logical output that no longer exists."));
                    ValidateGain(send.GainDb, "cue", id, $"Q{cue.Number} send", issues);
                }

                foreach (var placement in media.Placements)
                    ValidatePlacement(project, placement, cue, issues);
                ValidateLanes(project, media.EffectLanes, cue, issues);
                ValidateCurve(project, media.FadeInCurve, cue, issues);
                ValidateCurve(project, media.FadeOutCurve, cue, issues);
                ValidateNonNegativeTimes(
                    cue,
                    issues,
                    (media.SourceDurationMs, "source duration"),
                    (media.TrimInMs, "trim in"),
                    (media.TrimOutMs, "trim out"),
                    (media.FadeInMs, "fade in"),
                    (media.FadeOutMs, "fade out"),
                    (media.LoopCrossfadeMs, "loop crossfade"));
                break;

            case TextCueNode text:
                if (!double.IsFinite(text.FontScale) || text.FontScale is < 0.01 or > 1)
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} has a font size outside the 1–100% range."));
                if (!double.IsFinite(text.OutlineWidth) || text.OutlineWidth is < 0 or > 0.1)
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} has an outline width outside the 0–10% range."));
                if (!Enum.IsDefined(text.Align) || !Enum.IsDefined(text.Anchor))
                    issues.Add(Error("cue", id, $"Q{cue.Number} uses an unknown text alignment."));
                foreach (var placement in text.Placements)
                    ValidatePlacement(project, placement, cue, issues);
                ValidateLanes(project, text.EffectLanes, cue, issues);
                ValidateCurve(project, text.FadeInCurve, cue, issues);
                ValidateCurve(project, text.FadeOutCurve, cue, issues);
                ValidateNonNegativeTimes(
                    cue,
                    issues,
                    (text.DurationMs, "duration"),
                    (text.FadeInMs, "fade in"),
                    (text.FadeOutMs, "fade out"));
                break;

            case GroupCueNode group:
                ValidateLanes(project, group.EffectLanes, cue, issues);
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
                if (jump.Condition == JumpCondition.CountThenContinue && jump.JumpCount <= 0)
                    issues.Add(Error("cue", id, $"Q{cue.Number} has an invalid jump count."));
                foreach (var targetId in jump.TargetCueIds.Where(target => !cueIds.Contains(target)))
                    issues.Add(Error("cue", id, $"Q{cue.Number} jumps to a cue that is no longer in the show."));
                break;

            case VisualizerCueNode visualizer:
                foreach (var feedId in visualizer.FeedCueIds)
                {
                    if (project.FindCue(feedId) is not MediaCueNode)
                        issues.Add(Error("cue", id,
                            $"Q{cue.Number} has a visualizer feed source that is not a live media cue."));
                }
                foreach (var placement in visualizer.Placements)
                    ValidatePlacement(project, placement, cue, issues);
                ValidateLanes(project, visualizer.EffectLanes, cue, issues);
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

    private static void ValidateNonNegativeTimes(
        CueNode cue,
        List<ShowValidationIssue> issues,
        params (int Value, string Name)[] values)
    {
        foreach (var (value, name) in values.Where(item => item.Value < 0))
            issues.Add(Error("cue", cue.Id.ToString(),
                $"Q{cue.Number} has a negative {name}."));
    }

    private static void ValidatePlacement(
        HaCueProject project, LayerPlacement? placement, CueNode cue, List<ShowValidationIssue> issues)
    {
        if (placement is null)
            return;

        if (project.Compositions.All(composition => composition.Id != placement.CompositionId))
            issues.Add(Error("cue", cue.Id.ToString(),
                $"Q{cue.Number} is placed on a composition that no longer exists."));
        if (new[] { placement.X, placement.Y, placement.Width, placement.Height, placement.Opacity }
            .Any(value => !double.IsFinite(value)))
            issues.Add(Error("cue", cue.Id.ToString(),
                $"Q{cue.Number} has a placement containing a non-finite number."));
        if (placement.Width <= 0 || placement.Height <= 0 || placement.Opacity is < 0 or > 1)
            issues.Add(Error("cue", cue.Id.ToString(),
                $"Q{cue.Number} has a placement with invalid size or opacity."));
    }

    private static void ValidateLanes(
        HaCueProject project,
        IReadOnlyList<EffectLane> lanes,
        CueNode cue,
        List<ShowValidationIssue> issues)
    {
        var id = cue.Id.ToString();
        foreach (var duplicate in lanes.GroupBy(lane => lane.Kind).Where(group => group.Count() > 1))
            issues.Add(Error("cue", id, $"Q{cue.Number} has more than one {duplicate.Key} lane."));

        foreach (var lane in lanes)
        {
            if (lane.Points.Count < 2)
                issues.Add(Error("cue", id, $"Q{cue.Number} has a {lane.Kind} lane with fewer than two points."));
            // Points must advance. A lane whose X went backwards would evaluate differently depending
            // on how it was walked, which is the kind of ambiguity that shows up once, live.
            var previousX = double.NegativeInfinity;
            foreach (var point in lane.Points)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
                    || point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1)
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} has a {lane.Kind} lane point outside the 0–1 range."));
                if (point.X < previousX)
                    issues.Add(Error("cue", id, $"Q{cue.Number} has an out-of-order {lane.Kind} lane."));
                if (!Enum.IsDefined(point.CurveToNext))
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} has a {lane.Kind} lane with an unknown segment curve."));
                previousX = point.X;
            }

            if (lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp && lane.EndpointId is null)
                issues.Add(Error("cue", id,
                    $"Q{cue.Number} has an outbound {lane.Kind} lane with no endpoint."));
            else if (lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp)
            {
                var endpoint = project.ActionEndpoints.FirstOrDefault(candidate => candidate.Id == lane.EndpointId);
                var expected = lane.Kind == EffectLaneKind.OscRamp ? EndpointKind.OscOut : EndpointKind.MidiOut;
                if (endpoint is null)
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} has an outbound lane whose endpoint no longer exists."));
                else if (endpoint.Kind != expected)
                    issues.Add(Error("cue", id,
                        $"Q{cue.Number} has a {lane.Kind} lane pointed at a {endpoint.Kind} endpoint."));
                if (string.IsNullOrWhiteSpace(lane.Address))
                    issues.Add(Error("cue", id, $"Q{cue.Number} has an outbound lane with no address/message."));
                else if (lane.Kind == EffectLaneKind.MidiRamp
                         && MidiActions.TryParse(lane.Address, "0", out _) is { } wrong)
                    issues.Add(Error("cue", id, $"Q{cue.Number} has a MIDI ramp that sends {wrong}"));
            }
        }
    }

    private static void ValidateCurve(
        HaCueProject project, CurveSpec? curve, CueNode cue, List<ShowValidationIssue> issues)
    {
        if (curve is null)
            return;

        if (!Enum.IsDefined(curve.Law))
            issues.Add(Error("cue", cue.Id.ToString(), $"Q{cue.Number} uses an unknown fade law."));

        if (curve.Points is { } inline)
            ValidateCurvePoints(inline, "cue", cue.Id.ToString(), $"Q{cue.Number}", issues);

        if (curve.PresetId is not { } presetId)
            return;

        if (project.CurvePresets.All(preset => preset.Id != presetId))
            issues.Add(Error("cue", cue.Id.ToString(),
                $"Q{cue.Number} uses a curve preset that no longer exists."));
    }

    private static void ValidateCurvePoints(
        IReadOnlyList<FadeCurvePoint> points,
        string kind,
        string? id,
        string label,
        List<ShowValidationIssue> issues)
    {
        if (points.Count < 2)
            issues.Add(Error(kind, id, $"{label} curve has fewer than two points."));

        var previous = double.NegativeInfinity;
        foreach (var point in points)
        {
            if (!double.IsFinite(point.Progress) || !double.IsFinite(point.Level)
                || point.Progress is < 0 or > 1 || point.Level is < 0 or > 1)
                issues.Add(Error(kind, id, $"{label} curve has a point outside the finite 0–1 range."));
            if (point.Progress < previous)
                issues.Add(Error(kind, id, $"{label} curve points are out of order."));
            if (!Enum.IsDefined(point.CurveToNext))
                issues.Add(Error(kind, id, $"{label} curve has an unknown segment law."));
            previous = point.Progress;
        }
    }

    private static void ValidateTriggers(HaCueProject project, List<ShowValidationIssue> issues)
    {
        foreach (var input in project.TriggerInputs)
            foreach (var binding in input.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Input))
                    issues.Add(Error("triggerInput", input.Id.ToString(),
                        $"“{input.Name}” has a binding with no input pattern."));
                if (!double.IsFinite(binding.RangeMin) || !double.IsFinite(binding.RangeMax))
                    issues.Add(Error("triggerInput", input.Id.ToString(),
                        $"“{input.Name}” has a binding with a non-finite parameter range."));
                if (binding.NoRepeatMs is < 0 or > 60_000)
                    issues.Add(Error("triggerInput", input.Id.ToString(),
                        $"“{input.Name}” has an invalid repeat filter."));
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
