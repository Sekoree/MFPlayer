using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace S.Media.FFmpeg;

/// <summary>
/// Bridges a <see cref="System.IO.Stream"/> to an FFmpeg <see cref="AVIOContext"/>,
/// enabling media decoding from arbitrary .NET streams (MemoryStream, NetworkStream,
/// HttpContent streams, etc.).
///
/// The class pins itself via <see cref="GCHandle"/> so that the native AVIO callbacks
/// can recover <c>this</c> from the opaque pointer. Disposal frees the AVIO context,
/// its internal buffer, the GCHandle, and optionally closes the underlying stream.
/// </summary>
internal sealed unsafe class StreamAvioContext : IDisposable
{
    private static readonly ILogger Log = FFmpegLogging.GetLogger(nameof(StreamAvioContext));

    private const int DefaultBufferSize = 32 * 1024; // 32 KB — matches ffmpeg default

    private readonly Stream _stream;
    private readonly bool   _leaveOpen;
    private GCHandle        _gcHandle;
    private AVIOContext*     _avioCtx;
    private bool            _disposed;

    // Must remain as fields (not locals) — prevents GC collection while native AVIO holds the function pointers.
    // ReSharper disable FieldCanBeMadeReadOnly.Local
    private readonly avio_alloc_context_read_packet _readDelegate;
    private readonly avio_alloc_context_seek        _seekDelegate;
    // ReSharper restore FieldCanBeMadeReadOnly.Local

    /// <summary>
    /// The AVIO context to assign to <c>AVFormatContext.pb</c> before calling
    /// <c>avformat_open_input</c>.
    /// </summary>
    public AVIOContext* Context => _avioCtx;

    /// <summary>
    /// Creates a custom AVIO context backed by <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">The source stream. Must support <see cref="Stream.Read(byte[], int, int)"/>.</param>
    /// <param name="leaveOpen">
    /// When <see langword="false"/> (default), <see cref="Dispose"/> closes the stream.
    /// When <see langword="true"/>, the caller retains ownership of the stream.
    /// </param>
    /// <param name="bufferSize">Size of the internal AVIO read buffer (allocated via <c>av_malloc</c>).</param>
    public StreamAvioContext(Stream stream, bool leaveOpen = false, int bufferSize = DefaultBufferSize)
    {
        _stream    = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;

        Log.LogDebug("Creating StreamAvioContext: streamType={StreamType}, canSeek={CanSeek}, leaveOpen={LeaveOpen}, bufferSize={BufferSize}",
            stream.GetType().Name, stream.CanSeek, leaveOpen, bufferSize);

        // Pin this object so native code can recover it from the opaque pointer.
        _gcHandle = GCHandle.Alloc(this);

        // Keep delegate instances alive for the lifetime of this object.
        _readDelegate = ReadPacket;
        _seekDelegate = Seek;

        // av_malloc is required — ffmpeg may call av_free on the buffer internally.
        byte* buffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
        if (buffer == null)
        {
            _gcHandle.Free();
            throw new OutOfMemoryException("av_malloc failed for AVIO buffer.");
        }

        _avioCtx = ffmpeg.avio_alloc_context(
            buffer,
            bufferSize,
            0,                                            // write_flag = 0 (read-only)
            (void*)GCHandle.ToIntPtr(_gcHandle),          // opaque
            _readDelegate,                                // read_packet
            null,                                         // write_packet (not needed)
            stream.CanSeek ? _seekDelegate : null);       // seek (only if stream supports it)

        if (_avioCtx == null)
        {
            ffmpeg.av_free(buffer);
            _gcHandle.Free();
            throw new InvalidOperationException("avio_alloc_context returned null.");
        }
    }

    /// <summary>
    /// AVIO read callback: int read_packet(void* opaque, byte* buf, int buf_size).
    /// Returns number of bytes read, or AVERROR_EOF on end-of-stream.
    /// </summary>
    private static int ReadPacket(void* opaque, byte* buf, int bufSize)
    {
        var self = (StreamAvioContext)GCHandle.FromIntPtr((nint)opaque).Target!;
        var span = new Span<byte>(buf, bufSize);

        try
        {
            int totalRead = 0;
            while (totalRead < bufSize)
            {
                int n = self._stream.Read(span.Slice(totalRead));
                if (n == 0) break; // end of stream
                totalRead += n;
            }

            return totalRead > 0 ? totalRead : ffmpeg.AVERROR_EOF;
        }
        catch (Exception)
        {
            return ffmpeg.AVERROR_EOF;
        }
    }

    /// <summary>
    /// AVIO seek callback: long seek(void* opaque, long offset, int whence).
    /// <c>whence</c> matches POSIX values (SEEK_SET=0, SEEK_CUR=1, SEEK_END=2)
    /// plus the FFmpeg extension AVSEEK_SIZE (0x10000) which requests the total size.
    /// </summary>
    private static long Seek(void* opaque, long offset, int whence)
    {
        var self = (StreamAvioContext)GCHandle.FromIntPtr((nint)opaque).Target!;
        var stream = self._stream;

        try
        {
            if (whence == ffmpeg.AVSEEK_SIZE)
            {
                // Return total stream length, or -1 if unknown.
                try { return stream.Length; }
                catch { return -1; }
            }

            var origin = whence switch
            {
                0 => SeekOrigin.Begin,   // SEEK_SET
                1 => SeekOrigin.Current, // SEEK_CUR
                2 => SeekOrigin.End,     // SEEK_END
                _ => SeekOrigin.Begin
            };

            return stream.Seek(offset, origin);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogDebug("Disposing StreamAvioContext: leaveOpen={LeaveOpen}", _leaveOpen);

        if (_avioCtx != null)
        {
            // avio_context_free frees the context struct.
            // The internal buffer is freed by ffmpeg if it was allocated with av_malloc.
            // However, if avformat_open_input took ownership, the buffer may already be freed.
            // We let avio_context_free handle the buffer field if present, and null it to
            // prevent avformat_close_input from double-freeing.
            ffmpeg.av_free(_avioCtx->buffer);
            _avioCtx->buffer = null;

            fixed (AVIOContext** pp = &_avioCtx)
                ffmpeg.avio_context_free(pp);
        }

        if (_gcHandle.IsAllocated)
            _gcHandle.Free();

        if (!_leaveOpen)
            _stream.Dispose();
    }
}

