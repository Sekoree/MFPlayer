using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Seko.OwnAudioNET.Video.Probing;

/// <summary>Utility methods to inspect all streams in a media file.</summary>
public static unsafe class MediaStreamCatalog
{
    private const int AvioBufferSize = 32 * 1024;
    private const int SeekSet = 0;
    private const int SeekCur = 1;
    private const int SeekEnd = 2;

    // Keep AVIO callbacks rooted for process lifetime.
    private static readonly avio_alloc_context_read_packet SReadPacketDelegate = ReadPacket;
    private static readonly avio_alloc_context_seek SSeekPacketDelegate = SeekPacket;

    /// <summary>Enumerates all streams in <paramref name="filePath"/>.</summary>
    public static IReadOnlyList<MediaStreamInfoEntry> GetStreams(string filePath)
    {
        return GetStreamsCore(filePath, kindFilter: null);
    }

    /// <summary>Tries to enumerate all streams in <paramref name="filePath"/>.</summary>
    public static bool TryGetStreams(string filePath, out IReadOnlyList<MediaStreamInfoEntry> streams)
    {
        return TryGetStreamsCore(() => GetStreamsCore(filePath, kindFilter: null), out streams);
    }

    /// <summary>Enumerates all streams from <paramref name="stream"/>.</summary>
    public static IReadOnlyList<MediaStreamInfoEntry> GetStreams(Stream stream, bool leaveOpen = false)
    {
        return GetStreamsCore(stream, kindFilter: null, leaveOpen);
    }

    /// <summary>Tries to enumerate all streams from <paramref name="stream"/>.</summary>
    public static bool TryGetStreams(Stream stream, out IReadOnlyList<MediaStreamInfoEntry> streams, bool leaveOpen = false)
    {
        return TryGetStreamsCore(() => GetStreamsCore(stream, kindFilter: null, leaveOpen), out streams);
    }

    /// <summary>Enumerates only streams of <paramref name="kind"/> in <paramref name="filePath"/>.</summary>
    public static IReadOnlyList<MediaStreamInfoEntry> GetStreams(string filePath, MediaStreamKind kind)
    {
        return GetStreamsCore(filePath, kind);
    }

    /// <summary>Enumerates only streams of <paramref name="kind"/> from <paramref name="stream"/>.</summary>
    public static IReadOnlyList<MediaStreamInfoEntry> GetStreams(Stream stream, MediaStreamKind kind, bool leaveOpen = false)
    {
        return GetStreamsCore(stream, kind, leaveOpen);
    }

    /// <summary>Tries to enumerate only streams of <paramref name="kind"/> in <paramref name="filePath"/>.</summary>
    public static bool TryGetStreams(string filePath, MediaStreamKind kind, out IReadOnlyList<MediaStreamInfoEntry> streams)
    {
        return TryGetStreamsCore(() => GetStreamsCore(filePath, kind), out streams);
    }

    /// <summary>Tries to enumerate only streams of <paramref name="kind"/> from <paramref name="stream"/>.</summary>
    public static bool TryGetStreams(Stream stream, MediaStreamKind kind, out IReadOnlyList<MediaStreamInfoEntry> streams, bool leaveOpen = false)
    {
        return TryGetStreamsCore(() => GetStreamsCore(stream, kind, leaveOpen), out streams);
    }

    /// <summary>Returns the first stream of <paramref name="kind"/> in <paramref name="filePath"/>, or <see langword="null"/> when none exists.</summary>
    public static MediaStreamInfoEntry? GetFirstStream(string filePath, MediaStreamKind kind)
    {
        var streams = GetStreamsCore(filePath, kind);
        return streams.Count > 0 ? streams[0] : null;
    }

    /// <summary>Returns the first stream of <paramref name="kind"/> from <paramref name="stream"/>, or <see langword="null"/> when none exists.</summary>
    public static MediaStreamInfoEntry? GetFirstStream(Stream stream, MediaStreamKind kind, bool leaveOpen = false)
    {
        var streams = GetStreamsCore(stream, kind, leaveOpen);
        return streams.Count > 0 ? streams[0] : null;
    }

    /// <summary>Tries to get the first stream of <paramref name="kind"/> in <paramref name="filePath"/>.</summary>
    public static bool TryGetFirstStream(string filePath, MediaStreamKind kind, out MediaStreamInfoEntry stream)
    {
        stream = default;
        if (!TryGetStreams(filePath, kind, out var streams) || streams.Count == 0)
            return false;

        stream = streams[0];
        return true;
    }

