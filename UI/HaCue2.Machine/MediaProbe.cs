using S.Media.Decode.FFmpeg;

namespace HaCue2.Machine;

/// <summary>What a media file turned out to contain.</summary>
/// <param name="Duration">Null when the container does not declare one — a live source, or a stream.</param>
public readonly record struct MediaFacts(
    TimeSpan? Duration,
    bool HasAudio,
    bool HasVideo,
    int AudioChannels,
    int VideoWidth,
    int VideoHeight,
    double FramesPerSecond)
{
    /// <summary>What is known about a file nobody could open. Every field is deliberately blank.</summary>
    public static MediaFacts Unknown => default;
}

/// <summary>
/// Opens a media file and reports what is in it.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between a cue list that says "—" in every Len column and one that tells the
/// operator how long the show is. A duration is a MACHINE fact, not a document one: two machines with
/// different copies of the same file can legitimately disagree, so it is never written into the
/// project — it is asked for, cached, and shown.
/// </para>
/// <para>
/// A failure is <see cref="MediaFacts.Unknown"/>, never an exception and never a guess. An unreadable
/// file is a thing the status pass reports; it must not take a view down with it.
/// </para>
/// </remarks>
public static class MediaProbe
{
    /// <summary>Probes one file. Returns <see cref="MediaFacts.Unknown"/> for anything unreadable.</summary>
    public static async Task<MediaFacts> ProbeAsync(string path, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return MediaFacts.Unknown;

        try
        {
            // Off the calling thread: opening a container touches the disk, and on a network mount or
            // a sleeping drive that is seconds, not milliseconds.
            return await Task.Run(() => Read(path), cancellation).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return MediaFacts.Unknown;
        }
    }

    private static MediaFacts Read(string path)
    {
        var decoder = MediaContainerDecoder.Open(path);

        try
        {
            var frameRate = 0d;
            var width = 0;
            var height = 0;

            if (decoder.HasVideo)
            {
                var format = decoder.Video.Format;
                width = Math.Max(0, format.Width);
                height = Math.Max(0, format.Height);

                if (format.FrameRate is { Numerator: > 0, Denominator: > 0 } rate)
                    frameRate = (double)rate.Numerator / rate.Denominator;
            }

            return new MediaFacts(
                // Zero means "the container did not say", which is not the same as a zero-length file.
                Duration: decoder.Duration > TimeSpan.Zero ? decoder.Duration : null,
                HasAudio: decoder.HasAudio,
                HasVideo: decoder.HasVideo,
                AudioChannels: decoder.HasAudio ? Math.Max(0, decoder.Audio.Format.Channels) : 0,
                VideoWidth: width,
                VideoHeight: height,
                FramesPerSecond: frameRate);
        }
        finally
        {
            decoder.Dispose();
        }
    }
}
