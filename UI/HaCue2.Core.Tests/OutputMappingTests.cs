using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Converting a video output's authored mapping into the engine's spec.
/// </summary>
/// <remarks>
/// The document stores destination geometry in FRACTIONS and warp points as OFFSETS from an even
/// grid; the engine wants output pixels and absolute positions. Getting either wrong produces a
/// picture that is subtly in the wrong place — which on a projector is discovered at a get-in, by
/// eye, with no error anywhere.
/// </remarks>
public class OutputMappingTests
{
    private static VideoOutputDefinition Output(params MappingSection[] sections) =>
        new() { Name = "Projector", Mapping = [.. sections] };

    [Fact]
    public void AnUnmappedOutputHasNoSpec()
    {
        // Null, not an empty section list: "no mapping" presents the canvas whole, while "mapped with
        // nothing in it" renders black and looks exactly like a dead output.
        Assert.Null(OutputMapping.Spec(Output(), 1920, 1080));
    }

    [Fact]
    public void DestinationFractionsBecomeOutputPixels()
    {
        var section = new MappingSection
        {
            TargetX = 0.5, TargetY = 0.25, TargetWidth = 0.5, TargetHeight = 0.5,
        };

        var spec = OutputMapping.Spec(Output(section), 1920, 1080);

        var resolved = Assert.Single(spec!.Sections);
        Assert.Equal(960, resolved.DestX);
        Assert.Equal(270, resolved.DestY);
        Assert.Equal(960, resolved.DestWidth);
        Assert.Equal(540, resolved.DestHeight);
    }

    [Fact]
    public void SourceGeometryStaysNormalized()
    {
        var section = new MappingSection
        {
            SourceX = 0.25, SourceY = 0, SourceWidth = 0.5, SourceHeight = 1,
        };

        var resolved = Assert.Single(OutputMapping.Spec(Output(section), 1920, 1080)!.Sections);

        // The SOURCE is a slice of the canvas and stays in canvas fractions — only the destination is
        // measured in the output's pixels.
        Assert.Equal(0.25, resolved.SrcX);
        Assert.Equal(0.5, resolved.SrcWidth);
    }

    [Fact]
    public void AGridBelowTwoIsNotAMesh()
    {
        foreach (var grid in new[] { 0, 1 })
        {
            var section = new MappingSection { WarpGrid = grid };
            var resolved = Assert.Single(OutputMapping.Spec(Output(section), 1920, 1080)!.Sections);

            // The affine path, which every backend supports. The warp path is GL-only.
            Assert.Equal(0, resolved.MeshColumns);
            Assert.Null(resolved.MeshPoints);
        }
    }

    [Fact]
    public void AGridWithoutItsOffsetsIsNotAMesh()
    {
        // WarpOffsets is empty until the mesh is touched. Emitting a mesh here would hand the resolver
        // a point list of the wrong length, which is a crash rather than a wrong picture.
        var section = new MappingSection { WarpGrid = 3 };
        var resolved = Assert.Single(OutputMapping.Spec(Output(section), 1920, 1080)!.Sections);

        Assert.Equal(0, resolved.MeshColumns);
    }

    [Fact]
    public void AnUntouchedMeshResolvesToTheEvenGrid()
    {
        // 3×3 with zero offsets: the control points are the even grid itself, corners at 0 and 1.
        var section = new MappingSection { WarpGrid = 3, WarpOffsets = [.. new double[18]] };

        var resolved = Assert.Single(OutputMapping.Spec(Output(section), 1920, 1080)!.Sections);

        Assert.Equal(3, resolved.MeshColumns);
        Assert.Equal(3, resolved.MeshRows);
        Assert.Equal(9, resolved.MeshPoints!.Count);

        Assert.Equal(0, resolved.MeshPoints[0].X, 6);
        Assert.Equal(0, resolved.MeshPoints[0].Y, 6);
        Assert.Equal(0.5, resolved.MeshPoints[4].X, 6);
        Assert.Equal(0.5, resolved.MeshPoints[4].Y, 6);
        Assert.Equal(1, resolved.MeshPoints[8].X, 6);
        Assert.Equal(1, resolved.MeshPoints[8].Y, 6);
    }

    [Fact]
    public void MeshOffsetsAreAddedToTheEvenGrid()
    {
        var offsets = new double[18];
        offsets[0] = 0.1;   // top-left, x
        offsets[1] = -0.05; // top-left, y

        var section = new MappingSection { WarpGrid = 3, WarpOffsets = [.. offsets] };
        var resolved = Assert.Single(OutputMapping.Spec(Output(section), 1920, 1080)!.Sections);

        // Stored as an offset so the warp travels with the section when it is moved or resized; the
        // even grid is added back exactly once, here.
        Assert.Equal(0.1, resolved.MeshPoints![0].X, 6);
        Assert.Equal(-0.05, resolved.MeshPoints[0].Y, 6);
    }

    [Fact]
    public void OpacityAndBrightnessAreClamped()
    {
        var section = new MappingSection { Opacity = 2.5, Brightness = -1 };
        var resolved = Assert.Single(OutputMapping.Spec(Output(section), 1920, 1080)!.Sections);

        Assert.Equal(1, resolved.Opacity);
        Assert.Equal(0, resolved.Brightness);
    }

    [Fact]
    public void AnOutputWithNoSizeYetHasNoSpec()
    {
        // A window that has not reported its size cannot have fractions resolved against it, and a
        // spec built against zero would place every section at the origin with no width.
        var section = new MappingSection { TargetWidth = 1, TargetHeight = 1 };

        Assert.Null(OutputMapping.Spec(Output(section), 0, 0));
    }
}
