using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.Models;
using HaPlay.ViewModels;
using HaPlay.Views.Controls;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

/// <summary>
/// Drags the placement editor with real pointer input, which is what was missing: every earlier test covered
/// the snap/clamp MATH (<see cref="PlacementSnapMathTests"/>) or the view model, and a control-level defect
/// therefore stayed invisible. Reported twice from a live project - "moving it down and right just resizes it,
/// moving up or left does nothing" - so these tests assert the two things the operator was actually watching:
/// a move changes POSITION ONLY, and it reaches negative coordinates.
/// </summary>
public sealed class CompositionPlacementCanvasDragTests(ITestOutputHelper output)
{
    private const double CanvasW = 640;
    private const double CanvasH = 400;

    private sealed record Rig(
        Window Window, CompositionPlacementCanvas Canvas, CueVideoPlacementViewModel Placement);

    /// <summary>One placement on a 16:9 editor. Sized well inside the composition so there is plenty of room
    /// to drag in every direction, and so the resize handle is nowhere near the middle where we grab it.</summary>
    private static Rig Build(double x = 0.25, double y = 0.25, double w = 0.5, double h = 0.5, bool snap = true)
    {
        HeadlessAppTheme.ApplyProductionBaseTheme();
        var placement = new CueVideoPlacementViewModel { CompositionId = Guid.NewGuid(), LayerIndex = 0 };
        placement.SetDestRect(x, y, w, h);
        var canvas = new CompositionPlacementCanvas
        {
            Placements = new ObservableCollection<CueVideoPlacementViewModel> { placement },
            SelectedPlacement = placement,
            AspectRatio = 16.0 / 9.0,
            SnapToEdges = snap,
        };
        var window = new Window { Content = canvas, Width = CanvasW, Height = CanvasH };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame(); // a real Render pass, so the drawing code is exercised too
        return new Rig(window, canvas, placement);
    }

    /// <summary>Pointer position, in window coordinates, of a normalized point inside the composition.</summary>
    private static Point At(Rig rig, double nx, double ny)
    {
        var canvasRect = InvokeCanvasRect(rig.Canvas);
        var offset = rig.Canvas.TranslatePoint(default, rig.Window) ?? default;
        return new Point(
            canvasRect.X + nx * canvasRect.Width + offset.X,
            canvasRect.Y + ny * canvasRect.Height + offset.Y);
    }

