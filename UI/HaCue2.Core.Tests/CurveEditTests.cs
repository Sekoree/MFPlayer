using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Curve editing: the shape stays something the engine will accept, and undo puts back what was there.
/// </summary>
/// <remarks>
/// <c>CustomFadeCurve</c> THROWS on fewer than two points or an unsorted list, and that throw would
/// happen when the show ran, not when the curve was drawn. Every rule below exists to keep an editing
/// gesture from reaching it.
/// </remarks>
public sealed class CurveEditTests
{
    [Fact]
    public void AnUntouchedCurveOpensAsALineAndStoresNothing()
    {
        var fixture = new TestProject();
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        Assert.False(target.HasStored);
        Assert.Equal(2, target.Read().Count);
        Assert.Null(fixture.Track.FadeInCurve.Points);
    }

    [Fact]
    public void UndoingTheFirstEditRestoresAbsenceRatherThanTheLine()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.Move(target, 0, 0.2, 0.6));
        journal.CloseGroup();
        Assert.NotNull(fixture.Track.FadeInCurve.Points);

        journal.Undo();

        // NOT a stored straight line: an inline point list beats the chosen law, so leaving one behind
        // would quietly replace equal-power with linear on a cue nobody edited.
        Assert.Null(fixture.Track.FadeInCurve.Points);
        Assert.Equal(FadeCurve.EqualPower, fixture.Track.FadeInCurve.Law);
    }

    [Fact]
    public void ADragOfManyEventsIsOneUndoStep()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        using (journal.Composite("move curve point", "cues"))
        {
            for (var step = 1; step <= 15; step++)
                journal.Do(CurveEdits.Move(target, 0, step * 0.01, 0.5));
        }

        Assert.Single(journal.Log);
        Assert.Equal(0.15, fixture.Track.FadeInCurve.Points![0].Progress, 4);
    }

    [Fact]
    public void DraggingAPointPastItsNeighbourResortsRatherThanSticking()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.Add(target, 0.5, 0.5));
        journal.CloseGroup();

        // Point 1 (x = 0.5) dragged out past point 2 (x = 1).
        journal.Do(CurveEdits.Move(target, 1, 0.9, 0.2));
        journal.CloseGroup();

        var progress = fixture.Track.FadeInCurve.Points!.Select(point => point.Progress).ToList();
        Assert.Equal(progress.OrderBy(x => x), progress);
        Assert.Equal(3, progress.Count);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    public void PointsAreClampedToTheCanvas(double typed)
    {
        var fixture = new TestProject();
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        CurveEdits.Move(target, 0, typed, typed).Apply(fixture.Project);

        // A point off the canvas is not an error to report - it is a drag that went too far, and the
        // only useful answer is the edge. NaN is in here because a divide by a zero-width lane
        // produces one, and CustomFadeCurve rejects anything non-finite.
        foreach (var point in fixture.Track.FadeInCurve.Points!)
        {
            Assert.InRange(point.Progress, 0, 1);
            Assert.InRange(point.Level, 0, 1);
        }
    }

    [Fact]
    public void ACurveNeverDropsBelowTwoPoints()
    {
        var fixture = new TestProject();
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        // The gesture that reaches Remove is "drag a point off the canvas", which is easy to do by
        // accident - so it refuses rather than leaving a curve the engine will throw on.
        Assert.Null(CurveEdits.Remove(target, 0));
        Assert.Null(CurveEdits.Remove(target, 1));
    }

    [Fact]
    public void WhateverTheEditorProducesIsACurveTheEngineAccepts()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.Add(target, 0.3, 0.9));
        journal.Do(CurveEdits.Add(target, 0.7, 0.1));
        journal.Do(CurveEdits.Move(target, 1, 5, -2));
        journal.Do(CurveEdits.SetHold(target, 1, hold: true)!);
        journal.CloseGroup();

        // Constructing it is the assertion: CustomFadeCurve throws on anything it cannot evaluate.
        var curve = new CustomFadeCurve(fixture.Track.FadeInCurve.Points!);
        Assert.InRange(curve.Evaluate(0.5), 0f, 1f);
    }

    [Fact]
    public void ALaneHasNoHoldToSet()
    {
        var fixture = new TestProject();
        var lane = new EffectLane { Kind = EffectLaneKind.Volume };
        fixture.Track.EffectLanes.Add(lane);

        var target = new EffectLaneTarget(fixture.Track.Id, lane);

        // There is no step in the lane model to write a hold into, so the edit is refused rather than
        // accepted and dropped - a control that offered it would be lying about what it does.
        Assert.False(target.SupportsHold);
        Assert.Null(CurveEdits.SetHold(target, 0, hold: true));
    }

    [Fact]
    public void AFadeCurveAndALaneAreSeparateUndoSteps()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var lane = new EffectLane { Kind = EffectLaneKind.Volume };
        fixture.Track.EffectLanes.Add(lane);

        journal.Do(CurveEdits.Move(
            new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve), 0, 0.1, 0.2));
        journal.Do(CurveEdits.Move(new EffectLaneTarget(fixture.Track.Id, lane), 0, 0.4, 0.5));
        journal.CloseGroup();

        // Same subject, different property: editing a cue's fade must not swallow a drag on its lane.
        Assert.Equal(2, journal.Log.Count);
    }

    // ── the curve picker (register: choosing a curve, not only drawing one) ────────────────────

    [Fact]
    public void PickingALawClearsTheDrawnPointsThatWouldHaveBeatenIt()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.Move(target, 0, 0.2, 0.6));
        journal.CloseGroup();
        Assert.NotNull(fixture.Track.FadeInCurve.Points);

        journal.Do(CurveEdits.PickLaw(target, FadeCurve.SCurve)!);
        journal.CloseGroup();

        // CurveSpec.Resolve follows preset → points → law. Setting the law and leaving the points
        // would be a picker that changes the highlight and nothing an operator can hear.
        Assert.Equal(FadeCurve.SCurve, fixture.Track.FadeInCurve.Law);
        Assert.Null(fixture.Track.FadeInCurve.Points);
    }

    [Fact]
    public void UndoingAPickPutsBackBothTheLawAndTheShape()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        fixture.Track.FadeInCurve.Law = FadeCurve.Exponential;
        journal.Do(CurveEdits.Move(target, 0, 0.2, 0.6));
        journal.CloseGroup();
        var drawn = target.Read();

        journal.Do(CurveEdits.PickLaw(target, FadeCurve.Linear)!);
        journal.CloseGroup();
        journal.Undo();

        // One step, both halves. Restoring the shape under the new law - or the law under the old
        // shape - would leave a curve nobody drew and nobody chose.
        Assert.Equal(FadeCurve.Exponential, fixture.Track.FadeInCurve.Law);
        Assert.Equal(drawn, target.Read());
    }

    [Fact]
    public void UndoingAPickOnAnUntouchedCurveRestoresAbsence()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.PickLaw(target, FadeCurve.SCurve)!);
        journal.CloseGroup();
        journal.Undo();

        // The same trap Clear() exists for: writing the straight line the editor opens on would leave
        // an inline list that beats the law being restored.
        Assert.Null(fixture.Track.FadeInCurve.Points);
        Assert.Equal(FadeCurve.EqualPower, fixture.Track.FadeInCurve.Law);
    }

    [Fact]
    public void RepickingTheLawAlreadyInForceIsNotAnEdit()
    {
        var fixture = new TestProject();
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        // The picker is rebuilt whenever the editor reloads, which re-selects the current entry. An
        // undo step for that would bury the operator's real edits under their own scrolling.
        Assert.Null(CurveEdits.PickLaw(target, FadeCurve.EqualPower));
    }

    [Fact]
    public void RepickingTheCurrentLawOVERDrawnPointsIsStillAnEdit()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.Move(target, 0, 0.2, 0.6));
        journal.CloseGroup();

        // "Give me plain equal power back" is a real request when a custom shape is drawn over it, and
        // the law field alone cannot tell the two states apart.
        Assert.NotNull(CurveEdits.PickLaw(target, FadeCurve.EqualPower));
    }

    [Fact]
    public void APresetAndALaneHaveNoLawToPick()
    {
        var fixture = new TestProject();
        var preset = new CurvePresetTarget(new CurvePreset { Name = "slow tail" });
        var lane = new EffectLaneTarget(fixture.Track.Id, new EffectLane());

        // They ARE the drawn shape, so the picker is disabled over them rather than offering four
        // choices that would do nothing.
        Assert.Null(preset.Law);
        Assert.Null(lane.Law);
        Assert.Null(CurveEdits.PickLaw(preset, FadeCurve.Linear));
        Assert.Null(CurveEdits.PickLaw(lane, FadeCurve.Linear));
    }

    [Fact]
    public void ShapedCustomSegmentsAreEvaluatedAndUndoable()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.SetSegment(target, 0, FadeCurve.SCurve)!);
        journal.CloseGroup();

        Assert.Equal(FadeCurve.SCurve, fixture.Track.FadeInCurve.Points![0].CurveToNext);
        var curve = new CustomFadeCurve(fixture.Track.FadeInCurve.Points);
        Assert.NotEqual(curve.Evaluate(0.25), new CustomFadeCurve(
            [new FadeCurvePoint(0, 0), new FadeCurvePoint(1, 1)]).Evaluate(0.25));

        journal.Undo();
        Assert.Null(fixture.Track.FadeInCurve.Points);
    }

    [Fact]
    public void BezierTangentsAreCreatedMovedEvaluatedAndUndoable()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var target = new CurveSpecTarget(fixture.Track.Id, "fadeIn", fixture.Track.FadeInCurve);

        journal.Do(CurveEdits.SetBezier(target, 0)!);
        journal.CloseGroup();
        journal.Do(CurveEdits.MoveTangent(target, 0, false, 0.1, 0.9)!);
        journal.CloseGroup();

        var points = fixture.Track.FadeInCurve.Points!;
        Assert.Equal(0.1, points[0].OutHandleX);
        Assert.Equal(0.9, points[0].OutHandleLevel);
        Assert.NotNull(points[1].InHandleX);
        Assert.True(new CustomFadeCurve(points).Evaluate(0.25) > 0.25f);

        journal.Undo();
        Assert.NotEqual(0.1, fixture.Track.FadeInCurve.Points![0].OutHandleX);
        journal.Undo();
        Assert.Null(fixture.Track.FadeInCurve.Points);
    }

    [Fact]
    public void EveryPickerThumbnailNamesTheLawItDraws() =>
        // The thumbnails are drawings of these. Out of step, the picker's pictures and its effects
        // would disagree - the one failure nobody would think to look for.
        Assert.All(
            CurveEdits.Laws,
            law => Assert.Equal(law, CurveEdits.Laws[CurveEdits.LawIndex(law)]));
}
