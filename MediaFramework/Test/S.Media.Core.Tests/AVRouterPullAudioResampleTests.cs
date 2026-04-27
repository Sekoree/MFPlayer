using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Regression coverage for the pull-callback audio path in
/// <see cref="AVRouter"/> when the source rate differs from the endpoint
/// rate.
///
/// <para>
/// Bug history: <c>AudioFillCallbackForEndpoint.Fill</c> originally called
/// <c>channel.FillBuffer(srcSpan, frameCount)</c> using the <b>output</b>
/// frame count even when a resampler was attached. For 44.1 kHz → 48 kHz
/// playback that meant pulling ~9% more source frames than the resampler
/// actually consumed each callback. The unconsumed tail accumulated in the
/// resampler's <c>_pendingBuf</c>, which (a) caused the source channel's
/// <c>Position</c> to advance at <c>srcRate/dstRate × wall time</c> and
/// produce false A/V drift readings, and (b) forced the resampler to
/// allocate progressively larger combined buffers on the RT thread, with
/// audible crackling/stutter as a result.
/// </para>
///
/// <para>
/// The fix mirrors the push-tick path: ask the resampler via
/// <see cref="IAudioResampler.GetRequiredInputFrames"/> how many input
/// frames it needs to produce <c>frameCount</c> output frames and pull
/// exactly that many.
/// </para>
/// </summary>
public sealed class AVRouterPullAudioResampleTests
{
    [Fact]
    public async Task PullAudio_44k_to_48k_DoesNotOverpullSourceFrames()
    {
        const int srcRate          = 44_100;
        const int dstRate          = 48_000;
        const int outFramesPerCall = 1024;

        // Resampler from 44.1k → 48k expects ~941 source frames per 1024
        // output frames. Anything substantially larger means we're walking
        // the resampler's pending buffer up — the bug we're guarding
        // against.
        const int    upperBoundInputFrames = 960;
        const double expectedRatio         = (double)srcRate / dstRate;

        using var router = new AVRouter();

        var srcChannel = new RecordingAudioChannel(srcRate, channels: 2);
        var endpoint   = new FakePullAudioEndpoint(dstRate, channels: 2, framesPerBuffer: outFramesPerCall);

        var inputId    = router.RegisterAudioInput(srcChannel);
        var endpointId = router.RegisterEndpoint(endpoint);

        // CreateRoute auto-attaches a LinearResampler when rates differ.
        router.CreateRoute(inputId, endpointId);

        await router.StartAsync();
        try
        {
            Assert.NotNull(endpoint.FillCallback);

            var dest = new float[outFramesPerCall * endpoint.EndpointFormat.Channels];

            // Drive a few callbacks; collect the first N input requests.
            const int callbacks = 16;
            for (int i = 0; i < callbacks; i++)
            {
                Array.Clear(dest);
                endpoint.FillCallback!.Fill(dest, outFramesPerCall, endpoint.EndpointFormat);
            }

            // No call should have over-pulled the source: each request
            // must be near the steady-state ~941 frames, never the full
            // output frame count.
            var observed = srcChannel.RequestedFrameCounts;
            Assert.NotEmpty(observed);
            foreach (int requested in observed)
            {
                Assert.True(
                    requested <= upperBoundInputFrames,
                    $"Pull callback requested {requested} source frames; expected ≤ {upperBoundInputFrames} " +
                    $"(srcRate={srcRate}, dstRate={dstRate}, outFrames={outFramesPerCall}). " +
                    "This is the symptom of the over-pull regression that grows the resampler's " +
                    "pending buffer and causes audible stutter + false A/V drift.");
            }

            // Average should also land near the ratio (allow a loose ±2 %
            // band to absorb the resampler's per-call rounding).
            double avg = 0;
            foreach (int r in observed) avg += r;
            avg /= observed.Count;

            double expectedFrames = outFramesPerCall * expectedRatio;
            Assert.InRange(avg, expectedFrames - 8, expectedFrames + 8);
        }
        finally
        {
            await router.StopAsync();
        }
    }

    // ── Test stubs ────────────────────────────────────────────────────────

    /// <summary>
    /// IAudioChannel that records the <c>frameCount</c> argument of every
    /// <see cref="FillBuffer"/> call but otherwise returns silence.
    /// Recording happens on the RT-style fill thread so the list is guarded
    /// by a lock; the test reads it after the router stops.
    /// </summary>
    private sealed class RecordingAudioChannel : IAudioChannel
    {
        private readonly object             _lock     = new();
        private readonly List<int>          _requests = new();

        public RecordingAudioChannel(int rate, int channels)
            => SourceFormat = new AudioFormat(rate, channels);

        public Guid        Id              { get; } = Guid.NewGuid();
        public bool        IsOpen          => true;
        public bool        CanSeek         => false;
        public AudioFormat SourceFormat    { get; }
        public float       Volume          { get; set; } = 1f;
        public TimeSpan    Position        => TimeSpan.Zero;
        public int         BufferDepth     => 8;
        public int         BufferAvailable => 0;

        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler?                          EndOfStream    { add { } remove { } }

        public IReadOnlyList<int> RequestedFrameCounts
        {
            get
            {
                lock (_lock) return _requests.ToArray();
            }
        }

        public int FillBuffer(Span<float> dest, int frameCount)
        {
            lock (_lock) _requests.Add(frameCount);
            dest.Clear();
            return 0;
        }

        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Minimal <see cref="IPullAudioEndpoint"/> stub: the router writes a
    /// fill callback into <see cref="FillCallback"/> at registration time;
    /// the test invokes it manually to simulate the hardware RT thread.
    /// </summary>
    private sealed class FakePullAudioEndpoint : IPullAudioEndpoint
    {
        private IAudioFillCallback? _fillCallback;

        public FakePullAudioEndpoint(int rate, int channels, int framesPerBuffer)
        {
            EndpointFormat  = new AudioFormat(rate, channels);
            FramesPerBuffer = framesPerBuffer;
        }

        public string      Name            => "FakePullAudioEndpoint";
        public bool        IsRunning       { get; private set; }
        public AudioFormat EndpointFormat  { get; }
        public int         FramesPerBuffer { get; }

        public IAudioFillCallback? FillCallback
        {
            get => Volatile.Read(ref _fillCallback);
            set => Volatile.Write(ref _fillCallback, value);
        }

        public Task StartAsync(CancellationToken ct = default) { IsRunning = true;  return Task.CompletedTask; }
        public Task StopAsync (CancellationToken ct = default) { IsRunning = false; return Task.CompletedTask; }

        public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts)
        {
            // Pull endpoint — push path is unused.
        }

        public void Dispose() { FillCallback = null; }
    }
}
