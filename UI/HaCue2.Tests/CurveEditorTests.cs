using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
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
}
