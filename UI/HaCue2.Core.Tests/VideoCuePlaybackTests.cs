using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// A REAL decoded video file, fired as a cue, landing on a composition.
/// </summary>
/// <remarks>
/// Every composition test in the framework's own suite drives fake sources, so the one thing none of
/// them can catch is the format negotiation between a real decoder and the compositor's layer input.
/// That input is a fan-out BRANCH — the player's primary output is the discard sink it negotiates
/// against — and a branch that needs a pixel conversion has to ask the router for a converter.
/// </remarks>
public sealed class VideoCuePlaybackTests
{
    /// <summary>A second of colour bars, decoded for real. Returns null where ffmpeg is unavailable.</summary>
    private static string? MakeClip(string directory)
    {
        var path = Path.Combine(directory, "clip.mp4");

        try
        {
            using var ffmpeg = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    "-v error -f lavfi -i testsrc=size=320x240:rate=25:duration=1 "
                    + $"-pix_fmt yuv420p -c:v libx264 \"{path}\" -y",
                UseShellExecute = false,
            });

            if (ffmpeg is null)
                return null;

            ffmpeg.WaitForExit(60_000);
            return File.Exists(path) ? path : null;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private sealed class CountingVideoOutput : IVideoOutput
    {
        private VideoFormat _format;
        private int _submitted;

        public VideoFormat Format => _format;

        // What the compositor's own layer input accepts, which is the whole point: the decoder does
        // not produce BGRA, so reaching this output means a conversion happened.
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];

        public int Submitted => Volatile.Read(ref _submitted);

        public void Configure(VideoFormat format) => _format = format;

