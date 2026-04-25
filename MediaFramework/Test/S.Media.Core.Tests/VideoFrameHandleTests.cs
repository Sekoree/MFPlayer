using System.Buffers;
using S.Media.Core.Media;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Unit tests for <see cref="VideoFrameHandle"/> ref-counted fan-out (§3.11 /
/// B15+B16+R18+CH7). Verifies Retain/Release symmetry, legacy-frame fallback,
/// and reference-identity equality used by endpoints' texture-reuse gates.
/// </summary>
public class VideoFrameHandleTests
{
    private static VideoFrame MakeRefCountedFrame(out RefCountedVideoBuffer owner, int size = 16)
    {
        var rented = ArrayPool<byte>.Shared.Rent(size);
        owner = new RefCountedVideoBuffer(new ArrayPoolOwner<byte>(rented));
        return new VideoFrame(
            Width: 2, Height: 2, PixelFormat: PixelFormat.Bgra32,
            Data: rented.AsMemory(0, size),
            Pts: TimeSpan.Zero,
            MemoryOwner: owner);
    }

    [Fact]
    public void Handle_ForwardsFrameAccessors()
    {
        var frame = MakeRefCountedFrame(out var owner);
        var handle = new VideoFrameHandle(in frame);

        Assert.Equal(frame.Width,       handle.Width);
        Assert.Equal(frame.Height,      handle.Height);
        Assert.Equal(frame.PixelFormat, handle.PixelFormat);
        Assert.Equal(frame.Pts,         handle.Pts);
        Assert.True(handle.IsValid);
        Assert.Same(owner, handle.RefBuffer);

        // Producer's initial ref still held — release it so the rental returns to the pool.
        handle.Release();
        Assert.Equal(0, owner.RefCount);
    }

    [Fact]
    public void Handle_Retain_IncrementsRefCount_Release_Decrements()
    {
        var frame = MakeRefCountedFrame(out var owner);
        var handle = new VideoFrameHandle(in frame);

        Assert.Equal(1, owner.RefCount);
        handle.Retain();
        Assert.Equal(2, owner.RefCount);
        handle.Retain();
        Assert.Equal(3, owner.RefCount);

        handle.Release();
        handle.Release();
        Assert.Equal(1, owner.RefCount);
        handle.Release();
        Assert.Equal(0, owner.RefCount);
    }

    [Fact]
    public void Handle_Retain_OnNonRefCountedFrame_Throws()
    {
        // Raw IDisposable owner, not a RefCountedVideoBuffer → Retain must throw.
        var disposed = false;
        var rawOwner = new DelegateDisposable(() => disposed = true);
        var frame = new VideoFrame(2, 2, PixelFormat.Bgra32,
            ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, rawOwner);
        var handle = new VideoFrameHandle(in frame);

        Assert.Null(handle.RefBuffer);
        Assert.Throws<InvalidOperationException>(() => handle.Retain());

        // Release on a non-ref-counted handle falls back to disposing the raw owner.
        handle.Release();
        Assert.True(disposed);
    }

    [Fact]
    public void Handle_Release_OnNullOwner_IsNoOp()
    {
        var frame = new VideoFrame(2, 2, PixelFormat.Bgra32,
            ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, null);
        var handle = new VideoFrameHandle(in frame);

        // Must not throw.
        handle.Release();
    }

    [Fact]
    public void Handle_Equality_IsReferenceBased_OnRefBuffer()
    {
        var frameA = MakeRefCountedFrame(out var ownerA);
        var frameB = MakeRefCountedFrame(out var ownerB);

        var a1 = new VideoFrameHandle(in frameA);
        var a2 = new VideoFrameHandle(in frameA); // wraps same owner
        var b  = new VideoFrameHandle(in frameB);

        Assert.True(a1 == a2);
        Assert.False(a1 == b);
        Assert.NotEqual(a1.GetHashCode(), b.GetHashCode()); // probabilistic but fine for distinct refs

        a1.Release(); // owner A still has its producer ref consumed
        b.Release();
        Assert.Equal(0, ownerA.RefCount);
        Assert.Equal(0, ownerB.RefCount);
    }

    [Fact]
    public void Handle_FanOut_NEndpoints_ReleasesWhenAllDone()
    {
        const int fanOut = 4;
        var frame = MakeRefCountedFrame(out var owner);
        // Simulate producer publishing to N subscribers: each retains one ref.
        for (int i = 1; i < fanOut; i++) owner.Retain();
        Assert.Equal(fanOut, owner.RefCount);

        // Each "endpoint" receives a handle copy and releases after use.
        for (int i = 0; i < fanOut; i++)
        {
            var h = new VideoFrameHandle(in frame);
            // Endpoint does its synchronous copy / upload here.
            h.Release();
        }

        Assert.Equal(0, owner.RefCount);
    }

    [Fact]
    public void Handle_DefaultInstance_IsInvalid()
    {
        VideoFrameHandle d = default;
        Assert.False(d.IsValid);
        Assert.Null(d.RefBuffer);
        // Release on default: falls into MemoryOwner?.Dispose() which is null — no throw.
        d.Release();
    }

    private sealed class DelegateDisposable(Action onDispose) : IDisposable
    {
        private readonly Action _onDispose = onDispose;
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _onDispose();
        }
    }
}

