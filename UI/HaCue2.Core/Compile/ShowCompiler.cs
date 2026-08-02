using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Compile;

/// <summary>
/// Turns a project into the engine's <see cref="ShowDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// The document is a PLAYBACK GRAPH, not a copy of the project: it carries what has to be playable —
/// cues, the media each plays, the canvases, the audio endpoints — and nothing about how the show is
/// authored. Numbers, notes, colour tags, the patch's editing conveniences and every control-flow cue
/// stay on the project side, because none of them changes a sound or a picture.
/// </para>
/// <para>
/// Modelled on HaPlay's <c>HaPlayShowMapper</c>, which has been compiling the same engine for real
/// shows: media cues become clips, groups become runtime transport groups, and control-flow cues
/// (action, fade, jump, patch, comment) have no document representation at all — they execute at the
/// app's transport layer, which is the only place that can decide what they mean.
/// </para>
/// </remarks>
public static class ShowCompiler
{
    /// <summary>The document version this compiler writes.</summary>
    public const int DocumentVersion = 1;

    /// <summary>
    /// Compiles the whole project — every cue list, merged into one document.
    /// </summary>
    /// <remarks>
    /// Merged rather than one document per list because a show has one transport and one patch, and
    /// per-list documents could not express a cue in Act 1 stopping something started in the Preshow.
    /// Each list becomes its own runtime GROUP, so the lists still keep separate playheads.
    /// </remarks>
    public static ShowDocument Compile(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var cues = new List<CueDefinition>();
        var clips = new List<ShowClipBinding>();
        var number = 0;

        foreach (var list in project.CueLists)
            Append(project, list, cues, clips, ref number);

        return new ShowDocument(
            DocumentVersion,
            cues,
            clips,
            [.. project.Compositions.Select(Composition)],
            Routes(project))
        {
            AudioOutputs = [.. AudioOutputs(project)],
        };
    }

    /// <summary>The runtime group id for a whole cue list — its own transport unit.</summary>
    public static string GroupId(CueList list) => list.Id.ToString("N");

    /// <summary>The runtime group id for a group cue inside a list.</summary>
    public static string GroupId(CueList list, GroupCueNode group) => $"{list.Id:N}:{group.Id:N}";

