using System.Text.Json;
using HaPlay.Models;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Pins the serialized-model rule from the FadeCueNode doc note across the whole project schema:
/// the source-generated serializer assigns EVERY <c>init</c> property through one object
/// initializer, so a field absent from the JSON loads as the CLR default and silently discards
/// the C# property initializer. Every non-CLR-default default therefore uses <c>set</c>.
/// These tests feed minimal JSON (fields absent, as an older schema or a hand-edited file would)
/// and assert the authored defaults survive - non-null collections, non-empty ids, non-zero
/// scales - through the real <see cref="HaPlayProjectJsonContext"/> path.
/// </summary>
public sealed class SerializedModelDefaultsTests
{
    [Fact]
    public void MinimalProjectJson_LoadsAuthoredDefaults()
    {
        var project = JsonSerializer.Deserialize("{}", HaPlayProjectJsonContext.Default.HaPlayProject)!;

        Assert.Equal(HaPlayProject.CurrentSchemaVersion, project.SchemaVersion);
        Assert.NotNull(project.Outputs);
        Assert.NotNull(project.Players);
        Assert.NotNull(project.ActionEndpoints);
        Assert.NotNull(project.CueLists);
        Assert.NotNull(project.Soundboards);
        Assert.NotNull(project.ControlGraphs);
        Assert.NotNull(project.ControlSystem);
    }

    [Fact]
    public void MinimalNestedRecords_LoadAuthoredDefaults()
    {
        const string json = """
            {
              "players": [ { "playlistTabs": [ {} ] } ],
              "soundboards": [ {} ],
              "controlGraphs": [ {} ],
              "cueLists": [ { "nodes": [ { "kind": "media", "number": "1", "label": "Song" } ] } ]
            }
            """;
        var project = JsonSerializer.Deserialize(json, HaPlayProjectJsonContext.Default.HaPlayProject)!;

        var player = Assert.Single(project.Players);
        Assert.NotNull(player.PlaylistTabs);
        Assert.False(string.IsNullOrEmpty(player.Name));

        var soundboard = Assert.Single(project.Soundboards);
        Assert.NotEqual(Guid.Empty, soundboard.Id);
        Assert.True(soundboard.Rows > 0);
        Assert.True(soundboard.Columns > 0);

        var graph = Assert.Single(project.ControlGraphs);
        Assert.NotEqual(Guid.Empty, graph.Id);
        Assert.Equal(1.0, graph.Zoom);
        Assert.NotNull(graph.Nodes);
        Assert.NotNull(graph.Connections);

        var list = Assert.Single(project.CueLists);
        Assert.NotNull(list.Nodes);
        Assert.NotNull(list.Compositions);
        Assert.NotNull(list.VideoOutputs);
        var node = Assert.Single(list.Nodes);
        Assert.NotEqual(Guid.Empty, node.Id);
    }

    [Fact]
    public void MinimalCueListJson_CompositionKeepsCanvasDefaults()
    {
        const string json = """
            { "compositions": [ {} ] }
            """;
        var list = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var composition = Assert.Single(list.Compositions);
        Assert.NotEqual(Guid.Empty, composition.Id);
        Assert.True(composition.Width > 0);
        Assert.True(composition.Height > 0);
        Assert.True(composition.FrameRateNum > 0);
        Assert.True(composition.FrameRateDen > 0);
    }
}
