using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The rectangle a canvas drag moves: a layer placement, or a mapping section's source and target.
/// </summary>
/// <remarks>
/// What is being pinned here is that a GESTURE — many events, one intent — leaves one undo step, and
/// that a rectangle cannot be dragged off the canvas or down to nothing. Both are properties a
/// compiling binding does not demonstrate, and both are how a placement gets lost with one slip.
/// </remarks>
public sealed class RectEditTests
{
    [Fact]
    public void ADragOfManyEventsLeavesOneUndoStep()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var placement = Placed(fixture);

        using (journal.Composite("move layer", "video"))
        {
            for (var step = 1; step <= 20; step++)
            {
                journal.Do(RectEdits.Placement(
                    fixture.Track, placement, new NormalizedRect(step * 0.01, 0.1, 0.5, 0.5)));
            }
        }

        Assert.Single(journal.Log);
        Assert.Equal(0.20, placement.X, 4);

        Assert.True(journal.Undo());
        Assert.Equal(0.1, placement.X, 4);
        Assert.Equal(0.1, placement.Y, 4);
    }

    [Fact]
    public void TwoSeparateGesturesAreTwoUndoSteps()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var placement = Placed(fixture);

        journal.Do(RectEdits.Placement(fixture.Track, placement, new NormalizedRect(0.2, 0.1, 0.5, 0.5)));
        journal.CloseGroup();
        journal.Do(RectEdits.Placement(fixture.Track, placement, new NormalizedRect(0.3, 0.1, 0.5, 0.5)));
        journal.CloseGroup();

        Assert.Equal(2, journal.Log.Count);

        journal.Undo();
        Assert.Equal(0.2, placement.X, 4);
    }

    [Fact]
    public void ARectangleCannotLeaveTheCanvasOrShrinkToNothing()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var placement = Placed(fixture);

        journal.Do(RectEdits.Placement(fixture.Track, placement, new NormalizedRect(5, -3, 0.5, 0.5)));

        Assert.Equal(0.5, placement.X, 4);
        Assert.Equal(0, placement.Y, 4);

        // A drag that would collapse the box keeps enough of it to grab again — losing a layer to one
        // slip with nothing left to click on is the failure this guards.
        journal.Do(RectEdits.Placement(fixture.Track, placement, new NormalizedRect(0.5, 0.5, 0, 0)));

        Assert.True(placement.Width > 0);
        Assert.True(placement.Height > 0);
        Assert.True(placement.X + placement.Width <= 1.0001);
    }

    [Fact]
    public void ASectionsSourceAndTargetAreSeparateUndoSteps()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var section = Sectioned(fixture);

        journal.Do(RectEdits.MappingSource(section, new NormalizedRect(0.1, 0.1, 0.4, 0.4)));
        journal.Do(RectEdits.MappingTarget(section, new NormalizedRect(0.6, 0.6, 0.3, 0.3)));
        journal.CloseGroup();

        // Same subject, different property: one gesture on the left canvas must not swallow one on the
        // right, or dragging the source then the target would undo as a single unexplainable jump.
        Assert.Equal(2, journal.Log.Count);

        // Undo walks back the target drag and leaves the source one standing.
        journal.Undo();
        Assert.Equal(0, section.TargetX, 4);
        Assert.Equal(0.1, section.SourceX, 4);
    }

    [Fact]
    public void AMoveRevertsToAByteIdenticalDocument()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var placement = Placed(fixture);
        var before = HaCueProjectFile.Serialize(fixture.Project);

        journal.Do(RectEdits.Placement(fixture.Track, placement, new NormalizedRect(0.42, 0.17, 0.3, 0.3)));
        journal.CloseGroup();
        Assert.NotEqual(before, HaCueProjectFile.Serialize(fixture.Project));

        journal.Undo();
        Assert.Equal(before, HaCueProjectFile.Serialize(fixture.Project));
    }

    private static LayerPlacement Placed(TestProject fixture)
    {
        fixture.Track.Placement = new LayerPlacement
        {
            CompositionId = fixture.Cyc.Id,
            LayerIndex = 1,
            X = 0.1,
            Y = 0.1,
            Width = 0.5,
            Height = 0.5,
        };

        return fixture.Track.Placement;
    }

    private static MappingSection Sectioned(TestProject fixture)
    {
        var output = new VideoOutputDefinition
        {
            Name = "Projector A",
            CompositionId = fixture.Cyc.Id,
            Mapping =
            [
                new MappingSection { Name = "Left wall" },
            ],
        };

        fixture.Project.VideoOutputs.Add(output);
        return output.Mapping[0];
    }
}
