using S.Media.Core.Errors;
using S.Media.FFmpeg.Tests.Helpers;
using S.Media.Playback;
using Xunit;

namespace S.Media.FFmpeg.Tests;

[Collection("FFmpeg")]
public sealed class MediaPlayerSeekAsyncTests
{
    [Fact]
    public async Task SeekAsync_WithoutOpen_ThrowsMediaException()
    {
        using var player = new MediaPlayer();
        await Assert.ThrowsAsync<MediaException>(() => player.SeekAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task SeekAsync_WhenNotPlaying_ReturnsImmediately()
    {
        string path = WavFileGenerator.CreateTempSineWav(48_000, 2, 440f, 0.4f);
        try
        {
            using var player = new MediaPlayer();
            await player.OpenAsync(path);

            // Small timeout would fail if SeekAsync waited for presentation while idle.
            await player.SeekAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SeekAsync_WhenPlaying_CompletesOnPostSeekData()
    {
        string path = WavFileGenerator.CreateTempSineWav(48_000, 2, 440f, 1.0f);
        try
        {
            using var player = new MediaPlayer();
            await player.OpenAsync(path);
            await player.PlayAsync();
            await Task.Delay(40);

            await player.SeekAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2));

            // Position may lag by one pull quantum; verify we're around the target.
            Assert.True(player.Position >= TimeSpan.FromMilliseconds(150),
                $"Expected post-seek position near target, got {player.Position}.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
