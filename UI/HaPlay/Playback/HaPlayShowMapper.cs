using S.Media.Session;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Playback;

/// <summary>
/// Maps a GUI <see cref="CueList"/> onto the framework's headless <see cref="ShowDocument"/> - the bridge
/// that runs the cue workspace on <see cref="S.Media.Session.ShowSession"/> (Phase 8 "full superset"
/// convergence; the ported <c>CuePlaybackEngine</c> it replaced is deleted).
/// <para>Two entries: the single-list form (standalone / <c>ShowDocumentSidecar</c> export, one document
/// per list) and the cross-list form
/// (<see cref="ToShowDocument(IReadOnlyList{ValueTuple{Guid, CueList}}, IReadOnlyList{OutputDefinition})"/>),
/// which concatenates every loaded list into the ONE document the cue workspace runs - the merged session
/// that makes any list's cues fireable by schedules, triggers and the remote API.</para>
/// </summary>
/// <remarks>
/// Lossless today for the cue core: the media/group node tree flattens to ordered cues carrying a
/// <see cref="CueDefinition.GroupId"/> (nested groups collapse onto their outermost transport unit); each media
/// cue maps to a <see cref="ShowClipBinding"/> with its clip playback params (trim / fade / loop / end-behaviour)
/// and <em>all</em> of its composition placements (primary + <see cref="ShowClipBinding.ExtraPlacements"/>, fanned
/// out at play time); and each <see cref="CueComposition"/> maps to a <see cref="ShowComposition"/> including its
/// output-mapping warp sections (affine + mesh).
/// <para>Deferred (tracked, surfaced inline where they bite): action/comment cues; group fire modes are resolved
/// by the VM trigger plan, not the document; corner-pin (the framework section is affine + mesh only -
/// <see cref="CueOutputMappingSection.Corners"/> is dropped); and the GUI string cue number (cues are renumbered
/// 1..N by document order).</para>
/// </remarks>
public static class HaPlayShowMapper
{
    /// <summary>Resolves every composition/output binding to the mapping the runtime should use. Enabled
    /// bindings without persisted geometry receive the same implicit native-size tile shown by the layout
    /// editor; disabled mappings remain raw. This prevents the first layout save from appearing to resize an
    /// output that the editor already depicted as a tile.</summary>
    public static IReadOnlyDictionary<Guid, CueOutputMapping?> ResolveEffectiveVideoOutputMappings(
        CueList cueList,
        IReadOnlyList<OutputDefinition> outputs)
    {
        ArgumentNullException.ThrowIfNull(cueList);
        ArgumentNullException.ThrowIfNull(outputs);
        var result = new Dictionary<Guid, CueOutputMapping?>();
        var definitions = outputs.GroupBy(d => d.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var composition in cueList.Compositions)
        {
            var bindings = cueList.VideoOutputs
                .Where(binding => binding.CompositionId == composition.Id && binding.OutputLineId != Guid.Empty)
                .ToArray();
            if (bindings.Length == 0)
                continue;

            var layout = CompositionOutputLayoutViewModel.Build(
                composition.Width,
                composition.Height,
                bindings.Select(binding =>
                {
                    int? width = null;
                    int? height = null;
                    definitions.TryGetValue(binding.OutputLineId, out var definition);
                    if (definition is not null
                        && HaPlayPlaybackHelpers.TryGetOutputResolution(definition, out var w, out var h))
                    {
                        width = w;
                        height = h;
                    }

                    return (binding.OutputLineId, definition?.DisplayName ?? string.Empty, width, height, binding.Mapping);
                }));

            foreach (var binding in bindings)
            {
                if (!binding.MappingEnabled)
                    result[binding.Id] = null;
                else if (binding.Mapping is not null)
                    result[binding.Id] = binding.Mapping;
                else
                {
                    var item = layout.Items.First(i => i.OutputLineId == binding.OutputLineId);
                    result[binding.Id] = layout.ToMapping(item);
                }
            }
        }

        return result;
    }

