using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.NDI;

/// <summary>
/// Consolidated NDI sink that can accept both audio and video and send them through one sender
/// with a shared A/V timing context.
/// </summary>
public sealed class NDIAVSink : IAVEndpoint, IFormatCapabilities<PixelFormat>
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIAVSink));

    private readonly struct PendingVideo
    {
        public readonly byte[] Buffer;
        public readonly int Width;
        public readonly int Height;
        public readonly long PtsTicks;
        public readonly PixelFormat PixelFormat;
        public readonly int Bytes;

        public PendingVideo(byte[] buffer, int width, int height, long ptsTicks, PixelFormat pixelFormat, int bytes)
        {
            Buffer = buffer;
            Width = width;
            Height = height;
            PtsTicks = ptsTicks;
            PixelFormat = pixelFormat;
            Bytes = bytes;
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
    // Per NDI SDK §13 ("NDI-Send"): audio, video and metadata frames may be submitted
    // to a sender "at any time, off any thread, and in any order".  We therefore keep
    // send access thread-safe with *separate* locks per stream so a large RGBA
    // SendVideo cannot block a concurrent SendAudio (which would starve downstream
    // receivers — NDI audio timecode sensitivity is high).
    private readonly Lock _videoSendLock = new();
    private readonly Lock _audioSendLock = new();

    // Video path
    private readonly bool _hasVideo;
    private readonly VideoFormat _videoTargetFormat;
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
    private long _audioQueueDrops;
    private readonly DriftCorrector? _audioDriftCorrector;

    // A/V sync tracing — opt-in counters updated on the send paths so consumers
    // (debug UIs, test apps) can derive the actual wall-clock submit cadence and
    // observe video-vs-audio launch/drain offsets + current stamped timecodes.
    // All reads use Volatile/Interlocked so a debug thread can poll without locking.
    private readonly System.Diagnostics.Stopwatch _sinkClock = System.Diagnostics.Stopwatch.StartNew();
    private long _firstVideoSubmitMs = -1;
    private long _firstAudioSubmitMs = -1;
    private long _lastVideoSubmitMs;
    private long _lastAudioSubmitMs;
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

    public NDIAVSink(
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
        bool enableAudioDriftCorrection = false)
    {
        _sender = sender;
        Name = name ?? "NDIAVSink";

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
                _audioResampler = new LinearResampler();
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
                _audioDriftCorrector = new DriftCorrector(
                    targetDepth: Math.Max(1, _audioMaxPendingBuffers / 2),
                    ownerName: Name);
        }

        Log.LogInformation("Created NDIAVSink '{Name}': hasVideo={HasVideo}, hasAudio={HasAudio}, preset={Preset}",
            Name, _hasVideo, _hasAudio, preset);
        if (_hasVideo)
            Log.LogDebug("NDIAVSink '{Name}' video: {Width}x{Height} px={PixelFormat}, maxPending={MaxPending}",
                Name, _videoTargetFormat.Width, _videoTargetFormat.Height, _videoTargetFormat.PixelFormat, _videoMaxPendingFrames);
        if (_hasAudio)
            Log.LogDebug("NDIAVSink '{Name}' audio: {SampleRate}Hz/{Channels}ch, fpb={FramesPerBuffer}, maxPending={MaxPending}",
                Name, _audioTargetFormat.SampleRate, _audioTargetFormat.Channels, _audioFramesPerBuffer, _audioMaxPendingBuffers);
    }

    /// <summary>
    /// Creates an <see cref="NDIAVSink"/> using an <see cref="NDIAVSinkOptions"/> record.
    /// This is the preferred constructor.
    /// </summary>
    public NDIAVSink(NDISender sender, NDIAVSinkOptions? options) : this(
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
        options?.EnableAudioDriftCorrection ?? false)
    { }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _audioDriftCorrector?.Reset();

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

        Log.LogInformation("NDIAVSink '{Name}' started: videoThread={HasVideo}, audioThread={HasAudio}",
            Name, _hasVideo, _hasAudio);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1)
            return;

        Log.LogInformation("Stopping NDIAVSink '{Name}'", Name);
        _cts?.Cancel();

        await Task.Run(() => _videoThread?.Join(TimeSpan.FromSeconds(3)), ct).ConfigureAwait(false);
        await Task.Run(() => _audioThread?.Join(TimeSpan.FromSeconds(3)), ct).ConfigureAwait(false);
    }

    public void ReceiveFrame(in VideoFrame frame)
    {
        if (!_hasVideo || Volatile.Read(ref _started) == 0)
            return;

        int bytes = frame.Data.Length;
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

    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
        => ReceiveBufferCore(buffer, frameCount, sourceFormat, long.MinValue);

    /// <summary>
    /// PTS-aware overload — carries the stream-time PTS of the first sample through to
    /// the write loop so NDI timecodes for audio can share the video path's media-time
    /// domain.  Without this, both streams fall back to <c>TimecodeSynthesize</c>
    /// (wall-clock-at-submit), which bakes any decoder warm-up difference into a
    /// permanent A/V offset — typically leaving video ahead of audio because the
    /// video decoder produces frames sooner than the audio decoder.
    /// </summary>
    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat, TimeSpan sourcePts)
        => ReceiveBufferCore(buffer, frameCount, sourceFormat,
            sourcePts == TimeSpan.MinValue ? long.MinValue : sourcePts.Ticks);

    private void ReceiveBufferCore(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat, long sourcePtsTicks)
    {
        if (!_hasAudio || Volatile.Read(ref _started) == 0)
            return;

        int outCh = _audioTargetFormat.Channels;

        // Compute rate-adjusted + drift-corrected output frame count (§6.2).
        int writeFrames = SinkBufferHelper.ComputeWriteFrames(
            frameCount, sourceFormat.SampleRate, _audioTargetFormat.SampleRate,
            _audioDriftCorrector, _audioWork.Count);
        int writeSamples = writeFrames * outCh;

        if (!_audioPool.TryDequeue(out var dest))
        {
            Interlocked.Increment(ref _audioPoolMissDrops);
            return;
        }

        if (dest.Length < writeSamples)
        {
            _audioPool.Enqueue(dest);
            Interlocked.Increment(ref _audioCapacityMissDrops);
            return;
        }

        int writtenSamples;
        if (_audioResampler != null && sourceFormat.SampleRate != _audioTargetFormat.SampleRate)
        {
            // Cross-rate: resampler output sized for the drift-corrected frame count.
            int writtenFrames = _audioResampler.Resample(buffer, dest.AsSpan(0, writeSamples), sourceFormat, _audioTargetFormat.SampleRate);
            writtenSamples = Math.Clamp(writtenFrames, 0, writeFrames) * outCh;
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
            "Disposing NDIAVSink '{Name}': videoPassthrough={VideoPassthrough}, videoConverted={VideoConverted}, videoConversionDrops={VideoConversionDrops}, " +
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

        // Drain pending queues so pooled buffers are not leaked.
        _videoWork.Drain(pv => _videoPool.Enqueue(pv.Buffer));
        _audioWork.Drain(pa => _audioPool.Enqueue(pa.Buffer));

        _videoWork.Dispose();
        _audioWork.Dispose();
        _videoConverter.Dispose();
        if (_ownsAudioResampler) _audioResampler?.Dispose();
    }

    private static int VideoLineStride(PixelFormat fmt, int w) => fmt switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 => w * 4,
        PixelFormat.Uyvy422 => w * 2,
        PixelFormat.Nv12 or PixelFormat.Yuv420p => w,
        _ => w * 4,
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

    private static bool EnsureFfmpegLoaded()
    {
        // FFmpegLoader.EnsureLoaded is already idempotent with its own internal lock.
        // We catch failures here so the video write loop can fall back to a non-FFmpeg
        // conversion path instead of tearing down the sink.
        try
        {
            FFmpegLoader.EnsureLoaded();
            return true;
        }
        catch (Exception ex)
        {
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

            // Process bottom-to-top because in-place output (2 bytes/px-pair) is smaller
            // than source (4 bytes for 2×Y + U + V in 10-bit), avoiding overwrites.
            for (int row = height - 1; row >= 0; row--)
            {
                int yRowOff  = row * width;         // in ushort units
                int uvRowOff = row * (width >> 1);   // in ushort units
                byte* dstRow = pBuf + row * dstStride;

                for (int x = width - 2; x >= 0; x -= 2)
                {
                    int uvIdx = uvRowOff + (x >> 1);
                    byte* d = dstRow + x * 2;

                    // Narrow 10-bit to 8-bit: (v + 2) >> 2
                    d[0] = (byte)(((pU[uvIdx] & 0x03FF) + 2) >> 2);
                    d[1] = (byte)(((pY[yRowOff + x] & 0x03FF) + 2) >> 2);
                    d[2] = (byte)(((pV[uvIdx] & 0x03FF) + 2) >> 2);
                    d[3] = (byte)(((pY[yRowOff + x + 1] & 0x03FF) + 2) >> 2);
                }
            }
        }

        return true;
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

    private unsafe void VideoWriteLoop()
    {
        var token = _cts!.Token;
        int fpsNum = _videoTargetFormat.FrameRateNumerator > 0 ? _videoTargetFormat.FrameRateNumerator : 30000;
        int fpsDen = _videoTargetFormat.FrameRateDenominator > 0 ? _videoTargetFormat.FrameRateDenominator : 1001;
        SwsContext* ffmpegSws = null;

        // Pre-allocate scratch arrays for sws_scale to avoid 4 heap allocations per frame.
        var swsSrcData   = new byte*[4];
        var swsSrcStride = new int[4];
        var swsDstData   = new byte*[4];
        var swsDstStride = new int[4];

        while (!token.IsCancellationRequested)
        {
            if (!_videoWork.WaitForItem(token)) break;

            while (_videoWork.TryDequeue(out var pf))
            {

                try
                {
                    ReadOnlyMemory<byte> payload;
                    PixelFormat sendFormat;
                    IDisposable? tempOwner = null;
                    byte[]? scratchBuffer = null;

                    try
                    {
                        if (pf.PixelFormat == _videoTargetFormat.PixelFormat)
                        {
                            payload = pf.Buffer.AsMemory(0, pf.Bytes);
                            sendFormat = pf.PixelFormat;
                            Interlocked.Increment(ref _videoPassthroughFrames);
                        }
                        else if (pf.PixelFormat == PixelFormat.Yuv422p10 && _videoTargetFormat.PixelFormat == PixelFormat.Uyvy422)
                        {
                            if (!TryConvertI210ToUyvyInPlace(pf.Buffer, pf.Width, pf.Height, pf.Bytes, out int uyvyBytes))
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
                                    pf.Buffer.AsSpan(0, pf.Bytes),
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
                                    pf.Buffer.AsSpan(0, pf.Bytes),
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
                                pf.Buffer.AsMemory(0, pf.Bytes),
                                TimeSpan.FromTicks(pf.PtsTicks));

                            var converted = _videoConverter.Convert(srcFrame, _videoTargetFormat.PixelFormat);
                            payload = converted.Data;
                            sendFormat = converted.PixelFormat;
                            tempOwner = converted.MemoryOwner;
                            Interlocked.Increment(ref _videoConvertedFrames);
                        }

                        if (!MemoryMarshal.TryGetArray(payload, out var seg) || seg.Array == null)
                        {
                            Interlocked.Increment(ref _videoFormatDrops);
                            continue;
                        }

                        fixed (byte* p = &seg.Array[seg.Offset])
                        {
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

                            long videoPts = pf.PtsTicks;
                            long videoTimecode = videoPts >= 0 ? videoPts : NDIConstants.TimecodeSynthesize;
                            if (videoPts >= 0)
                                _timing.ObserveVideoPts(videoPts);

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
                                PData = (nint)p,
                                LineStrideInBytes = VideoLineStride(sendFormat, pf.Width),
                                PMetadata = nint.Zero,
                                Timestamp = NDIConstants.TimestampUndefined
                            };

                            lock (_videoSendLock)
                                _sender.SendVideo(vf);

                            // Diagnostics (post-send so the timestamp reflects when the frame
                            // actually left the sender, including any clockVideo rate-limiting).
                            long nowMs = _sinkClock.ElapsedMilliseconds;
                            Interlocked.CompareExchange(ref _firstVideoSubmitMs, nowMs, -1);
                            Interlocked.Exchange(ref _lastVideoSubmitMs, nowMs);
                            Interlocked.Increment(ref _videoFramesSubmitted);
                            Interlocked.Exchange(ref _lastVideoTimecodeTicks, videoTimecode);
                            Interlocked.Exchange(ref _lastVideoPtsTicks, videoPts);
                            Interlocked.Exchange(ref _atLastVideoAudioMs,
                                Interlocked.Read(ref _lastAudioSubmitMs));
                        }
                    }
                    catch (NotSupportedException)
                    {
                        Interlocked.Increment(ref _videoConversionDrops);
                        Interlocked.Increment(ref _videoFormatDrops);
                    }
                    finally
                    {
                        tempOwner?.Dispose();
                        if (scratchBuffer != null)
                            ArrayPool<byte>.Shared.Return(scratchBuffer);
                    }
                }

                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                        Log.LogError(ex, "NDI video send exception: {Message}", ex.Message);
                }
                finally
                {
                    _videoPool.Enqueue(pf.Buffer);
                }
            }
        }

        if (ffmpegSws != null)
            ffmpeg.sws_freeContext(ffmpegSws);
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
                        // Derive a media-time timecode in the same domain as the video
                        // path.  NDIAvTimingContext seeds from the first observed video
                        // PTS (or 0 if audio leads) and reserves a sample-accurate slot
                        // per buffer — this guarantees audio timecodes advance at exactly
                        // the decoded sample rate and never drift against video.
                        //
                        // If the producer supplied a buffer-level PTS (router's PTS-aware
                        // overload) and it deviates noticeably from the reserved cursor
                        // (e.g. after a seek or an upstream buffer starvation), snap the
                        // cursor to the PTS so we don't accumulate error silently.
                        long reserved = _timing.ReserveAudioTimecode(samplesPerChannel, _audioTargetFormat.SampleRate);
                        long audioTimecode = reserved;

                        if (pending.PtsTicks >= 0)
                        {
                            long deltaTicks = pending.PtsTicks - reserved;
                            long oneBufferTicks = (long)Math.Round(
                                (double)samplesPerChannel * TimeSpan.TicksPerSecond / _audioTargetFormat.SampleRate);
                            // Only realign on large jumps — normal sample-accurate drift
                            // stays well under one buffer.  Anything larger is an upstream
                            // discontinuity (seek / reset) we want to follow.
                            if (Math.Abs(deltaTicks) > oneBufferTicks * 4)
                                audioTimecode = pending.PtsTicks;
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

                        lock (_audioSendLock)
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