    /// <summary>Tries to get the first stream of <paramref name="kind"/> from <paramref name="stream"/>.</summary>
    public static bool TryGetFirstStream(Stream stream, MediaStreamKind kind, out MediaStreamInfoEntry result, bool leaveOpen = false)
    {
        result = default;
        if (!TryGetStreams(stream, kind, out var streams, leaveOpen) || streams.Count == 0)
            return false;

        result = streams[0];
        return true;
    }

    private static IReadOnlyList<MediaStreamInfoEntry> GetStreamsCore(string filePath, MediaStreamKind? kindFilter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        AVFormatContext* formatContext = null;
        var openResult = ffmpeg.avformat_open_input(&formatContext, filePath, null, null);
        if (openResult < 0 || formatContext == null)
            throw new InvalidOperationException($"avformat_open_input failed: {GetErrorText(openResult)}");

        try
        {
            var infoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (infoResult < 0)
                throw new InvalidOperationException($"avformat_find_stream_info failed: {GetErrorText(infoResult)}");

            var result = new List<MediaStreamInfoEntry>((int)formatContext->nb_streams);
            FillEntries(formatContext, kindFilter, result);

            return result;
        }
        finally
        {
            ffmpeg.avformat_close_input(&formatContext);
        }
    }

    private static IReadOnlyList<MediaStreamInfoEntry> GetStreamsCore(Stream stream, MediaStreamKind? kindFilter, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Input stream must be readable.", nameof(stream));

        var state = new StreamProbeState(stream, leaveOpen);
        var stateHandle = GCHandle.Alloc(state, GCHandleType.Normal);
        var hasState = true;

        AVFormatContext* formatContext = null;
        AVIOContext* avioContext = null;
        byte* avioBuffer = null;

        try
        {
            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
                throw new InvalidOperationException("avformat_alloc_context failed");

            avioBuffer = (byte*)ffmpeg.av_malloc(AvioBufferSize);
            if (avioBuffer == null)
                throw new InvalidOperationException("av_malloc failed for AVIO buffer");

            var opaque = GCHandle.ToIntPtr(stateHandle).ToPointer();
            avioContext = ffmpeg.avio_alloc_context(
                avioBuffer,
                AvioBufferSize,
                0,
                opaque,
                (avio_alloc_context_read_packet_func)SReadPacketDelegate,
                null,
                stream.CanSeek ? (avio_alloc_context_seek_func)SSeekPacketDelegate : default);

            if (avioContext == null)
                throw new InvalidOperationException("avio_alloc_context failed");

            formatContext->pb = avioContext;
            formatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            var openResult = ffmpeg.avformat_open_input(&formatContext, null, null, null);
            if (openResult < 0 || formatContext == null)
                throw new InvalidOperationException($"avformat_open_input(stream) failed: {GetErrorText(openResult)}");

            var infoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (infoResult < 0)
                throw new InvalidOperationException($"avformat_find_stream_info failed: {GetErrorText(infoResult)}");

            var result = new List<MediaStreamInfoEntry>((int)formatContext->nb_streams);
            FillEntries(formatContext, kindFilter, result);
            return result;
        }
        finally
        {
            if (formatContext != null)
                ffmpeg.avformat_close_input(&formatContext);

            if (avioContext != null)
            {
                if (avioContext->buffer != null)
                {
                    ffmpeg.av_free(avioContext->buffer);
                    avioContext->buffer = null;
                }

                ffmpeg.avio_context_free(&avioContext);
                avioBuffer = null;
            }
            else if (avioBuffer != null)
            {
                ffmpeg.av_free(avioBuffer);
            }

            if (hasState)
            {
                stateHandle.Free();
                hasState = false;
            }

            if (!leaveOpen)
                stream.Dispose();
        }
    }


    private static bool TryGetStreamsCore(Func<IReadOnlyList<MediaStreamInfoEntry>> getter, out IReadOnlyList<MediaStreamInfoEntry> streams)
    {
        try
        {
            streams = getter();
            return true;
        }
        catch
        {
            streams = Array.Empty<MediaStreamInfoEntry>();
            return false;
        }
    }

