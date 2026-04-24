using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Covers the priority-resolution rule introduced in §4.8 / R11:
/// <list type="number">
///   <item>An endpoint that overrode <see cref="IClockCapableEndpoint.DefaultPriority"/>
///         wins — virtual endpoints register at <see cref="ClockPriority.Internal"/>
///         and therefore cannot outrank a real hardware clock.</item>
///   <item>An endpoint that left <see cref="IClockCapableEndpoint.DefaultPriority"/>
///         at the <see cref="ClockPriority.Hardware"/> default defers to
///         <see cref="AVRouterOptions.DefaultEndpointClockPriority"/>.</item>
/// </list>
/// </summary>
public sealed class EndpointClockPriorityTests
{
    [Fact]
    public void VirtualClockEndpoint_DeclaresInternalPriority()
    {
        using var ep = new VirtualClockEndpoint();
        Assert.Equal(ClockPriority.Internal, ((IClockCapableEndpoint)ep).DefaultPriority);
    }

    [Fact]
    public void Register_VirtualThenHardware_HardwareClockWins()
    {
        using var router = new AVRouter();
        using var virt   = new VirtualClockEndpoint();
        using var hw     = new HardwareCapableStub();

        router.RegisterEndpoint(virt);
        Assert.Same(virt.Clock, router.Clock);          // only clock so far

        router.RegisterEndpoint(hw);
        Assert.Same(hw.Clock, router.Clock);            // hardware outranks internal
    }

    [Fact]
    public void Register_ExternalDeclaring_EndpointOutranksHardwareRegisteredEarlier()
    {
        using var router = new AVRouter();
        using var hw     = new HardwareCapableStub();
        using var ext    = new ExternalCapableStub();

        router.RegisterEndpoint(hw);
        router.RegisterEndpoint(ext);

        Assert.Same(ext.Clock, router.Clock);
    }

    // ── Minimal stubs ───────────────────────────────────────────────────────

    private sealed class HardwareCapableStub : IAudioEndpoint, IClockCapableEndpoint
    {
        private readonly StopwatchClock _clock = new(TimeSpan.FromMilliseconds(10));
        public string Name => "HardwareCapableStub";
        public bool   IsRunning => false;
        public Task   StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task   StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void   ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts) { }
        public IMediaClock Clock => _clock;
        // Uses the interface default (Hardware).
        public void Dispose() => _clock.Dispose();
    }

    private sealed class ExternalCapableStub : IAudioEndpoint, IClockCapableEndpoint
    {
        private readonly StopwatchClock _clock = new(TimeSpan.FromMilliseconds(10));
        public string Name => "ExternalCapableStub";
        public bool   IsRunning => false;
        public Task   StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task   StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void   ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts) { }
        public IMediaClock    Clock           => _clock;
        public ClockPriority  DefaultPriority => ClockPriority.External;
        public void Dispose() => _clock.Dispose();
    }
}
