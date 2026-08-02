using S.Control;
using S.Media.Session;
using Xunit;

namespace S.Control.Tests;

/// <summary>
/// Continuous-controller → parameter bindings. Before this a control surface could fire a cue but could
/// not touch a level: the trigger action model carries no value semantics and the show-action surface
/// exposes only transport verbs.
/// </summary>
public class ControlParametersTests
{
    /// <summary>The plan's own example target: a master trim in dB.</summary>
    private static ParameterTarget Trim() => new("master.trim", "Master trim", -60, 0, "dB");

    private static ParameterRegistry RegistryWith(ParameterTarget target, double initial, out Box box)
    {
        var registry = new ParameterRegistry();
        var value = new Box { Value = initial };
        registry.Register(target, () => value.Value, v => value.Value = v);
        box = value;
        return registry;
    }

    private sealed class Box { public double Value; }

    [Fact]
    public void RegistersReadsAndWrites_ClampingToTheRange()
    {
        var registry = RegistryWith(Trim(), -12, out var box);

        Assert.True(registry.TryGet("master.trim", out var read));
        Assert.Equal(-12, read, 5);

        Assert.True(registry.TrySet("master.trim", 20)); // above the range
        Assert.Equal(0, box.Value, 5);
        Assert.True(registry.TrySet("master.trim", -200)); // below it
        Assert.Equal(-60, box.Value, 5);
    }

    [Fact]
    public void AnUnknownParameter_GoesQuietRatherThanThrowing()
    {
        var registry = new ParameterRegistry();

        // A binding whose target the host has withdrawn must not throw on every fader move.
        Assert.False(registry.TrySet("nope", 1));
        Assert.False(registry.TryGet("nope", out _));
        Assert.False(registry.TryGetTarget("nope", out _));
    }

    [Fact]
    public void RejectsAnEmptyRange()
    {
        var registry = new ParameterRegistry();

        Assert.Throws<ArgumentException>(() =>
            registry.Register(new ParameterTarget("flat", "Flat", 1, 1), () => 0, _ => { }));
    }

    [Fact]
    public void MapsTheFullControllerRange_OntoTheParameterRange()
    {
        var registry = RegistryWith(Trim(), -60, out var box);
        var binding = new ContinuousBinding(
            new ContinuousBindingSpec("master.trim", SoftTakeover: false), registry);

        binding.Apply(0);
        Assert.Equal(-60, box.Value, 5);
        binding.Apply(127);
        Assert.Equal(0, box.Value, 5);
        binding.Apply(64);
        Assert.Equal(-60 + 60 * (64 / 127d), box.Value, 3);
    }

    [Fact]
    public void HonoursAnExplicitOutputRange()
    {
        var registry = RegistryWith(Trim(), -60, out var box);
        // Ride only the top 10 dB, which is what an operator usually wants a fader for.
        var binding = new ContinuousBinding(
            new ContinuousBindingSpec("master.trim", OutputMin: -10, OutputMax: 0, SoftTakeover: false),
            registry);

        binding.Apply(0);
        Assert.Equal(-10, box.Value, 5);
        binding.Apply(127);
        Assert.Equal(0, box.Value, 5);
    }

    [Fact]
    public void TargetMappingCurve_ShapesControllerTravel()
    {
        var registry = RegistryWith(
            new ParameterTarget("curved", "Curved", 0, 1, MappingCurve: FadeCurve.Exponential),
            0,
            out var box);
        var binding = new ContinuousBinding(
            new ContinuousBindingSpec("curved", InputMin: 0, InputMax: 100, SoftTakeover: false),
            registry);

        Assert.True(binding.Apply(50));
        Assert.Equal(0.125, box.Value, 4);
    }

    [Fact]
    public void SoftTakeover_IgnoresTheControl_UntilItCatchesTheCurrentValue()
    {
        // The parameter sits at -30 dB; the physical fader is at the bottom.
        var registry = RegistryWith(Trim(), -30, out var box);
        var binding = new ContinuousBinding(new ContinuousBindingSpec("master.trim"), registry);

        // Touching the fader at its own position must NOT jump the level to -60.
        Assert.False(binding.Apply(0));
        Assert.Equal(-30, box.Value, 5);
        Assert.False(binding.IsLatched);

        // Sweeping up, still short of the value: still ignored.
        Assert.False(binding.Apply(40));
        Assert.Equal(-30, box.Value, 5);

        // Passing through the current value latches, and from then on the fader is authoritative.
        Assert.True(binding.Apply(64));
        Assert.True(binding.IsLatched);
        Assert.True(binding.Apply(127));
        Assert.Equal(0, box.Value, 5);
    }

