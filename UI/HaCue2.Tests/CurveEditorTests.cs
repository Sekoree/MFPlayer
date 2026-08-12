using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Timeline;
using HaCue2.ViewModels;
using HaCue2.Views;
using S.Media.Session;
using Xunit;

namespace HaCue2.Tests;

/// <summary>Behavioral coverage for the curve controls: these assertions follow the document, not
/// merely a selected row that can snap back on the next refresh.</summary>
public sealed class CurveEditorTests
{
    [Fact]
    public Task TheInspectorComboActuallyChoosesBuiltInAndCustomCurves() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);
            shell.Cues.Inspector.SelectedTab = "AUDIO";

            var pane = new InspectorPane { DataContext = shell.Cues.Inspector };
            var window = new Window { Width = 420, Height = 740, Content = pane };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                var fadeIn = pane.GetVisualDescendants()
                    .OfType<ComboBox>()
                    .First(combo => combo.IsVisible
                                    && combo.ItemsSource is IReadOnlyList<CurveOption>
                                    && combo.SelectedIndex == shell.Cues.Inspector.FadeInCurve.SelectedIndex);

                fadeIn.SelectedIndex = 0;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(FadeCurve.Linear, cue.FadeInCurve.Law);
                Assert.Null(cue.FadeInCurve.Points);

                fadeIn.SelectedIndex = fadeIn.ItemCount - 1;
                Dispatcher.UIThread.RunJobs();

                Assert.NotNull(cue.FadeInCurve.Points);
                Assert.Equal(2, cue.FadeInCurve.Points!.Count);
                Assert.Equal(fadeIn.ItemCount - 1, shell.Cues.Inspector.FadeInCurve.SelectedIndex);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task SaveAsCreatesSelectsAndReoffersAProjectPreset() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);

            var editor = shell.Cues.Inspector.CurveEditor("fadeIn")!;
            editor.Apply(new CurveGesture(CurveGestureKind.Add, -1, 0.4, 0.25));
            editor.EndGesture();
            editor.PresetName = "Slow tail";
            editor.SavePreset();

            var preset = Assert.Single(shell.Project.CurvePresets);
            Assert.Equal("Slow tail", preset.Name);
            Assert.Equal(preset.Id, cue.FadeInCurve.PresetId);
            Assert.Null(cue.FadeInCurve.Points);

            var fadeOut = shell.Cues.Inspector.FadeOutCurve;
            var presetIndex = fadeOut.Curves
                .Select((option, index) => (option, index))
                .Single(pair => pair.option.PresetId == preset.Id)
                .index;
            fadeOut.SelectedIndex = presetIndex;

            Assert.Equal(preset.Id, cue.FadeOutCurve.PresetId);
            Assert.Equal(presetIndex, shell.Cues.Inspector.FadeOutCurve.SelectedIndex);
        });

    [Fact]
    public Task ExactTimeValueAndSplineSegmentReachTheLaneModel() =>
        ShellFixture.WithShell(shell =>
        {
            var project = shell.Project;
            var group = project.AllCues().OfType<GroupCueNode>()
                .First(candidate => candidate.FireMode == GroupFireMode.Timeline);
            var media = group.Children.OfType<MediaCueNode>().First();
            var lane = new EffectLane
            {
                Kind = EffectLaneKind.Volume,
                Points = [new LanePoint(0, 1), new LanePoint(1, 1)],
            };
            media.EffectLanes.Add(lane);
            shell.Runtime.MediaDurations[media.Id] = TimeSpan.FromSeconds(8);
            shell.Cues.Timeline.Show(group);

            var row = shell.Cues.Timeline.Lanes.Single(candidate => candidate.EffectLaneId == lane.Id);
            shell.Cues.Timeline.ToggleEffectLane(row);
            Assert.True(row.IsExpanded);
            Assert.True(row.Height > 100);

            shell.Cues.Timeline.ApplyLaneGesture(
                row,
                new CurveGesture(CurveGestureKind.Add, -1, 0.5, 0.75));
            shell.Cues.Timeline.EndGesture();
            Assert.Contains(lane.Points, point => Math.Abs(point.X - 0.5) < 0.001
                                                  && Math.Abs(point.Y - 0.25) < 0.001);

            row = shell.Cues.Timeline.Lanes.Single(candidate => candidate.EffectLaneId == lane.Id);
            var detail = shell.Cues.Timeline.LaneEditor(row)!;
            detail.Apply(new CurveGesture(CurveGestureKind.Select, 1, 0, 0));
            detail.PointTime = "2";
            detail.PointValue = "-6 dB";
            detail.Segment = "s-curve";

            Assert.Equal(0.25, lane.Points[1].X, 3);
            Assert.Equal(Math.Pow(10, -6d / 20), lane.Points[1].Y, 3);
            Assert.Equal(FadeCurve.SCurve, lane.Points[1].CurveToNext);
        });

    [Fact]
    public Task TheResizableCurveWindowRendersItsCanvasAndExactFields() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);
            var window = new CurveEditorWindow(shell.Cues.Inspector.CurveEditor("fadeIn")!);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                Assert.True(window.CanResize);
                Assert.NotNull(window.GetVisualDescendants().OfType<CurveCanvas>().SingleOrDefault());
                Assert.True(window.GetVisualDescendants().OfType<TextBox>().Count(box => box.IsVisible) >= 3);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task AnExpandedTimelineLaneRendersHandlesOverItsWaveform() =>
        ShellFixture.WithShell(shell =>
        {
            var group = shell.Project.AllCues().OfType<GroupCueNode>()
                .First(candidate => candidate.FireMode == GroupFireMode.Timeline);
            var media = group.Children.OfType<MediaCueNode>().First();
            var lane = new EffectLane
            {
                Kind = EffectLaneKind.Volume,
                Points = [new LanePoint(0, 1), new LanePoint(1, 0.5)],
            };
            media.EffectLanes.Add(lane);
            shell.Cues.Timeline.Show(group);

            var row = shell.Cues.Timeline.Lanes.Single(candidate => candidate.EffectLaneId == lane.Id);
            row.Peaks = [0.1f, 0.8f, 0.3f, 1f];
            shell.Cues.Timeline.ToggleEffectLane(row);

            var sheet = new TimelineSheet { DataContext = shell.Cues.Timeline };
            var window = new Window { Width = 1000, Height = 430, Content = sheet };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                Assert.Contains(sheet.GetVisualDescendants().OfType<CurveCanvas>(), canvas => canvas.IsVisible);
                Assert.Contains(
                    sheet.GetVisualDescendants().OfType<WaveformGraph>(),
                    graph => graph.IsVisible && !graph.ShowMarkers && graph.Peaks is { Count: 4 });
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task TheProjectStopFadeUsesTheSameWorkingCurvePicker() =>
        ShellFixture.WithShell(shell =>
        {
            var settings = new SettingsViewModel(shell.Project, shell.Journal);
            var linear = settings.StopFadeCurveChoices
                .Select((option, index) => (option, index))
                .Single(pair => pair.option.Law == FadeCurve.Linear)
                .index;

            settings.StopFadeCurveIndex = linear;
            Assert.Equal(FadeCurve.Linear, shell.Project.Settings.StopFadeCurve.Law);

            var custom = settings.StopFadeCurveChoices.Count - 1;
            settings.StopFadeCurveIndex = custom;
            Assert.NotNull(shell.Project.Settings.StopFadeCurve.Points);
            Assert.Equal(1, shell.Project.Settings.StopFadeCurve.Points![0].Level);
            Assert.Equal(0, shell.Project.Settings.StopFadeCurve.Points[^1].Level);
            Assert.Equal(custom, settings.StopFadeCurveIndex);
            Assert.NotNull(settings.StopFadeEditor());
        });

    [Fact]
    public Task BezierSegmentHandlesAreVisibleAndWriteThroughToTheFade() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);
            var editor = shell.Cues.Inspector.CurveEditor("fadeIn")!;

            editor.Apply(new CurveGesture(CurveGestureKind.Select, 0, 0, 0));
            editor.Segment = "bezier";

            Assert.Single(editor.Tangents);
            Assert.NotNull(cue.FadeInCurve.Points![0].OutHandleX);
            Assert.NotNull(cue.FadeInCurve.Points[1].InHandleX);

            editor.Apply(new CurveGesture(CurveGestureKind.MoveOutgoingTangent, 0, 0.12, 0.1));
            editor.EndGesture();

            Assert.Equal(0.12, cue.FadeInCurve.Points[0].OutHandleX!.Value, 3);
            Assert.Equal(0.9, cue.FadeInCurve.Points[0].OutHandleLevel!.Value, 3);
            Assert.True(cue.FadeInCurve.Resolve(shell.Project).Evaluate(0.25) > 0.25f);
        });

    [Fact]
    public Task PresetsCanBeRenamedDeletedWithoutChangingReferencesAndImportedSafely() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);
            var editor = shell.Cues.Inspector.CurveEditor("fadeIn")!;
            editor.PresetName = "House curve";
            editor.SavePreset();
            var preset = Assert.Single(shell.Project.CurvePresets);
            shell.Project.Settings.StopFadeCurve.PresetId = preset.Id;

            editor.ManagedPresetName = "House fade";
            editor.RenamePreset();
            Assert.Equal("House fade", preset.Name);
            Assert.Equal(preset.Id, cue.FadeInCurve.PresetId);

            editor.DeletePreset();
            Assert.Empty(shell.Project.CurvePresets);
            Assert.Null(cue.FadeInCurve.PresetId);
            Assert.NotNull(cue.FadeInCurve.Points);
            Assert.Null(shell.Project.Settings.StopFadeCurve.PresetId);
            Assert.NotNull(shell.Project.Settings.StopFadeCurve.Points);

            shell.Journal.Undo();
            Assert.Single(shell.Project.CurvePresets);
            Assert.Equal(preset.Id, cue.FadeInCurve.PresetId);
            Assert.Equal(preset.Id, shell.Project.Settings.StopFadeCurve.PresetId);

            var source = new HaCueProject
            {
                CurvePresets =
                [
                    new CurvePreset
                    {
                        Id = preset.Id,
                        Name = "House fade",
                        Points = [new FadeCurvePoint(0, 0), new FadeCurvePoint(1, 1)],
                    },
                ],
            };
            editor.ImportPresets(source);

            Assert.Equal(2, shell.Project.CurvePresets.Count);
            Assert.Equal(2, shell.Project.CurvePresets.Select(item => item.Id).Distinct().Count());
            Assert.Contains(shell.Project.CurvePresets, item => item.Name == "House fade (2)");
        });

    [Fact]
    public Task TimelineKeyframesCanBeMultiSelectedMovedCopiedPastedAndDeleted() =>
        ShellFixture.WithShell(shell =>
        {
            var group = shell.Project.AllCues().OfType<GroupCueNode>()
                .First(candidate => candidate.FireMode == GroupFireMode.Timeline);
            var media = group.Children.OfType<MediaCueNode>().First();
            var lane = new EffectLane
            {
                Kind = EffectLaneKind.Volume,
                Points = [new LanePoint(0.1, 1), new LanePoint(0.3, 0.5), new LanePoint(0.8, 1)],
            };
            media.EffectLanes.Add(lane);
            shell.Runtime.MediaDurations[media.Id] = TimeSpan.FromSeconds(8);
            shell.Cues.Timeline.Show(group);
            var row = shell.Cues.Timeline.Lanes.Single(item => item.EffectLaneId == lane.Id);

            shell.Cues.Timeline.ApplyLaneGesture(
                row, new CurveGesture(CurveGestureKind.Select, 0, 0, 0));
            shell.Cues.Timeline.ApplyLaneGesture(
                row, new CurveGesture(CurveGestureKind.ToggleSelection, 1, 0, 0));
            shell.Cues.Timeline.ApplyLaneGesture(
                row, new CurveGesture(CurveGestureKind.Move, 0, 0.2, 0));
            shell.Cues.Timeline.EndGesture();
            Assert.Equal(0.2, lane.Points[0].X, 3);
            Assert.Equal(0.4, lane.Points[1].X, 3);

            row = shell.Cues.Timeline.Lanes.Single(item => item.EffectLaneId == lane.Id);
            var clipboard = shell.Cues.Timeline.CopySelectedKeyframes(row);
            Assert.StartsWith(LaneKeyframeClipboard.Header, clipboard);

            // Paste REPLACES the span it lands on. The copied pair spans 0 → 0.2 and the playhead
            // puts it at the clip's start, so the keyframe already at 0.2 is consumed rather than
            // doubled — 0.4 and 0.8 are outside the span and survive.
            Assert.True(shell.Cues.Timeline.PasteKeyframes(row, clipboard));
            Assert.Equal(4, lane.Points.Count);
            Assert.Equal([0, 0.2, 0.4, 0.8], lane.Points.Select(point => Math.Round(point.X, 3)));

            row = shell.Cues.Timeline.Lanes.Single(item => item.EffectLaneId == lane.Id);
            Assert.Equal(2, row.Points.Count(point => point.IsSelected));
            shell.Cues.Timeline.DeleteSelectedKeyframes(row);
            Assert.Equal(2, lane.Points.Count);

            // A lane keeps two keyframes, so deleting the rest is refused — and SAYS so rather than
            // reporting a deletion that did not happen.
            row = shell.Cues.Timeline.Lanes.Single(item => item.EffectLaneId == lane.Id);
            shell.Cues.Timeline.SelectAllKeyframes(row);
            shell.Cues.Timeline.DeleteSelectedKeyframes(row);
            Assert.Equal(2, lane.Points.Count);
            Assert.Contains("at least two", shell.Cues.Timeline.KeyframeStatus);
        });

    /// <summary>
    /// The standalone window is the FULLER editor, so it cannot know fewer gestures than the inline
    /// lane does. The canvas raised Ctrl+A/C/V and Ctrl/Shift-click in both hosts; only one listened.
    /// </summary>
    [Fact]
    public Task TheCurveWindowMultiSelectsMovesCopiesAndPastesLikeTheInlineLane() =>
        ShellFixture.WithShell(shell =>
        {
            var group = shell.Project.AllCues().OfType<GroupCueNode>()
                .First(candidate => candidate.FireMode == GroupFireMode.Timeline);
            var media = group.Children.OfType<MediaCueNode>().First();
            var lane = new EffectLane
            {
                Kind = EffectLaneKind.Volume,
                Points = [new LanePoint(0, 1), new LanePoint(0.25, 0.5), new LanePoint(0.5, 1),
                          new LanePoint(1, 1)],
            };
            media.EffectLanes.Add(lane);
            shell.Runtime.MediaDurations[media.Id] = TimeSpan.FromSeconds(8);
            shell.Cues.Timeline.Show(group);

            var row = shell.Cues.Timeline.Lanes.Single(item => item.EffectLaneId == lane.Id);
            var editor = shell.Cues.Timeline.LaneEditor(row)!;

            // Ctrl-click adds to the selection rather than replacing it.
            editor.Apply(new CurveGesture(CurveGestureKind.Select, 1, 0, 0));
            editor.Apply(new CurveGesture(CurveGestureKind.ToggleSelection, 2, 0, 0));
            Assert.Equal(2, editor.SelectionCount);
            Assert.True(editor.HasMultipleSelected);
            Assert.Equal(2, editor.Points.Count(point => point.IsSelected));

            // Dragging one of them moves the GROUP, spacing preserved.
            editor.Apply(new CurveGesture(CurveGestureKind.Move, 1, 0.35, 0));
            editor.EndGesture();
            Assert.Equal(0.35, lane.Points[1].X, 3);
            Assert.Equal(0.6, lane.Points[2].X, 3);

            var text = editor.Copy();
            Assert.StartsWith(LaneKeyframeClipboard.Header, text);

            // Paste lands at the primary selection and REPLACES the span it covers: the copied pair
            // spans 0.25, so pasting at 0 consumes the keyframe already at 0 and leaves the three
            // beyond 0.25 alone. Nothing is doubled onto an instant that already had a keyframe.
            editor.Apply(new CurveGesture(CurveGestureKind.Select, 0, 0, 0));
            Assert.True(editor.Paste(text));
            Assert.Equal([0, 0.25, 0.35, 0.6, 1], lane.Points.Select(point => Math.Round(point.X, 3)));
            Assert.Equal(2, editor.SelectionCount);

            // Delete acts on the whole selection, not just the primary.
            editor.Apply(new CurveGesture(CurveGestureKind.RemoveSelection, -1, 0, 0));
            editor.EndGesture();
            Assert.Equal(3, lane.Points.Count);

            editor.SelectAll();
            Assert.Equal(3, editor.SelectionCount);
        });

    /// <summary>A fade knot holds; a lane point cannot. The clipboard is shared by both editors, so
    /// the format has to carry the richer of the two — and still read what version 1 wrote.</summary>
    [Fact]
    public Task ClipboardKeyframesCarryHoldAndStillReadTheOlderFormat() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = shell.Project.AllCues().OfType<MediaCueNode>().First();
            cue.FadeInCurve.Points =
            [
                new FadeCurvePoint(0, 0),
                new FadeCurvePoint(0.4, 0.5, Hold: true),
                new FadeCurvePoint(1, 1),
            ];
            var target = new CurveSpecTarget(cue.Id, "fadeIn", cue.FadeInCurve, shell.Project);
            var editor = new CurveEditorViewModel(shell.Journal, target, "fade in");

            editor.SelectAll();
            var text = editor.Copy()!;
            var decoded = LaneKeyframeClipboard.DecodeKnots(text)!;
            Assert.Equal(3, decoded.Count);
            Assert.True(decoded[1].Hold);

            // A version 1 line has seven fields and no hold flag. It must still decode.
            var legacy = "HaCue2-Keyframes/1\n0;1;0;;;;\n0.5;0.25;0;;;;";
            var older = LaneKeyframeClipboard.DecodeKnots(legacy)!;
            Assert.Equal(2, older.Count);
            Assert.All(older, knot => Assert.False(knot.Hold));
            Assert.Equal(0.5, older[1].X, 5);

            Assert.Null(LaneKeyframeClipboard.DecodeKnots("HaCue2-Keyframes/2\n0;1;0;;;;;7"));
        });
}
