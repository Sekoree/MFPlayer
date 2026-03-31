using System.Buffers;
using NDILib;
using S.Media.Core.Video;

namespace S.Media.NDI.Input;

/// <summary>
/// An <see cref="INDICaptureCoordinator"/> backed by the NDI SDK's built-in
/// <see cref="NDIFrameSync"/> time-base corrector.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="NDICaptureCoordinator"/>, this implementation delegates clock
/// management entirely to the NDI SDK.  The SDK maintains an internal receive thread for
/// the attached <see cref="NDIReceiver"/> and applies hysteresis-based TBC so that:
/// <list type="bullet">
///   <item>Video frames are delivered without jitter even when sender and receiver clocks differ.</item>
///   <item>Audio is dynamically resampled to keep A/V sync regardless of network timing variance.</item>
/// </list>
/// </para>
/// <para>
/// <b>Usage rule:</b> once a frame-sync is created on a receiver, <em>only</em> the frame-sync
/// should be used to pull video/audio from that receiver.  Do not call
/// <c>NDIReceiver.CaptureScoped</c> while this coordinator is alive.
/// </para>
/// </remarks>
internal sealed class NDIFrameSyncCoordinator : INDICaptureCoordinator
{
    /// <summary>
    /// Maximum audio samples pulled in a single <see cref="TryReadAudio"/> call.
    /// Keeps individual allocations small while still draining the SDK queue quickly.
    /// </summary>
    private const int MaxBlockSamples = 2048;

    private readonly NDIFrameSync _frameSync;

    private NDIFrameSyncCoordinator(NDIFrameSync frameSync)
        => _frameSync = frameSync;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates an <see cref="NDIFrameSyncCoordinator"/> backed by <paramref name="receiver"/>.
    /// </summary>
    /// <param name="coordinator">On success, the created coordinator. <see langword="null"/> on failure.</param>
    /// <param name="receiver">The NDI receiver to attach the frame-sync to.</param>
    /// <returns><c>0</c> on success; the NDILib error code on failure.</returns>
    public static int Create(out NDIFrameSyncCoordinator? coordinator, NDIReceiver receiver)
    {
        var result = NDIFrameSync.Create(out var frameSync, receiver);
        if (result != 0 || frameSync is null)
        {
            coordinator = null;
            return result;
        }

        coordinator = new NDIFrameSyncCoordinator(frameSync);
        return 0;
    }

    // ------------------------------------------------------------------
    // INDICaptureCoordinator — video
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <c>NDIlib_framesync_capture_video</c> which returns immediately.
    /// Returns <see langword="false"/> (no frame available) when the frame-sync has not yet
    /// received any video from the sender (<c>Xres == 0</c>).
    /// </remarks>
    public bool TryReadVideo(uint timeoutMs, out CapturedVideoFrame frame)
    {
        _frameSync.CaptureVideo(out var ndiFrame, NdiFrameFormatType.Progressive);

        // Xres == 0 means no frame has been received yet; free the empty struct and signal unavailable.
        if (ndiFrame.Xres <= 0 || ndiFrame.Yres <= 0 || ndiFrame.PData == nint.Zero)
        {
            if (ndiFrame.PData != nint.Zero) _frameSync.FreeVideo(ndiFrame);
            frame = default;
            return false;
        }

        var width = ndiFrame.Xres;
        var height = ndiFrame.Yres;
        var stride = ndiFrame.LineStrideInBytes > 0 ? ndiFrame.LineStrideInBytes : width * 4;
        var fourCC = ndiFrame.FourCC;
        var timestamp = ndiFrame.Timestamp;
        var validLength = checked(width * height * 4);

        var rented = NDIVideoPixelConverter.RentFrameBuffer(width, height);

        if (!NDIVideoPixelConverter.TryCopyPacked32(ndiFrame.PData, stride, fourCC, width, height,
                rented, validLength, out var outputFormat, out var conversionPath))
        {
            ArrayPool<byte>.Shared.Return(rented);
            _frameSync.FreeVideo(ndiFrame);
            frame = default;
            return false;
        }

        // Data is now in `rented`; native buffer can be freed.
        _frameSync.FreeVideo(ndiFrame);

        frame = new CapturedVideoFrame(
            Rgba: rented,
            ValidLength: validLength,
            Width: width,
            Height: height,
            Timestamp100Ns: timestamp,
            IncomingPixelFormat: fourCC.ToString(),
            OutputPixelFormat: outputFormat,
            ConversionPath: conversionPath,
            IsPooled: true);

        return true;
    }

