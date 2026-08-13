using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Media.Encode.FFmpeg;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// What a recording's extension means, and what it cannot mean.
/// </summary>
/// <remarks>
/// Register item 30's patterns are whole filenames, so the extension is the document's only statement
/// of format. That makes the refusals as important as the mappings: an extension this build cannot mux
/// has to be caught before a file is opened under a name that lies about its contents.
/// </remarks>
public class RecordFormatTests
{
    [Fact]
    public void TheExtensionChoosesTheContainer()
    {
        Assert.Equal(EncodeContainer.Matroska, RecordFormats.Find("show.mkv")!.Container);
        Assert.Equal(EncodeContainer.Mp4, RecordFormats.Find("show.mp4")!.Container);
        Assert.Equal(EncodeContainer.MpegTs, RecordFormats.Find("show.ts")!.Container);
    }

    [Fact]
    public void TheExtensionIsMatchedRegardlessOfCase()
    {
        Assert.NotNull(RecordFormats.Find("SHOW.MKV"));
    }

    [Fact]
    public void AudioOnlyContainersSaySo()
    {
        Assert.False(RecordFormats.Find("show.mka")!.CarriesVideo);
        Assert.False(RecordFormats.Find("show.m4a")!.CarriesVideo);
        Assert.True(RecordFormats.Find("show.mkv")!.CarriesVideo);
    }

    [Fact]
    public void EveryOfferedFormatProducesValidEncodeOptions()
    {
        // The table is the only place a container and its codecs are paired, and the encode library
        // validates that pairing at Create. A row that disagreed would fail at ARM TIME, in front of an
        // operator, on the one night it mattered - so every row is checked here instead.
        foreach (var format in RecordFormats.All)
        {
            var options = format.CarriesVideo
                ? RecordFormats.Options(format, channels: 2, sampleRate: 48_000, width: 1920, height: 1080, fps: 30)
                : RecordFormats.Options(format, channels: 2, sampleRate: 48_000);

            // probeEncoders:false - whether THIS build ships libx264 is a machine fact, not a table fact.
            Assert.Empty(options.Validate(probeEncoders: false));
        }
    }

    [Fact]
    public void AVideoFormatWithNoPictureBecomesAudioOnly()
    {
        var options = RecordFormats.Options(RecordFormats.Find("show.mkv")!, channels: 2, sampleRate: 48_000);

        Assert.Equal(EncodeOutputMode.AudioOnly, options.OutputMode);
        Assert.Empty(options.Validate(probeEncoders: false));
    }

    [Fact]
    public void APictureWithNoAudioBecomesVideoOnly()
    {
        var options = RecordFormats.Options(
            RecordFormats.Find("show.mkv")!, channels: 0, sampleRate: 48_000, width: 1280, height: 720, fps: 25);

        Assert.Equal(EncodeOutputMode.VideoOnly, options.OutputMode);
        Assert.Empty(options.Validate(probeEncoders: false));
    }

    // ── refusals ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnExtensionThisBuildCannotWriteNamesOneItCan()
    {
        // The mockup drew "show-{date}.flac", and this build muxes five containers of which none is a
        // raw FLAC stream - lossless audio is FLAC inside Matroska. Saying which extension to use beats
        // listing what went wrong.
        var problem = RecordFormatNames.Problem("show.flac", carriesVideo: false);

        Assert.NotNull(problem);
        Assert.Contains(".mka", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("show.wav", ".mka")]
    [InlineData("show.mp3", ".mka")]
    [InlineData("show.webm", ".mkv")]
    [InlineData("show.avi", ".mkv")]
    public void EveryExtensionSomebodyWouldTryNamesItsAlternative(string pattern, string expected)
    {
        Assert.Contains(expected, RecordFormatNames.Problem(pattern, carriesVideo: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public void AVideoTargetIsNeverSentToAnAudioOnlyAlternative()
    {
        // "‘.flac’ cannot be written, use ‘.mka’" is useless advice to somebody recording a picture:
        // .mka has no room for one, so following it would earn a second refusal.
        var problem = RecordFormatNames.Problem("show.flac", carriesVideo: true)!;

        Assert.Contains(".mkv", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(".mka", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAlternativeIsItselfWritable()
    {
        // A table that pointed at an extension nothing could write would send an operator in a circle.
        foreach (var (_, instead) in RecordFormatNames.Alternatives)
            Assert.True(RecordFormatNames.IsKnown("x" + instead), instead);
    }

    [Fact]
    public void APatternWithNoExtensionIsRefused()
    {
        var problem = RecordFormatNames.Problem("show-{date}", carriesVideo: true);

        Assert.NotNull(problem);
        Assert.Contains(".mkv", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AVideoRecordingRefusesAnAudioOnlyContainer()
    {
        // Silently dropping the video leg would produce a file that opens, plays, and is missing the
        // half the operator was recording.
        var problem = RecordFormatNames.Problem("show.mka", carriesVideo: true);

        Assert.NotNull(problem);
        Assert.Contains("audio only", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAudioRecordingAcceptsAVideoContainer()
    {
        // The other direction is fine: .mkv holding only audio is valid, and an operator who typed it
        // gets a working file rather than a lecture.
        Assert.Null(RecordFormatNames.Problem("show.mkv", carriesVideo: false));
    }

    [Fact]
    public void AnUnheardOfExtensionListsWhatIsAvailable()
    {
        var problem = RecordFormatNames.Problem("show.xyz", carriesVideo: false);

        Assert.NotNull(problem);
        Assert.Contains(".mkv", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentsFormatListAndTheEncodersAgreeExactly()
    {
        // The two halves are split on purpose - project status validates a pattern with no encoder in
        // reach - and this is what stops them drifting. An extension the document accepts that the
        // encoder cannot resolve would pass validation at the get-in and fail at arm time on the night.
        Assert.Equal(
            RecordFormatNames.All.OrderBy(name => name, StringComparer.Ordinal),
            RecordFormats.All.Select(format => format.Extension).OrderBy(name => name, StringComparer.Ordinal));

        foreach (var format in RecordFormats.All)
        {
            Assert.Equal(
                RecordFormatNames.AudioOnly.Contains(format.Extension, StringComparer.OrdinalIgnoreCase),
                !format.CarriesVideo);
        }

        Assert.NotNull(RecordFormats.Find("x" + RecordFormatNames.DefaultAudio));
        Assert.NotNull(RecordFormats.Find("x" + RecordFormatNames.DefaultVideo));
    }

    [Fact]
    public void AStreamKeyIsNeverPrintedInFull()
    {
        // The last path segment of an ingest URL is a credential. It reaches logs and the devices list,
        // and a log pasted into a bug report must not hand out the ability to broadcast as somebody.
        var redacted = ProjectRecorders.Redact("rtmp://live.example.com/app/super-secret-key");

        Assert.DoesNotContain("super-secret-key", redacted, StringComparison.Ordinal);
        Assert.Contains("live.example.com", redacted, StringComparison.Ordinal);
    }
}
