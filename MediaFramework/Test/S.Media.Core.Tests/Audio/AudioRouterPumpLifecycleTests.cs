using System.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioRouterPumpLifecycleTests
{
    private const int SampleRate = 48_000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    [Fact]
    public void OutputAddedBeforeStart_ReceivesAudio_AfterStart()
    {
        // Lazy pump-thread start (P2-1): the drainer Thread is created idle at AddOutput time
        // and only .Start()ed when the router runs (avoids one OS thread per output for routers
        // that never start - the suite-level thread-pressure / OOM source). If Start() forgets to
        // EnsureStarted the registered pumps, committed chunks never reach the output. Guard it.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var output = new RecordingOutput(Stereo);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Assert.True(SpinUntil(() => output.SubmitCount > 0, 2000),
            "output added before Start should receive audio once the router runs");
        r.Stop();
    }

    [Fact]
    public void OutputAddedWhileRunning_ReceivesAudio()
    {
        // The other EnsureStarted call site: an output added to an already-running router must
        // start its drainer immediately, otherwise it silently never drains.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.Start();
        Thread.Sleep(30);

        var output = new RecordingOutput(Stereo);
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        Assert.True(SpinUntil(() => output.SubmitCount > 0, 2000),
            "output added while running should start draining immediately");
        r.Stop();
    }

    [Fact]
    public void ClockedOutputAddedWhileRunning_DoesNotThrow_AndReceivesAudio()
    {
        // Regression: hot-wiring an audio device (PortAudio is IClockedOutput + IPlaybackClock) into a
        // running router used to throw "cannot slave clock while router is running" - AutoWirePrimary
        // tried to promote the first clocked output to pacing primary mid-stream. A running router must
        // instead keep the new clocked output as a non-primary slave (no mid-stream re-clock).
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        r.AddSource(new SilenceSource(Stereo), "src");
        // Start with only a non-clocked sink routed (the "video playing, no audio output" case: the
        // framework wires a discard sink so the source is consumed and the router runs at wall clock).
        r.AddOutput(new RecordingOutput(Stereo), "discard");
        r.AddRoute("src", "discard", ChannelMap.Identity(2));
        r.Start();
        Thread.Sleep(30);
        Assert.True(r.IsRunning);

        var clocked = new ClockedRecordingOutput(Stereo);
        var ex = Record.Exception(() =>
        {
            var id = r.AddOutput(clocked, "device");
            r.AddRoute("src", id, ChannelMap.Identity(2));
        });

        Assert.Null(ex);
        Assert.True(SpinUntil(() => clocked.SubmitCount > 0, 2000),
            "a clocked output hot-wired into a running router should still receive audio");
        // The hot-wired clocked output must NOT have hijacked the router as the pacing primary.
        Assert.Null(r.PrimaryOutputId);
        r.Stop();
    }

    [Fact]
    public void OutputAddedBeforeStart_DisposeBeforeStart_DoesNotThrow()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(new RecordingOutput(Stereo), "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        var ex = Record.Exception(r.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void NormalPumpLifecycle_DoesNotReportStuck()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var output = new RecordingOutput(Stereo);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Assert.True(SpinUntil(() => output.SubmitCount > 0, 2000),
            "normal pump should process at least one chunk");
        r.Stop();

        Assert.False(r.GetPumpStats("out").IsStuck);
        Assert.Empty(r.StuckOutputPumpIds);
    }

    [Fact]
    public async Task Dispose_WithOutputBlockedInSubmit_ReturnsWithoutDisposingLivePumpState()
    {
        var output = new BlockingOutput(Stereo);
        var router = new AudioRouter(SampleRate, chunkSamples: 64);
        router.AddSource(new SilenceSource(Stereo), "src");
        router.AddOutput(output, "out");
        router.AddRoute("src", "out", ChannelMap.Identity(2));
        Task? disposeTask = null;

        try
        {
            router.Start();
            Assert.True(output.Entered.Wait(TimeSpan.FromSeconds(2)), "output pump should be blocked inside Submit");

            disposeTask = Task.Run(router.Dispose);
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(7))) == disposeTask;

            Assert.True(completed,
                "router dispose should return after bounded pump join attempts even when Submit remains blocked");
            await disposeTask;
            Assert.Contains("out", router.StuckOutputPumpIds);
        }
        finally
        {
            output.Release();
            router.Dispose();
            if (disposeTask is { IsCompleted: false })
                await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public void RemoveOutput_WedgedPump_QuarantinesInBackground_WithoutStallingTheRunLoop()
    {
        // HaCue quarantine (plan Phase 3): removing an output whose drainer is wedged in a native
        // Submit must not stall the mix. The pump teardown joins the drainer for up to ~3s; that
        // join used to run INLINE on the run loop thread (top-of-iteration pending-dispose drain),
        // freezing every other output for the full cap. It now runs on the thread pool, and the
        // leak is reported afterwards via the stuck flag + a quarantine OutputErrored event.
        var wedged = new BlockingOutput(Stereo);
        var healthy = new RecordingOutput(Stereo);
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var errors = new List<AudioRouterOutputErrorEventArgs>();
        r.OutputErrored += (_, e) => { lock (errors) errors.Add(e); };
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(healthy, "good");
        r.AddOutput(wedged, "bad");
        r.AddRoute("src", "good", ChannelMap.Identity(2));
        r.AddRoute("src", "bad", ChannelMap.Identity(2));

        try
        {
            r.Start();
            Assert.True(wedged.Entered.Wait(TimeSpan.FromSeconds(2)), "wedged output should be blocked inside Submit");
            Assert.True(SpinUntil(() => healthy.SubmitCount > 0, 2000), "healthy output should be flowing");

            var sw = Stopwatch.StartNew();
            Assert.True(r.RemoveOutput("bad"));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"RemoveOutput must not join the wedged drainer inline (took {sw.ElapsedMilliseconds}ms)");

            // Continuity: the run loop keeps mixing while the quarantine join runs in the background.
            //
            // Phrased as "does it make progress at all, given time" rather than "does it hit a throughput
            // floor inside a fixed sleep". The property under test is stalled-vs-running, and a stalled loop
            // delivers exactly 0 no matter how long you wait - so a generous poll separates the two just as
            // sharply while a rate assertion also fails whenever the machine is merely busy (this flaked
            // roughly 1 run in 10 under a full-solution run).
            var before = healthy.SubmitCount;
            Assert.True(
                SpinUntil(() => healthy.SubmitCount - before > 20, 5000),
                $"run loop stalled during quarantine: only {healthy.SubmitCount - before} chunks in 5s");

            // The background teardown eventually gives up joining, leaks the pump, and reports it.
            Assert.True(SpinUntil(() => r.StuckOutputPumpIds.Contains("bad"), 6000),
                "wedged pump was never flagged as quarantined");
            lock (errors)
                Assert.Contains(errors, e => e.OutputId == "bad"
                    && e.Exception is TimeoutException t && t.Message.Contains("quarantined"));

            // Hot-swap: a fresh device under the same id starts healthy - stale flag cleared, audio flows.
            var replacement = new RecordingOutput(Stereo);
            r.AddOutput(replacement, "bad");
            r.AddRoute("src", "bad", ChannelMap.Identity(2));
            Assert.DoesNotContain("bad", r.StuckOutputPumpIds);
            Assert.True(SpinUntil(() => replacement.SubmitCount > 0, 2000),
                "hot-swapped replacement output should receive audio");
            r.Stop();
        }
        finally
        {
            wedged.Release(); // let the leaked drainer thread exit
        }
    }

    [Fact]
    public void EvictionDrops_DoNotStall_StopWaitForIdle()
    {
        // Regression: Commit's pool-exhaustion eviction removed an already-enqueued chunk from _ready but did
        // not mark it "no longer in flight" (only counted the drop). So processed stayed permanently below
        // enqueued and WaitForIdle - which Pause/Stop/Seek call to quiesce a pump - spun to its FULL timeout
        // after ANY drop. The _audio_discard negotiation-lead sink hit this on every deck stop (~1s stall).
        // A blocked non-primary output floods the pump until it evicts; after release, Stop's per-pump
        // WaitForIdle must return at once (evictions counted as settled), not burn the ~1s timeout.
        var output = new BlockingOutput(Stereo);
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Assert.True(output.Entered.Wait(TimeSpan.FromSeconds(2)), "output pump should block inside Submit");
        // With the drainer stuck in Submit, the producer floods the pump; once the buffer pool is exhausted
        // Commit evicts the oldest queued chunk (a drop). Wait until at least one eviction is recorded.
        Assert.True(SpinUntil(() => r.GetPumpStats("out").Dropped > 0, 2000),
            "a blocked non-primary output should force eviction drops");

        output.Release();
        var sw = Stopwatch.StartNew();
        r.Stop();
        sw.Stop();

        Assert.True(r.GetPumpStats("out").Dropped > 0, "the scenario must have produced eviction drops");
        // Fixed: Stop returns immediately. Broken: it spins the full ~1s WaitForIdle timeout. 500ms decisively
        // separates the two while leaving generous slack for scheduling under a loaded test run.
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Stop should not stall on WaitForIdle after eviction drops (took {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void DisposedOutput_IsReportedOnce_AndThePumpKeepsDraining()
    {
        // Regression (HaCue app close): the host disposed a PortAudio output while its pump was
        // still draining, and every queued chunk then threw ObjectDisposedException out of
        // Submit - one OutputErrored per chunk, reading as "an exception is thrown on close".
        // The pump must report the disposed sink ONCE, then keep recycling chunks quietly so
        // WaitForIdle/Stop still see progress.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var output = new DisposableOutput(Stereo);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        var disposedErrors = 0;
        r.OutputErrored += (_, e) =>
        {
            if (e.Exception is ObjectDisposedException)
                Interlocked.Increment(ref disposedErrors);
        };

        r.Start();
        Assert.True(SpinUntil(() => output.SubmitCount > 0, 2000), "output should be flowing before disposal");
        output.Dispose(); // the host tears the device down under the running pump

        Assert.True(SpinUntil(() => Volatile.Read(ref disposedErrors) > 0, 2000),
            "the disposed sink must be reported");
        // The pump keeps consuming (chunks recycled, processed rising) without further error spam.
        var processedAtError = r.GetPumpStats("out").Processed;
        Assert.True(SpinUntil(() => r.GetPumpStats("out").Processed > processedAtError + 2, 2000),
            "the pump should keep draining after the sink is disposed");
        Assert.Equal(1, Volatile.Read(ref disposedErrors));

        var sw = Stopwatch.StartNew();
        r.Stop();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Stop should not stall after sink disposal (took {sw.ElapsedMilliseconds}ms)");
    }

    private static bool SpinUntil(Func<bool> cond, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    private sealed class SilenceSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst) { dst.Clear(); return dst.Length; }
    }

    private sealed class RecordingOutput(AudioFormat fmt) : IAudioOutput
    {
        private int _submits;
        public int SubmitCount => Volatile.Read(ref _submits);
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) => Interlocked.Increment(ref _submits);
    }

    /// <summary>Models a hardware output whose host disposes it under a running pump - Submit
    /// throws <see cref="ObjectDisposedException"/> from then on, like <c>PortAudioOutput</c>.</summary>
    private sealed class DisposableOutput(AudioFormat fmt) : IAudioOutput, IDisposable
    {
        private int _submits;
        private volatile bool _disposed;
        public int SubmitCount => Volatile.Read(ref _submits);
        public AudioFormat Format { get; } = fmt;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _submits);
        }

        public void Dispose() => _disposed = true;
    }

    /// <summary>Models a hardware device: clocked (paces the router when promoted) and exposes a playback
    /// clock, exactly like <c>PortAudioOutput</c>. Always ready to accept a chunk.</summary>
    private sealed class ClockedRecordingOutput(AudioFormat fmt) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        private int _submits;
        public int SubmitCount => Volatile.Read(ref _submits);
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) => Interlocked.Increment(ref _submits);
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;
        public bool IsAdvancing => true;
    }

    private sealed class BlockingOutput(AudioFormat fmt) : IAudioOutput
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);
        public AudioFormat Format { get; } = fmt;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            Entered.Set();
            _release.Wait();
        }

        public void Release() => _release.Set();
    }
}
