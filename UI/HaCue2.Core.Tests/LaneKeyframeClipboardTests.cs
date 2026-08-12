using HaCue2.Core.Model;
using HaCue2.Core.Timeline;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class LaneKeyframeClipboardTests
{
    [Fact]
    public void SelectedKeyframesRoundTripRelativeToTheirFirstPointWithInnerTangents()
    {
        var points = new List<LanePoint>
        {
            new(0, 1),
            new(0.2, 0.8, FadeCurve.Linear, 0.3, 0.9),
            new(0.6, 0.2, FadeCurve.SCurve, InHandleX: 0.5, InHandleY: 0.1),
            new(1, 1),
        };

        var text = LaneKeyframeClipboard.Encode(points, new HashSet<int> { 1, 2 });
        var decoded = LaneKeyframeClipboard.Decode(text)!;

        Assert.Equal(2, decoded.Count);
        Assert.Equal(0, decoded[0].X);
        Assert.Equal(0.4, decoded[1].X, 5);
        Assert.Equal(0.1, decoded[0].OutHandleX!.Value, 5);
        Assert.Equal(0.3, decoded[1].InHandleX!.Value, 5);
        Assert.Equal(FadeCurve.SCurve, decoded[1].CurveToNext);
    }

    [Theory]
    [InlineData("")]
    [InlineData("some other clipboard text")]
    [InlineData("HaCue2-Keyframes/1\nnot;a;keyframe")]
    public void ForeignOrMalformedClipboardTextIsRefused(string text) =>
        Assert.Null(LaneKeyframeClipboard.Decode(text));
}
