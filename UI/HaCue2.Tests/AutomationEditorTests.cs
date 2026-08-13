using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
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

    /// <summary>
    /// The zoom box sets the visible span, and follows − and + rather than going stale.
    /// </summary>
    /// <remarks>
    /// There was no zoom box. The only dropdown on that toolbar was the TIME SNAP list, sitting between the
    /// zoom keys and the playhead with nothing to say which it was - so it read as the zoom level, did not
    /// move when the zoom keys were pressed, and zoomed nothing when it was changed.
    /// </remarks>
    [Fact]
    public void TheZoomBoxSetsTheVisibleSpanAndFollowsTheZoomKeys()
    {
        var (editor, _, _) = Editor(); // a 45-minute cue
        editor.CursorMs = 10 * 60 * 1000;

        var tenMinutes = editor.ZoomOptions.Single(choice => choice.Label == "10 min");
        editor.ZoomSpan = tenMinutes;
        Assert.Equal(10 * 60 * 1000d, editor.ViewLengthMs);

        // Zooming from the box keeps the cursor where it was on screen, like the keys do.
        Assert.InRange(editor.CursorMs, editor.ViewStartMs, editor.ViewStartMs + editor.ViewLengthMs);

        // And the keys move the box, which is the half that was missing: a multiplied span landed on values
        // no entry names, so any use of − or + would have left it blank.
        editor.Zoom(0.5);
        Assert.Equal("5 min", editor.ZoomSpan?.Label);
        editor.Zoom(2);
        Assert.Equal("10 min", editor.ZoomSpan?.Label);

        editor.Fit();
        Assert.Equal("whole cue", editor.ZoomSpan?.Label);

        // The ladder is the cue's, not a fixed one: nothing wider than the cue is offered, and the widest
        // entry IS the cue.
        Assert.All(editor.ZoomOptions, choice => Assert.True(choice.LengthMs <= editor.DurationMs));
        Assert.Equal(editor.DurationMs, editor.ZoomOptions[0].LengthMs);
    }

    [Fact]
    public Task TheEditorHasSeparateBoxesForZoomAndSnap() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var (editor, _, _) = Editor();
            var window = new AutomationEditorWindow(editor);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                var zoom = window.GetVisualDescendants().OfType<ComboBox>().Single(box =>
                    AutomationProperties.GetName(box) == "Visible span");
                var snap = window.GetVisualDescendants().OfType<ComboBox>().Single(box =>
                    AutomationProperties.GetName(box) == "Time snap in milliseconds");

                // Both show a value. A blank zoom box would be the old failure wearing new paint.
                Assert.NotNull(zoom.SelectedItem);
                Assert.NotNull(snap.SelectedItem);

                editor.Zoom(0.5);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(editor.ZoomSpan, zoom.SelectedItem);
                Assert.Equal(editor.SnapTimeMs, snap.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>
    /// The selected keyframe is drawn differently from the rest.
    /// </summary>
    /// <remarks>
    /// It was not. The template set <c>Classes.warn</c> on the dot, and no style in the app targets a warn
    /// Ellipse or Rectangle - <c>.warn</c> is TextBlock, Border.chip and Ellipse.led - so the class matched
    /// nothing and every key drew the same congo dot. With the fields on the right editing one key and no
    /// way to see which, the only way to find out was to move it and watch.
    /// </remarks>
    [Fact]
    public Task TheSelectedKeyframeIsMarkedOnTheCanvas() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var (editor, _, _) = Editor(
                new AutomationKeyframe { TimeMs = 1_000, Value = 0.2 },
                new AutomationKeyframe { TimeMs = 5_000, Value = 0.8 });
            var window = new AutomationEditorWindow(editor);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                editor.Fit();
                editor.CursorMs = 1_000;
                editor.JumpKey(1); // lands on the 5 s key and selects exactly that one
                Dispatcher.UIThread.RunJobs();

                var selected = Assert.Single(editor.Points, point => point.IsSelected);
                Assert.True(selected.IsSelectedDot);
                Assert.False(selected.IsPlainDot);

                // The whole point is that it draws differently, so assert on the pixels' worth of visual
                // tree rather than on the flag the old version also had.
                var canvas = Assert.Single(window.GetVisualDescendants().OfType<CurveCanvas>());
                var amber = window.FindResource("GelAmber") as IBrush;
                Assert.NotNull(amber);

                var marks = canvas.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Shapes.Shape>()
                    .Where(shape => shape.IsVisible && Equals(shape.Fill, amber))
                    .ToList();

                Assert.Single(marks);
                Assert.Contains(
                    canvas.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>(),
                    ring => ring.IsVisible && Equals(ring.Stroke, amber));
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>
    /// Enter in the key-time and value boxes moves the key, without waiting for focus to go somewhere else.
    /// </summary>
    /// <remarks>
    /// They committed on LostFocus alone, so typing a time and pressing Enter did nothing at all - and the
    /// nearest place to click to make it commit is the canvas, which selects a different key first.
    /// </remarks>
    [Fact]
    public Task TypingAKeyTimeAndPressingEnterMovesTheKey() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var key = new AutomationKeyframe { TimeMs = 1_000, Value = 0.25 };
            var (editor, track, _) = Editor(key);
            var window = new AutomationEditorWindow(editor);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                editor.SelectAll();
                Dispatcher.UIThread.RunJobs();

                Commit(window, "Selected keyframe time", "0:04.000");
                Assert.Equal(4_000, Assert.Single(track.Keyframes).TimeMs);

                // And the canvas followed - a field that edits the document but not the picture is only
                // half working.
                var height = Assert.Single(editor.Points).Y;

                Commit(window, "Selected keyframe value", "0.75");
                Assert.Equal(0.75, Assert.Single(track.Keyframes).Value, 3);
                Assert.NotEqual(height, Assert.Single(editor.Points).Y, 3);
            }
            finally
            {
                window.Close();
            }
        });

    private static void Commit(Window window, string automationName, string text)
    {
        var box = window.GetVisualDescendants().OfType<TextBox>()
            .Single(item => AutomationProperties.GetName(item) == automationName);
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The editor says what the lane produces at the cursor, live, with nothing playing.
    /// </summary>
    /// <remarks>
    /// There was no way to read a ramp without firing the cue - the plot did not even show where the
    /// cursor was, though the slider set it and keys were added at it. Sampled through the same evaluator
    /// the drivers use, so this is the value the show will send and not an approximation of it.
    /// </remarks>
    [Fact]
    public void TheEditorReadsTheLaneAtTheCursorWithoutPlayingAnything()
    {
        var (editor, track, _) = ShortEditor(
            new AutomationKeyframe { TimeMs = 0, Value = 0 },
            new AutomationKeyframe { TimeMs = 10_000, Value = -20 });

        editor.Fit();
        editor.CursorMs = 5_000;
        Assert.Equal("-10 dB", editor.CursorValueLabel);

        // Half way across a 30-second view.
        Assert.Equal(1 / 6d, editor.CursorFraction, 3);

        editor.JumpKey(1);   // selects the key at 10 s
        editor.CursorMs = 7_500;
        Assert.Equal("-15 dB", editor.CursorValueLabel);

        // And it follows the KEYS, not only the cursor: moving the ramp's end changes what the lane
        // produces where the operator is standing, and a readout that ignored that would describe the
        // curve as it was before the edit.
        editor.CommitPointValue("-40");

        Assert.Equal(-40, track.Keyframes.Single(key => key.TimeMs == 10_000).Value, 3);
        Assert.Equal(7_500, editor.CursorMs);
        Assert.Equal("-30 dB", editor.CursorValueLabel);
    }

    [Fact]
    public Task ThePlotDrawsTheCursorAndDropsItWhenItScrollsOutOfView() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var (editor, _, _) = Editor(); // a 45-minute cue
            var window = new AutomationEditorWindow(editor);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            try
            {
                var canvas = Assert.Single(window.GetVisualDescendants().OfType<CurveCanvas>());

                editor.ViewStartMs = 0;
                editor.ViewLengthMs = 60_000;
                editor.CursorMs = 30_000;
                Dispatcher.UIThread.RunJobs();

                Assert.True(canvas.HasCursor);
                Assert.Equal(0.5, canvas.CursorFraction, 3);

                // Scrolled past: no line, rather than one pinned to an edge it is not at.
                editor.ViewStartMs = 600_000;
                Dispatcher.UIThread.RunJobs();

                Assert.False(canvas.HasCursor);
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

    /// <summary>
    /// Zooming into a long cue rescans the visible stretch instead of stretching four coarse bars.
    /// </summary>
    /// <remarks>
    /// The whole-file pass SAMPLES anything past twelve minutes and caps itself at a thousand buckets, so
    /// a two-hour cue is one bar every seven seconds. The viewport was a slice of that array and nothing
    /// more - a thirty-second view had four bars to draw, which is the "bunch of blocks" a two-hour test
    /// cue showed. The rescan reads only what is on screen, so its cost is the window's length and not
    /// the file's.
    /// </remarks>
    [Fact]
    public async Task ZoomingIntoALongCueRescansTheVisibleStretchAtItsOwnResolution()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-automation-detail");
        var mediaPath = Path.Combine(directory.FullName, "twohours.wav");
        try
        {
            File.WriteAllBytes(mediaPath, [1, 2, 3, 4]);

            // What the sampled whole-file pass produces for two hours: 1000 buckets, 7.2 s apiece.
            var raw = Enumerable.Range(0, 1_000).Select(_ => 0.5f).ToArray();
            WaveformCache.Write(directory.FullName, mediaPath, raw);

            var asked = new List<(TimeSpan From, TimeSpan Length)>();
            var track = new AutomationTrack
            {
                Target = new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
            };
            var media = new MediaCueNode
            {
                MediaPath = mediaPath,
                TrimInMs = 60_000,
                AutomationTracks = [track],
            };
            var project = new HaCueProject { CueLists = [new CueList { Cues = [media] }] };
            var editor = new AutomationEditorViewModel(
                new ProjectJournal(project),
                media,
                track,
                TimeSpan.FromHours(2),
                waveformCue: media,
                waveformPath: mediaPath,
                waveformSourceDuration: TimeSpan.FromHours(2),
                cacheRoot: directory.FullName,
                windowScan: (_, from, length, buckets, _) =>
                {
                    asked.Add((from, length));
                    return Task.FromResult<float[]?>([.. Enumerable.Repeat(1f, buckets)]);
                });

            editor.BeginWaveform();
            editor.ViewStartMs = 600_000;
            editor.ViewLengthMs = 30_000;

            // The coarse slice first, because a rescan takes time and a blank plot in the meantime would
            // be worse than a rough one.
            Assert.InRange(editor.WaveformPeaks!.Count, 1, 10);

            for (var attempt = 0; attempt < 100 && asked.Count == 0; attempt++)
                await Task.Delay(20);

            var (from, length) = Assert.Single(asked);

            // Source time, not cue time: the cue starts a minute into the file.
            Assert.Equal(TimeSpan.FromMilliseconds(660_000), from);
            Assert.Equal(TimeSpan.FromSeconds(30), length);

            for (var attempt = 0; attempt < 100 && editor.WaveformPeaks!.Count <= 10; attempt++)
                await Task.Delay(20);

            Assert.True(editor.WaveformPeaks!.Count > 100, "the rescan never reached the plot");

            // Rescaled to what the coarse pass said about this stretch. The rescan normalizes within its
            // own window, so without this a quiet passage would jump to full height on being zoomed into.
            Assert.Equal(0.5f, editor.WaveformPeaks![0], 3);

            editor.EndWaveform();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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

    /// <summary>The inspector's static Level field must not look authoritative while a track drives the
    /// cue. Off air it names the track and its range; while the cue sounds it shows the design's
    /// "base … · automated now …" using the value the engine poll pushes in.</summary>
    [Fact]
    public void TheInspectorSaysWhatAutomationIsDoingToTheLevel()
    {
        var track = new AutomationTrack
        {
            Target = new AutomationTargetRef { PropertyId = AutomationPropertyIds.CueVolume },
            Keyframes =
            [
                new AutomationKeyframe { TimeMs = 0, Value = 0 },
                new AutomationKeyframe { TimeMs = 5_000, Value = -20 },
            ],
        };
        var media = new MediaCueNode { MediaPath = "a.wav", LevelDb = -6, AutomationTracks = [track] };
        var project = new HaCueProject { CueLists = [new CueList { Name = "Main", Cues = [media] }] };
        var inspector = new InspectorViewModel(new ProjectJournal(project));
        inspector.Show([media.Id]);

        // Off air: the operator is told a track owns this value, and over what range.
        Assert.True(inspector.LevelIsAutomated);
        Assert.Contains("the value above is the base", inspector.LevelAutomationNote, StringComparison.Ordinal);

        // Sounding: the engine poll supplies what automation has actually reached.
        inspector.LiveAutomatedVolumeDb = -12;
        Assert.Contains("automated now", inspector.LevelAutomationNote, StringComparison.Ordinal);
        Assert.Contains("12.0", inspector.LevelAutomationNote, StringComparison.Ordinal);

        // A cue with no track and nothing driving it says nothing at all.
        var plain = new MediaCueNode { MediaPath = "b.wav", LevelDb = -6 };
        project.CueLists[0].Cues.Add(plain);
        inspector.Show([plain.Id]);
        Assert.False(inspector.LevelIsAutomated);
        Assert.Equal("", inspector.LevelAutomationNote);
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
