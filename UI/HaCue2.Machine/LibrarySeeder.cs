using HaCue2.Core.Media;
using HaCue2.Core.Model;

namespace HaCue2.Machine;

/// <summary>What to build a fixture project out of.</summary>
/// <param name="Title">The show's name, and the basis of its file name.</param>
/// <param name="MediaRoot">Paths under this are stored relative to it, so the show transports.</param>
public sealed record LibrarySeed(
    string Title,
    string MediaRoot,
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Video);

/// <summary>
/// Builds a project out of real media, so the app can be exercised against files that exist.
/// </summary>
/// <remarks>
/// <para>
/// The point is not a prettier demo. Hand-written sample data agrees with itself by construction and
/// therefore proves nothing: every duration is a round number, every file resolves, no track list
/// surprises the inspector, and the probe has nothing to disagree with. A project built from somebody's
/// actual library has odd lengths, unicode names, files with several audio tracks, cover art where a
/// video track is expected, and formats the decoder has to be asked about — which is where the bugs are.
/// </para>
/// <para>
/// <b>Every cue KIND is emitted</b>, not just media, because the transport resolves them app-side and
/// the only way to notice that a jump target went stale or a patch cue lost a channel is to have one
/// in the fixture.
/// </para>
/// </remarks>
public static class LibrarySeeder
{
    /// <summary>Builds the project. Pure — it reads no files, so it is testable without a library.</summary>
    public static HaCueProject Build(LibrarySeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var project = new HaCueProject { Title = seed.Title };
        project.Settings.MediaRoot = seed.MediaRoot;

        var (mainL, mainR, foldL, foldR, sub) = Channels(project);
        var line = Line(project);
        Patch(project, line, mainL, mainR, foldL, foldR, sub);

        var composition = new CompositionDefinition { Name = "Main screen", FramesPerSecond = 30 };
        project.Compositions.Add(composition);

        project.VideoOutputs.Add(new VideoOutputDefinition
        {
            Name = "Screen 1",
            Kind = VideoOutputKind.LocalScreen,
            CompositionId = composition.Id,
            TargetHint = "1",
        });

        project.PatchSnapshots.Add(new PatchSnapshot
        {
            Name = "Foldback down",
            Cells =
            [
                new PatchCell { LogicalChannelId = foldL.Id, LineId = line.Id, LineChannel = 0, GainDb = -12 },
                new PatchCell { LogicalChannelId = foldR.Id, LineId = line.Id, LineChannel = 1, GainDb = -12 },
            ],
        });

        project.CueLists.Add(MusicList(seed, project, mainL, mainR, foldL, foldR, sub));
        project.CueLists.Add(VideoList(seed, project, composition, mainL, mainR));

        return project;
    }

    private static (LogicalAudioChannel L, LogicalAudioChannel R, LogicalAudioChannel FoldL,
        LogicalAudioChannel FoldR, LogicalAudioChannel Sub) Channels(HaCueProject project)
    {
        var mainL = new LogicalAudioChannel { Name = "Main L", SortOrder = 0 };
        var mainR = new LogicalAudioChannel { Name = "Main R", SortOrder = 1 };
        var foldL = new LogicalAudioChannel { Name = "Fold L", SortOrder = 2 };
        var foldR = new LogicalAudioChannel { Name = "Fold R", SortOrder = 3 };
        var sub = new LogicalAudioChannel { Name = "Sub", SortOrder = 4 };

        project.AudioPatch.LogicalChannels.AddRange([mainL, mainR, foldL, foldR, sub]);
        project.AudioPatch.Groups.Add(new OutputGroup { Name = "Main", MemberIds = [mainL.Id, mainR.Id] });
        project.AudioPatch.Groups.Add(new OutputGroup { Name = "Fold", MemberIds = [foldL.Id, foldR.Id] });

        return (mainL, mainR, foldL, foldR, sub);
    }

