using HaCue2.Core.Model;
using HaCue2.Core.Media;
using S.Media.Core.Video;
using S.Media.Session;
using S.Media.Source.Text;

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
    /// <param name="durations">
    /// What the probe found each cue's media to be, by cue id. Optional, and the compiler is honest
    /// without it: a lane or an out-point that needs a length it does not have is omitted rather than
    /// guessed. Supplying it is what makes untrimmed cues carry their automation.
    /// </param>
    public static ShowDocument Compile(
        HaCueProject project, IReadOnlyDictionary<Guid, TimeSpan>? durations = null)
        => Compile(project, new ShowCompileContext { Durations = durations });

    /// <summary>
    /// Compiles with the machine facts belonging to the currently opened project file.
    /// </summary>
    public static ShowDocument Compile(HaCueProject project, ShowCompileContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        var cues = new List<CueDefinition>();
        var clips = new List<ShowClipBinding>();
        var number = 0;

        foreach (var list in project.CueLists)
            Append(project, list, cues, clips, context, ref number);

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

    /// <summary>
    /// The runtime group id for ONE cue that must not share a transport with its siblings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session group holds exactly one active voice: firing a second cue into it releases the first.
    /// That is right for a PLAYLIST, where the whole point is that items replace each other — and wrong
    /// for every mode whose children are meant to sound at once, which is both the timeline (a stab at
    /// five seconds must play over the bed rather than cut it) and all-together.
    /// </para>
    /// <para>
    /// All-together shared its parent's transport, so firing a group of thirteen stems played the
    /// thirteenth: each fire displaced the one before it, and the group went silent except for whichever
    /// child happened to be last in the list. The mode's entire meaning is simultaneity, so it needs a
    /// voice per child exactly as the timeline does.
    /// </para>
    /// </remarks>
    public static string GroupId(CueList list, GroupCueNode group, CueNode child) =>
        $"{list.Id:N}:{group.Id:N}:{child.Id:N}";

    /// <summary>
    /// Whether a group's children each need their own transport, rather than sharing one.
    /// </summary>
    /// <remarks>
    /// The question is "do two of these ever sound at the same time". Only a playlist can answer no —
    /// playlist/armed-list items succeed one another, and first-only can only fire one child. Those
    /// modes keep a shared transport; timeline and all-together require concurrent child voices.
    /// </remarks>
    private static bool LayersChildren(GroupCueNode group) =>
        group.FireMode is GroupFireMode.Timeline or GroupFireMode.AllTogether;

    private static void Append(
        HaCueProject project,
        CueList list,
        List<CueDefinition> cues,
        List<ShowClipBinding> clips,
        ShowCompileContext context,
        ref int number)
    {
        var listGroup = GroupId(list);
        var running = number;

        void Walk(
            IEnumerable<CueNode> nodes,
            string groupId,
            TimeSpan preEndNotify,
            GroupCueNode? layering = null,
            IReadOnlyList<EffectLane>? inheritedLanes = null)
        {
            foreach (var node in nodes)
            {
                // A layering group's children each get their own transport, so they sound TOGETHER
                // rather than replacing one another. A playlist shares its parent's, which is what
                // makes a playlist a playlist. A LOCAL rather than reassigning the parameter: the
                // shared id has to survive the loop for the siblings that still use it.
                var id = layering is { } layered ? GroupId(list, layered, node) : groupId;

                switch (node)
                {
                    case GroupCueNode group:
                        // The GROUP ITSELF is a cue, so the cursor can stand on it and GO can reach it;
                        // what firing it MEANS is the fire mode's business and is resolved app-side.
                        cues.Add(Definition(group, ++running, id));

                        // Nested PLAYLISTS collapse into their outermost ancestor, as HaPlay's mapper
                        // does: the whole chain moves on one clock rather than splitting across a
                        // clock per subgroup. A group whose children sound together is the exception,
                        // and Walk gives its children one group each — see LayersChildren for why.
                        Walk(
                            group.Children,
                            id == listGroup ? GroupId(list, group) : id,
                            // A playlist's crossfade becomes its children's pre-end notify, so the
                            // session raises "approaching end" exactly that far before each out-point
                            // and the app can fire the next item early. Recomputed per group: a nested
                            // non-playlist group's children do not inherit it.
                            group is { FireMode: GroupFireMode.Playlist, CrossfadeMs: > 0 }
                                ? TimeSpan.FromMilliseconds(group.CrossfadeMs)
                                : TimeSpan.Zero,
                            LayersChildren(group) ? group : null,
                            MergeLanes(group.EffectLanes, inheritedLanes));
                        break;

                    case MediaCueNode media:
                        cues.Add(Definition(media, ++running, id));

                        // A cue with no file yet still gets its CUE — numbering and order have to stay
                        // stable while a show is being built — but no clip. Emitting an empty
                        // MediaPath would make the engine refuse the WHOLE document, so one unfinished
                        // cue would stop the show loading in the middle of a rehearsal. The project
                        // validator reports it by name instead.
                        if (media.MediaPath.Length > 0)
                        {
                            clips.Add(Clip(
                                project,
                                media,
                                Duration(context.Durations, media),
                                context,
                                inheritedLanes) with
                            {
                                PreEndNotify = MaxNotify(preEndNotify, FollowLead(project, media)),
                            });
                        }

                        break;

                    case TextCueNode text:
                        cues.Add(Definition(text, ++running, id));

                        // A card with no words is a cue with no clip — the same honest state as a
                        // media cue with no file, and the state every text cue is in between being
                        // added and typed into.
                        if (text.Text.Trim().Length > 0)
                            clips.Add(TextClip(project, text, inheritedLanes));

                        break;

                    // Every remaining kind — visualizer, action, fade, jump, patch, comment — is a cue
                    // with NO CLIP. A visualizer has a canvas presence and no media to open; the rest
                    // are decisions about the show rather than something to play, and the app's
                    // transport resolves them by id when they fire.
                    //
                    // They are emitted rather than omitted because the cursor is the session's: a cue
                    // absent from the document cannot be made standby (SetStandbyCueAsync refuses an
                    // unknown id) and GO would step straight over it. A clipless CueDefinition is an
                    // already-exercised state — an unfinished media cue is one too.
                    default:
                        cues.Add(Definition(node, ++running, id));
                        break;
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
            // HaCue2's executor owns waits for every cue kind. Leaving a second copy in the playback
            // graph made media waits run once here and once in CueExecutor.
            PreWait: TimeSpan.Zero,
            PostWait: TimeSpan.Zero,
            GroupId: groupId,
            // The application executor owns Continue and Follow for every cue kind; the framework
            // graph intentionally receives no second, media-only chain.
            AutoContinue: false);

    /// <summary>
    /// How far before this cue's out-point the session should raise "approaching end" so a chained
    /// successor can be OPENED before the edge rather than after it. Zero unless the project sets
    /// <see cref="ProjectSettings.FollowLeadMs"/> and this cue actually hands on to something: a plain
    /// cue that simply stops needs no lead and must not pay for one.
    /// </summary>
    private static TimeSpan FollowLead(HaCueProject project, MediaCueNode media)
    {
        if (project.Settings.FollowLeadMs <= 0)
            return TimeSpan.Zero;
        var handsOn = media.Trigger == CueTrigger.Follow || media.EndTargetCueId is not null;
        return handsOn ? TimeSpan.FromMilliseconds(project.Settings.FollowLeadMs) : TimeSpan.Zero;
    }

    /// <summary>
    /// One notify window per clip, so a playlist crossfade and a follow lead cannot cancel each other
    /// out. The LONGER wins: both want the notification EARLIER than the out-point, and the app decides
    /// per-notification what to do with it.
    /// </summary>
    private static TimeSpan MaxNotify(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    /// <summary>What the probe says this cue's file runs for, or null when nobody has looked.</summary>
    private static TimeSpan? Duration(
        IReadOnlyDictionary<Guid, TimeSpan>? durations, MediaCueNode media) =>
        durations is not null && durations.TryGetValue(media.Id, out var length) ? length : null;

    private static ShowClipBinding Clip(
        HaCueProject project,
        MediaCueNode media,
        TimeSpan? fileLength,
        ShowCompileContext context,
        IReadOnlyList<EffectLane>? inheritedLanes = null)
    {
        // The FIRST placement is the primary; the rest ride along as ExtraPlacements, which is how the
        // engine fans one DECODED source to several canvases. Playing the file again for a mirror
        // would double the decode cost and let the two copies drift apart.
        var placements = media.Placements.OrderBy(item => item.LayerIndex).ToList();
        var placement = placements.FirstOrDefault();

        var hasCheckedTracks = context.Tracks.TryGetValue(media.Id, out var tracks);
        var fadeIn = media.FadeInCurve.Resolve(project);
        var fadeOut = media.FadeOutCurve.Resolve(project);

        return new ShowClipBinding(
            ClipId: media.Id.ToString(),
            MediaPath: MediaPaths.Resolve(project, media.MediaPath, context.ProjectPath),
            CompositionId: placement?.CompositionId.ToString(),
            LayerIndex: placement?.LayerIndex ?? 0,
            // Null means "elect one", which is also what the document's null means, so an unmade
            // choice stays unmade all the way down rather than being frozen into an index here.
            // A checked null is meaningful: the saved signature no longer exists, so asking the
            // decoder to elect a stream is safer than silently reusing the stale numeric index.
            AudioStreamIndex: hasCheckedTracks ? tracks!.AudioStreamIndex : media.AudioTrackIndex,
            Subtitles: Subtitles(project, media, context, tracks))
        {
            // −1 is "no video", which is a real choice and not the same as electing one; the engine
            // reads it exactly that way.
            VideoStreamIndex = hasCheckedTracks ? tracks!.VideoStreamIndex : media.VideoTrackIndex,
            StartOffset = TimeSpan.FromMilliseconds(media.TrimInMs),
            // The document's EndOffset is measured from the SOURCE END; the project stores an ABSOLUTE
            // out-point, so converting one to the other needs the file's length. With a probed length
            // the out-point is honoured; without one it stays zero — "through to the end" — because a
            // guessed length would cut the cue somewhere nobody chose.
            EndOffset = EndOffset(media, fileLength),
            FadeIn = TimeSpan.FromMilliseconds(media.FadeInMs),
            FadeInCurve = fadeIn.Law,
            FadeInShape = fadeIn.Custom,
            FadeOut = TimeSpan.FromMilliseconds(media.FadeOutMs),
            FadeOutCurve = fadeOut.Law,
            FadeOutShape = fadeOut.Custom,
            // Either says it: the flag predates the enum, and a document carrying only the flag must
            // keep looping. The engine reads both the same way.
            Loop = media.Loop || media.EndBehavior == CueEndBehavior.Loop,
            EndBehavior = media.EndBehavior switch
            {
                CueEndBehavior.FreezeLastFrame => ClipEndBehavior.FreezeLastFrame,
                CueEndBehavior.Loop => ClipEndBehavior.Loop,
                CueEndBehavior.FadeOutAndStop => ClipEndBehavior.FadeOutAndStop,
                _ => media.Loop ? ClipEndBehavior.Loop : ClipEndBehavior.Stop,
            },
            // Always on. The engine now monitors every clip with a known end, so the POSITION path
            // (playhead reaches the out-point) no longer needs this flag — but the STALL path does, and
            // it is the one that catches a source whose real content is SHORTER than its metadata says.
            // A mis-tagged VBR file simply stops: its playhead never reaches the declared out-point, so
            // without this the cue sits in the Active panel forever with nothing having ended it. HaCue2
            // has follow cues and an Active panel, which is exactly the host this flag exists for, and
            // it had never been set.
            NotifyNaturalEnd = true,
            LoopCrossfade = TimeSpan.FromMilliseconds(Math.Max(0, media.LoopCrossfadeMs)),
            DisablePreRoll = media.DisablePreRoll,
            Placement = placement is null ? null : VideoPlacement(placement),
            ExtraPlacements = placements.Count < 2
                ? null
                : [.. placements.Skip(1).Select(extra => new ShowClipPlacement(
                    extra.CompositionId.ToString(),
                    extra.LayerIndex,
                    VideoPlacement(extra)))],
            LogicalSends = [.. Sends(media)],
            VolumeEnvelope = Envelope(media, EffectLaneKind.Volume, fileLength, inheritedLanes),
            OpacityEnvelope = Envelope(media, EffectLaneKind.Opacity, fileLength, inheritedLanes),
        };
    }

    /// <summary>
    /// The out-point as a distance back from the END of the file, which is how the document counts it.
    /// </summary>
    /// <remarks>
    /// Zero — play through — for an untrimmed cue, for a cue whose file nobody probed, and for an
    /// out-point at or past the end. An out-point BEFORE the in-point is treated as untrimmed rather
    /// than as a negative window: it is a half-finished edit, not an instruction to play backwards.
    /// </remarks>
    private static TimeSpan EndOffset(MediaCueNode media, TimeSpan? fileLength)
    {
        if (media.TrimOutMs <= media.TrimInMs || fileLength is not { } length)
            return TimeSpan.Zero;

        var remainder = length - TimeSpan.FromMilliseconds(media.TrimOutMs);
        return remainder > TimeSpan.Zero ? remainder : TimeSpan.Zero;
    }

    /// <summary>
    /// The subtitle tracks a cue shows, or null when it shows none.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty list: <see cref="ShowClipBinding.GetSubtitleSelections"/> treats an
    /// empty list and a null the same, and null is the shape that says "this cue never had any".
    /// </remarks>
    private static IReadOnlyList<ShowSubtitleSelection>? Subtitles(
        HaCueProject project,
        MediaCueNode media,
        ShowCompileContext context,
        ResolvedMediaTracks? tracks) =>
        context.PreparedSubtitlePaths.TryGetValue(media.Id, out var prepared)
            ? [new ShowSubtitleSelection(prepared, -1)]
            : SourceUri.YouTubeSubtitleLanguage(media.MediaPath) is not null
            ? null
            : media.Subtitles.Count == 0
            ? null
            : [.. media.Subtitles.Select((selection, index) => new ShowSubtitleSelection(
                selection.Path.Length > 0
                    ? MediaPaths.Resolve(project, selection.Path, context.ProjectPath)
                    : null,
                tracks is not null && index < tracks.SubtitleStreamIndices.Count
                    ? tracks.SubtitleStreamIndices[index]
                    : selection.StreamIndex))];

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
    private static IEnumerable<ShowClipLogicalSend> Sends(MediaCueNode media) => LogicalSends(media);

    /// <summary>
    /// One cue's sends as the engine takes them — the same values a fire would compile.
    /// </summary>
    /// <remarks>
    /// Public because the inspector pushes these at a PLAYING voice when the operator edits a send or a
    /// level, rather than reloading the document (which would restart the cue). Composing the gain a
    /// second time in the view would be a second implementation of the level rule — including the
    /// detail that a muted send is emitted at silence rather than dropped — and the two would drift.
    /// </remarks>
    public static IReadOnlyList<ShowClipLogicalSend> LogicalSends(MediaCueNode media)
    {
        ArgumentNullException.ThrowIfNull(media);

        return
        [
            .. media.Sends.Select(send => new ShowClipLogicalSend(
                send.SourceChannel,
                send.LogicalChannelId.ToString(),
                send.Muted ? 0f : Linear(send.GainDb + media.LevelDb))),
        ];
    }

    /// <summary>
    /// A text cue as a clip: its rendered card, held on screen or shown for an authored duration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A duration of zero is an indefinite card and therefore freezes its final frame. A positive
    /// duration stops naturally, which is what makes Follow and a duration authored in the inspector
    /// mean what they say.
    /// </para>
    /// <para>
    /// No audio at all — a text cue has no sends and asks the decoder for no audio stream, so a card
    /// standing over a running bed cannot interrupt it.
    /// </para>
    /// </remarks>
    private static ShowClipBinding TextClip(
        HaCueProject project,
        TextCueNode text,
        IReadOnlyList<EffectLane>? inheritedLanes = null)
    {
        var placements = text.Placements.OrderBy(item => item.LayerIndex).ToList();
        var placement = placements.FirstOrDefault();
        var fadeIn = text.FadeInCurve.Resolve(project);
        var fadeOut = text.FadeOutCurve.Resolve(project);

        return new ShowClipBinding(
            ClipId: text.Id.ToString(),
            MediaPath: TextSourceUri.Encode(TextSource(text)),
            CompositionId: placement?.CompositionId.ToString(),
            LayerIndex: placement?.LayerIndex ?? 0,
            // −1 rather than null: a card has no audio, and asking the decoder to ELECT a stream in a
            // file that has none is a question with no good answer.
            AudioStreamIndex: -1)
        {
            FadeIn = TimeSpan.FromMilliseconds(text.FadeInMs),
            FadeInCurve = fadeIn.Law,
            FadeInShape = fadeIn.Custom,
            FadeOut = TimeSpan.FromMilliseconds(text.FadeOutMs),
            FadeOutCurve = fadeOut.Law,
            FadeOutShape = fadeOut.Custom,
            EndBehavior = text.DurationMs > 0
                ? ClipEndBehavior.Stop
                : ClipEndBehavior.FreezeLastFrame,
            Placement = placement is null ? null : VideoPlacement(placement),
            ExtraPlacements = placements.Count < 2
                ? null
                : [.. placements.Skip(1).Select(extra => new ShowClipPlacement(
                    extra.CompositionId.ToString(),
                    extra.LayerIndex,
                    VideoPlacement(extra)))],
            // An indefinite card has no honest time span for an envelope. Timed cards do, and inherit
            // their nearest group lane just like media cues.
            OpacityEnvelope = Envelope(
                text.EffectLanes,
                EffectLaneKind.Opacity,
                text.DurationMs > 0 ? TimeSpan.FromMilliseconds(text.DurationMs) : null,
                inheritedLanes),
        };
    }

    /// <summary>
    /// A card's render parameters, in the units the framework's text source takes.
    /// </summary>
    /// <remarks>
    /// The document stores sizes as FRACTIONS of the canvas so a card survives a composition resize;
    /// the source wants pixels against its own canvas, and this is the one place that knows both.
    /// Colours are stored as "#RRGGBB" because that is what a designer is handed, and packed to ARGB
    /// here — an empty background means transparent, which is alpha zero rather than black.
    /// </remarks>
    public static TextSourceSpec TextSource(TextCueNode text) => new()
    {
        Text = text.Text,
        FontFamily = text.FontFamily.Trim().Length > 0 ? text.FontFamily.Trim() : "Inter",
        FontSizePx = Math.Clamp(text.FontScale, 0.01, 1) * TextCanvasHeight,
        Bold = text.Bold,
        Italic = text.Italic,
        ColorArgb = Argb(text.Foreground, 0xFFFFFFFFu),
        BackgroundArgb = Argb(text.Background, 0u),
        OutlineArgb = Argb(text.Outline, 0xFF000000u),
        OutlineWidthPx = Math.Clamp(text.OutlineWidth, 0, 0.1) * TextCanvasHeight,
        HAlign = (int)text.Align,
        VAlign = (int)text.Anchor,
        CanvasWidth = TextCanvasWidth,
        CanvasHeight = TextCanvasHeight,
        DurationMs = Math.Max(0, text.DurationMs),
    };

    /// <summary>
    /// The canvas a card is drawn at.
    /// </summary>
    /// <remarks>
    /// Fixed rather than the composition's, and the placement scales it from there: a card is placed
    /// like any other picture, and tying the render to a canvas size would re-encode every card's URI
    /// — and so re-open every card — the moment somebody resized a composition.
    /// </remarks>
    private const int TextCanvasWidth = 1920;

    private const int TextCanvasHeight = 1080;

    /// <summary>"#RRGGBB" as opaque ARGB; the fallback for anything else, including empty.</summary>
    private static uint Argb(string text, uint fallback)
    {
        var hex = (text ?? "").Trim().TrimStart('#');

        return hex.Length == 6
               && uint.TryParse(
                   hex,
                   System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var packed)
            ? 0xFF000000u | packed
            : fallback;
    }

    /// <summary>
    /// One placement, with everything the compositor can act on.
    /// </summary>
    /// <remarks>
    /// The crop, the rotation, the layer mapping and the two colour effects all existed on
    /// <see cref="ShowVideoPlacement"/> and were never filled — the document had nowhere to say them.
    /// The fit name is passed as TEXT the framework maps by name, so the two enums can be read side by
    /// side rather than through a table that drifts.
    /// </remarks>
    public static ShowVideoPlacement VideoPlacement(LayerPlacement placement) =>
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
                LayerFit.Center => "Center",
                LayerFit.FillWidth => "FillWidth",
                LayerFit.FillHeight => "FillHeight",
                _ => "Contain",
            },
            RotationDegrees: placement.RotationDegrees,
            CropLeft: Fraction(placement.CropLeft),
            CropTop: Fraction(placement.CropTop),
            CropRight: Fraction(placement.CropRight),
            CropBottom: Fraction(placement.CropBottom),
            VideoFx: LayerMapping(placement),
            ChromaKey: placement is { ChromaKeyEnabled: true, ChromaKey: { } key }
                ? new S.Media.Compositor.ChromaKeySettings(
                    (float)key.Red, (float)key.Green, (float)key.Blue,
                    (float)key.Similarity, (float)key.Smoothness, (float)key.SpillReduction)
                : null,
            ColorAdjust: placement is { ColorAdjustEnabled: true, ColorAdjust: { } colour }
                ? new S.Media.Compositor.Effects.BrightnessContrastSettings(
                    (float)colour.Brightness, (float)colour.Contrast)
                : null);

    /// <summary>A crop inset, clamped so opposite edges can never cross and erase the picture.</summary>
    private static double Fraction(double value) => Math.Clamp(value, 0, 0.49);

    /// <summary>
    /// A placement's own mapping, resolved against the SOURCE video rather than an output.
    /// </summary>
    /// <remarks>
    /// The destination is measured in the same normalized space the section stores, because a layer
    /// mapping has no output raster to resolve against — it happens before the layer is placed, and the
    /// destination rectangle does the placing afterwards.
    /// </remarks>
    private static ClipOutputMappingSpec? LayerMapping(LayerPlacement placement)
    {
        if (!placement.HasVideoFx)
            return null;

        var sections = placement.VideoFx
            .Where(section => section.Enabled)
            .Select(section => new ClipOutputMappingSection(
                Id: section.Id.ToString("N"),
                Enabled: true,
                SrcX: section.SourceX,
                SrcY: section.SourceY,
                SrcWidth: section.SourceWidth,
                SrcHeight: section.SourceHeight,
                DestX: section.TargetX,
                DestY: section.TargetY,
                DestWidth: section.TargetWidth,
                DestHeight: section.TargetHeight,
                RotationDegrees: section.RotationDegrees,
                Opacity: Math.Clamp(section.Opacity, 0, 1),
                Brightness: Math.Clamp(section.Brightness, 0, 1),
                MeshColumns: section.HasMesh ? section.MeshColumns : 0,
                MeshRows: section.HasMesh ? section.MeshRows : 0,
                MeshPoints: section.HasMesh ? MeshPoints(section) : null))
            .ToList();

        // Every section switched off is the same as no mapping — and NOT the same as a mapping with
        // nothing in it, which would render the layer black.
        return sections.Count == 0 ? null : new ClipOutputMappingSpec(sections, 1, 1);
    }

    /// <summary>The mesh as absolute points, adding back the even grid the document stores offsets from.</summary>
    private static List<ClipMeshPoint> MeshPoints(MappingSection section)
    {
        var points = new List<ClipMeshPoint>(section.MeshPointCount);

        for (var row = 0; row < section.MeshRows; row++)
        {
            for (var column = 0; column < section.MeshColumns; column++)
            {
                var at = ((row * section.MeshColumns) + column) * 2;

                points.Add(new ClipMeshPoint(
                    ((double)column / (section.MeshColumns - 1)) + section.WarpOffsets[at],
                    ((double)row / (section.MeshRows - 1)) + section.WarpOffsets[at + 1]));
            }
        }

        return points;
    }

    /// <summary>
    /// One automation lane as engine keyframes, or null when the cue has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lane X is a fraction of the cue; the engine wants clip TIME, so the lane is compiled against the
    /// cue's PLAYED length — its trim window when it has one, otherwise the probed file length less the
    /// in-point.
    /// </para>
    /// <para>
    /// The probed length matters more than it looks: <see cref="MediaCueNode.TrimOutMs"/> is zero on
    /// every untrimmed cue, so keying only off the trim window silently dropped the lane from the
    /// common case — the operator drew an envelope, the timeline drew it back, and the engine never
    /// received it. With no length from either source the lane is still skipped, because a lane
    /// stretched over a guessed duration automates the wrong moments.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ShowEnvelopePoint>? Envelope(
        MediaCueNode media,
        EffectLaneKind kind,
        TimeSpan? fileLength,
        IReadOnlyList<EffectLane>? inheritedLanes = null) =>
        Envelope(media.EffectLanes, kind, media.TrimmedLength(fileLength), inheritedLanes);

    private static IReadOnlyList<ShowEnvelopePoint>? Envelope(
        IReadOnlyList<EffectLane> ownLanes,
        EffectLaneKind kind,
        TimeSpan? span,
        IReadOnlyList<EffectLane>? inheritedLanes = null)
    {
        var lane = ownLanes.FirstOrDefault(candidate => candidate.Kind == kind)
                   ?? inheritedLanes?.FirstOrDefault(candidate => candidate.Kind == kind);
        if (lane is not { Points.Count: > 1 })
            return null;

        if (span is not { } duration || duration <= TimeSpan.Zero)
            return null;

        return
        [
            .. lane.Points.Select(point => new ShowEnvelopePoint(
                point.X * duration,
                (float)Math.Clamp(point.Y, 0, 1))),
        ];
    }

    private static IReadOnlyList<EffectLane> MergeLanes(
        IReadOnlyList<EffectLane> nearest,
        IReadOnlyList<EffectLane>? inherited) =>
        [
            .. nearest,
            .. (inherited ?? []).Where(parent => nearest.All(lane => lane.Kind != parent.Kind)),
        ];

    /// <summary>
    /// The canvas rate as an exact ratio.
    /// </summary>
    /// <remarks>
    /// New documents persist numerator/denominator. Legacy documents carry only a <see cref="double"/>;
    /// <see cref="CompositionDefinition.ExactFrameRate"/> recovers the intended common ratio for those.
    /// </remarks>
    private static ShowComposition Composition(CompositionDefinition composition)
    {
        var rate = composition.ExactFrameRate;
        if (rate.Numerator <= 0 || rate.Denominator <= 0)
            rate = new Rational(60, 1);

        return new ShowComposition(
            Id: composition.Id.ToString(),
            Name: composition.Name,
            Width: composition.Width,
            Height: composition.Height,
            FrameRateNum: rate.Numerator,
            FrameRateDen: rate.Denominator);
    }

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
