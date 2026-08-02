using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// The persistent patch bay (HaCue plan, Phase 3): program voices through the V-wide bus to real
/// terminals via dense V×R patches, with live terminal add/patch-update/remove that never touches a
/// running voice, borrowed-terminal ownership, and the clock-policy validation (a foreign-rate
/// clock master is a NAMED failure; secondaries wrap through the injected resampler factory).
/// </summary>
public class AudioPatchBayTests
{
    private const int Rate = 48_000;
    private const int Frames = 480;

    private static float[] Chunk(int channels, float value)
    {
        var samples = new float[Frames * channels];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void LiveTopology_AddUpdateRemoveTerminals_WithoutInterruptingVoices()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        var house = new CapturingOutput(new AudioFormat(Rate, 2));
        bay.AddTerminal("house", house, new float[,] { { 1f, 0f }, { 0f, 1f } });

        using var voiceL = bay.AcquireProducer(1, [1f, 0f]);
        using var voiceR = bay.AcquireProducer(1, [0f, 1f]);

        bay.Play();
        var feeding = true;
        var feeder = new Thread(() =>
        {
            var chunk = Chunk(1, 1f);
            while (Volatile.Read(ref feeding))
            {
                voiceL.Submit(chunk);
                voiceR.Submit(chunk);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        feeder.Start();
        try
        {
            WaitFor(() => house.MaxSample >= 0.95f, "house never reached level");

            // Hot ADD while running: the summing stream terminal joins with a fade-in.
            var stream = new CapturingOutput(new AudioFormat(Rate, 2));
            bay.AddTerminal("stream", stream, new float[,] { { 0.5f, 0.5f }, { 0.5f, 0.5f } });
            WaitFor(() => stream.MaxSample >= 0.95f, "hot-added terminal never reached level");

            // Hot PATCH-UPDATE: zero the stream's cells; house and both voices keep running.
            bay.UpdatePatch("stream", new float[,] { { 0f, 0f }, { 0f, 0f } });
            Thread.Sleep(150); // let the one-chunk fade land + a few chunks drain
            stream.Reset();
            house.Reset();
            Thread.Sleep(150);
            Assert.True(stream.MaxSample <= 0.05f, $"zeroed patch still audible: {stream.MaxSample}");
            Assert.True(house.MaxSample >= 0.95f, "patch edit on one terminal disturbed another");

            // Hot REMOVE: the stream line detaches; the house line is unaffected.
            Assert.True(bay.RemoveTerminal("stream"));
            house.Reset();
            WaitFor(() => house.MaxSample >= 0.95f, "terminal removal disturbed the survivor");
            Assert.Equal(1, bay.TerminalCount);
        }
        finally
        {
            Volatile.Write(ref feeding, false);
            feeder.Join(1000);
            bay.Stop();
        }
    }

    [Fact]
    public void ClockMaster_AtForeignRate_IsANamedValidationFailure()
    {
        using var bay = new AudioPatchBay(2, Rate, resamplerFactory: static (inner, _) => inner);
        var foreign = new CapturingOutput(new AudioFormat(44_100, 2));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            bay.AddTerminal("master", foreign, new float[,] { { 1f, 0f }, { 0f, 1f } }, isClockMaster: true));
        Assert.Contains("natively", ex.Message);
        Assert.Equal(0, bay.TerminalCount); // nothing half-added
    }

    [Fact]
    public void ForeignRateSecondary_RequiresAndUsesTheResamplerFactory_AndOwnsOnlyTheWrapper()
    {
        var terminal = new DisposableOutput(new AudioFormat(44_100, 2));

        using (var noFactory = new AudioPatchBay(2, Rate))
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                noFactory.AddTerminal("t", terminal, new float[,] { { 1f, 0f }, { 0f, 1f } }));
            Assert.Contains("resampler", ex.Message);
        }

        DisposableOutput? wrapper = null;
        using (var bay = new AudioPatchBay(2, Rate, resamplerFactory: (inner, format) =>
               {
                   Assert.Same(terminal, inner);
                   Assert.Equal(Rate, format.SampleRate);
                   return wrapper = new DisposableOutput(format);
               }))
        {
            bay.AddTerminal("t", terminal, new float[,] { { 1f, 0f }, { 0f, 1f } });
            Assert.NotNull(wrapper);

            // Removing the terminal disposes the bay-created wrapper but NEVER the borrowed terminal.
            Assert.True(bay.RemoveTerminal("t"));
            Assert.True(wrapper!.Disposed);
            Assert.False(terminal.Disposed);
        }

        Assert.False(terminal.Disposed); // bay dispose still leaves the borrowed device alone
    }

