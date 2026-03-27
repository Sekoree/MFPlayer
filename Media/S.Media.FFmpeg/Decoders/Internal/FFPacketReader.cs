using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Runtime;
using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFPacketReader : IDisposable
{

    private bool _disposed;
    private bool _initialized;
    private bool _isNativeDemuxActive;
    private bool _hasAudio;
    private bool _hasVideo;
    private long _generation;
    private long _nextAudioPacketIndex;
    private long _nextVideoPacketIndex;
    private TimeSpan _nextAudioPresentationTime;
    private TimeSpan _nextVideoPresentationTime;
    private FFNativeFileDemux? _nativeDemux;

    internal bool IsNativeDemuxActive => _isNativeDemuxActive;

    internal bool TryGetNativeStreamDescriptors(out FFStreamDescriptor? audioStream, out FFStreamDescriptor? videoStream)
    {
        audioStream = null;
        videoStream = null;

        if (!_isNativeDemuxActive || _nativeDemux is null)
        {
            return false;
        }

        audioStream = _nativeDemux.GetAudioStreamDescriptor();
        videoStream = _nativeDemux.GetVideoStreamDescriptor();
        return audioStream is not null || videoStream is not null;
    }

    public int Initialize(bool hasAudio, bool hasVideo)
    {
        return Initialize(hasAudio, hasVideo, openOptions: null, audioStreamIndex: null, videoStreamIndex: null);
    }

    public int Initialize(
        bool hasAudio,
        bool hasVideo,
        FFmpegOpenOptions? openOptions,
        int? audioStreamIndex,
        int? videoStreamIndex)
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegReadFailed;
        }

        _hasAudio = hasAudio;
        _hasVideo = hasVideo;
        _nativeDemux?.Dispose();
        _nativeDemux = null;
        _isNativeDemuxActive = FFNativeFileDemux.TryOpen(openOptions, hasAudio, hasVideo, audioStreamIndex, videoStreamIndex, out _nativeDemux);
        _generation = 0;
        _nextAudioPacketIndex = 0;
        _nextVideoPacketIndex = 0;
        _nextAudioPresentationTime = TimeSpan.Zero;
        _nextVideoPresentationTime = TimeSpan.Zero;
        _initialized = true;
        return MediaResult.Success;
    }

    public int Seek(double positionSeconds)
    {
        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegSeekFailed;
        }

        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        _generation++;

        if (_isNativeDemuxActive && _nativeDemux is not null && !_nativeDemux.Seek(positionSeconds))
        {
            return (int)MediaErrorCode.FFmpegSeekFailed;
        }

        _nextAudioPresentationTime = TimeSpan.FromSeconds(positionSeconds);
        _nextVideoPresentationTime = TimeSpan.FromSeconds(positionSeconds);
        _nextAudioPacketIndex = 0;
        _nextVideoPacketIndex = 0;
        return MediaResult.Success;
    }

    public int ReadAudioPacket(out FFPacket packet)
    {
        packet = default;

        if (_disposed || !_initialized || !_hasAudio)
        {
            return (int)MediaErrorCode.FFmpegReadFailed;
        }

        if (_isNativeDemuxActive && _nativeDemux is not null)
        {
            if (!_nativeDemux.TryReadAudioPacket(
                    out var presentationTime,
                    out var isKeyFrame,
                    out var streamIndex,
                    out var codecId,
                    out var nativePacketData,
                    out var packetFlags,
                    out var codecParametersPtr,
                    out var timeBaseNumerator,
                    out var timeBaseDenominator,
                    out var frameRateNumerator,
                    out var frameRateDenominator))
            {
                return (int)MediaErrorCode.FFmpegReadFailed;
            }

            var nativeSampleValue = (float)((_nextAudioPacketIndex % 16) / 16d);
            packet = new FFPacket(
                _generation,
                _nextAudioPacketIndex,
                presentationTime,
                isKeyFrame,
                nativeSampleValue,
                NativePacketData: nativePacketData,
                NativePacketFlags: packetFlags,
                NativeStreamIndex: streamIndex,
                NativeCodecId: codecId,
                NativeCodecParametersPtr: codecParametersPtr,
                NativeTimeBaseNumerator: timeBaseNumerator,
                NativeTimeBaseDenominator: timeBaseDenominator,
                NativeFrameRateNumerator: frameRateNumerator,
                NativeFrameRateDenominator: frameRateDenominator);
            _nextAudioPacketIndex++;
            return MediaResult.Success;
        }

        return (int)MediaErrorCode.FFmpegReadFailed;
    }

    public int ReadVideoPacket(out FFPacket packet)
    {
        packet = default;

        if (_disposed || !_initialized || !_hasVideo)
        {
            return (int)MediaErrorCode.FFmpegReadFailed;
        }

        if (_isNativeDemuxActive && _nativeDemux is not null)
        {
            if (!_nativeDemux.TryReadVideoPacket(
                    out var presentationTime,
                    out var isKeyFrame,
                    out var streamIndex,
                    out var codecId,
                    out var nativePacketData,
                    out var packetFlags,
                    out var codecParametersPtr,
                    out var timeBaseNumerator,
                    out var timeBaseDenominator,
                    out var frameRateNumerator,
                    out var frameRateDenominator))
            {
                return (int)MediaErrorCode.FFmpegReadFailed;
            }

            packet = new FFPacket(
                _generation,
                _nextVideoPacketIndex,
                presentationTime,
                isKeyFrame,
                SampleValue: 0f,
                NativePacketData: nativePacketData,
                NativePacketFlags: packetFlags,
                NativeStreamIndex: streamIndex,
                NativeCodecId: codecId,
                NativeCodecParametersPtr: codecParametersPtr,
                NativeTimeBaseNumerator: timeBaseNumerator,
                NativeTimeBaseDenominator: timeBaseDenominator,
                NativeFrameRateNumerator: frameRateNumerator,
                NativeFrameRateDenominator: frameRateDenominator);
            _nextVideoPacketIndex++;
            return MediaResult.Success;
        }

        return (int)MediaErrorCode.FFmpegReadFailed;
    }

    public int ReadNextPacket()
    {
        if (_hasAudio)
        {
            return ReadAudioPacket(out _);
        }

        if (_hasVideo)
        {
            return ReadVideoPacket(out _);
        }

        return (int)MediaErrorCode.FFmpegReadFailed;
    }

    public void Dispose()
    {
        _disposed = true;
        _nativeDemux?.Dispose();
        _nativeDemux = null;
        _isNativeDemuxActive = false;
    }
}

