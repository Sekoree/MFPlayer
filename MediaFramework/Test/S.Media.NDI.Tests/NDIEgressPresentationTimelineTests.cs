using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// The egress timeline's two modes. Shared-domain is the original single-anchor behaviour (muxed
/// A/V from one player); independent-domain is the linked-carrier fix - each stream maps its own
/// PTS domain onto one wall epoch, so assertions here avoid wall-time exactness and pin the parts
/// that are deterministic: within-stream deltas, cross-reset independence, and non-negativity.
/// </summary>
public sealed class NDIEgressPresentationTimelineTests
{
    private static readonly TimeSpan GenerousWallBound = TimeSpan.FromSeconds(30);

    [Fact]
    public void SharedMode_BothStreamsAnswerToOneAnchor()
    {
        var timeline = new NDIEgressPresentationTimeline();

        var videoFirst = timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromSeconds(10));
        var audioNext = timeline.TimecodeFromPresentationTime(NDIEgressStream.Audio, TimeSpan.FromSeconds(10.02));

        Assert.Equal(0, videoFirst);
        Assert.Equal(TimeSpan.FromSeconds(0.02).Ticks, audioNext);
    }

    [Fact]
    public void SharedMode_BackwardJumpBeyondOneSecond_ReanchorsAtZero()
    {
        var timeline = new NDIEgressPresentationTimeline();
        timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromSeconds(100));

        var afterJump = timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromSeconds(3));

        Assert.Equal(0, afterJump);
    }

    [Fact]
    public void IndependentMode_WithinStreamDeltasAreExact_AcrossUnrelatedDomains()
    {
        var timeline = new NDIEgressPresentationTimeline(independentStreamDomains: true);

        // Video lives at composition time (hours in), audio at bay stream position (near zero) -
        // the case that made a shared anchor produce hours of skew.
        var v0 = timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromHours(3));
        var a0 = timeline.TimecodeFromPresentationTime(NDIEgressStream.Audio, TimeSpan.Zero);
        var v1 = timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromHours(3) + TimeSpan.FromMilliseconds(40));
        var a1 = timeline.TimecodeFromPresentationTime(NDIEgressStream.Audio, TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.FromMilliseconds(40).Ticks, v1 - v0);
        Assert.Equal(TimeSpan.FromMilliseconds(10).Ticks, a1 - a0);

        // Both anchors map to elapsed-since-epoch, so neither is hours away from the other.
        Assert.InRange(v0, 0, GenerousWallBound.Ticks);
        Assert.InRange(a0, 0, GenerousWallBound.Ticks);
    }

    [Fact]
    public void IndependentMode_VideoStreamReset_LeavesTheAudioMappingUntouched()
    {
        var timeline = new NDIEgressPresentationTimeline(independentStreamDomains: true);
        var a0 = timeline.TimecodeFromPresentationTime(NDIEgressStream.Audio, TimeSpan.FromSeconds(1));
        timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromHours(2));

        // An edit re-sync: the video half re-configures and re-maps; the bay's audio half keeps
        // playing and its base must not move - that drift was the NDI-01 defect.
        timeline.ResetStream(NDIEgressStream.Video);
        var v0 = timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromHours(5));
        var a1 = timeline.TimecodeFromPresentationTime(NDIEgressStream.Audio, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, a1 - a0);
        Assert.InRange(v0, 0, GenerousWallBound.Ticks);
    }

    [Fact]
    public void IndependentMode_BackwardJump_RemapsWithoutGoingNegative()
    {
        var timeline = new NDIEgressPresentationTimeline(independentStreamDomains: true);
        timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromSeconds(500));

        var afterJump = timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromSeconds(2));
        var next = timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromSeconds(2.5));

        Assert.InRange(afterJump, 0, GenerousWallBound.Ticks);
        Assert.Equal(TimeSpan.FromSeconds(0.5).Ticks, next - afterJump);
    }

    [Fact]
    public void SharedMode_ResetStream_DegradesToFullReset()
    {
        var timeline = new NDIEgressPresentationTimeline();
        timeline.TimecodeFromPresentationTime(NDIEgressStream.Video, TimeSpan.FromSeconds(50));

        timeline.ResetStream(NDIEgressStream.Video);
        var reanchored = timeline.TimecodeFromPresentationTime(NDIEgressStream.Audio, TimeSpan.FromSeconds(90));

        Assert.Equal(0, reanchored);
    }
}
