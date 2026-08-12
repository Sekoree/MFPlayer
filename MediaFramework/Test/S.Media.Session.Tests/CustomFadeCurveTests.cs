using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// User-drawn fade shapes. The evaluator is deliberately the same one the volume envelope uses - a custom
/// curve IS a normalized envelope - so a shape behaves identically whether it is applied as a fade or as
/// automation.
/// </summary>
public class CustomFadeCurveTests
{
    private static CustomFadeCurve Ramp() =>
        new([new FadeCurvePoint(0, 0), new FadeCurvePoint(1, 1)]);

    [Fact]
    public void EvaluatesEndpointsExactly()
    {
        var curve = Ramp();

        Assert.Equal(0f, curve.Evaluate(0), 5);
        Assert.Equal(1f, curve.Evaluate(1), 5);
    }

    [Fact]
    public void ClampsOutsideTheDrawnRange()
    {
        var curve = new CustomFadeCurve([new FadeCurvePoint(0.25, 0.4), new FadeCurvePoint(0.75, 0.9)]);

        Assert.Equal(0.4f, curve.Evaluate(-1), 5);
        Assert.Equal(0.9f, curve.Evaluate(2), 5);
    }

    [Fact]
    public void InterpolatesBetweenPoints()
    {
        var curve = Ramp();

        Assert.Equal(0.5f, curve.Evaluate(0.5), 3);
    }

    [Fact]
    public void CubicBezierHandlesAreEvaluatedAtTheAuthoredXCoordinate()
    {
        var curve = new CustomFadeCurve(
        [
            new FadeCurvePoint(0, 0, OutHandleX: 0, OutHandleLevel: 1),
            new FadeCurvePoint(1, 0, InHandleX: 1, InHandleLevel: 1),
        ]);

        Assert.Equal(0.75f, curve.Evaluate(0.5), 4);
        Assert.Equal(0f, curve.Evaluate(0), 5);
        Assert.Equal(0f, curve.Evaluate(1), 5);
    }

    [Fact]
    public void BezierSegmentsRejectIncompleteOrOutOfSegmentHandles()
    {
        Assert.Throws<ArgumentException>(() => new CustomFadeCurve(
        [
            new FadeCurvePoint(0, 0, OutHandleX: 0.2, OutHandleLevel: 0.2),
            new FadeCurvePoint(1, 1),
        ]));
        Assert.Throws<ArgumentException>(() => new CustomFadeCurve(
        [
            new FadeCurvePoint(0, 0, OutHandleX: 1.2, OutHandleLevel: 0.2),
            new FadeCurvePoint(1, 1, InHandleX: 0.8, InHandleLevel: 0.8),
        ]));
    }

    [Fact]
    public void HoldSegments_Step_RatherThanRamp()
    {
        // A hold is how an author draws "stay here, then jump" - a plain ramp cannot express it.
        var curve = new CustomFadeCurve(
        [
            new FadeCurvePoint(0, 0.2, Hold: true),
            new FadeCurvePoint(0.5, 0.8),
            new FadeCurvePoint(1, 1),
        ]);

        Assert.Equal(0.2f, curve.Evaluate(0.1), 5);
        Assert.Equal(0.2f, curve.Evaluate(0.49), 5);
        Assert.Equal(0.8f, curve.Evaluate(0.5), 5);
    }

    [Fact]
    public void RejectsTooFewOrUnsortedOrNonFinitePoints()
    {
        Assert.Throws<ArgumentException>(() => new CustomFadeCurve([new FadeCurvePoint(0, 0)]));
        Assert.Throws<ArgumentException>(() => new CustomFadeCurve(
            [new FadeCurvePoint(1, 0), new FadeCurvePoint(0, 1)]));
        Assert.Throws<ArgumentException>(() => new CustomFadeCurve(
            [new FadeCurvePoint(0, 0), new FadeCurvePoint(double.NaN, 1)]));
    }

    [Fact]
    public void EqualityIsByValue_SoTwoDeserializedCurvesCompareEqual()
    {
        // Record equality would compare the point array by reference, and two separately deserialized
        // documents never share instances - which would make every reload look like an edit.
        Assert.Equal(Ramp(), Ramp());
        Assert.Equal(Ramp().GetHashCode(), Ramp().GetHashCode());
        Assert.NotEqual(Ramp(), new CustomFadeCurve([new FadeCurvePoint(0, 0), new FadeCurvePoint(1, 0.5)]));
    }

    [Fact]
    public void FadeShape_ImplicitlyConvertsFromTheBuiltInLaws()
    {
        FadeShape shape = FadeCurve.EqualPower;

        Assert.False(shape.IsCustom);
        Assert.Equal(FadeCurve.EqualPower, shape.Law);
        // Every existing call site keeps its exact behaviour; the custom case is purely additive.
        Assert.Equal(FadeCurves.LevelUp(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), FadeCurve.EqualPower),
            shape.Evaluate(0.5), 5);
    }

    [Fact]
    public void FadeRamp_UsesTheCustomShape_ForBothDirections()
    {
        var curve = new CustomFadeCurve([new FadeCurvePoint(0, 0.25), new FadeCurvePoint(1, 0.75)]);
        var shape = new FadeShape(FadeCurve.Linear, curve);

        var half = TimeSpan.FromMilliseconds(500);
        var second = TimeSpan.FromSeconds(1);

        // A custom curve is used AS DRAWN in both directions - it is the author's shape, not a law to be
        // inverted - so the up and down ramps read the same value at the same progress.
        Assert.Equal(0.5f, FadeRamp.LevelUp(half, second, shape), 3);
        Assert.Equal(0.5f, FadeRamp.LevelDown(half, second, shape), 3);
    }

    [Fact]
    public void FadeRamp_WithNoCustomShape_FallsBackToTheLaw()
    {
        FadeShape shape = FadeCurve.SCurve;
        var half = TimeSpan.FromMilliseconds(500);
        var second = TimeSpan.FromSeconds(1);

        Assert.Equal(FadeRamp.LevelUp(half, second, FadeCurve.SCurve), FadeRamp.LevelUp(half, second, shape), 6);
        Assert.Equal(FadeRamp.LevelDown(half, second, FadeCurve.SCurve), FadeRamp.LevelDown(half, second, shape), 6);
    }

    [Fact]
    public void ZeroDurationFade_IsAHardCut_NotNaN()
    {
        var shape = new FadeShape(FadeCurve.Linear, Ramp());

        Assert.Equal(1f, FadeRamp.LevelUp(TimeSpan.Zero, TimeSpan.Zero, shape), 5);
        Assert.Equal(1f, FadeRamp.LevelDown(TimeSpan.Zero, TimeSpan.Zero, shape), 5);
    }
}
