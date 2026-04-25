using S.Media.Core.Errors;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Verifies §3.3 / B9: when the custom AVIO stream throws during a read, the
/// demux loop surfaces the failure via <see cref="FFmpegDecoder.OnError"/>
/// (as a <see cref="MediaDecodeException"/>) and then terminates cleanly
/// instead of tight-looping on <c>Retry</c>.
/// </summary>
[Collection("FFmpeg")]
public sealed class FFmpegDecoderErrorSurfaceTests
{
    /// <summary>
    /// Delegating stream that throws <see cref="IOException"/> on every read once
    /// <see cref="Arm"/> is called. Used to let <c>FFmpegDecoder.Open</c> probe
    /// the full file normally and only trip during the subsequent demux loop.
    /// </summary>
    private sealed class ArmableBrokenStream : Stream
    {
        private readonly Stream _inner;
        private volatile bool   _armed;

        public ArmableBrokenStream(Stream inner) { _inner = inner; }
        public void Arm() => _armed = true;

        public override bool CanRead  => _inner.CanRead;
        public override bool CanSeek  => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length   => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_armed) throw new IOException("Simulated broken stream");
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_armed) throw new IOException("Simulated broken stream");
            return _inner.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value)                => _inner.SetLength(value);
        public override void Flush()                              => _inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task BrokenStream_DuringDemux_RaisesOnErrorAndStops()
    {
        string path = Helpers.WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 1.0f);
        try
        {
            using var file    = File.OpenRead(path);
            using var armable = new ArmableBrokenStream(file);
            using var dec     = FFmpegDecoder.Open(armable, leaveOpen: true);

            var tcs = new TaskCompletionSource<MediaDecodeException>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            dec.OnError += (_, ex) => tcs.TrySetResult(ex);

            // Arm the break, then start demux — the next av_read_frame must fail.
            armable.Arm();
            dec.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
            Assert.Same(tcs.Task, winner);

            var ex = await tcs.Task;
            Assert.NotNull(ex);
            Assert.IsType<IOException>(ex.InnerException);
        }
        finally { File.Delete(path); }
    }
}

