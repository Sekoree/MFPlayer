using System.Diagnostics;
using S.Media.Core;
using S.Media.Routing;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.Players.Tests;

public sealed class MediaPlayerTests(ITestOutputHelper output)
{
    [Fact]
    public void OpenAudio_WiresDecodedSourceToOutput()
    {
        var source = new ToneSource(sampleRate: 48_000, channels: 6, chunks: 8);
        var backend = new CollectingBackend();
        var registry = MediaRegistry.Build(b => b.AddDecoder(new FixedDecoderProvider(source)));

        using var player = MediaPlayer.OpenAudio(registry, backend, "file:///tone.wav");
        player.Play();

        Assert.True(SpinWait.SpinUntil(() => backend.Output.NonZeroSamples > 0, TimeSpan.FromSeconds(2)),
            "MediaPlayer opened a source and output but did not route non-zero audio between them.");
    }

    [Fact]
    public void TryOpen_ForwardsOpenOptionsToRegistry()
    {
        var provider = new CapturingDecoderProvider();
        var registry = MediaRegistry.Build(b => b.AddDecoder(provider));
        var options = MediaPlayerOpenOptions.Default with
        {
            TryHardwareAcceleration = false,
            RetainDmabufForGl = true,
            RetainD3D11SharedHandleForGl = true,
            Win32Nv12SharedHandleOnlyExport = true,
            AudioPacketQueueDepth = 12,
            VideoPacketQueueDepth = 34,
            FileReadBufferBytes = 1024 * 1024,
            StreamIsSeekable = true,
            SpoolStreamToDisk = true,
            AudioStreamIndex = 2,
            VideoStreamIndex = 3,
        };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            Assert.NotNull(provider.VideoOptions);
            Assert.False(provider.VideoOptions.TryHardwareAcceleration);
            Assert.True(provider.VideoOptions.RetainDmabufForGl);
            Assert.True(provider.VideoOptions.RetainD3D11SharedHandleForGl);
            Assert.True(provider.VideoOptions.Win32Nv12SharedHandleOnlyExport);
            Assert.Equal(12, provider.VideoOptions.AudioPacketQueueDepth);
            Assert.Equal(34, provider.VideoOptions.VideoPacketQueueDepth);
            Assert.Equal(1024 * 1024, provider.VideoOptions.FileReadBufferBytes);
            Assert.True(provider.VideoOptions.StreamIsSeekable);
            Assert.True(provider.VideoOptions.SpoolToDisk);
            Assert.Equal(2, provider.VideoOptions.AudioStreamIndex);
            Assert.Equal(3, provider.VideoOptions.VideoStreamIndex);

            Assert.NotNull(provider.AudioOptions);
            Assert.True(provider.AudioOptions.StreamIsSeekable);
            Assert.True(provider.AudioOptions.SpoolToDisk);
            Assert.Equal(2, provider.AudioOptions.AudioStreamIndex);
        }
    }

    [Fact]
    public void TryOpen_DisabledAudio_DoesNotOpenAudioRouter()
    {
        var provider = new CapturingDecoderProvider();
        var registry = MediaRegistry.Build(b => b.AddDecoder(provider));
        var options = MediaPlayerOpenOptions.Default with { AudioStreamIndex = MediaPlayerOpenOptions.DisabledStreamIndex };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            Assert.Equal(1, provider.VideoOpenCount);
            Assert.Equal(0, provider.AudioOpenCount);
            Assert.Null(player.AudioRouter);
        }
    }

    [Fact]
    public void TryOpen_TargetAudioSampleRate_ResamplesTheDecodedSource()
    {
        var source = new ToneSource(sampleRate: 44_100, channels: 2, chunks: 8);
        var requestedRate = 0;
        var registry = MediaRegistry.Build(b => b
            .AddDecoder(new FixedDecoderProvider(source))
            .SetResamplerFactory((inner, rate) =>
            {
                requestedRate = rate;
                return new TargetRateAudioSource(inner, rate);
            }));
        var options = MediaPlayerOpenOptions.Default with { TargetAudioSampleRate = 48_000 };

        Assert.True(MediaPlayer.TryOpen(
            registry, "file:///tone.wav", options, null, out var player, out var error), error);
        using (player)
        {
            Assert.Equal(48_000, requestedRate);
            Assert.Equal(48_000, player.SampleRate);
        }
    }

    [Fact]
    public void IsRunning_TracksFreerunClockForVideoOnlyPlayback()
    {
        var provider = new CapturingDecoderProvider();
        var registry = MediaRegistry.Build(b => b.AddDecoder(provider));
        var options = MediaPlayerOpenOptions.Default with { IncludeAudioRouter = false };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            player.Play();

            Assert.True(player.IsRunning);
        }
    }

    [Fact]
    public void VideoOnlyPlayback_WithoutOptIn_KeepsTheFreerunClockUnmastered()
    {
        // D3 default: no VideoPtsClock is created or attached, so video-only playback paces exactly as before.
        var registry = MediaRegistry.Build(b => b.AddDecoder(new CapturingDecoderProvider()));
        var options = MediaPlayerOpenOptions.Default with { IncludeAudioRouter = false };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            player.Play();
            var clock = Assert.IsType<MediaClock>(player.PlayClock);
            Assert.Null(clock.Master);
            player.Pause();
        }
    }

    [Fact]
    public void VideoOnlyPlayback_WithOptIn_MastersTheClockOnPresentedVideoPts()
    {
        var registry = MediaRegistry.Build(b => b.AddDecoder(new CapturingDecoderProvider()));
        var options = MediaPlayerOpenOptions.Default with
        {
            IncludeAudioRouter = false,
            MasterVideoOnlyClockFromPts = true,
        };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            player.Play();
            var clock = Assert.IsType<MediaClock>(player.PlayClock);
            var master = Assert.IsType<VideoPtsClock>(clock.Master);
            Assert.True(master.IsAdvancing);

            // Frozen with the transport: nothing may accrue while paused (otherwise MediaClock would fold
            // the pause duration into the position on the next Start).
            player.Pause();
            Assert.False(master.IsAdvancing);
            var frozen = master.ElapsedSinceStart;
            Thread.Sleep(30);
            Assert.Equal(frozen, master.ElapsedSinceStart);

            // A seek re-anchors the PTS origin on the target, so the next presented frame maps onto the
            // seeked media timeline instead of the pre-seek origin.
            player.Seek(TimeSpan.FromSeconds(4));
            Assert.Equal(TimeSpan.FromSeconds(4), player.Position);
        }
    }

    [Fact]
    public void AudioMasteredPlayback_IgnoresTheVideoOnlyPtsOptIn()
    {
        // The opt-in is video-only by construction: with an audio router wired the clock keeps its audio
        // master (none here - no clocked output), and no VideoPtsClock is ever attached.
        var registry = MediaRegistry.Build(b => b.AddDecoder(new CapturingDecoderProvider()));
        var options = MediaPlayerOpenOptions.Default with { MasterVideoOnlyClockFromPts = true };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            Assert.NotNull(player.AudioRouter);
            player.Play();
            var clock = Assert.IsType<MediaClock>(player.PlayClock);
            Assert.False(clock.Master is VideoPtsClock);
            player.Pause();
        }
    }

    [Fact]
    public void Open_SourceWithoutIngestPacingOptIn_KeepsTheRouterOnItsWallClock()
    {
        // D2 default: a live source that HAS an ingest clock (every NDI receiver does) but was not configured
        // for ingest pacing must not promote it - the router keeps producing on wall time as before.
        var source = new IngestPacedToneSource(sampleRate: 48_000, channels: 2, chunks: 8, ingestClock: null);
        var registry = MediaRegistry.Build(b => b.AddDecoder(new FixedDecoderProvider(source)));

        Assert.True(MediaPlayer.TryOpen(
            registry, "file:///live.tone", MediaPlayerOpenOptions.Default, null, out var player, out var error), error);
        using (player)
        {
            Assert.Null(player.AudioRouter!.IngestPaceMaster);
        }
    }

    [Fact]
    public void Open_SourceOptedIntoIngestPacing_SlavesTheRouterToThatClock()
    {
        var ingest = new FakeIngestClock();
        var source = new IngestPacedToneSource(sampleRate: 48_000, channels: 2, chunks: 8, ingestClock: ingest);
        var registry = MediaRegistry.Build(b => b.AddDecoder(new FixedDecoderProvider(source)));

        Assert.True(MediaPlayer.TryOpen(
            registry, "file:///live.tone", MediaPlayerOpenOptions.Default, null, out var player, out var error), error);
        using (player)
        {
            Assert.Same(ingest, player.AudioRouter!.IngestPaceMaster);
        }
    }

    [TimingFact] // per-clip-thread scheduling soak - hangs the testhost on an oversubscribed CI VM regardless
                 // of thread count; opt-in via MFP_TIMING_TESTS=1 (players still scale with core count below).
    public void ManySimultaneousPlayers_AllStayScheduled_ThreadCostMeasured()
    {
        // TIME-01: evidence for the per-clip-thread scheduling model at a representative max simultaneous clip
        // count. Each player runs its own decode/pump; if that model missed deadlines or starved a clip under
        // load, some players would produce no audio within the soak window. We assert every player stays
        // scheduled and record the thread cost, so a "consolidate the scheduler" decision can be made on
        // evidence rather than speculation (the finding: the per-clip model keeps every clip scheduled here).
        // Scale the count with core count: the representative max is 24, but a constrained CI runner (2 cores,
        // heavily contended) would otherwise be oversubscribed into a multi-minute stall / blame-hang. Kept at
        // ≥2× cores so the per-clip model is still exercised under real oversubscription - which is the point.
        var players = Math.Clamp(Environment.ProcessorCount * 2, 4, 24);
        var startThreads = Process.GetCurrentProcess().Threads.Count;

        var running = new List<(MediaPlayer Player, CollectingBackend Backend)>();
        try
        {
            for (var i = 0; i < players; i++)
            {
                var source = new ToneSource(sampleRate: 48_000, channels: 2, chunks: 10_000_000); // effectively infinite for the soak
                var backend = new CollectingBackend();
                var registry = MediaRegistry.Build(b => b.AddDecoder(new FixedDecoderProvider(source)));
                var player = MediaPlayer.OpenAudio(registry, backend, $"file:///tone{i}.wav");
                player.Play();
                running.Add((player, backend));
            }

            // Every player must reach non-zero output within the window - i.e. none is starved of scheduling.
            var allScheduled = SpinWait.SpinUntil(
                () => running.All(r => r.Backend.Output.NonZeroSamples > 0),
                TimeSpan.FromSeconds(15));

            var peakThreads = Process.GetCurrentProcess().Threads.Count;
            var progressed = running.Count(r => r.Backend.Output.NonZeroSamples > 0);
            output.WriteLine(
                $"TIME-01 soak: {players} simultaneous players, {progressed}/{players} producing audio, " +
                $"process threads {startThreads} → {peakThreads} (~{peakThreads - startThreads} added, " +
                $"{(peakThreads - startThreads) / (double)players:0.0}/player)");

            Assert.True(allScheduled,
                $"only {progressed}/{players} simultaneous players stayed scheduled - the per-clip model starved a clip under load");
        }
        finally
        {
            foreach (var (player, _) in running)
                player.Dispose();
        }
    }

    [Theory]
    // Natural EOF, unchanged timebase epoch since Play → report exact Duration.
    [InlineData(true, 2.0, 5, 5, true)]
    // A seek (or reset/master swap) after EOF took a new epoch → the clamp is stale; read the
    // live clock (the seek target) instead of lying with Duration until the next Play.
    [InlineData(true, 2.0, 6, 5, false)]
    // Not completed → never clamp, regardless of epoch.
    [InlineData(false, 2.0, 5, 5, false)]
    [InlineData(false, 2.0, 6, 5, false)]
    // Live/unknown duration → nothing meaningful to clamp to.
    [InlineData(true, 0.0, 5, 5, false)]
    public void ShouldReportDurationAtNaturalEof_TruthTable(
        bool completedNaturally, double durationSeconds, long currentEpoch, long playEpoch, bool expected)
    {
        Assert.Equal(expected, MediaPlayer.ShouldReportDurationAtNaturalEof(
            completedNaturally, TimeSpan.FromSeconds(durationSeconds), currentEpoch, playEpoch));
    }

    [Fact]
    public void Position_AfterNaturalEof_ClampsToDuration_ThenSeekReadsSeekTarget()
    {
        // §2.14 regression: after a natural EOF the clamp used to apply until the next Play, so a
        // Seek between EOF and Play still reported Duration instead of the seek target.
        var source = new SeekableToneSource(sampleRate: 48_000, channels: 2, chunks: 8);
        var backend = new CollectingBackend();
        var registry = MediaRegistry.Build(b => b.AddDecoder(new FixedDecoderProvider(source)));

        using var player = MediaPlayer.OpenAudio(registry, backend, "file:///tone.wav");
        Assert.Equal(source.Duration, player.Duration);
        player.Play();

        Assert.True(
            SpinWait.SpinUntil(() => player.AudioRouter!.CompletedNaturally, TimeSpan.FromSeconds(5)),
            "player did not reach natural EOF within the window.");
        Assert.Equal(player.Duration, player.Position);
        Assert.False(player.IsRunning);

        var target = TimeSpan.FromMilliseconds(300);
        player.Seek(target);

        // The seek takes a new timebase epoch, so Position must drop the Duration clamp and read the
        // LIVE clock. That clock object is still advancing at this point (only IsRunning is synthesised
        // false at EOF - see MediaPlayer.IsRunning), so the reading is the seek target PLUS however long
        // this thread took to get here: an exact Equal made the outcome a wall-clock race and failed under
        // CPU contention with 0.35 s / 0.38 s against the 0.30 s target. Assert what the regression is
        // about instead - anchored on the seek target, no longer stale-clamped to the 2 s Duration.
        var afterSeek = player.Position;
        Assert.True(afterSeek >= target, $"position {afterSeek} rewound behind the seek target {target}");
        Assert.True(
            afterSeek < target + TimeSpan.FromMilliseconds(500),
            $"position {afterSeek} is not anchored on the seek target {target} (Duration is {player.Duration})");
        Assert.False(player.IsRunning);
    }

    private sealed class FixedDecoderProvider(IAudioSource source) : IMediaDecoderProvider
    {
        public string Name => "fixed";
        public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;
        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => throw new NotSupportedException();
        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => source;
    }

    private sealed class CapturingDecoderProvider : IMediaDecoderProvider
    {
        public string Name => "capturing";
        public int VideoOpenCount { get; private set; }
        public int AudioOpenCount { get; private set; }
        public VideoSourceOpenOptions VideoOptions { get; private set; } = null!;
        public AudioSourceOpenOptions AudioOptions { get; private set; } = null!;

        public double Probe(string uri, MediaKind kind) => kind is MediaKind.Audio or MediaKind.Video ? 1.0 : 0.0;

        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
        {
            VideoOpenCount++;
            VideoOptions = options ?? new VideoSourceOpenOptions();
            return new SyntheticVideoSource();
        }

        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options)
        {
            AudioOpenCount++;
            AudioOptions = options ?? new AudioSourceOpenOptions();
            return new ToneSource(sampleRate: 48_000, channels: 2, chunks: 16);
        }
    }

    private sealed class ToneSource(int sampleRate, int channels, int chunks) : IAudioSource
    {
        private int _remainingChunks = chunks;

        public AudioFormat Format { get; } = new(sampleRate, channels);
        public bool IsExhausted => Volatile.Read(ref _remainingChunks) <= 0;

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remainingChunks) < 0)
                return 0;

            for (var i = 0; i < destination.Length; i++)
                destination[i] = ((i % channels) + 1) / 16f;
            return destination.Length;
        }
    }

    /// <summary>A <see cref="ToneSource"/> that is also seekable: carries a fixed Duration (so the
    /// natural-EOF clamp has something to clamp to) and refills on Seek (a real decoder becomes
    /// readable again after seeking back from EOF).</summary>
    private sealed class SeekableToneSource(int sampleRate, int channels, int chunks) : IAudioSource, ISeekableSource
    {
        private readonly int _chunks = chunks;
        private int _remainingChunks = chunks;

        public AudioFormat Format { get; } = new(sampleRate, channels);
        public bool IsExhausted => Volatile.Read(ref _remainingChunks) <= 0;
        public TimeSpan Duration { get; } = TimeSpan.FromSeconds(2);
        public TimeSpan Position { get; private set; }

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remainingChunks) < 0)
                return 0;

            for (var i = 0; i < destination.Length; i++)
                destination[i] = ((i % channels) + 1) / 16f;
            return destination.Length;
        }

        public void Seek(TimeSpan position)
        {
            Position = position;
            Volatile.Write(ref _remainingChunks, _chunks);
        }
    }

    /// <summary>A tone source that advertises the D2 ingest-pacing opt-in - <c>ingestClock: null</c> models a
    /// live source that owns an ingest clock but was not configured to pace from it.</summary>
    private sealed class IngestPacedToneSource(int sampleRate, int channels, int chunks, IPlaybackClock? ingestClock)
        : IAudioSource, IIngestPacedSource
    {
        private int _remainingChunks = chunks;

        public IPlaybackClock? IngestPacingClock => ingestClock;
        public AudioFormat Format { get; } = new(sampleRate, channels);
        public bool IsExhausted => Volatile.Read(ref _remainingChunks) <= 0;

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remainingChunks) < 0)
                return 0;
            destination.Clear();
            return destination.Length;
        }
    }

    private sealed class FakeIngestClock : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;
        public bool IsAdvancing => false;
    }

    /// <summary>Format-only test resampler. The test verifies graph negotiation and never starts playback.</summary>
    private sealed class TargetRateAudioSource(IAudioSource inner, int sampleRate) : IAudioSource
    {
        public AudioFormat Format { get; } = new(sampleRate, inner.Format.Channels);
        public bool IsExhausted => inner.IsExhausted;
        public int ReadInto(Span<float> destination) => inner.ReadInto(destination);
    }

    private sealed class SyntheticVideoSource : IVideoSource, ISeekableSource
    {
        private int _frameIndex;
        private VideoFormat _format = new(16, 16, PixelFormat.Bgra32, new Rational(24, 1));

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => false;
        public TimeSpan Duration => TimeSpan.FromSeconds(10);
        public TimeSpan Position { get; private set; }

        public void SelectOutputFormat(PixelFormat format) =>
            _format = _format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var index = Interlocked.Increment(ref _frameIndex);
            Position = TimeSpan.FromSeconds(index / 24.0);
            var stride = _format.Width * 4;
            frame = new VideoFrame(
                Position,
                _format,
                [new byte[stride * _format.Height]],
                [stride]);
            return true;
        }

        public void Seek(TimeSpan position)
        {
            Position = position;
            Volatile.Write(ref _frameIndex, Math.Max(0, (int)Math.Round(position.TotalSeconds * 24)));
        }
    }

    private sealed class CollectingBackend : IAudioBackend
    {
        public CollectingOutput Output { get; } = new(sampleRate: 48_000, channels: 2);
        public string Name => "collecting";

        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
        [
            new("default", "Default", MaxChannels: 2, DefaultSampleRate: 48_000, IsDefault: true),
        ];

        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];

        public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
        {
            Assert.Equal("default", deviceId);
            Assert.Equal(Output.Format, format);
            return Output;
        }

        public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }

    private sealed class CollectingOutput(int sampleRate, int channels) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        private long _submittedSamples;
        private long _nonZeroSamples;

        public AudioFormat Format { get; } = new(sampleRate, channels);
        public long NonZeroSamples => Volatile.Read(ref _nonZeroSamples);
        public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(Volatile.Read(ref _submittedSamples) / (double)Format.Channels / Format.SampleRate);
        public bool IsAdvancing => true;

        public void Submit(ReadOnlySpan<float> samples)
        {
            foreach (var sample in samples)
            {
                if (sample != 0f)
                    Interlocked.Increment(ref _nonZeroSamples);
            }

            Interlocked.Add(ref _submittedSamples, samples.Length);
        }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
    }
}
