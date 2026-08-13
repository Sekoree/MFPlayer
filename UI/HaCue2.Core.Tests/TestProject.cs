using HaCue2.Core.Model;

namespace HaCue2.Core.Tests;

/// <summary>
/// A small but complete show: two lines, a stereo Output Group, a fed patch, a media cue with sends,
/// and the jump/fade/snapshot references the reverse-reference queries are supposed to find.
/// </summary>
/// <remarks>
/// Deliberately not minimal. A fixture with one channel and one cue passes rules that a real project
/// breaks - most of the interesting cases here (a group, two cells on one channel, a cue targeted by
/// two other cues) only exist because the fixture has enough in it to express them.
/// </remarks>
internal sealed class TestProject
{
    public TestProject()
    {
        Interface = new AudioLineDefinition { Name = "18i20", Channels = 8, SampleRate = 48_000 };
        Wedge = new AudioLineDefinition { Name = "Wedge", Channels = 2 };

        MainL = new LogicalAudioChannel { Name = "Main L", SortOrder = 0 };
        MainR = new LogicalAudioChannel { Name = "Main R", SortOrder = 1 };
        FoldL = new LogicalAudioChannel { Name = "Fold L", SortOrder = 2 };
        FoldR = new LogicalAudioChannel { Name = "Fold R", SortOrder = 3 };

        MainGroup = new OutputGroup { Name = "Main", MemberIds = [MainL.Id, MainR.Id] };

        Cyc = new CompositionDefinition { Name = "Cyc" };

        Track = new MediaCueNode
        {
            Number = "1",
            Label = "Preshow bed",
            MediaPath = "preshow-loop.wav",
            LevelDb = -6,
            Sends =
            [
                new CueAudioSend { SourceChannel = 0, LogicalChannelId = MainL.Id },
                new CueAudioSend { SourceChannel = 1, LogicalChannelId = MainR.Id, GainDb = -3 },
            ],
        };

        Jump = new JumpCueNode { Number = "2", Label = "Loop to 1", TargetCueIds = [Track.Id] };
        Fade = new FadeCueNode { Number = "3", Label = "Fade band mics", TargetCueIds = [Track.Id] };

        Snapshot = new PatchSnapshot
        {
            Name = "Act 1",
            Cells =
            [
                new PatchCell { LogicalChannelId = FoldL.Id, LineId = Interface.Id, LineChannel = 2, GainDb = 0 },
                new PatchCell { LogicalChannelId = FoldR.Id, LineId = Interface.Id, LineChannel = 3, GainDb = 0 },
            ],
        };

        List = new CueList { Name = "Act 1", Cues = [Track, Jump, Fade] };

        Project = new HaCueProject
        {
            Title = "midsummer-2026",
            AudioLines = [Interface, Wedge],
            AudioPatch = new ProjectAudioPatch
            {
                ClockMasterLineId = Interface.Id,
                LogicalChannels = [MainL, MainR, FoldL, FoldR],
                Groups = [MainGroup],
                Cells =
                [
                    new PatchCell { LogicalChannelId = MainL.Id, LineId = Interface.Id, LineChannel = 0 },
                    new PatchCell { LogicalChannelId = MainR.Id, LineId = Interface.Id, LineChannel = 1 },
                    new PatchCell { LogicalChannelId = FoldL.Id, LineId = Interface.Id, LineChannel = 2, GainDb = -3 },
                    new PatchCell { LogicalChannelId = FoldR.Id, LineId = Interface.Id, LineChannel = 3, GainDb = -3 },
                ],
            },
            PatchSnapshots = [Snapshot],
            Compositions = [Cyc],
            CueLists = [List],
        };
    }

    public HaCueProject Project { get; }
    public AudioLineDefinition Interface { get; }
    public AudioLineDefinition Wedge { get; }
    public LogicalAudioChannel MainL { get; }
    public LogicalAudioChannel MainR { get; }
    public LogicalAudioChannel FoldL { get; }
    public LogicalAudioChannel FoldR { get; }
    public OutputGroup MainGroup { get; }
    public CompositionDefinition Cyc { get; }
    public CueList List { get; }
    public MediaCueNode Track { get; }
    public JumpCueNode Jump { get; }
    public FadeCueNode Fade { get; }
    public PatchSnapshot Snapshot { get; }

    /// <summary>Feeds Fold L/R too, so the patch has no unfed channels for tests that want it clean.</summary>
    public TestProject WithFoldbackFed()
    {
        Track.Sends.Add(new CueAudioSend { SourceChannel = 0, LogicalChannelId = FoldL.Id, GainDb = -6 });
        Track.Sends.Add(new CueAudioSend { SourceChannel = 1, LogicalChannelId = FoldR.Id, GainDb = -6 });
        return this;
    }
}
