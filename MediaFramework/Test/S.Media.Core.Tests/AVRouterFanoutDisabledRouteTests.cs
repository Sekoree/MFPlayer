using System;
using System.Threading;
using System.Threading.Tasks;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Regression coverage for the audio fanout's behaviour when one of the
/// routes through a shared input is disabled.
///
/// <para>
/// Bug history: when an audio input fed two push endpoints (e.g. local
/// PortAudio + an NDI sink that the user explicitly set to video-only via
/// <c>SetAveStreamSelection(VideoOnly)</c>), the fanout's writer used
/// <c>WriteAsync(...)</c> on every subscriber and waited on a
/// <c>WhenAll</c>. The disabled route's push tick stopped draining its
/// fanout subscriber, but the fanout was still feeding it — so the
/// bounded ring filled up, the WhenAll-style write blocked indefinitely,
/// and every other audio route through the same input froze. The user
/// observed this as "playback completely stuck" the moment a video-only
/// NDI endpoint was added.
/// </para>
///
/// <para>
/// The fix mirrors a route's <c>Enabled</c> flag onto its fanout
/// subscriber. The fanout writer now skips disabled subscribers
/// (best-effort <c>TryWrite</c>) so a non-draining route can never
/// back-pressure a sibling.
/// </para>
/// </summary>
public sealed class AVRouterFanoutDisabledRouteTests
{
    [Fact]
    public async Task Fanout_DoesNotStall_WhenSiblingRouteIsDisabled()
    {
        var format    = new AudioFormat(48_000, 2);
        var inputCh   = new AudioChannel(format, bufferDepth: 4);
        var drainedEp = new DrainingPushEndpoint("drained");
        var stalledEp = new StalledPushEndpoint("stalled"); // never drains its subscriber

        using var router = new AVRouter();
        var inputId  = router.RegisterAudioInput(inputCh);
        var drainId  = router.RegisterEndpoint(drainedEp);
        var stallId  = router.RegisterEndpoint(stalledEp);
        var routeOk  = router.CreateRoute(inputId, drainId);
        var routeBad = router.CreateRoute(inputId, stallId);

        // Disable the sibling that doesn't drain — the exact pattern
        // SetAveStreamSelection(VideoOnly) uses on a video-only NDI sink
        // that still has an auto-created audio route.
        router.SetRouteEnabled(routeBad, false);

        await router.StartAsync();
        try
        {
            // Push enough audio to overflow the disabled sibling's
            // fanout ring many times over (4 chunks × 512 frames).
            // With the fix, the fanout's WriteAsync only waits on the
            // ENABLED subscriber, so this completes promptly. Without
            // the fix it deadlocks on the disabled subscriber's full
            // bounded ring and the test times out.
            var chunk = new float[512 * format.Channels];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 32; i++)
            {
                await inputCh.WriteAsync(chunk, cts.Token);
            }

            // Allow the fanout writer + push tick to deliver a few buffers
            // to the draining endpoint.
            for (int i = 0; i < 50 && drainedEp.ReceivedFrames == 0; i++)
                await Task.Delay(20, cts.Token);

            Assert.True(
                drainedEp.ReceivedFrames > 0,
                "Draining endpoint received no audio: the fanout writer " +
                "was likely stalled by the disabled sibling's full ring.");
        }
        finally
        {
            await router.StopAsync();
        }
    }

    // ── Test stubs ────────────────────────────────────────────────────────

    private abstract class FakePushEndpoint : IAudioEndpoint
    {
        protected FakePushEndpoint(string name) { Name = name; }
        public string Name { get; }
        public bool   IsRunning { get; private set; }
        public AudioFormat? NegotiatedFormat => new(48_000, 2);

        public Task StartAsync(CancellationToken ct = default) { IsRunning = true;  return Task.CompletedTask; }
        public Task StopAsync (CancellationToken ct = default) { IsRunning = false; return Task.CompletedTask; }

        public abstract void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts);

        public void Dispose() { }
    }

    /// <summary>Counts received frames so the test can assert delivery.</summary>
    private sealed class DrainingPushEndpoint : FakePushEndpoint
    {
        private long _receivedFrames;
        public DrainingPushEndpoint(string name) : base(name) { }
        public long ReceivedFrames => Interlocked.Read(ref _receivedFrames);
        public override void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts)
            => Interlocked.Add(ref _receivedFrames, frameCount);
    }

    /// <summary>
    /// Push endpoint whose audio path the router will skip because we
    /// disable its route. Stand-in for a video-only NDI sink whose
    /// auto-created audio route was disabled by
    /// <c>SetAveStreamSelection(VideoOnly)</c>.
    /// </summary>
    private sealed class StalledPushEndpoint : FakePushEndpoint
    {
        public StalledPushEndpoint(string name) : base(name) { }
        public override void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts)
        {
            // Disabled route — should never be invoked. If the router ever
            // routes audio here, the test still passes (drain endpoint is
            // independently checked); this is just a safety net.
        }
    }
}
