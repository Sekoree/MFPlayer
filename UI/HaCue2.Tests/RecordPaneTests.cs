using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The record pane edits the document (register item 30).
/// </summary>
/// <remarks>
/// The pane was drawn complete — Directory, Pattern, an insert-token dropdown, a format line — with
/// every value a literal in the markup and every button inert. It looked finished from the screenshot
/// and edited nothing, which is the fifth time on this branch that a fully drawn surface turned out to
/// be wired to nothing. These are the assertions that tell the two apart.
/// </remarks>
public class RecordPaneTests
{
    private static (AudioViewModel Audio, AudioLineDefinition Line) WithRecordLine(
        ShellViewModel shell, AudioLineKind kind = AudioLineKind.FileRecord)
    {
        var line = new AudioLineDefinition { Name = "Archive", Kind = kind, Channels = 2 };
        shell.Project.AudioLines.Add(line);

        var audio = new AudioViewModel(shell.Journal, shell.Runtime);
        audio.SelectedLine = audio.Lines.Single(row => row.Id == line.Id);

        return (audio, line);
    }

    [Fact]
    public Task ThePaneOnlyAppliesToRecordAndStreamLines() => ShellFixture.WithShell(shell =>
    {
        var (audio, _) = WithRecordLine(shell, AudioLineKind.LocalAudio);
        Assert.False(audio.Record.IsRecording);

        var (record, _) = WithRecordLine(shell);
        Assert.True(record.Record.IsRecording);
        Assert.False(record.Record.IsStream);
    });

    [Fact]
    public Task TypingAPatternReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (audio, line) = WithRecordLine(shell);

        audio.Record.Pattern = "act-{n}.mka";

