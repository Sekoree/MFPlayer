using System.Globalization;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Engine;
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
            ColorTag = cue.ColorTag,
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
    /// The Active panel's rows, built from what the session says is holding a voice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value here is a RUNTIME fact joined to a document one: the engine supplies the cue id and
    /// its clock, the project supplies the number, label and destination. Nothing is invented — a cue
    /// whose length nobody has probed shows its elapsed time and an empty progress bar rather than a
    /// bar filled to a guess.
    /// </para>
    /// <para>
    /// Ordered by WHEN each was fired, newest LAST, so a row never moves while its cue runs. The
    /// previous key was the playhead, which rewinds on loop wraps, jumps on seeks and freezes on
    /// pause — every one of those reshuffled the list under the pointer. Cue id breaks ties so a
    /// batch fire (a group's stems, all stamped together) keeps one stable order. Scope never
    /// filters this list: a sounding cue the operator cannot see is the one thing the Active panel
    /// exists to prevent.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ActiveCueRow> Active(
        HaCueProject project,
        IReadOnlyList<ActiveCueState> states,
        IReadOnlyDictionary<Guid, TimeSpan> durations)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(durations);

        var rows = new List<ActiveCueRow>();

        // Every cue that is inside a group, gathered ONCE. This was a full walk of the show per active
        // cue — on a 600-cue show with five sounding, three thousand node visits four times a second
        // to answer a question whose answer does not change between rows.
        var children = project.AllCues()
            .OfType<GroupCueNode>()
            .SelectMany(group => group.Children)
            .Select(child => child.Id)
            .ToHashSet();

        foreach (var state in states.OrderBy(state => state.StartedTicks).ThenBy(state => state.CueId))
        {
            if (project.FindCue(state.CueId) is not { } cue)
                continue;

            // The TRANSPORT's own answer first, and the file probe's only as a fallback. The transport
            // already accounts for the trim and exists on a machine that has not probed the file; the
            // probe is what the cue list uses before anything is playing.
            var length = state.Length
                ?? (cue is MediaCueNode media
                    ? media.TrimmedLength(durations.TryGetValue(cue.Id, out var probed) ? probed : null)
                    : cue is TextCueNode { DurationMs: > 0 } text
                        ? TimeSpan.FromMilliseconds(text.DurationMs)
                        : null);

            var remaining = length is { } total
                ? total - state.Elapsed < TimeSpan.Zero ? TimeSpan.Zero : total - state.Elapsed
                : (TimeSpan?)null;

            rows.Add(new ActiveCueRow
            {
                CueId = cue.Id,
                Number = Number(cue.Number),
                Label = cue.Label,
                // Which list, but only when there is more than one — on a single-list show the name
                // would be on every row and tell the operator nothing.
                Qualifier = project.CueLists.Count > 1
                    ? project.CueLists.FirstOrDefault(list => list.Id == state.ListId)?.Name ?? ""
                    : "",
                Clock = Clock(state.Elapsed),
                // Counted DOWN, and marked as such. "How long have I got" is the question somebody
                // driving a show asks; "how long has it been" is the question they ask afterwards.
                Remaining = remaining is { } left ? $"−{Clock(left)}" : "",
                Length = length is { } run ? Clock(run) : "",
                Position = state.Elapsed,
                Duration = length,
                Progress = length is { TotalMilliseconds: > 0 } span
                    ? Math.Clamp(state.Elapsed / span, 0, 1)
                    : 0,
                Destination = Destination(project, cue),
                IsChild = children.Contains(cue.Id),
                IsFading = state.IsFading,
                // Ten seconds, not a fraction: what matters to the person driving is how long they
                // have, and that is the same ten seconds on a 30-second sting and a 6-minute bed.
                IsNearEnd = remaining is { } close && close > TimeSpan.Zero && close <= TimeSpan.FromSeconds(10),
            });
        }

        return rows;
    }

    /// <summary>
    /// The Active panel's rows, with a group's sounding children gathered under one header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A playlist group of twelve used to fill the panel with twelve equal rows and nothing saying they
    /// were one thing. Now the group is one row that owns them — with the whole group's remaining time,
    /// which is the number an operator actually wants ("how long until this is done"), and the rest of
    /// the chain underneath with a countdown to each.
    /// </para>
    /// <para>
    /// Cues that are not in a group stay top-level rows, unchanged. A group appears only when something
    /// inside it is sounding; the panel is still strictly "what is holding a voice", plus what that
    /// commits the show to next.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<object> ActivePanel(
        HaCueProject project,
        IReadOnlyList<ActiveCueRow> active,
        IReadOnlyDictionary<Guid, TimeSpan> durations)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(durations);

        // Which group owns each cue, gathered once rather than walked per row.
        var owner = new Dictionary<Guid, GroupCueNode>();

        foreach (var group in project.AllCues().OfType<GroupCueNode>())
        {
            foreach (var child in group.Children)
                owner[child.Id] = group;
        }

        var rows = new List<object>();
        var placed = new Dictionary<Guid, ActiveGroupRow>();

        foreach (var row in active)
        {
            if (!owner.TryGetValue(row.CueId, out var group))
            {
                rows.Add(row);
                continue;
            }

            if (!placed.TryGetValue(group.Id, out var header))
            {
                header = new ActiveGroupRow
                {
                    GroupId = group.Id,
                    Number = Number(group.Number),
                    Label = group.Label,
                    Mode = group.FireMode switch
                    {
                        GroupFireMode.Playlist => "playlist",
                        GroupFireMode.Timeline => "timeline",
                        _ => "together",
                    },
                };

                placed[group.Id] = header;
                rows.Add(header);
            }

            header.Children.Add(row);
        }

        foreach (var (groupId, header) in placed)
        {
            if (project.FindCue(groupId) is not GroupCueNode group)
                continue;

            Aggregate(group, header, durations);
        }

        return rows;
    }

    /// <summary>
    /// Fills a group header's clock, progress and upcoming chain from what its children will do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole group's remaining, not the current item's: a playlist's operator is waiting for the
    /// LIST to finish. Lengths come from the probe, so a group whose files nobody has looked at shows a
    /// count and no clock rather than a number that would be a guess.
    /// </para>
    /// <para>
    /// <b>Only a playlist adds up.</b> Its items succeed one another, so its clock is the sum of their
    /// lengths and its count-down is the current item's remainder plus everything after it. Every other
    /// mode's children sound at the SAME time, so the group is exactly as long as its longest child —
    /// summing them reported a group of eleven three-minute stems, fired all together, as running for
    /// half an hour, and drove the progress bar from a total the group never had.
    /// </para>
    /// </remarks>
    private static void Aggregate(
        GroupCueNode group,
        ActiveGroupRow header,
        IReadOnlyDictionary<Guid, TimeSpan> durations)
    {
        var sounding = header.Children.Select(child => child.CueId).ToHashSet();

        // Everything the group still owes: what is playing now, plus the chain after it. An
        // ALL-TOGETHER group owes nothing beyond what is already up — they all started at once.
        var chained = group.FireMode is GroupFireMode.Playlist or GroupFireMode.Timeline;

        // A playlist accumulates; the simultaneous modes take the longest. A TIMELINE is simultaneous
        // too — its children overlap — but each starts at its authored offset, so its span is measured
        // from the group's own zero rather than from each child's start.
        var overlaps = group.FireMode is not GroupFireMode.Playlist;
        var offsets = group.FireMode is GroupFireMode.Timeline;

        var playing = group.Children.Where(child => child.Enabled).ToList();
        var known = playing.All(child => Played(child, durations) is not null);

        TimeSpan Start(CueNode child) =>
            offsets ? TimeSpan.FromMilliseconds(child.TimelineOffsetMs) : TimeSpan.Zero;

        TimeSpan End(CueNode child) => Start(child) + (Played(child, durations) ?? TimeSpan.Zero);

        // How far into the group the show is. Read from a SOUNDING child's own playhead rather than
        // accumulated, because with overlapping children there is no chain to accumulate along: the
        // group's clock is whichever child has got furthest through the group's span.
        var elapsed = TimeSpan.Zero;

        foreach (var child in playing.Where(child => sounding.Contains(child.Id)))
        {
            var into = Start(child) + header.Children.First(row => row.CueId == child.Id).Position;
            if (into > elapsed)
                elapsed = into;
        }

        var total = overlaps
            ? playing.Aggregate(TimeSpan.Zero, (longest, child) =>
                End(child) is var ends && ends > longest ? ends : longest)
            : playing.Aggregate(TimeSpan.Zero, (sum, child) => sum + (Played(child, durations) ?? TimeSpan.Zero));

        var remaining = overlaps
            ? (total > elapsed ? total - elapsed : TimeSpan.Zero)
            : TimeSpan.Zero;

        var reached = false;
        var ahead = TimeSpan.Zero;

        foreach (var child in playing)
        {
            if (sounding.Contains(child.Id))
            {
                reached = true;

                if (!overlaps)
                {
                    var row = header.Children.First(item => item.CueId == child.Id);
                    var span = row.Duration is { } length ? length - row.Position : TimeSpan.Zero;
                    remaining += span > TimeSpan.Zero ? span : TimeSpan.Zero;
                    ahead = remaining;
                }

                continue;
            }

            // Only what comes AFTER something that is playing counts as upcoming; a playlist that has
            // passed an item is not going to play it again this pass.
            if (!reached || !chained)
                continue;

            // A timeline cue is due at its own offset; a playlist item is due when everything queued
            // before it has finished. Both are already inside `total`, so neither extends it.
            var starts = overlaps
                ? (Start(child) > elapsed ? Start(child) - elapsed : TimeSpan.Zero)
                : ahead;

            header.Upcoming.Add(new UpcomingCueRow(
                Number(child.Number),
                child.Label,
                Played(child, durations) is { } run ? Clock(run) : "—",
                $"in {Clock(starts)}"));

            if (!overlaps)
            {
                remaining += Played(child, durations) ?? TimeSpan.Zero;
                ahead += Played(child, durations) ?? TimeSpan.Zero;
            }
        }

        header.Clock = known && total > TimeSpan.Zero
            ? $"−{Clock(remaining)} / {Clock(total)}"
            : $"{header.Children.Count} playing";

        header.Progress = known && total.TotalMilliseconds > 0
            ? Math.Clamp(1 - (remaining / total), 0, 1)
            : 0;

        header.IsNearEnd = known && remaining > TimeSpan.Zero && remaining <= TimeSpan.FromSeconds(10);

        // "item 3/12" is the position an operator calls out over talkback.
        var playable = group.Children.Count(child => child.Enabled);
        var at = group.Children.Where(child => child.Enabled).ToList()
            .FindIndex(child => sounding.Contains(child.Id));

        header.Position = at >= 0 && chained ? $"item {at + 1}/{playable}" : $"{playable} cues";
    }

    /// <summary>How long a cue will play for, or null when nothing has looked at its file.</summary>
    private static TimeSpan? Played(CueNode cue, IReadOnlyDictionary<Guid, TimeSpan> durations) =>
        cue is MediaCueNode media
            ? media.TrimmedLength(durations.TryGetValue(cue.Id, out var probed) ? probed : null)
            : cue is TextCueNode { DurationMs: > 0 } text
                ? TimeSpan.FromMilliseconds(text.DurationMs)
                : null;

    /// <summary>Where a sounding cue is going, as the Active panel's right-hand column.</summary>
    private static string Destination(HaCueProject project, CueNode cue)
    {
        if (cue is TextCueNode text)
        {
            var compositions = text.Placements
                .Select(placement => CompositionName(project, placement))
                .Distinct()
                .ToList();

            return compositions.Count switch
            {
                0 => "not placed",
                <= 2 => string.Join(", ", compositions),
                _ => $"{compositions.Count} compositions",
            };
        }

        if (cue is not MediaCueNode media)
            return "—";

        var names = PatchOperations.DestinationsOf(project, media)
            .Select(channel => channel.Name)
            .ToList();

        return names.Count switch
        {
            0 => "not routed",
            <= 2 => string.Join(", ", names),
            _ => $"{names.Count} outputs",
        };
    }

    /// <summary>mm:ss, counting past an hour rather than wrapping.</summary>
    private static string Clock(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";

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
        MediaCueNode { Placements.Count: > 0 } => ViewModels.CueKind.Video,
        MediaCueNode => ViewModels.CueKind.Media,
        GroupCueNode => ViewModels.CueKind.Group,
        ActionCueNode => ViewModels.CueKind.Action,
        FadeCueNode => ViewModels.CueKind.Fade,
        JumpCueNode => ViewModels.CueKind.Jump,
        VisualizerCueNode => ViewModels.CueKind.Visualizer,
        PatchCueNode => ViewModels.CueKind.Patch,
        TextCueNode => ViewModels.CueKind.Text,
        _ => ViewModels.CueKind.Comment,
    };

    /// <summary>One line of a card's words, with the newlines shown rather than swallowed.</summary>
    private static string Quote(string text)
    {
        var single = text.ReplaceLineEndings(" ⏎ ").Trim();

        return single.Length <= 48 ? $"“{single}”" : $"“{single[..47]}…”";
    }

    private static string Source(CueNode cue, HaCueProject project, ShowRuntime runtime) => cue switch
    {
        // A missing file replaces the path with why it matters, because on this row the path is no
        // longer the useful fact.
        MediaCueNode media when runtime.Broken.Contains(cue.Id) =>
            $"media offline · {Path.GetFileName(media.MediaPath)}",

        // A URI is unreadable at row width and mostly punctuation. What identifies a live cue is the
        // camera's name or the device's, which is what Describe pulls out of it.
        MediaCueNode media when SourceUri.IsSource(media.MediaPath) => SourceUri.Describe(media.MediaPath),

        MediaCueNode media => media.MediaPath,

        // The WORDS, trimmed to the column. A card's path is a cache file nobody chose and nobody can
        // read; what identifies it in a list is what it says.
        TextCueNode text when string.IsNullOrWhiteSpace(text.Text) => "no words yet",
        TextCueNode text => Quote(text.Text),

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
        TextCueNode { FadeInMs: > 0 } text => Seconds(text.FadeInMs),
        FadeCueNode fade => Seconds(fade.DurationMs),
        PatchCueNode { FadeMs: > 0 } patch => Seconds(patch.FadeMs),
        VisualizerCueNode visualizer => Seconds(visualizer.BlendMs),
        _ => "—",
    };

    /// <summary>
    /// A media file's duration is a MACHINE fact — it comes from probing the file, not from the
    /// document — so it arrives through the runtime and reads "—" until something has looked.
    /// </summary>
    /// <summary>
    /// How long the cue PLAYS for, which is the trimmed length rather than the file's.
    /// </summary>
    /// <remarks>
    /// It showed the raw file duration, so a cue trimmed to a ten-second sting out of a four-minute
    /// track read as four minutes — the one number in the row an operator uses to plan, wrong by the
    /// whole of the trim.
    /// </remarks>
    private static string Length(CueNode cue, ShowRuntime runtime)
    {
        if (cue is TextCueNode text)
            return text.DurationMs > 0
                ? FormatDuration(TimeSpan.FromMilliseconds(text.DurationMs))
                : "hold";

        if (!runtime.MediaDurations.TryGetValue(cue.Id, out var duration))
            return "—";

        var played = cue is MediaCueNode media ? media.TrimmedLength(duration) ?? duration : duration;

        return FormatDuration(played);
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

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
                // A live cue behaves unlike every other row in the list: it never ends, it cannot be
                // seeked, and firing it claims a device or a network connection. Worth one word.
                if (SourceUri.IsLive(media.MediaPath))
                    badges.Add(new Badge("live", Gel.Congo));
                if (media.Loop)
                    badges.Add(new Badge("loop"));
                foreach (var placement in media.Placements)
                    badges.Add(new Badge(CompositionName(project, placement)));
                foreach (var lane in media.EffectLanes)
                    badges.Add(LaneBadge(lane));
                break;

            case GroupCueNode group:
                badges.Add(new Badge(group.FireMode.ToString().ToLowerInvariant()));
                break;

            case TextCueNode text:
                if (text.DurationMs == 0)
                    badges.Add(new Badge("hold"));
                foreach (var placement in text.Placements)
                    badges.Add(new Badge(CompositionName(project, placement)));
                foreach (var lane in text.EffectLanes)
                    badges.Add(LaneBadge(lane));
                break;

            case ActionCueNode action:
                var endpoint = project.ActionEndpoints.FirstOrDefault(item => item.Id == action.EndpointId);
                badges.Add(endpoint?.Kind == EndpointKind.MidiOut
                    ? new Badge("MIDI", Gel.Congo)
                    : new Badge("OSC", Gel.Steel));
                break;

            case VisualizerCueNode visualizer:
                foreach (var placement in visualizer.Placements)
                    badges.Add(new Badge(CompositionName(project, placement)));
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