    [Fact]
    public void SoftTakeoverDisabled_IsAuthoritativeImmediately()
    {
        var registry = RegistryWith(Trim(), -30, out var box);
        var binding = new ContinuousBinding(
            new ContinuousBindingSpec("master.trim", SoftTakeover: false), registry);

        // Motorised faders already sit at the right place, so waiting would be wrong.
        Assert.True(binding.Apply(0));
        Assert.Equal(-60, box.Value, 5);
    }

    [Fact]
    public void ReleaseLatch_MakesTheControlCatchUpAgain()
    {
        var registry = RegistryWith(Trim(), -60, out var box);
        var binding = new ContinuousBinding(new ContinuousBindingSpec("master.trim"), registry);
        Assert.True(binding.Apply(0));
        Assert.True(binding.IsLatched);

        // Something else moved the level - a cue, a snapshot recall - so the fader is stale again.
        box.Value = -10;
        binding.ReleaseLatch();

        Assert.False(binding.IsLatched);
        Assert.False(binding.Apply(0));
        Assert.Equal(-10, box.Value, 5);
    }

    [Fact]
    public void ToleranceIsNormalized_SoItMeansTheSameAcrossParameters()
    {
        // A wide dB range and a 0..1 range must behave identically for the same tolerance.
        var wide = RegistryWith(new ParameterTarget("wide", "Wide", -60, 0), -30, out _);
        var unit = RegistryWith(new ParameterTarget("unit", "Unit", 0, 1), 0.5, out _);

        var wideBinding = new ContinuousBinding(new ContinuousBindingSpec("wide"), wide);
        var unitBinding = new ContinuousBinding(new ContinuousBindingSpec("unit"), unit);

        Assert.Equal(wideBinding.Apply(64), unitBinding.Apply(64));
        Assert.Equal(wideBinding.IsLatched, unitBinding.IsLatched);
    }

    // --- coalescing ---------------------------------------------------------------------------

    [Fact]
    public void Coalescer_PassesTheFirstValue_ThenRateLimits()
    {
        var writer = new CoalescingParameterWriter(TimeSpan.FromMilliseconds(40));

        Assert.True(writer.TryAccept(0.1, TimeSpan.Zero, out var first));
        Assert.Equal(0.1, first, 5);
        Assert.False(writer.TryAccept(0.2, TimeSpan.FromMilliseconds(10), out _));
        Assert.False(writer.TryAccept(0.3, TimeSpan.FromMilliseconds(20), out _));
        Assert.True(writer.TryAccept(0.4, TimeSpan.FromMilliseconds(50), out var later));
        Assert.Equal(0.4, later, 5);
    }

    [Fact]
    public void Coalescer_KeepsOnlyTheNewestHeldValue()
    {
        var writer = new CoalescingParameterWriter(TimeSpan.FromMilliseconds(40));
        writer.TryAccept(0.1, TimeSpan.Zero, out _);

        // A sweep: three intermediate positions arrive inside one interval. An intermediate fader
        // position has no meaning once a newer one exists, so only the last survives.
        writer.TryAccept(0.2, TimeSpan.FromMilliseconds(5), out _);
        writer.TryAccept(0.3, TimeSpan.FromMilliseconds(10), out _);
        writer.TryAccept(0.4, TimeSpan.FromMilliseconds(15), out _);

        Assert.True(writer.TryFlush(TimeSpan.FromMilliseconds(45), out var flushed));
        Assert.Equal(0.4, flushed, 5);
    }

    [Fact]
    public void Coalescer_FlushesNothingWhenIdle_AndNotBeforeTheInterval()
    {
        var writer = new CoalescingParameterWriter(TimeSpan.FromMilliseconds(40));
        writer.TryAccept(0.1, TimeSpan.Zero, out _);

        Assert.False(writer.TryFlush(TimeSpan.FromMilliseconds(50), out _)); // nothing pending
        writer.TryAccept(0.9, TimeSpan.FromMilliseconds(55), out _);         // becomes pending
        Assert.False(writer.TryFlush(TimeSpan.FromMilliseconds(60), out _)); // interval not elapsed
    }

    [Fact]
    public void Coalescer_LandsOnTheFinalPosition_WhenASweepStopsMidInterval()
    {
        var writer = new CoalescingParameterWriter(TimeSpan.FromMilliseconds(40));
        writer.TryAccept(0, TimeSpan.Zero, out _);
        writer.TryAccept(1.0, TimeSpan.FromMilliseconds(20), out _); // fader released here

        // Without the flush the parameter would rest short of where the operator left the fader.
        Assert.True(writer.TryFlush(TimeSpan.FromMilliseconds(80), out var final));
        Assert.Equal(1.0, final, 5);
    }
}