    private static void Append(
        HaCueProject project,
        CueList list,
        List<CueDefinition> cues,
        List<ShowClipBinding> clips,
        ref int number)
    {
        var listGroup = GroupId(list);
        var running = number;

        void Walk(IEnumerable<CueNode> nodes, string groupId, TimeSpan preEndNotify)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case GroupCueNode group:
                        // Nested groups collapse into their OUTERMOST ancestor, as HaPlay's mapper
                        // does: the whole tree moves on one clock rather than splitting across a clock
                        // per subgroup. WHICH children fire on GO is the fire mode's business, and the
                        // fire mode is resolved app-side by cue id, so it needs no document shape.
                        Walk(
                            group.Children,
                            groupId == listGroup ? GroupId(list, group) : groupId,
                            // A playlist's crossfade becomes its children's pre-end notify, so the
                            // session raises "approaching end" exactly that far before each out-point
                            // and the app can fire the next item early. Recomputed per group: a nested
                            // non-playlist group's children do not inherit it.
                            group is { FireMode: GroupFireMode.Playlist, CrossfadeMs: > 0 }
                                ? TimeSpan.FromMilliseconds(group.CrossfadeMs)
                                : TimeSpan.Zero);
                        break;

                    case MediaCueNode media:
                        cues.Add(Definition(media, ++running, groupId));

                        // A cue with no file yet still gets its CUE — numbering and order have to stay
                        // stable while a show is being built — but no clip. Emitting an empty
                        // MediaPath would make the engine refuse the WHOLE document, so one unfinished
                        // cue would stop the show loading in the middle of a rehearsal. The project
                        // validator reports it by name instead.
                        if (media.MediaPath.Length > 0)
                            clips.Add(Clip(project, media) with { PreEndNotify = preEndNotify });

                        break;

                    case VisualizerCueNode visualizer:
                        // A visualizer has a canvas presence but no media to open, so it is a cue with
                        // no clip: the app starts the generator and the placement rides on the cue.
                        cues.Add(Definition(visualizer, ++running, groupId));
                        break;

                    // Action, fade, jump, patch and comment cues have NO document representation.
                    // Every one of them is a decision about the show rather than something to play,
                    // and the transport layer is the only place that can make it.
                }
            }
        }

        Walk(list.Cues, listGroup, TimeSpan.Zero);
        number = running;
    }

    /// <summary>
    /// One cue, with the dense ordinal the engine numbers by.
    /// </summary>
    /// <remarks>
    /// <see cref="CueDefinition.Number"/> is an <c>int</c> and stays one: it is a POSITION, assigned
    /// here in list order, not the number the operator calls over comms — that is
    /// <see cref="CueNode.Number"/>, which is dotted and lives only on the project. GO's "lowest
    /// number greater than the cursor" therefore walks the list in the order the tree shows it, which
    /// is the order somebody is reading down during the show.
    /// <para>
    /// A DISABLED cue is still emitted, with <c>Enabled: false</c>. Dropping it would renumber
    /// everything after it, so re-enabling a cue mid-show would shift the running order underneath the
    /// operator.
    /// </para>
    /// </remarks>
    private static CueDefinition Definition(CueNode cue, int number, string groupId) =>
        new(
            Id: cue.Id.ToString(),
            Number: number,
            Label: cue.Label.Length > 0 ? cue.Label : cue.Number.Text,
            Enabled: cue.Enabled,
            PreWait: TimeSpan.FromMilliseconds(cue.PreWaitMs),
            PostWait: TimeSpan.FromMilliseconds(cue.PostWaitMs),
            GroupId: groupId,
            AutoContinue: cue.Trigger == CueTrigger.Continue);

    private static ShowClipBinding Clip(HaCueProject project, MediaCueNode media)
    {
        var placement = media.Placement;

        return new ShowClipBinding(
            ClipId: media.Id.ToString(),
            MediaPath: media.MediaPath,
            CompositionId: placement?.CompositionId.ToString(),
            LayerIndex: placement?.LayerIndex ?? 0,
            // Null means "elect one", which is also what the document's null means, so an unmade
            // choice stays unmade all the way down rather than being frozen into an index here.
            AudioStreamIndex: media.AudioTrackIndex,
            Subtitles: Subtitles(media))
        {
            // −1 is "no video", which is a real choice and not the same as electing one; the engine
            // reads it exactly that way.
            VideoStreamIndex = media.VideoTrackIndex,
            StartOffset = TimeSpan.FromMilliseconds(media.TrimInMs),
            // The document's EndOffset is measured from the SOURCE END; the project stores an absolute
            // out-point, and only a probe knows the length. Zero — "through to the end" — is the
            // honest translation until the app has probed and can convert it.
            EndOffset = TimeSpan.Zero,
            FadeIn = TimeSpan.FromMilliseconds(media.FadeInMs),
            FadeInCurve = media.FadeInCurve.Law,
            FadeOut = TimeSpan.FromMilliseconds(media.FadeOutMs),
            FadeOutCurve = media.FadeOutCurve.Law,
            Loop = media.Loop,
            Placement = placement is null ? null : Placement(placement),
            LogicalSends = [.. Sends(media)],
            VolumeEnvelope = Envelope(media, EffectLaneKind.Volume),
            OpacityEnvelope = Envelope(media, EffectLaneKind.Opacity),
        };
    }

    /// <summary>
    /// The subtitle tracks a cue shows, or null when it shows none.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty list: <see cref="ShowClipBinding.GetSubtitleSelections"/> treats an
    /// empty list and a null the same, and null is the shape that says "this cue never had any".
    /// </remarks>
    private static IReadOnlyList<ShowSubtitleSelection>? Subtitles(MediaCueNode media) =>
        media.Subtitles.Count == 0
            ? null
            : [.. media.Subtitles.Select(selection => new ShowSubtitleSelection(
                selection.Path.Length > 0 ? selection.Path : null,
                selection.StreamIndex))];

    /// <summary>
    /// The clip's N×V matrix: which source channel feeds which logical output, at what gain.
    /// </summary>
    /// <remarks>
    /// Only the FIRST matrix goes in the document. The second — logical outputs onto device channels —
    /// belongs to the program-audio target, because it is a property of the RIG rather than of the
    /// show: the same document played in another venue keeps its sends and gets a different patch.
    /// <para>
    /// A muted send is emitted at silence rather than dropped, so muting is a level the operator can
    /// see and undo rather than a route that vanished.
    /// </para>
    /// </remarks>
    private static IEnumerable<ShowClipLogicalSend> Sends(MediaCueNode media) =>
        media.Sends.Select(send => new ShowClipLogicalSend(
            send.SourceChannel,
            send.LogicalChannelId.ToString(),
            send.Muted ? 0f : Linear(send.GainDb + media.LevelDb)));

    private static ShowVideoPlacement Placement(LayerPlacement placement) =>
        new(
            DestX: placement.X,
            DestY: placement.Y,
            DestWidth: placement.Width,
            DestHeight: placement.Height,
            Opacity: placement.Opacity,
            Fit: placement.Fit switch
            {
                LayerFit.Cover => "Cover",
                LayerFit.Stretch => "Stretch",
                _ => "Contain",
            });

    /// <summary>
    /// One automation lane as engine keyframes, or null when the cue has none.
    /// </summary>
    /// <remarks>
    /// Lane X is a fraction of the cue; the engine wants clip TIME, and only a probe knows the length.
    /// Until the app supplies one the lane is compiled against its own trim window when there is one,
    /// and skipped otherwise — a lane stretched over a guessed duration would automate the wrong
    /// moments, which is worse than not automating at all.
    /// </remarks>
    private static IReadOnlyList<ShowEnvelopePoint>? Envelope(MediaCueNode media, EffectLaneKind kind)
    {
        if (media.EffectLanes.FirstOrDefault(lane => lane.Kind == kind) is not { Points.Count: > 1 } lane)
            return null;

        var span = media.TrimOutMs - media.TrimInMs;
        if (span <= 0)
            return null;

        return
        [
            .. lane.Points.Select(point => new ShowEnvelopePoint(
                TimeSpan.FromMilliseconds(point.X * span),
                (float)Math.Clamp(point.Y, 0, 1))),
        ];
    }

    private static ShowComposition Composition(CompositionDefinition composition) =>
        new(
            Id: composition.Id.ToString(),
            Name: composition.Name,
            Width: composition.Width,
            Height: composition.Height,
            FrameRateNum: (int)Math.Round(composition.FramesPerSecond * 1000),
            FrameRateDen: 1000);

    /// <summary>
    /// One audio endpoint per PATCHED line.
    /// </summary>
    /// <remarks>
    /// A line nobody has patched anything to is deliberately absent: opening a device to send it
    /// silence takes it away from whatever else on the machine wants it, and an unused line is an
    /// authoring leftover the status pass already reports.
    /// </remarks>
    private static IEnumerable<ShowAudioOutput> AudioOutputs(HaCueProject project) =>
        project.AudioLines
            .Where(line => project.AudioPatch.Cells.Any(cell => cell.LineId == line.Id))
            .Select(line => new ShowAudioOutput(
                Id: line.Id.ToString(),
                DeviceId: line.DeviceHint.Length > 0 ? line.DeviceHint : null,
                GroupId: "main"));

    /// <summary>
    /// The V×R patch is NOT compiled into routes.
    /// </summary>
    /// <remarks>
    /// <see cref="OutputPatchRoute"/> is a source→output channel remap, which is the v1 direct-route
    /// model the program-audio target supersedes. Emitting both would give the engine two answers
    /// about where a cue's audio goes, and the fallback path would win on any session without a
    /// program target — quietly playing the show through a patch nobody edited.
    /// </remarks>
    private static IReadOnlyList<OutputPatchRoute> Routes(HaCueProject project) => [];

    /// <summary>Decibels to the linear gain the engine multiplies by; the silence floor maps to zero.</summary>
    private static float Linear(double decibels) =>
        decibels <= GainRange.SilenceFloorDb ? 0f : (float)Math.Pow(10, decibels / 20);
}
