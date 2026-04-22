using S.Media.FFmpeg.Tests.Helpers;
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
}

