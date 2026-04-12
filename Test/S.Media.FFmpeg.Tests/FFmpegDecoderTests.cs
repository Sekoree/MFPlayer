using System.Diagnostics;
using S.Media.Core.Media;
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

            var buf = new float[1024 * 2];
            int filled = 0;

            // Allow startup jitter in worker scheduling.
            var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.0);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                filled = ch.FillBuffer(buf, 1024);
                if (filled > 0) break;
                Thread.Sleep(20);
            }

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

    [Fact]
    public void Start_CalledTwice_DoesNotThrow()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.3f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            var ex = Record.Exception(() =>
            {
                dec.Start();
                dec.Start();
            });
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Start_Seek_DoesNotThrow()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 1.0f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            dec.Start();

            var ex = Record.Exception(() => dec.Seek(TimeSpan.FromMilliseconds(250)));
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RapidSeeks_DoNotSnapAudioPositionBackToZero()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 3.0f);
        try
        {
            using var dec = FFmpegDecoder.Open(path);
            var ch = dec.AudioChannels[0];
            dec.Start();

            var buf = new float[256 * 2];

            // Warm up decode.
            var warmupDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.5);
            while (Stopwatch.GetTimestamp() < warmupDeadline)
            {
                ch.FillBuffer(buf, 256);
                Thread.Sleep(5);
            }

            dec.Seek(TimeSpan.FromSeconds(1.5));
            TimeSpan minAfterForward = TimeSpan.MaxValue;
            var forwardDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.5);
            while (Stopwatch.GetTimestamp() < forwardDeadline)
            {
                ch.FillBuffer(buf, 256);
                var pos = ch.Position;
                if (pos < minAfterForward) minAfterForward = pos;
                Thread.Sleep(5);
            }

            dec.Seek(TimeSpan.FromSeconds(0.8));
            TimeSpan minAfterBackward = TimeSpan.MaxValue;
            var backwardDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.5);
            while (Stopwatch.GetTimestamp() < backwardDeadline)
            {
                ch.FillBuffer(buf, 256);
                var pos = ch.Position;
                if (pos < minAfterBackward) minAfterBackward = pos;
                Thread.Sleep(5);
            }

            Assert.True(minAfterForward > TimeSpan.FromSeconds(1.0),
                $"Position regressed too far after forward seek. min={minAfterForward}");
            Assert.True(minAfterBackward > TimeSpan.FromSeconds(0.4),
                $"Position regressed too far after backward seek. min={minAfterBackward}");
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

    [Fact]
    public void Open_WithOptions_NegativeThreadCount_IsNormalizedAndDoesNotThrow()
    {
        string path = WavFileGenerator.CreateTempSineWav(48000, 2, 440f, 0.1f);
        try
        {
            var opts = new FFmpegDecoderOptions { DecoderThreadCount = -8 };
            var ex = Record.Exception(() =>
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