    [Fact]
    public void Validation_RejectsBadPatchesDuplicatesAndSecondMasters()
    {
        using var bay = new AudioPatchBay(2, Rate);
        var a = new CapturingOutput(new AudioFormat(Rate, 2));
        var b = new CapturingOutput(new AudioFormat(Rate, 2));

        // Oversized and non-finite patches.
        Assert.Throws<ArgumentException>(() => bay.AddTerminal("a", a, new float[3, 2]));
        Assert.Throws<ArgumentException>(() => bay.AddTerminal("a", a, new float[2, 3]));
        Assert.Throws<ArgumentException>(() => bay.AddTerminal("a", a, new float[,] { { float.NaN, 0f }, { 0f, 1f } }));

        bay.AddTerminal("a", a, new float[,] { { 1f, 0f }, { 0f, 1f } }, isClockMaster: false);
        Assert.Throws<ArgumentException>(() => bay.AddTerminal("a", b, new float[,] { { 1f, 0f }, { 0f, 1f } }));

        // Unknown terminal on a live patch edit.
        Assert.Throws<ArgumentException>(() => bay.UpdatePatch("nope", new float[,] { { 1f, 0f }, { 0f, 1f } }));

        Assert.False(bay.RemoveTerminal("nope"));
    }

    [Fact]
    public void Dispose_InvalidatesOutstandingProducerEndpoints()
    {
        var bay = new AudioPatchBay(2, Rate);
        var producer = bay.AcquireProducer(1, [1f, 0f]);
        producer.Submit(Chunk(1, 1f));
        Assert.True(producer.BufferedFrames > 0);

        bay.Dispose();

        Assert.Equal(0, producer.BufferedFrames);
        producer.Submit(Chunk(1, 1f)); // disposed endpoints silently reject real-time submissions
        Assert.Equal(0, producer.BufferedFrames);
        Assert.Throws<ObjectDisposedException>(() => producer.UpdateSends([0f, 1f]));
        Assert.Throws<ObjectDisposedException>(() => bay.AcquireProducer(1, [1f, 0f]));
    }

    [Fact]
    public async Task AcquireProducer_RacingDispose_NeverReturnsALiveEndpointAfterDispose()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var bay = new AudioPatchBay(2, Rate);
            using var start = new ManualResetEventSlim();
            ProgramBusProducer? acquired = null;

