using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The clip editor, driven the way a pointer drives it.
/// </summary>
/// <remarks>
/// The window exists for one sentence — "trim thirty minutes off the front and ten off the end of a
/// two-hour recording" — so that sentence is what is asserted here, against the DOCUMENT rather than
/// against the view-model's own fields.
/// </remarks>
public class ClipEditorTests
{
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private static (ClipEditorViewModel Editor, MediaCueNode Cue, ProjectJournal Journal) Editor(
        bool probed = true)
    {
        var cue = new MediaCueNode { Number = "1", Label = "Concert", MediaPath = "concert.mov" };
        var project = new HaCueProject { CueLists = [new CueList { Name = "Act 1", Cues = [cue] }] };
        var journal = new ProjectJournal(project);

        // No path: nothing opens a file, so the scan and the frame grab stay out of the way and the
        // trim logic is what is under test.
        return (
            new ClipEditorViewModel(journal, cue, "", probed ? TwoHours : null, ""), cue, journal);
    }

    [Fact]
    public Task TheWindowRendersAndBindsToItsEditor() => ShellFixture.WithShell(_ =>
    {
        var (editor, _, _) = Editor();

        var window = new ClipEditorWindow(editor);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The waveform is the control the whole window is built around; markup that failed to load it
        // would compile cleanly and leave a blank sheet that still passed every view-model test.
        Assert.NotNull(window.GetVisualDescendants().OfType<WaveformGraph>().FirstOrDefault());

        window.Close();
    });

    [Fact]
    public void ThirtyMinutesOffTheFrontIsTypedAsThirtyMinutes()
    {
        var (editor, cue, _) = Editor();

        Assert.Null(editor.SetTrimIn("30:00"));

        // It used to be 1800.0. That is the whole reason this exists.
        Assert.Equal(1_800_000, cue.TrimInMs);
        Assert.Equal("30:00", editor.TrimInText);
    }

    [Fact]
    public void TenMinutesOffTheEndIsTypedAsTenMinutesOffTheEnd()
    {
        var (editor, cue, _) = Editor();

        Assert.Null(editor.SetTrimOut("-10:00"));

        // The document stores an absolute out-point, so this is 2:00:00 − 10:00. The operator no longer
        // does that subtraction, and no longer has to find the file's length to do it against.
        Assert.Equal(6_600_000, cue.TrimOutMs);
        Assert.Equal("1:50:00", editor.TrimOutText);
    }

    [Fact]
    public void TheWindowReportsWhatWillActuallyPlay()
    {
        var (editor, _, _) = Editor();

        editor.SetTrimIn("30:00");
        editor.SetTrimOut("-10:00");

        // Two hours, less half an hour, less ten minutes. The number the operator is aiming at, stated
        // rather than left to be inferred from two other numbers.
        Assert.Equal("1:20:00 of 2:00:00", editor.KeptLabel);
    }

    [Fact]
    public void AnOutPointBeforeTheInPointIsRefusedRatherThanAccepted()
    {
        var (editor, cue, _) = Editor();

        editor.SetTrimIn("30:00");
        var refusal = editor.SetTrimOut("10:00");

        Assert.NotNull(refusal);
        // Unchanged: a window whose ends crossed is a cue that plays nothing, found by the validator
        // long after the keystroke that caused it.
        Assert.Equal(0, cue.TrimOutMs);
    }

    [Fact]
    public void TypedTrimPointsOutsideTheFileAreRefused()
    {
        var (editor, cue, _) = Editor();

        Assert.NotNull(editor.SetTrimIn("2:00:00"));
        Assert.NotNull(editor.SetTrimOut("2:00:01"));

        Assert.Equal(0, cue.TrimInMs);
        Assert.Equal(0, cue.TrimOutMs);
    }

    [Fact]
    public void AMistypedTimeIsRefusedAndChangesNothing()
    {
        var (editor, cue, _) = Editor();

        var refusal = editor.SetTrimIn("half past");

        Assert.NotNull(refusal);
        Assert.Contains("not a time", refusal, StringComparison.Ordinal);
        Assert.Equal(0, cue.TrimInMs);
    }

