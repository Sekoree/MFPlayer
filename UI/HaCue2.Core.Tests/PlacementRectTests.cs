using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The rule that decides where a dragged rectangle is allowed to end up.
/// </summary>
/// <remarks>
/// Two different rules, and conflating them broke the commonest gesture in the app. A mapping's source
/// and target are regions OF something and stay inside it; a layer placement is a picture positioned ON
/// a canvas and may hang off the edge.
/// </remarks>
public sealed class PlacementRectTests
{
    [Fact]
    public void AFullCanvasPlacementCanBeMoved()
    {
        var cue = new MediaCueNode();
        var placement = new LayerPlacement { X = 0, Y = 0, Width = 1, Height = 1 };
        cue.Placements.Add(placement);

        RectEdits.Placement(cue, placement, new NormalizedRect(-0.25, 0.1, 1, 1)).Apply(new HaCueProject());

        // Clamped() pins X to 1 − width, which for a full-canvas layer is 0 — so the placement every
        // show starts with could not be dragged at all, in any direction.
        Assert.Equal(-0.25, placement.X, 6);
        Assert.Equal(0.1, placement.Y, 6);
    }

    [Fact]
    public void APlacementMayHangOffTheCanvas()
    {
        var rect = new NormalizedRect(-0.4, 0.8, 0.5, 0.5).Free();

        Assert.Equal(-0.4, rect.X, 6);
        Assert.Equal(0.8, rect.Y, 6);
        Assert.Equal(0.5, rect.Width, 6);
    }

    [Fact]
    public void APlacementMayBeLargerThanTheCanvas()
    {
        var rect = new NormalizedRect(0, 0, 2.5, 1.75).Free();

        Assert.Equal(2.5, rect.Width, 6);
        Assert.Equal(1.75, rect.Height, 6);
    }

    [Fact]
    public void FreeIsStillBounded()
    {
        var rect = new NormalizedRect(-500, 900, 0.5, 0.5).Free();

        // A slip that threw a layer a thousand canvases away would leave nothing on screen to drag
        // back — the same trap the minimum size exists to avoid.
        Assert.True(rect.X >= -NormalizedRect.FreeReach, $"X was {rect.X}");
        Assert.True(rect.Y <= 1 + NormalizedRect.FreeReach, $"Y was {rect.Y}");
    }

    [Fact]
    public void FreeStillRefusesToVanish()
    {
        var rect = new NormalizedRect(0.5, 0.5, 0, -3).Free();

        Assert.True(rect.Width > 0, "a layer dragged to zero width would have nothing left to grab");
        Assert.True(rect.Height > 0, "a layer dragged to zero height would have nothing left to grab");
    }

    [Fact]
    public void AMappingRegionStaysInsideItsFrame()
    {
        var section = new MappingSection();

        RectEdits.MappingSource(section, new NormalizedRect(-0.5, -0.5, 0.4, 0.4))
            .Apply(new HaCueProject());

        // The opposite rule, deliberately: a source region outside the source is a crop of nothing.
        Assert.Equal(0, section.SourceX, 6);
        Assert.Equal(0, section.SourceY, 6);
    }

    [Fact]
    public void AMappingTargetStaysInsideItsOutput()
    {
        var section = new MappingSection();

        RectEdits.MappingTarget(section, new NormalizedRect(0.9, 0.9, 0.5, 0.5))
            .Apply(new HaCueProject());

        Assert.True(section.TargetX + section.TargetWidth <= 1.0001, "the target left the output raster");
        Assert.True(section.TargetY + section.TargetHeight <= 1.0001, "the target left the output raster");
    }

    [Fact]
    public void MappingEditorRegionsMayOverhangWithoutChangingSize()
    {
        var section = new MappingSection();

        RectEdits.MappingSource(
                section, new NormalizedRect(-0.25, 0.2, 0.4, 0.3), allowOutsideFrame: true)
            .Apply(new HaCueProject());
        RectEdits.MappingTarget(
                section, new NormalizedRect(0.9, -0.2, 0.5, 0.4), allowOutsideFrame: true)
            .Apply(new HaCueProject());

        Assert.Equal(-0.25, section.SourceX, 6);
        Assert.Equal(0.4, section.SourceWidth, 6);
        Assert.Equal(0.9, section.TargetX, 6);
        Assert.Equal(-0.2, section.TargetY, 6);
        Assert.Equal(0.5, section.TargetWidth, 6);
        Assert.Equal(0.4, section.TargetHeight, 6);
    }

    [Fact]
    public void AnOutOfFramePlacementIsNotAValidationError()
    {
        var fixture = new TestProject();
        var cue = new MediaCueNode
        {
            Number = "99",
            Label = "Lower third",
            MediaPath = "x.mp4",
            Placements =
            [
                new LayerPlacement
                {
                    CompositionId = fixture.Cyc.Id,
                    X = -0.3,
                    Y = 0.85,
                    Width = 1,
                    Height = 0.4,
                },
            ],
        };
        fixture.List.Cues.Add(cue);

        var issues = ProjectValidator.Validate(fixture.Project);

        // Bleeding a caption off the bottom of the frame is authoring, not a fault.
        Assert.DoesNotContain(
            issues,
            issue => issue.Severity == ShowValidationSeverity.Error
                     && issue.Message.Contains("placement", StringComparison.OrdinalIgnoreCase));
    }
}