        public void Submit(VideoFrame frame)
        {
            Interlocked.Increment(ref _submitted);
            frame.Dispose();
        }
    }

    /// <summary>
    /// Firing a video cue onto a composition composites frames rather than throwing.
    /// </summary>
    /// <remarks>
    /// It threw: "video fan-out: no swscale path from I420 to any branch format [Bgra32]", for EVERY
    /// video cue rather than only the album-art case that surfaced it. The router's converter factory
    /// and its can-convert probe are both options nobody ever passed, so the probe answered false for
    /// every pair and the route was rejected and rolled back.
    /// </remarks>
    [Fact]
    public async Task AVideoCueCompositesOntoItsComposition()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-video-cue");

        try
        {
            if (MakeClip(directory.FullName) is not { } clip)
                return; // no ffmpeg on this box — nothing to assert about decoding

            using var registry = MediaRegistry.Build(builder => builder.Use(new FFmpegModule()));
            await using var session = new ShowSession(registry);

            var screen = new CountingVideoOutput();

            session.LoadDocument(new ShowDocument(
                Version: 1,
                Cues: [new CueDefinition("q1", 1, "Clip")],
                Clips: [new ShowClipBinding("q1", clip, CompositionId: "screen", LayerIndex: 0)],
                Compositions: [new ShowComposition("screen", "Screen", 320, 240, 25, 1)],
                Routes: []));

            Assert.True(await session.AttachCompositionOutputAsync("screen", screen, "projector"));

            await session.FireCueAsync("q1");

            for (var attempt = 0; attempt < 80 && screen.Submitted == 0; attempt++)
                await Task.Delay(25);

            Assert.True(screen.Submitted > 0, "a video cue fired but nothing reached the composition's output");
            Assert.Equal(320, screen.Format.Width);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>An audio file carrying album art, which is a video stream with `attached_pic` set.</summary>
    private static string? MakeAudioWithCover(string directory, int seconds = 1)
    {
        var cover = Path.Combine(directory, "cover.jpg");
        var path = Path.Combine(directory, "song.flac");

        try
        {
            using (var art = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-v error -f lavfi -i testsrc=size=600x600:rate=1:duration=1 -frames:v 1 \"{cover}\" -y",
                UseShellExecute = false,
            }))
            {
                art?.WaitForExit(60_000);
            }

            using var mux = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    $"-v error -f lavfi -i sine=frequency=440:duration={seconds} "
                    + $"-i \"{cover}\" -map 0:a -map 1:v -c:a flac -c:v copy "
                    + $"-disposition:v attached_pic \"{path}\" -y",
                UseShellExecute = false,
            });

            mux?.WaitForExit(60_000);
            return File.Exists(path) ? path : null;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Placing an audio file's album art on a canvas plays the file rather than killing the cue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's report was "once I tried to place the audio file's cover art it didn't play any
    /// more", and the crash report named the cause: the cover is MJPEG in <c>yuvj420p</c>, so the route
    /// to the composition needed a conversion the router refused, the route was rolled back, and the
    /// exception travelled out of the fire as an unobserved task — taking the AUDIO with it.
    /// </para>
    /// <para>
    /// The important half is that the audio survives. A still that cannot be shown is a picture
    /// missing; a cue that will not fire is the show stopping.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PlacingAnAudioFilesCoverArtStillPlaysTheFile()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-cover-art");

        try
        {
            if (MakeAudioWithCover(directory.FullName) is not { } song)
                return;

            using var registry = MediaRegistry.Build(builder => builder.Use(new FFmpegModule()));
            await using var session = new ShowSession(registry);

            var screen = new CountingVideoOutput();

            session.LoadDocument(new ShowDocument(
                Version: 1,
                Cues: [new CueDefinition("q1", 1, "Song")],
                Clips: [new ShowClipBinding("q1", song, CompositionId: "screen", LayerIndex: 0)],
                Compositions: [new ShowComposition("screen", "Screen", 640, 480, 25, 1)],
                Routes: []));

            Assert.True(await session.AttachCompositionOutputAsync("screen", screen, "projector"));

            await session.FireCueAsync("q1");

            // Sounding is the assertion that matters: the cue fired and held a voice.
            for (var attempt = 0; attempt < 80; attempt++)
            {
                if ((await session.SnapshotAsync()).Any(state => state.IsActive))
                    return;

                await Task.Delay(25);
            }

            Assert.Fail("a cue whose media carries album art never became active");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A cover-art cue keeps playing PAST the single frame the still is made of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other cover-art test proves the cue starts. This one proves it does not stop, which is a
    /// different failure with the same symptom: an <c>attached_pic</c> stream is one frame, and the
    /// demuxer reports its video track exhausted the moment that frame is out. If the clip's life were
    /// taken from the video track, every album-art cue would end almost immediately and the audio would
    /// go with it — silently, a second after GO.
    /// </para>
    /// <para>
    /// The stream is named EXPLICITLY here, which is what "place on composition" writes for a
    /// cover-art-only file: automatic election skips attached pictures, so the default path never opens
    /// the still at all and cannot exercise this.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACoverArtCueKeepsPlayingAfterTheStillHasBeenDelivered()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-cover-art-life");

        try
        {
            if (MakeAudioWithCover(directory.FullName, seconds: 4) is not { } song)
                return;

            using var registry = MediaRegistry.Build(builder => builder.Use(new FFmpegModule()));
            await using var session = new ShowSession(registry);

            var screen = new CountingVideoOutput();
            var endedEarly = 0;
            session.ClipNaturallyEnded += _ => Interlocked.Increment(ref endedEarly);

            session.LoadDocument(new ShowDocument(
                Version: 1,
                Cues: [new CueDefinition("q1", 1, "Song")],
                Clips:
                [
                    new ShowClipBinding("q1", song, CompositionId: "screen", LayerIndex: 0)
                    {
                        VideoStreamIndex = 1,
                    },
                ],
                Compositions: [new ShowComposition("screen", "Screen", 640, 480, 25, 1)],
                Routes: []));

            Assert.True(await session.AttachCompositionOutputAsync("screen", screen, "projector"));
            await session.FireCueAsync("q1");

            // Well past the one frame the still is, and well short of the file's own end.
            await Task.Delay(1_500);

            var state = (await session.SnapshotAsync()).FirstOrDefault();

            Assert.NotNull(state);
            Assert.True(state!.IsActive, "the cue stopped holding a voice once the still was delivered");
            Assert.True(state.IsRunning, "the clock stopped once the still was delivered");
            Assert.True(
                state.ClipPosition > TimeSpan.FromMilliseconds(700),
                $"the playhead only reached {state.ClipPosition.TotalMilliseconds:0} ms");
            Assert.Equal(0, Volatile.Read(ref endedEarly));

            // And the still is HELD rather than shown once: a card that vanished after one frame is
            // the same defect seen from the canvas.
            Assert.True(screen.Submitted > 1, $"the canvas received {screen.Submitted} frame(s)");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