    /// <summary>Merged form of <see cref="ResolveEffectiveVideoOutputMappings(CueList, IReadOnlyList{OutputDefinition})"/>
    /// for the cross-list session: binding ids are Guids, so the per-list dictionaries concatenate without
    /// collision.</summary>
    public static IReadOnlyDictionary<Guid, CueOutputMapping?> ResolveEffectiveVideoOutputMappings(
        IReadOnlyList<CueList> cueLists,
        IReadOnlyList<OutputDefinition> outputs)
    {
        ArgumentNullException.ThrowIfNull(cueLists);
        ArgumentNullException.ThrowIfNull(outputs);
        var result = new Dictionary<Guid, CueOutputMapping?>();
        foreach (var cueList in cueLists)
            foreach (var (bindingId, mapping) in ResolveEffectiveVideoOutputMappings(cueList, outputs))
                result[bindingId] = mapping;
        return result;
    }

    /// <summary>The runtime transport group a list's TOP-LEVEL cues (those with no authored group) run on.
    /// Cue / group / composition ids are already Guids and therefore unique across lists, but "no authored
    /// group" is not: every list would otherwise land its top-level cues on the session's single default
    /// group and cues from different lists would replace one another. The list-id prefix gives each list its
    /// own default transport unit inside the one merged session.</summary>
    public static string RuntimeGroupId(Guid listId) => listId.ToString("N");

    /// <summary>The runtime transport group for an authored group, scoped to its list (see
    /// <see cref="RuntimeGroupId(Guid)"/>).</summary>
    public static string RuntimeGroupId(Guid listId, Guid groupId) => $"{listId:N}:{groupId}";

    /// <summary>Builds ONE runnable <see cref="ShowDocument"/> from every loaded cue list (workstream A -
    /// the cross-list merged session): the per-list documents are concatenated so any cue in any list is
    /// fireable by schedules, triggers and the remote API, while the visible transport still follows the
    /// selected list only. Identity is list-scoped exactly where it needs to be - cue / clip / composition
    /// ids stay the authored Guids (globally unique), and only the runtime transport-group ids gain the
    /// list-id prefix (<see cref="RuntimeGroupId(Guid)"/>). Cue numbers continue across lists in the given
    /// order, so pass a STABLE order (the cue workspace passes its cue-list collection order): the
    /// document must not renumber itself just because the operator selected a different list, or the
    /// session's per-group "last fired number" state would silently stop matching it.</summary>
    public static ShowDocument ToShowDocument(
        IReadOnlyList<(Guid ListId, CueList List)> cueLists, IReadOnlyList<OutputDefinition>? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(cueLists);

        var outputsById = BuildOutputIndex(outputs);
        var cues = new List<CueDefinition>();
        var clips = new List<ShowClipBinding>();
        var compositions = new List<ShowComposition>();
        var number = 0;
        foreach (var (listId, list) in cueLists)
        {
            ArgumentNullException.ThrowIfNull(list);
            AppendCueList(list, listId, outputsById, cues, clips, ref number);
            compositions.AddRange(list.Compositions.Select(MapComposition));
        }

        return ShowDocument.Empty with
        {
            Cues = cues,
            Clips = clips,
            Compositions = compositions,
        };
    }

    /// <summary>Builds a runnable <see cref="ShowDocument"/> from a GUI cue list. Pass the output definitions
    /// (<c>OutputManagement.DefinitionsSnapshot</c>) to resolve per-cue audio routes onto their real devices;
    /// omit them and clips fall back to the per-group/default output.
    /// <para>Single-list form: transport groups are the bare authored group ids and top-level cues carry no
    /// group at all (the session default). The cue workspace runs the cross-list
    /// <see cref="ToShowDocument(IReadOnlyList{ValueTuple{Guid, CueList}}, IReadOnlyList{OutputDefinition})"/>
    /// overload; this one stays the standalone/export mapping (<c>ShowDocumentSidecar</c> writes one document
    /// per list).</para></summary>
    public static ShowDocument ToShowDocument(CueList cueList, IReadOnlyList<OutputDefinition>? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(cueList);

        var outputsById = BuildOutputIndex(outputs);
        var cues = new List<CueDefinition>();
        var clips = new List<ShowClipBinding>();
        var number = 0;
        AppendCueList(cueList, listId: null, outputsById, cues, clips, ref number);

        return ShowDocument.Empty with
        {
            Cues = cues,
            Clips = clips,
            Compositions = cueList.Compositions.Select(MapComposition).ToArray(),
        };
    }

