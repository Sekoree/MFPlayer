using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The HaPlay-parity visualizer options (2026-08-14): shuffle, beat sensitivity and the projectM
/// render override travel with the show, and a legacy document without them loads the same
/// defaults HaPlay's cue player uses.
/// </summary>
public sealed class VisualizerCueOptionTests
{
    [Fact]
    public void TheParityOptionsRoundTrip()
    {
        var project = new HaCueProject { Title = "Viz" };
        project.CueLists.Add(new CueList { Name = "Main" });
        project.CueLists[0].Cues.Add(new VisualizerCueNode
        {
            Number = "1",
            Label = "viz",
            ShufflePresets = false,
            BeatSensitivity = 2.5,
            RenderWidth = 1280,
            RenderHeight = 720,
            RenderFps = 30,
        });

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));
        var cue = Assert.IsType<VisualizerCueNode>(restored.CueLists[0].Cues[0]);

        Assert.False(cue.ShufflePresets);
        Assert.Equal(2.5, cue.BeatSensitivity);
        Assert.Equal((1280, 720, 30), (cue.RenderWidth, cue.RenderHeight, cue.RenderFps));
    }

    [Fact]
    public void ALegacyDocumentLoadsHaPlaysDefaults()
    {
        // A pre-parity file carries none of the new fields; the cue must behave as before the
        // options existed: shuffled advance, library-default sensitivity, follow-the-composition.
        var project = new HaCueProject { Title = "Viz" };
        project.CueLists.Add(new CueList { Name = "Main" });
        project.CueLists[0].Cues.Add(new VisualizerCueNode { Number = "1", Label = "viz" });

        var stripped = HaCueProjectFile.Serialize(project)
            .Replace("\"shufflePresets\": false,", "")
            .Replace("\"shufflePresets\": true,", "")
            .Replace("\"beatSensitivity\": 1,", "");
        var cue = Assert.IsType<VisualizerCueNode>(
            HaCueProjectFile.Deserialize(stripped).CueLists[0].Cues[0]);

        Assert.True(cue.ShufflePresets);
        Assert.Equal(1, cue.BeatSensitivity);
        Assert.Equal((0, 0, 0), (cue.RenderWidth, cue.RenderHeight, cue.RenderFps));
    }
}
