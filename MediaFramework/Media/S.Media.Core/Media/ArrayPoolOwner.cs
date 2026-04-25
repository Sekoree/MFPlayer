using System.Buffers;

namespace S.Media.Core.Media;

/// <summary>
/// Wraps an <see cref="ArrayPool{T}"/> rental as an <see cref="IDisposable"/>.
/// Dispose is idempotent — the array is returned to the pool exactly once,
/// even if the owning <see cref="VideoFrame"/> value has been copied.
/// </summary>
public sealed class ArrayPoolOwner<T> : IDisposable
{
    private T[]? _array;

    public ArrayPoolOwner(T[] array) => _array = array;

    public void Dispose()
    {
        var arr = Interlocked.Exchange(ref _array, null);
        if (arr is not null)
            ArrayPool<T>.Shared.Return(arr);
    }
}

