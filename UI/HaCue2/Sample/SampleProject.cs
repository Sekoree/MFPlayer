using HaCue2.Core.Model;

namespace HaCue2.Sample;

/// <summary>
/// The fictional <c>midsummer-2026</c> show as an actual <see cref="HaCueProject"/>.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the hand-authored presentation rows the shell started with. Everything the views
/// show about the SHOW is now derived from this document, which means the counts on screen are real:
/// "logical outputs · 11" is <c>LogicalChannels.Count</c>, the scope tallies are cue counts, and the
/// two failure states screen 06 exists to catch are properties of the patch rather than hard-coded
/// colours. Change a number here and the screens follow.
/// </para>
/// <para>
/// What is NOT here is anything about a running show — see <c>Session/ShowRuntime</c>. The separation
/// is the point: a reader can tell at a glance which values a document could actually produce.
/// </para>
/// </remarks>
public static class SampleProject
{
    public const string FileName = "midsummer-2026.hacue2proj";

    public static HaCueProject Create()
    {
        // ── audio lines ────────────────────────────────────────────────────────────────────────
        var interface18i20 = Line("18i20", AudioLineKind.LocalAudio, 8, 48_000, "Scarlett 18i20 (ALSA)");
        var ndi = Line("NDI Prog", AudioLineKind.Ndi, 2, 48_000, "HACUE-PROG");
        // 44.1 k sink on a 48 k mix: legal, resampled, and never eligible as clock master.
        var record = Line("Record", AudioLineKind.FileRecord, 2, 44_100, "show-{date}.flac");
        var stream = Line("Stream", AudioLineKind.Stream, 2, 48_000, "rtmp://…");
        var wedge = Line("Wedge", AudioLineKind.LocalAudio, 2, null, "Behringer UCA222");

        // ── logical outputs ────────────────────────────────────────────────────────────────────
        var mainL = Channel("Main L", 0);
        var mainR = Channel("Main R", 1);
        var foldL = Channel("Foldback L", 2);
        var foldR = Channel("Foldback R", 3);
        var sub = Channel("Sub", 4);
        var stageL = Channel("Stage cue L", 5);
        var stageR = Channel("Stage cue R", 6);
        var fx = Channel("FX return", 7);
        var orchestra = Channel("Orchestra", 8);
        var lobbyL = Channel("Lobby L", 9);
        var lobbyR = Channel("Lobby R", 10);

        // ── compositions and video outputs ─────────────────────────────────────────────────────
        var cyc = new CompositionDefinition
        {
            Name = "Cyc", Width = 1920, Height = 1080, FramesPerSecond = 29.97,
        };
        var portal = new CompositionDefinition
        {
            Name = "Portal", Width = 1280, Height = 720, FramesPerSecond = 30,
            IdleImagePath = "art/logo.png",
        };

        // ── endpoints and triggers ─────────────────────────────────────────────────────────────
        var eos = new ActionEndpoint
        {
            Name = "Eos", Kind = EndpointKind.OscOut, Host = "10.0.1.20", Port = 8000,
            TestMessage = "/eos/ping",
        };
        var x32 = new ActionEndpoint
        {
            Name = "X32", Kind = EndpointKind.OscOut, Host = "10.0.1.30", Port = 10023,
            TestMessage = "/info",
        };
        var hog = new ActionEndpoint { Name = "Hog wing", Kind = EndpointKind.MidiOut };

        // ── cues ───────────────────────────────────────────────────────────────────────────────
        var preshowBed = new MediaCueNode
        {
            Number = "12", Label = "Preshow bed", MediaPath = "audio/preshow-loop.wav",
            LevelDb = -6, FadeInMs = 3_000, Loop = true,
            Sends = [Send(0, mainL), Send(1, mainR)],
        };

        var houseToHalf = new ActionCueNode
        {
            Number = "12.5", Label = "House to half", EndpointId = eos.Id, Address = "/eos/cue/2/fire",
            Trigger = CueTrigger.Follow,
        };

        var stormBed = new MediaCueNode
        {
            Number = "13.1", Label = "Storm bed", MediaPath = "sfx/storm-bed.flac",
            LevelDb = -3, FadeInMs = 3_000, FadeOutMs = 4_000,
            // Runs under the whole sequence, so it starts at zero and is the longest thing in the group.
            TrimOutMs = 134_000,
            Sends = [Send(0, mainL), Send(1, mainR, -3), Send(0, foldL, -6), Send(1, foldR, -6)],
            Note = "Storm bed runs under the whole opening. Do not stop it on the scene change — "
                 + "Q14 rides the foldback instead.",
            EffectLanes =
            [
                new EffectLane
                {
                    Kind = EffectLaneKind.Volume,
                    Points = [new(0, 0.1), new(0.08, 0.72), new(0.55, 0.72), new(0.7, 0.36),
                              new(0.88, 0.36), new(1, 0.1)],
                },
            ],
        };

        var rain = new MediaCueNode
        {
            Number = "13.2", Label = "Projection · rain", MediaPath = "video/rain-loop.mov",
            FadeInMs = 1_500,
            // Authored positions and lengths, not probed ones: where a clip sits in its group and how
            // much of the file it uses are decisions somebody made, so the timeline can draw them
            // before any media has been opened.
            TimelineOffsetMs = 4_000, TrimOutMs = 48_000,
            Placements = [Place(cyc, layer: 2, 0.06, 0.08, 0.58, 0.78)],
            EffectLanes =
            [
                new EffectLane
                {
                    Kind = EffectLaneKind.Opacity,
                    Points = [new(0.11, 0.9), new(0.16, 0.18), new(0.4, 0.18), new(0.48, 0.9)],
                },
            ],
        };

        var thunder = new MediaCueNode
        {
            Number = "13.3", Label = "Thunder crack (cut for previews)", MediaPath = "sfx/thunder-03.wav",
            LevelDb = 2, Enabled = false, Sends = [Send(0, mainL), Send(1, mainR)],
            TimelineOffsetMs = 26_500, TrimInMs = 1_000, TrimOutMs = 4_500,
        };

        var opening = new GroupCueNode
        {
            Number = "13", Label = "Act 1 · Opening sequence", FireMode = GroupFireMode.Timeline,
            Children = [stormBed, rain, thunder],
        };

        var actOneSnapshot = new PatchSnapshot
        {
            Name = "Act 1",
            Cells =
            [
                Cell(foldL, interface18i20, 2, 0),
                Cell(foldR, interface18i20, 3, 0),
                Cell(sub, interface18i20, 6, 2),
            ],
        };

        var foldbackUp = new PatchCueNode
        {
            Number = "14", Label = "Patch · Act 1 foldback up", SnapshotId = actOneSnapshot.Id,
            FadeMs = 4_000,
        };

        // Deliberately points at a file that is not on disk: the missing-media check has to have
        // something to find, or nobody ever sees Project status do its job.
        var intervalMusic = new MediaCueNode
        {
            Number = "15", Label = "Interval music", MediaPath = "audio/interval.wav",
            LevelDb = -9, FadeInMs = 6_000, Sends = [Send(0, lobbyL, -10), Send(1, lobbyR, -10)],
            Note = "Relink before the get-in. Last seen on the USB stick in the road case.",
        };

        var visualizer = new VisualizerCueNode
        {
            Number = "15.5", Label = "Interval visualizer", PresetPack = "preset pack A",
            Placements = [Place(cyc, layer: 1, 0.60, 0.40, 0.36, 0.55)],
        };

        var loopBack = new JumpCueNode
        {
            Number = "16", Label = "Loop to 12 if held", TargetCueIds = [preshowBed.Id],
            Condition = JumpCondition.WhileTriggerHeld,
        };

        var fadeMics = new FadeCueNode
        {
            Number = "17", Label = "Fade band mics down",
            TargetChannelIds = [mainL.Id, mainR.Id, foldL.Id, foldR.Id],
            DurationMs = 4_000,
        };

        var actTwoMarker = new CommentCueNode
        {
            Number = "18", Label = "— Act 2 begins —",
            Note = "House lights to half on the band's cue, not on a count. Stage manager calls it; "
                 + "we follow. If the call is late, hold — Q19 has a 6 s pre-wait to absorb it.",
        };

        var songs = new GroupCueNode
        {
            Number = "7", Label = "Songs", FireMode = GroupFireMode.Playlist,
            CrossfadeMs = 2_000, Shuffle = true,
            Children =
            [
                Song("5", "Killer Queen", mainL, mainR, cyc),
                Song("6", "Somebody to Love", mainL, mainR, cyc),
                Song("7.5", "Bohemian Rhapsody", mainL, mainR, cyc),
            ],
        };

        var actOne = new CueList
        {
            Name = "Act 1",
            Cues =
            [
                songs, preshowBed, houseToHalf, opening, foldbackUp, intervalMusic, visualizer,
                loopBack, fadeMics, actTwoMarker,
            ],
            StandbyCueId = houseToHalf.Id,
        };

        var preshow = new CueList
        {
            Name = "Preshow",
            Cues =
            [
                new MediaCueNode
                {
                    Number = "1", Label = "Haze loop", MediaPath = "audio/haze.wav", Loop = true,
                    Sends = [Send(0, mainL), Send(1, mainR)],
                },
                new MediaCueNode
                {
                    Number = "2", Label = "Walk-in music", MediaPath = "audio/walk-in.flac",
                    LevelDb = -8, FadeInMs = 2_000, Sends = [Send(0, mainL), Send(1, mainR)],
                },
                new ActionCueNode
                {
                    Number = "3", Label = "House to full", EndpointId = eos.Id, Address = "/eos/cue/1/fire",
                },
            ],
        };

        var interval = new CueList
        {
            Name = "Interval",
            Cues =
            [
                new MediaCueNode
                {
                    Number = "41", Label = "Interval walk-out", MediaPath = "audio/walk-out.flac",
                    LevelDb = -10, Sends = [Send(0, lobbyL, -10), Send(1, lobbyR, -10)],
                },
            ],
        };

        return new HaCueProject
        {
            Title = "midsummer-2026",
            Settings = new ProjectSettings
            {
                MediaRoot = "~/shows/midsummer-media",
                StopFadeMs = 750,
                RemoteApi = new RemoteApiOverride { Enabled = true, Port = 8420, LanAllowed = true },
            },
            AudioLines = [interface18i20, ndi, record, stream, wedge],
            AudioPatch = new ProjectAudioPatch
            {
                MixSampleRate = 48_000,
                ClockMasterLineId = interface18i20.Id,
                LogicalChannels =
                [
                    mainL, mainR, foldL, foldR, sub, stageL, stageR, fx, orchestra, lobbyL, lobbyR,
                ],
                Groups =
                [
                    new OutputGroup { Name = "Main", MemberIds = [mainL.Id, mainR.Id] },
                    new OutputGroup { Name = "Fold", MemberIds = [foldL.Id, foldR.Id] },
                    new OutputGroup { Name = "Stage", MemberIds = [stageL.Id, stageR.Id] },
                    new OutputGroup { Name = "Lobby", MemberIds = [lobbyL.Id, lobbyR.Id] },
                ],
                Cells =
                [
                    Cell(mainL, interface18i20, 0, 0),
                    Cell(mainR, interface18i20, 1, 0),
                    Cell(foldL, interface18i20, 2, -3),
                    Cell(foldR, interface18i20, 3, -3),
                    Cell(fx, interface18i20, 4, -6),
                    Cell(sub, interface18i20, 6, 2),
                    Cell(orchestra, interface18i20, 7, 0),
                    Cell(mainL, ndi, 0, -6),
                    Cell(mainR, ndi, 1, -6),
                    Cell(mainL, record, 0, 0),
                    Cell(mainR, record, 1, 0),
                    // Kept although the device is absent, and muted rather than deleted — the two
                    // states that let an operator get the wedge back when it is plugged in again.
                    Cell(stageL, wedge, 0, 0, muted: true),
                    Cell(stageR, wedge, 1, 0, muted: true),
                    // Lobby L/R are deliberately absent from this list: fed by cues, patched to
                    // nothing. That is the error screen 06 exists to catch, and it is a property of
                    // the document rather than a hard-coded red row.
                ],
            },
            PatchSnapshots =
            [
                new PatchSnapshot { Name = "Preshow", Cells = [Cell(mainL, interface18i20, 0, 0), Cell(mainR, interface18i20, 1, 0)] },
                actOneSnapshot,
                new PatchSnapshot { Name = "Interval", Cells = [Cell(lobbyL, interface18i20, 4, -4), Cell(lobbyR, interface18i20, 5, -4)] },
            ],
            Compositions = [cyc, portal],
            VideoOutputs =
            [
                new VideoOutputDefinition
                {
                    Name = "Projector A", Kind = VideoOutputKind.LocalScreen, CompositionId = cyc.Id,
                    // A NUMBER, like every other output's hint: "screen 2" parsed as nothing, so the
                    // demo showed a screen picker sitting on "anywhere" over a document that said 2.
                    TargetHint = "2", Required = true,
                    Mapping =
                    [
                        new MappingSection
                        {
                            Name = "Left wall", SourceX = 0.02, SourceY = 0.06,
                            SourceWidth = 0.47, SourceHeight = 0.86,
                            TargetX = 0.03, TargetY = 0.10, TargetWidth = 0.44, TargetHeight = 0.80,
                            MeshColumns = 3, MeshRows = 3, Brightness = 0.92,
                        },
                        new MappingSection
                        {
                            Name = "Right wall", SourceX = 0.45, SourceY = 0.06,
                            SourceWidth = 0.52, SourceHeight = 0.86,
                            TargetX = 0.52, TargetY = 0.08, TargetWidth = 0.44, TargetHeight = 0.84,
                        },
                    ],
                },
                new VideoOutputDefinition
                {
                    Name = "Lobby TV", Kind = VideoOutputKind.LocalScreen, CompositionId = cyc.Id,
                    TargetHint = "screen 3", IdleFallbackPath = "art/venue-logo.png",
                },
                new VideoOutputDefinition
                {
                    Name = "NDI Prog", Kind = VideoOutputKind.Ndi, CompositionId = cyc.Id,
                    TargetHint = "HACUE-PROG",
                },
            ],
            ActionEndpoints = [eos, x32, hog],
            TriggerInputs =
            [
                new TriggerInputDefinition
                {
                    Name = "APC mini", Kind = TriggerInputKind.MidiIn, DeviceHint = "APC MINI MIDI 1",
                    Bindings =
                    [
                        new TriggerBinding
                        {
                            Input = "note 3 · ch 1", TargetCueId = loopBack.Id, NoRepeatMs = 250,
                        },
                        new TriggerBinding { Input = "note 4 · ch 1", TargetCueId = fadeMics.Id },
                        // Register item 24: cc → parameter is v1, not just note → cue.
                        new TriggerBinding
                        {
                            Input = "cc 48 · ch 1", Target = TriggerTarget.Parameter,
                            ParameterId = "master.trim", RangeMin = -60, RangeMax = 0,
                        },
                    ],
                },
                new TriggerInputDefinition
                {
                    Name = "QLab bridge", Kind = TriggerInputKind.OscIn, Port = 9000,
                    Bindings = [new TriggerBinding { Input = "/hacue/go", Target = TriggerTarget.Transport }],
                },
                new TriggerInputDefinition { Name = "Hotkeys", Kind = TriggerInputKind.Keyboard },
            ],
            CueLists = [preshow, actOne, interval],
        };
    }

