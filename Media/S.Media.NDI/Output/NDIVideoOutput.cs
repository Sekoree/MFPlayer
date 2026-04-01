using System.Buffers;
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
    // Staging buffers are rented from ArrayPool and returned in Dispose (Issue 2.4).
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
        // NDI frame pacing is governed by NDIOutputOptions.ClockVideo / ClockAudio set at
        // construction time. VideoOutputConfig backpressure / presentation settings are not
        // applicable to a network output and are intentionally ignored (Issue 2.3).
        _ = config;

        NDISender? oldSender = null;

        lock (_gate)
        {
            if (_disposed)
                return (int)MediaErrorCode.MediaObjectDisposed;   // Issue 5.2: was NDIOutputPushVideoFailed

            // P2.10: validate EnableVideo at Start() rather than on every PushFrame.
            if (!Options.EnableVideo)
                return (int)MediaErrorCode.NDIInvalidOutputOptions;

            var outputValidate = Options.Validate();
            if (outputValidate != MediaResult.Success)
                return outputValidate;

            oldSender = _sender;
            _sender = null;
        }

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
                return (int)MediaErrorCode.MediaObjectDisposed;
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
            // Issue 5.3: reject early if audio is not enabled — do not silently accept then fail on every push.
            if (!Options.EnableAudio)
                return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
            if (_running)
                return MediaResult.Success;
        }

        return Start(new VideoOutputConfig());
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

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
                var failMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _lastPushMs = failMs;
                Interlocked.Increment(ref _videoPushFailures);
                return _disposed ? (int)MediaErrorCode.MediaObjectDisposed
                    : frameValidation != MediaResult.Success ? frameValidation
                    : (int)MediaErrorCode.NDIOutputPushVideoFailed;
            }

            // Issue 5.13: honour EnableVideo flag.
            if (!Options.EnableVideo)
            {
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Interlocked.Increment(ref _videoPushFailures);
                return (int)MediaErrorCode.NDIInvalidOutputOptions;
            }

            sender = _sender;
        }

        var result = PushFrameCore(frame, presentationTime, sender);

        lock (_gate)
        {
            if (result == MediaResult.Success) _videoPushSuccesses++;
            else _videoPushFailures++;
            _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;   // Issue 5.19
        }
        return result;
    }

    // ── IAudioSink.PushFrame ──────────────────────────────────────────────────

    int IAudioSink.PushFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap)
        => PushAudioInternal(in frame, routeMap, frame.SourceChannelCount);

    int IAudioSink.PushFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap, int sourceChannelCount)
        => PushAudioInternal(in frame, routeMap, sourceChannelCount);

    // Issue 1.2: public PushAudio removed — use ((IAudioSink)this).PushFrame(...) instead.

    // ── Tally & connection count (Issues 5.14, 5.15) ─────────────────────────

    /// <summary>
    /// Returns the current on-program / on-preview tally state reported by connected receivers.
    /// Returns <see langword="false"/> if the sender is not running.
    /// </summary>
    public bool GetTally(out bool onProgram, out bool onPreview)
    {
        NDISender? sender;
        lock (_gate) { sender = _sender; }
        if (sender is null) { onProgram = false; onPreview = false; return false; }
        var ok = sender.GetTally(out var tally);
        onProgram = tally.OnProgram != 0;
        onPreview = tally.OnPreview != 0;
        return ok;
    }

    /// <summary>
    /// Returns the number of NDI receivers currently connected to this sender.
    /// Specify a non-zero <paramref name="timeoutMs"/> to wait until at least one receiver connects.
    /// Returns 0 if the sender is not running.
    /// </summary>
    public int GetConnectionCount(uint timeoutMs = 0)
    {
        NDISender? sender;
        lock (_gate) { sender = _sender; }
        return sender?.GetConnectionCount(timeoutMs) ?? 0;
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
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Interlocked.Increment(ref _audioPushFailures);
                return _disposed ? (int)MediaErrorCode.MediaObjectDisposed
                    : (int)MediaErrorCode.NDIOutputPushAudioFailed;
            }

            if (!Options.EnableAudio)
            {
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Interlocked.Increment(ref _audioPushFailures);
                return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
            }

            sender = _sender;
        }

        var result = PushAudioCore(in frame, frame.PresentationTime, routeMap, sourceChannelCount, sender);

        lock (_gate)
        {
            if (result == MediaResult.Success) _audioPushSuccesses++;
            else _audioPushFailures++;
            _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;   // Issue 5.19
        }
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

        var audioBuffer = RentAudioStagingBuffer(totalFloats);
        var src = frame.Samples.Span;

        if (routeMap.IsEmpty)
        {
            if (frame.Layout == AudioFrameLayout.Planar)
                src.Slice(0, totalFloats).CopyTo(audioBuffer.AsSpan(0, totalFloats));
            else
                for (var ch = 0; ch < outputChannels; ch++)
                    for (var s = 0; s < samplesPerChannel; s++)
                        audioBuffer[ch * samplesPerChannel + s] = src[s * outputChannels + ch];
        }
        else
        {
            if (frame.Layout == AudioFrameLayout.Planar)
            {
                for (var outCh = 0; outCh < outputChannels; outCh++)
                {
                    var srcCh = Math.Clamp(routeMap[outCh], 0, srcChannels - 1);
                    for (var s = 0; s < samplesPerChannel; s++)
                        audioBuffer[outCh * samplesPerChannel + s] = src[srcCh * samplesPerChannel + s];
                }
            }
            else
            {
                for (var outCh = 0; outCh < outputChannels; outCh++)
                {
                    var srcCh = Math.Clamp(routeMap[outCh], 0, srcChannels - 1);
                    for (var s = 0; s < samplesPerChannel; s++)
                        audioBuffer[outCh * samplesPerChannel + s] = src[s * srcChannels + srcCh];
                }
            }
        }

        fixed (float* ptr = audioBuffer)
        {
            var ndiFrame = new NdiAudioFrameV3
            {
                SampleRate = frame.SampleRate,
                NoChannels = outputChannels,
                NoSamples = samplesPerChannel,
                Timecode = NdiConstants.TimecodeSynthesize,   // Issue 5.17: let SDK generate SMPTE TC
                FourCC = NdiFourCCAudioType.Fltp,
                PData = (nint)ptr,
                ChannelStrideInBytes = samplesPerChannel * sizeof(float),
                PMetadata = nint.Zero,
                Timestamp = presentationTime.Ticks,            // relative PTS
            };
            sender.SendAudio(ndiFrame);
        }

        return MediaResult.Success;
    }

    // Issue 2.4: buffers rented from ArrayPool; returned in Dispose.
    private float[] RentAudioStagingBuffer(int requiredFloats)
    {
        if (_audioStagingBuffer is null || _audioStagingBuffer.Length < requiredFloats)
        {
            if (_audioStagingBuffer is not null)
                ArrayPool<float>.Shared.Return(_audioStagingBuffer);
            _audioStagingBuffer = ArrayPool<float>.Shared.Rent(requiredFloats);
        }
        return _audioStagingBuffer;
    }

    private byte[] RentStagingBuffer(int requiredSize)
    {
        if (_stagingBuffer is null || _stagingBuffer.Length < requiredSize)
        {
            if (_stagingBuffer is not null)
                ArrayPool<byte>.Shared.Return(_stagingBuffer);
            _stagingBuffer = ArrayPool<byte>.Shared.Rent(requiredSize);
        }
        return _stagingBuffer;
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

        // Return ArrayPool buffers (Issue 2.4).
        if (_stagingBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_stagingBuffer);
            _stagingBuffer = null;
        }
        if (_audioStagingBuffer is not null)
        {
            ArrayPool<float>.Shared.Return(_audioStagingBuffer);
            _audioStagingBuffer = null;
        }
    }

    private unsafe int PushFrameCore(VideoFrame frame, TimeSpan presentationTime, NDISender sender)
    {
        var timecode = NdiConstants.TimecodeSynthesize;   // Issue 5.17: let SDK generate SMPTE TC
        var timestamp = presentationTime.Ticks;            // relative PTS
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
                    Xres = frame.Width, Yres = frame.Height,
                    FourCC = NdiFourCCVideoType.Bgra,
                    FrameRateN = frameRateN, FrameRateD = frameRateD,
                    PictureAspectRatio = aspectRatio,
                    FrameFormatType = NdiFrameFormatType.Progressive,
                    Timecode = timecode, PData = (nint)pin.Pointer,
                    LineStrideInBytes = frame.Plane0Stride,
                    PMetadata = nint.Zero, Timestamp = timestamp,
                };
                sender.SendVideo(ndiFrame);
                return MediaResult.Success;
            }

            case VideoPixelFormat.Rgba32:
            {
                using var pin = frame.Plane0.Pin();
                var ndiFrame = new NdiVideoFrameV2
                {
                    Xres = frame.Width, Yres = frame.Height,
                    FourCC = NdiFourCCVideoType.Rgba,
                    FrameRateN = frameRateN, FrameRateD = frameRateD,
                    PictureAspectRatio = aspectRatio,
                    FrameFormatType = NdiFrameFormatType.Progressive,
                    Timecode = timecode, PData = (nint)pin.Pointer,
                    LineStrideInBytes = frame.Plane0Stride,
                    PMetadata = nint.Zero, Timestamp = timestamp,
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
                var buf = RentStagingBuffer(ySize + uvSize);

                frame.Plane0.Span.CopyTo(buf.AsSpan(0, ySize));
                frame.Plane1.Span.CopyTo(buf.AsSpan(ySize, uvSize));

                fixed (byte* ptr = buf)
                {
                    var ndiFrame = new NdiVideoFrameV2
                    {
                        Xres = frame.Width, Yres = frame.Height,
                        FourCC = NdiFourCCVideoType.Nv12,
                        FrameRateN = frameRateN, FrameRateD = frameRateD,
                        PictureAspectRatio = aspectRatio,
                        FrameFormatType = NdiFrameFormatType.Progressive,
                        Timecode = timecode, PData = (nint)ptr,
                        LineStrideInBytes = yStride,
                        PMetadata = nint.Zero, Timestamp = timestamp,
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
                var buf = RentStagingBuffer(ySize + uSize + vSize);

                frame.Plane0.Span.CopyTo(buf.AsSpan(0, ySize));
                frame.Plane1.Span.CopyTo(buf.AsSpan(ySize, uSize));
                frame.Plane2.Span.CopyTo(buf.AsSpan(ySize + uSize, vSize));

                fixed (byte* ptr = buf)
                {
                    var ndiFrame = new NdiVideoFrameV2
                    {
                        Xres = frame.Width, Yres = frame.Height,
                        FourCC = NdiFourCCVideoType.I420,
                        FrameRateN = frameRateN, FrameRateD = frameRateD,
                        PictureAspectRatio = aspectRatio,
                        FrameFormatType = NdiFrameFormatType.Progressive,
                        Timecode = timecode, PData = (nint)ptr,
                        LineStrideInBytes = yStride,
                        PMetadata = nint.Zero, Timestamp = timestamp,
                    };
                    sender.SendVideo(ndiFrame);
                }
                return MediaResult.Success;
            }

            default:
                return (int)MediaErrorCode.NDIOutputUnsupportedPixelFormat;
        }
    }
}
