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

    /// <summary>Copy/paste must carry NATIVE milliseconds. It used to normalize through the source cue's
    /// DurationMs and re-multiply by the destination's, so keys 500 ms apart copied out of a 45-minute cue
    /// collapsed to ~5 ms when pasted into a 30-second one - re-introducing the very defect absolute-time
    /// automation exists to remove.</summary>
    [Fact]
    public void CopiedKeyframesKeepTheirMillisecondSpacingInAMuchShorterCue()
    {
        var first = new AutomationKeyframe { TimeMs = 1_635_000, Value = -6 };
        var second = new AutomationKeyframe { TimeMs = 1_635_500, Value = -12 };
        var (source, _, _) = Editor(first, second);
        source.SelectAll();
        var text = source.Copy();
        Assert.NotNull(text);

        var (destination, shortTrack, _) = ShortEditor();
        destination.CursorMs = 2_000;
        Assert.True(destination.Paste(text));

        // 500 ms apart before, 500 ms apart after - anchored at the playhead, not rescaled.
        Assert.Equal([2_000L, 2_500L], shortTrack.Keyframes.OrderBy(key => key.TimeMs).Select(key => key.TimeMs));
        // Native dB values survive too; they are not remapped through the descriptor's range.
        Assert.Equal([-6d, -12d], shortTrack.Keyframes.OrderBy(key => key.TimeMs).Select(key => key.Value));
    }

    /// <summary>A refused add (a key already occupies that time) must not report acceptance: the canvas
    /// takes capture on acceptance alone, and an unconditional "accept" armed a sentinel that resolved to
    /// the PREVIOUS selection - so pressing empty canvas near an existing key dragged an unrelated key.</summary>
    [Fact]
    public void ARefusedAddIsNotReportedAsAccepted()
    {
        var existing = new AutomationKeyframe { TimeMs = 1_635_100, Value = -6 };
        var (editor, track, _) = Editor(existing);
        editor.ViewStartMs = 1_635_000;
        editor.ViewLengthMs = 1_000;

        // Same snap cell as the existing key, but well away from it vertically.
        var refused = new CurveGesture(CurveGestureKind.Add, -1, 0.1, 0.9);
        editor.Apply(refused);
        Assert.False(refused.Accepted);
        Assert.Single(track.Keyframes);

        var accepted = new CurveGesture(CurveGestureKind.Add, -1, 0.5, 0.5);
        editor.Apply(accepted);
        Assert.True(accepted.Accepted);
        editor.EndGesture();
        Assert.Equal(2, track.Keyframes.Count);
    }

    /// <summary>Shortening a cue never rescales its keys, so keys past the out-point are legitimate and
    /// preserved - but the ruler stops at the out-point, so they were invisible AND unreachable. They must
    /// be reported and removable by an explicit command.</summary>
    [Fact]
    public void KeysPastTheCueEndAreReportedAndRemovableByAnExplicitCommand()
    {
        var inside = new AutomationKeyframe { TimeMs = 10_000, Value = -6 };
        var beyond = new AutomationKeyframe { TimeMs = 40_000, Value = -12 };
        var (editor, track, journal) = ShortEditor(inside, beyond);   // 30 s cue

        Assert.True(editor.HasOutOfRangeKeys);
        Assert.Equal(1, editor.OutOfRangeKeyCount);
        Assert.Contains("never play", editor.OutOfRangeLabel, StringComparison.Ordinal);

        Assert.True(editor.DeleteOutOfRangeKeys());
        Assert.Equal([inside.Id], track.Keyframes.Select(key => key.Id));
        Assert.False(editor.HasOutOfRangeKeys);

        // One undo step, and the preserved key comes back.
        journal.Undo();
        Assert.Equal(2, track.Keyframes.Count);
    }

    /// <summary>A gesture that changes nothing must not journal an undo step or dirty the project - a
    /// refused add used to push a command whose before and after were identical, so the operator's next
    /// Undo appeared to do nothing.</summary>
    [Fact]
    public void AGestureThatChangesNothingJournalsNothing()
    {
        var existing = new AutomationKeyframe { TimeMs = 1_635_100, Value = -6 };
        var (editor, _, journal) = Editor(existing);
        editor.ViewStartMs = 1_635_000;
        editor.ViewLengthMs = 1_000;

        var refused = new CurveGesture(CurveGestureKind.Add, -1, 0.1, 0.9);
        editor.Apply(refused);
        editor.EndGesture();

        Assert.False(refused.Accepted);
        Assert.Empty(journal.Log);
        Assert.False(journal.IsDirty);
    }

    private static (AutomationEditorViewModel Editor, AutomationTrack Track, ProjectJournal Journal) ShortEditor(
        params AutomationKeyframe[] keys)
    {
        var track = new AutomationTrack
        {
            Target = new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
            Keyframes = [.. keys],
        };
        var media = new MediaCueNode { SourceDurationMs = 30_000, AutomationTracks = [track] };
        var project = new HaCueProject { CueLists = [new CueList { Cues = [media] }] };
        var journal = new ProjectJournal(project);
        return (new AutomationEditorViewModel(journal, media, track, TimeSpan.FromSeconds(30)), track, journal);
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
