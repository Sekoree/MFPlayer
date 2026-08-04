using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// An output that shows no composition yet.
/// </summary>
/// <remarks>
/// Outputs are created UNBOUND — they are pieces of this machine, and exist before any canvas is
/// authored against them. That made the first thing an operator does produce nothing they could see:
/// the output was skipped entirely by the open pass, so adding a local screen put a row in a table and
/// no window anywhere, with no error to explain it.
/// </remarks>
public class UnassignedOutputTests
{
    private static HaCueProject WithLocalScreen(out VideoOutputDefinition output)
    {
        output = new VideoOutputDefinition
        {
            Name = "Projector",
            Kind = VideoOutputKind.LocalScreen,
            Fullscreen = false,
            WindowWidth = 640,
            WindowHeight = 360,
        };

        return new HaCueProject { VideoOutputs = [output] };
    }

    /// <summary>
    /// A local screen with no composition is not reported as a failure to open.
    /// </summary>
    /// <remarks>
    /// Headless here, so no SDL window is created — what this pins is the DECISION: "shows no
    /// composition" must stop being a reason a local screen is skipped. On a machine with a display the
    /// same branch opens the window and the host paints it black.
    /// </remarks>
    [Fact]
    public void ALocalScreenWithNoCompositionIsNotAnOpenFailure()
    {
        var project = WithLocalScreen(out var output);

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        // Headless reports it unopened for the honest reason — no display — rather than for the wrong
        // one. What matters is that a machine WITH a display would take the window branch.
        Assert.Contains(outputs.Failures, failure => failure.Contains("no display", StringComparison.Ordinal));
        Assert.DoesNotContain(
            outputs.Failures, failure => failure.Contains("shows no composition", StringComparison.Ordinal));
    }

    /// <summary>An NDI sender with nothing to send stays closed, and says so.</summary>
    /// <remarks>
    /// The asymmetry is deliberate: a window is something an operator has to SEE to know where it
    /// landed, while an NDI source with no canvas is a name on the network carrying black and a
    /// recorder with no canvas is a file of it.
    /// </remarks>
    [Fact]
    public void ASenderWithNoCompositionStaysClosed()
    {
        var project = new HaCueProject
        {
            VideoOutputs = [new VideoOutputDefinition { Name = "NDI program", Kind = VideoOutputKind.Ndi }],
        };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        Assert.Contains(
            outputs.Failures, failure => failure.Contains("shows no composition", StringComparison.Ordinal));
        Assert.Single(outputs.Unopened);
    }

    /// <summary>
    /// Assigning a composition to an output that shows none attaches it on the next sync.
    /// </summary>
    /// <remarks>
    /// The exact sequence the new Video tab asks an operator to perform: make the output, then send a
    /// canvas to it.
    /// </remarks>
    [Fact]
    public void AssigningACompositionAttachesTheOutput()
    {
        var canvas = new CompositionDefinition { Name = "Cyc", Width = 320, Height = 240 };
        var project = new HaCueProject
        {
            Compositions = [canvas],
            VideoOutputs =
            [
                new VideoOutputDefinition { Name = "Recorder", Kind = VideoOutputKind.Record },
            ],
        };

        // A recorder, because it opens headless and so exercises the wiring without a display.
        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        Assert.Empty(outputs.Leases(project));

        project.VideoOutputs[0].CompositionId = canvas.Id;
        outputs.Sync(project);

        Assert.Single(outputs.Leases(project));
    }

    /// <summary>
    /// Moving an ALREADY-OPEN output from one canvas to another releases the first.
    /// </summary>
    /// <remarks>
    /// The open record kept the canvas it was created with, and the sync pass skipped anything already
    /// open — so re-pointing a live projector at a different composition did nothing, and the host went
    /// on believing it was attached where it used to be.
    /// </remarks>
    [Fact]
    public void MovingAnOpenOutputBetweenCompositionsRetargetsIt()
    {
        var cyc = new CompositionDefinition { Name = "Cyc", Width = 320, Height = 240 };
        var portal = new CompositionDefinition { Name = "Portal", Width = 640, Height = 360 };
        var project = new HaCueProject
        {
            Compositions = [cyc, portal],
            VideoOutputs =
            [
                new VideoOutputDefinition
                {
                    Name = "Recorder", Kind = VideoOutputKind.Record, CompositionId = cyc.Id,
                },
            ],
        };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);
        Assert.Equal(cyc.Id.ToString(), Assert.Single(outputs.Leases(project)).CompositionId);

        project.VideoOutputs[0].CompositionId = portal.Id;
        outputs.Sync(project);

        var moved = Assert.Single(outputs.Retargeted);
        Assert.Equal(cyc.Id, moved.From);
        Assert.Equal(portal.Id, moved.To);
        Assert.Equal(portal.Id.ToString(), Assert.Single(outputs.Leases(project)).CompositionId);
    }

    /// <summary>Taking the composition away again releases the attachment rather than freezing a frame.</summary>
    [Fact]
    public void UnassigningReleasesTheAttachment()
    {
        var canvas = new CompositionDefinition { Name = "Cyc", Width = 320, Height = 240 };
        var project = new HaCueProject
        {
            Compositions = [canvas],
            VideoOutputs =
            [
                new VideoOutputDefinition
                {
                    Name = "Recorder", Kind = VideoOutputKind.Record, CompositionId = canvas.Id,
                },
            ],
        };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);
        Assert.Single(outputs.Leases(project));

        project.VideoOutputs[0].CompositionId = null;
        outputs.Sync(project);

        var moved = Assert.Single(outputs.Retargeted);
        Assert.Equal(canvas.Id, moved.From);
        Assert.Null(moved.To);
        Assert.Empty(outputs.Leases(project));
    }

    /// <summary>A composition that is deleted out from under an output counts as unassigning it.</summary>
    [Fact]
    public void ACompositionThatNoLongerExistsIsTreatedAsNone()
    {
        var canvas = new CompositionDefinition { Name = "Cyc", Width = 320, Height = 240 };
        var project = new HaCueProject
        {
            Compositions = [canvas],
            VideoOutputs =
            [
                new VideoOutputDefinition
                {
                    Name = "Recorder", Kind = VideoOutputKind.Record, CompositionId = canvas.Id,
                },
            ],
        };

        using var outputs = ProjectVideoOutputs.OpenAll(project, headless: true);

        project.Compositions.Clear();
        outputs.Sync(project);

        Assert.Empty(outputs.Leases(project));
    }
}
