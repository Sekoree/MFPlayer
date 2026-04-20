using System.Runtime.CompilerServices;

namespace S.Media.Core;

/// <summary>
/// Static helpers for the copy-on-write array pattern used throughout the
/// router layer (<see cref="S.Media.Core.Routing.AVRouter"/>).
/// <para>
/// <b>Pattern:</b> A <c>volatile T[]</c> field is read lock-free on the RT thread.
/// The management thread mutates the array under a lock by creating a new copy with
/// the desired change and assigning it atomically.  These helpers encapsulate the
/// copy mechanics (append, remove, replace, search) to avoid duplicated, error-prone
/// manual array manipulation across ~15 call sites.
/// </para>
/// </summary>
internal static class CopyOnWriteArray
{
    /// <summary>Appends <paramref name="item"/> and returns the new array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] Add<T>(T[] source, T item)
    {
        var neo = new T[source.Length + 1];
        source.CopyTo(neo, 0);
        neo[^1] = item;
        return neo;
    }

    /// <summary>
    /// Removes the element at <paramref name="index"/> and returns the new array.
    /// Returns the original array unchanged if <paramref name="index"/> is out of range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] RemoveAt<T>(T[] source, int index)
    {
        if ((uint)index >= (uint)source.Length) return source;
        var neo = new T[source.Length - 1];
        for (int i = 0, j = 0; i < source.Length; i++)
        {
            if (i != index) neo[j++] = source[i];
        }
        return neo;
    }

    /// <summary>
    /// Finds the first element matching <paramref name="predicate"/> and returns its index,
    /// or -1 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf<T>(T[] source, Func<T, bool> predicate)
    {
        for (int i = 0; i < source.Length; i++)
            if (predicate(source[i])) return i;
        return -1;
    }

    /// <summary>
    /// Replaces the element at <paramref name="index"/> with <paramref name="replacement"/>
    /// and returns the new array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] ReplaceAt<T>(T[] source, int index, T replacement)
    {
        var neo = new T[source.Length];
        source.CopyTo(neo, 0);
        neo[index] = replacement;
        return neo;
    }
}

