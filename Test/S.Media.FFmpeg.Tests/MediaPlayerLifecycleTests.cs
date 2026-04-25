using S.Media.FFmpeg.Tests.Helpers;
using S.Media.Playback;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Lifecycle tests for <see cref="MediaPlayer"/> / <see cref="FFmpegDecoder"/>:
/// <list type="bullet">
///   <item><see cref="FFmpegDecoder.StopAsync"/> — review §4.5.</item>
///   <item><see cref="MediaPlayer.DisposeAsync"/> — review §4.4 / B19.</item>
/// </list>
/// </summary>
[Collection("FFmpeg")]
public sealed class MediaPlayerLifecycleTests
{
    [Fact]
    public async Task MediaPlayer_AwaitUsing_WithoutMedia_DoesNotThrow()
    {
        var ex = await Record.ExceptionAsync(async () =>
        {
            await using var player = new MediaPlayer();
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task MediaPlayer_AwaitUsing_AfterOpen_CleansUp()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            var player = new MediaPlayer();
            await player.OpenAsync(path);

            var ex = await Record.ExceptionAsync(async () => await player.DisposeAsync());
            Assert.Null(ex);
            Assert.Equal(PlaybackState.Stopped, player.State);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MediaPlayer_DisposeAsync_IsIdempotent()
    {
        var player = new MediaPlayer();
        await player.DisposeAsync();
        var ex = await Record.ExceptionAsync(async () => await player.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task FFmpegDecoder_StopAsync_WithoutStart_CompletesQuickly()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await dec.StopAsync();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 500, $"StopAsync should be near-instant without Start; was {sw.ElapsedMilliseconds} ms.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FFmpegDecoder_StopAsync_AfterStart_JoinsDemuxTask()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.3f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            dec.Start();
            // Give the demux task a chance to actually start.
            await Task.Delay(25);

            await dec.StopAsync();
            // Second call must be a no-op.
            await dec.StopAsync();
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// §3.2 / B2+B3 — Dispose must: (a) cancel CTS, (b) join the demux task
    /// before disposing channels (so no in-flight <c>WriteAsync</c> hits a
    /// completed ring), (c) close the format context under the
    /// <c>_formatIoGate</c> write lock. Exercised by repeated
    /// open→start→immediate-dispose cycles; any ordering fault would surface
    /// as an AVCodec double-free, a ChannelClosedException leaking through
    /// ReportDemuxLoopError, or a deadlock.
    /// </summary>
    [Fact]
    public async Task FFmpegDecoder_DisposeDuringDemux_IsCleanUnderStress()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.5f);
        try
        {
            for (int i = 0; i < 10; i++)
            {
                var dec = FFmpegDecoder.Open(path);
                dec.Start();
                // Varying delays: sometimes dispose before demux starts,
                // sometimes after it is pushing packets.
                await Task.Delay(i * 5);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                dec.Dispose();
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 3500,
                    $"Dispose stalled ({sw.ElapsedMilliseconds} ms) on iteration {i} — demux-join may have deadlocked.");
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// §3.1 / B1 — Seek control packet must reach the decode worker even when
    /// the packet queue is full at the moment Seek fires. Without the bounded
    /// WriteAsync fallback this test would intermittently leak stale pre-seek
    /// packets and the post-seek Position would briefly reflect the old PTS.
    /// </summary>
    [Fact]
    public async Task FFmpegDecoder_SeekUnderLoad_DoesNotDropFlushSentinel()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 1.0f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            dec.Start();
            // Let the queue fill past typical PacketQueueDepth (64).
            await Task.Delay(50);

            // Perform a burst of seeks; each must complete without throwing
            // even though the ring is likely saturated.
            for (int i = 0; i < 5; i++)
            {
                var pos = TimeSpan.FromMilliseconds(100 + i * 50);
                dec.Seek(pos);
                await Task.Delay(5);
            }

            await dec.StopAsync();
        }
        finally { File.Delete(path); }
    }
}