    /// <summary>The control's own composition rectangle - private, and the test must measure against exactly
    /// the same rectangle the drag math uses or it would be asserting against its own guess.</summary>
    private static Rect InvokeCanvasRect(CompositionPlacementCanvas canvas) =>
        (Rect)typeof(CompositionPlacementCanvas)
            .GetMethod("CanvasRect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(canvas, null)!;

    private static void Drag(Rig rig, Point from, Point to)
    {
        rig.Window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        // A couple of intermediate moves: the control accumulates from the grab offset each time, so a single
        // jump could hide an error that only shows once several moves have been applied.
        rig.Window.MouseMove(from + (to - from) * 0.5);
        Dispatcher.UIThread.RunJobs();
        rig.Window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
        rig.Window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    // right and down - the direction reported as "just resizes it"
    [InlineData(0.45, 0.45)]
    // left and up - the direction reported as "does nothing"
    [InlineData(-0.45, -0.45)]
    public async Task DraggingTheBody_MovesOnly_AndNeverResizes(double dx, double dy)
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CompositionPlacementCanvasDragTests).Assembly)
            .Dispatch(() =>
            {
                // Snapping off: this test is about the move itself, and a guide could legitimately absorb
                // part of the travel. Snapping has its own tests.
                var rig = Build(snap: false);
                var (w0, h0) = (rig.Placement.DestWidth, rig.Placement.DestHeight);
                var (x0, y0) = (rig.Placement.DestX, rig.Placement.DestY);

                // Grab the CENTRE of the box - nowhere near the bottom-right resize handle.
                Drag(rig, At(rig, x0 + w0 / 2, y0 + h0 / 2), At(rig, x0 + w0 / 2 + dx, y0 + h0 / 2 + dy));

                output.WriteLine(
                    $"before=({x0:F3},{y0:F3} {w0:F3}x{h0:F3}) "
                    + $"after=({rig.Placement.DestX:F3},{rig.Placement.DestY:F3} "
                    + $"{rig.Placement.DestWidth:F3}x{rig.Placement.DestHeight:F3})");

                // THE assertion: a body drag is a move. Size is untouched, in either direction.
                Assert.Equal(w0, rig.Placement.DestWidth, 3);
                Assert.Equal(h0, rig.Placement.DestHeight, 3);
                Assert.Equal(x0 + dx, rig.Placement.DestX, 2);
                Assert.Equal(y0 + dy, rig.Placement.DestY, 2);
            }, CancellationToken.None);
    }

    /// <summary>The reported case exactly: a FULL-CANVAS placement (the authoring default, and what
    /// testproject.haplayproj contains - DestWidth/DestHeight 1.00) dragged toward the bottom-left, with
    /// snapping left ON as it ships. Every earlier drag test used a half-size box, which is the one shape
    /// where the resize handle is nowhere near the middle - so it could not reproduce this.</summary>
    [Theory]
    [InlineData(-0.4, 0.4, true)]   // bottom-left, snapping on (the report)
    [InlineData(-0.4, 0.4, false)]  // …and with snapping off, to separate the two mechanisms
    [InlineData(0.4, 0.4, true)]    // bottom-right for symmetry
    public async Task DraggingAFullCanvasPlacement_MovesOnly_AndNeverShrinksIt(double dx, double dy, bool snap)
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CompositionPlacementCanvasDragTests).Assembly)
            .Dispatch(() =>
            {
                var rig = Build(x: 0, y: 0, w: 1, h: 1, snap: snap);

                // Grab the centre of the composition - the natural "pick it up and move it" gesture.
                Drag(rig, At(rig, 0.5, 0.5), At(rig, 0.5 + dx, 0.5 + dy));

                output.WriteLine(
                    $"snap={snap} d=({dx},{dy}) -> ({rig.Placement.DestX:F3},{rig.Placement.DestY:F3} "
                    + $"{rig.Placement.DestWidth:F3}x{rig.Placement.DestHeight:F3})");

                Assert.Equal(1.0, rig.Placement.DestWidth, 3);
                Assert.Equal(1.0, rig.Placement.DestHeight, 3);
                Assert.Equal(dx, rig.Placement.DestX, 2);
                Assert.Equal(dy, rig.Placement.DestY, 2);
            }, CancellationToken.None);
    }

    [Fact]
    public async Task DraggingTheBody_ReachesNegativeCoordinates_SoAPlacementCanLeaveTheCompositionTopLeft()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CompositionPlacementCanvasDragTests).Assembly)
            .Dispatch(() =>
            {
                var rig = Build(x: 0.25, y: 0.25, w: 0.5, h: 0.5, snap: false);

                // Drag hard past the top-left corner: the placement must end up fully outside, not pinned.
                Drag(rig, At(rig, 0.5, 0.5), At(rig, -0.9, -0.9));

                output.WriteLine($"after=({rig.Placement.DestX:F3},{rig.Placement.DestY:F3})");
                Assert.True(rig.Placement.DestX < 0, $"DestX stayed at {rig.Placement.DestX}");
                Assert.True(rig.Placement.DestY < 0, $"DestY stayed at {rig.Placement.DestY}");
                Assert.Equal(0.5, rig.Placement.DestWidth, 3);
                Assert.Equal(0.5, rig.Placement.DestHeight, 3);
            }, CancellationToken.None);
    }

    /// <summary>
    /// The placement editors in the drawer must permit the model's whole range. They are TWO-WAY bound, so a
    /// narrower Minimum/Maximum does not merely restrict typing - a <c>NumericUpDown</c> coerces its Value
    /// into its own range and writes the coerced number BACK through the binding. With <c>Minimum="0"</c> on
    /// DestX/DestY that silently undid every drag past the top or left edge, which is exactly how "moving up
    /// or left does nothing" was reported: the canvas, the snap math and the view model were all correct and
    /// all three had passing tests, because none of them could see the editor coercing the result.
    /// <para>A source lint (the idiom this repo already uses for the raw-literal and Dispatch checks) rather
    /// than a realized-view walk: the property is a fact about the MARKUP, it holds whether or not the tab
    /// happens to be realized, and it names the offending line.</para>
    /// </summary>
    [Fact]
    public void PlacementEditorsInTheDrawer_MustNotClampInsideTheModelsRange()
    {
        var axaml = File.ReadAllText(RepoFile("UI/HaPlay/Views/CuePlayerView.axaml"));
        // Each NumericUpDown element, with its attributes, that is bound to a placement rectangle field.
        var editors = Regex.Matches(
            axaml,
            @"<NumericUpDown\b[^>]*?Value\s*=\s*""\{Binding\s+SelectedVideoPlacement\.(Dest[XYWidtHeigh]+)[^""]*""[^>]*?/?>",
            RegexOptions.Singleline);
        Assert.True(editors.Count >= 4, $"expected the four Dest* editors, found {editors.Count}");

        var offenders = new List<string>();
        foreach (var m in editors.Cast<Match>())
        {
            var field = m.Groups[1].Value;
            var min = ReadDouble(m.Value, "Minimum");
            var max = ReadDouble(m.Value, "Maximum");
            if (field is "DestX" or "DestY")
            {
                // Must reach a negative position, or a placement can never be dragged off the top/left.
                if (min is null || min >= 0)
                    offenders.Add($"{field}: Minimum={min?.ToString() ?? "(unset)"} - cannot go out of bounds");
            }
            else
            {
                // Must reach past the canvas, or a fill-and-crop oversize is coerced away.
                if (max is null || max < NormalizedRectRange.MaxSize)
                    offenders.Add($"{field}: Maximum={max?.ToString() ?? "(unset)"} < {NormalizedRectRange.MaxSize}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "two-way placement editors clamp inside Models.NormalizedRectRange, so they will coerce a drag "
            + "back into the composition:\n  " + string.Join("\n  ", offenders));
    }

    private static double? ReadDouble(string element, string attribute) =>
        Regex.Match(element, attribute + @"\s*=\s*""(-?[0-9.]+)""") is { Success: true } m
            ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>Repo-relative path from the test binary (bin/Debug/net10.0 → four levels up).</summary>
    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"could not locate {relative} above {AppContext.BaseDirectory}");
    }

    [Fact]
    public async Task DraggingTheHandle_Resizes_AndOnlyTheHandleDoes()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CompositionPlacementCanvasDragTests).Assembly)
            .Dispatch(() =>
            {
                var rig = Build(x: 0.1, y: 0.1, w: 0.4, h: 0.4, snap: false);

                // The handle sits at the box's bottom-right corner.
                Drag(rig, At(rig, 0.5, 0.5), At(rig, 0.8, 0.8));

                Assert.True(
                    rig.Placement.DestWidth > 0.45,
                    $"the handle drag did not resize (width {rig.Placement.DestWidth})");
                // The origin is pinned during a resize - only the trailing edge moves.
                Assert.Equal(0.1, rig.Placement.DestX, 3);
                Assert.Equal(0.1, rig.Placement.DestY, 3);
            }, CancellationToken.None);
    }
}
