using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Sample;

/// <summary>
/// A plausible mid-show moment for the sample project: what would be sounding, metering and streaming
/// if the engine were running.
/// </summary>
/// <remarks>
/// Every value here is INVENTED, and that is the whole reason this class is separate from
/// <see cref="SampleProject"/>. Phase 5 replaces it with real telemetry from <c>ShowSession</c>; until
/// then, anything the shell shows that comes through here is a picture of a show rather than a fact
/// about one, and a reader can tell which is which by where it came from.
/// </remarks>
public static class SampleRuntime
{
    /// <summary>
    /// Invented telemetry for the SAMPLE show, or nothing at all for any other project.
    /// </summary>
    /// <remarks>
    /// The guard is load-bearing now that the app can open real files. Every value below is written
    /// against the midsummer-2026 show by name — Q13.1, the Wedge, the Lobby TV — and none of it means
    /// anything about somebody else's project. Inventing sounding cues for a show that has none would
    /// be worse than showing an idle one; and before the guard existed, opening any other project
    /// simply threw.
    /// </remarks>
    public static ShowRuntime For(HaCueProject project)
    {
        if (project.CueLists.FirstOrDefault(list => list.Name == "Act 1") is not { } act1
            || project.CueLists.FirstOrDefault(list => list.Name == "Preshow") is not { } preshow
            || project.AudioLines.FirstOrDefault(line => line.Name == "Wedge") is not { } wedge
            || project.VideoOutputs.FirstOrDefault(output => output.Name == "Lobby TV") is not { } lobbyTv)
        {
            // A real project gets an idle runtime: nothing sounding, no meters, no invented log. Which
            // is exactly what the shell should show for a show that is not running.
            return ShowRuntime.Idle;
        }

        var preshowBed = Find(act1, "12");
        var opening = Find(act1, "13");
        var stormBed = Find(act1, "13.1");
        var rain = Find(act1, "13.2");
        var intervalMusic = Find(act1, "15");
        var walkIn = Find(preshow, "2");

        var channels = project.AudioPatch.LogicalChannels.ToDictionary(channel => channel.Name);

        return new ShowRuntime
        {
            Sounding = [preshowBed, opening, stormBed, rain],

            // The one cue whose media is genuinely absent, so Project status has something to find and
            // the tree has a broken row to render.
            Broken = [intervalMusic],

            MediaDurations = new Dictionary<Guid, TimeSpan>
            {
                [preshowBed] = TimeSpan.FromMinutes(6),
                [stormBed] = TimeSpan.FromSeconds(134),
                [rain] = TimeSpan.FromSeconds(48),
                [opening] = TimeSpan.FromSeconds(134),
            },

            Levels = new Dictionary<Guid, OutputLevel>
            {
                [channels["Main L"].Id] = new(5, false),
                [channels["Main R"].Id] = new(4, false),
                [channels["Foldback L"].Id] = new(2, false),
                [channels["Foldback R"].Id] = new(2, false),
                [channels["Sub"].Id] = new(7, IsHot: true),
            },

            AbsentLines = [wedge.Id],
            AbsentVideoOutputs = [lobbyTv.Id],

            ActiveCues =
            [
                new ActiveCueRow
                {
                    Number = "12", Label = "Preshow bed", Clock = "02:41 / 06:00", Progress = 0.44,
                    Destination = "Main L/R",
                },
                new ActiveCueRow
                {
                    Number = "13", Label = "Act 1 · Opening sequence", Qualifier = "timeline · 3 of 4",
                    Clock = "00:38 / 02:14", Progress = 0.28, IsGroup = true,
                },
                new ActiveCueRow
                {
                    Number = "13.1", Label = "Storm bed", Clock = "00:38 / 02:14", Progress = 0.28,
                    Destination = "Main, Fold", IsChild = true,
                },
                new ActiveCueRow
                {
                    Number = "13.2", Label = "Projection · rain", Clock = "00:41 / 00:48",
                    Progress = 0.85, Destination = "Cyc", IsChild = true, IsNearEnd = true,
                },
                new ActiveCueRow
                {
                    Number = "2", Label = "Walk-in music", Qualifier = "Preshow", Clock = "fade 2.1 s",
                    Progress = 0.62, Destination = "Main L/R", IsFading = true,
                },
            ],

            Meters =
            [
                new("ML", 0.58, 0.71),
                new("MR", 0.54, 0.68),
                new("FL", 0.31, 0.44),
                new("FR", 0.29, 0.41),
                new("SUB", 0.97, 0.99, IsClipping: true),
            ],

            LineChips =
            [
                new() { Name = "18i20", Suffix = "master", Detail = "48k · 0 drop · 21 ms · 2/4" },
                new() { Name = "NDI Prog", Detail = "2 rx · 0 drop · 3/8" },
                new() { Name = "Record", Detail = "41:20 · 12 drop · 7/8", Gel = Gel.Amber },
                new() { Name = "Wedge", Detail = "device absent", Gel = Gel.Red },
                new() { Name = "Projector A", Detail = "29.97 · 0 late" },
            ],

            BaySummary = $"5 leases · {project.AudioPatch.LogicalChannels.Count} logical · 48 000 Hz",
            BayClock = "clock 01:12:44.318 · epoch 7 · adv",
            ChaseReadout = "MTC 01:12:44:07",

            LastSeen = project.TriggerInputs.Count >= 2
                ? new Dictionary<Guid, string>
                {
                    [project.TriggerInputs[0].Id] = "note 3 ch 1 · 14:01",
                    [project.TriggerInputs[1].Id] = "/hacue/go · 13:44",
                }
                : [],

            LastSent = project.ActionEndpoints.Count >= 2
                ? new Dictionary<Guid, string>
                {
                    [project.ActionEndpoints[0].Id] = "/eos/cue/7.2 · 14:01",
                    [project.ActionEndpoints[1].Id] = "/ch/01/mix/fader · 13:52",
                }
                : [],

            CompositionStats = CompositionStats(project),
            Log = SampleShow.LogTail,
            TriggerMonitor = SampleShow.TriggerMonitor,
        };
    }

    /// <summary>Composition telemetry, derived from the real compositions so the names cannot drift.</summary>
    private static IReadOnlyList<CompositionStatsRow> CompositionStats(HaCueProject project) =>
    [
        .. project.Compositions.Select((composition, index) => new CompositionStatsRow
        {
            Name = $"{composition.Name} · {composition.Width}×{composition.Height}",
            // The TARGET rate, which is a document fact; an achieved rate is a delta over wall time
            // that only a running compositor can compute.
            Fps = index == 0
                ? new Status(composition.FramesPerSecond.ToString("0.##"), Gel.Green)
                : new Status("28.4", Gel.Amber),
            Layers = VideoPresentation.Layers(project, composition).Count.ToString(),
            Late = index == 0 ? new Status("0") : new Status("6", Gel.Amber),
            Dropped = "0",
        }),
    ];

    private static Guid Find(CueList list, CueNumber number) =>
        list.Flatten().First(cue => cue.Number == number).Id;
}