    /// <summary>
    /// One stereo line on the machine's default device.
    /// </summary>
    /// <remarks>
    /// An EMPTY device hint on purpose: that means "the default device", which exists on any machine
    /// this fixture is opened on. A named interface would report absent everywhere but one desk, and a
    /// fixture whose first impression is a red line teaches the wrong thing about the red.
    /// </remarks>
    private static AudioLineDefinition Line(HaCueProject project)
    {
        var line = new AudioLineDefinition { Name = "Default out", Channels = 2 };
        project.AudioLines.Add(line);
        project.AudioPatch.ClockMasterLineId = line.Id;
        return line;
    }

    private static void Patch(
        HaCueProject project,
        AudioLineDefinition line,
        LogicalAudioChannel mainL,
        LogicalAudioChannel mainR,
        LogicalAudioChannel foldL,
        LogicalAudioChannel foldR,
        LogicalAudioChannel sub)
    {
        // Main is patched; Fold shares the same pair at a trim, so the patch has a cell that SUMS and
        // the matrix is not merely diagonal.
        //
        // Sub is deliberately left UNPATCHED while a cue feeds it (see the bed below), which is the
        // one condition register item 25 calls an error rather than a warning. A fixture that passes
        // every check teaches nothing about the screen that reports them — this one opens with exactly
        // one real, explainable failure and a fix action that resolves it.
        project.AudioPatch.Cells.AddRange(
        [
            new PatchCell { LogicalChannelId = mainL.Id, LineId = line.Id, LineChannel = 0 },
            new PatchCell { LogicalChannelId = mainR.Id, LineId = line.Id, LineChannel = 1 },
            new PatchCell { LogicalChannelId = foldL.Id, LineId = line.Id, LineChannel = 0, GainDb = -6 },
            new PatchCell { LogicalChannelId = foldR.Id, LineId = line.Id, LineChannel = 1, GainDb = -6 },
        ]);

        _ = sub;
    }

    /// <summary>The audio list: a playlist group, a timeline group, and the control-flow kinds.</summary>
    private static CueList MusicList(
        LibrarySeed seed,
        HaCueProject project,
        LogicalAudioChannel mainL,
        LogicalAudioChannel mainR,
        LogicalAudioChannel foldL,
        LogicalAudioChannel foldR,
        LogicalAudioChannel sub)
    {
        var list = new CueList { Name = "Music" };
        var audio = seed.Audio;
        var number = 1;

        list.Cues.Add(new CommentCueNode { Number = New(ref number), Note = "Built from a real library." });

        // A plain bed. It also feeds Sub, which nothing patches — the deliberate error described in
        // Patch, so the status screen and the red health token have something real to report.
        if (Take(audio, 0) is { } bed)
        {
            list.Cues.Add(Media(project, seed, bed, New(ref number), "Walk-in bed",
                [(mainL, 0), (mainR, 1), (sub, 0)], fadeInMs: 2_000, fadeOutMs: 4_000, loop: true));
        }

        // A playlist group: children chain on their own natural ends, crossfading.
        var playlist = new GroupCueNode
        {
            Number = New(ref number),
            Label = "Set",
            FireMode = GroupFireMode.Playlist,
            CrossfadeMs = 2_000,
        };

        foreach (var (path, index) in audio.Skip(1).Take(4).Select((path, index) => (path, index)))
        {
            playlist.Children.Add(Media(project, seed, path, playlist.Number.Child(index + 1),
                Name(path), [(mainL, 0), (mainR, 1)], fadeInMs: 500, fadeOutMs: 1_500));
        }

        if (playlist.Children.Count > 0)
            list.Cues.Add(playlist);

        // A timeline group: children fire at authored offsets rather than chaining.
        var timeline = new GroupCueNode
        {
            Number = New(ref number),
            Label = "Underscore",
            FireMode = GroupFireMode.Timeline,
        };

        foreach (var (path, index) in audio.Skip(5).Take(3).Select((path, index) => (path, index)))
        {
            var cue = Media(project, seed, path, timeline.Number.Child(index + 1), Name(path),
                [(foldL, 0), (foldR, 1)], fadeInMs: 750, fadeOutMs: 750);

            cue.TimelineOffsetMs = index * 8_000;
            timeline.Children.Add(cue);
        }

        if (timeline.Children.Count > 0)
            list.Cues.Add(timeline);

        // The control-flow kinds, each pointing at something real in this document.
        list.Cues.Add(new PatchCueNode
        {
            Number = New(ref number),
            Label = "Fold down for speech",
            SnapshotId = project.PatchSnapshots[0].Id,
            FadeMs = 1_500,
        });

        var firstMedia = list.Flatten().OfType<MediaCueNode>().FirstOrDefault();

        list.Cues.Add(new FadeCueNode
        {
            Number = New(ref number),
            Label = "Fade everything out",
            FadeEverythingSounding = true,
            DurationMs = 3_000,
            StopTargetsWhenComplete = true,
        });

        if (firstMedia is not null)
        {
            list.Cues.Add(new JumpCueNode
            {
                Number = New(ref number),
                Label = "Back to the top",
                TargetCueIds = [firstMedia.Id],
                FireOnArrival = false,
            });
        }

        return list;
    }

