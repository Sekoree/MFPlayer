using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class AutomationTests
{
    [Fact]
    public void EvaluatorUsesAbsoluteCueTimeNativeValuesAndHolds()
    {
        var track = new AutomationTrack
        {
            Target = new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
            Keyframes =
            [
                new AutomationKeyframe { TimeMs = 1_635_000, Value = -6 },
                new AutomationKeyframe { TimeMs = 1_635_100, Value = -18, Hold = true },
                new AutomationKeyframe { TimeMs = 1_635_200, Value = -30 },
            ],
        };

        Assert.Equal(-12, AutomationEvaluator.Sample(track, new HaCueProject(), 1_635_050, 0), 6);
        Assert.Equal(-18, AutomationEvaluator.Sample(track, new HaCueProject(), 1_635_150, 0), 6);
        Assert.Equal(-6, AutomationEvaluator.Sample(track, new HaCueProject(), 0, 0), 6);
        Assert.Equal(-30, AutomationEvaluator.Sample(track, new HaCueProject(), 2_000_000, 0), 6);
    }

    [Fact]
    public void SchemaOneVolumeAndOpacityBecomeAbsolutePropertyTracks()
    {
        var composition = new CompositionDefinition();
        var first = new LayerPlacement { CompositionId = composition.Id, LayerIndex = 1, Opacity = .8 };
        var second = new LayerPlacement { CompositionId = composition.Id, LayerIndex = 2, Opacity = .5 };
        var media = new MediaCueNode
        {
            LevelDb = -6,
            SourceDurationMs = 45 * 60 * 1000,
            Placements = [first, second],
            EffectLanes =
            [
                new EffectLane
                {
                    Kind = EffectLaneKind.Volume,
                    Points = [new LanePoint(0, 1), new LanePoint(.5, .5)],
                },
                new EffectLane
                {
                    Kind = EffectLaneKind.Opacity,
                    Points = [new LanePoint(0, 1), new LanePoint(.5, .25)],
                },
            ],
        };
        var project = new HaCueProject
        {
            SchemaVersion = 1,
            Compositions = [composition],
            CueLists = [new CueList { Cues = [media] }],
        };

        var result = AutomationMigration.Migrate(project);

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.TracksCreated);
        Assert.Equal(HaCueProject.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Empty(media.EffectLanes);
        var volume = media.AutomationTracks.Single(track =>
            track.Target.PropertyId == AutomationPropertyIds.CueVolume);
        Assert.Equal(1_350_000, volume.Keyframes[1].TimeMs);
        Assert.Equal(-12.0205999, volume.Keyframes[1].Value, 6);
        var firstOpacity = media.AutomationTracks.Single(track => track.Target.ObjectId == first.Id);
        var secondOpacity = media.AutomationTracks.Single(track => track.Target.ObjectId == second.Id);
        Assert.Equal(.2, firstOpacity.Keyframes[1].Value, 6);
        Assert.Equal(.125, secondOpacity.Keyframes[1].Value, 6);
    }

    [Fact]
    public void UnknownDurationKeepsTheLegacyLaneUntilProbeFactsExist()
    {
        var media = new MediaCueNode
        {
            EffectLanes =
            [
                new EffectLane
                {
                    Kind = EffectLaneKind.Volume,
                    Points = [new LanePoint(0, 1), new LanePoint(1, 0)],
                },
            ],
        };
        var project = new HaCueProject
        {
            SchemaVersion = 1,
            CueLists = [new CueList { Cues = [media] }],
        };

        var unresolved = AutomationMigration.Migrate(project);
        Assert.Equal(1, unresolved.UnresolvedLanes);
        Assert.Single(media.EffectLanes);
        Assert.Empty(media.AutomationTracks);

        var resolved = AutomationMigration.Migrate(
            project, new Dictionary<Guid, TimeSpan> { [media.Id] = TimeSpan.FromMinutes(30) });
        Assert.True(resolved.IsComplete);
        Assert.Empty(media.EffectLanes);
        Assert.Equal(1_800_000, Assert.Single(media.AutomationTracks).Keyframes[^1].TimeMs);
    }

    [Fact]
    public void CompilerAddressesOpacityToOnlyItsPlacement()
    {
        var composition = new CompositionDefinition();
        var first = new LayerPlacement { CompositionId = composition.Id, LayerIndex = 1, Opacity = .8 };
        var second = new LayerPlacement { CompositionId = composition.Id, LayerIndex = 2, Opacity = .6 };
        var media = new MediaCueNode
        {
            MediaPath = "picture.mp4",
            Placements = [first, second],
            AutomationTracks =
            [
                new AutomationTrack
                {
                    Target = new AutomationTargetRef
                    {
                        PropertyId = AutomationPropertyIds.PlacementOpacity,
                        ObjectId = second.Id,
                    },
                    Keyframes =
                    [
                        new AutomationKeyframe { TimeMs = 0, Value = .6 },
                        new AutomationKeyframe { TimeMs = 500, Value = .2 },
                    ],
                },
            ],
        };
        var project = new HaCueProject
        {
            Compositions = [composition],
            CueLists = [new CueList { Cues = [media] }],
        };

        var clip = Assert.Single(ShowCompiler.Compile(project).Clips);
        var envelope = Assert.Single(clip.PlacementOpacityEnvelopes!);

        Assert.Equal(composition.Id.ToString(), envelope.CompositionId);
        Assert.Equal(2, envelope.LayerIndex);
        Assert.True(envelope.Absolute);
        Assert.Equal(.2f, envelope.Points[^1].Level, 6);
    }
}
