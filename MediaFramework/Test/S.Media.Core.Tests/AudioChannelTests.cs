using S.Media.Core.Audio;
using S.Media.Core.Media;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Tests for <see cref="AudioChannel"/>: push/pull, BufferAvailable tracking, underrun,
/// seek flush, and dispose guards.
/// </summary>
public sealed class AudioChannelTests
{
    private static AudioFormat Stereo48k => new(48000, 2);
    private static AudioFormat Mono48k   => new(48000, 1);

    // Convenience: avoid repeating ReadOnlyMemory<float> construction everywhere.
    private static ReadOnlyMemory<float> M(params float[] samples) => samples;

    // ── WriteAsync / FillBuffer round-trip ───────────────────────────────

    [Fact]
    public async Task WriteAsync_Then_FillBuffer_ReturnsCorrectSamples()
    {
        using var ch = new AudioChannel(Stereo48k, bufferDepth: 4);
        float[] data = [1f, 2f, 3f, 4f]; // 2 stereo frames

        await ch.WriteAsync(M(1f, 2f, 3f, 4f));

        float[] dest = new float[4];
        int frames = ch.FillBuffer(dest, 2);

        Assert.Equal(2, frames);
        Assert.Equal(data, dest);
    }

    [Fact]
    public async Task FillBuffer_AcrossChunkBoundary_ReturnsAllSamples()
    {
        // Ring buffers use chunks; ensure partial reads across two chunks work.
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);

        await ch.WriteAsync(M(1f, 2f)); // chunk 1
        await ch.WriteAsync(M(3f, 4f)); // chunk 2

        float[] dest = new float[4];
        int frames = ch.FillBuffer(dest, 4);

