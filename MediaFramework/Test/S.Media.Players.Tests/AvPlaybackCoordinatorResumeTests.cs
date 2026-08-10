using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.Routing;
using S.Media.Time;
using Xunit;

namespace S.Media.Players.Tests;

/// <summary>
/// Pause/resume and repeat-Play behavior of <see cref="AvPlaybackCoordinator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The genlock tests pin the silent-voice pause bug: a voice whose audio reaches no clocked output is
/// mastered to the SHARED show clock by the coordinator's genlock branch. That clock keeps advancing
/// (same epoch) while this one voice pauses, and <see cref="Time.MediaClock"/>'s start-time fold of
/// same-epoch master drift - correct for a producer clock that froze with the voice - counted the
/// whole pause as forward progress: a 30 s pause resumed 30 s ahead. The fix re-anchors on resume
/// (mirroring the video-only branch's SetMaster-on-every-Play) exactly and only for clocks the
/// coordinator itself genlocked; the producer-mastered fold is pinned untouched here too.
/// </para>
/// <para>
/// The no-op tests pin Play idempotence: session code calls <c>Play()</c> unconditionally on players
/// that may already be running, and the slow half used to re-run mid-stream (stealing a queued frame
/// for the sync present; BeginPreRoll/EndPreRoll on a LIVE producer = a mute plus a ~200 ms baseline
/// step). The natural-EOF test pins the deliberate asymmetry in the predicate: clock AND router must
/// be running, because at natural EOF the router run loop has exited while the clock object still
/// reads running, and a seek+Play restart from there still needs the full path.
/// </para>
/// </remarks>
public sealed class AvPlaybackCoordinatorResumeTests
{
    private const int Rate = 48_000;
    private const int Chunk = 480;

    // --- fix 1: genlocked silent voice ------------------------------------

    [Fact]
    public void GenlockedSilentVoice_ResumeAfterPause_DoesNotJumpForwardByThePauseDuration()
    {
        using var graph = SilentVoiceGraph.Create();
        var show = new ManualShowClock();

        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            videoOnlyMaster: show, audioSourceId: graph.SourceId)();
        Assert.Same(show, graph.Clock.Master); // the genlock branch took the show clock