        Assert.Equal("act-{n}.mka", line.Record!.Pattern);
    });

    [Fact]
    public Task TypingADirectoryReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (audio, line) = WithRecordLine(shell);

        audio.Record.Directory = "/tmp/shows";

        Assert.Equal("/tmp/shows", line.Record!.Directory);
    });

    [Fact]
    public Task EveryRecordEditIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var (audio, line) = WithRecordLine(shell);

        audio.Record.Pattern = "act-{n}.mka";
        shell.Undo();

        // Changing where a show records is exactly the sort of edit somebody makes by accident on the
        // wrong line and needs back.
        Assert.NotEqual("act-{n}.mka", line.Record?.Pattern);
    });

    [Fact]
    public Task TheModeSegmentPicksArchiveOrReel() => ShellFixture.WithShell(shell =>
    {
        var (audio, line) = WithRecordLine(shell);

        // Content-only is the default: it collapses idle time, which is what somebody recording a few
        // cues expects. Continuous is the deliberate choice for an archive.
        Assert.Equal(1, audio.Record.ModeIndex);

        audio.Record.ModeIndex = 0;
        Assert.True(line.Record!.Continuous);

        audio.Record.ModeIndex = 1;
        Assert.False(line.Record.Continuous);
    });

    [Fact]
    public Task ArmWithShowReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (audio, line) = WithRecordLine(shell);

        audio.Record.ArmWithShow = true;

        Assert.True(line.Record!.ArmWithShow);
    });

    [Fact]
    public Task InsertingATokenPutsItBeforeTheExtension() => ShellFixture.WithShell(shell =>
    {
        var (audio, line) = WithRecordLine(shell);
        audio.Record.Pattern = "show.mka";

        audio.Record.InsertToken("{n}");

        // Appending after ".mka" would produce "show.mka{n}", which names no format at all and would be
        // refused on the next arm.
        Assert.Equal("show{n}.mka", line.Record!.Pattern);
    });

    [Fact]
    public Task EveryOfferedTokenCanBeInserted() => ShellFixture.WithShell(shell =>
    {
        var (audio, _) = WithRecordLine(shell);
        audio.Record.Pattern = "show.mka";

        foreach (var token in audio.Record.Tokens)
            audio.Record.InsertToken(token.Token);

        // The dropdown's list and the expander's are the same list, so anything offered here expands.
        Assert.DoesNotContain("{", audio.Record.Preview, StringComparison.Ordinal);
    });

    [Fact]
    public Task ThePreviewShowsTheNameThatWillBeWritten() => ShellFixture.WithShell(shell =>
    {
        var (audio, _) = WithRecordLine(shell);
        shell.Project.Title = "Hamlet";

        audio.Record.Pattern = "{project}-{n}.mka";

        Assert.Equal("Hamlet-1.mka", audio.Record.Preview);
    });

    [Fact]
    public Task AnUnwritableExtensionIsCalledOutInThePane() => ShellFixture.WithShell(shell =>
    {
        var (audio, _) = WithRecordLine(shell);

        audio.Record.Pattern = "show.flac";

        // Said HERE, while they are typing it, rather than at arm time when there is no room to fix it.
        Assert.True(audio.Record.HasProblem);
        Assert.Contains(".mka", audio.Record.Problem, StringComparison.Ordinal);
    });

    [Fact]
    public Task TheFormatLineNamesTheCodecsTheExtensionChose() => ShellFixture.WithShell(shell =>
    {
        var (audio, _) = WithRecordLine(shell);

        audio.Record.Pattern = "show.mka";

        // Was a hardcoded "FLAC · 44 100 · 2ch" that no project could change.
        Assert.Contains("FLAC", audio.Record.FormatSummary, StringComparison.Ordinal);
        Assert.Contains("2ch", audio.Record.FormatSummary, StringComparison.Ordinal);
    });

    [Fact]
    public Task AStreamAsksForAUrlRatherThanAFolder() => ShellFixture.WithShell(shell =>
    {
        var (audio, line) = WithRecordLine(shell, AudioLineKind.Stream);

        Assert.True(audio.Record.IsStream);

        audio.Record.Url = "rtmp://example/app/key";
        Assert.Equal("rtmp://example/app/key", line.Record!.Url);

        // A stream's container follows its protocol, so there is no format for a pattern to choose.
        Assert.Contains("protocol", audio.Record.FormatSummary, StringComparison.Ordinal);
    });

    [Fact]
    public Task SelectingAnotherLineShowsThatLinesRecording() => ShellFixture.WithShell(shell =>
    {
        var first = new AudioLineDefinition { Name = "A", Kind = AudioLineKind.FileRecord };
        var second = new AudioLineDefinition { Name = "B", Kind = AudioLineKind.FileRecord };
        shell.Project.AudioLines.Add(first);
        shell.Project.AudioLines.Add(second);

        var audio = new AudioViewModel(shell.Journal, shell.Runtime);

        audio.SelectedLine = audio.Lines.Single(row => row.Id == first.Id);
        audio.Record.Pattern = "a.mka";

        audio.SelectedLine = audio.Lines.Single(row => row.Id == second.Id);
        audio.Record.Pattern = "b.mka";

        // The pane follows the selection rather than editing whichever line it first saw — the bug that
        // would put every recording in the show under one filename.
        Assert.Equal("a.mka", first.Record!.Pattern);
        Assert.Equal("b.mka", second.Record!.Pattern);
    });

    // ── the video side uses the same pane ──────────────────────────────────────────────────────

    [Fact]
    public Task AVideoRecordOutputGetsTheSamePane() => ShellFixture.WithShell(shell =>
    {
        var output = new VideoOutputDefinition { Name = "Capture", Kind = VideoOutputKind.Record };
        shell.Project.VideoOutputs.Add(output);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedOutput = video.Outputs.Single(row => row.Id == output.Id);

        Assert.True(video.Record.IsRecording);

        video.Record.Pattern = "capture-{n}.mkv";
        Assert.Equal("capture-{n}.mkv", output.Record!.Pattern);
    });

    [Fact]
    public Task AScreenOutputHasNoRecordPane() => ShellFixture.WithShell(shell =>
    {
        var output = new VideoOutputDefinition { Name = "Projector", Kind = VideoOutputKind.LocalScreen };
        shell.Project.VideoOutputs.Add(output);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedOutput = video.Outputs.Single(row => row.Id == output.Id);

        Assert.False(video.Record.IsRecording);
    });

    [Fact]
    public Task AVideoOutputIsJudgedOnVideoRules() => ShellFixture.WithShell(shell =>
    {
        var output = new VideoOutputDefinition { Name = "Capture", Kind = VideoOutputKind.Record };
        shell.Project.VideoOutputs.Add(output);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedOutput = video.Outputs.Single(row => row.Id == output.Id);

        // .mka is right for a record LINE and wrong for an output — the same editor, told what it is
        // recording, has to reach opposite conclusions about the same extension.
        video.Record.Pattern = "capture.mka";

        Assert.True(video.Record.HasProblem);
        Assert.Contains("audio only", video.Record.Problem, StringComparison.Ordinal);
    });
}