        Assert.Equal(4, frames);
        Assert.Equal(new float[] { 1f, 2f, 3f, 4f }, dest);
    }

    // ── BufferAvailable ───────────────────────────────────────────────────

    [Fact]
    public async Task BufferAvailable_IncrementsOnWrite_DecrementsOnPull()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 8);

        Assert.Equal(0, ch.BufferAvailable);

        await ch.WriteAsync(M(1f, 2f, 3f, 4f)); // 4 frames
        Assert.Equal(4, ch.BufferAvailable);

        float[] dest = new float[2];
        ch.FillBuffer(dest, 2); // pull 2 frames
        Assert.Equal(2, ch.BufferAvailable);

        ch.FillBuffer(dest, 2); // pull remaining 2
        Assert.Equal(0, ch.BufferAvailable);
    }

    [Fact]
    public async Task BufferAvailable_IsAccurateAfterPartialPull()
    {
        // Write 6 frames across 2 chunks of 3, pull 4 — 2 should remain
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        await ch.WriteAsync(M(1f, 2f, 3f)); // 3 frames
        await ch.WriteAsync(M(4f, 5f, 6f)); // 3 frames

        float[] dest = new float[4];
        ch.FillBuffer(dest, 4); // pull 4 from two chunks
        Assert.Equal(2, ch.BufferAvailable);
    }

    // ── Underrun ──────────────────────────────────────────────────────────

    [Fact]
    public void FillBuffer_OnEmptyRing_ReturnsSilenceAndZeroFrames()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        float[] dest = new float[] { 9f, 9f, 9f, 9f }; // pre-fill with non-zero

        int frames = ch.FillBuffer(dest, 4);

        Assert.Equal(0, frames);
        Assert.All(dest, s => Assert.Equal(0f, s)); // silence
    }

    [Fact]
    public async Task FillBuffer_OnUnderrun_RaisesBufferUnderrunEvent()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        await ch.WriteAsync(M(1f, 2f)); // only 2 frames, request 4

        int droppedFrames = 0;
        var tcs = new TaskCompletionSource<bool>();
        ch.BufferUnderrun += (_, e) =>
        {
            droppedFrames = e.FramesDropped;
            tcs.TrySetResult(true);
        };

        float[] dest = new float[4];
        ch.FillBuffer(dest, 4);

        // Underrun fires on a thread-pool thread — give it a moment.
        bool fired = await Task.WhenAny(tcs.Task, Task.Delay(500)) == tcs.Task;
        Assert.True(fired, "BufferUnderrun event was not raised.");
        Assert.Equal(2, droppedFrames); // 4 requested - 2 available = 2 dropped
    }

    // ── Seek ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Seek_ToNonZero_ThenPullNewData_PositionAdvancesFromSeekPoint()
    {
        // Arrange: seek to 5 s, then write 48 000 mono frames (1 s) and pull them all.
        using var ch = new AudioChannel(Mono48k, bufferDepth: 8);

        ch.Seek(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), ch.Position);

        float[] samples = new float[48000];
        await ch.WriteAsync(samples);

        float[] dest = new float[48000];
        int frames = ch.FillBuffer(dest, 48000);

        Assert.Equal(48000, frames);
        // Position must be seek-point (5 s) + pulled (1 s) = 6 s.
        Assert.Equal(TimeSpan.FromSeconds(6), ch.Position);
    }

    [Fact]
    public async Task Seek_ToNonZero_OnEmptyRing_PositionDoesNotRegress()
    {
        // Seek to 10 s; FillBuffer on an empty ring must not alter position.
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        ch.Seek(TimeSpan.FromSeconds(10));

        float[] dest = new float[4];
        ch.FillBuffer(dest, 4); // underrun — no frames available

        Assert.Equal(TimeSpan.FromSeconds(10), ch.Position);
    }

    [Fact]
    public async Task Seek_WhilePartiallyReadingChunk_NoStaleDataAfterSeek()
    {
        // Write 4 frames, pull 2 (chunk is now partially consumed, offset = 2),
        // then seek and write new data.  The old 2 remaining frames must NOT
        // appear after the seek.
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        await ch.WriteAsync(M(1f, 2f, 3f, 4f)); // 4 frames in one chunk

        float[] half = new float[2];
        ch.FillBuffer(half, 2); // consume first 2; offset is now mid-chunk

        ch.Seek(TimeSpan.FromSeconds(5));

        // Write fresh data with a distinguishable value.
        await ch.WriteAsync(M(9f, 8f));

        float[] dest = new float[2];
        int frames = ch.FillBuffer(dest, 2);

        Assert.Equal(2, frames);
        Assert.Equal(9f, dest[0]); // fresh data, not stale 3f / 4f
        Assert.Equal(8f, dest[1]);
    }

    [Fact]
    public async Task Seek_WhilePartiallyReadingChunk_BufferAvailableIsZeroAfterSeek()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        await ch.WriteAsync(M(1f, 2f, 3f, 4f)); // 4 frames

        float[] half = new float[2];
        ch.FillBuffer(half, 2); // consume 2; 2 remain in partial chunk

        ch.Seek(TimeSpan.Zero);

        Assert.Equal(0, ch.BufferAvailable);
    }

    [Fact]
    public async Task Seek_ClearsRingAndResetsBufferAvailable()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        await ch.WriteAsync(M(1f, 2f, 3f, 4f));
        Assert.Equal(4, ch.BufferAvailable);

        ch.Seek(TimeSpan.Zero);

        Assert.Equal(0, ch.BufferAvailable);

        // Ring is drained — FillBuffer should return 0 (silence).
        float[] dest = new float[2];
        int frames = ch.FillBuffer(dest, 2);
        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task Seek_UpdatesPosition()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        await ch.WriteAsync(M(0f));
        ch.FillBuffer(new float[1], 1); // advance position

        ch.Seek(TimeSpan.FromSeconds(10));

        // Position is initialised from the seek argument.
        Assert.Equal(TimeSpan.FromSeconds(10), ch.Position);
    }

    // ── TryWrite ──────────────────────────────────────────────────────────

    [Fact]
    public void TryWrite_ReturnsFalse_WhenRingFull()
    {
        // bufferDepth=1 means capacity 1 chunk; fill it then attempt a second write.
        using var ch = new AudioChannel(Mono48k, bufferDepth: 1);
        bool first  = ch.TryWrite(new float[] { 1f });
        bool second = ch.TryWrite(new float[] { 2f });

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void TryWrite_IncrementsBufferAvailable()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        ch.TryWrite(new float[] { 1f, 2f, 3f });
        Assert.Equal(3, ch.BufferAvailable);
    }

    // ── Dispose guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_AfterDispose_Throws()
    {
        var ch = new AudioChannel(Mono48k, bufferDepth: 4);
        ch.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await ch.WriteAsync(M(1f)));
    }

    // ── Position ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Position_AdvancesWithPulledFrames()
    {
        using var ch = new AudioChannel(Mono48k, bufferDepth: 8);
        // 48000 frames = 1 second
        float[] samples = new float[48000];
        await ch.WriteAsync(samples);

        float[] dest = new float[48000];
        ch.FillBuffer(dest, 48000);

        // Position should be 1 second.
        Assert.Equal(TimeSpan.FromSeconds(1), ch.Position);
    }
}

