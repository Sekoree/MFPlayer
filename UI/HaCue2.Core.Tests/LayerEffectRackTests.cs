using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Core.Validation;
using S.Media.Compositor.Effects;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class LayerEffectRackTests
{
    [Fact]
    public void SchemaTwoTypedEffectsMigrateInRenderOrderWithStableIds()
    {
        var chromaId = Guid.NewGuid();
        var colourId = Guid.NewGuid();
        var json = $$"""
            {
              "schemaVersion": 2,
              "cueLists": [{ "cues": [{
                "kind": "media",
                "placements": [{
                  "chromaKey": { "id": "{{chromaId}}", "similarity": 0.61 },
                  "chromaKeyEnabled": false,
                  "colorAdjust": { "id": "{{colourId}}", "brightness": 0.2, "contrast": 1.5 }
                }]
              }] }]
            }
            """;

        var project = HaCueProjectFile.Deserialize(json);
        var placement = Assert.Single(Assert.IsType<MediaCueNode>(project.AllCues().Single()).Placements);

        Assert.Equal(HaCueProject.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Null(placement.ChromaKey);
        Assert.Null(placement.ColorAdjust);
        Assert.Collection(
            placement.Effects,
            effect =>
            {
                Assert.Equal(chromaId, effect.Id);
                Assert.Equal(ChromaKeyVideoEffect.EffectId, effect.EffectTypeId);
                Assert.False(effect.Enabled);
                Assert.Equal(.61, effect.Read("similarity", 0), 6);
            },
            effect =>
            {
                Assert.Equal(colourId, effect.Id);
                Assert.Equal(BrightnessContrastVideoEffect.EffectId, effect.EffectTypeId);
                Assert.True(effect.Enabled);
            });
    }

    [Fact]
    public void CompilerPreservesRackOrderBypassAndGenericParameterAutomation()
    {
        var composition = new CompositionDefinition { Id = Guid.NewGuid(), Name = "screen" };
        var colour = LayerEffectCatalog.Create(BrightnessContrastVideoEffect.EffectId);
        var chroma = LayerEffectCatalog.Create(ChromaKeyVideoEffect.EffectId);
        LayerEffectRack.Write(chroma, "keyR", .25);
        var cue = new MediaCueNode
        {
            MediaPath = "picture.mp4",
            Placements =
            [
                new LayerPlacement
                {
                    CompositionId = composition.Id,
                    Effects = [colour, chroma],
                },
            ],
            AutomationTracks =
            [
                new AutomationTrack
                {
                    Target = new AutomationTargetRef
                    {
                        ObjectId = chroma.Id,
                        PropertyId = LayerEffectCatalog.PropertyId(ChromaKeyVideoEffect.EffectId, "keyR"),
                    },
                    Keyframes =
                    [
                        new AutomationKeyframe { TimeMs = 0, Value = .25 },
                        new AutomationKeyframe { TimeMs = 100, Value = .75 },
                    ],
                },
            ],
        };
        var project = new HaCueProject
        {
            Compositions = [composition],
            CueLists = [new CueList { Cues = [cue] }],
        };

        var clip = Assert.Single(ShowCompiler.Compile(project).Clips);
        Assert.Collection(
            clip.Placement!.Effects!,
            effect => Assert.Equal(BrightnessContrastVideoEffect.EffectId, effect.EffectTypeId),
            effect => Assert.Equal(ChromaKeyVideoEffect.EffectId, effect.EffectTypeId));
        var lane = Assert.Single(clip.PlacementEffectEnvelopes!);
        Assert.Equal(chroma.Id.ToString(), lane.EffectInstanceId);
        Assert.Equal("keyR", lane.ParameterId);
        Assert.Equal(ShowPlacementEffectProperty.Custom, lane.Property);
        Assert.Equal(.75f, lane.Points[^1].Level, 5);
    }

    [Fact]
    public void CatalogOffersEveryDescriptorParameterForTheConcreteInstance()
    {
        var effect = LayerEffectCatalog.Create(ChromaKeyVideoEffect.EffectId);
        var cue = new MediaCueNode { Placements = [new LayerPlacement { Effects = [effect] }] };

        var targets = AutomationPropertyCatalog.ForCue(cue)
            .Where(option => option.Target.ObjectId == effect.Id)
            .ToArray();

        Assert.Equal(6, targets.Length);
        Assert.Contains(targets, option => option.Target.PropertyId ==
            LayerEffectCatalog.PropertyId(ChromaKeyVideoEffect.EffectId, "keyR"));
        Assert.All(targets, option => Assert.Equal(AutomationTargetKind.EffectInstance, option.Descriptor.TargetKind));
    }

    [Fact]
    public void ValidationRejectsDuplicateInstancesAndOutOfRangeParameters()
    {
        var composition = new CompositionDefinition { Id = Guid.NewGuid() };
        var first = LayerEffectCatalog.Create(BrightnessContrastVideoEffect.EffectId);
        var second = LayerEffectCatalog.Create(BrightnessContrastVideoEffect.EffectId);
        second.Id = first.Id;
        LayerEffectRack.Write(first, "contrast", 99);
        var project = new HaCueProject
        {
            Compositions = [composition],
            CueLists =
            [
                new CueList
                {
                    Cues =
                    [
                        new MediaCueNode
                        {
                            Placements =
                            [
                                new LayerPlacement
                                {
                                    CompositionId = composition.Id,
                                    Effects = [first, second],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var issues = ProjectValidator.Validate(project);
        Assert.Contains(issues, issue => issue.Message.Contains("duplicate effect instance", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Message.Contains("Contrast outside", StringComparison.Ordinal));
    }

    [Fact]
    public void AudioInsertCatalogAndCompilerLowerStableParameterAutomation()
    {
        var effect = AudioEffectCatalog.Create(S.Media.Routing.GainAudioEffect.EffectId);
        var propertyId = AudioEffectCatalog.PropertyId(
            effect.EffectTypeId, S.Media.Routing.GainAudioEffect.GainParameterId);
        var cue = new MediaCueNode
        {
            MediaPath = "tone.wav",
            AudioEffects = [effect],
            AutomationTracks =
            [
                new AutomationTrack
                {
                    Target = new AutomationTargetRef { ObjectId = effect.Id, PropertyId = propertyId },
                    Keyframes =
                    [
                        new AutomationKeyframe { TimeMs = 0, Value = -6 },
                        new AutomationKeyframe { TimeMs = 1000, Value = 3 },
                    ],
                },
            ],
        };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [cue] }] };

        var descriptor = Assert.Single(AutomationPropertyCatalog.ForCue(cue), option =>
            option.Target.ObjectId == effect.Id).Descriptor;
        Assert.Equal(AutomationDomain.SessionAudio, descriptor.Domain);
        Assert.Equal(AutomationScale.Decibels, descriptor.Value.Scale);
        var clip = Assert.Single(ShowCompiler.Compile(project).Clips);
        Assert.Equal(effect.Id.ToString(), Assert.Single(clip.AudioEffects!).InstanceId);
        var lane = Assert.Single(clip.AudioEffectEnvelopes!);
        Assert.Equal(S.Media.Routing.GainAudioEffect.GainParameterId, lane.ParameterId);
        Assert.Equal(3f, lane.Points[^1].Level, 5);
        ShowDocumentValidator.ThrowIfInvalid(ShowCompiler.Compile(project));
    }

    [Fact]
    public void SchemaThreeRoundTripsVideoAndAudioEffectIdentityAndValues()
    {
        var video = LayerEffectCatalog.Create(ChromaKeyVideoEffect.EffectId);
        var audio = AudioEffectCatalog.Create(S.Media.Routing.GainAudioEffect.EffectId);
        LayerEffectRack.Write(video, "similarity", .73);
        audio.Parameters.Single(parameter => parameter.ParameterId ==
            S.Media.Routing.GainAudioEffect.GainParameterId).Value = -8;
        var project = new HaCueProject
        {
            CueLists =
            [
                new CueList
                {
                    Cues =
                    [
                        new MediaCueNode
                        {
                            Placements = [new LayerPlacement { Effects = [video] }],
                            AudioEffects = [audio],
                        },
                    ],
                },
            ],
        };

        var restored = Assert.IsType<MediaCueNode>(HaCueProjectFile.Deserialize(
            HaCueProjectFile.Serialize(project)).AllCues().Single());

        var restoredVideo = Assert.Single(Assert.Single(restored.Placements).Effects);
        Assert.Equal(video.Id, restoredVideo.Id);
        Assert.Equal(.73, restoredVideo.Read("similarity", 0), 6);
        var restoredAudio = Assert.Single(restored.AudioEffects);
        Assert.Equal(audio.Id, restoredAudio.Id);
        Assert.Equal(-8, restoredAudio.Read(S.Media.Routing.GainAudioEffect.GainParameterId, 0));
    }
}