    // ------------------------------------------------------------------
    // INDICaptureCoordinator — audio
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// Probes the current incoming audio format, then pulls at most <c>2048</c> samples at the
    /// native sample rate and channel count.  Returns <see langword="false"/> when no signal has
    /// arrived yet or the SDK queue is empty.  Audio channel and sample-rate conversion is left
    /// to the caller (<see cref="NDIAudioSource"/> handles both via its ring buffer).
    /// </remarks>
    public unsafe bool TryReadAudio(uint timeoutMs, out CapturedAudioBlock frame)
    {
        // Step 1: Probe current incoming format without consuming samples.
        // Passing (0, 0, 0) asks the framesync for format metadata only (no samples allocated).
        _frameSync.CaptureAudio(out var probe, 0, 0, 0);
        var nativeSampleRate = probe.SampleRate;
        var nativeChannels   = probe.NoChannels;
        // PData is null for a zero-sample probe — only call FreeAudio if memory was allocated.
        if (probe.PData != nint.Zero) _frameSync.FreeAudio(probe);

        if (nativeSampleRate == 0 || nativeChannels == 0)
        {
            frame = default;
            return false; // No signal yet.
        }

        // Step 2: Check how many samples are available. Return early when the SDK queue
        // is empty so we don't fill the caller's ring buffer with silence packets.
        var depth = _frameSync.AudioQueueDepth();
        if (depth <= 0)
        {
            frame = default;
            return false;
        }

        // Step 3: Pull at most MaxBlockSamples at the native rate/channels.
        // The framesync resamples internally so any requested count is valid.
        var samples = Math.Min(depth, MaxBlockSamples);
        _frameSync.CaptureAudio(out var audioFrame, nativeSampleRate, nativeChannels, samples);

        if (audioFrame.PData == nint.Zero || audioFrame.NoSamples == 0)
        {
            if (audioFrame.PData != nint.Zero) _frameSync.FreeAudio(audioFrame);
            frame = default;
            return false;
        }

        // Step 4: De-interleave planar FLTP → interleaved float[] (same layout as NDICaptureCoordinator).
        var noSamples     = audioFrame.NoSamples;
        var noChannels    = audioFrame.NoChannels;
        var channelStride = audioFrame.ChannelStrideInBytes > 0
            ? audioFrame.ChannelStrideInBytes
            : noSamples * sizeof(float);
        var timestamp  = audioFrame.Timestamp;
        var sampleCount = checked(noSamples * noChannels);
        var rented      = ArrayPool<float>.Shared.Rent(sampleCount);

        var basePtr = (byte*)audioFrame.PData;
        for (var s = 0; s < noSamples; s++)
            for (var c = 0; c < noChannels; c++)
            {
                var channelPtr = (float*)(basePtr + (c * channelStride));
                rented[(s * noChannels) + c] = channelPtr[s];
            }

        // Data is now in `rented`; native buffer can be freed.
        _frameSync.FreeAudio(audioFrame);

        frame = new CapturedAudioBlock(
            InterleavedSamples: rented,
            SampleCount: sampleCount,
            Frames: noSamples,
            Channels: noChannels,
            SampleRate: nativeSampleRate,
            Timestamp100Ns: timestamp,
            IsPooled: true);

        return true;
    }

    // ------------------------------------------------------------------
    // IDisposable
    // ------------------------------------------------------------------

    public void Dispose() => _frameSync.Dispose();
}

