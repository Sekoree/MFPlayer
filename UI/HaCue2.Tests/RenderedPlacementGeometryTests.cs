using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Presentation;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>The placement canvas outlines pixels that are painted, not merely their fitting area.</summary>
public class RenderedPlacementGeometryTests
{
    [Fact]
    public void AContainedSquareCoverIsDrawnSquareOnAWideComposition()
    {
        var composition = new CompositionDefinition { Name = "Main", Width = 1920, Height = 1080 };
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Cover",
            Placements = [new LayerPlacement { CompositionId = composition.Id, Fit = LayerFit.Contain }],
        };
        var project = new HaCueProject
        {
            Compositions = [composition],
            CueLists = [new CueList { Name = "Cues", Cues = [cue] }],
        };
        var facts = new MediaFacts
        {
            VideoTracks =
            [
                new MediaTrack(0, "768×768 cover", "cover", null, 0, 768, 768,
                    IsDefault: true, IsAttachedPicture: true, IsDecodable: true),
            ],
        };

        var box = Assert.Single(VideoPresentation.Layers(project, composition, cue.Id, _ => facts));

        Assert.Equal(box.Width * composition.Width, box.Height * composition.Height, 5);
        Assert.Equal(1080, box.Height * composition.Height, 5);
        Assert.Equal(new NormalizedRect(0, 0, 1, 1), box.AuthoredRect);
    }

    [Fact]
    public void TransparentTextIsOnlyAsLargeAsItsRenderedInk()
    {
        var composition = new CompositionDefinition { Name = "Main", Width = 1920, Height = 1080 };
        var cue = new TextCueNode
        {
            Number = "2",
            Label = "Title",
            Text = "Hello",
            FontScale = .12,
            Placements = [new LayerPlacement { CompositionId = composition.Id, Fit = LayerFit.Contain }],
        };
        var project = new HaCueProject
        {
            Compositions = [composition],
            CueLists = [new CueList { Name = "Cues", Cues = [cue] }],
        };

        var box = Assert.Single(VideoPresentation.Layers(project, composition, cue.Id));

        Assert.InRange(box.Width, .01, .5);
        Assert.InRange(box.Height, .01, .3);
        Assert.NotNull(box.AuthoredRect);
    }

    [Fact]
    public void MovingRenderedBoundsMovesTheAuthoredDestinationByTheSameDistance()
    {
        var display = new NormalizedRect(.25, 0, .5, 1);
        var authored = new NormalizedRect(0, 0, 1, 1);
        var gesture = new PlacementGesture(
            0,
            Guid.NewGuid(),
            0,
            new NormalizedRect(.35, .1, .5, 1),
            display,
            authored);

        var result = gesture.AuthoredRect();
        Assert.Equal(.1, result.X, 6);
        Assert.Equal(.1, result.Y, 6);
        Assert.Equal(1, result.Width, 6);
        Assert.Equal(1, result.Height, 6);
    }

    [Fact]
    public Task ABoundRenderedBoxFollowsSeveralPointerMovesDuringOneDrag() =>
        ShellFixture.WithShell(shell =>
        {
            var composition = shell.Project.Compositions.First();
            var cue = ShellFixture.Bed(shell.Project);
            cue.Placements.Clear();
            cue.Placements.Add(new LayerPlacement
            {
                CompositionId = composition.Id,
                LayerIndex = 999,
                Fit = LayerFit.Contain,
            });
            shell.Cues.Inspector.MediaFacts = _ => new MediaFacts
            {
                VideoTracks =
                [
                    new MediaTrack(0, "square cover", "cover", null, 0, 768, 768,
                        IsDefault: true, IsAttachedPicture: true, IsDecodable: true),
                ],
            };
            ShellFixture.Select(shell.Cues, cue.Id);
            shell.Cues.Refresh();

            var window = new PlacementEditorWindow
            {
                Width = 900,
                Height = 700,
                DataContext = shell.Cues.Inspector,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                var canvas = window.GetVisualDescendants().OfType<PlacementCanvas>().Single();
                canvas.SnapEnabled = false;
                PlacementGesture? seen = null;
                canvas.Gesture += (_, gesture) => seen = gesture;
                var surface = canvas.GetVisualDescendants().OfType<FractionPanel>().Single();
                var origin = surface.TranslatePoint(default, window)!.Value;
                var box = Assert.Single(canvas.Boxes, item => item.SubjectId == cue.Id);
                var start = new Point(
                    origin.X + ((box.Left + (box.Width / 2)) * surface.Bounds.Width),
                    origin.Y + ((box.Top + (box.Height / 2)) * surface.Bounds.Height));

                window.MouseDown(start, MouseButton.Left);
                for (var step = 1; step <= 3; step++)
                {
                    window.MouseMove(new Point(
                        start.X + (step * .05 * surface.Bounds.Width),
                        start.Y + (step * .04 * surface.Bounds.Height)));
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.NotNull(seen);
                Assert.Equal(.15, cue.Placements[0].X, 5);
                Assert.Equal(.12, cue.Placements[0].Y, 5);
                Assert.Equal(.15,
                    Assert.Single(canvas.Boxes, item => item.SubjectId == cue.Id).AuthoredRect!.Value.X, 5);

                window.MouseUp(new Point(
                    start.X + (.15 * surface.Bounds.Width),
                    start.Y + (.12 * surface.Bounds.Height)), MouseButton.Left);
            }
            finally
            {
                window.Close();
            }
        });
}