    private static Dictionary<Guid, OutputDefinition> BuildOutputIndex(IReadOnlyList<OutputDefinition>? outputs) =>
        outputs?.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First())
        ?? new Dictionary<Guid, OutputDefinition>();

    /// <summary>Flattens one cue list's node tree onto the shared cue/clip lists. <paramref name="listId"/>
    /// null = the standalone single-list mapping (bare group ids, no group for top-level cues); a value
    /// scopes every runtime transport group to that list for the merged cross-list document.</summary>
    private static void AppendCueList(
        CueList cueList,
        Guid? listId,
        IReadOnlyDictionary<Guid, OutputDefinition> outputsById,
        List<CueDefinition> cues,
        List<ShowClipBinding> clips,
        ref int number)
    {
        // Top-level cues: no group at all in the single-list document (ShowSession's own default group),
        // the list's own default transport unit in the merged one.
        var topLevelGroupId = listId is { } topLevelListId ? RuntimeGroupId(topLevelListId) : null;
        var localNumber = number;

        void Walk(IEnumerable<CueNode> nodes, string? groupId, TimeSpan preEndNotify)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case CueGroupNode group:
                        // A top-level group is the authored transport/replacement unit for normal single-cue GO.
                        // Fire-all batches temporarily override it with stable per-cue runtime groups so siblings
                        // can remain active together; HaPlay coordinates pause/seek across those runtime groups.
                        // Nested subgroups collapse into their OUTERMOST ancestor (first non-null wins) so
                        // the whole tree moves as one unit rather than splitting across per-subgroup clocks.
                        // WHICH cues fire on GO - including per-subgroup fire modes (FirstCueOnly / …) - is
                        // resolved by the VM's trigger plan and fired by explicit cue id, so it needs no
                        // representation in the ShowDocument.
                        // Playlist crossfade: DIRECT media children of a Playlist group with CrossfadeMs
                        // carry the window as their pre-end notify offset, so the session's end monitor
                        // raises ClipApproachingEnd exactly CrossfadeMs before each item's out-point and
                        // the VM fires the next pick early. Recomputed per group - a nested non-playlist
                        // group's children do NOT inherit it (nested-group picks stay butt splice).
                        Walk(
                            group.Children,
                            groupId ?? (listId is { } id
                                ? RuntimeGroupId(id, group.Id)
                                : group.Id.ToString()),
                            group.FireMode == CueGroupFireMode.Playlist
                            && group.Playlist is { CrossfadeMs: > 0 } playlistOptions
                                ? TimeSpan.FromMilliseconds(playlistOptions.CrossfadeMs)
                                : TimeSpan.Zero);
                        break;

                    case MediaCueNode media:
                        var cueId = media.Id.ToString();
                        cues.Add(new CueDefinition(
                            Id: cueId,
                            Number: ++localNumber,
                            Label: string.IsNullOrEmpty(media.Label) ? cueId : media.Label,
                            PreWait: TimeSpan.FromMilliseconds(media.PreWaitMs),
                            GroupId: groupId ?? topLevelGroupId));

                        if (MapClip(cueId, media, outputsById) is { } binding)
                            clips.Add(binding with { PreEndNotify = preEndNotify });
                        break;

                    // ActionCueNode / CommentCueNode / JumpCueNode have no ShowDocument equivalent - action
                    // and jump (control-flow) cues execute at the HaPlay transport layer.
                }
            }
        }

        Walk(cueList.Nodes, groupId: null, preEndNotify: TimeSpan.Zero);
        number = localNumber;
    }

    private static ShowClipBinding? MapClip(
        string cueId, MediaCueNode media, IReadOnlyDictionary<Guid, OutputDefinition> outputsById)
    {
        // Text cues encode their render spec + duration into a `text:` URI so the ShowSession `text:` provider can
        // render + play them (NXT-06); every other source resolves to a path/scheme URI.
        var mediaPath = media.Source is TextPlaylistItem text
            ? S.Media.Source.Text.TextSourceUri.Encode(text.ToSpec(media.DurationMs))
            : ResolveMediaPath(media.Source);
        if (mediaPath is null)
            return null; // media cue with no resolvable source (unbound) - nothing to play yet.

        // A cue may place its one decoded source onto several composition layers at once (PiP, the same feed in
        // two regions, or mirrored to a second canvas). Bound placements only (empty composition id = unbound),
        // ordered by layer index like the legacy engine. The first fills the binding's primary fields; the rest
        // become ExtraPlacements - ShowSession fans the video out to every one.
        var placements = media.VideoPlacements
            .Where(p => p.CompositionId != Guid.Empty)
            .OrderBy(p => p.LayerIndex)
            .ToList();
        var primary = placements.Count > 0 ? placements[0] : null;
        var extra = placements.Count > 1
            ? placements.Skip(1)
                .Select(p => new ShowClipPlacement(
                    p.CompositionId.ToString(), p.LayerIndex, ToShowVideoPlacement(p)))
                .ToArray()
            : null;
        return new ShowClipBinding(
            ClipId: cueId,
            MediaPath: mediaPath,
            CompositionId: primary?.CompositionId.ToString(),
            LayerIndex: primary?.LayerIndex ?? 0,
            AudioStreamIndex: media.AudioTrackIndex)
        {
            VideoStreamIndex = media.VideoTrackIndex,
            StartOffset = TimeSpan.FromMilliseconds(media.StartOffsetMs),
            EndOffset = TimeSpan.FromMilliseconds(media.EndOffsetMs),
            FadeIn = TimeSpan.FromMilliseconds(media.FadeInMs),
            FadeInCurve = MapFadeCurve(media.FadeInCurve),
            FadeOut = TimeSpan.FromMilliseconds(media.FadeOutMs),
            FadeOutCurve = MapFadeCurve(media.FadeOutCurve),
            VolumeEnvelope = MapVolumeEnvelope(media.VolumeEnvelope),
            Loop = media.Loop || media.EndBehavior == CueEndBehavior.Loop,
            LoopCrossfade = TimeSpan.FromMilliseconds(Math.Max(0, media.LoopCrossfadeMs)),
            // A held image has no meaningful "play to the end and stop": it displays until the next
            // GO. Most image containers report no duration and simply idle, but a duration-reporting
            // one (a GIF probes as ~40 ms) would reach that end and RELEASE under the always-armed end
            // monitor - flashing the image for one poll tick and auto-advancing - so the default Stop
            // maps to a freeze. An authored loop or fade-out still wins.
            EndBehavior = media.Source is ImagePlaylistItem && media.EndBehavior == CueEndBehavior.Stop
                ? ClipEndBehavior.FreezeLastFrame
                : MapEndBehavior(media.EndBehavior),
            // A text cue plays a held frame that never signals EOF, so end it at its duration via the time-based
            // monitor (EndAtDuration) rather than by source exhaustion - otherwise a resize/live-edit re-read ends
            // it early. Only when a positive duration is set; a 0-duration text cue holds until the next cue.
            EndAtDuration = media.Source is TextPlaylistItem && media.DurationMs > 0,
            // A real FILE cue must fire cue auto-follow when it plays through, even as a bare plain-Stop clip
            // with no trim/fade/loop (which otherwise starts no end monitor and just idles at EOF). Finite
            // sources only: images/text hold deliberately, live inputs never naturally end, and an MMD scene
            // is finite exactly when it has a motion (MMDVideoSource exhausts at the VMD's end; a bind-pose
            // scene renders indefinitely).
            NotifyNaturalEnd = media.Source is FilePlaylistItem or YouTubePlaylistItem
                or MMDPlaylistItem { MotionPath.Length: > 0 },
            // The cue's picked subtitle tracks (embedded stream indices or sidecar paths - including a
            // prepared YouTube caption sidecar). Only when the cue is placed on a composition: subtitles
            // need a canvas. Same mapping as the deck's MediaPlayerShowMapper.MapSubtitles.
            Subtitles = MapCueSubtitles(media.Subtitles, hasCanvas: primary is not null),
            // Primary placement's full appearance; the fit enum name maps straight to the framework's fit string
            // (MapFit lowercases it). Any additional placements ride along in ExtraPlacements.
            Placement = primary is null ? null : ToShowVideoPlacement(primary),
            ExtraPlacements = extra is { Length: > 0 } ? extra : null,
            // Per-cue audio routing → per-clip outputs (device + N→M channel map + gain), so the cue plays on
            // exactly its routed lines. Null when the cue declares no routes (then the group/default output).
            AudioRoutes = MapAudioRoutes(media, outputsById),
        };
    }

    /// <summary>GUI per-cue <see cref="CueAudioRoute"/>s → per-clip <see cref="ShowClipAudioRoute"/>s, one per
    /// output line: the line's shared PortAudio runtime plus either an N→M <see cref="ChannelMap"/> array
    /// (out-channel ← src-channel, unrouted = silent) with one line gain when every route shares a gain, or a
    /// per-cell gain matrix when route gains differ. Muted routes are dropped; a fully muted line contributes
    /// no output. Returns an explicit empty list when the cue has no usable routes so HaPlay never falls back
    /// to an inferred/default device.</summary>
    private static IReadOnlyList<ShowClipAudioRoute>? MapAudioRoutes(
        MediaCueNode media, IReadOnlyDictionary<Guid, OutputDefinition> outputsById)
        => MapAudioRoutes(media.AudioRoutes, outputsById, media.LevelDb);

    /// <summary>Live-edit entry: map a cue's edited <see cref="CueAudioRoute"/>s with the current output
    /// definitions, for <c>ShowSession.ApplyActiveAudioRoutesAsync</c>. Same conversion + ordering as the load
    /// path so the <c>clip{i}</c> output order lines up with what the fire path attached.</summary>
    /// <param name="levelDb">The cue's master level (<see cref="MediaCueNode.LevelDb"/>) - it is baked into
    /// the routed gains, so a live route edit must re-apply it or the cue would pop to unity.</param>
    public static IReadOnlyList<ShowClipAudioRoute> MapActiveAudioRoutes(
        IReadOnlyList<CueAudioRoute> routes, IReadOnlyList<OutputDefinition>? outputs, double levelDb = 0)
    {
        var outputsById = outputs?.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First())
                          ?? new Dictionary<Guid, OutputDefinition>();
        return MapAudioRoutes(routes, outputsById, levelDb);
    }

    private static IReadOnlyList<ShowClipAudioRoute> MapAudioRoutes(
        IReadOnlyList<CueAudioRoute>? cueRoutes,
        IReadOnlyDictionary<Guid, OutputDefinition> outputsById,
        double levelDb = 0)
    {
        // Per-cue master (review §6): a single linear factor over every route; ≤ −60 dB = routed silent.
        // Baked into the emitted gains/matrix cells, so fades and envelopes (which multiply the routed
        // gains) compose against it for free.
        var masterGain = levelDb <= CueAutomationPoint.SilenceLevelDb
            ? 0f
            : (float)Math.Pow(10, Math.Min(levelDb, CueAutomationPoint.MaxLevelDb) / 20.0);
        if (cueRoutes is not { Count: > 0 } routes)
            return []; // HaPlay is manual-route-only: no cue routes means deliberately silent.

        var mapped = new List<ShowClipAudioRoute>();
        foreach (var line in routes.Where(r => !r.Muted && r.OutputChannel > 0).GroupBy(r => r.OutputLineId))
        {
            var lineRoutes = line.ToList();
            // Cue output channels are operator-facing and 1-based (1..N); ChannelMap is zero-based.
            // Treating the persisted value as an array index turned a normal stereo 1/2 route into
            // a three-channel [-1, L, R] output. PortAudio then rejected that format on a 2-channel
            // device, so the ShowSession cue faulted as soon as it was fired.
            outputsById.TryGetValue(line.Key, out var def);

            // Encode lines: the matrix MUST span the sink's full combined track layout - a matrix
            // sized only to the highest routed channel would force a channel-count adapter whose
            // default mixing bleeds audio across tracks. Unrouted combined channels stay silent (-1).
            var minChannels = def switch
            {
                // The shared hardware runtime is opened at the line's declared width. Preserve
                // silent trailing channels so no generic channel adapter can upmix into them.
                PortAudioOutputDefinition p => p.ChannelCount,
                FileOutputDefinition f when f.EffectiveEncode.OutputMode != "VideoOnly" =>
                    f.EffectiveEncode.AudioLegs.Sum(l => l.Channels > 0 ? l.Channels : 2),
                LiveStreamOutputDefinition s when s.EffectiveEncode.OutputMode != "VideoOnly" =>
                    s.EffectiveEncode.AudioLegs.Sum(l => l.Channels > 0 ? l.Channels : 2),
                _ => 0,
            };
            var matrix = new int[Math.Max(lineRoutes.Max(r => r.OutputChannel), minChannels)];
            Array.Fill(matrix, -1); // ChannelMap.Silence - channels with no route stay silent
            foreach (var r in lineRoutes)
                if (r.SourceChannel >= 0)
                    matrix[r.OutputChannel - 1] = r.SourceChannel;
            var deviceId = def switch
            {
                // Resolve the configured line's already-open shared runtime in the host factory.
                // Emitting the backend hardware id here would make ShowSession open a second stream.
                PortAudioOutputDefinition => OutputAudioRouteDeviceIds.PortAudio(line.Key),
                // Encode lines resolve through the cue session's audio-output factory (the armed
                // session's combined multi-track sink) - same carrier pattern as the deck.
                FileOutputDefinition or LiveStreamOutputDefinition => OutputAudioRouteDeviceIds.Encode(line.Key),
                _ => null,
            };
            var sampleRate = def switch
            {
                PortAudioOutputDefinition pa => pa.SampleRate,
                NDIOutputDefinition ndi => ndi.AudioSampleRate,
                _ => 0,
            };
            // Per-route gains (review §6): routes with DIFFERING gains become a per-cell gain matrix -
            // the old collapse to one line gain used the linear of the MEAN dB, so 0 dB + −60 dB routes
            // both played at −30 dB. Uniform-gain lines (the common case, and all-0 dB in particular)
            // keep the plain channel map + single line gain: the fade ride then stays on the cheap
            // SetRouteGain path instead of writing a full matrix every 25 ms step.
            var uniformGainDb = lineRoutes.All(r => r.GainDb == lineRoutes[0].GainDb);
            var cells = uniformGainDb
                ? null
                : lineRoutes
                    .Where(r => r.SourceChannel >= 0)
                    .Select(r => new ShowAudioMatrixCell(
                        r.SourceChannel, r.OutputChannel - 1,
                        (float)Math.Pow(10, r.GainDb / 20.0) * masterGain))
                    .ToList();
            if (cells is { Count: > 0 })
            {
                mapped.Add(new ShowClipAudioRoute(deviceId, null, 1f, sampleRate > 0 ? sampleRate : null)
                {
                    MatrixCells = cells,
                    MatrixOutputChannels = matrix.Length,
                });
            }
            else
            {
                var gain = (float)Math.Pow(10, lineRoutes[0].GainDb / 20.0) * masterGain;
                mapped.Add(new ShowClipAudioRoute(deviceId, matrix, gain, sampleRate > 0 ? sampleRate : null));
            }
        }

        return mapped; // all invalid/muted routes is also explicitly silent, never an implicit default device.
    }

    /// <summary>GUI media source → a registry path / URI (D2). Files and images map to their path; live
    /// sources to a <c>scheme:</c> URI. Text cues are handled by the caller (encoded into a <c>text:</c> URI with
    /// their duration, since that needs the cue node, not just the source).</summary>
    private static string? ResolveMediaPath(PlaylistItem? source) => source switch
    {
        FilePlaylistItem f => f.Path,
        ImagePlaylistItem i => i.Path,
        // Live inputs use the SAME descriptor URIs as the deck so a cue-fired item keeps its per-item options
        // (NDI stream selection / bandwidth / audio jitter-buffer override; PortAudio host API / channels /
        // rate / latency) instead of silently opening with provider defaults.
        NDIInputPlaylistItem n => HaPlayPlaybackHelpers.BuildNDIInputUri(n),
        PortAudioInputPlaylistItem p => HaPlayPlaybackHelpers.BuildPortAudioInputUri(p),
        // Prepared-cache youtube asset behind its canonical URI (reliable mode - see the provider).
        YouTubePlaylistItem y => HaPlayPlaybackHelpers.BuildYouTubeUri(y),
        MMDPlaylistItem mmd => HaPlayPlaybackHelpers.BuildMMDUri(mmd),
        _ => null,
    };

    private static IReadOnlyList<ShowSubtitleSelection>? MapCueSubtitles(
        IReadOnlyList<CueSubtitleSelection>? subtitles, bool hasCanvas)
    {
        if (!hasCanvas || subtitles is not { Count: > 0 })
            return null;

        var mapped = new List<ShowSubtitleSelection>(subtitles.Count);
        foreach (var s in subtitles)
        {
            if (s.StreamIndex is { } idx)
                mapped.Add(new ShowSubtitleSelection(StreamIndex: idx)); // embedded container stream
            else if (!string.IsNullOrWhiteSpace(s.Path))
                mapped.Add(new ShowSubtitleSelection(Path: s.Path)); // sidecar file (StreamIndex stays -1)
        }

        return mapped.Count > 0 ? mapped : null;
    }

    private static ClipEndBehavior MapEndBehavior(CueEndBehavior behavior) => behavior switch
    {
        CueEndBehavior.Stop => ClipEndBehavior.Stop,
        CueEndBehavior.FreezeLastFrame => ClipEndBehavior.FreezeLastFrame,
        CueEndBehavior.Loop => ClipEndBehavior.Loop,
        CueEndBehavior.FadeOutAndStop => ClipEndBehavior.FadeOutAndStop,
        _ => ClipEndBehavior.Stop,
    };

    /// <summary>GUI curve → framework curve (Linear on anything unrecognized, never a fade failure).</summary>
    public static FadeCurve MapFadeCurve(CueFadeCurve curve) => curve switch
    {
        CueFadeCurve.EqualPower => FadeCurve.EqualPower,
        CueFadeCurve.Exponential => FadeCurve.Exponential,
        CueFadeCurve.SCurve => FadeCurve.SCurve,
        _ => FadeCurve.Linear,
    };

    /// <summary>GUI volume-automation points (dB, clip-relative ms) → the session's linear-gain envelope
    /// (<see cref="ShowClipBinding.VolumeEnvelope"/>). The dB→linear conversion happens here, at the
    /// GUI/mapper boundary the framework sampler documents: at or below the −60 dB silence floor maps to
    /// exact 0, and levels clamp to the +12 dB authoring ceiling. Points are emitted time-sorted (the
    /// sampler's binary search requires it). Null/empty stays null - no envelope runner is started.</summary>
    public static IReadOnlyList<ShowEnvelopePoint>? MapVolumeEnvelope(IReadOnlyList<CueAutomationPoint>? points)
    {
        if (points is not { Count: > 0 })
            return null;
        return points
            .OrderBy(p => p.TimeMs)
            .Select(p => new ShowEnvelopePoint(
                TimeSpan.FromMilliseconds(Math.Max(0, p.TimeMs)),
                p.LevelDb <= CueAutomationPoint.SilenceLevelDb
                    ? 0f
                    : (float)Math.Pow(10, Math.Min(p.LevelDb, CueAutomationPoint.MaxLevelDb) / 20.0),
                MapFadeCurve(p.CurveToNext)))
            .ToList();
    }

    private static ShowComposition MapComposition(CueComposition composition) => new(
        Id: composition.Id.ToString(),
        Name: composition.Name,
        Width: composition.Width,
        Height: composition.Height,
        FrameRateNum: composition.FrameRateNum,
        FrameRateDen: composition.FrameRateDen,
        OutputMapping: composition is { VideoFxEnabled: true, VideoFx: { } fx } ? ToClipOutputMapping(fx) : null);

    /// <summary>Maps HaPlay's top-left-origin placement to the compositor's bottom-left destination axis.</summary>
    public static ShowVideoPlacement ToShowVideoPlacement(CueVideoPlacement placement) => new(
        placement.DestX,
        1.0 - placement.DestY - placement.DestHeight,
        placement.DestWidth,
        placement.DestHeight,
        placement.Opacity,
        placement.Position.ToString(),
        placement.RotationDegrees,
        placement.CropLeft,
        placement.CropTop,
        placement.CropRight,
        placement.CropBottom,
        placement.VideoFxEnabled ? ToClipOutputMapping(placement.VideoFx) : null,
        ToChromaKeySettings(placement),
        ToColorAdjustSettings(placement));

    /// <summary>Maps the placement's chroma key to the framework settings; null while disabled
    /// (settings are retained on the model but must not key the layer).</summary>
    public static S.Media.Compositor.ChromaKeySettings? ToChromaKeySettings(CueVideoPlacement placement) =>
        placement is { ChromaKeyEnabled: true, ChromaKey: { } key }
            ? new S.Media.Compositor.ChromaKeySettings(
                (float)key.KeyR, (float)key.KeyG, (float)key.KeyB,
                (float)key.Similarity, (float)key.Smoothness, (float)key.SpillSuppression)
            : null;

    /// <summary>Maps the placement's brightness/contrast to the framework settings; null while
    /// disabled (settings retained on the model but must not alter the layer).</summary>
    public static S.Media.Compositor.Effects.BrightnessContrastSettings? ToColorAdjustSettings(CueVideoPlacement placement) =>
        placement is { ColorAdjustEnabled: true, ColorAdjust: { } adjust }
            ? new S.Media.Compositor.Effects.BrightnessContrastSettings(
                (float)adjust.Brightness, (float)adjust.Contrast)
            : null;

    /// <summary>Maps a persisted HaPlay warp/FX model to the session runtime representation.</summary>
    public static ClipOutputMappingSpec? ToClipOutputMapping(CueOutputMapping? mapping) => mapping is null ? null : new(
        Sections: mapping.Sections.Select(MapSection).ToArray(),
        OutputWidth: mapping.OutputWidth,
        OutputHeight: mapping.OutputHeight);

    private static ClipOutputMappingSection MapSection(CueOutputMappingSection section) => new(
        Id: section.Id.ToString(),
        Enabled: section.Enabled,
        SrcX: section.SrcX, SrcY: section.SrcY, SrcWidth: section.SrcWidth, SrcHeight: section.SrcHeight,
        DestX: section.DestX, DestY: section.DestY, DestWidth: section.DestWidth, DestHeight: section.DestHeight,
        RotationDegrees: section.RotationDegrees,
        Opacity: section.Opacity,
        Brightness: section.Brightness,
        MeshColumns: section.MeshColumns,
        MeshRows: section.MeshRows,
        // section.Corners is Phase-3-reserved corner-pin (CueList: "ignored in Phase 1"): no editor produces it
        // and no compositor consumes it - the shipping path drops it too, so omitting it here is exact parity, not
        // a ShowSession regression. When Phase 3 lands, corners will bake to a fine MeshPoints grid (the GL warp is
        // already perspective-correct), so no framework change is needed - only this mapper + the editor.
        MeshPoints: section.MeshPoints?.Select(p => new ClipMeshPoint(p.X, p.Y)).ToArray());
}
