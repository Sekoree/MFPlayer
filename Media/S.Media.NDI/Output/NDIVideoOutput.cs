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
    private long _videoPushSuccesses;
    private long _videoPushFailures;
    private long _audioPushSuccesses;
    private long _audioPushFailures;
    private double _lastPushMs;

    public NDIVideoOutput(string outputName, NDIOutputOptions options)
    {
        if (string.IsNullOrWhiteSpace(outputName))
        {
            throw new ArgumentException("Output name is required.", nameof(outputName));
        }

        OutputName = outputName;
        Options = options;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

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
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.NDIOutputPushVideoFailed;
            }

            var outputValidate = Options.Validate();
            if (outputValidate != MediaResult.Success)
            {
                return outputValidate;
            }

            var configValidate = config.Validate(hasEffectiveFrameDuration: false);
            if (configValidate != MediaResult.Success)
            {
                return configValidate;
            }

            _running = true;
            return MediaResult.Success;
        }
    }

    public int Start()
    {
        return Start(new VideoOutputConfig());
    }

    public int Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            _running = false;
            return MediaResult.Success;
        }
    }

    public int PushFrame(VideoFrame frame)
    {
        return PushFrame(frame, frame.PresentationTime);
    }

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

            if (!_running)
            {
                _videoPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputPushVideoFailed;
            }

            _videoPushSuccesses++;
            _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return MediaResult.Success;
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

            if (!_running)
            {
                _audioPushFailures++;
                _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (int)MediaErrorCode.NDIOutputPushAudioFailed;
            }

            _audioPushSuccesses++;
            _lastPushMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return MediaResult.Success;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
        }
    }
}
