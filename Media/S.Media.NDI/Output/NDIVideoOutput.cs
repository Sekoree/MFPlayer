using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using System.Diagnostics;

namespace S.Media.NDI.Output;

public sealed class NDIVideoOutput : IVideoOutput, IAudioSink
{
    private readonly Lock _gate = new();
    private bool _disposed;
    private bool _running;
    private NDISender? _sender;
    private byte[]? _stagingBuffer;
    private float[]? _audioStagingBuffer;
    private long _videoPushSuccesses;
    private long _videoPushFailures;
    private long _audioPushSuccesses;
    private long _audioPushFailures;
    private double _lastPushMs;

    public NDIVideoOutput(string outputName, NDIOutputOptions options)
    {
        if (string.IsNullOrWhiteSpace(outputName))
            throw new ArgumentException("Output name is required.", nameof(outputName));

        OutputName = outputName;
        Options = options;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

    /// <summary>Video output state. Use <c>((IAudioSink)this).State</c> for <see cref="AudioOutputState"/>.</summary>
    public VideoOutputState State => _running ? VideoOutputState.Running : VideoOutputState.Stopped;

    // IAudioSink.State — same lifecycle, different enum.
    AudioOutputState IAudioSink.State => _running ? AudioOutputState.Running : AudioOutputState.Stopped;

    public string OutputName { get; }

    public NDIOutputOptions Options { get; }

    public NDIVideoOutputDebugInfo Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new NDIVideoOutputDebugInfo(
                    VideoPushSuccesses: _videoPushSuccesses,
                    VideoPushFailures: _videoPushFailures,
                    AudioPushSuccesses: _audioPushSuccesses,
                    AudioPushFailures: _audioPushFailures,
                    LastPushMs: _lastPushMs);
            }
        }
    }

    // ── IVideoOutput.Start ────────────────────────────────────────────────────

    public int Start(VideoOutputConfig config)
    {
        NDISender? oldSender = null;

        lock (_gate)
        {
            if (_disposed)
                return (int)MediaErrorCode.NDIOutputPushVideoFailed;

            var outputValidate = Options.Validate();
            if (outputValidate != MediaResult.Success)
                return outputValidate;

            var configValidate = config.Validate(hasEffectiveFrameDuration: false);
            if (configValidate != MediaResult.Success)
                return configValidate;

            oldSender = _sender;
            _sender = null;
        }

        // Dispose any previous sender outside the lock.
        oldSender?.Dispose();

        var createErr = NDISender.Create(out var newSender,
            senderName: OutputName,
            clockVideo: Options.ClockVideo,
            clockAudio: Options.ClockAudio);
        if (createErr != 0 || newSender == null)
            return (int)MediaErrorCode.NDISenderCreateFailed;

        lock (_gate)
        {
            if (_disposed)
            {
                newSender.Dispose();
                return (int)MediaErrorCode.NDIOutputPushVideoFailed;
            }

            _sender = newSender;
            _running = true;
            return MediaResult.Success;
        }
    }

    // ── IAudioSink.Start ──────────────────────────────────────────────────────

    /// <summary>
    /// Starts this output as an audio sink. If the NDI sender is already running (started
    /// via <see cref="Start(VideoOutputConfig)"/>), returns <see cref="MediaResult.Success"/>
    /// immediately. Otherwise creates the sender with a default video config.
    /// </summary>
    int IAudioSink.Start(AudioOutputConfig config)
    {
        lock (_gate)
        {
            if (_disposed)
                return (int)MediaErrorCode.MediaObjectDisposed;
            if (_running)
                return MediaResult.Success;
        }

        // Start sender with a default video config — NDI senders are A/V combined.
        return Start(new VideoOutputConfig());
    }

    // ── Stop (serves both IVideoOutput.Stop and IAudioSink.Stop) ─────────────

    public int Stop()
    {
        NDISender? senderToDispose;

        lock (_gate)
        {
            if (_disposed)
                return MediaResult.Success;

            _running = false;
            senderToDispose = _sender;
            _sender = null;
        }

        senderToDispose?.Dispose();
        return MediaResult.Success;
    }

    // ── IVideoOutput.PushFrame ────────────────────────────────────────────────
    // P1.9: capture sender ref under lock, release lock before native SendVideo call.

    public int PushFrame(VideoFrame frame) => PushFrame(frame, frame.PresentationTime);

    public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
        var started = Stopwatch.GetTimestamp();
        var frameValidation = frame.ValidateForPush();

        NDISender? sender;
        lock (_gate)
        {
            if (_disposed || frameValidation != MediaResult.Success || !_running || _sender is null)
            {
                Interlocked.Increment(ref _videoPushFailures);
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return _disposed ? (int)MediaErrorCode.MediaObjectDisposed
                    : frameValidation != MediaResult.Success ? frameValidation
                    : (int)MediaErrorCode.NDIOutputPushVideoFailed;
            }
            sender = _sender;
        }

        // Native SendVideo call happens outside the lock (P1.9).
        var result = PushFrameCore(frame, presentationTime, sender);

        if (result == MediaResult.Success)
            Interlocked.Increment(ref _videoPushSuccesses);
        else
            Interlocked.Increment(ref _videoPushFailures);
        _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    // ── IAudioSink.PushFrame ──────────────────────────────────────────────────

    int IAudioSink.PushFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap)
        => PushAudioInternal(in frame, routeMap, frame.SourceChannelCount);

    int IAudioSink.PushFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap, int sourceChannelCount)
        => PushAudioInternal(in frame, routeMap, sourceChannelCount);

    // ── Legacy PushAudio (kept for source compatibility, redirects to IAudioSink) ──

    /// <summary>
    /// Pushes an audio frame to the NDI stream.
    /// </summary>
    /// <remarks>
    /// Prefer casting to <see cref="IAudioSink"/> and calling <c>PushFrame</c>,
    /// which supports explicit channel routing.
    /// </remarks>
    public int PushAudio(in AudioFrame frame, TimeSpan presentationTime)
    {
        var ch = frame.SourceChannelCount;
        if (ch <= 0) return PushAudioInternal(in frame, ReadOnlySpan<int>.Empty, 0);
        Span<int> identity = stackalloc int[ch];
        for (var i = 0; i < ch; i++) identity[i] = i;
        return PushAudioInternal(in frame, identity, ch);
    }

    // ── Internal audio push ────────────────────────────────────────────────────

    private int PushAudioInternal(in AudioFrame frame, ReadOnlySpan<int> routeMap, int sourceChannelCount)
    {
        var started = Stopwatch.GetTimestamp();

        NDISender? sender;
        lock (_gate)
        {
            if (_disposed || !_running || _sender is null)
            {
                Interlocked.Increment(ref _audioPushFailures);
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return _disposed ? (int)MediaErrorCode.MediaObjectDisposed
                    : (int)MediaErrorCode.NDIOutputPushAudioFailed;
            }

            if (!Options.EnableAudio)
            {
                Interlocked.Increment(ref _audioPushFailures);
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
            }

            sender = _sender;
        }

        // Native SendAudio call happens outside the lock (P1.9).
        var result = PushAudioCore(in frame, frame.PresentationTime, routeMap, sourceChannelCount, sender);

        if (result == MediaResult.Success)
            Interlocked.Increment(ref _audioPushSuccesses);
        else
            Interlocked.Increment(ref _audioPushFailures);
        _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    private unsafe int PushAudioCore(in AudioFrame frame, TimeSpan presentationTime,
        ReadOnlySpan<int> routeMap, int sourceChannelCount, NDISender sender)
    {
        if (frame.FrameCount <= 0 || frame.SourceChannelCount <= 0 || frame.SampleRate <= 0)
            return (int)MediaErrorCode.NDIOutputPushAudioFailed;

        var srcChannels = Math.Max(1, sourceChannelCount > 0 ? sourceChannelCount : frame.SourceChannelCount);
        var outputChannels = routeMap.IsEmpty ? frame.SourceChannelCount : routeMap.Length;
        var samplesPerChannel = frame.FrameCount;
        var totalFloats = outputChannels * samplesPerChannel;

        EnsureAudioStagingBuffer(totalFloats);

        var src = frame.Samples.Span;

        if (routeMap.IsEmpty)
        {
            // Identity path — no remapping needed.
            if (frame.Layout == AudioFrameLayout.Planar)
            {
                src.Slice(0, totalFloats).CopyTo(_audioStagingBuffer.AsSpan(0, totalFloats));
            }
            else
            {
                // Interleaved → planar
                for (var ch = 0; ch < outputChannels; ch++)
                    for (var s = 0; s < samplesPerChannel; s++)
                        _audioStagingBuffer![ch * samplesPerChannel + s] = src[s * outputChannels + ch];
            }
        }
        else
        {
            // Route-map path: routeMap[outputCh] = sourceCh.
            if (frame.Layout == AudioFrameLayout.Planar)
            {
                for (var outCh = 0; outCh < outputChannels; outCh++)
                {
                    var srcCh = Math.Clamp(routeMap[outCh], 0, srcChannels - 1);
                    for (var s = 0; s < samplesPerChannel; s++)
                        _audioStagingBuffer![outCh * samplesPerChannel + s] = src[srcCh * samplesPerChannel + s];
                }
            }
            else
            {
                // Interleaved → planar with route map
                for (var outCh = 0; outCh < outputChannels; outCh++)
                {
                    var srcCh = Math.Clamp(routeMap[outCh], 0, srcChannels - 1);
                    for (var s = 0; s < samplesPerChannel; s++)
                        _audioStagingBuffer![outCh * samplesPerChannel + s] = src[s * srcChannels + srcCh];
                }
            }
        }

        fixed (float* ptr = _audioStagingBuffer!)
        {
            var ndiFrame = new NdiAudioFrameV3
            {
                SampleRate = frame.SampleRate,
                NoChannels = outputChannels,
                NoSamples = samplesPerChannel,
                Timecode = presentationTime.Ticks,
                FourCC = NdiFourCCAudioType.Fltp,
                PData = (nint)ptr,
                ChannelStrideInBytes = samplesPerChannel * sizeof(float),
                PMetadata = nint.Zero,
                Timestamp = presentationTime.Ticks,
            };
            sender.SendAudio(ndiFrame);
        }

        return MediaResult.Success;
    }

    private void EnsureAudioStagingBuffer(int requiredFloats)
    {
        if (_audioStagingBuffer is null || _audioStagingBuffer.Length < requiredFloats)
            _audioStagingBuffer = new float[requiredFloats];
    }

    public void Dispose()
    {
        NDISender? senderToDispose;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _running = false;
            senderToDispose = _sender;
            _sender = null;
        }

        senderToDispose?.Dispose();
    }

    private unsafe int PushFrameCore(VideoFrame frame, TimeSpan presentationTime, NDISender sender)
    {
        var timecode = presentationTime.Ticks;
        var frameRateN = Options.FrameRateN;
        var frameRateD = Options.FrameRateD;
        var aspectRatio = frame.Width > 0 && frame.Height > 0
            ? (float)frame.Width / frame.Height
            : 16f / 9f;

        switch (frame.PixelFormat)
        {
            case VideoPixelFormat.Bgra32:
            {
                using var pin = frame.Plane0.Pin();
                var ndiFrame = new NdiVideoFrameV2
                {
                    Xres = frame.Width,
                    Yres = frame.Height,
                    FourCC = NdiFourCCVideoType.Bgra,
                    FrameRateN = frameRateN,
                    FrameRateD = frameRateD,
                    PictureAspectRatio = aspectRatio,
                    FrameFormatType = NdiFrameFormatType.Progressive,
                    Timecode = timecode,
                    PData = (nint)pin.Pointer,
                    LineStrideInBytes = frame.Plane0Stride,
                    PMetadata = nint.Zero,
                    Timestamp = timecode,
                };
                sender.SendVideo(ndiFrame);
                return MediaResult.Success;
            }

            case VideoPixelFormat.Rgba32:
            {
                using var pin = frame.Plane0.Pin();
                var ndiFrame = new NdiVideoFrameV2
                {
                    Xres = frame.Width,
                    Yres = frame.Height,
                    FourCC = NdiFourCCVideoType.Rgba,
                    FrameRateN = frameRateN,
                    FrameRateD = frameRateD,
                    PictureAspectRatio = aspectRatio,
                    FrameFormatType = NdiFrameFormatType.Progressive,
                    Timecode = timecode,
                    PData = (nint)pin.Pointer,
                    LineStrideInBytes = frame.Plane0Stride,
                    PMetadata = nint.Zero,
                    Timestamp = timecode,
                };
                sender.SendVideo(ndiFrame);
                return MediaResult.Success;
            }

            case VideoPixelFormat.Nv12:
            {
                var yStride = frame.Plane0Stride;
                var uvStride = frame.Plane1Stride;
                var ySize = yStride * frame.Height;
                var uvSize = uvStride * ((frame.Height + 1) / 2);
                EnsureStagingBuffer(ySize + uvSize);

                frame.Plane0.Span.CopyTo(_stagingBuffer.AsSpan(0, ySize));
                frame.Plane1.Span.CopyTo(_stagingBuffer.AsSpan(ySize, uvSize));

                fixed (byte* ptr = _stagingBuffer)
                {
                    var ndiFrame = new NdiVideoFrameV2
                    {
                        Xres = frame.Width,
                        Yres = frame.Height,
                        FourCC = NdiFourCCVideoType.Nv12,
                        FrameRateN = frameRateN,
                        FrameRateD = frameRateD,
                        PictureAspectRatio = aspectRatio,
                        FrameFormatType = NdiFrameFormatType.Progressive,
                        Timecode = timecode,
                        PData = (nint)ptr,
                        LineStrideInBytes = yStride,
                        PMetadata = nint.Zero,
                        Timestamp = timecode,
                    };
                    sender.SendVideo(ndiFrame);
                }
                return MediaResult.Success;
            }

            case VideoPixelFormat.Yuv420P:
            {
                var yStride = frame.Plane0Stride;
                var uStride = frame.Plane1Stride;
                var vStride = frame.Plane2Stride;
                var chromaHeight = (frame.Height + 1) / 2;
                var ySize = yStride * frame.Height;
                var uSize = uStride * chromaHeight;
                var vSize = vStride * chromaHeight;
                EnsureStagingBuffer(ySize + uSize + vSize);

                frame.Plane0.Span.CopyTo(_stagingBuffer.AsSpan(0, ySize));
                frame.Plane1.Span.CopyTo(_stagingBuffer.AsSpan(ySize, uSize));
                frame.Plane2.Span.CopyTo(_stagingBuffer.AsSpan(ySize + uSize, vSize));

                fixed (byte* ptr = _stagingBuffer)
                {
                    var ndiFrame = new NdiVideoFrameV2
                    {
                        Xres = frame.Width,
                        Yres = frame.Height,
                        FourCC = NdiFourCCVideoType.I420,
                        FrameRateN = frameRateN,
                        FrameRateD = frameRateD,
                        PictureAspectRatio = aspectRatio,
                        FrameFormatType = NdiFrameFormatType.Progressive,
                        Timecode = timecode,
                        PData = (nint)ptr,
                        LineStrideInBytes = yStride,
                        PMetadata = nint.Zero,
                        Timestamp = timecode,
                    };
                    sender.SendVideo(ndiFrame);
                }
                return MediaResult.Success;
            }

            default:
                return (int)MediaErrorCode.NDIOutputUnsupportedPixelFormat;
        }
    }

    private void EnsureStagingBuffer(int requiredSize)
    {
        if (_stagingBuffer is null || _stagingBuffer.Length < requiredSize)
            _stagingBuffer = new byte[requiredSize];
    }
}
