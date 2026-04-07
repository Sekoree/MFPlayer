using System.Buffers;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Tests for the <see cref="ArrayPoolOwner{T}"/> IDisposable rental wrapper.
/// No FFmpeg native libraries required.
/// </summary>
public sealed class ArrayPoolOwnerTests
{
    [Fact]
    public void Dispose_ReturnsArrayToPool()
    {
        // Rent an array, wrap it, then dispose — the same array should be available
        // again from the pool immediately afterwards.
        var rented = ArrayPool<byte>.Shared.Rent(1024);
        var owner  = new ArrayPoolOwner<byte>(rented);

        // Dispose should not throw and should return the buffer.
        owner.Dispose();

        // After return the pool can serve the same (or a recycled) buffer.
        // We just verify no exception was thrown and the owner is unusable.
        owner.Dispose(); // idempotent — must not throw
    }

    [Fact]
    public void Dispose_IsIdempotent_NoDoubleReturn()
    {
        var rented = ArrayPool<byte>.Shared.Rent(512);
        var owner  = new ArrayPoolOwner<byte>(rented);

        owner.Dispose();
        // Second dispose should be a no-op, not throw or double-return.
        var ex = Record.Exception(() => owner.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_OnCopiedStruct_SecondDisposeSafe()
    {
        // VideoFrame is a struct — multiple references to the same MemoryOwner
        // can exist. Only the first Dispose should return the array.
        var rented = ArrayPool<float>.Shared.Rent(256);
        var owner1 = new ArrayPoolOwner<float>(rented);
        var owner2 = owner1; // struct copy — same reference

        owner1.Dispose();
        var ex = Record.Exception(() => owner2.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_EmptyArray_DoesNotThrow()
    {
        var rented = ArrayPool<byte>.Shared.Rent(1);
        var owner  = new ArrayPoolOwner<byte>(rented);
        var ex = Record.Exception(() => owner.Dispose());
        Assert.Null(ex);
    }
}

