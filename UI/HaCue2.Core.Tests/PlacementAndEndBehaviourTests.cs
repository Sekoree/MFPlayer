using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The placement and end-behaviour fields the document could not express.
/// </summary>
/// <remarks>
/// Every one of these existed on the framework's own spec and was never filled, so the compiler is
/// where they are worth pinning: a field the document carries and the compiler drops is exactly as
/// useless as one that was never added, and looks finished from the inspector.
/// </remarks>
public class PlacementAndEndBehaviourTests
{
    private static HaCueProject WithPlacement(LayerPlacement placement, out MediaCueNode cue)
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        placement.CompositionId = composition.Id;

        cue = new MediaCueNode
        {
            Number = "1",
            Label = "Clip",
            MediaPath = "/media/clip.mp4",
            Placements = [placement],
        };

        var media = cue;

        return new HaCueProject
        {
            Compositions = [composition],
            CueLists = [new CueList { Name = "Main", Cues = [media] }],
        };
    }

    private static ShowClipBinding Compile(HaCueProject project) =>
        Assert.Single(ShowCompiler.Compile(project, new ShowCompileContext()).Clips);

    [Fact]
    public void CropAndRotationReachTheEngine()
    {
        var project = WithPlacement(
            new LayerPlacement
            {
                CropLeft = 0.1, CropTop = 0.05, CropRight = 0.1, CropBottom = 0.05,
                RotationDegrees = 90,
            },
            out _);

        var placement = Compile(project).Placement;

        Assert.NotNull(placement);
        Assert.Equal(0.1, placement.CropLeft, 6);
        Assert.Equal(0.05, placement.CropTop, 6);
        Assert.Equal(90, placement.RotationDegrees, 6);
    }

    /// <summary>Opposite crops can never meet and erase the picture entirely.</summary>
    [Fact]
    public void ACropIsClampedShortOfTheWholePicture()
    {
        var project = WithPlacement(new LayerPlacement { CropLeft = 5, CropRight = 5 }, out _);

        var placement = Compile(project).Placement;

        Assert.True(placement!.CropLeft < 0.5);
        Assert.True(placement.CropLeft + placement.CropRight < 1);
    }

    [Theory]
    [InlineData(LayerFit.Contain, "Contain")]
    [InlineData(LayerFit.Cover, "Cover")]
    [InlineData(LayerFit.Stretch, "Stretch")]
    [InlineData(LayerFit.Center, "Center")]
    [InlineData(LayerFit.FillWidth, "FillWidth")]
    [InlineData(LayerFit.FillHeight, "FillHeight")]
    public void EveryFitModeHasAName(LayerFit fit, string expected)
    {
        // The framework maps these BY NAME, so a fit that compiled to the wrong string would silently
        // fall through to Cover - a picture in the wrong shape and no error anywhere.
        var project = WithPlacement(new LayerPlacement { Fit = fit }, out _);

        Assert.Equal(expected, Compile(project).Placement!.Fit);
    }

    [Fact]
    public void ADisabledChromaKeyKeepsItsSettingsAndSendsNothing()
    {
        var project = WithPlacement(
            new LayerPlacement
            {
                ChromaKey = new ChromaKeySpec { Similarity = 0.6 },
                ChromaKeyEnabled = false,
            },
            out var cue);

        Assert.Null(Compile(project).Placement!.ChromaKey);

        // Off is not delete: the settings survive so switching it back on costs nothing.
        Assert.Equal(0.6, cue.Placements[0].ChromaKey!.Similarity, 6);
    }

    [Fact]
    public void AnEnabledChromaKeyReachesTheEngine()
    {
        var project = WithPlacement(
            new LayerPlacement
            {
                ChromaKey = new ChromaKeySpec { Red = 0, Green = 1, Blue = 0, Similarity = 0.45 },
            },
            out _);

        var key = Compile(project).Placement!.ChromaKey;

        Assert.NotNull(key);
        Assert.Equal(0.45f, key.Value.Similarity, 4);
    }

    [Fact]
    public void ALayerMappingWithEverySectionOffIsNoMapping()
    {
        // NOT the same as a mapping with nothing in it, which would render the layer black.
        var project = WithPlacement(
            new LayerPlacement
            {
                VideoFx = [new MappingSection { Name = "Left", Enabled = false }],
            },
            out _);

        Assert.Null(Compile(project).Placement!.VideoFx);
    }

    [Fact]
    public void ALayerMappingReachesTheEngine()
    {
        var project = WithPlacement(
            new LayerPlacement
            {
                VideoFx = [new MappingSection { Name = "Left", SourceWidth = 0.5, TargetWidth = 0.5 }],
            },
            out _);

        var mapping = Compile(project).Placement!.VideoFx;

        Assert.NotNull(mapping);
        Assert.Equal(0.5, Assert.Single(mapping.Sections).SrcWidth, 6);
    }

    // ── end behaviour ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EndBehaviourReachesTheEngine()
    {
        var composition = new CompositionDefinition { Name = "Cyc" };
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Card",
            MediaPath = "/media/card.png",
            EndBehavior = CueEndBehavior.FreezeLastFrame,
        };

        var project = new HaCueProject
        {
            Compositions = [composition],
            CueLists = [new CueList { Name = "Main", Cues = [cue] }],
        };

        Assert.Equal(ClipEndBehavior.FreezeLastFrame, Compile(project).EndBehavior);
    }

    /// <summary>The old flag still loops, so a document that predates the enum keeps working.</summary>
    [Fact]
    public void TheLoopFlagAloneStillLoops()
    {
        var cue = new MediaCueNode
        {
            Number = "1", Label = "Bed", MediaPath = "/media/bed.wav", Loop = true,
        };

        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Main", Cues = [cue] }],
        };

        var binding = Compile(project);

        Assert.True(binding.Loop);
        Assert.Equal(ClipEndBehavior.Loop, binding.EndBehavior);
    }

    [Fact]
    public void ALoopCrossfadeReachesTheEngine()
    {
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Bed",
            MediaPath = "/media/bed.wav",
            EndBehavior = CueEndBehavior.Loop,
            LoopCrossfadeMs = 1_500,
        };

        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Main", Cues = [cue] }],
        };

        var binding = Compile(project);

        Assert.True(binding.Loop);
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), binding.LoopCrossfade);
    }
}
