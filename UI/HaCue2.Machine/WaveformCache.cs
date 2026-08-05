using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HaCue2.Machine;

/// <summary>
/// Peaks kept on disk, so a long file is scanned once rather than once per window.
/// </summary>
/// <remarks>
/// <para>
/// The cache root already had a <c>waveforms</c> folder and the settings pane already reported and
/// cleared it — this is what puts something in it.
/// </para>
/// <para>
/// <b>Keyed by what the file IS, not by where it is.</b> Path, length and last-write time together:
/// a re-encoded file at the same path must not read back the old shape, and the same file reached
/// through a different mount must not be scanned twice. Re-deriving is always safe, so a miss costs
/// time and never correctness.
/// </para>
/// </remarks>
public static class WaveformCache
{
    /// <summary>A tiny header, so a file from a future layout is discarded rather than misread.</summary>
    private const int Version = 1;

    /// <summary>Reads a cached scan, or null when there is not one.</summary>
    public static float[]? Read(string cacheRoot, string mediaPath)
    {
        try
        {
            var file = FileFor(cacheRoot, mediaPath);

            if (file is null || !File.Exists(file))
                return null;

            var bytes = File.ReadAllBytes(file);

            if (bytes.Length < 8 || BitConverter.ToInt32(bytes, 0) != Version)
                return null;

            var count = BitConverter.ToInt32(bytes, 4);

            if (count <= 0 || bytes.Length < 8 + (count * 4))
                return null;

            var peaks = new float[count];
            Buffer.BlockCopy(bytes, 8, peaks, 0, count * 4);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            return peaks;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be read is a cache miss, never an error: the peaks are re-derivable
            // and a trim editor must not fail to open because a temp folder is unwritable.
            return null;
        }
    }

    /// <summary>Stores a scan. Silent on failure, for the same reason a read is.</summary>
    public static void Write(string cacheRoot, string mediaPath, float[] peaks, long? maxCacheBytes = null)
    {
        ArgumentNullException.ThrowIfNull(peaks);

        try
        {
            var file = FileFor(cacheRoot, mediaPath);

            if (file is null || peaks.Length == 0)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var bytes = new byte[8 + (peaks.Length * 4)];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), Version);
            BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), peaks.Length);
            Buffer.BlockCopy(peaks, 0, bytes, 8, peaks.Length * 4);

            // Written beside and moved, so a cancelled write never leaves a half file that would read
            // back as a truncated waveform.
            var temporary = file + ".part";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, file, overwrite: true);
            MediaCache.EnforceBudget(cacheRoot, "waveforms", maxCacheBytes, file);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Nothing to do and nothing to say: the scan still happened and the editor still has it.
        }
    }

    /// <summary>Where a file's peaks live, or null when the media cannot be identified.</summary>
    private static string? FileFor(string cacheRoot, string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || string.IsNullOrWhiteSpace(mediaPath))
            return null;

        FileInfo info;

        try
        {
            info = new FileInfo(mediaPath);

            if (!info.Exists)
                return null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

        return Path.Combine(cacheRoot, "waveforms", hash[..2], hash + ".peaks");
    }
}