    private static AudioLineDefinition Line(
        string name, AudioLineKind kind, int channels, int? rate, string hint) =>
        new() { Name = name, Kind = kind, Channels = channels, SampleRate = rate, DeviceHint = hint };

    private static LogicalAudioChannel Channel(string name, int order) =>
        new() { Name = name, SortOrder = order };

    private static CueAudioSend Send(int source, LogicalAudioChannel channel, double gainDb = 0) =>
        new() { SourceChannel = source, LogicalChannelId = channel.Id, GainDb = gainDb };

    private static PatchCell Cell(
        LogicalAudioChannel channel, AudioLineDefinition line, int lineChannel, double gainDb,
        bool muted = false) =>
        new()
        {
            LogicalChannelId = channel.Id, LineId = line.Id, LineChannel = lineChannel,
            GainDb = gainDb, Muted = muted,
        };

    private static LayerPlacement Place(
        CompositionDefinition composition, int layer, double x, double y, double w, double h) =>
        new()
        {
            CompositionId = composition.Id, LayerIndex = layer,
            X = x, Y = y, Width = w, Height = h, Fit = LayerFit.Cover,
        };

    /// <summary>
    /// One song as a group of the cues a song actually carries, so the scoped view (screen 03) has
    /// real subtrees to narrow to and the tallies beside them are counts rather than decoration.
    /// </summary>
    private static GroupCueNode Song(
        CueNumber number, string title, LogicalAudioChannel left, LogicalAudioChannel right,
        CompositionDefinition composition) =>
        new()
        {
            Number = number, Label = title, FireMode = GroupFireMode.Timeline,
            Children =
            [
                new MediaCueNode
                {
                    Number = number.Child(1), Label = "Track", TrimOutMs = 214_000,
                    MediaPath = $"songs/{title.ToLowerInvariant().Replace(' ', '-')}.flac",
                    LevelDb = -4, FadeInMs = 1_000, Sends = [Send(0, left), Send(1, right, -0)],
                },
                new ActionCueNode
                {
                    Number = number.Child(2), Label = "Ballad look", Address = $"/eos/cue/{number}.2",
                    TimelineOffsetMs = 18_000,
                },
                new ActionCueNode
                {
                    Number = number.Child(3), Label = "Chorus — full rig", Address = $"/eos/cue/{number}.3",
                    TimelineOffsetMs = 61_500,
                },
                new MediaCueNode
                {
                    Number = number.Child(4), Label = "Projection · silhouettes",
                    MediaPath = "video/silhouette.mov", FadeInMs = 2_000,
                    TimelineOffsetMs = 30_000, TrimOutMs = 96_000,
                    Placements =
                    [
                        new LayerPlacement
                        {
                            CompositionId = composition.Id, LayerIndex = 1,
                            X = 0.1, Y = 0.1, Width = 0.8, Height = 0.8,
                        },
                    ],
                },
            ],
        };
}
