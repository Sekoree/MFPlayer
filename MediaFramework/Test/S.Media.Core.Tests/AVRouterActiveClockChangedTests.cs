using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// §4.9 / R10 — <see cref="AVRouter.ActiveClockChanged"/> fires whenever the
/// resolver picks a new active clock, outside the router's <c>_clockLock</c>
/// so subscribers may call back into the router safely. These tests pin the
/// event wiring for the three mutation entry points
/// (<c>RegisterClock</c> / <c>UnregisterClock</c> / <c>SetClock</c>) and the
/// endpoint-auto-registration path.
/// </summary>
public sealed class AVRouterActiveClockChangedTests
{
    [Fact]
    public void RegisterClock_Higher_Priority_FiresEvent()
    {
        using var router = new AVRouter();
        var fired = new List<IMediaClock>();
        router.ActiveClockChanged += c => fired.Add(c);

        using var hw  = new StopwatchClock(TimeSpan.FromMilliseconds(10));
        using var ext = new StopwatchClock(TimeSpan.FromMilliseconds(10));

        router.RegisterClock(hw,  ClockPriority.Hardware);
        router.RegisterClock(ext, ClockPriority.External);

        Assert.Equal(2, fired.Count);
        Assert.Same(hw,  fired[0]);
        Assert.Same(ext, fired[1]);
    }

    [Fact]
    public void UnregisterClock_ActiveRemoved_FallsBackToNextHighest()
    {
        using var router = new AVRouter();
        using var hw  = new StopwatchClock(TimeSpan.FromMilliseconds(10));
        using var ext = new StopwatchClock(TimeSpan.FromMilliseconds(10));

        router.RegisterClock(hw,  ClockPriority.Hardware);
        router.RegisterClock(ext, ClockPriority.External);

        var fired = new List<IMediaClock>();
        router.ActiveClockChanged += c => fired.Add(c);

        router.UnregisterClock(ext);  // should fall back to hw
        Assert.Single(fired);
        Assert.Same(hw, fired[0]);
    }

    [Fact]
    public void SetClock_Override_FiresAndThenClears()
    {
        using var router = new AVRouter();
        using var hw  = new StopwatchClock(TimeSpan.FromMilliseconds(10));
        using var ovr = new StopwatchClock(TimeSpan.FromMilliseconds(10));
        router.RegisterClock(hw, ClockPriority.Hardware);

        var fired = new List<IMediaClock>();
        router.ActiveClockChanged += c => fired.Add(c);

        router.SetClock(ovr);   // override takes priority
        router.SetClock(null);  // cleared → back to hw

        Assert.Equal(2, fired.Count);
        Assert.Same(ovr, fired[0]);
        Assert.Same(hw,  fired[1]);
    }

    [Fact]
    public void RegisterSameClockTwice_NoRedundantEvent()
    {
        using var router = new AVRouter();
        using var hw = new StopwatchClock(TimeSpan.FromMilliseconds(10));
        router.RegisterClock(hw, ClockPriority.Hardware);

        int count = 0;
        router.ActiveClockChanged += _ => count++;

        router.RegisterClock(hw, ClockPriority.Hardware); // same identity → resolved stays same
        Assert.Equal(0, count);
    }
}