    /// <summary>The video list: real files, placed on the composition.</summary>
    private static CueList VideoList(
        LibrarySeed seed,
        HaCueProject project,
        CompositionDefinition composition,
        LogicalAudioChannel mainL,
        LogicalAudioChannel mainR)
    {
        var list = new CueList { Name = "Video" };
        var number = 1;

        foreach (var (path, index) in seed.Video.Take(4).Select((path, index) => (path, index)))
        {
            var cue = Media(project, seed, path, New(ref number), Name(path),
                [(mainL, 0), (mainR, 1)], fadeInMs: 250, fadeOutMs: 1_000);

            // Full-canvas at its own layer. The second cue is deliberately a half-size inset, so the
            // placement editor has something to show that is not the default rectangle.
            cue.Placements.Add(new LayerPlacement
            {
                CompositionId = composition.Id,
                LayerIndex = index,
                Width = index == 1 ? 0.45 : 1,
                Height = index == 1 ? 0.45 : 1,
                X = index == 1 ? 0.53 : 0,
                Y = index == 1 ? 0.05 : 0,
                Fit = LayerFit.Contain,
            });

            list.Cues.Add(cue);
        }

        return list;
    }

    /// <summary>One media cue with its sends, stored relative to the media root where it can be.</summary>
    private static MediaCueNode Media(
        HaCueProject project,
        LibrarySeed seed,
        string absolutePath,
        CueNumber number,
        string label,
        IReadOnlyList<(LogicalAudioChannel Channel, int Source)> sends,
        int fadeInMs = 0,
        int fadeOutMs = 0,
        bool loop = false)
    {
        var cue = new MediaCueNode
        {
            Number = number,
            Label = label,
            // Relative when it lives under the root, so the project transports; absolute otherwise,
            // which register item 26 allows and the status pass warns about.
            MediaPath = MediaPaths.Store(project, absolutePath, projectPath: null),
            FadeInMs = fadeInMs,
            FadeOutMs = fadeOutMs,
            Loop = loop,
        };

        foreach (var (channel, source) in sends)
            cue.Sends.Add(new CueAudioSend { SourceChannel = source, LogicalChannelId = channel.Id });

        return cue;
    }

    private static CueNumber New(ref int number) => new((number++).ToString());

    /// <summary>A cue label from a file name — readable, and short enough for the tree's column.</summary>
    private static string Name(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length <= 42 ? name : name[..41] + "…";
    }

    private static string? Take(IReadOnlyList<string> paths, int index) =>
        index < paths.Count ? paths[index] : null;
}
