using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using System.Diagnostics;

namespace S.Media.NDI.Output;

public sealed class NDIVideoOutput : IVideoOutput
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

    public VideoOutputState State => _running ? VideoOutputState.Running : VideoOutputState.Stopped;

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

        NDISender? newSender;
        var createErr = NDISender.Create(out newSender,
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

    public int Start() => Start(new VideoOutputConfig());

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

    public int PushFrame(VideoFrame frame) => PushFrame(frame, frame.PresentationTime);

    public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
        var started = Stopwatch.GetTimestamp();
        var frameValidation = frame.ValidateForPush();

        lock (_gate)
        {
            if (_disposed)
            {
                _videoPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputPushVideoFailed;
            }

            if (frameValidation != MediaResult.Success)
            {
                _videoPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return frameValidation;
            }

            if (!_running || _sender is null)
            {
                _videoPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputPushVideoFailed;
            }

            var result = PushFrameCore(frame, presentationTime);

            if (result == MediaResult.Success)
                _videoPushSuccesses++;
            else
                _videoPushFailures++;

            _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return result;
        }
    }

    public int PushAudio(in AudioFrame frame, TimeSpan presentationTime)
    {
        var started = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (_disposed)
            {
                _audioPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputPushAudioFailed;
            }

            if (!Options.EnableAudio)
            {
                _audioPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
            }

            if (!_running || _sender is null)
            {
                _audioPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputPushAudioFailed;
            }

            var result = PushAudioCore(frame, presentationTime);

            if (result == MediaResult.Success)
                _audioPushSuccesses++;
            else
                _audioPushFailures++;

            _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return result;
        }
    }

    private unsafe int PushAudioCore(in AudioFrame frame, TimeSpan presentationTime)
    {
        if (frame.FrameCount <= 0 || frame.SourceChannelCount <= 0 || frame.SampleRate <= 0)
            return (int)MediaErrorCode.NDIOutputPushAudioFailed;

        var channels = frame.SourceChannelCount;
        var samplesPerChannel = frame.FrameCount;
        var totalFloats = channels * samplesPerChannel;

        EnsureAudioStagingBuffer(totalFloats);

        var src = frame.Samples.Span;

        if (frame.Layout == AudioFrameLayout.Planar)
        {
            // Already planar: [ch0_s0..ch0_sN, ch1_s0..ch1_sN, ...]
            src.Slice(0, totalFloats).CopyTo(_audioStagingBuffer.AsSpan(0, totalFloats));
        }
        else
        {
            // Interleaved → deinterleave: [ch0_s0, ch1_s0, ..., ch0_s1, ch1_s1, ...] → planar
            for (var ch = 0; ch < channels; ch++)
            {
                for (var s = 0; s < samplesPerChannel; s++)
                {
                    _audioStagingBuffer![ch * samplesPerChannel + s] = src[s * channels + ch];
                }
            }
        }

        fixed (float* ptr = _audioStagingBuffer!)
        {
            var ndiFrame = new NdiAudioFrameV3
            {
                SampleRate = frame.SampleRate,
                NoChannels = channels,
                NoSamples = samplesPerChannel,
                Timecode = presentationTime.Ticks,
                FourCC = NdiFourCCAudioType.Fltp,
                PData = (nint)ptr,
                ChannelStrideInBytes = samplesPerChannel * sizeof(float),
                PMetadata = nint.Zero,
                Timestamp = presentationTime.Ticks,
            };
            _sender!.SendAudio(ndiFrame);
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

    private unsafe int PushFrameCore(VideoFrame frame, TimeSpan presentationTime)
    {
        // NDI timecode is in 100-nanosecond units — same as TimeSpan.Ticks.
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
                _sender!.SendVideo(ndiFrame);
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
                _sender!.SendVideo(ndiFrame);
                return MediaResult.Success;
            }

            case VideoPixelFormat.Nv12:
            {
                // NDI NV12 layout: Y plane immediately followed by interleaved UV plane,
                // both sharing the same stride.
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
                    _sender!.SendVideo(ndiFrame);
                }
                return MediaResult.Success;
            }

            case VideoPixelFormat.Yuv420P:
            {
                // NDI I420 layout: Y, then U, then V planes consecutively.
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
                    _sender!.SendVideo(ndiFrame);
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