    [Fact]
    public void DraggingAHandleWritesTheDocumentAsOneUndoStep()
    {
        var (editor, cue, journal) = Editor();

        // A drag is a stream of moves, exactly as the control raises them.
        editor.Apply(TrimHandle.In, 0.10);
        editor.Apply(TrimHandle.In, 0.20);
        editor.Apply(TrimHandle.In, 0.25);
        editor.EndGesture();

        Assert.Equal(1_800_000, cue.TrimInMs);

        journal.Undo();

        // ONE step for the whole drag. Three would mean an operator pressing undo once still had a
        // trim they did not ask for.
        Assert.Equal(0, cue.TrimInMs);
    }

    [Fact]
    public void DraggingEitherTrimHandleMovesThePreviewToThatBoundary()
    {
        var (editor, _, _) = Editor();

        editor.Apply(TrimHandle.In, 0.25);
        Assert.Equal("30:00", editor.PlayheadLabel);
        editor.EndGesture();

        editor.Apply(TrimHandle.Out, 0.75);
        Assert.Equal("1:30:00", editor.PlayheadLabel);
        editor.EndGesture();
    }

    [Fact]
    public Task HoveringAHandleAndTheScrubAreaUsesDifferentCursors() => ShellFixture.WithShell(_ =>
    {
        var (editor, _, _) = Editor();
        editor.Peaks = [1, 1, 1, 1];
        var window = new ClipEditorWindow(editor);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var graph = window.GetVisualDescendants().OfType<WaveformGraph>().Single();
            var origin = graph.TranslatePoint(default, window)!.Value;
            var y = origin.Y + (graph.Bounds.Height / 2);

            window.MouseMove(new Point(origin.X + 2, y));
            Dispatcher.UIThread.RunJobs();
            var trimCursor = graph.Cursor;

            window.MouseMove(new Point(origin.X + (graph.Bounds.Width / 2), y));
            Dispatcher.UIThread.RunJobs();
            var scrubCursor = graph.Cursor;

            window.MouseMove(new Point(origin.X + graph.Bounds.Width - 2, y));
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(trimCursor);
            Assert.NotNull(scrubCursor);
            Assert.NotSame(trimCursor, scrubCursor);
            Assert.Same(trimCursor, graph.Cursor);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AHandleCannotBeDraggedPastTheOtherOne()
    {
        var (editor, cue, _) = Editor();

        editor.SetTrimOut("30:00");
        editor.Apply(TrimHandle.In, 0.9);
        editor.EndGesture();

        // Clamped to just inside the out-point rather than crossing it.
        Assert.True(cue.TrimInMs < cue.TrimOutMs, $"{cue.TrimInMs} is not before {cue.TrimOutMs}");
    }

    [Fact]
    public void ScrubbingMovesThePlayheadWithoutTouchingTheTrim()
    {
        var (editor, cue, _) = Editor();

        editor.Apply(TrimHandle.Playhead, 0.5);

        Assert.Equal("1:00:00", editor.PlayheadLabel);
        // A scrub is looking, not editing. Moving a trim point by clicking to look at something would
        // be the worst kind of surprise in this window.
        Assert.Equal(0, cue.TrimInMs);
        Assert.Equal(0, cue.TrimOutMs);
    }

    [Fact]
    public void AFileNobodyHasProbedSaysSoRatherThanDrawingAWindowItCannotPlace()
    {
        var (editor, cue, _) = Editor(probed: false);

        Assert.False(editor.IsProbed);
        Assert.Equal("not probed", editor.LengthLabel);

        // A from-the-end time cannot be resolved without a length, and a handle cannot be placed.
        Assert.NotNull(editor.SetTrimOut("-10:00"));
        editor.Apply(TrimHandle.In, 0.5);
        Assert.Equal(0, cue.TrimInMs);
    }
}
