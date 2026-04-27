using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Clock;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.NDI;

/// <summary>
/// Consolidated NDI sink that can accept both audio and video and send them through one sender
/// with a shared A/V timing context.
/// </summary>
public class NDIAVEndpoint : IAVEndpoint, IFormatCapabilities<PixelFormat>, ISupportsDynamicMetadata
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIAVEndpoint));

    private readonly struct PendingVideo
    {
        public readonly ReadOnlyMemory<byte> Data;
        public readonly byte[]? Buffer;
        public readonly int Width;
        public readonly int Height;
        public readonly long PtsTicks;
        public readonly PixelFormat PixelFormat;
        public readonly int Bytes;
        public readonly VideoFrameHandle RetainedHandle;
        public readonly bool HasRetainedHandle;

        public PendingVideo(byte[] buffer, int width, int height, long ptsTicks, PixelFormat pixelFormat, int bytes)
        {
            Data = buffer.AsMemory(0, bytes);
            Buffer = buffer;
            Width = width;
            Height = height;
            PtsTicks = ptsTicks;
            PixelFormat = pixelFormat;
            Bytes = bytes;
            RetainedHandle = default;
            HasRetainedHandle = false;
        }

        public PendingVideo(VideoFrameHandle retainedHandle)
        {
            Data = retainedHandle.Data;
            Buffer = null;
            Width = retainedHandle.Width;
            Height = retainedHandle.Height;
            PtsTicks = retainedHandle.Pts.Ticks;
            PixelFormat = retainedHandle.PixelFormat;
            Bytes = retainedHandle.Data.Length;
            RetainedHandle = retainedHandle;
            HasRetainedHandle = true;
        }
    }

    private readonly struct PendingAudio
    {
        public readonly float[] Buffer;
        public readonly int Samples;
        /// <summary>
        /// Stream-time PTS (ticks) of the first sample in <see cref="Buffer"/>, or
        /// <see cref="long.MinValue"/> if unknown. Propagated through the sink so the
        /// write loop can stamp NDI timecodes in the same time domain as the video
        /// path — preventing the first-frame wall-clock race from baking itself into
        /// permanent A/V drift.
        /// </summary>
        public readonly long PtsTicks;

        public PendingAudio(float[] buffer, int samples, long ptsTicks)
        {
            Buffer = buffer;
            Samples = samples;
            PtsTicks = ptsTicks;
        }
    }

    // ImmutableArray gives us a value-type, allocation-free slice that also advertises
    // read-only intent in the type signature (IFormatCapabilities<PixelFormat>).
    private static readonly ImmutableArray<PixelFormat> sBgraPrefs = [PixelFormat.Bgra32, PixelFormat.Rgba32];
    private static readonly ImmutableArray<PixelFormat> sRgbaPrefs = [PixelFormat.Rgba32, PixelFormat.Bgra32];
    private static readonly ImmutableArray<PixelFormat> sNv12Prefs = [PixelFormat.Nv12];
    private static readonly ImmutableArray<PixelFormat> sUyvyPrefs = [PixelFormat.Uyvy422];
    private static readonly ImmutableArray<PixelFormat> sI420Prefs = [PixelFormat.Yuv420p];

    private readonly NDISender _sender;
    private readonly NDIAvTimingContext _timing = new();
    private readonly NDIClock _clock;
    // Per NDI SDK §13 ("NDI-Send"): audio, video and metadata frames may be submitted
    // to a sender "at any time, off any thread, and in any order".  The video send is
    // protected because Dispose may flush concurrently with the video thread; audio is
    // single-threaded (only AudioWriteLoop calls SendAudio) so it needs no lock.
    private readonly Lock _videoSendLock = new();

    // Video path
    private readonly bool _hasVideo;
    private readonly VideoFormat _videoTargetFormat;
    // Fps hint supplied by AVRouter at route-creation time from the source
    // stream's container frame-rate (r_frame_rate). Used by VideoWriteLoop so
    // the correct fps is declared from the first sent frame, letting NDI
    // receivers see the real content fps without waiting for PTS deltas.
    private volatile int _videoFpsHintNum;
    private volatile int _videoFpsHintDen;
    private readonly ConcurrentQueue<byte[]> _videoPool = new();
    private readonly PooledWorkQueue<PendingVideo> _videoWork = new();
    private readonly int _videoMaxPendingFrames;
    // Byte size of a single pooled video buffer.  Cached so ReceiveFrame can
    // grow the pool lazily (rather than LOH-preallocating the worst-case
    // backlog at construction) when steady-state demand outstrips the initial
    // pool.  Growth is still bounded by <see cref="_videoMaxPendingFrames"/>
    // because the work queue's reserve-slot gate rejects extra frames.
    private readonly int _videoBufferBytes;
    private readonly BasicPixelFormatConverter _videoConverter = new();
    private long _videoPoolMissDrops;
    private long _videoPoolLazyGrowths;
    private long _videoCapacityMissDrops;
    private long _videoFormatDrops;
    private long _videoQueueDrops;
    private long _videoPassthroughFrames;
    private long _videoConvertedFrames;
    private long _videoConversionDrops;

    // ── Async video send retention ─────────────────────────────────────────
    // NDIlib_send_send_video_async_v2 returns immediately; the SDK keeps a
    // reference to the submitted buffer until the NEXT async send (or until a
    // flush/sync send).  The buffer must therefore remain pinned and its
    // backing resources must not be recycled until we issue the next send.
    // These fields hold the one in-flight frame's resources.  They are only
    // touched by the video thread (plus Dispose after the thread has joined),
    // so no locking is required.
    private MemoryHandle _pendingAsyncPin;
    private byte[]? _pendingAsyncPoolBuffer;     // pf.Buffer to return to _videoPool
    private byte[]? _pendingAsyncScratch;        // rented from ArrayPool<byte>.Shared
    private IDisposable? _pendingAsyncTempOwner; // VideoConverter.Convert result owner
    private bool _pendingAsyncHasRetainedHandle;
    private VideoFrameHandle _pendingAsyncRetainedHandle;

    // Audio path
    private readonly bool _hasAudio;
    private readonly AudioFormat _audioTargetFormat;
    private readonly int _audioFramesPerBuffer;
    private readonly int _audioMaxPendingBuffers;
    private readonly IAudioResampler? _audioResampler;
    private readonly bool _ownsAudioResampler;
    private readonly ConcurrentQueue<float[]> _audioPool = new();
    private readonly PooledWorkQueue<PendingAudio> _audioWork = new();
    private long _audioPoolMissDrops;
    private long _audioCapacityMissDrops;
    private long _audioPoolLazyGrowths;
    private long _audioQueueDrops;
    private readonly DriftCorrector? _audioDriftCorrector;
    private readonly long _audioPtsDiscontinuityThresholdTicks;

    // FFmpeg load state: tri-state guard so the per-frame RGB/BGR conversion path
    // doesn't rebuild logger scopes on every frame when FFmpeg is unavailable.
    // 0 = untried, 1 = loaded, -1 = failed (don't retry).
    private int _ffmpegLoadState;

    // Log-once guard for the synthesize-timecode fallback on the video path.
    private int _loggedVideoSynthesizeFallback;

    // Log-once guard for the channel remix path (source channels ≠ target channels).
    private int _loggedChannelRemix;

    // A/V sync tracing — opt-in counters updated on the send paths so consumers
    // (debug UIs, test apps) can derive the actual wall-clock submit cadence and
    // observe video-vs-audio launch/drain offsets + current stamped timecodes.
    // All reads use Volatile/Interlocked so a debug thread can poll without locking.
    private readonly System.Diagnostics.Stopwatch _sinkClock = System.Diagnostics.Stopwatch.StartNew();
    private long _firstVideoSubmitMs = -1;
    private long _firstAudioSubmitMs = -1;
    private long _lastVideoSubmitMs;
    private long _lastAudioSubmitMs;
    private long _audioContentFloorTicks = long.MinValue;
    private volatile int _sourceGeneration;
    private long _videoFramesSubmitted;
    private long _audioBuffersSubmitted;
    private long _audioSamplesSubmitted;
    private long _lastVideoTimecodeTicks = long.MinValue;
    private long _lastAudioTimecodeTicks = long.MinValue;
    private long _lastVideoPtsTicks = long.MinValue;
    private long _lastAudioPtsTicks = long.MinValue;
    // Sequential timestamping of a matched A/V pair so a reader can compute
    // "at last video submit, what was the most recent audio submit position?"
    // (i.e., how many ms of audio have been emitted by the time each video frame
    // leaves the sink).  These are snapshots, not monotonic — use them for UI
    // readout, not for correctness decisions.
    private long _atLastVideoAudioMs;
    private long _atLastAudioVideoMs;
    private const int StartupAudioLeadHoldMs = 220;
    // When the first dequeued video frame is already far from stream start
    // (DropOldest overflow during decoder warm-up), the audio path may need
    // longer than the base startup hold to reach the same content PTS.
    // Cap the adaptive hold so video-only routes cannot stall indefinitely.
    private const int StartupAudioLeadMaxHoldMs = 1500;

    private Thread? _videoThread;
    private Thread? _audioThread;
    private CancellationTokenSource? _cts;
    private int _started;
    private bool _disposed;

    public string Name { get; }
    public bool IsRunning => Volatile.Read(ref _started) == 1;
    public bool HasAudio => _hasAudio;
    public bool HasVideo => _hasVideo;
    /// <summary>
    /// Sender-side media clock driven by submitted A/V timecodes.
    /// Exposed for host applications that want to explicitly select this clock.
    /// </summary>
    public IMediaClock Clock => _clock;

    /// <summary>
    /// The audio drift corrector instance, or <see langword="null"/> if drift correction is disabled.
    /// </summary>
    public DriftCorrector? AudioDriftCorrection => _audioDriftCorrector;

    public IReadOnlyList<PixelFormat> SupportedFormats => _videoTargetFormat.PixelFormat switch
    {
        PixelFormat.Bgra32 => sBgraPrefs,
        PixelFormat.Rgba32 => sRgbaPrefs,
        PixelFormat.Nv12 => sNv12Prefs,
        PixelFormat.Uyvy422 => sUyvyPrefs,
        PixelFormat.Yuv420p => sI420Prefs,
        _ => sRgbaPrefs,
    };

    public PixelFormat? PreferredFormat => _videoTargetFormat.PixelFormat;

    public NDIAVEndpoint(
        NDISender sender,
        VideoFormat? videoTargetFormat = null,
        AudioFormat? audioTargetFormat = null,
        NDIEndpointPreset preset = NDIEndpointPreset.Balanced,
        string? name = null,
        bool preferPerformanceOverQuality = false,
        int videoPoolCount = 0,
        int videoMaxPendingFrames = 0,
        int audioFramesPerBuffer = 1024,
        int audioPoolCount = 0,
        int audioMaxPendingBuffers = 0,
        IAudioResampler? audioResampler = null,
        bool enableAudioDriftCorrection = false,
        int audioPtsDiscontinuityThresholdMs = 500,
        int audioUnderrunRecoveryThresholdMs = 80)
    {
        _sender = sender;
        Name = name ?? "NDIAVEndpoint";
        _clock = new NDIClock(sampleRate: audioTargetFormat?.SampleRate ?? 48_000);

        // Apply the underrun-recovery threshold to the shared timing context up front so
        // the very first audio buffer sees the right policy.  Values <= 0 revert to the
        // static default inside the context.
        _timing.SetUnderrunRecoveryThresholdMs(audioUnderrunRecoveryThresholdMs);

        if (videoTargetFormat is { } v)
        {
            // NDI assumes a fixed YUV color space based on resolution (SDK §21.1):
            //   SD → Rec.601, HD → Rec.709, UHD (>1920 or >1080) → Rec.2020.
            // For UHD sources encoded in Rec.709, sending UYVY causes the receiver to
            // misinterpret colors as Rec.2020.  Fall back to RGBA for UHD to avoid this.
            bool isUhd = v.Width > 1920 || v.Height > 1080;
            var is422Source = v.PixelFormat is PixelFormat.Uyvy422 or PixelFormat.Yuv422p10;
            PixelFormat fallbackPixelFormat;
            if (isUhd)
                fallbackPixelFormat = PixelFormat.Rgba32;  // safe for any color space
            else if (preferPerformanceOverQuality || is422Source)
                fallbackPixelFormat = PixelFormat.Uyvy422;
            else
                fallbackPixelFormat = PixelFormat.Rgba32;
            var px = v.PixelFormat is PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Nv12 or PixelFormat.Uyvy422 or PixelFormat.Yuv420p
                ? v.PixelFormat
                : fallbackPixelFormat;
            _videoTargetFormat = v with { PixelFormat = px };
            _hasVideo = true;

            var videoPreset = NDIVideoPresetOptions.For(preset);
            if (videoPoolCount <= 0) videoPoolCount = videoPreset.PoolCount;
            if (videoMaxPendingFrames <= 0) videoMaxPendingFrames = videoPreset.MaxPendingFrames;


            _videoMaxPendingFrames = videoMaxPendingFrames;

            int w = _videoTargetFormat.Width > 0 ? _videoTargetFormat.Width : 1280;
            int h = _videoTargetFormat.Height > 0 ? _videoTargetFormat.Height : 720;
            // Pool buffers must hold the *source* frame data (copied in ReceiveFrame)
            // which may be larger than the target format (conversion happens later).
            int srcBytes = GetVideoBufferBytes(v.PixelFormat, w, h);
            int dstBytes = GetVideoBufferBytes(_videoTargetFormat.PixelFormat, w, h);
            int bytes = Math.Max(srcBytes, dstBytes);
            _videoBufferBytes = bytes;
            // Only pre-allocate the preset's steady-state pool count; anything
            // beyond is grown on demand via the lazy-grow path in ReceiveFrame.
            int preallocate = Math.Max(1, videoPoolCount);
            for (int i = 0; i < preallocate; i++)
                _videoPool.Enqueue(new byte[bytes]);
        }

        _hasAudio = audioTargetFormat.HasValue;
        if (audioTargetFormat is { } atf)
        {
            _audioTargetFormat = atf;
            var audioPreset = NDIAudioPresetOptions.For(preset);
            if (audioFramesPerBuffer <= 0) audioFramesPerBuffer = 512;
            if (audioPoolCount <= 0) audioPoolCount = audioPreset.PoolCount;
            if (audioMaxPendingBuffers <= 0) audioMaxPendingBuffers = audioPreset.MaxPendingBuffers;

            _audioFramesPerBuffer = audioFramesPerBuffer;
            _audioMaxPendingBuffers = audioMaxPendingBuffers;

            if (audioResampler == null)
            {
                // Default to SwrResampler (FFmpeg's sinc, high quality) when FFmpeg is
                // available.  LinearResampler is a minimal fallback and its aliasing is
                // audible on music content — the NDI sink is latency-sensitive, not
                // quality-neutral, so we prefer Swr here by default.  Callers wanting the
                // old behaviour can pass a LinearResampler explicitly via the options
                // (or set AudioResampler to any other IAudioResampler implementation).
                IAudioResampler? swr = null;
                try
                {
                    FFmpegLoader.EnsureLoaded();
                    swr = new SwrResampler();
                }
                catch (Exception ex)
                {
                    Log.LogWarning(ex,
                        "NDIAVEndpoint '{Name}': FFmpeg unavailable, falling back to LinearResampler " +
                        "(lower quality — may sound grainy on 44.1↔48 kHz conversions).", Name);
                }

                _audioResampler = swr ?? new LinearResampler();
                _ownsAudioResampler = true;
            }
            else
            {
                _audioResampler = audioResampler;
                _ownsAudioResampler = false;
            }

            int headroom = Math.Max(1, audioPreset.BufferHeadroomMultiplier);
            for (int i = 0; i < audioPoolCount; i++)
                _audioPool.Enqueue(new float[_audioFramesPerBuffer * _audioTargetFormat.Channels * headroom]);

            if (enableAudioDriftCorrection)
            {
                // Drift correction is queue-depth driven — only meaningful when the
                // sender back-pressures us (clockAudio:true). On the async/unclocked
                // path the queue stays near zero → PI saturates at +maxCorrection,
                // producing a permanent rate skew. We can't see the sender's clock
                // flag from here, so just warn loudly.
                Log.LogWarning(
                    "NDIAVEndpoint '{Name}': EnableAudioDriftCorrection=true. This is queue-depth driven and " +
                    "only meaningful with clockAudio:true on the NDISender. On clockAudio:false (default) " +
                    "the PI controller saturates and produces a permanent rate skew — disable this unless " +
                    "you explicitly created the sender with clockAudio:true.", Name);

                _audioDriftCorrector = new DriftCorrector(
                    targetDepth: Math.Max(1, _audioMaxPendingBuffers / 2),
                    ownerName: Name);
            }
        }

        _audioPtsDiscontinuityThresholdTicks = TimeSpan.FromMilliseconds(
            audioPtsDiscontinuityThresholdMs > 0 ? audioPtsDiscontinuityThresholdMs : 500).Ticks;

        Log.LogInformation("Created NDIAVEndpoint '{Name}': hasVideo={HasVideo}, hasAudio={HasAudio}, preset={Preset}",
            Name, _hasVideo, _hasAudio, preset);
        if (_hasVideo)
            Log.LogDebug("NDIAVEndpoint '{Name}' video: {Width}x{Height} px={PixelFormat}, maxPending={MaxPending}",
                Name, _videoTargetFormat.Width, _videoTargetFormat.Height, _videoTargetFormat.PixelFormat, _videoMaxPendingFrames);
        if (_hasAudio)
            Log.LogDebug("NDIAVEndpoint '{Name}' audio: {SampleRate}Hz/{Channels}ch, fpb={FramesPerBuffer}, maxPending={MaxPending}",
                Name, _audioTargetFormat.SampleRate, _audioTargetFormat.Channels, _audioFramesPerBuffer, _audioMaxPendingBuffers);
    }

    /// <summary>
    /// Creates an <see cref="NDIAVEndpoint"/> using an <see cref="NDIAVSinkOptions"/> record.
    /// This is the preferred constructor.
    /// </summary>
    public NDIAVEndpoint(NDISender sender, NDIAVSinkOptions? options) : this(
        sender,
        options?.VideoTargetFormat,
        options?.AudioTargetFormat,
        options?.Preset ?? NDIEndpointPreset.Balanced,
        options?.Name,
        options?.PreferPerformanceOverQuality ?? false,
        options?.VideoPoolCount ?? 0,
        options?.VideoMaxPendingFrames ?? 0,
        options?.AudioFramesPerBuffer ?? 1024,
        options?.AudioPoolCount ?? 0,
        options?.AudioMaxPendingBuffers ?? 0,
        options?.AudioResampler,
        options?.EnableAudioDriftCorrection ?? false,
        options?.AudioPtsDiscontinuityThresholdMs ?? 500,
        options?.AudioUnderrunRecoveryThresholdMs ?? 80)
    { }

    // ── Factories (§1.4) ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a fully-configured <see cref="NDIAVEndpoint"/> ready to register
    /// on an <c>AVRouter</c>. Accepts the same options as the options-based
    /// constructor but makes intent obvious at call sites and mirrors the
    /// <c>PortAudioEndpoint.Create</c> pattern (§1.4).
    /// </summary>
    public static NDIAVEndpoint Create(NDISender sender, NDIAVSinkOptions? options = null) =>
        new(sender, options);

    /// <summary>
    /// Shortcut factory: builds <see cref="NDIAVSinkOptions"/> inline from the
    /// two most common format parameters and the preset.
    /// </summary>
    public static NDIAVEndpoint Create(
        NDISender         sender,
        VideoFormat?      videoTargetFormat = null,
        AudioFormat?      audioTargetFormat = null,
        NDIEndpointPreset preset            = NDIEndpointPreset.Balanced,
        string?           name              = null) =>
        new(sender, videoTargetFormat, audioTargetFormat, preset, name);

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _audioDriftCorrector?.Reset();

        // Reset the resampler's fractional phase / pending-tail buffer so a Stop →
        // Start cycle begins from a clean state (otherwise the first new buffer
        // interpolates against stale samples from the prior session).
        _audioResampler?.Reset();

        // Reset shared A/V timing so a Stop → Start cycle does not inherit a
        // stale audio cursor from the previous session (which would resume
        // audio timecodes from an arbitrary past point).
        _timing.Reset();
        _clock.Reset();
        _clock.Start();

        // Drain any pending work left over from a prior session so stale PTS
        // buffers don't leak into the new timeline.  Pooled buffers are
        // returned to their pools so the upcoming session starts warm.
        _audioWork.Drain(pa => _audioPool.Enqueue(pa.Buffer));
        _videoWork.Drain(pv =>
        {
            if (pv.Buffer != null) _videoPool.Enqueue(pv.Buffer);
            if (pv.HasRetainedHandle) pv.RetainedHandle.Release();
        });

        // Reset first-submit sentinels so the per-session stats reflect this
        // session's launch, not the previous one.
        Interlocked.Exchange(ref _firstVideoSubmitMs, -1);
        Interlocked.Exchange(ref _firstAudioSubmitMs, -1);
        Interlocked.Exchange(ref _audioContentFloorTicks, long.MinValue);
        Interlocked.Exchange(ref _loggedVideoSynthesizeFallback, 0);
        Interlocked.Exchange(ref _loggedChannelRemix, 0);

        if (_hasVideo)
        {
            _videoThread = new Thread(VideoWriteLoop)
            {
                Name = $"{Name}.VideoThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _videoThread.Start();
        }

        if (_hasAudio)
        {
            _audioThread = new Thread(AudioWriteLoop)
            {
                Name = $"{Name}.AudioThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _audioThread.Start();
        }

        Log.LogInformation("NDIAVEndpoint '{Name}' started: videoThread={HasVideo}, audioThread={HasAudio}",
            Name, _hasVideo, _hasAudio);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1)
            return;

        Log.LogInformation("Stopping NDIAVEndpoint '{Name}'", Name);
        _cts?.Cancel();

        await Task.Run(() => _videoThread?.Join(TimeSpan.FromSeconds(3)), ct).ConfigureAwait(false);
        await Task.Run(() => _audioThread?.Join(TimeSpan.FromSeconds(3)), ct).ConfigureAwait(false);

        // Flush and release the last pending async video send so the pin and
        // backing buffers don't leak between Stop → Start → Stop cycles.  Safe
        // to call even when no frames were ever sent (FlushAsync on an idle
        // sender is a no-op; ReleasePendingAsyncVideo tolerates empty state).
        if (_hasVideo)
        {
            try { lock (_videoSendLock) _sender.FlushAsync(); }
            catch (Exception ex) { Log.LogDebug(ex, "NDI flush-async on stop failed: {Message}", ex.Message); }
            ReleasePendingAsyncVideo();
        }
        _clock.Stop();
    }

    /// <summary>
    /// §8.2 — zero-copy fast path for ref-counted frames already in the sink's
    /// target pixel format. We retain the incoming handle and enqueue it directly
    /// so the async NDI send thread can pin and submit without an intermediate
    /// copy into <see cref="_videoPool"/>.
    /// </summary>
    public void ReceiveFrame(in VideoFrameHandle handle)
    {
        if (!_hasVideo || Volatile.Read(ref _started) == 0)
            return;

        int bytes = handle.Data.Length;
        if (bytes <= 0)
        {
            Interlocked.Increment(ref _videoCapacityMissDrops);
            return;
        }

        // Atomic reserve-slot pattern so concurrent producers can't exceed the cap by N.
        if (!_videoWork.TryReserveSlot(_videoMaxPendingFrames))
        {
            Interlocked.Increment(ref _videoQueueDrops);
            return;
        }

        if (handle.IsRefCounted &&
            handle.PixelFormat == _videoTargetFormat.PixelFormat)
        {
            var retained = handle.Retain();
            _videoWork.EnqueueReserved(new PendingVideo(retained));
            return;
        }

        EnqueueCopiedVideoFrameReserved(handle.Frame, bytes);
    }

    private void EnqueueCopiedVideoFrameReserved(in VideoFrame frame, int bytes)
    {
        if (!_videoPool.TryDequeue(out var dst))
        {
            // Pool empty: grow lazily if we know the per-frame size.  The
            // work-queue cap (reserved above) already bounds growth to
            // _videoMaxPendingFrames, so we can't allocate unbounded memory.
            // Only drop if we truly can't allocate (zero buffer size = sink
            // wasn't configured for video).
            if (_videoBufferBytes > 0)
            {
                dst = new byte[_videoBufferBytes];
                Interlocked.Increment(ref _videoPoolLazyGrowths);
            }
            else
            {
                _videoWork.ReleaseReservation();
                Interlocked.Increment(ref _videoPoolMissDrops);
                return;
            }
        }

        if (dst.Length < bytes)
        {
            _videoPool.Enqueue(dst);
            _videoWork.ReleaseReservation();
            Interlocked.Increment(ref _videoCapacityMissDrops);
            return;
        }

        frame.Data.Span[..bytes].CopyTo(dst.AsSpan(0, bytes));
        _videoWork.EnqueueReserved(new PendingVideo(dst, frame.Width, frame.Height, frame.Pts.Ticks, frame.PixelFormat, bytes));
    }

    /// <summary>
    /// Carries the stream-time PTS of the first sample through to the write loop so
    /// NDI timecodes for audio can share the video path's media-time domain.
    /// </summary>
    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat, TimeSpan sourcePts)
        => ReceiveBufferCore(buffer, frameCount, sourceFormat,
            sourcePts == TimeSpan.MinValue ? long.MinValue : sourcePts.Ticks);

    private void ReceiveBufferCore(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat, long sourcePtsTicks)
    {
        if (!_hasAudio || Volatile.Read(ref _started) == 0)
            return;

        int outCh = _audioTargetFormat.Channels;

        // Channel-count reconciliation is the router's responsibility (AVRouter's channel
        // map + endpoint-target-format handshake).  If we still see a mismatch here the
        // router is misconfigured — log once, drop extra channels or zero-fill missing
        // ones, and continue.  Mixing policy (e.g. surround → stereo downmix) is NOT the
        // sink's job.
        if (sourceFormat.Channels != outCh &&
            Interlocked.CompareExchange(ref _loggedChannelRemix, 1, 0) == 0)
        {
            Log.LogWarning(
                "NDIAVEndpoint '{Name}': received audio with {SrcCh} channels but target is {DstCh}. " +
                "The AVRouter should apply a channel map for this route.  Falling back to " +
                "copy-min/zero-fill — surround mixing/downmix must happen in the router, not here.",
                Name, sourceFormat.Channels, outCh);
        }

        // Compute rate-adjusted + drift-corrected output frame count (§6.2).
        int writeFrames = SinkBufferHelper.ComputeWriteFrames(
            frameCount, sourceFormat.SampleRate, _audioTargetFormat.SampleRate,
            _audioDriftCorrector, _audioWork.Count);
        int writeSamples = writeFrames * outCh;

        if (!_audioPool.TryDequeue(out var dest))
        {
            // Pool empty: allocate on demand.  The work-queue's reserve-slot
            // gate (below) bounds the total buffer count to _audioMaxPendingBuffers,
            // so growth is still capped.
            dest = new float[writeSamples];
            Interlocked.Increment(ref _audioPoolMissDrops);
            Interlocked.Increment(ref _audioPoolLazyGrowths);
        }

        if (dest.Length < writeSamples)
        {
            // Existing pool buffer too small (tick jitter or non-default
            // DefaultFramesPerBuffer produced a larger-than-expected write).
            // Replace it with a right-sized array rather than silently dropping
            // the caller's audio — the upper bound is still the work-queue cap.
            dest = new float[writeSamples];
            Interlocked.Increment(ref _audioCapacityMissDrops);
            Interlocked.Increment(ref _audioPoolLazyGrowths);
        }

        int writtenSamples;
        if (_audioResampler != null && sourceFormat.SampleRate != _audioTargetFormat.SampleRate)
        {
            // Cross-rate: let SinkBufferHelper call the resampler with the nominal
            // output size (so its phase advances by exactly one buffer) and then
            // apply drift correction as a post-pass hold/trim on the output.
            // Passing the drift-inflated writeFrames directly to the resampler
            // would over-advance its internal phase, desynchronising cross-buffer
            // state and producing audible distortion.
            writtenSamples = SinkBufferHelper.ResampleWithDrift(
                _audioResampler, buffer, dest.AsSpan(0, writeSamples),
                sourceFormat, _audioTargetFormat.SampleRate, outCh, writeFrames);
        }
        else
        {
            // Same rate: direct copy with drift-corrected last-frame hold (§6.2).
            SinkBufferHelper.CopySameRate(buffer, dest.AsSpan(0, writeSamples),
                frameCount, writeFrames, outCh, clearTail: true);
            writtenSamples = writeSamples;
        }

        if (writtenSamples <= 0)
        {
            _audioPool.Enqueue(dest);
            Interlocked.Increment(ref _audioCapacityMissDrops);
            return;
        }

        // Atomic reserve-slot pattern to keep the cap honoured under racing producers.
        if (!_audioWork.TryReserveSlot(_audioMaxPendingBuffers))
        {
            _audioPool.Enqueue(dest);
            Interlocked.Increment(ref _audioQueueDrops);
            return;
        }

        // No explicit timecode is stamped here: the AudioWriteLoop derives a
        // media-time timecode through NDIAvTimingContext so audio & video share one
        // time domain (SDK §13.2 — explicit timecodes override synthesis).  The
        // stream PTS travels with the buffer to keep sample-accurate accumulation
        // anchored to the decoder's real clock.
        _audioWork.EnqueueReserved(new PendingAudio(dest, writtenSamples, sourcePtsTicks));
    }

    public VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        long queueDrops = Interlocked.Read(ref _videoQueueDrops);
        long dropped = Interlocked.Read(ref _videoPoolMissDrops)
                       + Interlocked.Read(ref _videoCapacityMissDrops)
                       + Interlocked.Read(ref _videoFormatDrops)
                       + queueDrops;

        return new VideoEndpointDiagnosticsSnapshot(
            PassthroughFrames: Interlocked.Read(ref _videoPassthroughFrames),
            ConvertedFrames: Interlocked.Read(ref _videoConvertedFrames),
            DroppedFrames: dropped,
            QueueDepth: _videoWork.Count,
            QueueDrops: queueDrops);
    }

    /// <summary>
    /// Immutable snapshot of A/V submission timing — lets debug surfaces see the
    /// wall-clock times (relative to sink construction) of first / last video and
    /// audio submits, the timecodes that were stamped, and the per-submit pair
    /// offsets.  All times are in milliseconds unless named "Ticks" (100 ns units,
    /// same convention as <see cref="TimeSpan.Ticks"/>).
    /// </summary>
    public readonly record struct AvSyncSnapshot(
        long SinkUptimeMs,
        long FirstVideoSubmitMs,
        long FirstAudioSubmitMs,
        long LastVideoSubmitMs,
        long LastAudioSubmitMs,
        long VideoFramesSubmitted,
        long AudioBuffersSubmitted,
        long AudioSamplesSubmitted,
        long LastVideoTimecodeTicks,
        long LastAudioTimecodeTicks,
        long LastVideoPtsTicks,
        long LastAudioPtsTicks,
        long AudioMsAtLastVideoSubmit,
        long VideoMsAtLastAudioSubmit)
    {
        /// <summary>
        /// Wall-clock gap between first video and first audio submit.
        /// Positive → video was submitted first (the typical cause of video-ahead-of-audio).
        /// Returns <see cref="long.MinValue"/> if one side hasn't submitted yet.
        /// </summary>
        public long FirstSubmitGapMs =>
            FirstVideoSubmitMs < 0 || FirstAudioSubmitMs < 0
                ? long.MinValue
                : FirstAudioSubmitMs - FirstVideoSubmitMs;

        /// <summary>
        /// Difference between the timecodes on the most recently submitted video frame
        /// and audio buffer.  If both paths are in the same media-time domain this is
        /// small (&lt; 1 buffer duration); a large constant value means we fell back to
        /// <c>TimecodeSynthesize</c> on one side and explicit PTS on the other.
        /// Ticks units; <see cref="long.MinValue"/> if either side is unset.
        /// </summary>
        public long LastTimecodeDeltaTicks =>
            LastVideoTimecodeTicks == long.MinValue || LastAudioTimecodeTicks == long.MinValue
                ? long.MinValue
                : LastVideoTimecodeTicks - LastAudioTimecodeTicks;

        /// <summary>
        /// Difference between the stream PTS on the most recently submitted video frame
        /// and audio buffer.  This is a media-time comparison (independent of NDI).
        /// </summary>
        public long LastPtsDeltaTicks =>
            LastVideoPtsTicks == long.MinValue || LastAudioPtsTicks == long.MinValue
                ? long.MinValue
                : LastVideoPtsTicks - LastAudioPtsTicks;
    }

    /// <summary>Snapshot the current A/V submission timing counters.</summary>
    public AvSyncSnapshot GetAvSyncSnapshot() => new(
        SinkUptimeMs: _sinkClock.ElapsedMilliseconds,
        FirstVideoSubmitMs: Interlocked.Read(ref _firstVideoSubmitMs),
        FirstAudioSubmitMs: Interlocked.Read(ref _firstAudioSubmitMs),
        LastVideoSubmitMs: Interlocked.Read(ref _lastVideoSubmitMs),
        LastAudioSubmitMs: Interlocked.Read(ref _lastAudioSubmitMs),
        VideoFramesSubmitted: Interlocked.Read(ref _videoFramesSubmitted),
        AudioBuffersSubmitted: Interlocked.Read(ref _audioBuffersSubmitted),
        AudioSamplesSubmitted: Interlocked.Read(ref _audioSamplesSubmitted),
        LastVideoTimecodeTicks: Interlocked.Read(ref _lastVideoTimecodeTicks),
        LastAudioTimecodeTicks: Interlocked.Read(ref _lastAudioTimecodeTicks),
        LastVideoPtsTicks: Interlocked.Read(ref _lastVideoPtsTicks),
        LastAudioPtsTicks: Interlocked.Read(ref _lastAudioPtsTicks),
        AudioMsAtLastVideoSubmit: Interlocked.Read(ref _atLastVideoAudioMs),
        VideoMsAtLastAudioSubmit: Interlocked.Read(ref _atLastAudioVideoMs));

    public void Dispose()
    {
        if (_disposed) return;
        // Mark disposed FIRST so any in-flight thread observing _started / _disposed
        // stops re-entering SendVideo/SendAudio before we tear down native resources.
        _disposed = true;
        Volatile.Write(ref _started, 0);

        Log.LogInformation(
            "Disposing NDIAVEndpoint '{Name}': videoPassthrough={VideoPassthrough}, videoConverted={VideoConverted}, videoConversionDrops={VideoConversionDrops}, " +
            "videoPoolMissDrops={VideoPoolMissDrops}, videoCapacityDrops={VideoCapacityDrops}, videoFormatDrops={VideoFormatDrops}, videoQueueDrops={VideoQueueDrops}, " +
            "audioPoolMissDrops={AudioPoolMissDrops}, audioCapacityDrops={AudioCapacityDrops}, audioQueueDrops={AudioQueueDrops}, audioDriftRatio={AudioDriftRatio}",
            Name,
            Interlocked.Read(ref _videoPassthroughFrames), Interlocked.Read(ref _videoConvertedFrames), Interlocked.Read(ref _videoConversionDrops),
            Interlocked.Read(ref _videoPoolMissDrops), Interlocked.Read(ref _videoCapacityMissDrops), Interlocked.Read(ref _videoFormatDrops), Interlocked.Read(ref _videoQueueDrops),
            Interlocked.Read(ref _audioPoolMissDrops), Interlocked.Read(ref _audioCapacityMissDrops), Interlocked.Read(ref _audioQueueDrops),
            _audioDriftCorrector?.CorrectionRatio ?? 1.0);

        _cts?.Cancel();
        // NDI SendVideo on a congested network can genuinely take several seconds to
        // return.  Allow more time here than the old 2 s so we don't race the drain
        // phase with a still-running thread, which would corrupt the pool queues.
        _videoThread?.Join(TimeSpan.FromSeconds(5));
        _audioThread?.Join(TimeSpan.FromSeconds(5));

        // Flush any pending async video send and release its pinned buffer.
        // FlushAsync() calls NDIlib_send_send_video_async_v2(instance, NULL),
        // which tells the SDK it's done with the last submitted buffer.  After
        // this call returns it is safe to unpin and recycle.
        if (_hasVideo)
        {
            try { lock (_videoSendLock) _sender.FlushAsync(); }
            catch (Exception ex) { Log.LogDebug(ex, "NDI flush-async on stop failed: {Message}", ex.Message); }
            ReleasePendingAsyncVideo();
        }

        // Drain pending queues so pooled buffers are not leaked and retained
        // zero-copy frame refs are released.
        _videoWork.Drain(pv =>
        {
            if (pv.Buffer != null) _videoPool.Enqueue(pv.Buffer);
            if (pv.HasRetainedHandle) pv.RetainedHandle.Release();
        });
        _audioWork.Drain(pa => _audioPool.Enqueue(pa.Buffer));

        _videoWork.Dispose();
        _audioWork.Dispose();
        _videoConverter.Dispose();
        if (_ownsAudioResampler) _audioResampler?.Dispose();
        _clock.Dispose();
    }

    /// <summary>
    /// Releases the resources retained for the most recently issued async video
    /// send.  Safe to call when nothing is pending (all fields are null/default).
    /// Must be called on the video thread (or after it has joined).
    /// </summary>
    private void ReleasePendingAsyncVideo()
    {
        _pendingAsyncPin.Dispose();
        _pendingAsyncPin = default;

        if (_pendingAsyncHasRetainedHandle)
        {
            _pendingAsyncRetainedHandle.Release();
            _pendingAsyncRetainedHandle = default;
            _pendingAsyncHasRetainedHandle = false;
        }

        var tempOwner = _pendingAsyncTempOwner; _pendingAsyncTempOwner = null;
        var scratch   = _pendingAsyncScratch;   _pendingAsyncScratch   = null;
        var poolBuf   = _pendingAsyncPoolBuffer; _pendingAsyncPoolBuffer = null;

        tempOwner?.Dispose();
        if (scratch != null) ArrayPool<byte>.Shared.Return(scratch);
        if (poolBuf != null) _videoPool.Enqueue(poolBuf);
    }

    private static int VideoLineStride(PixelFormat fmt, int w) => fmt switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 => w * 4,
        PixelFormat.Uyvy422 => w * 2,
        PixelFormat.Nv12 or PixelFormat.Yuv420p => w,
        // Per NDI SDK (Processing.NDI.structs.h §line_stride_in_bytes):
        // 0 tells the SDK to compute stride from FourCC and xres.
        _ => 0,
    };

    private static int GetVideoBufferBytes(PixelFormat fmt, int width, int height) => fmt switch
    {
        PixelFormat.Uyvy422 => width * height * 2,
        PixelFormat.Nv12 => width * height + (width * ((height + 1) / 2)),
        PixelFormat.Yuv420p =>
            width * height +
            ((width + 1) / 2) * ((height + 1) / 2) +
            ((width + 1) / 2) * ((height + 1) / 2),
        _ => width * height * 4,
    };

    private static NDIFourCCVideoType ToFourCc(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Bgra32 => NDIFourCCVideoType.Bgra,
        PixelFormat.Rgba32 => NDIFourCCVideoType.Rgba,
        PixelFormat.Nv12 => NDIFourCCVideoType.Nv12,
        PixelFormat.Uyvy422 => NDIFourCCVideoType.Uyvy,
        PixelFormat.Yuv420p => NDIFourCCVideoType.I420,
        _ => NDIFourCCVideoType.Rgba,
    };

    private bool EnsureFfmpegLoaded()
    {
        // Tri-state guard: 0 = untried, 1 = loaded, -1 = failed.  FFmpegLoader.EnsureLoaded
        // is idempotent but on failure the per-frame RGB/BGR conversion path would
        // rebuild a logger scope on every frame.  Cache the failure so subsequent frames
        // cost nothing.
        int state = Volatile.Read(ref _ffmpegLoadState);
        if (state == 1) return true;
        if (state == -1) return false;

        try
        {
            FFmpegLoader.EnsureLoaded();
            Interlocked.Exchange(ref _ffmpegLoadState, 1);
            return true;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _ffmpegLoadState, -1) != -1)
                Log.LogWarning(ex, "FFmpeg load failed; I210→RGBA path disabled for this sink");
            return false;
        }
    }

    private static unsafe bool TryConvertI210ToUyvyInPlace(byte[] buffer, int width, int height, int sourceBytes, out int outputBytes)
    {
        outputBytes = 0;
        if (width <= 0 || height <= 0) return false;

        int yStride = width * 2;
        int uvStride = width;
        int ySize = yStride * height;
        int uvSize = uvStride * height;
        int required = ySize + (uvSize * 2);
        if (sourceBytes < required || buffer.Length < required) return false;

        int dstStride = width * 2;
        outputBytes = dstStride * height;
        if (buffer.Length < outputBytes) return false;

        fixed (byte* pBuf = buffer)
        {
            ushort* pY = (ushort*)pBuf;
            ushort* pU = (ushort*)(pBuf + ySize);
            ushort* pV = (ushort*)(pBuf + ySize + uvSize);

            // Process bottom-to-top so the in-place write never risks clobbering rows
            // that are still waiting to be read.
            for (int row = height - 1; row >= 0; row--)
            {
                ushort* yRow = pY + (row * width);
                ushort* uRow = pU + (row * (width >> 1));
                ushort* vRow = pV + (row * (width >> 1));
                byte* dstRow = pBuf + row * dstStride;

                // §8.3 / Tier 6 #30 — SIMD converter path (SSE2) with scalar fallback.
                if (!TryConvertI210RowToUyvySse2(yRow, uRow, vRow, dstRow, width))
                    ConvertI210RowToUyvyScalar(yRow, uRow, vRow, dstRow, width);
            }
        }

        return true;
    }

    private static unsafe bool TryConvertI210RowToUyvySse2(
        ushort* yRow, ushort* uRow, ushort* vRow, byte* dstRow, int width)
    {
        if (!Sse2.IsSupported)
            return false;

        int pairCount = width >> 1;
        if (pairCount < 8)
            return false;

        var mask = Vector128.Create((ushort)0x03FF);
        var add2 = Vector128.Create((ushort)2);

        byte* tmpY = stackalloc byte[16];
        byte* tmpU = stackalloc byte[16];
        byte* tmpV = stackalloc byte[16];

        int pair = 0;
        int vectorPairs = pairCount & ~7; // 8 chroma pairs per SSE2 block
        for (; pair < vectorPairs; pair += 8)
        {
            var u = Narrow10To8(Sse2.LoadVector128(uRow + pair), mask, add2);
            var v = Narrow10To8(Sse2.LoadVector128(vRow + pair), mask, add2);

            int yIndex = pair << 1;
            var yLo = Narrow10To8(Sse2.LoadVector128(yRow + yIndex), mask, add2);
            var yHi = Narrow10To8(Sse2.LoadVector128(yRow + yIndex + 8), mask, add2);

            Sse2.Store(tmpU, u);
            Sse2.Store(tmpV, v);
            Sse2.Store(tmpY, yLo);
            Sse2.Store(tmpY + 8, yHi);

            byte* d = dstRow + (pair << 2);
            for (int i = 0; i < 8; i++)
            {
                int yOff = i << 1;
                d[0] = tmpU[i];
                d[1] = tmpY[yOff];
                d[2] = tmpV[i];
                d[3] = tmpY[yOff + 1];
                d += 4;
            }
        }

        for (; pair < pairCount; pair++)
            ConvertI210PairToUyvyScalar(yRow, uRow, vRow, dstRow, pair);

        return true;
    }

    private static Vector128<byte> Narrow10To8(Vector128<ushort> value, Vector128<ushort> mask, Vector128<ushort> add2)
    {
        var v = Sse2.And(value, mask);
        v = Sse2.Add(v, add2);
        v = Sse2.ShiftRightLogical(v, 2);
        return Sse2.PackUnsignedSaturate(v.AsInt16(), Vector128<short>.Zero);
    }

    private static unsafe void ConvertI210RowToUyvyScalar(
        ushort* yRow, ushort* uRow, ushort* vRow, byte* dstRow, int width)
    {
        int pairCount = width >> 1;
        for (int pair = 0; pair < pairCount; pair++)
            ConvertI210PairToUyvyScalar(yRow, uRow, vRow, dstRow, pair);
    }

    private static unsafe void ConvertI210PairToUyvyScalar(
        ushort* yRow, ushort* uRow, ushort* vRow, byte* dstRow, int pair)
    {
        int yOff = pair << 1;
        byte* d = dstRow + (pair << 2);

        // Narrow 10-bit to 8-bit: (v + 2) >> 2
        d[0] = (byte)(((uRow[pair] & 0x03FF) + 2) >> 2);
        d[1] = (byte)(((yRow[yOff] & 0x03FF) + 2) >> 2);
        d[2] = (byte)(((vRow[pair] & 0x03FF) + 2) >> 2);
        d[3] = (byte)(((yRow[yOff + 1] & 0x03FF) + 2) >> 2);
    }

    private static bool TryConvertI210ToRgbaManaged(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, bool dstRgba)
    {
        if (width <= 0 || height <= 0) return false;

        int yStride = width * 2;
        int uvStride = width;
        int ySize = yStride * height;
        int uvSize = uvStride * height;
        int srcRequired = ySize + (uvSize * 2);
        int dstRequired = width * height * 4;
        if (src.Length < srcRequired || dst.Length < dstRequired) return false;

        var yPlane = src[..ySize];
        var uPlane = src.Slice(ySize, uvSize);
        var vPlane = src.Slice(ySize + uvSize, uvSize);

        static byte Clamp(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 255f) return 255;
            return (byte)(v + 0.5f);
        }

        for (int y = 0; y < height; y++)
        {
            int yRow = y * yStride;
            int uvRow = y * uvStride;
            int dstRow = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int yOff = yRow + (x * 2);
                int uvOff = uvRow + ((x >> 1) * 2);

                int y10 = BinaryPrimitives.ReadUInt16LittleEndian(yPlane.Slice(yOff, 2)) & 0x03FF;
                int u10 = BinaryPrimitives.ReadUInt16LittleEndian(uPlane.Slice(uvOff, 2)) & 0x03FF;
                int v10 = BinaryPrimitives.ReadUInt16LittleEndian(vPlane.Slice(uvOff, 2)) & 0x03FF;

                float yf = y10 / 1023f;
                float uf = (u10 - 512f) / 512f;
                float vf = (v10 - 512f) / 512f;

                byte r = Clamp((yf + (1.5748f * vf)) * 255f);
                byte g = Clamp((yf - (0.1873f * uf) - (0.4681f * vf)) * 255f);
                byte b = Clamp((yf + (1.8556f * uf)) * 255f);

                int d = dstRow + (x * 4);
                if (dstRgba)
                {
                    dst[d] = r;
                    dst[d + 1] = g;
                    dst[d + 2] = b;
                    dst[d + 3] = 255;
                }
                else
                {
                    dst[d] = b;
                    dst[d + 1] = g;
                    dst[d + 2] = r;
                    dst[d + 3] = 255;
                }
            }
        }

        return true;
    }

    private static unsafe bool TryConvertI210ToRgbaFfmpeg(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int width,
        int height,
        bool dstRgba,
        ref SwsContext* sws,
        byte*[] scratchSrcData,
        int[] scratchSrcStride,
        byte*[] scratchDstData,
        int[] scratchDstStride)
    {
        if (width <= 0 || height <= 0) return false;

        int yStride = width * 2;
        int uvStride = width;
        int ySize = yStride * height;
        int uvSize = uvStride * height;
        int srcRequired = ySize + (uvSize * 2);
        int dstRequired = width * height * 4;
        if (src.Length < srcRequired || dst.Length < dstRequired) return false;

        AVPixelFormat dstFmt = dstRgba ? AVPixelFormat.AV_PIX_FMT_RGBA : AVPixelFormat.AV_PIX_FMT_BGRA;
        sws = ffmpeg.sws_getCachedContext(
            sws,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
            width,
            height,
            dstFmt,
            2,
            null,
            null,
            null);

        if (sws == null) return false;

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            byte* y = pSrc;
            byte* u = pSrc + ySize;
            byte* v = pSrc + ySize + uvSize;

            scratchSrcData[0] = y; scratchSrcData[1] = u; scratchSrcData[2] = v; scratchSrcData[3] = null;
            scratchSrcStride[0] = yStride; scratchSrcStride[1] = uvStride; scratchSrcStride[2] = uvStride; scratchSrcStride[3] = 0;
            scratchDstData[0] = pDst; scratchDstData[1] = null; scratchDstData[2] = null; scratchDstData[3] = null;
            scratchDstStride[0] = width * 4; scratchDstStride[1] = 0; scratchDstStride[2] = 0; scratchDstStride[3] = 0;

            return ffmpeg.sws_scale(sws, scratchSrcData, scratchSrcStride, 0, height, scratchDstData, scratchDstStride) == height;
        }
    }

    void ISupportsDynamicMetadata.AnnounceUpcomingVideoFormat(VideoFormat format)
    {
        if (format.FrameRateNumerator > 0 && format.FrameRateDenominator > 0)
            ApplyVideoFpsHintCore(format.FrameRateNumerator, format.FrameRateDenominator);

        // A new source is about to produce frames. Reset the shared A/V
        // timing context so audio timecodes reseed from the new video PTS
        // instead of inheriting the cursor from the previous source (which
        // would place audio in a different time domain and bake a permanent
        // A/V offset at the receiver). Also reset first-submit sentinels so
        // the startup A/V gate fires for this source.
        _sourceGeneration++;
        _timing.Reset();
        Interlocked.Exchange(ref _firstVideoSubmitMs, -1);
        Interlocked.Exchange(ref _firstAudioSubmitMs, -1);
        Interlocked.Exchange(ref _audioContentFloorTicks, long.MinValue);

        // Reset the NDIClock's monotonic floor so the new source's PTS values
        // (starting near 0) can advance the clock.  Without this, the floor
        // from the previous file's last PTS blocks all clock advances, which
        // stalls any non-LiveMode route (e.g. local video) gated on this clock.
        _clock.ResetForNewSource();

        // Drain leftover video/audio frames from the previous source so they
        // don't corrupt the timing state for the new source.
        while (_videoWork.TryDequeue(out var stale))
        {
            if (stale.Buffer != null) _videoPool.Enqueue(stale.Buffer);
            if (stale.HasRetainedHandle) stale.RetainedHandle.Release();
        }
        while (_audioWork.TryDequeue(out var staleAudio))
            _audioPool.Enqueue(staleAudio.Buffer);
    }

    void ISupportsDynamicMetadata.ApplyVideoFpsHint(int numerator, int denominator)
        => ApplyVideoFpsHintCore(numerator, denominator);

    private void ApplyVideoFpsHintCore(int numerator, int denominator)
    {
        if (numerator <= 0 || denominator <= 0) return;
        _videoFpsHintNum = numerator;
        _videoFpsHintDen = denominator;
    }

    private unsafe void VideoWriteLoop()
    {
        var token = _cts!.Token;
        // Prefer fps from source stream metadata (propagated via ISupportsDynamicMetadata
        // at route creation), then fall back to the configured target format, then to 30000/1001.
        int hintNum = _videoFpsHintNum;
        int hintDen = _videoFpsHintDen;
        int fpsNum = hintNum > 0 ? hintNum
            : _videoTargetFormat.FrameRateNumerator > 0 ? _videoTargetFormat.FrameRateNumerator : 30000;
        int fpsDen = hintNum > 0 ? hintDen
            : _videoTargetFormat.FrameRateDenominator > 0 ? _videoTargetFormat.FrameRateDenominator : 1001;
        int lastKnownHintNum = hintNum;
        int lastKnownHintDen = hintDen;
        SwsContext* ffmpegSws = null;

        // PTS-delta tracking for fps auto-adaptation.
        long lastPtsTicks = long.MinValue;
        const int DeltaWindowSize = 8;
        var ptsDeltaRing = new long[DeltaWindowSize];
        int deltaFilled = 0;

        // Pre-allocate scratch arrays for sws_scale to avoid 4 heap allocations per frame.
        var swsSrcData   = new byte*[4];
        var swsSrcStride = new int[4];
        var swsDstData   = new byte*[4];
        var swsDstStride = new int[4];

        while (!token.IsCancellationRequested)
        {
            if (!_videoWork.WaitForItem(token)) break;

            bool drainedStartupBurst = false;

            while (_videoWork.TryDequeue(out var pf))
            {
                int frameGen = _sourceGeneration;

                // True once this frame's pool buffer + conversion scratch are
                // handed off to _pendingAsync* for retention past SendVideoAsync.
                // If the send never issues (conversion error, format mismatch,
                // exception), the outer/inner finally blocks reclaim them.
                bool handedOff = false;

                try
                {
                    // Startup A/V gate: on the very first video send, hold until audio has
                    // warmed up (or the timeout expires), then drain any frames that queued
                    // in _videoWork during the hold.  Falls through to send pf normally so
                    // video PTS and audio cursor stay anchored to the same media-time origin.
                    if (_hasAudio && Interlocked.Read(ref _firstVideoSubmitMs) < 0)
                    {
                        if (pf.PtsTicks >= 0)
                        {
                            _timing.ObserveVideoPts(pf.PtsTicks);
                            Interlocked.Exchange(ref _audioContentFloorTicks, pf.PtsTicks);
                        }

                        long holdMs = StartupAudioLeadHoldMs;
                        if (pf.PtsTicks > 0)
                        {
                            // Keep first-video release aligned with the content floor that
                            // AudioWriteLoop enforces on startup.  Without this, a large
                            // first video PTS (e.g. ~1 s after DropOldest warm-up churn)
                            // lets video start after 220 ms while audio is still waiting
                            // to reach that floor, yielding visible "video ahead of audio".
                            long ptsMs = (long)TimeSpan.FromTicks(pf.PtsTicks).TotalMilliseconds;
                            holdMs = Math.Clamp(ptsMs + 120, StartupAudioLeadHoldMs, StartupAudioLeadMaxHoldMs);
                        }

                        long holdDeadlineMs = _sinkClock.ElapsedMilliseconds + holdMs;
                        long waitStartedMs = _sinkClock.ElapsedMilliseconds;
                        while (!token.IsCancellationRequested &&
                               Interlocked.Read(ref _firstAudioSubmitMs) < 0 &&
                               _sinkClock.ElapsedMilliseconds < holdDeadlineMs)
                        {
                            Thread.Sleep(2);
                        }

                        int drainedCount = 0;
                        while (_videoWork.TryDequeue(out var stale))
                        {
                            drainedCount++;
                            if (stale.Buffer != null) _videoPool.Enqueue(stale.Buffer);
                            if (stale.HasRetainedHandle) stale.RetainedHandle.Release();
                        }

                        long waitedMs = _sinkClock.ElapsedMilliseconds - waitStartedMs;
                        bool audioReady = Interlocked.Read(ref _firstAudioSubmitMs) >= 0;
                        long floorPtsMs = pf.PtsTicks > 0 ? (long)TimeSpan.FromTicks(pf.PtsTicks).TotalMilliseconds : -1;
                        Log.LogInformation(
                            "NDIAVEndpoint '{Name}' startup gate: sourceGen={SourceGen}, firstVideoPtsMs={FirstVideoPtsMs}, " +
                            "holdMs={HoldMs}, waitedMs={WaitedMs}, audioReady={AudioReady}, drainedVideoFrames={Drained}",
                            Name, frameGen, floorPtsMs, holdMs, waitedMs, audioReady, drainedCount);

                        drainedStartupBurst = true;
                        // Fall through: pf is sent normally; the pacing loop below
                        // will hold it until the audio cursor is within threshold.
                    }

                    // Detect source changes via the fps-hint volatile fields
                    // (set by AnnounceUpcomingVideoFormat on new route creation)
                    // and re-seed the fps + delta ring so a prior file's stale
                    // PTS deltas can't pollute the new content's fps declaration.
                    {
                        int curHintNum = _videoFpsHintNum;
                        int curHintDen = _videoFpsHintDen;
                        if (curHintNum > 0 && curHintDen > 0 &&
                            (curHintNum != lastKnownHintNum || curHintDen != lastKnownHintDen))
                        {
                            fpsNum = curHintNum;
                            fpsDen = curHintDen;
                            lastKnownHintNum = curHintNum;
                            lastKnownHintDen = curHintDen;
                            lastPtsTicks = long.MinValue;
                            deltaFilled = 0;
                        }
                    }

                    ReadOnlyMemory<byte> payload;
                    PixelFormat sendFormat;
                    IDisposable? tempOwner = null;
                    byte[]? scratchBuffer = null;

                    try
                    {
                        if (pf.PixelFormat == _videoTargetFormat.PixelFormat)
                        {
                            payload = pf.Data;
                            sendFormat = pf.PixelFormat;
                            Interlocked.Increment(ref _videoPassthroughFrames);
                        }
                        else if (pf.PixelFormat == PixelFormat.Yuv422p10 && _videoTargetFormat.PixelFormat == PixelFormat.Uyvy422)
                        {
                            if (pf.Buffer is null ||
                                !TryConvertI210ToUyvyInPlace(pf.Buffer, pf.Width, pf.Height, pf.Bytes, out int uyvyBytes))
                            {
                                Interlocked.Increment(ref _videoConversionDrops);
                                Interlocked.Increment(ref _videoFormatDrops);
                                continue;
                            }

                            payload = pf.Buffer.AsMemory(0, uyvyBytes);
                            sendFormat = PixelFormat.Uyvy422;
                            Interlocked.Increment(ref _videoConvertedFrames);
                        }
                        else if (pf.PixelFormat == PixelFormat.Yuv422p10
                                 && (_videoTargetFormat.PixelFormat == PixelFormat.Rgba32 || _videoTargetFormat.PixelFormat == PixelFormat.Bgra32))
                        {
                            int rgbaBytes = pf.Width * pf.Height * 4;
                            bool converted = false;

                            scratchBuffer = ArrayPool<byte>.Shared.Rent(rgbaBytes);

                            if (EnsureFfmpegLoaded())
                            {
                                converted = TryConvertI210ToRgbaFfmpeg(
                                    pf.Data.Span,
                                    scratchBuffer.AsSpan(0, rgbaBytes),
                                    pf.Width,
                                    pf.Height,
                                    _videoTargetFormat.PixelFormat == PixelFormat.Rgba32,
                                    ref ffmpegSws,
                                    swsSrcData, swsSrcStride, swsDstData, swsDstStride);
                            }

                            if (!converted)
                            {
                                converted = TryConvertI210ToRgbaManaged(
                                    pf.Data.Span,
                                    scratchBuffer.AsSpan(0, rgbaBytes),
                                    pf.Width,
                                    pf.Height,
                                    _videoTargetFormat.PixelFormat == PixelFormat.Rgba32);
                            }

                            if (!converted)
                            {
                                Interlocked.Increment(ref _videoConversionDrops);
                                Interlocked.Increment(ref _videoFormatDrops);
                                continue;
                            }

                            payload = scratchBuffer.AsMemory(0, rgbaBytes);
                            sendFormat = _videoTargetFormat.PixelFormat;
                            Interlocked.Increment(ref _videoConvertedFrames);
                        }
                        else
                        {
                            var srcFrame = new VideoFrame(
                                pf.Width,
                                pf.Height,
                                pf.PixelFormat,
                                pf.Data,
                                TimeSpan.FromTicks(pf.PtsTicks));

                            var converted = _videoConverter.Convert(srcFrame, _videoTargetFormat.PixelFormat);
                            payload = converted.Data;
                            sendFormat = converted.PixelFormat;
                            tempOwner = converted.MemoryOwner;
                            Interlocked.Increment(ref _videoConvertedFrames);
                        }

                        // Pin the payload for the async send. NDI's async API
                        // (send_video_async_v2) returns immediately but the SDK keeps
                        // a pointer to this buffer until the NEXT async send or a
                        // flush — so the pin must outlive this loop iteration.
                        MemoryHandle sendHandle = payload.Pin();
                        nint sendPtr = (nint)sendHandle.Pointer;
                        if (sendPtr == nint.Zero)
                        {
                            sendHandle.Dispose();
                            Interlocked.Increment(ref _videoFormatDrops);
                            continue;
                        }

                        // Stamp the NDI timecode with the frame's stream-time PTS (ticks,
                        // 100 ns units — matches NDI's UTC-since-epoch convention closely
                        // enough for receivers to align by).  Falling back to
                        // TimecodeSynthesize (wall-clock-at-submit) creates a *permanent*
                        // A/V offset whenever the audio decoder warms up later than the
                        // video decoder: the first video submit gets a wall-clock stamp,
                        // the first audio submit gets a later wall-clock stamp, and from
                        // there `clockVideo`/`clockAudio` hold both paced at media rate —
                        // keeping that warm-up delta baked in forever.  Using the stream
                        // PTS as the timecode puts both streams in the same media-time
                        // domain regardless of submit order.  Timestamp is still filled
                        // in by the SDK (§21.1).
                        // If the source changed since we dequeued this frame
                        // (AnnounceUpcomingVideoFormat fired on another thread),
                        // this frame is from the old source. Sending it would
                        // poison the freshly-reset timing/clock state with stale
                        // PTS values.
                        if (frameGen != _sourceGeneration)
                        {
                            continue;
                        }

                        long videoPts = pf.PtsTicks;
                        long videoTimecode = videoPts >= 0 ? videoPts : NDIConstants.TimecodeSynthesize;
                        if (videoPts >= 0)
                        {
                            _timing.ObserveVideoPts(videoPts);
                            _clock.UpdateFromFrame(videoPts);
                        }
                        else if (Interlocked.CompareExchange(ref _loggedVideoSynthesizeFallback, 1, 0) == 0)
                        {
                            // Warn once per session: the sink is pairing media-PTS audio
                            // timecodes with wall-clock video timecodes, which creates
                            // a permanent A/V offset at the receiver.
                            Log.LogWarning(
                                "NDIAVEndpoint '{Name}': video frame arrived with no PTS — using NDI timecode-synthesize " +
                                "on the video path.  If the audio path carries stream PTS this introduces a permanent " +
                                "A/V offset at the receiver.  Upstream should supply valid video PTS.", Name);
                        }

                        // Adapt NDI frame-rate declaration to actual content fps.
                        // The configured rate (e.g. 60fps) is wrong for 24fps content;
                        // receivers use FrameRateN/D for buffer management, so a mismatch
                        // causes burst delivery every few seconds.
                        // Skip PTS=0: SafePts maps AV_NOPTS_VALUE to TimeSpan.Zero, so
                        // zero is ambiguous. Using > 0 avoids a false cluster of zero deltas
                        // that would produce a huge bogus first-delta and pollute the ring.
                        if (videoPts > 0 && lastPtsTicks > 0)
                        {
                            long delta = videoPts - lastPtsTicks;
                            if (delta < -TimeSpan.TicksPerSecond)
                            {
                                // PTS jumped backward by > 1 s — new source or
                                // backward seek. Flush the delta ring and re-read
                                // the fps hint so the NDI frame-rate declaration
                                // reflects the new content immediately.
                                deltaFilled = 0;
                                lastPtsTicks = long.MinValue;
                                int freshNum = _videoFpsHintNum;
                                int freshDen = _videoFpsHintDen;
                                if (freshNum > 0 && freshDen > 0)
                                {
                                    fpsNum = freshNum;
                                    fpsDen = freshDen;
                                    lastKnownHintNum = freshNum;
                                    lastKnownHintDen = freshDen;
                                }
                            }
                            else if (delta > 83_333 && delta < 10_000_000) // 1–120 fps bounds
                            {
                                ptsDeltaRing[deltaFilled % DeltaWindowSize] = delta;
                                deltaFilled++;
                                // Snap on the very first valid delta so frame 2 (the second
                                // non-zero-PTS frame) already declares the correct fps.
                                int windowCount = Math.Min(deltaFilled, DeltaWindowSize);
                                long avgDelta = 0;
                                for (int i = 0; i < windowCount; i++) avgDelta += ptsDeltaRing[i];
                                avgDelta /= windowCount;
                                var (sn, sd) = SnapFps(avgDelta);
                                if (sn > 0 && (sn != fpsNum || sd != fpsDen))
                                {
                                    fpsNum = sn;
                                    fpsDen = sd;
                                }
                            }
                        }
                        if (videoPts > 0)
                        {
                            if (drainedStartupBurst)
                            {
                                // The gap between this frame and the next spans the
                                // drained range and would produce an outlier delta
                                // that averages to ~15 fps in the ring.  Skip seeding
                                // lastPtsTicks so the next frame also starts with
                                // lastPtsTicks == MinValue → its delta is skipped too,
                                // and normal tracking resumes on the frame after.
                                drainedStartupBurst = false;
                            }
                            else
                            {
                                lastPtsTicks = videoPts;
                            }
                        }

                        var vf = new NDIVideoFrameV2
                        {
                            Xres = pf.Width,
                            Yres = pf.Height,
                            FourCC = ToFourCc(sendFormat),
                            FrameRateN = fpsNum,
                            FrameRateD = fpsDen,
                            PictureAspectRatio = pf.Height > 0 ? (float)pf.Width / pf.Height : 1f,
                            FrameFormatType = NDIFrameFormatType.Progressive,
                            Timecode = videoTimecode,
                            PData = sendPtr,
                            LineStrideInBytes = VideoLineStride(sendFormat, pf.Width),
                            PMetadata = nint.Zero,
                            Timestamp = NDIConstants.TimestampUndefined
                        };

                        // Issue the async send.  The SDK takes a reference to the
                        // pinned buffer and returns immediately; the previous send's
                        // buffer is now guaranteed to be free (this call is the event
                        // that releases it, per NDI SDK §12.4).
                        try
                        {
                            lock (_videoSendLock)
                                _sender.SendVideoAsync(vf);
                        }
                        catch
                        {
                            // Send failed before any SDK retention: free the pin now.
                            sendHandle.Dispose();
                            throw;
                        }

                        // The prior pending send's resources are now safe to reclaim.
                        ReleasePendingAsyncVideo();

                        // Hand off this send's resources to the "pending" slot — they
                        // will be released on the next iteration's ReleasePendingAsyncVideo
                        // (or on shutdown via FlushAsync + ReleasePendingAsyncVideo).
                        _pendingAsyncPin        = sendHandle;
                        _pendingAsyncPoolBuffer = pf.Buffer;
                        _pendingAsyncScratch    = scratchBuffer;
                        _pendingAsyncTempOwner  = tempOwner;
                        _pendingAsyncHasRetainedHandle = pf.HasRetainedHandle;
                        _pendingAsyncRetainedHandle = pf.RetainedHandle;
                        handedOff = true;

                        // Diagnostics (post-submit: reflects the async-enqueue time,
                        // which is effectively "now" since async returns immediately).
                        long nowMs = _sinkClock.ElapsedMilliseconds;
                        Interlocked.CompareExchange(ref _firstVideoSubmitMs, nowMs, -1);
                        Interlocked.Exchange(ref _lastVideoSubmitMs, nowMs);
                        Interlocked.Increment(ref _videoFramesSubmitted);
                        Interlocked.Exchange(ref _lastVideoTimecodeTicks, videoTimecode);
                        Interlocked.Exchange(ref _lastVideoPtsTicks, videoPts);
                        Interlocked.Exchange(ref _atLastVideoAudioMs,
                            Interlocked.Read(ref _lastAudioSubmitMs));
                    }
                    catch (NotSupportedException)
                    {
                        Interlocked.Increment(ref _videoConversionDrops);
                        Interlocked.Increment(ref _videoFormatDrops);
                    }
                    finally
                    {
                        // Only release conversion scratch / temp-owner locally when
                        // the send did NOT hand them off to the pending-async slot.
                        if (!handedOff)
                        {
                            tempOwner?.Dispose();
                            if (scratchBuffer != null)
                                ArrayPool<byte>.Shared.Return(scratchBuffer);
                        }
                    }
                }

                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                        Log.LogError(ex, "NDI video send exception: {Message}", ex.Message);
                }
                finally
                {
                    // Same policy as the inner finally: only release queue-owned
                    // resources when they were not handed off to async retention.
                    if (!handedOff)
                    {
                        if (pf.Buffer != null) _videoPool.Enqueue(pf.Buffer);
                        if (pf.HasRetainedHandle) pf.RetainedHandle.Release();
                    }
                }
            }
        }

        if (ffmpegSws != null)
            ffmpeg.sws_freeContext(ffmpegSws);
    }

    /// <summary>
    /// Maps an average PTS delta (in 100 ns ticks) to the nearest standard NDI
    /// frame-rate fraction.  Returns (0, 0) if no standard rate is within 2 %.
    /// </summary>
    private static (int n, int d) SnapFps(long avgDeltaTicks)
    {
        if (avgDeltaTicks <= 0) return (0, 0);
        double fps = 10_000_000.0 / avgDeltaTicks;
        ReadOnlySpan<(int n, int d)> standards =
        [
            (120, 1), (60000, 1001), (60, 1), (50, 1), (48, 1),
            (30000, 1001), (30, 1), (25, 1), (24000, 1001), (24, 1), (15, 1)
        ];
        double bestDiff = double.MaxValue;
        (int n, int d) best = (0, 0);
        foreach (var (n, d) in standards)
        {
            double diff = Math.Abs(fps - (double)n / d);
            if (diff < bestDiff) { bestDiff = diff; best = (n, d); }
        }
        double bestFps = (double)best.n / best.d;
        return bestDiff / bestFps < 0.02 ? best : (0, 0);
    }

    /// <summary>
    /// Background thread that dequeues pending audio buffers, deinterleaves them into
    /// NDI's planar float layout (one contiguous block per channel), and calls
    /// <c>SendAudio</c> under the send lock.  The deinterleave uses sample-outer /
    /// channel-inner order for better cache locality on the interleaved source.
    /// </summary>
    private unsafe void AudioWriteLoop()
    {
        var token = _cts!.Token;
        int channels = _audioTargetFormat.Channels;
        float[] planar = ArrayPool<float>.Shared.Rent(_audioFramesPerBuffer * channels);

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_audioWork.WaitForItem(token)) break;

                while (_audioWork.TryDequeue(out var pending))
                {
                    // Skip audio buffers whose content predates the first video
                    // frame.  With NDI-only output the video subscription uses
                    // DropOldest and may overflow during decoder warmup, so the
                    // first available video PTS is often > 0 while audio starts
                    // from PTS 0.  Sending that early audio with a timecode
                    // seeded from the video PTS creates a permanent content-level
                    // A/V offset (audio data from position 0 plays alongside
                    // video data from position 83 ms, for example).
                    if (Interlocked.Read(ref _firstAudioSubmitMs) < 0)
                    {
                        long floor = Interlocked.Read(ref _audioContentFloorTicks);
                        if (floor > 0 && pending.PtsTicks >= 0 && pending.PtsTicks < floor)
                        {
                            _audioPool.Enqueue(pending.Buffer);
                            continue;
                        }
                    }

                    var interleaved = pending.Buffer;
                    int sampleValues = pending.Samples;
                    int samplesPerChannel = sampleValues / channels;
                    int planarNeed = channels * samplesPerChannel;
                    if (planar.Length < planarNeed)
                    {
                        ArrayPool<float>.Shared.Return(planar);
                        planar = ArrayPool<float>.Shared.Rent(planarNeed);
                    }

                    // Deinterleave: s-outer/c-inner gives sequential reads on the
                    // interleaved source buffer (better cache locality than c-outer/s-inner).
                    for (int s = 0; s < samplesPerChannel; s++)
                    {
                        int srcBase = s * channels;
                        for (int c = 0; c < channels; c++)
                            planar[c * samplesPerChannel + s] = interleaved[srcBase + c];
                    }

                    _audioPool.Enqueue(interleaved);

                    fixed (float* pData = planar)
                    {
                        // Derive the NDI audio timecode.
                        //
                        // Primary path: sample-counted cursor via the timing context
                        // (`ReserveAudioTimecode`).  In steady state the router pushes
                        // audio at wall-clock rate, so the cursor advances at wall-clock
                        // rate — exactly matching the video path's PTS-based timecodes.
                        //
                        // On audio-decoder starvation (CPU spike, GC pause) the router
                        // briefly pushes fewer/no buffers, so the cursor would naturally
                        // lag.  The timing context snaps the cursor forward to the latest
                        // video PTS whenever the lag exceeds the underrun-recovery
                        // threshold (default 80 ms), so a transient decoder glitch can't
                        // turn into a permanent A/V offset at the receiver.
                        //
                        // Discontinuity path: if the producer stream-PTS jumps forward
                        // past the cursor by more than AudioPtsDiscontinuityThresholdMs
                        // (default 500 ms) the source almost certainly seeked — re-anchor
                        // the cursor to the new PTS so the NDI wire tc immediately
                        // reflects the new media position.  Forward-only; backward jumps
                        // keep the cursor stationary so wire tc stays monotonic.
                        //
                        // NB: we intentionally do NOT stamp from `pending.PtsTicks`
                        // (producer stream PTS) on every buffer.  A previous revision did,
                        // but that caused audio timecodes to permanently lag video after
                        // any decoder underrun — the receiver then saw a persistent skew
                        // that looked like "video sped up and never corrected".
                        long audioTimecode;
                        long cursorNow = _timing.NextAudioTimecodeTicks;
                        if (pending.PtsTicks >= 0 &&
                            cursorNow != long.MinValue &&
                            pending.PtsTicks - cursorNow > _audioPtsDiscontinuityThresholdTicks)
                        {
                            audioTimecode = _timing.AdvanceAudioCursorTo(
                                pending.PtsTicks, samplesPerChannel, _audioTargetFormat.SampleRate);
                        }
                        else
                        {
                            audioTimecode = _timing.ReserveAudioTimecode(
                                samplesPerChannel, _audioTargetFormat.SampleRate);
                        }

                        var frame = new NDIAudioFrameV3
                        {
                            SampleRate = _audioTargetFormat.SampleRate,
                            NoChannels = channels,
                            NoSamples = samplesPerChannel,
                            FourCC = NDIFourCCAudioType.Fltp,
                            PData = (nint)pData,
                            ChannelStrideInBytes = samplesPerChannel * sizeof(float),
                            Timecode = audioTimecode,
                            Timestamp = NDIConstants.TimestampUndefined
                        };
                        // SendAudio (NDIlib_send_send_audio_v3) is synchronous and copies the
                        // payload before returning — no cross-thread contention on the planar
                        // buffer, and no async-retention to manage.  The per-sink audio thread
                        // is the only caller, so no send-lock is needed here.
                        _sender.SendAudio(frame);

                        long nowMs = _sinkClock.ElapsedMilliseconds;
                        Interlocked.CompareExchange(ref _firstAudioSubmitMs, nowMs, -1);
                        Interlocked.Exchange(ref _lastAudioSubmitMs, nowMs);
                        Interlocked.Increment(ref _audioBuffersSubmitted);
                        Interlocked.Add(ref _audioSamplesSubmitted, samplesPerChannel);
                        Interlocked.Exchange(ref _lastAudioTimecodeTicks, audioTimecode);
                        Interlocked.Exchange(ref _lastAudioPtsTicks, pending.PtsTicks);
                        Interlocked.Exchange(ref _atLastAudioVideoMs,
                            Interlocked.Read(ref _lastVideoSubmitMs));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(planar);
        }
    }
}
