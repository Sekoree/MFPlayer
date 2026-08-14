using System.Security.Cryptography;
using System.Text;

namespace HaPlay.Models;

/// <summary>Stable content hash of a <see cref="HaPlayProject"/> (its serialized JSON). Used to detect
/// unsaved changes without a central dirty flag - the shell compares the current snapshot's hash against a
/// baseline captured at the last New / Open / Save.</summary>
public static class ProjectHash
{
    public static string Of(HaPlayProject project) =>
        OfSerializedJson(ProjectIO.Serialize(project));

    /// <summary>
    /// F-12: pays the serialize+hash path's first-use cost (serializer construction + JIT, measured
    /// ~200 ms) somewhere it does not matter. The first REAL hash is MainViewModel's clean-baseline
    /// capture, on the UI thread, inside the startup critical path - warming on a background thread
    /// during app init overlaps that cost with XAML load instead (the second call measured 0.15 ms).
    /// Safe from any thread: touches only a throwaway default project, never UI state.
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            Of(new HaPlayProject());
        }
        catch (Exception)
        {
            // Warmup must never break startup - the real call simply pays the cost instead.
        }
    }

    /// <summary>Hashes an already-serialized project without serializing it a second time.</summary>
    public static string OfSerializedJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, json);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Feeds UTF-8 incrementally so large projects/scripts do not allocate a second full-size byte array.</summary>
    internal static void AppendUtf8(IncrementalHash hash, string value)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(value);

        var encoder = Encoding.UTF8.GetEncoder();
        var remaining = value.AsSpan();
        Span<byte> buffer = stackalloc byte[4096];
        do
        {
            encoder.Convert(
                remaining,
                buffer,
                flush: true,
                out var charsUsed,
                out var bytesUsed,
                out var completed);
            if (bytesUsed > 0)
                hash.AppendData(buffer[..bytesUsed]);
            remaining = remaining[charsUsed..];
            if (completed)
                break;
        }
        while (true);
    }
}
