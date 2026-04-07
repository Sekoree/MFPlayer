using System.Buffers;

namespace S.Media.FFmpeg;

/// <summary>
/// Wraps an <see cref="ArrayPool{T}"/> rental as an <see cref="IDisposable"/>.
/// Dispose is idempotent — the array is returned to the pool only once,
/// even if multiple copies of the owning <see cref="S.Media.Core.Media.VideoFrame"/>
/// value are in scope.
/// </summary>
internal sealed class ArrayPoolOwner<T> : IDisposable
{
    private T[]? _array;

    internal ArrayPoolOwner(T[] array) => _array = array;

    public void Dispose()
    {
        var arr = Interlocked.Exchange(ref _array, null);
        if (arr is not null)
            ArrayPool<T>.Shared.Return(arr);
    }
}