internal unsafe sealed class FFNativeFileDemux : IDisposable
{
    private AVFormatContext* _formatContext;
    private AVPacket* _packet;
    private bool _disposed;

    private FFNativeFileDemux(AVFormatContext* formatContext, AVPacket* packet, int audioStreamIndex, int videoStreamIndex)
    {
        _formatContext = formatContext;
        _packet = packet;
        AudioStreamIndex = audioStreamIndex;
        VideoStreamIndex = videoStreamIndex;
    }

    public int AudioStreamIndex { get; }

    public int VideoStreamIndex { get; }

    public FFStreamDescriptor? GetAudioStreamDescriptor() => BuildStreamDescriptor(AudioStreamIndex, isAudio: true);

    public FFStreamDescriptor? GetVideoStreamDescriptor() => BuildStreamDescriptor(VideoStreamIndex, isAudio: false);

    public static bool TryOpen(
        FFmpegOpenOptions? openOptions,
        bool hasAudio,
        bool hasVideo,
        int? audioStreamIndex,
        int? videoStreamIndex,
        out FFNativeFileDemux? demux)
    {
        demux = null;

        if (openOptions is null || openOptions.InputStream is not null)
        {
            return false;
        }

        var path = ResolveLocalPath(openOptions.InputUri);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        AVFormatContext* formatContext = null;
        AVPacket* packet = null;

        try
        {
            var openCode = ffmpeg.avformat_open_input(&formatContext, path, null, null);
            if (openCode < 0)
            {
                return false;
            }

            var streamInfoCode = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (streamInfoCode < 0)
            {
                ffmpeg.avformat_close_input(&formatContext);
                return false;
            }

            var resolvedAudioStreamIndex = hasAudio
                ? ResolveStreamIndex(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, audioStreamIndex)
                : -1;
            var resolvedVideoStreamIndex = hasVideo
                ? ResolveStreamIndex(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, videoStreamIndex)
                : -1;

            // Exclude attached-picture streams (e.g. album art in FLAC/MP3).
            // These are single-frame "video" streams that would cause the video reader
            // to drain the entire format context looking for more packets, discarding
            // all audio packets in the process and reaching premature EOF.
            if (resolvedVideoStreamIndex >= 0)
            {
                var videoStream = formatContext->streams[resolvedVideoStreamIndex];
                if ((videoStream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                {
                    resolvedVideoStreamIndex = -1;
                }
            }

            // Fail only when none of the requested streams are found.
            // Audio-only files (e.g. FLAC) legitimately have no video stream.
            var foundAudio = hasAudio && resolvedAudioStreamIndex >= 0;
            var foundVideo = hasVideo && resolvedVideoStreamIndex >= 0;
            if (!foundAudio && !foundVideo)
            {
                ffmpeg.avformat_close_input(&formatContext);
                return false;
            }

            packet = ffmpeg.av_packet_alloc();
            if (packet is null)
            {
                ffmpeg.avformat_close_input(&formatContext);
                return false;
            }

            demux = new FFNativeFileDemux(formatContext, packet, resolvedAudioStreamIndex, resolvedVideoStreamIndex);
            return true;
        }
        catch (DllNotFoundException)
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            demux = null;
            return false;
        }
        catch (NotSupportedException)
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            demux = null;
            return false;
        }
        catch (TypeInitializationException)
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            demux = null;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            demux = null;
            return false;
        }
    }

    public bool Seek(double positionSeconds)
    {
        if (_disposed || _formatContext is null)
        {
            return false;
        }

        var targetUs = (long)Math.Round(positionSeconds * ffmpeg.AV_TIME_BASE);
        var seekCode = ffmpeg.av_seek_frame(_formatContext, -1, targetUs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (seekCode < 0)
        {
            return false;
        }

        ffmpeg.avformat_flush(_formatContext);
        if (_packet is not null)
        {
            ffmpeg.av_packet_unref(_packet);
        }

        return true;
    }

    public bool TryReadAudioPacket(
        out TimeSpan presentationTime,
        out bool isKeyFrame,
        out int streamIndex,
        out int codecId,
        out byte[]? nativePacketData,
        out int packetFlags,
        out nint codecParametersPtr,
        out int timeBaseNumerator,
        out int timeBaseDenominator,
        out int frameRateNumerator,
        out int frameRateDenominator)
    {
        return TryReadPacketForStream(
            AudioStreamIndex,
            out presentationTime,
            out isKeyFrame,
            out streamIndex,
            out codecId,
            out nativePacketData,
            out packetFlags,
            out codecParametersPtr,
            out timeBaseNumerator,
            out timeBaseDenominator,
            out frameRateNumerator,
            out frameRateDenominator);
    }

    public bool TryReadVideoPacket(
        out TimeSpan presentationTime,
        out bool isKeyFrame,
        out int streamIndex,
        out int codecId,
        out byte[]? nativePacketData,
        out int packetFlags,
        out nint codecParametersPtr,
        out int timeBaseNumerator,
        out int timeBaseDenominator,
        out int frameRateNumerator,
        out int frameRateDenominator)
    {
        return TryReadPacketForStream(
            VideoStreamIndex,
            out presentationTime,
            out isKeyFrame,
            out streamIndex,
            out codecId,
            out nativePacketData,
            out packetFlags,
            out codecParametersPtr,
            out timeBaseNumerator,
            out timeBaseDenominator,
            out frameRateNumerator,
            out frameRateDenominator);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_formatContext is not null)
        {
            var formatContext = _formatContext;
            ffmpeg.avformat_close_input(&formatContext);
            _formatContext = null;
        }
    }

    private bool TryReadPacketForStream(
        int streamIndex,
        out TimeSpan presentationTime,
        out bool isKeyFrame,
        out int resolvedStreamIndex,
        out int codecId,
        out byte[]? nativePacketData,
        out int packetFlags,
        out nint codecParametersPtr,
        out int timeBaseNumerator,
        out int timeBaseDenominator,
        out int frameRateNumerator,
        out int frameRateDenominator)
    {
        presentationTime = TimeSpan.Zero;
        isKeyFrame = false;
        resolvedStreamIndex = -1;
        codecId = 0;
        nativePacketData = null;
        packetFlags = 0;
        codecParametersPtr = 0;
        timeBaseNumerator = 0;
        timeBaseDenominator = 0;
        frameRateNumerator = 0;
        frameRateDenominator = 0;

        if (_disposed || _formatContext is null || _packet is null || streamIndex < 0)
        {
            Console.Error.WriteLine($"[TRACE-PKT] TryReadPacketForStream: guard fail disposed={_disposed} fmtCtx={(nint)_formatContext} pkt={(nint)_packet} streamIdx={streamIndex}");
            return false;
        }

        while (true)
        {
            var readCode = ffmpeg.av_read_frame(_formatContext, _packet);
            if (readCode < 0)
            {
                Console.Error.WriteLine($"[TRACE-PKT] av_read_frame returned {readCode} (0x{readCode:X8}) for streamIdx={streamIndex}");
                return false;
            }

            if (_packet->stream_index != streamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            var stream = _formatContext->streams[streamIndex];
            var rawPts = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : _packet->dts;
            var ptsSeconds = rawPts == ffmpeg.AV_NOPTS_VALUE ? 0d : rawPts * ffmpeg.av_q2d(stream->time_base);

            if (!double.IsFinite(ptsSeconds) || ptsSeconds < 0)
            {
                ptsSeconds = 0;
            }

            presentationTime = TimeSpan.FromSeconds(ptsSeconds);
            isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            resolvedStreamIndex = streamIndex;
            codecId = (int)stream->codecpar->codec_id;
            packetFlags = _packet->flags;
            codecParametersPtr = (nint)stream->codecpar;
            timeBaseNumerator = stream->time_base.num;
            timeBaseDenominator = stream->time_base.den;
            frameRateNumerator = stream->avg_frame_rate.num;
            frameRateDenominator = stream->avg_frame_rate.den;
            if (_packet->size > 0 && _packet->data is not null)
            {
                nativePacketData = new byte[_packet->size];
                Marshal.Copy((IntPtr)_packet->data, nativePacketData, 0, _packet->size);
            }

            ffmpeg.av_packet_unref(_packet);
            return true;
        }
    }

    private static int ResolveStreamIndex(AVFormatContext* formatContext, AVMediaType streamType, int? preferredStreamIndex)
    {
        if (preferredStreamIndex.HasValue)
        {
            var preferred = preferredStreamIndex.Value;
            if (preferred >= 0 && preferred < formatContext->nb_streams)
            {
                var preferredStream = formatContext->streams[preferred];
                if (preferredStream->codecpar->codec_type == streamType)
                {
                    return preferred;
                }
            }
        }

        return ffmpeg.av_find_best_stream(formatContext, streamType, -1, -1, null, 0);
    }

    private static string? ResolveLocalPath(string? inputUri)
    {
        if (string.IsNullOrWhiteSpace(inputUri))
        {
            return null;
        }

        if (Uri.TryCreate(inputUri, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return File.Exists(inputUri) ? inputUri : null;
    }

    private FFStreamDescriptor? BuildStreamDescriptor(int streamIndex, bool isAudio)
    {
        if (_disposed || _formatContext is null || streamIndex < 0 || streamIndex >= _formatContext->nb_streams)
        {
            return null;
        }

        var stream = _formatContext->streams[streamIndex];
        var codecParameters = stream->codecpar;
        if (codecParameters is null)
        {
            return null;
        }

        var duration = stream->duration > 0 && stream->time_base.den > 0
            ? TimeSpan.FromSeconds(stream->duration * ffmpeg.av_q2d(stream->time_base))
            : (TimeSpan?)null;

        var frameRate = stream->avg_frame_rate.den > 0
            ? stream->avg_frame_rate.num / (double)stream->avg_frame_rate.den
            : (double?)null;

        var codecName = ffmpeg.avcodec_get_name(codecParameters->codec_id);
        if (string.IsNullOrWhiteSpace(codecName))
        {
            codecName = codecParameters->codec_id.ToString();
        }

        var channelCount = isAudio && codecParameters->ch_layout.nb_channels > 0
            ? codecParameters->ch_layout.nb_channels
            : (int?)null;

        return new FFStreamDescriptor
        {
            StreamIndex = streamIndex,
            CodecName = codecName,
            Duration = duration,
            SampleRate = isAudio && codecParameters->sample_rate > 0 ? codecParameters->sample_rate : null,
            ChannelCount = channelCount,
            Width = !isAudio && codecParameters->width > 0 ? codecParameters->width : null,
            Height = !isAudio && codecParameters->height > 0 ? codecParameters->height : null,
            FrameRate = !isAudio ? frameRate : null,
        };
    }
}

internal readonly record struct FFPacket(
    long Generation,
    long Sequence,
    TimeSpan PresentationTime,
    bool IsKeyFrame,
    float SampleValue,
    byte[]? NativePacketData = null,
    int NativePacketFlags = 0,
    int? NativeStreamIndex = null,
    int? NativeCodecId = null,
    nint? NativeCodecParametersPtr = null,
    int? NativeTimeBaseNumerator = null,
    int? NativeTimeBaseDenominator = null,
    int? NativeFrameRateNumerator = null,
    int? NativeFrameRateDenominator = null);
