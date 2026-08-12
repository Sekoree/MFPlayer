using System.Text.Json;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using S.Media.Core.Video;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class ProjectFileTests
{
    [Fact]
    public void BezierFadeAndLaneTangentsRoundTripAdditively()
    {
        var media = new MediaCueNode
        {
            FadeInCurve = new CurveSpec
            {
                Points =
                [
                    new S.Media.Session.FadeCurvePoint(
                        0, 0, OutHandleX: 0.2, OutHandleLevel: 0.8),
                    new S.Media.Session.FadeCurvePoint(
                        1, 1, InHandleX: 0.8, InHandleLevel: 0.2),
                ],
            },
            EffectLanes =
            [
                new EffectLane
                {
                    Points =
                    [
                        new LanePoint(0, 0, OutHandleX: 0.25, OutHandleY: 0.75),
                        new LanePoint(1, 1, InHandleX: 0.75, InHandleY: 0.25),
                    ],
                },
            ],
        };
        var project = new HaCueProject
        {
            CueLists = [new CueList { Cues = [media] }],
        };

        var restored = Assert.IsType<MediaCueNode>(
            Assert.Single(HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project)).CueLists).Cues[0]);

        Assert.Equal(0.2, restored.FadeInCurve.Points![0].OutHandleX);
        Assert.Equal(0.25, restored.EffectLanes[0].Points[0].OutHandleX);
    }

    [Fact]
    public void ExactCompositionRateRoundTripsAlongsideItsDisplayDecimal()
    {
        var composition = new CompositionDefinition { Name = "Broadcast" };
        composition.SetFrameRate(new Rational(60_000, 1_001));
        var project = new HaCueProject { Compositions = [composition] };

        var json = HaCueProjectFile.Serialize(project);
        var restored = Assert.Single(HaCueProjectFile.Deserialize(json).Compositions);

        Assert.DoesNotContain("exactFrameRate", json);
        Assert.Equal(60_000, restored.ExactFrameRate.Numerator);
        Assert.Equal(1_001, restored.ExactFrameRate.Denominator);
        Assert.Equal(60_000d / 1_001d, restored.FramesPerSecond, 9);
    }

    [Fact]
    public void ExplicitUncommonRatioIsNotReapproximatedThroughTheDisplayDecimal()
    {
        var composition = new CompositionDefinition();

        composition.SetFrameRate(new Rational(12_345, 678));

        Assert.Equal(new Rational(4_115, 226), composition.ExactFrameRate);
        Assert.Equal(4_115d / 226d, composition.FramesPerSecond, 12);
    }

    [Fact]
    public void RepairsTheKnownFirstPanelDestinationWritebackFromSplitProjects()
    {
        var project = new HaCueProject
        {
            VideoOutputs =
            [
                new VideoOutputDefinition
                {
                    Name = "Projector",
                    Mapping =
                    [
                        new MappingSection
                        {
                            Name = "Panel r1 c1",
                            SourceWidth = .5,
                            SourceHeight = .5,
                            TargetWidth = 1,
                            TargetHeight = 1,
                        },
                        new MappingSection
                        {
                            Name = "Panel r1 c2",
                            SourceX = .5,
                            SourceWidth = .5,
                            SourceHeight = .5,
                            TargetX = .5,
                            TargetWidth = .5,
                            TargetHeight = .5,
                        },
                        new MappingSection
                        {
                            Name = "Panel r2 c1",
                            SourceY = .5,
                            SourceWidth = .5,
                            SourceHeight = .5,
                            TargetY = .5,
                            TargetWidth = .5,
                            TargetHeight = .5,
                        },
                        new MappingSection
                        {
                            Name = "Panel r2 c2",
                            SourceX = .5,
                            SourceY = .5,
                            SourceWidth = .5,
                            SourceHeight = .5,
                            TargetX = .5,
                            TargetY = .5,
                            TargetWidth = .5,
                            TargetHeight = .5,
                        },
                    ],
                },
            ],
        };

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));
        var first = restored.VideoOutputs[0].Mapping[0];
        Assert.Equal(.5, first.TargetWidth, 6);
        Assert.Equal(.5, first.TargetHeight, 6);
    }

    [Fact]
    public void DoesNotRepairAHandNamedFullFrameDestination()
    {
        var project = new HaCueProject
        {
            VideoOutputs =
            [
                new VideoOutputDefinition
                {
                    Mapping =
                    [
                        new MappingSection { Name = "Hero", SourceWidth = .5 },
                        new MappingSection
                        {
                            Name = "Side",
                            SourceX = .5,
                            SourceWidth = .5,
                            TargetX = .5,
                            TargetWidth = .5,
                        },
                    ],
                },
            ],
        };

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));
        Assert.Equal(1, restored.VideoOutputs[0].Mapping[0].TargetWidth, 6);
    }

    [Fact]
    public void AProjectRoundTripsThroughJson()
    {
        var original = new TestProject().Project;

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(original));

        Assert.Equal(HaCueProjectFile.Serialize(original), HaCueProjectFile.Serialize(restored));
    }

    [Fact]
    public void LocalWindowLocksRoundTripWithTheOutput()
    {
        var project = new HaCueProject
        {
            VideoOutputs =
            [
                new VideoOutputDefinition
                {
                    Name = "Preview",
                    Fullscreen = false,
                    WindowWidth = 1280,
                    WindowHeight = 720,
                    WindowAspectLocked = true,
                    WindowResolutionLocked = true,
                },
            ],
        };

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));
        var output = Assert.Single(restored.VideoOutputs);
        Assert.True(output.WindowAspectLocked);
        Assert.True(output.WindowResolutionLocked);
    }

    [Fact]
    public void EveryCueKindRoundTripsThroughItsDiscriminator()
    {
        var project = new HaCueProject
        {
            CueLists =
            [
                new CueList
                {
                    Name = "All kinds",
                    Cues =
                    [
                        new MediaCueNode { Number = "1", Label = "media" },
                        new GroupCueNode { Number = "2", Label = "group" },
                        new ActionCueNode { Number = "3", Label = "action" },
                        new AutomationCueNode { Number = "4", Label = "automation" },
                        new FadeCueNode { Number = "5", Label = "fade" },
                        new JumpCueNode { Number = "6", Label = "jump" },
                        new VisualizerCueNode { Number = "7", Label = "visualizer" },
                        new PatchCueNode { Number = "8", Label = "patch" },
                        new CommentCueNode { Number = "9", Label = "comment" },
                        new TextCueNode { Number = "10", Label = "text" },
                    ],
                },
            ],
        };

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));

        Assert.Equal(
            project.CueLists[0].Cues.Select(cue => cue.GetType()),
            restored.CueLists[0].Cues.Select(cue => cue.GetType()));
    }

    /// <summary>
    /// The reason every property in this model uses <c>set</c> rather than <c>init</c>.
    /// </summary>
    /// <remarks>
    /// System.Text.Json's source generator assigns every init-only property in an object initializer,
    /// so one absent from the JSON lands as the CLR default and the property initializer beside it is
    /// lost. For <c>Enabled = true</c> that means a cue deliberately disabled for one performance
    /// coming back on — or, on an older document, every cue arriving disabled. This test fails if
    /// anyone converts these properties back to <c>init</c>.
    /// </remarks>
    [Fact]
    public void ADisabledCueStaysDisabledAcrossASave()
    {
        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Act 1", Cues = [new MediaCueNode { Number = "1", Enabled = false }] }],
        };

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));

        Assert.False(restored.CueLists[0].Cues[0].Enabled);
    }

    [Fact]
    public void ADocumentMissingAFieldKeepsThatFieldsDefault()
    {
        // A minimal document, as an older build would have written it before these fields existed.
        const string json = """
            { "schemaVersion": 1, "title": "old show" }
            """;

        var project = HaCueProjectFile.Deserialize(json);

        Assert.Equal("old show", project.Title);
        Assert.True(project.Settings.RunStatusChecksOnOpen);
        Assert.Equal(48_000, project.AudioPatch.MixSampleRate);
        Assert.Equal(DisabledCueFollow.SkipOnward, project.Settings.DisabledCueFollow);
    }

    /// <summary>
    /// A square warp mesh written by an older build opens as its two-axis equivalent.
    /// </summary>
    /// <remarks>
    /// The mesh was a single <c>warpGrid</c> — always N×N — because the picker only offered off, 3×3
    /// and 5×5. Panels are not square, so the two axes are independent now; a document that predates
    /// that must not open with its warp silently switched off, which is a projector that has quietly
    /// lost its alignment.
    /// </remarks>
    [Fact]
    public void ASquareWarpGridFromAnOlderBuildBecomesATwoAxisMesh()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "title": "old show",
              "videoOutputs": [
                {
                  "name": "Projector",
                  "mapping": [ { "name": "Left wall", "warpGrid": 3 } ]
                }
              ]
            }
            """;

        var section = HaCueProjectFile.Deserialize(json).VideoOutputs[0].Mapping[0];

        Assert.Equal(3, section.MeshColumns);
        Assert.Equal(3, section.MeshRows);

        // And it does not travel back out: the legacy key is read-only, so a document saved by this
        // build carries the two axes and nothing else.
        Assert.DoesNotContain("warpGrid", HaCueProjectFile.Serialize(HaCueProjectFile.Deserialize(json)));
    }

    /// <summary>The two-axis form wins when both are present, whatever order they appear in.</summary>
    [Fact]
    public void AnExplicitMeshIsNotOverwrittenByTheLegacyKey()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "videoOutputs": [
                {
                  "name": "Projector",
                  "mapping": [ { "warpGrid": 5, "meshColumns": 5, "meshRows": 2 } ]
                }
              ]
            }
            """;

        var section = HaCueProjectFile.Deserialize(json).VideoOutputs[0].Mapping[0];

        Assert.Equal(5, section.MeshColumns);
        Assert.Equal(2, section.MeshRows);
    }

    /// <summary>
    /// A mapping section with no <c>enabled</c> key is DRAWN.
    /// </summary>
    /// <remarks>
    /// The flag is new, so every section in every existing show lacks it — and defaulting the other way
    /// would open a warped projector with every panel switched off.
    /// </remarks>
    [Fact]
    public void AMappingSectionWithoutTheEnabledKeyIsDrawn()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "videoOutputs": [ { "name": "Projector", "mapping": [ { "name": "Left" } ] } ]
            }
            """;

        var output = HaCueProjectFile.Deserialize(json).VideoOutputs[0];

        Assert.True(output.Mapping[0].Enabled);
        // And with no raster stated, mapping still resolves against the composition — zero is
        // "follow the canvas", not "an output of no size".
        Assert.Equal(0, output.MappingWidth);
        Assert.Equal(0, output.MappingHeight);
    }

    [Fact]
    public void ANewerSchemaIsRefusedWithAMessageThatSaysWhy()
    {
        var json = HaCueProjectFile.Serialize(new HaCueProject
        {
            SchemaVersion = HaCueProject.CurrentSchemaVersion + 1,
        });

        var error = Assert.Throws<HaCueProjectFormatException>(() => HaCueProjectFile.Deserialize(json));

        Assert.Contains("newer HaCue2", error.Message);
    }

    [Fact]
    public void MalformedJsonReportsAProjectProblemRatherThanAParserOne()
    {
        var error = Assert.Throws<HaCueProjectFormatException>(
            () => HaCueProjectFile.Deserialize("{ not json"));

        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task SavingIsAtomicAndReadsBack()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-core-tests");
        try
        {
            var path = Path.Combine(directory.FullName, "show" + HaCueProjectFile.Extension);
            var project = new TestProject().Project;

            await HaCueProjectFile.SaveAsync(project, path);
            var restored = await HaCueProjectFile.LoadAsync(path);

            Assert.Equal(HaCueProjectFile.Serialize(project), HaCueProjectFile.Serialize(restored));
            // The temp file is the mechanism, not a leftover: finding one means a save was interrupted.
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheHashFollowsTheDocumentAndNothingElse()
    {
        var fixture = new TestProject();
        var before = HaCueProjectFile.ComputeHash(fixture.Project);

        fixture.Track.LevelDb = -3;
        var after = HaCueProjectFile.ComputeHash(fixture.Project);

        Assert.NotEqual(before, after);

        fixture.Track.LevelDb = -6;
        Assert.Equal(before, HaCueProjectFile.ComputeHash(fixture.Project));
    }
}
