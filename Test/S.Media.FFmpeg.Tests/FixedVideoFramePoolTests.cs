using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FixedVideoFramePoolTests
{
    [Fact]
    public void RentReturn_SameSize_ReusesBufferInstance()
    {
        using var pool = new FixedVideoFramePool(maxRetained: 4);

        var first = pool.Rent(120_000);
        pool.Return(first);
        var second = pool.Rent(120_000);

        Assert.Same(first, second);
    }

    [Fact]
    public void Resize_DropsOldSizeBuffers()
    {
        using var pool = new FixedVideoFramePool(maxRetained: 4);

        var oldSize = pool.Rent(120_000);
        pool.Return(oldSize);

        var newSize = pool.Rent(128_000);
        Assert.NotSame(oldSize, newSize);
        Assert.Equal(128_000, newSize.Length);
    }

    [Fact]
    public void Owner_Dispose_ReturnsBufferToPool()
    {
        using var pool = new FixedVideoFramePool(maxRetained: 2);
        var rented = pool.Rent(96_000);

        var owner = new FixedVideoFrameOwner(pool, rented);
        owner.Dispose();

        var again = pool.Rent(96_000);
        Assert.Same(rented, again);
    }
}
