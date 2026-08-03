using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The interpolation behind a patch cue's fade.
/// </summary>
/// <remarks>
/// The bay reconciles a matrix over a few milliseconds, which is right for a dragged cell and wrong for
/// a cue that says "over four seconds". These are the rules that make the difference audible as a ramp
/// rather than a step, and they are all cases somebody would otherwise discover mid-show.
/// </remarks>
public class PatchRampTests
{
    private static readonly Guid Channel = Guid.NewGuid();
    private static readonly Guid Line = Guid.NewGuid();

    private static PatchCell Cell(double gainDb, bool muted = false) => new()
    {
        LogicalChannelId = Channel,
        LineId = Line,
        LineChannel = 0,
        GainDb = gainDb,
        Muted = muted,
    };

    [Fact]
    public void TheEndOfARampIsExactlyTheDestination()
    {
        var blended = PatchRamp.Blend([Cell(-20)], [Cell(-6)], 1);

        Assert.Equal(-6, blended[0].GainDb, 3);
    }

    [Fact]
    public void GainsInterpolateInDecibelsRatherThanLinearly()
    {
        var blended = PatchRamp.Blend([Cell(-20)], [Cell(0)], 0.5);

        // Halfway between −20 dB and 0 dB is −10 dB. Interpolating LINEAR gain instead would put the
        // midpoint at 0.55 (about −5 dB), so the fade would spend most of its time nearly at full
        // level and sound like a step followed by a wait.
        Assert.Equal(-10, blended[0].GainDb, 3);
    }

    [Fact]
    public void ACellWithNoPriorValueFadesUpFromSilenceRatherThanAppearing()
    {
        var blended = PatchRamp.Blend([], [Cell(0)], 0.5);

        // Nothing was routed here before, so the ramp starts at the floor. Without this the cell would
        // arrive at half of full level on the first frame — audible as a click.
        Assert.True(blended[0].GainDb < -20, $"expected a level near the floor, got {blended[0].GainDb}");
    }

    [Fact]
    public void ARampDownMutesOnlyAtTheEnd()
    {
        var midway = PatchRamp.Blend([Cell(0)], [Cell(-6, muted: true)], 0.5);
        var landed = PatchRamp.Blend([Cell(0)], [Cell(-6, muted: true)], 1);

        // Muting at the start would silence the cell instantly and make the ramp inaudible — the mute
        // is the destination STATE, and the gain is what the operator hears travelling toward it.
        Assert.False(midway[0].Muted);
        Assert.True(landed[0].Muted);
    }

    [Fact]
    public void ARampAwayFromAMutedCellStartsFromSilence()
    {
        var blended = PatchRamp.Blend([Cell(0, muted: true)], [Cell(0)], 0.5);

        // The origin was muted, so it contributed nothing however high its stored gain was. Reading
        // the stored 0 dB as the starting point would make the "fade up" start at full level.
        Assert.True(blended[0].GainDb < -20, $"expected a level near the floor, got {blended[0].GainDb}");
        Assert.False(blended[0].Muted);
    }

    [Fact]
    public void ACellTheCueDoesNotMentionIsLeftAlone()
    {
        var other = new PatchCell { LogicalChannelId = Guid.NewGuid(), LineId = Line, LineChannel = 1 };

        var blended = PatchRamp.Blend([other, Cell(0)], [Cell(-6)], 0.5);

        // Keyed off the DESTINATION, which is the partial-recall promise held one frame at a time: a
        // patch cue that covers Fold must not touch Main on its way past.
        Assert.Single(blended);
        Assert.Equal(Channel, blended[0].LogicalChannelId);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    public void ARampWithNoDurationIsOneStep(int milliseconds, int expected) =>
        Assert.Equal(expected, PatchRamp.StepsFor(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void ALongerRampTakesProportionallyMoreSteps()
    {
        var short_ = PatchRamp.StepsFor(TimeSpan.FromMilliseconds(500));
        var long_ = PatchRamp.StepsFor(TimeSpan.FromSeconds(4));

        Assert.True(long_ > short_);
        // Bounded by the step size rather than unbounded: a four-second fade is tens of updates, not
        // hundreds, so a long ramp costs the bay no more per second than a short one.
        Assert.Equal(TimeSpan.FromSeconds(4) / PatchRamp.Step, long_, 0);
    }

    [Fact]
    public void ProgressIsClampedRatherThanExtrapolated()
    {
        var over = PatchRamp.Blend([Cell(-20)], [Cell(0)], 2);
        var under = PatchRamp.Blend([Cell(-20)], [Cell(0)], -1);

        Assert.Equal(0, over[0].GainDb, 3);
        Assert.Equal(-20, under[0].GainDb, 3);
    }
}
