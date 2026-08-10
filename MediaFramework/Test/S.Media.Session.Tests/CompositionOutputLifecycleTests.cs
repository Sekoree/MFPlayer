using System.Diagnostics;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Per-output lifecycle isolation on the composition pump. The invariant under test everywhere here:
/// ONE output's slow moment (its sink's dispose joining a render thread, its first window/GL configure,
/// a configure that outright fails) must cost THAT output frames - never the canvas tick, and never a
/// sibling output. The measured failure modes these pin down: Retire used to dispose the pump/sink
/// while holding the same lifecycle gate TrySubmit takes on the tick thread (a tick that had captured
/// the pre-removal snapshot then froze every output for the entire dispose - up to 45 s for an SDL
/// sink), and Configure used to run synchronously on the tick thread (an ownsThread SDL sink's first
/// configure blocks unboundedly in window creation, starving every sibling).
/// </summary>
public sealed class CompositionOutputLifecycleTests
{
    private static readonly Rational CanvasRate = new(25, 1);

    private static ClipCompositionRuntime Runtime(params ClipCompositionOutputLease[] leases) =>
        new(new ClipCompositionDefinition("screen", "Screen", 64, 48, 25, 1), leases);

    private static VideoFrame Frame(int width, int height, TimeSpan pts = default)
    {
        var stride = width * 4;
        return new VideoFrame(
            pts,
            new VideoFormat(width, height, PixelFormat.Bgra32, CanvasRate),
            [new byte[stride * height]],
            [stride]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
            await Task.Delay(10);
    }

    // ---------------------------------------------------------------------------------------------
    // FIX 1: Retire must not hold the lifecycle gate across the pump/sink dispose.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetiringAnOutputWithASlowDispose_DoesNotStallTheTickForSiblingOutputs()
    {
        var sibling = new GateableCountingOutput();
        var slow = new SlowDisposeOutput(TimeSpan.FromSeconds(3));
        // Sibling FIRST: the tick walks the snapshot in lease order, so we can park it inside the
        // sibling's Submit while it still holds a snapshot containing the output about to retire -
        // the exact interleaving that used to block the canvas for the whole dispose.
        using var runtime = Runtime(
            new ClipCompositionOutputLease("sibling", "Sibling", sibling),
            new ClipCompositionOutputLease("slow", "Slow", slow, DisposeOutputOnRuntimeDispose: true));
        runtime.SetIdleFrame(Frame(64, 48));
        runtime.EnsurePumpStarted();

        // The tick is now parked inside sibling.Submit, pre-removal snapshot in hand.
        Assert.True(sibling.SubmitEntered.Wait(TimeSpan.FromSeconds(10)),
            "pump never reached the sibling output");

        var removal = Task.Run(() => runtime.RemoveOutput("slow"));
        Assert.True(slow.DisposeStarted.Wait(TimeSpan.FromSeconds(10)),
            "RemoveOutput never began disposing the sink");

        // The 3 s dispose is underway on the removal thread. Release the tick and time how long the
        // sibling waits for its next frames: with the dispose outside the gate the parked tick's
        // TrySubmit on the retired output refuses immediately and the very next ticks feed the
        // sibling; with the old under-gate dispose it sat on the retired output's gate for ~3 s.
        var baseline = sibling.SubmitCount;
        var sw = Stopwatch.StartNew();
        sibling.ReleaseSubmits();
        await WaitUntilAsync(() => sibling.SubmitCount >= baseline + 2, TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.True(sibling.SubmitCount >= baseline + 2,
            $"sibling received {sibling.SubmitCount - baseline} frames while a retired sibling disposed");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5),
            $"tick stalled {sw.Elapsed.TotalMilliseconds:0}ms behind a retired output's dispose");

        // The removal itself still pays for the dispose - on its own thread - and completes.
        Assert.True(await removal.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, slow.DisposeCount);
    }

