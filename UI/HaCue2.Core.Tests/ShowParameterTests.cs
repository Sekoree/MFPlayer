using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Control;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The values a control surface may ride (register item 24).
/// </summary>
/// <remarks>
/// The registry and the soft-takeover arithmetic are the framework's and are tested there. What is
/// tested here is HaCue2's own answer to "which values does a cue player offer, over what range, and
/// reading what" — including the property that makes soft takeover work at all: a parameter must read
/// what is true NOW, not a cached number.
/// </remarks>
public class ShowParameterTests
{
    private static ParameterRegistry Build(
        out Func<double> readTrim, out List<double> written, HaCueProject? project = null)
    {
        var trim = 0d;
        var writes = new List<double>();
        var document = project ?? new HaCueProject();

        readTrim = () => trim;
        written = writes;

        return ShowParameters.Build(
            () => trim,
            value => { trim = value; writes.Add(value); },
            () => document,
            value => document.Audition.LevelDb = value);
    }

    [Fact]
    public void TheShowOffersMasterTrimAndAuditionLevel()
    {
        var registry = Build(out _, out _);
        var ids = ShowParameters.Describe(registry).Select(target => target.Id).ToList();

        Assert.Contains(ShowParameters.MasterTrim, ids);
        Assert.Contains(ShowParameters.AuditionLevel, ids);
    }

    [Fact]
    public void ParametersUseTheSameRangeTheAppsOwnFieldsAccept()
    {
        var registry = Build(out _, out _);

        Assert.True(registry.TryGetTarget(ShowParameters.MasterTrim, out var trim));
        // A fader and a typed value must not disagree about what the ends mean.
        Assert.Equal(GainRange.SilenceFloorDb, trim.Minimum, 3);
        Assert.Equal(12, trim.Maximum, 3);
        Assert.Equal("dB", trim.Unit);
    }

    [Fact]
    public void AParameterReadsWhatIsTrueNow()
    {
        var project = new HaCueProject();
        var registry = Build(out _, out _, project);

        project.Audition.LevelDb = -18;

        // The registry holds delegates, not values. A cached number would make a fader latch against
        // something the show had already moved past — which is soft takeover latching on a lie.
        Assert.True(registry.TryGet(ShowParameters.AuditionLevel, out var level));
        Assert.Equal(-18, level, 3);
    }

    [Fact]
    public void WritingAParameterReachesTheAccessor()
    {
        var registry = Build(out var readTrim, out var written);

        Assert.True(registry.TrySet(ShowParameters.MasterTrim, -6));

        Assert.Equal(-6, readTrim(), 3);
        Assert.Single(written);
    }

    [Fact]
    public void AValueOutsideTheRangeIsClamped()
    {
        var registry = Build(out var readTrim, out _);

        registry.TrySet(ShowParameters.MasterTrim, 999);

        Assert.Equal(12, readTrim(), 3);
    }

    [Fact]
    public void AnUnknownParameterIsRefused()
    {
        var registry = Build(out _, out var written);

        Assert.False(registry.TrySet("nothing.here", 1));
        Assert.Empty(written);
    }

    [Fact]
    public void SoftTakeoverIgnoresAControlUntilItCatchesTheValue()
    {
        var registry = Build(out var readTrim, out _);
        registry.TrySet(ShowParameters.MasterTrim, 0);

        var binding = new ContinuousBinding(
            new ContinuousBindingSpec(
                ShowParameters.MasterTrim, InputMin: GainRange.SilenceFloorDb, InputMax: 12),
            registry);

        // The fader is down at the floor while the trim sits at 0 dB. Applying its position on the
        // first move would drop the show to silence, audibly, mid-cue.
        Assert.False(binding.Apply(GainRange.SilenceFloorDb));
        Assert.Equal(0, readTrim(), 3);

        // Once it travels up to where the value actually is, it latches and takes over.
        Assert.True(binding.Apply(0));
        Assert.True(binding.IsLatched);

        Assert.True(binding.Apply(-6));
        Assert.Equal(-6, readTrim(), 3);
    }

    [Fact]
    public void TheDescribedListIsStableAndNamed()
    {
        var registry = Build(out _, out _);
        var described = ShowParameters.Describe(registry);

        // A binding editor lists these; an unnamed or unordered set would reshuffle under the operator
        // between one session and the next.
        Assert.All(described, target => Assert.False(string.IsNullOrWhiteSpace(target.DisplayName)));
        Assert.Equal(
            described.Select(target => target.DisplayName).OrderBy(name => name, StringComparer.Ordinal),
            described.Select(target => target.DisplayName));
    }
}