    private static void FillEntries(AVFormatContext* formatContext, MediaStreamKind? kindFilter, List<MediaStreamInfoEntry> result)
    {
        for (var i = 0; i < formatContext->nb_streams; i++)
        {
            var stream = formatContext->streams[i];
            var codecParameters = stream->codecpar;
            var kind = MapKind(codecParameters->codec_type);

            if (kindFilter.HasValue && kind != kindFilter.Value)
                continue;

            var codec = ffmpeg.avcodec_get_name(codecParameters->codec_id) ?? "unknown";
            var language = ReadMetadataValue(stream->metadata, "language");

            var duration = stream->duration > 0
                ? TimeSpan.FromSeconds(stream->duration * ffmpeg.av_q2d(stream->time_base))
                : (TimeSpan?)null;

            var frameRate = kind == MediaStreamKind.Video
                ? ResolveFrameRate(stream)
                : null;

            result.Add(new MediaStreamInfoEntry(
                Index: (int)i,
                Kind: kind,
                Codec: codec,
                Language: language,
                Channels: kind == MediaStreamKind.Audio ? codecParameters->ch_layout.nb_channels : null,
                SampleRate: kind == MediaStreamKind.Audio ? codecParameters->sample_rate : null,
                Width: kind == MediaStreamKind.Video ? codecParameters->width : null,
                Height: kind == MediaStreamKind.Video ? codecParameters->height : null,
                FrameRate: frameRate,
                Duration: duration,
                BitRate: codecParameters->bit_rate > 0 ? codecParameters->bit_rate : null));
        }
    }

    private static MediaStreamKind MapKind(AVMediaType mediaType)
    {
        return mediaType switch
        {
            AVMediaType.AVMEDIA_TYPE_AUDIO => MediaStreamKind.Audio,
            AVMediaType.AVMEDIA_TYPE_VIDEO => MediaStreamKind.Video,
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => MediaStreamKind.Subtitle,
            AVMediaType.AVMEDIA_TYPE_DATA => MediaStreamKind.Data,
            AVMediaType.AVMEDIA_TYPE_ATTACHMENT => MediaStreamKind.Attachment,
            _ => MediaStreamKind.Unknown
        };
    }

    private static double? ResolveFrameRate(AVStream* stream)
    {
        var avg = ffmpeg.av_q2d(stream->avg_frame_rate);
        if (avg > 0)
            return avg;

        var raw = ffmpeg.av_q2d(stream->r_frame_rate);
        return raw > 0 ? raw : null;
    }

    private static string? ReadMetadataValue(AVDictionary* metadata, string key)
    {
        if (metadata == null)
            return null;

        var tag = ffmpeg.av_dict_get(metadata, key, null, 0);
        if (tag == null || tag->value == null)
            return null;

        return Marshal.PtrToStringAnsi((nint)tag->value);
    }

    private sealed class StreamProbeState
    {
        public StreamProbeState(Stream stream, bool leaveOpen)
        {
            Stream = stream;
            LeaveOpen = leaveOpen;
        }

        public Stream Stream { get; }
        public bool LeaveOpen { get; }
        public Lock SyncRoot { get; } = new();
    }

    private static int ReadPacket(void* opaque, byte* buffer, int bufferSize)
    {
        try
        {
            var state = GetState(opaque);
            if (state == null || bufferSize <= 0)
                return ffmpeg.AVERROR(ffmpeg.EINVAL);

            lock (state.SyncRoot)
            {
                var destination = new Span<byte>(buffer, bufferSize);
                var bytesRead = state.Stream.Read(destination);
                if (bytesRead <= 0)
                    return ffmpeg.AVERROR_EOF;

                return bytesRead;
            }
        }
        catch
        {
            return ffmpeg.AVERROR(ffmpeg.EINVAL);
        }
    }

    private static long SeekPacket(void* opaque, long offset, int whence)
    {
        try
        {
            var state = GetState(opaque);
            if (state == null || !state.Stream.CanSeek)
                return ffmpeg.AVERROR(ffmpeg.EINVAL);

            lock (state.SyncRoot)
            {
                if ((whence & ffmpeg.AVSEEK_SIZE) != 0)
                    return state.Stream.Length;

                var baseWhence = whence & ~ffmpeg.AVSEEK_FORCE;
                var origin = baseWhence switch
                {
                    SeekSet => SeekOrigin.Begin,
                    SeekCur => SeekOrigin.Current,
                    SeekEnd => SeekOrigin.End,
                    _ => throw new InvalidOperationException()
                };

                return state.Stream.Seek(offset, origin);
            }
        }
        catch
        {
            return ffmpeg.AVERROR(ffmpeg.EINVAL);
        }
    }

    private static StreamProbeState? GetState(void* opaque)
    {
        if (opaque == null)
            return null;

        var handle = GCHandle.FromIntPtr((nint)opaque);
        return handle.Target as StreamProbeState;
    }

    private static string GetErrorText(int code)
    {
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(code, buffer, 1024);
        return Marshal.PtrToStringAnsi((nint)buffer) ?? code.ToString();
    }
}