    // ---------------------------------------------------------------------------------------------
    // FIX 2: Configure runs off the tick thread; failures surface once and don't wedge the output.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASlowFirstConfigure_OnOneOutput_DoesNotStarveSiblingOutputs()
    {
        var slow = new SlowConfigureOutput(TimeSpan.FromSeconds(2));
        var sibling = new CountingOutput();
        // Slow output FIRST, so the old synchronous-configure code path would have blocked the tick
        // before it ever reached the sibling.
        using var runtime = Runtime(
            new ClipCompositionOutputLease("slow", "Slow", slow),
            new ClipCompositionOutputLease("sibling", "Sibling", sibling));
        runtime.SetIdleFrame(Frame(64, 48));
        runtime.EnsurePumpStarted();

        Assert.True(slow.ConfigureStarted.Wait(TimeSpan.FromSeconds(10)),
            "slow output's configure never started");

        // While the 2 s first configure is still in flight, siblings must keep eating frames. Five
        // canvas frames is 200 ms of pump time - far inside the configure window, so a tick that was
        // blocked inside Configure could not possibly pass this.
        var sw = Stopwatch.StartNew();
        await WaitUntilAsync(() => sibling.SubmitCount >= 5, TimeSpan.FromSeconds(5));
        sw.Stop();
        Assert.True(sibling.SubmitCount >= 5,
            $"sibling got only {sibling.SubmitCount} frames during a sibling's slow configure");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5),
            $"sibling frames took {sw.Elapsed.TotalMilliseconds:0}ms - the tick waited on a configure");

        // And the slow output is not abandoned: once its configure lands it joins the fan-out.
        await WaitUntilAsync(() => slow.SubmitCount >= 1, TimeSpan.FromSeconds(10));
        Assert.True(slow.SubmitCount >= 1, "slow output never recovered after its configure completed");
    }

    [Fact]
    public async Task AConfigureFailure_SurfacesOnce_AndAFormatChangeRearmsTheOutput()
    {
        // Rejects the 64-wide canvas idle format, accepts anything else - a sink whose window/GL
        // creation fails for one mode but works for another.
        var flaky = new FormatRejectingOutput(rejectWidth: 64);
        using var runtime = Runtime(new ClipCompositionOutputLease("flaky", "Flaky", flaky));
        var failures = new List<ClipCompositionOutputConfigureFailure>();
        runtime.OutputConfigureFailure += (_, failure) => { lock (failures) failures.Add(failure); };
        int FailureCount() { lock (failures) return failures.Count; }

        runtime.SetIdleFrame(Frame(64, 48));
        runtime.EnsurePumpStarted();

        // The failure surfaces through the composition-level event (the same plumbing pattern as pump
        // pressure), naming the line.
        await WaitUntilAsync(() => FailureCount() >= 1, TimeSpan.FromSeconds(10));
        Assert.True(FailureCount() >= 1, "configure failure never surfaced");
        lock (failures)
        {
            Assert.Equal("flaky", failures[0].OutputId);
            Assert.Equal(64, failures[0].Format.Width);
        }
        Assert.Equal(0, flaky.SubmitCount);

        // No retry storm: ~15 further ticks arrive with the same format, and none of them re-runs the
        // failed configure (the old code would have re-thrown - and re-counted - once per tick).
        await Task.Delay(600);
        Assert.Equal(1, flaky.ConfigureAttempts);

        // A REAL format change re-arms the output: swap the idle image to a mode the sink accepts and
        // frames start flowing - the fault latch must not be a permanent wedge.
        runtime.SetIdleFrame(Frame(32, 24));
        await WaitUntilAsync(() => flaky.SubmitCount >= 1, TimeSpan.FromSeconds(10));
        Assert.True(flaky.SubmitCount >= 1, "output stayed wedged after its format changed");
        Assert.Equal(2, flaky.ConfigureAttempts);

        // The failure is also a counted per-output stat, so a diagnostics row shows the line as
        // failing rather than the number hiding in a log.
        var row = Assert.Single(runtime.GetStats().OutputStats);
        Assert.True(row.SubmitFailures >= 1, "configure failure not visible in the output's stats row");
    }

    // ---------------------------------------------------------------------------------------------
    // FIX 3: the freerun PTS fallback must survive a pump-clock driver restart.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FreerunOutputPts_StaysMonotonic_AcrossAPumpClockRestart()
    {
        var capture = new PtsCapturingOutput();
        using var runtime = Runtime(new ClipCompositionOutputLease("cap", "Capture", capture));
        var canvas = runtime.CanvasFormat;

        // One latest-wins layer with a single held frame: the mixer re-presents it every tick, and
        // each composite is stamped with the freerun outputPts - which is exactly the value under test.
        using var layer = runtime.AddLayer(canvas, new VideoPlacementSpec("screen", 0, Placement: "stretch"));
        layer.Output.Submit(Frame(canvas.Width, canvas.Height));

        await WaitUntilAsync(() => capture.MaxPts >= TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
        Assert.True(capture.MaxPts >= TimeSpan.FromMilliseconds(300), "freerun pump never advanced");

        // Restart the pump clock's driver. MediaClock's DriverLoopCore restarts its video tick index
        // from 1 on every Start, so a fallback anchored to LastVideoTickIndex snapped the composition
        // PTS back to ~0 here while the sinks persisted; the position-anchored fallback keeps counting.
        var clock = runtime.PumpClockForTests;
        Assert.NotNull(clock);
        var beforeRestart = capture.MaxPts;
        clock!.Pause();
        clock.Start();

        var countAtRestart = capture.Count;
        await WaitUntilAsync(() => capture.Count >= countAtRestart + 5, TimeSpan.FromSeconds(10));
        Assert.True(capture.Count >= countAtRestart + 5, "pump did not resume after the clock restart");

        // Continuity: nothing after the restart may fall behind what was already presented...
        Assert.True(capture.MinPtsAfter(countAtRestart) >= beforeRestart,
            $"PTS snapped back after restart: {capture.MinPtsAfter(countAtRestart)} < {beforeRestart}");
        // ...and the whole stream is non-decreasing (the position the fallback snaps is monotonic).
        Assert.True(capture.IsMonotonicNonDecreasing, "freerun PTS stream regressed");
    }

    // ---------------------------------------------------------------------------------------------
    // Test doubles. All INonBlockingVideoOutput so the runtime does not wrap them in a
    // VideoOutputPump - these tests are about the runtime's own gate/configure behaviour, and the
    // pump's queue would put a second, unrelated buffer between the tick and the observations.
    // ---------------------------------------------------------------------------------------------

    private sealed class CountingOutput : INonBlockingVideoOutput
    {
        private int _count;
        public int SubmitCount => Volatile.Read(ref _count);
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            Interlocked.Increment(ref _count);
            frame.Dispose();
        }
    }

    /// <summary>Submits park on a latch until released - lets a test hold the tick thread at a chosen
    /// point in its snapshot walk to force the interleaving under test, then let it go.</summary>
    private sealed class GateableCountingOutput : INonBlockingVideoOutput
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _count;
        public ManualResetEventSlim SubmitEntered { get; } = new(false);
        public int SubmitCount => Volatile.Read(ref _count);
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;
        public void ReleaseSubmits() => _release.Set();

        public void Submit(VideoFrame frame)
        {
            SubmitEntered.Set();
            // Bounded so a failing test run can't hang the pump forever.
            _release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref _count);
            frame.Dispose();
        }
    }

    /// <summary>Models an SDL-like sink whose Dispose joins a render thread: slow, and observable.</summary>
    private sealed class SlowDisposeOutput(TimeSpan disposeDuration) : INonBlockingVideoOutput, IDisposable
    {
        private int _disposes;
        public ManualResetEventSlim DisposeStarted { get; } = new(false);
        public int DisposeCount => Volatile.Read(ref _disposes);
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame) => frame.Dispose();

        public void Dispose()
        {
            DisposeStarted.Set();
            Thread.Sleep(disposeDuration);
            Interlocked.Increment(ref _disposes);
        }
    }

    /// <summary>Models an ownsThread sink whose FIRST configure blocks in window/GL creation.</summary>
    private sealed class SlowConfigureOutput(TimeSpan firstConfigureDelay) : INonBlockingVideoOutput
    {
        private int _configures;
        private int _count;
        public ManualResetEventSlim ConfigureStarted { get; } = new(false);
        public int SubmitCount => Volatile.Read(ref _count);
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];

        public void Configure(VideoFormat format)
        {
            ConfigureStarted.Set();
            if (Interlocked.Increment(ref _configures) == 1)
                Thread.Sleep(firstConfigureDelay);
            Format = format;
        }

        public void Submit(VideoFrame frame)
        {
            Interlocked.Increment(ref _count);
            frame.Dispose();
        }
    }

    /// <summary>Configure throws for one width and succeeds for any other - a sink that cannot enter
    /// one mode but is otherwise healthy.</summary>
    private sealed class FormatRejectingOutput(int rejectWidth) : INonBlockingVideoOutput
    {
        private int _attempts;
        private int _count;
        public int ConfigureAttempts => Volatile.Read(ref _attempts);
        public int SubmitCount => Volatile.Read(ref _count);
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];

        public void Configure(VideoFormat format)
        {
            Interlocked.Increment(ref _attempts);
            if (format.Width == rejectWidth)
                throw new InvalidOperationException($"synthetic configure failure for width {rejectWidth}");
            Format = format;
        }

        public void Submit(VideoFrame frame)
        {
            Interlocked.Increment(ref _count);
            frame.Dispose();
        }
    }

    /// <summary>Records every submitted frame's PresentationTime - for the freerun path that IS the
    /// composition's outputPts, so the recorded stream is the fallback's observable behaviour.</summary>
    private sealed class PtsCapturingOutput : INonBlockingVideoOutput
    {
        private readonly object _gate = new();
        private readonly List<TimeSpan> _pts = [];
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;

        public int Count
        {
            get { lock (_gate) return _pts.Count; }
        }

        public TimeSpan MaxPts
        {
            get { lock (_gate) return _pts.Count == 0 ? TimeSpan.MinValue : _pts.Max(); }
        }

        public TimeSpan MinPtsAfter(int index)
        {
            lock (_gate)
                return _pts.Count <= index ? TimeSpan.MaxValue : _pts.Skip(index).Min();
        }

        public bool IsMonotonicNonDecreasing
        {
            get
            {
                lock (_gate)
                {
                    for (var i = 1; i < _pts.Count; i++)
                    {
                        if (_pts[i] < _pts[i - 1])
                            return false;
                    }

                    return true;
                }
            }
        }

        public void Submit(VideoFrame frame)
        {
            lock (_gate)
                _pts.Add(frame.PresentationTime);
            frame.Dispose();
        }
    }
}
