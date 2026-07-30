using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using HaPlay.ViewModels;
using HaPlay.Views.Controls;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// The envelope geometry SPLIT (structural refactor step 1C): <see cref="TimelineMath.ProjectEnvelope"/>
/// is a pure, never-clamped model→position projection that flags out-of-range keyframes, and the canvas
/// is a thin adapter that decides presentation from that flag (dots for in-range points, ONE counted
/// chevron badge per edge for the rest). These tests pin the invariant the split exists to guarantee:
/// <b>every point the renderer draws as a dot is hit-testable, and hit-testing never returns a point the
/// renderer did not draw</b> - previously false, because the shared centre function clamped x to the
/// block's right edge, stacking every keyframe past a trimmed-in end onto one pixel column where only
/// the last of them could ever be picked.
/// </summary>
public sealed class TimelineEnvelopeProjectionTests
{
    private const double PxPerMs = 0.02; // 20 px per second - a 30 s block is 600 px wide
    private const int ClipMs = 30_000;

    private static readonly Rect Block = TimelineMath.BlockRect(0, 0, ClipMs, PxPerMs);

    private static CueAutomationPoint Pt(int timeMs, double levelDb) => new() { TimeMs = timeMs, LevelDb = levelDb };

    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(TimelineEnvelopeProjectionTests).Assembly)
            .DispatchGuarded(action, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    // ---- the invariant ----

    [Fact]
    public void DrawnDotsAndPickedPoints_AlwaysAgree_AcrossGeneratedEnvelopes()
    {
        var rng = new Random(20260729); // fixed seed: a failure is reproducible
        for (var iteration = 0; iteration < 120; iteration++)
        {
            var envelope = RandomEnvelope(rng);
            var overlay = TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs);
            var where = $"iteration {iteration}: [{string.Join(", ", envelope.Select(p => p.TimeMs))}]";

            // 1. Every dot the renderer draws is pickable at its own centre, and what comes back is a
            //    point drawn AT THAT SAME SPOT (coincident keyframes are visually interchangeable).
            foreach (var drawn in overlay.Points.Where(p => p.IsInRange))
            {
                var hit = TimelineMath.HitTestEnvelope(overlay, drawn.Center);
                Assert.True(hit.IsHit, $"{where}: dot {drawn.Index} was drawn but not hit-testable");
                Assert.False(hit.ViaEdgeIndicator, $"{where}: an in-range dot resolved through a badge");
                var picked = overlay.Points[hit.PointIndex];
                Assert.True(picked.IsInRange, $"{where}: picked out-of-range point {hit.PointIndex}");
                Assert.Equal(drawn.Center.X, picked.Center.X, 6);
                Assert.Equal(drawn.Center.Y, picked.Center.Y, 6);
            }

            // 2. Nothing the renderer did NOT draw ever comes back: a pick is either an in-range dot or
            //    the representative of a badge that was actually drawn at that edge.
            foreach (var probe in ProbeGrid())
            {
                var hit = TimelineMath.HitTestEnvelope(overlay, probe);
                if (!hit.IsHit)
                    continue;
                var picked = overlay.Points[hit.PointIndex];
                if (!hit.ViaEdgeIndicator)
                {
                    Assert.True(picked.IsInRange, $"{where}: probe {probe} picked undrawn point {hit.PointIndex}");
                    continue;
                }

                var badge = picked.Range == TimelineEnvelopeRange.BeforeStart ? overlay.BeforeStart : overlay.BeyondEnd;
                Assert.True(badge.HasValue, $"{where}: probe {probe} resolved through a badge that was not drawn");
                Assert.Equal(badge!.Value.PointIndex, hit.PointIndex);
                Assert.Equal(badge.Value.Edge, picked.Range);
                Assert.False(picked.IsInRange, $"{where}: an in-range point resolved through a badge");
            }
        }
    }

    /// <summary>Envelopes mixing in-range, past-the-end and (file-authored) negative-time keyframes,
    /// including coincident times - kept time-sorted, as every writer keeps them.</summary>
    private static List<CueAutomationPoint> RandomEnvelope(Random rng)
    {
        var times = new List<int>();
        var count = rng.Next(1, 9);
        for (var i = 0; i < count; i++)
        {
            times.Add(rng.Next(10) switch
            {
                0 or 1 => rng.Next(-10_000, 0),           // before the trimmed start
                2 or 3 or 4 => rng.Next(ClipMs + 1, 90_000), // past the trimmed end
                _ => rng.Next(0, ClipMs + 1),
            });
            if (rng.Next(6) == 0 && times.Count > 0)
                times.Add(times[^1]); // coincident keyframes (an instant step)
        }
        times.Sort();
        return times.Select(t => Pt(t, Math.Round(rng.NextDouble() * -60 + 12, 1))).ToList();
    }

    private static IEnumerable<Point> ProbeGrid()
    {
        for (var x = Block.X - 30; x <= Block.Right + 30; x += 5)
            for (var y = Block.Y - 6; y <= Block.Bottom + 6; y += 4)
                yield return new Point(x, y);
    }

    // ---- edge indicator ----

    [Fact]
    public void ProjectEnvelope_CollapsesPointsPastTheTrimmedEnd_IntoOneCountedBadge()
    {
        var envelope = new[] { Pt(0, 0), Pt(15_000, -6), Pt(31_000, -6), Pt(45_000, -3), Pt(90_000, 0) };
        var overlay = TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs);

        Assert.Null(overlay.BeforeStart);
        Assert.NotNull(overlay.BeyondEnd);
        var badge = overlay.BeyondEnd!.Value;

        // THREE out-of-range keyframes, ONE indicator carrying their count.
        Assert.Equal(TimelineEnvelopeRange.BeyondEnd, badge.Edge);
        Assert.Equal(3, badge.Count);
        Assert.Equal(2, badge.PointIndex); // the innermost of them
        Assert.Equal(2, overlay.Points.Count(p => p.IsInRange)); // only the two in-range dots are drawn

        // It lives inside the block and clear of the right trim grip, so edge-dragging still works.
        Assert.True(Block.Contains(badge.Bounds), $"badge {badge.Bounds} escaped block {Block}");
        Assert.True(badge.Bounds.Right <= Block.Right - TimelineMath.EdgeGripPx + 0.001);

        // Hit-testable in its own right, and it resolves to the innermost out-of-range keyframe.
        var hit = TimelineMath.HitTestEnvelope(overlay, badge.Bounds.Center);
        Assert.Equal(2, hit.PointIndex);
        Assert.True(hit.ViaEdgeIndicator);

        // And the pixel column they all used to be clamped onto is no longer a keyframe target.
        Assert.False(TimelineMath
            .HitTestEnvelope(overlay, new Point(Block.Right, TimelineMath.EnvelopeYForDb(Block, -6))).IsHit);
    }

    [Fact]
    public void ProjectEnvelope_HandlesBothEdges_Symmetrically()
    {
        // Negative clip times only reach the editor from an externally authored/edited file, but the
        // runtime honours them (it interpolates from the first point), so they get the same treatment.
        var envelope = new[] { Pt(-9000, -3), Pt(-2000, -3), Pt(5000, 0), Pt(80_000, -6) };
        var overlay = TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs);

        Assert.NotNull(overlay.BeforeStart);
        Assert.NotNull(overlay.BeyondEnd);
        var before = overlay.BeforeStart!.Value;
        var beyond = overlay.BeyondEnd!.Value;

        Assert.Equal(2, before.Count);
        Assert.Equal(1, before.PointIndex); // innermost = the LAST one before the in-range run
        Assert.Equal(1, beyond.Count);
        Assert.Equal(3, beyond.PointIndex);

        // The two badges never overlap, and each sits at its own edge.
        Assert.True(before.Bounds.Right <= beyond.Bounds.X);
        Assert.True(before.Bounds.X >= Block.X);
        Assert.True(beyond.Bounds.Right <= Block.Right);

        Assert.Equal(1, TimelineMath.HitTestEnvelope(overlay, before.Bounds.Center).PointIndex);
        Assert.Equal(3, TimelineMath.HitTestEnvelope(overlay, beyond.Bounds.Center).PointIndex);
    }

    [Fact]
    public void EdgeIndicator_PeelsOutOfRangeKeyframesOff_OneAtATime()
    {
        // What the canvas does with the selection: click the badge, press Delete, repeat.
        var envelope = new List<CueAutomationPoint>
            { Pt(0, 0), Pt(20_000, -6), Pt(31_000, -6), Pt(45_000, -3), Pt(90_000, 0) };

        for (var expected = 3; expected >= 1; expected--)
        {
            var overlay = TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs);
            Assert.NotNull(overlay.BeyondEnd);
            var badge = overlay.BeyondEnd!.Value;
            Assert.Equal(expected, badge.Count);

            var hit = TimelineMath.HitTestEnvelope(overlay, badge.Bounds.Center);
            Assert.True(hit.ViaEdgeIndicator);
            Assert.Equal(2, hit.PointIndex); // always the innermost survivor
            envelope.RemoveAt(hit.PointIndex);
        }

        Assert.Null(TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs).BeyondEnd);
        Assert.Equal([0, 20_000], envelope.Select(p => p.TimeMs));
    }

    [Fact]
    public void EdgeIndicatorPoint_DragsBackIntoTheTrimmedRange()
    {
        var envelope = new[] { Pt(0, 0), Pt(20_000, -6), Pt(45_000, -3), Pt(90_000, 0) };
        var badge = TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs).BeyondEnd!.Value;

        // The badge's keyframe is the one the drag clamp can actually pull back in: its neighbours are
        // the last in-range point and another out-of-range one, so the whole trimmed tail is reachable.
        Assert.Equal(25_000, TimelineMath.EnvelopeClampDragTime(envelope, badge.PointIndex, 25_000, ClipMs));
        Assert.Equal(ClipMs, TimelineMath.EnvelopeClampDragTime(envelope, badge.PointIndex, 99_000, ClipMs));
        Assert.Equal(20_000, TimelineMath.EnvelopeClampDragTime(envelope, badge.PointIndex, 0, ClipMs));
    }

    [Fact]
    public void BeforeStartBadgeDrag_CannotRemainNegativeBehindANegativeNeighbor()
    {
        var envelope = new[] { Pt(-9000, -3), Pt(-2000, -3), Pt(5000, 0) };
        var badge = TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs).BeforeStart!.Value;

        Assert.Equal(1, badge.PointIndex);
        Assert.Equal(0, TimelineMath.EnvelopeClampDragTime(
            envelope, badge.PointIndex, candidateMs: -5000, maxTimeMs: ClipMs));
    }

    [Fact]
    public void EdgeIndicator_IsSuppressedOnABlockTooNarrowToShowIt_AndPickingAgrees()
    {
        // Renderer and hit-test must agree even in the degenerate case: no badge drawn, nothing picked.
        var narrow = TimelineMath.BlockRect(0, 0, 300, PxPerMs); // 6 px wide
        var envelope = new[] { Pt(0, 0), Pt(9000, -6), Pt(12_000, -6) };
        var overlay = TimelineMath.ProjectEnvelope(narrow, envelope, PxPerMs);

        Assert.Null(overlay.BeyondEnd);
        Assert.Null(overlay.BeforeStart);
        Assert.Equal(2, overlay.Points.Count(p => !p.IsInRange));
        foreach (var probe in ProbeGrid())
        {
            var hit = TimelineMath.HitTestEnvelope(overlay, probe);
            if (hit.IsHit)
                Assert.True(overlay.Points[hit.PointIndex].IsInRange);
        }
    }

    [Fact]
    public void HitTestEnvelope_PrefersAnInRangeDot_OverAnOverlappingBadge()
    {
        // A dot parked under the badge stays the precise target (badges are the fallback).
        var badgeCenter = TimelineMath
            .ProjectEnvelope(Block, [Pt(0, 0), Pt(90_000, 0)], PxPerMs).BeyondEnd!.Value.Bounds.Center;
        var underMs = (int)Math.Round((badgeCenter.X - Block.X) / PxPerMs);
        var envelope = new[]
        {
            Pt(0, 0),
            Pt(underMs, TimelineMath.EnvelopeDbForY(Block, badgeCenter.Y)),
            Pt(90_000, 0),
        };

        var overlay = TimelineMath.ProjectEnvelope(Block, envelope, PxPerMs);
        var hit = TimelineMath.HitTestEnvelope(overlay, badgeCenter);
        Assert.Equal(1, hit.PointIndex);
        Assert.False(hit.ViaEdgeIndicator);
    }

    // ---- the canvas adapter, end to end ----

    [Fact]
    public void Canvas_SelectingThroughTheEdgeIndicator_DeletesThatKeyframe()
    {
        DispatchUi(() =>
        {
            HeadlessAppTheme.ApplyProductionBaseTheme();
            var vm = new CuePlayerViewModel();
            vm.AddEmptyMediaCue();
            var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            media.DurationMs = ClipMs;
            media.VolumeEnvelope = [Pt(0, 0), Pt(10_000, -6), Pt(45_000, -3), Pt(60_000, -3), Pt(90_000, 0)];

            var canvas = new TimelineCanvas
            {
                Lanes = new ObservableCollection<CueNodeViewModel> { media },
                PixelsPerMs = PxPerMs,
                IsEditable = true,
            };
            var window = new Window { Content = canvas, Width = 800, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var block = TimelineMath.BlockRect(0, 0, ClipMs, PxPerMs);
            var badge = TimelineMath.ProjectEnvelope(block, media.VolumeEnvelope, PxPerMs).BeyondEnd!.Value;
            Assert.Equal(3, badge.Count);

            window.CaptureRenderedFrame(); // drives a real Render pass - the badge drawing must not throw

            var offset = canvas.TranslatePoint(default, window) ?? default;
            var click = badge.Bounds.Center + offset;
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // The badge click selected the innermost out-of-range keyframe; Delete removes exactly it.
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([0, 10_000, 60_000, 90_000], media.VolumeEnvelope.Select(p => p.TimeMs));
            Assert.Equal(
                2, TimelineMath.ProjectEnvelope(block, media.VolumeEnvelope, PxPerMs).BeyondEnd!.Value.Count);

            window.Close();
        });
    }

    /// <summary>
    /// A badge CLICK must not relocate the keyframe it stands for. The badge sits at the block's edge,
    /// nowhere near the represented keyframe's true position, so the ordinary absolute-position drag
    /// would teleport a 45 s keyframe to ~29 s on the first pixel of hand jitter between press and
    /// release - a silent, destructive edit from what the operator experienced as a click. Badge-started
    /// drags therefore only begin editing once the pointer has moved past
    /// <see cref="TimelineMath.EnvelopeBadgeDragThresholdPx"/>; a dot drag stays pixel-exact from the
    /// first move (its dot IS under the pointer, so a 1 px jitter is a 1 px edit).
    /// </summary>
    [Fact]
    public void Canvas_ClickingAnEdgeIndicator_DoesNotTeleportTheKeyframeOnPointerJitter()
    {
        DispatchUi(() =>
        {
            HeadlessAppTheme.ApplyProductionBaseTheme();
            var (window, canvas, media) = ShowEnvelopeCanvas([Pt(0, 0), Pt(10_000, -6), Pt(45_000, -3)]);
            try
            {
                var block = TimelineMath.BlockRect(0, 0, ClipMs, PxPerMs);
                var badge = TimelineMath.ProjectEnvelope(block, media.VolumeEnvelope, PxPerMs).BeyondEnd!.Value;
                var offset = canvas.TranslatePoint(default, window) ?? default;
                var press = badge.Bounds.Center + offset;

                window.MouseDown(press, MouseButton.Left);
                window.MouseMove(press + new Point(2, 1)); // hand jitter, well under the threshold
                window.MouseUp(press + new Point(2, 1), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal([0, 10_000, 45_000], media.VolumeEnvelope.Select(p => p.TimeMs));

                // A deliberate drag past the threshold still pulls the keyframe back into range.
                window.MouseDown(press, MouseButton.Left);
                window.MouseMove(press + new Point(-40, 0));
                window.MouseUp(press + new Point(-40, 0), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                var moved = media.VolumeEnvelope[2].TimeMs;
                Assert.True(moved is > 10_000 and <= ClipMs, $"keyframe landed at {moved} ms");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The threshold must not blunt an ordinary dot drag: its dot is under the pointer, so the
    /// very first pixel of movement is a legitimate (and tiny) edit.</summary>
    [Fact]
    public void Canvas_DraggingAnInRangeDot_MovesItOnTheFirstPixel()
    {
        DispatchUi(() =>
        {
            HeadlessAppTheme.ApplyProductionBaseTheme();
            var (window, canvas, media) = ShowEnvelopeCanvas([Pt(0, 0), Pt(10_000, -6), Pt(20_000, -3)]);
            try
            {
                var block = TimelineMath.BlockRect(0, 0, ClipMs, PxPerMs);
                var dot = TimelineMath.ProjectEnvelopePoint(block, media.VolumeEnvelope[1], 1, PxPerMs).Center;
                var offset = canvas.TranslatePoint(default, window) ?? default;

                window.MouseDown(dot + offset, MouseButton.Left);
                window.MouseMove(dot + offset + new Point(2, 0));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(10_100, media.VolumeEnvelope[1].TimeMs); // 2 px at 0.02 px/ms
                window.MouseUp(dot + offset + new Point(2, 0), MouseButton.Left);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (Window Window, TimelineCanvas Canvas, CueNodeViewModel Media) ShowEnvelopeCanvas(
        List<CueAutomationPoint> envelope)
    {
        var vm = new CuePlayerViewModel();
        vm.AddEmptyMediaCue();
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        media.DurationMs = ClipMs;
        media.VolumeEnvelope = envelope;

        var canvas = new TimelineCanvas
        {
            Lanes = new ObservableCollection<CueNodeViewModel> { media },
            PixelsPerMs = PxPerMs,
            IsEditable = true,
        };
        var window = new Window { Content = canvas, Width = 800, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, canvas, media);
    }
}
