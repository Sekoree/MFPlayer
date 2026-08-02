using System.Text.Json;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class ProjectFileTests
{
    [Fact]
    public void AProjectRoundTripsThroughJson()
    {
        var original = new TestProject().Project;

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(original));

        Assert.Equal(HaCueProjectFile.Serialize(original), HaCueProjectFile.Serialize(restored));
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
                        new FadeCueNode { Number = "4", Label = "fade" },
                        new JumpCueNode { Number = "5", Label = "jump" },
                        new VisualizerCueNode { Number = "6", Label = "visualizer" },
                        new PatchCueNode { Number = "7", Label = "patch" },
                        new CommentCueNode { Number = "8", Label = "comment" },
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
