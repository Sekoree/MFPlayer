using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// D2: the <c>ndi:</c> descriptor is where ingest genlock is opted into. The receiver always maintains an
/// ingest clock; only <c>ingestClock=1</c> makes it advertise that clock for router pacing, so the default
/// descriptor must keep today's wall-clock behaviour exactly.
/// </summary>
public sealed class NDISourceDescriptorTests
{
    [Fact]
    public void ParseSourceUri_WithoutIngestClockOption_DoesNotPaceFromIngest()
    {
        var descriptor = NDIDecoderProvider.ParseSourceUri("ndi://CAM-1?audio=1&video=1&lowBandwidth=0");

        Assert.False(descriptor.PaceFromIngestClock);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    public void ParseSourceUri_IngestClockOption_OptsIntoIngestPacing(string value)
    {
        var descriptor = NDIDecoderProvider.ParseSourceUri($"ndi://CAM-1?audio=1&video=1&ingestClock={value}");

        Assert.True(descriptor.PaceFromIngestClock);
        Assert.True(descriptor.ReceiveAudio);
    }

    [Fact]
    public void ParseSourceUri_IngestClockOption_ExplicitlyOff_StaysOnTheWallClock()
    {
        var descriptor = NDIDecoderProvider.ParseSourceUri("ndi://CAM-1?ingestClock=0");

        Assert.False(descriptor.PaceFromIngestClock);
    }

    [Fact]
    public void ParseSourceUri_IngestClockWithoutAudio_IsRejected()
    {
        // The ingest clock is audio-driven: a video-only receiver would never advance it and a slaved
        // router would produce nothing at all. Fail the open instead of silently going silent.
        Assert.Throws<ArgumentException>(() =>
            NDIDecoderProvider.ParseSourceUri("ndi://CAM-1?audio=0&video=1&ingestClock=1"));
    }

    [Fact]
    public void ParseSourceUri_NonBooleanIngestClock_IsRejected() =>
        Assert.Throws<ArgumentException>(() => NDIDecoderProvider.ParseSourceUri("ndi://CAM-1?ingestClock=maybe"));
}
