using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Covers §5.5: the router's effective push-tick cadence is the minimum of
/// <see cref="AVRouterOptions.InternalTickCadence"/> and every registered
/// endpoint's <see cref="IAudioEndpoint.NominalTickCadence"/> /
/// <see cref="IVideoEndpoint.NominalTickCadence"/>. Registration and
/// unregistration both update the effective cadence.
/// </summary>
public sealed class AVRouterEffectiveCadenceTests
{
    [Fact]
    public void EffectiveCadence_NoEndpoints_MatchesOptions()
    {
        using var router = new AVRouter(new AVRouterOptions { InternalTickCadence = TimeSpan.FromMilliseconds(10) });
        Assert.Equal(TimeSpan.FromMilliseconds(10), router.EffectiveTickCadence);
    }

    [Fact]
    public void EffectiveCadence_FastEndpointOverrides_OptionDefault()
    {
        using var router = new AVRouter(new AVRouterOptions { InternalTickCadence = TimeSpan.FromMilliseconds(10) });
        using var fast   = new CadenceHintStub(TimeSpan.FromMilliseconds(3));

        router.RegisterEndpoint(fast);

        Assert.Equal(TimeSpan.FromMilliseconds(3), router.EffectiveTickCadence);
    }

    [Fact]
    public void EffectiveCadence_SlowEndpoint_DoesNotInflateCadence()
    {
        using var router = new AVRouter(new AVRouterOptions { InternalTickCadence = TimeSpan.FromMilliseconds(10) });
        using var slow   = new CadenceHintStub(TimeSpan.FromMilliseconds(40));

        router.RegisterEndpoint(slow);

        // Slow endpoint must NOT override a faster options value.
        Assert.Equal(TimeSpan.FromMilliseconds(10), router.EffectiveTickCadence);
    }

    [Fact]
    public void EffectiveCadence_SubMillisecondHint_ClampedToOneMs()
    {
        using var router = new AVRouter(new AVRouterOptions { InternalTickCadence = TimeSpan.FromMilliseconds(10) });
        using var crazy  = new CadenceHintStub(TimeSpan.FromMicroseconds(50));

        router.RegisterEndpoint(crazy);

        Assert.Equal(TimeSpan.FromMilliseconds(1), router.EffectiveTickCadence);
    }

    [Fact]
    public void EffectiveCadence_UnregisterRestoresPreviousValue()
    {
        using var router = new AVRouter(new AVRouterOptions { InternalTickCadence = TimeSpan.FromMilliseconds(10) });
        using var fast   = new CadenceHintStub(TimeSpan.FromMilliseconds(3));

        var id = router.RegisterEndpoint(fast);
        Assert.Equal(TimeSpan.FromMilliseconds(3), router.EffectiveTickCadence);

        router.UnregisterEndpoint(id);
        Assert.Equal(TimeSpan.FromMilliseconds(10), router.EffectiveTickCadence);
    }

    [Fact]
    public void EffectiveCadence_AudioAndVideoSplit_IsolatesPaths()
    {
        // §6.7 — an audio-only endpoint hint must NOT lower the video tick
        // cadence, and vice versa.
        using var router = new AVRouter(new AVRouterOptions
        {
            InternalTickCadence = TimeSpan.FromMilliseconds(10),
            AudioTickCadence = TimeSpan.FromMilliseconds(10),
            VideoTickCadence = TimeSpan.FromMilliseconds(16),
        });
        using var fastAudio = new CadenceHintStub(TimeSpan.FromMilliseconds(3));

        router.RegisterEndpoint(fastAudio);

        // Audio picks up the 3 ms hint; video stays at its configured 16 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(3), router.EffectiveAudioTickCadence);
        Assert.Equal(TimeSpan.FromMilliseconds(16), router.EffectiveVideoTickCadence);
    }

    // Endpoint stub that advertises a NominalTickCadence hint.
    private sealed class CadenceHintStub : IAudioEndpoint
    {
        private readonly TimeSpan _cadence;
        public CadenceHintStub(TimeSpan cadence) { _cadence = cadence; }

        public string Name => $"CadenceStub({_cadence.TotalMilliseconds:F1}ms)";
        public bool   IsRunning => false;
        public Task   StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task   StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void   ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts) { }
        public TimeSpan? NominalTickCadence => _cadence;
        public void Dispose() { }
    }
}
