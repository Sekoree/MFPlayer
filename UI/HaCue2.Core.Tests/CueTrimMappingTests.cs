using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The CUE-time ↔ MEDIA-time mapping a trimmed cue lives on.
/// </summary>
/// <remarks>
/// <para>
/// Reported from a live rig, 2026-08-11: a cue trimmed to start ~36 minutes into a 2h36m file
/// "failed to properly seek … it seems to not quite know where it should actually seek to and goes all
/// the way from the beginning". Its Active row read <c>00:00.134</c> while its sounding siblings read
/// <c>30:50</c>.
/// </para>
/// <para>
/// The display was right and the seek was wrong. Only the READ direction of the mapping existed (the
/// engine's snapshot subtracted the trim so the operator saw cue time); nothing converted BACK, so a
/// scrub to 30:50 cue time reached the transport as 30:50 FILE time — before the cue's own in-point.
/// The player obeyed, and the read direction then floored the negative result at zero, which is the
/// <c>00:00</c> the operator saw. Both halves now live on <see cref="MediaCueNode"/>, adjacent, so they
/// cannot be written independently again.
/// </para>
/// </remarks>
public class CueTrimMappingTests
{
    // The reported cue: 36 min in-point, file 2:36:09 long.
    private static MediaCueNode Trimmed() => new()
    {
        Number = new CueNumber("1.13"),
        Label = "DAY1_EN3rd_Re_0826_2_Edited",
        MediaPath = "day1.mp4",
        TrimInMs = 36 * 60 * 1000,
    };

    [Fact]
    public void TheReportedCase_SeekingToThirtyMinutesLandsPastTheInPoint_NotAtTheFileStart()
    {
        var cue = Trimmed();

        // What the operator scrubbed to, in the coordinates the panel showed them.
        var media = cue.MediaTimeAt(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(50));

        // Bug shape: 30:50 (before the 36:00 in-point, so the row then displayed 00:00).
        Assert.Equal(TimeSpan.FromMinutes(66) + TimeSpan.FromSeconds(50), media);
    }

    [Fact]
    public void CueZeroIsTheInPoint_NotTheFileStart()
    {
        Assert.Equal(TimeSpan.FromMinutes(36), Trimmed().MediaTimeAt(TimeSpan.Zero));
    }

    [Fact]
    public void TheTwoDirectionsAreInverses()
    {
        var cue = Trimmed();
        foreach (var minutes in new[] { 0, 1, 30, 90 })
        {
            var cueTime = TimeSpan.FromMinutes(minutes);
            Assert.Equal(cueTime, cue.CueTimeAt(cue.MediaTimeAt(cueTime)));
        }
    }

    [Fact]
    public void MediaBeforeTheInPointReadsAsCueZero()
    {
        // The flooring that made the bug LOOK like "it jumped to the beginning" rather than
        // "it seeked somewhere it should not have". Correct on its own terms; kept.
        Assert.Equal(TimeSpan.Zero, Trimmed().CueTimeAt(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void SeekingPastTheOutPointStopsAtIt()
    {
        var cue = new MediaCueNode
        {
            Number = new CueNumber("1"), MediaPath = "x.wav",
            TrimInMs = 60_000,
            TrimOutMs = 120_000,
        };

        // The cue owns 60 s of media. A scrub beyond that is clamped to the out-point rather than
        // running into content the cue was authored to exclude.
        Assert.Equal(TimeSpan.FromSeconds(120), cue.MediaTimeAt(TimeSpan.FromSeconds(999)));
        Assert.Equal(TimeSpan.FromSeconds(90), cue.MediaTimeAt(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void NegativeCueTimeIsTheInPoint()
    {
        Assert.Equal(TimeSpan.FromMinutes(36), Trimmed().MediaTimeAt(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void AnUntrimmedCueMapsOneToOne()
    {
        var cue = new MediaCueNode { Number = new CueNumber("1"), MediaPath = "x.wav" };
        var t = TimeSpan.FromSeconds(42);

        // Why the stems in the reported screenshot were fine and only the trimmed cue was not.
        Assert.Equal(t, cue.MediaTimeAt(t));
        Assert.Equal(t, cue.CueTimeAt(t));
    }

    [Fact]
    public void TrimmedLengthAgreesWithTheMapping()
    {
        var cue = Trimmed();
        var file = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(36) + TimeSpan.FromSeconds(9);

        // The length the panel shows and the coordinate the scrub produces have to come from the same
        // trim, or the far end of the bar seeks somewhere other than the end of the cue.
        var length = cue.TrimmedLength(file);
        Assert.NotNull(length);
        Assert.Equal(file, cue.MediaTimeAt(length!.Value));
    }
}
