using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Media.Core.Video;
using S.Media.Time;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Arming a recording, end to end.
/// </summary>
/// <remarks>
/// <para>
/// This writes real files with a real encoder. The cheaper alternative — asserting that arming built
/// the right options object — would have passed for every mistake worth catching here: a container that
/// refuses its codec, a path that could not be created, a wrapper that never forwarded a frame. A
/// recording either produces a file somebody can open or it does not.
/// </para>
/// <para>
/// Video is the side under test because it can be armed without an audio device; the audio side's own
/// path into the bay is exercised where the bay is (<see cref="ProjectPatchBay.AttachRecorder"/>).
/// </para>
/// </remarks>
public sealed class RecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"hacue2_rec_{Guid.NewGuid():N}");

    private static VideoFrame Frame(int width, int height, int index)
    {
        var fps = new Rational(25, 1);
        var stride = width * 4;
        var bytes = new byte[stride * height];

        for (var offset = 3; offset < bytes.Length; offset += 4)
        {
            bytes[offset - 3] = (byte)(index * 8 & 0xFF);
            bytes[offset] = 255;
        }

        return new VideoFrame(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond * index / 25),
            new VideoFormat(width, height, PixelFormat.Bgra32, fps),
            [bytes],
            [stride]);
    }

    /// <summary>A project with one record output on one composition, plus the pieces to arm it.</summary>
    private (ProjectRecorders Recorders, HaCueProject Project, VideoOutputDefinition Output, RecordVideoOutput Sink)
        Show(string pattern, VideoOutputKind kind = VideoOutputKind.Record, string url = "", bool continuous = false)
    {
        var composition = new CompositionDefinition { Name = "Main", Width = 160, Height = 120, FramesPerSecond = 25 };
        var output = new VideoOutputDefinition
        {
            Name = "Capture",
            Kind = kind,
            CompositionId = composition.Id,
            Record = new RecordTarget
            {
                Directory = _folder, Pattern = pattern, Url = url, Continuous = continuous,
            },
        };

        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };
        var sink = new RecordVideoOutput();

        var recorders = new ProjectRecorders(
            project, bay: null, new Dictionary<Guid, RecordVideoOutput> { [output.Id] = sink }, _folder);

        return (recorders, project, output, sink);
    }

    [RecordingFact]
    public async Task ArmingWritesAFileAndDisarmingFinishesIt()
    {
        var (recorders, _, output, sink) = Show("act-{n}.mkv");

        Assert.Null(recorders.Arm(output.Id));
        Assert.True(recorders.IsArmed(output.Id));

        var path = recorders.Status().Single().Destination;
        Assert.NotNull(path);
        Assert.Equal("act-1.mkv", Path.GetFileName(path));

        sink.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, new Rational(25, 1)));

        await SubmitPacedAsync(recorders, output.Id, sink, frames: 25);

        // Every frame reached the encoder. Asserting only that a FILE APPEARED would have passed while
        // the queue silently dropped two thirds of them — which is what an unpaced burst does here.
        Assert.Equal(0, recorders.Status().Single().Dropped);

        await recorders.DisarmAsync(output.Id);

        Assert.False(recorders.IsArmed(output.Id));

        // Non-trivially sized: an empty reservation would also "exist", and a file with only a header
        // is what a recording that never received a frame produces.
        Assert.True(new FileInfo(path).Length > 1_000, $"only {new FileInfo(path).Length} bytes");

        KeepForInspection(path);

        await recorders.DisposeAsync();
    }

    /// <summary>
    /// Submits frames at a rate the encoder can absorb.
    /// </summary>
    /// <remarks>
    /// The encode queue is bounded and drops the oldest frame when it fills — correct for a live
    /// recording, which must never stall the show to keep up. A synthetic producer with no decode work
    /// to do outruns any encoder, so a test that wants every frame in the file has to pace itself; one
    /// that does not is testing the drop path.
    /// </remarks>
    private static async Task SubmitPacedAsync(
        ProjectRecorders recorders, Guid id, RecordVideoOutput sink, int frames)
    {
        for (var index = 0; index < frames; index++)
        {
            while (recorders.Status().Single(row => row.Id == id).Behind)
                await Task.Delay(1);

            sink.Submit(Frame(160, 120, index));
        }
    }

    [RecordingFact]
    public async Task AProducerFasterThanTheEncoderDropsFramesRatherThanStalling()
    {
        var (recorders, _, output, sink) = Show("burst.mkv");

        Assert.Null(recorders.Arm(output.Id));
        sink.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, new Rational(25, 1)));

        // Deliberately unpaced. A recording that blocked here would hold up the compositor, and through
        // it the show — so the queue drops instead, and the operator is told rather than left to
        // discover a gapped file afterwards.
        for (var index = 0; index < 400; index++)
            sink.Submit(Frame(160, 120, index));

        Assert.True(recorders.Status().Single().Dropped > 0, "a burst this size must report drops");

        await recorders.DisposeAsync();
    }

    /// <summary>Copies a finished recording out of the temp folder when asked, for manual inspection.</summary>
    private static void KeepForInspection(string path)
    {
        if (Environment.GetEnvironmentVariable("HACUE2_KEEP_RECORDINGS") is not { Length: > 0 } destination)
            return;

        Directory.CreateDirectory(destination);
        File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), overwrite: true);
    }

    [RecordingFact]
    public async Task FramesArrivingBeforeAnyArmAreDropped()
    {
        var (recorders, _, output, sink) = Show("idle.mkv");

        sink.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, new Rational(25, 1)));

        // The compositor renders into a record output from the moment the show loads. Those frames
        // must go nowhere rather than accumulate — a recording starts when it is armed.
        for (var index = 0; index < 10; index++)
            sink.Submit(Frame(160, 120, index));

        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : []);

        Assert.Null(recorders.Arm(output.Id));
        await recorders.DisarmAsync(output.Id);
        await recorders.DisposeAsync();
    }

    [RecordingFact]
    public async Task ArmingTwiceKeepsTheFirstRecording()
    {
        var (recorders, _, output, _) = Show("once-{n}.mkv");

        Assert.Null(recorders.Arm(output.Id));
        var first = recorders.Status().Single().Destination;

        // The second arm is a no-op, not a second file: a double click on RECORD must not abandon the
        // recording already running and start a fresh one.
        Assert.Null(recorders.Arm(output.Id));
        Assert.Equal(first, recorders.Status().Single().Destination);

        await recorders.DisposeAsync();
    }

    [RecordingFact]
    public async Task EachArmTakesTheNextFreeName()
    {
        var (recorders, _, output, _) = Show("take-{n}.mkv");

        Assert.Null(recorders.Arm(output.Id));
        var first = recorders.Status().Single().Destination;
        await recorders.DisarmAsync(output.Id);

        Assert.Null(recorders.Arm(output.Id));
        var second = recorders.Status().Single().Destination;
        await recorders.DisarmAsync(output.Id);

        // The counter climbs rather than the name being reused. Overwriting last act's recording
        // because the show was armed again is the one failure a recorder must never have.
        Assert.Equal("take-1.mkv", Path.GetFileName(first));
        Assert.Equal("take-2.mkv", Path.GetFileName(second));
        Assert.True(File.Exists(first));

        await recorders.DisposeAsync();
    }

    [RecordingFact]
    public async Task APatternWithNoCounterStillNeverOverwrites()
    {
        var (recorders, _, output, _) = Show("fixed.mkv");

        Assert.Null(recorders.Arm(output.Id));
        await recorders.DisarmAsync(output.Id);
        Assert.Null(recorders.Arm(output.Id));
        var second = recorders.Status().Single().Destination;
        await recorders.DisarmAsync(output.Id);

        // No {n} to vary, so a suffix is appended rather than the loop testing one taken name forever.
        Assert.Equal("fixed-2.mkv", Path.GetFileName(second));

        await recorders.DisposeAsync();
    }

    [RecordingFact]
    public async Task DisposingFinishesAnArmedRecording()
    {
        var (recorders, _, output, sink) = Show("closing.mkv");

        Assert.Null(recorders.Arm(output.Id));
        var path = recorders.Status().Single().Destination!;

        sink.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, new Rational(25, 1)));

        for (var index = 0; index < 25; index++)
            sink.Submit(Frame(160, 120, index));

        // The show closing with a recording running must still produce a file with a trailer — one
        // without is a file most players refuse to open, which loses the whole performance.
        await recorders.DisposeAsync();

        Assert.True(new FileInfo(path).Length > 1_000, $"only {new FileInfo(path).Length} bytes");
    }

    [RecordingFact]
    public async Task AContinuousRecordingWritesThroughIdleTime()
    {
        var (recorders, _, output, sink) = Show("archive.mkv", continuous: true);

        Assert.Null(recorders.Arm(output.Id));
        sink.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, new Rational(25, 1)));

        var path = recorders.Status().Single().Destination!;

        // Nothing is submitted at all: this is the gap between two acts. A content-only recording would
        // collapse it to nothing, and an archive whose timecode no longer matched the show would be
        // useless for finding anything in it.
        await Task.Delay(700);
        await recorders.DisarmAsync(output.Id);

        Assert.True(new FileInfo(path).Length > 1_000, $"only {new FileInfo(path).Length} bytes of filler");

        await recorders.DisposeAsync();
    }

    // ── refusals ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnwritableFormatIsRefusedBeforeAnyFileIsMade()
    {
        var (recorders, _, output, _) = Show("show.flac");

        var problem = recorders.Arm(output.Id);

        Assert.NotNull(problem);

        // .mkv, not .mka: this is a VIDEO output, and the audio-only suggestion would earn it a second
        // refusal for having no room for a picture.
        Assert.Contains(".mkv", problem, StringComparison.Ordinal);
        Assert.False(recorders.IsArmed(output.Id));

        // Nothing was reserved: a refusal must not leave an empty file named after a recording that
        // never happened.
        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : []);
    }

    [Fact]
    public void AVideoRecordingIsRefusedAnAudioOnlyContainer()
    {
        var (recorders, _, output, _) = Show("show.mka");

        Assert.Contains("audio only", recorders.Arm(output.Id)!, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamWithNoUrlIsRefused()
    {
        var (recorders, _, output, _) = Show("", VideoOutputKind.Stream);

        Assert.Contains("no URL", recorders.Arm(output.Id)!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProblemIsRememberedForTheDevicesList()
    {
        var (recorders, _, output, _) = Show("show.flac");

        recorders.Arm(output.Id);

        // The operator pressed RECORD and nothing happened; the reason has to survive to somewhere they
        // can read it, not vanish with the return value.
        var row = recorders.Status().Single();
        Assert.False(row.Armed);
        Assert.NotNull(row.Problem);
    }

    [RecordingFact]
    public async Task ASuccessfulArmClearsAnEarlierProblem()
    {
        var (recorders, project, output, _) = Show("show.flac");

        recorders.Arm(output.Id);
        Assert.NotNull(recorders.Status().Single().Problem);

        output.Record!.Pattern = "show.mkv";
        recorders.Adopt(project);

        Assert.Null(recorders.Arm(output.Id));
        Assert.Null(recorders.Status().Single().Problem);

        await recorders.DisposeAsync();
    }

    [Fact]
    public void OnlyRecordAndStreamTargetsAppear()
    {
        var (recorders, project, _, _) = Show("show.mkv");

        project.VideoOutputs.Add(new VideoOutputDefinition { Name = "Projector", Kind = VideoOutputKind.LocalScreen });
        project.AudioLines.Add(new AudioLineDefinition { Name = "Main", Kind = AudioLineKind.LocalAudio });
        recorders.Adopt(project);

        var row = Assert.Single(recorders.Status());
        Assert.Equal("Capture", row.Name);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that will not delete is not a test failure.
        }
    }
}
