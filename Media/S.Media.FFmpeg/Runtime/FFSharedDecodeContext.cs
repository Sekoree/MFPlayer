using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;

namespace S.Media.FFmpeg.Runtime;

public sealed class FFSharedDecodeContext : IDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;

    public bool IsOpen { get; private set; }

    public int RefCount { get; private set; }

    public FFStreamDescriptor? AudioStream { get; private set; }

    public FFStreamDescriptor? VideoStream { get; private set; }

    public FFmpegDecodeOptions ResolvedDecodeOptions { get; private set; } = new();

    public int Open(FFmpegOpenOptions openOptions, FFmpegDecodeOptions decodeOptions)
    {
        ArgumentNullException.ThrowIfNull(openOptions);
        ArgumentNullException.ThrowIfNull(decodeOptions);

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.FFmpegSharedContextDisposed;
            }

            var validation = FFmpegConfigValidator.Validate(openOptions, decodeOptions);
            if (validation != MediaResult.Success)
            {
                return validation;
            }

            var normalizedDecodeOptions = decodeOptions.Normalize();

            IsOpen = true;
            RefCount++;
            ResolvedDecodeOptions = normalizedDecodeOptions;

            AudioStream = openOptions.OpenAudio
                ? new FFStreamDescriptor
                {
                    StreamIndex = openOptions.AudioStreamIndex ?? 0,
                    CodecName = "pcm_f32le",
                    SampleRate = 48_000,
                    ChannelCount = 2,
                }
                : null;

            VideoStream = openOptions.OpenVideo
                ? new FFStreamDescriptor
                {
                    StreamIndex = openOptions.VideoStreamIndex ?? 0,
                    CodecName = "placeholder_rgba",
                    Width = 2,
                    Height = 2,
                    FrameRate = 30d,
                }
                : null;

            return MediaResult.Success;
        }
    }

    public int Close()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            if (RefCount > 0)
            {
                RefCount--;
            }

            if (RefCount == 0)
            {
                IsOpen = false;
                AudioStream = null;
                VideoStream = null;
                ResolvedDecodeOptions = new FFmpegDecodeOptions();
            }

            return MediaResult.Success;
        }
    }

    internal bool ApplyResolvedStreamDescriptors(FFStreamDescriptor? audioStream, FFStreamDescriptor? videoStream)
    {
        var changed = false;

        lock (_gate)
        {
            if (_disposed || !IsOpen)
            {
                return false;
            }

            if (audioStream is not null)
            {
                changed |= AudioStream != audioStream;
                AudioStream = audioStream;
            }

            if (videoStream is not null)
            {
                changed |= VideoStream != videoStream;
                VideoStream = videoStream;
            }
        }

        return changed;
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
            RefCount = 0;
            IsOpen = false;
            AudioStream = null;
            VideoStream = null;
            ResolvedDecodeOptions = new FFmpegDecodeOptions();
        }
    }
}

