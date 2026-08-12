using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

public sealed class AutomationEditorTests
{
    [Fact]
    public void UnresolvedPluginTrackOpensReadOnlyAndPreservesItsKeys()
    {
        var key = new AutomationKeyframe { TimeMs = 500, Value = 42 };
        var track = new AutomationTrack
        {
            Target = new AutomationTargetRef { PropertyId = "plugin.example.missing.parameter" },
            Keyframes = [key],
        };
        var cue = new MediaCueNode { MediaPath = "missing.wav", AutomationTracks = [track] };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [cue] }] };
        var editor = new AutomationEditorViewModel(
            new ProjectJournal(project), cue, track, TimeSpan.FromSeconds(10));

        Assert.False(editor.IsResolved);
        Assert.False(editor.CanEdit);
        Assert.Contains("preserved read-only", editor.Problem, StringComparison.Ordinal);
        editor.CursorMs = 1_000;
        editor.AddKeyAtCursor();
        Assert.Equal([key], track.Keyframes);
    }

    [Fact]
    public Task WindowRendersTheScrollableEditorAndAddAtCursorControl() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var (editor, _, _) = Editor();
            var window = new AutomationEditorWindow(editor);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                Assert.True(window.CanResize);
                Assert.Single(window.GetVisualDescendants().OfType<CurveCanvas>());
                Assert.Contains(window.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.IsVisible);
                Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button =>
                    button.IsVisible && button.Content?.ToString()?.Contains("KEY", StringComparison.Ordinal) == true);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void AddsKeysOneHundredMillisecondsApartDeepIntoALongCue()
    {
        var (editor, track, journal) = Editor();
        editor.ViewStartMs = 1_635_000;
        editor.ViewLengthMs = 1_000;

        editor.CursorMs = 1_635_100;
        editor.AddKeyAtCursor();
        editor.CursorMs = 1_635_200;
        editor.AddKeyAtCursor();

        Assert.Equal([1_635_100L, 1_635_200L], track.Keyframes.Select(key => key.TimeMs));
        journal.Undo();
        Assert.Single(track.Keyframes);
        journal.Undo();
        Assert.Empty(track.Keyframes);
    }

    [Fact]
    public void DraggingAcrossANeighbourKeepsTheCapturedKeyIdentity()
    {
        var first = new AutomationKeyframe { TimeMs = 1_635_100, Value = -6 };
        var second = new AutomationKeyframe { TimeMs = 1_635_200, Value = -12 };
        var (editor, track, journal) = Editor(first, second);
        editor.ViewStartMs = 1_635_000;
        editor.ViewLengthMs = 1_000;

        editor.Apply(new CurveGesture(CurveGestureKind.Select, 0, 0, 0));
        editor.Apply(new CurveGesture(CurveGestureKind.Move, 0, .4, .5));
        editor.Apply(new CurveGesture(CurveGestureKind.Move, 0, .5, .5));
        editor.EndGesture();

        Assert.Equal(1_635_500, track.Keyframes.Single(key => key.Id == first.Id).TimeMs);
        Assert.Equal(1_635_200, track.Keyframes.Single(key => key.Id == second.Id).TimeMs);
        Assert.Single(journal.Log);

        journal.Undo();
        Assert.Equal(1_635_100, track.Keyframes.Single(key => key.Id == first.Id).TimeMs);
        Assert.Equal(1_635_200, track.Keyframes.Single(key => key.Id == second.Id).TimeMs);
    }

    [Fact]
    public void DragUsesALocalDraftUntilReleaseAndEscapeDiscardsIt()
    {
        var key = new AutomationKeyframe { TimeMs = 1_000, Value = -6 };
        var (editor, track, journal) = Editor(key);
        editor.ViewLengthMs = 10_000;

        editor.Apply(new CurveGesture(CurveGestureKind.Select, 0, 0, 0));
        editor.Apply(new CurveGesture(CurveGestureKind.Move, 0, .5, .5));

        Assert.Equal(1_000, track.Keyframes.Single().TimeMs);
        Assert.Empty(journal.Log);

        editor.CancelGesture();

        Assert.Equal(1_000, track.Keyframes.Single().TimeMs);
        Assert.Empty(journal.Log);
    }

    [Fact]
    public void ArrowNudgeUsesTheSelectedSnapGridAndIsOneUndoStep()
    {
        var key = new AutomationKeyframe { TimeMs = 1_000, Value = -6 };
        var (editor, track, journal) = Editor(key);
        editor.SnapTimeMs = 40;

        editor.Apply(new CurveGesture(CurveGestureKind.Select, 0, 0, 0));
        editor.Apply(new CurveGesture(
            CurveGestureKind.Move, 0, 1, 0, IsNudge: true, Accelerated: true));
        editor.EndGesture();

        Assert.Equal(1_200, track.Keyframes.Single().TimeMs);
        Assert.Single(journal.Log);
        journal.Undo();
        Assert.Equal(1_000, track.Keyframes.Single().TimeMs);
    }

    [Fact]
    public void SegmentCurveUndoResolvesTheKeyAfterALaterListReplacement()
    {
        var key = new AutomationKeyframe { TimeMs = 1_000, Value = -6 };
        var (editor, track, journal) = Editor(key);
        editor.Apply(new CurveGesture(CurveGestureKind.Select, 0, 0, 0));
        var curve = Assert.IsType<CurveEditorViewModel>(editor.SegmentCurveEditor());

        curve.Apply(new CurveGesture(CurveGestureKind.Add, -1, .5, .5));
        curve.EndGesture();
        Assert.Equal(3, track.Keyframes.Single().Curve.Points?.Count);

        editor.Apply(new CurveGesture(CurveGestureKind.Move, 0, .2, .5));
        editor.EndGesture();
        journal.Undo();
        journal.Undo();

        Assert.Null(track.Keyframes.Single().Curve.Points);
    }

    [Fact]
    public void OpenEndedCueCanExtendItsAbsoluteTimeRuler()
    {
        var track = new AutomationTrack
        {
            Target = new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
        };
        var media = new MediaCueNode { Number = "13", MediaPath = "ndi://camera", AutomationTracks = [track] };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [media] }] };
        var editor = new AutomationEditorViewModel(new ProjectJournal(project), media, track, duration: null);
        var initial = editor.DurationMs;

        Assert.True(editor.CanExtend);
        editor.Extend();

        Assert.Equal(initial + (long)TimeSpan.FromMinutes(30).TotalMilliseconds, editor.DurationMs);
    }

    [Fact]
    public void WaveformIsTrimmedToCueTimeAndThenProjectedThroughTheViewport()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-automation-waveform");
        var mediaPath = Path.Combine(directory.FullName, "long.wav");
        try
        {
            File.WriteAllBytes(mediaPath, [1, 2, 3, 4]);
            var raw = Enumerable.Range(0, 100).Select(index => index / 100f).ToArray();
            WaveformCache.Write(directory.FullName, mediaPath, raw);

            var track = new AutomationTrack
            {
                Target = new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
            };
            var media = new MediaCueNode
            {
                Number = "14",
                MediaPath = mediaPath,
                TrimInMs = 25_000,
                TrimOutMs = 75_000,
                AutomationTracks = [track],
            };
            var project = new HaCueProject { CueLists = [new CueList { Cues = [media] }] };
            var editor = new AutomationEditorViewModel(
                new ProjectJournal(project),
                media,
                track,
                TimeSpan.FromSeconds(50),
                waveformCue: media,
                waveformPath: mediaPath,
                waveformSourceDuration: TimeSpan.FromSeconds(100),
                cacheRoot: directory.FullName);

            editor.BeginWaveform();

            Assert.Equal(30, editor.WaveformPeaks?.Count);
            Assert.Equal(.25f, editor.WaveformPeaks![0], 3);

            editor.ViewStartMs = 20_000;
            editor.ViewLengthMs = 10_000;

            Assert.Equal(10, editor.WaveformPeaks?.Count);
            Assert.Equal(.45f, editor.WaveformPeaks![0], 3);
            editor.EndWaveform();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static (AutomationEditorViewModel Editor, AutomationTrack Track, ProjectJournal Journal) Editor(
        params AutomationKeyframe[] keys)
    {
        var track = new AutomationTrack
        {
            Target = new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
            Keyframes = [.. keys],
        };
        var media = new MediaCueNode
        {
            Number = "12",
            Label = "Long ambience",
            SourceDurationMs = 45 * 60 * 1000,
            AutomationTracks = [track],
        };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [media] }] };
        var journal = new ProjectJournal(project);
        return (new AutomationEditorViewModel(journal, media, track, TimeSpan.FromMinutes(45)), track, journal);
    }
}
