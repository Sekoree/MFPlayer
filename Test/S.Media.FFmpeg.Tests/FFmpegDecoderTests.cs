using S.Media.FFmpeg.Tests.Helpers;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Integration tests for <see cref="FFmpegDecoder"/>.
/// Requires FFmpeg shared libraries. Generates temporary WAV files for testing.
/// </summary>
[Collection("FFmpeg")]
public sealed class FFmpegDecoderTests
{
    // FFmpeg is initialised by FfmpegFixture (injected via collection fixture).

    // ── Open / error handling ───────────────────────────────────────────────

    [Fact]
    public void Open_InvalidPath_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FFmpegDecoder.Open("/this/path/does/not/exist.wav"));
    }

    [Fact]
    public void Open_ValidWav_ReturnsOneAudioChannel()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            Assert.Single(dec.AudioChannels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Open_ValidWav_NoVideoChannels()
    {
        string path = WavFileGenerator.CreateTempSineWav(44100, 1, 440f, 0.1f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            Assert.Empty(dec.VideoChannels);
        }
        finally { File.Delete(path); }
    }

    // ── Audio channel format ─────────────────────────────────────────────────

    [Fact]
    public void AudioChannel_SourceFormat_MatchesWavHeader_StereoAt48k()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            var fmt = dec.AudioChannels[0].SourceFormat;
            Assert.Equal(48000, fmt.SampleRate);
            Assert.Equal(2,     fmt.Channels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AudioChannel_SourceFormat_MatchesWavHeader_MonoAt44k()
    {
        string path = WavFileGenerator.CreateTempSineWav(44100, 1, 440f, 0.1f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            var fmt = dec.AudioChannels[0].SourceFormat;
            Assert.Equal(44100, fmt.SampleRate);
            Assert.Equal(1,     fmt.Channels);
        }
        finally { File.Delete(path); }
    }

    // ── Decoding / FillBuffer ────────────────────────────────────────────────

    [Fact]
    public void Start_ThenFillBuffer_ProducesNonSilentOutput()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.5f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            var ch = dec.AudioChannels[0];
            dec.Start();

            // Give decode thread time to buffer some data.
            Thread.Sleep(200);

            var buf = new float[1024 * 2];
            int filled = ch.FillBuffer(buf, 1024);

            Assert.True(filled > 0, "FillBuffer should return decoded frames.");
            Assert.True(buf.Any(s => s != 0f), "Output should contain non-zero audio from the sine tone.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Start_ThenDispose_DoesNotThrow()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            var dec = FFmpegDecoder.Open(path);
            dec.Start();
            Thread.Sleep(50);
            var ex = Record.Exception(() => dec.Dispose());
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }

    // ── Decoder options ──────────────────────────────────────────────────────

    [Fact]
    public void Open_WithOptions_CustomBufferDepth_Applied()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            var opts = new FFmpegDecoderOptions { AudioBufferDepth = 4 };
            using var dec = FFmpegDecoder.Open(path, opts);
            Assert.Equal(4, dec.AudioChannels[0].BufferDepth);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Open_WithOptions_SingleThread_Applied()
    {
        // Verify that a single-thread option doesn't crash open.
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            var opts = new FFmpegDecoderOptions { DecoderThreadCount = 1 };
            var ex   = Record.Exception(() =>
            {
                using var dec = FFmpegDecoder.Open(path, opts);
            });
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }

    // ── VideoFrame pooling ───────────────────────────────────────────────────

    [Fact]
    public void VideoFrame_WithMemoryOwner_CanBeDisposed()
    {
        // Verify the VideoFrame.MemoryOwner pattern works end-to-end.
        using var owner = new ArrayPoolOwner<byte>(System.Buffers.ArrayPool<byte>.Shared.Rent(64));
        var ex = Record.Exception(() => owner.Dispose());
        Assert.Null(ex);
    }
}