            var acquire = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    acquired = bay.AcquireProducer(1, [1f, 0f]);
                }
                catch (ObjectDisposedException)
                {
                    // Disposal won the lifecycle gate.
                }
            });
            var dispose = Task.Run(() =>
            {
                start.Wait();
                bay.Dispose();
            });

            start.Set();
            await Task.WhenAll(acquire, dispose);

            Assert.Throws<ObjectDisposedException>(() => bay.AcquireProducer(1, [1f, 0f]));
            if (acquired is not null)
            {
                Assert.Throws<ObjectDisposedException>(() => acquired.UpdateSends([0f, 1f]));
                Assert.Equal(0, acquired.BufferedFrames);
            }
        }
    }

    /// <summary>The monitoring seam (HaCue plan: preview is monitoring): a monitor input reaches ONLY
    /// its selected terminal, bypasses the program patch entirely (audible even where the patch is
    /// all-zero), and detaches with its lease - while program voices keep flowing untouched.</summary>
    [Fact]
    public void MonitorInput_ReachesOnlyItsTerminal_AndBypassesTheProgramPatch()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        var house = new CapturingOutput(new AudioFormat(Rate, 2));
        var stream = new CapturingOutput(new AudioFormat(Rate, 2));
        bay.AddTerminal("house", house, new float[,] { { 1f, 0f }, { 0f, 1f } });
        // The stream terminal's PROGRAM patch is all-zero: program audio must not reach it, but the
        // monitor - which bypasses the patch - must.
        bay.AddTerminal("stream", stream, new float[,] { { 0f, 0f }, { 0f, 0f } });

        using var voice = bay.AcquireProducer(1, [1f, 0f]);
        var monitor = bay.AcquireMonitorInput("stream", 1, new float[,] { { 1f, 1f } });

        bay.Play();
        var feeding = true;
        var feeder = new Thread(() =>
        {
            var chunk = Chunk(1, 1f);
            var preview = Chunk(1, 0.5f);
            while (Volatile.Read(ref feeding))
            {
                voice.Submit(chunk);
                monitor.Input.Submit(preview);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        feeder.Start();
        try
        {
            WaitFor(() => house.MaxSample >= 0.95f, "program voice never reached the house");
            WaitFor(() => stream.MaxSample >= 0.45f, "monitor never reached its terminal");
            // The monitor peaked at its own 0.5 level: no program audio leaked through the zero patch.
            Assert.InRange(stream.MaxSample, 0.45f, 0.55f);

            // Lease dispose detaches the monitor; the program voice is untouched.
            monitor.Dispose();
            Thread.Sleep(100);
            stream.Reset();
            house.Reset();
            Thread.Sleep(150);
            Assert.True(stream.MaxSample <= 0.05f, $"disposed monitor still audible: {stream.MaxSample}");
            Assert.True(house.MaxSample >= 0.95f, "monitor teardown disturbed the program voice");
        }
        finally
        {
            Volatile.Write(ref feeding, false);
            feeder.Join(1000);
            bay.Stop();
        }
    }

    [Fact]
    public void MonitorInput_ValidatesTerminalAndMix_AndIsRevokedByBayDisposal()
    {
        var bay = new AudioPatchBay(2, Rate);
        var house = new CapturingOutput(new AudioFormat(Rate, 2));
        bay.AddTerminal("house", house, new float[,] { { 1f, 0f }, { 0f, 1f } });

        Assert.Throws<ArgumentException>(() => bay.AcquireMonitorInput("nope", 1, new float[,] { { 1f, 1f } }));
        Assert.Throws<ArgumentException>(() => bay.AcquireMonitorInput("house", 1, new float[,] { { 1f, 1f, 1f } }));
        Assert.Throws<ArgumentException>(() => bay.AcquireMonitorInput("house", 1, new float[,] { { float.NaN, 0f } }));

        var monitor = bay.AcquireMonitorInput("house", 1, new float[,] { { 1f, 1f } });
        bay.Dispose();
        // Same revocation contract as program leases: a bay teardown invalidates the endpoint.
        Assert.Throws<ObjectDisposedException>(() => monitor.Input.UpdateSends(new float[] { 1f, 1f }));
        monitor.Dispose(); // and a late lease dispose stays safe
    }

    /// <summary>The quarantine/hot-swap slice (HaCue plan, Phase 3): a line whose native Submit
    /// wedges is replaced under its id without rebuilding the bay, stalling the mix, or touching
    /// the other lines - and the stored patch rides over to the replacement.</summary>
    [Fact]
    public void ReplaceTerminal_HotSwapsAWedgedLine_WithoutInterruptingTheOtherLine()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        var house = new CapturingOutput(new AudioFormat(Rate, 2));
        var wedged = new WedgingOutput(new AudioFormat(Rate, 2));
        bay.AddTerminal("house", house, new float[,] { { 1f, 0f }, { 0f, 1f } });
        bay.AddTerminal("stream", wedged, new float[,] { { 1f, 0f }, { 0f, 1f } });

        using var voice = bay.AcquireProducer(1, [1f, 1f]);
        bay.Play();
        var feeding = true;
        var feeder = new Thread(() =>
        {
            var chunk = Chunk(1, 1f);
            while (Volatile.Read(ref feeding))
            {
                voice.Submit(chunk);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        feeder.Start();
        try
        {
            WaitFor(() => house.MaxSample >= 0.95f, "house never reached level");
            Assert.True(wedged.Entered.Wait(TimeSpan.FromSeconds(2)), "stream line never wedged");

            // Hot-swap the wedged line. Returns promptly - the wedged pump is quarantined in the
            // background - and re-applies the STORED patch to the replacement (none passed here).
            var fresh = new CapturingOutput(new AudioFormat(Rate, 2));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bay.ReplaceTerminal("stream", fresh);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"hot-swap must not join the wedged drainer inline (took {sw.ElapsedMilliseconds}ms)");

            // The survivor line keeps flowing through the swap (the old inline join stalled the mix
            // for the full ~3s cap - a 1s re-peak window separates the two decisively) ...
            house.Reset();
            WaitFor(() => house.MaxSample >= 0.95f, "hot-swap interrupted the other line", timeoutMs: 1000);
            // ... and the replacement is audible through the stored patch, no bay rebuild involved.
            WaitFor(() => fresh.MaxSample >= 0.95f, "replacement terminal never received program audio");
            Assert.Equal(2, bay.TerminalCount);
        }
        finally
        {
            Volatile.Write(ref feeding, false);
            feeder.Join(1000);
            bay.Stop();
            wedged.Release(); // let the leaked (quarantined) drainer thread exit
        }
    }

    [Fact]
    public void ReplaceTerminal_ValidationFailure_LeavesTheOldLineAttached()
    {
        using var bay = new AudioPatchBay(2, Rate);
        var house = new CapturingOutput(new AudioFormat(Rate, 2));
        var master = new NullClockedAudioOutput(new AudioFormat(Rate, 2));
        master.Start();
        bay.AddTerminal("house", house, new float[,] { { 1f, 0f }, { 0f, 1f } });
        bay.AddTerminal("master", master, new float[,] { { 1f, 0f }, { 0f, 1f } }, isClockMaster: true);

        Assert.Throws<ArgumentException>(() =>
            bay.ReplaceTerminal("nope", new CapturingOutput(new AudioFormat(Rate, 2))));
        // Foreign-rate replacement without a resampler factory: named rejection, old line untouched.
        var rateEx = Assert.Throws<InvalidOperationException>(() =>
            bay.ReplaceTerminal("house", new CapturingOutput(new AudioFormat(44_100, 2))));
        Assert.Contains("resampler", rateEx.Message);
        // The master-natively-at-mix-rate rule applies on the swap path exactly like on add.
        var masterEx = Assert.Throws<InvalidOperationException>(() =>
            bay.ReplaceTerminal("master", new CapturingOutput(new AudioFormat(44_100, 2))));
        Assert.Contains("natively", masterEx.Message);
        // A bad patch override is rejected BEFORE the old line is detached.
        Assert.Throws<ArgumentException>(() =>
            bay.ReplaceTerminal("house", new CapturingOutput(new AudioFormat(Rate, 2)), new float[3, 2]));

        Assert.Equal(2, bay.TerminalCount);
        bay.UpdatePatch("house", new float[,] { { 1f, 0f }, { 0f, 1f } }); // the old line is still live
    }

    [Fact]
    public void ReplaceTerminal_ClockMaster_HandsProducerClocksToTheNewMaster()
    {
        using var bay = new AudioPatchBay(2, Rate);
        var oldMaster = new NullClockedAudioOutput(new AudioFormat(Rate, 2));
        oldMaster.Start(); // borrowed terminals arrive already running
        bay.AddTerminal("master", oldMaster, new float[,] { { 1f, 0f }, { 0f, 1f } }, isClockMaster: true);

        using var producer = bay.AcquireProducer(1, [1f, 0f]);
        bay.Play();
        var feeding = true;
        var feeder = new Thread(() =>
        {
            var chunk = Chunk(1, 0.5f);
            while (Volatile.Read(ref feeding))
            {
                producer.Submit(chunk);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        feeder.Start();
        try
        {
            WaitFor(() => producer.ElapsedSinceStart >= TimeSpan.FromMilliseconds(200),
                "producer clock never advanced against the old master");

            var newMaster = new NullClockedAudioOutput(new AudioFormat(Rate, 2));
            newMaster.Start();
            var before = producer.ElapsedSinceStart;
            bay.ReplaceTerminal("master", newMaster);

            // Pacing and producer clocks follow the NEW master (its first read carries an unseen
            // epoch id - the extracted clock's announced-re-anchor path): the clock keeps advancing
            // with no fault and never steps backwards across the swap gap.
            var settle = producer.ElapsedSinceStart;
            Assert.True(settle >= before, "producer clock stepped backwards across a master swap");
            WaitFor(() => producer.ElapsedSinceStart >= settle + TimeSpan.FromMilliseconds(200),
                "producer clock stalled after the master swap");
            Assert.True(producer.IsAdvancing);
            Assert.Empty(bay.QuarantinedTerminalIds); // a healthy swap quarantines nothing
        }
        finally
        {
            Volatile.Write(ref feeding, false);
            feeder.Join(1000);
            bay.Stop();
        }
    }

    [Fact]
    public void AdaptiveRate_WrapsSecondariesButNeverTheMaster_AndRebuildsOnPromotion()
    {
        var wrappedIds = new List<string>();
        var wrappers = new List<TrackingAdaptiveOutput>();
        using var bay = new AudioPatchBay(
            2,
            Rate,
            adaptiveRateWrapper: (_, inner, id, maxDelta) =>
            {
                Assert.Equal(3, maxDelta);
                wrappedIds.Add(id);
                var wrapper = new TrackingAdaptiveOutput(inner);
                wrappers.Add(wrapper);
                return wrapper;
            });
        var master = new NullClockedAudioOutput(new AudioFormat(Rate, 2));
        var backup = new NullClockedAudioOutput(new AudioFormat(Rate, 2));
        master.Start();
        backup.Start();

        bay.AddTerminal("master", master, new float[,] { { 1, 0 }, { 0, 1 } }, isClockMaster: true);
        bay.AddTerminal("backup", backup, new float[,] { { 1, 0 }, { 0, 1 } });

        Assert.Equal(["backup"], wrappedIds);
        Assert.Equal("master", bay.ClockMasterTerminalId);

        bay.PromoteClockMaster("backup");

        Assert.Equal("backup", bay.ClockMasterTerminalId);
        Assert.Equal(["backup", "master"], wrappedIds);
        Assert.True(wrappers[0].Disposed); // backup's old adaptive chain retired before it became master
        Assert.False(wrappers[1].Disposed); // the demoted master now owns the live correction chain
    }

    [Fact]
    public void ReplaceTerminal_WrapperFactoryFailure_LeavesOldTerminalAttached()
    {
        using var bay = new AudioPatchBay(
            2,
            Rate,
            resamplerFactory: (_, _) => throw new InvalidOperationException("converter unavailable"));
        bay.AddTerminal("house", new CapturingOutput(new AudioFormat(Rate, 2)),
            new float[,] { { 1, 0 }, { 0, 1 } });

        var error = Assert.Throws<InvalidOperationException>(() =>
            bay.ReplaceTerminal("house", new CapturingOutput(new AudioFormat(44_100, 2))));

        Assert.Contains("converter unavailable", error.Message);
        Assert.Equal(1, bay.TerminalCount);
        Assert.True(bay.TryGetTerminalFormat("house", out var format));
        Assert.Equal(Rate, format.SampleRate);
        bay.UpdatePatch("house", new float[,] { { 0.5f, 0 }, { 0, 0.5f } });
    }

    private static void WaitFor(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
            Thread.Sleep(10);
        Assert.True(condition(), message);
    }

    private sealed class CapturingOutput(AudioFormat fmt) : IAudioOutput
    {
        private float _maxSample;
        public AudioFormat Format => fmt;
        public float MaxSample => Volatile.Read(ref _maxSample);
        public void Reset() => Volatile.Write(ref _maxSample, 0f);
        public void Submit(ReadOnlySpan<float> samples)
        {
            var max = Volatile.Read(ref _maxSample);
            foreach (var sample in samples)
            {
                if (sample > max)
                    max = sample;
            }
            Volatile.Write(ref _maxSample, max);
        }
    }

    private sealed class DisposableOutput(AudioFormat fmt) : IAudioOutput, IDisposable
    {
        public AudioFormat Format => fmt;
        public bool Disposed { get; private set; }
        public void Submit(ReadOnlySpan<float> samples) { }
        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingAdaptiveOutput(IAudioOutput inner)
        : IAudioOutput, IClockedOutput, IPlaybackClock, IAdaptiveRateWrappedOutput, IDisposable
    {
        public AudioFormat Format => inner.Format;
        public bool Disposed { get; private set; }
        public void Submit(ReadOnlySpan<float> samples) => inner.Submit(samples);
        public bool WaitForCapacity(int samplesPerChannel, CancellationToken cancellationToken) =>
            inner is not IClockedOutput clocked
            || clocked.WaitForCapacity(samplesPerChannel, cancellationToken);
        public TimeSpan ElapsedSinceStart => ((IPlaybackClock)inner).ElapsedSinceStart;
        public long EpochId => ((IPlaybackClock)inner).EpochId;
        public bool IsAdvancing => ((IPlaybackClock)inner).IsAdvancing;
        public ClockReading Read() => ((IPlaybackClock)inner).Read();
        public void Dispose() => Disposed = true;
    }

    /// <summary>Models a device whose native Submit wedges: the first Submit blocks until
    /// <see cref="Release"/> - the failure mode terminal quarantine exists for.</summary>
    private sealed class WedgingOutput(AudioFormat fmt) : IAudioOutput
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);
        public AudioFormat Format => fmt;

        public void Submit(ReadOnlySpan<float> samples)
        {
            Entered.Set();
            _release.Wait();
        }

        public void Release() => _release.Set();
    }
}