        show.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1.0, graph.Clock.CurrentPosition.TotalSeconds, 2);

        AvPlaybackCoordinator.Pause(graph.Video, graph.Router, graph.Clock);
        Assert.Equal(1.0, graph.Clock.CurrentPosition.TotalSeconds, 2);

        // The SHOW keeps running through this one voice's pause - same epoch, no flush. This is the
        // drift the old resume folded into the position wholesale.
        show.Advance(TimeSpan.FromSeconds(30));

        // Session-shaped resume: plain Play() passes NO videoOnlyMaster.
        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            videoOnlyMaster: null, audioSourceId: graph.SourceId)();

        // Bug shape: 31.0 s. Fixed: resumes where it paused...
        Assert.Equal(1.0, graph.Clock.CurrentPosition.TotalSeconds, 2);
        Assert.Same(show, graph.Clock.Master); // ...and is still genlocked, not silently free-running,

        show.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(1.5, graph.Clock.CurrentPosition.TotalSeconds, 2); // ...still tracking the show.
    }

    [Fact]
    public void GenlockedSilentVoice_ResumeWithTheShowClockPassedAgain_AlsoResumesInPlace()
    {
        // The group-fire path DOES pass the show clock on every Play - resume must behave the same
        // whether the host repeats the parameter or not.
        using var graph = SilentVoiceGraph.Create();
        var show = new ManualShowClock();

        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            videoOnlyMaster: show, audioSourceId: graph.SourceId)();
        show.Advance(TimeSpan.FromSeconds(2));
        AvPlaybackCoordinator.Pause(graph.Video, graph.Router, graph.Clock);
        show.Advance(TimeSpan.FromSeconds(10));

        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            videoOnlyMaster: show, audioSourceId: graph.SourceId)();

        Assert.Equal(2.0, graph.Clock.CurrentPosition.TotalSeconds, 2);
        Assert.Same(show, graph.Clock.Master);
    }

    [Fact]
    public void ProducerMasteredVoice_ResumeStillFoldsAudioDrainedDuringPause()
    {
        // The other side of the fix-1 coin: a SOUNDING voice's master is its own producer clock,
        // which freezes/flushes with the voice - audio that drained between Pause and the device
        // going quiet is real heard progress and MUST still be folded in at resume. The genlock
        // re-anchor must not fire here (the coordinator never genlocked this clock).
        using var graph = SilentVoiceGraph.Create(addDiscardOutput: false);
        var producer = new ManualClockedOutput(new AudioFormat(Rate, 2));
        var producerId = graph.Router.AddOutput(producer, "_producer");
        Assert.Equal(producerId, graph.Router.PrimaryOutputId); // promoted = clock mastered to it
        Assert.Same(producer, graph.Clock.Master);
        graph.Router.Connect(graph.SourceId, producerId);

        var show = new ManualShowClock();
        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            videoOnlyMaster: show, audioSourceId: graph.SourceId)();
        Assert.Same(producer, graph.Clock.Master); // producer-mastered: the genlock never applied

        producer.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1.0, graph.Clock.CurrentPosition.TotalSeconds, 2);

        AvPlaybackCoordinator.Pause(graph.Video, graph.Router, graph.Clock);
        // Audio still draining out of the device after the clock froze - same epoch, real progress.
        producer.Advance(TimeSpan.FromSeconds(0.2));

        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            videoOnlyMaster: show, audioSourceId: graph.SourceId)();

        Assert.Equal(1.2, graph.Clock.CurrentPosition.TotalSeconds, 2);
    }

    // --- fix 2: Play on a running transport is a cheap no-op ----------------

    [Fact]
    public void PreparePlay_OnARunningTransport_IsACheapNoOp()
    {
        using var graph = SilentVoiceGraph.Create();
        var show = new ManualShowClock();
        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            videoOnlyMaster: show, audioSourceId: graph.SourceId)();
        Assert.True(graph.Clock.IsRunning);
        Assert.True(graph.Router.IsRunning);

        var prefills = 0;
        var hardwareStarts = 0;
        var starter = AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock,
            prefillBeforeHardware: () => prefills++,
            startHardware: () => hardwareStarts++,
            videoOnlyMaster: show,
            audioSourceId: graph.SourceId);
        starter();

        // The slow half never ran: no prefill, no hardware start, and the transport kept its state.
        Assert.Equal(0, prefills);
        Assert.Equal(0, hardwareStarts);
        Assert.True(graph.Clock.IsRunning);
        Assert.True(graph.Router.IsRunning);
    }

    [Fact]
    public void PreparePlay_OnARunningTransport_DoesNotPreRollTheLiveProducer()
    {
        // The most destructive part of the old re-run: BeginPreRoll on a producer that is LIVE in the
        // mix mutes it, and the later EndPreRoll resets the pre-roll baseline to the pacing target
        // mid-stream (~200 ms position step + a mid-run Reanchor).
        using var graph = SilentVoiceGraph.Create(addDiscardOutput: false);
        var producer = new PreRollableClockedOutput(new AudioFormat(Rate, 2));
        var producerId = graph.Router.AddOutput(producer, "_producer");
        graph.Router.Connect(graph.SourceId, producerId);

        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock, audioSourceId: graph.SourceId)();
        Assert.Equal(1, producer.BeginPreRollCount); // the legitimate group-fire pre-roll
        Assert.Equal(1, producer.EndPreRollCount);

        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock, audioSourceId: graph.SourceId)();

        Assert.Equal(1, producer.BeginPreRollCount); // never re-held mid-stream
        Assert.Equal(1, producer.EndPreRollCount);
    }

    [Fact]
    public void PreparePlay_AfterNaturalEofAndSeek_StillRestartsTheRouter()
    {
        // Pins the no-op predicate's router half: at natural EOF the router run loop has exited while
        // the CLOCK object still reads running (nothing paused it). A clock-only "already running"
        // check would turn the seek+Play restart into a no-op and the clip would never sound again.
        using var graph = SilentVoiceGraph.Create(finiteAudioChunks: 4);

        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock, audioSourceId: graph.SourceId)();
        Assert.True(
            SpinWait.SpinUntil(() => graph.Router.CompletedNaturally, TimeSpan.FromSeconds(5)),
            "router did not reach natural EOF within the window");
        Assert.True(graph.Clock.IsRunning, "precondition: the clock object keeps running at natural EOF");
        Assert.False(graph.Router.IsRunning);

        AvPlaybackCoordinator.Seek(graph.Video, graph.Router, graph.Clock, graph.SourceId, TimeSpan.Zero);
        AvPlaybackCoordinator.PreparePlay(
            graph.Video, graph.Router, graph.Clock, audioSourceId: graph.SourceId)();

        Assert.True(graph.Router.IsRunning, "restart after natural EOF must run the full path");
        Assert.False(graph.Router.CompletedNaturally);
    }

    [Fact]
    public void VideoOnlyPreparePlay_OnARunningClock_IsACheapNoOp()
    {
        var source = new SteppedVideoSource();
        var sink = new NullVideoSink();
        using var clock = new Time.MediaClock();
        using var video = new VideoPlayer(source, sink, clock, queueCapacity: 4);

        AvPlaybackCoordinator.PreparePlay(video)();
        Assert.True(clock.IsRunning);

        var prefills = 0;
        AvPlaybackCoordinator.PreparePlay(video, prefillBeforeHardware: () => prefills++)();

        Assert.Equal(0, prefills);
        Assert.True(clock.IsRunning);

        AvPlaybackCoordinator.Pause(video);
        Assert.False(clock.IsRunning);
    }

    // --- shared graph -------------------------------------------------------

    /// <summary>
    /// The minimal silent-voice shape: an audio router whose only output is an UNCLOCKED discard sink,
    /// so no pacing primary is ever promoted and the voice's <see cref="Time.MediaClock"/> has no master
    /// until the coordinator genlocks it - exactly what <c>MediaPlayer.TryOpenLive</c> wires for a
    /// video clip whose audio is routed nowhere.
    /// </summary>
    private sealed class SilentVoiceGraph : IDisposable
    {
        public required Time.MediaClock Clock { get; init; }
        public required AudioRouter Router { get; init; }
        public required VideoPlayer Video { get; init; }
        public required string SourceId { get; init; }

        public static SilentVoiceGraph Create(bool addDiscardOutput = true, int? finiteAudioChunks = null)
        {
            var clock = new Time.MediaClock();
            var router = new AudioRouter(Rate, Chunk);
            router.AttachMasterClock(clock);
            IAudioSource tone = finiteAudioChunks is { } chunks
                ? new FiniteSeekableToneSource(chunks)
                : new InfiniteToneSource();
            var sourceId = router.AddSource(tone, autoResample: true);
            if (addDiscardOutput)
            {
                var discardId = router.AddOutput(new DiscardingAudioOutput(new AudioFormat(Rate, 2)), "_discard");
                router.Connect(sourceId, discardId);
            }

            var video = new VideoPlayer(new SteppedVideoSource(), new NullVideoSink(), clock, queueCapacity: 4);
            return new SilentVoiceGraph { Clock = clock, Router = router, Video = video, SourceId = sourceId };
        }

        public void Dispose()
        {
            Video.Dispose();
            Router.Dispose();
            Clock.Dispose();
        }
    }

    // --- fakes --------------------------------------------------------------

    /// <summary>The shared show clock: advances only when the test says so, never changes epoch -
    /// the same shape as the real <c>AudibleClientClock</c> across one voice's pause (the show's
    /// device keeps consuming; nothing flushes for a voice that merely paused).</summary>
    private sealed class ManualShowClock : IPlaybackClock
    {
        private long _elapsedTicks;

        public TimeSpan ElapsedSinceStart => new(Volatile.Read(ref _elapsedTicks));
        public long EpochId => 1;
        public bool IsAdvancing => true;
        public ClockReading Read() => new(EpochId, ElapsedSinceStart, IsAdvancing);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _elapsedTicks, delta.Ticks);
    }

    /// <summary>A clocked output: being <see cref="IClockedOutput"/> gets it auto-promoted to pacing
    /// primary at AddOutput, which masters the attached clock to it - the producer-mastered shape.</summary>
    private class ManualClockedOutput(AudioFormat format) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        private long _elapsedTicks;

        public AudioFormat Format { get; } = format;
        public TimeSpan ElapsedSinceStart => new(Volatile.Read(ref _elapsedTicks));
        public long EpochId => 1;
        public bool IsAdvancing => true;
        public ClockReading Read() => new(EpochId, ElapsedSinceStart, IsAdvancing);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _elapsedTicks, delta.Ticks);
        public void Submit(ReadOnlySpan<float> samples) { }
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
    }

    private sealed class PreRollableClockedOutput(AudioFormat format)
        : ManualClockedOutput(format), IPreRollableOutput
    {
        private int _begin;
        private int _end;

        public int BeginPreRollCount => Volatile.Read(ref _begin);
        public int EndPreRollCount => Volatile.Read(ref _end);
        public void BeginPreRoll() => Interlocked.Increment(ref _begin);
        public void EndPreRoll() => Interlocked.Increment(ref _end);
    }

    private sealed class InfiniteToneSource : IAudioSource
    {
        public AudioFormat Format { get; } = new(Rate, 2);
        public bool IsExhausted => false;

        public int ReadInto(Span<float> destination)
        {
            for (var i = 0; i < destination.Length; i++)
                destination[i] = 0.25f;
            return destination.Length;
        }
    }

    /// <summary>Finite and refillable-on-seek, so a natural-EOF restart has audio to produce again.</summary>
    private sealed class FiniteSeekableToneSource(int chunks) : IAudioSource, ISeekableSource
    {
        private readonly int _chunks = chunks;
        private int _remaining = chunks;

        public AudioFormat Format { get; } = new(Rate, 2);
        public bool IsExhausted => Volatile.Read(ref _remaining) <= 0;
        public TimeSpan Duration { get; } = TimeSpan.FromSeconds(2);
        public TimeSpan Position { get; private set; }

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remaining) < 0)
                return 0;
            destination.Fill(0.25f);
            return destination.Length;
        }

        public void Seek(TimeSpan position)
        {
            Position = position;
            Volatile.Write(ref _remaining, _chunks);
        }
    }

    /// <summary>24 fps synthetic frames from wherever the last seek left it - never exhausts, so the
    /// coordinator's video-buffer wait always finds frames near the sync target.</summary>
    private sealed class SteppedVideoSource : IVideoSource, ISeekableSource
    {
        private int _frameIndex;
        private VideoFormat _format = new(4, 4, PixelFormat.Bgra32, new Rational(24, 1));

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => false;
        public TimeSpan Duration => TimeSpan.FromMinutes(10);
        public TimeSpan Position { get; private set; }

        public void SelectOutputFormat(PixelFormat format) => _format = _format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var index = Interlocked.Increment(ref _frameIndex);
            Position = TimeSpan.FromSeconds(index / 24.0);
            var stride = _format.Width * 4;
            frame = new VideoFrame(Position, _format, [new byte[stride * _format.Height]], [stride]);
            return true;
        }

        public void Seek(TimeSpan position)
        {
            Position = position;
            Volatile.Write(ref _frameIndex, Math.Max(0, (int)Math.Round(position.TotalSeconds * 24)));
        }
    }

    private sealed class NullVideoSink : IVideoOutput
    {
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame) => frame.Dispose();
    }
}
