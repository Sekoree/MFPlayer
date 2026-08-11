namespace S.Media.FFmpeg.Common;

/// <summary>
/// Queries a codec's supported configuration lists (pixel formats, sample formats, sample rates, …).
/// </summary>
/// <remarks>
/// FFmpeg 9 removed the <c>AVCodec</c> capability arrays (<c>pix_fmts</c>, <c>sample_fmts</c>,
/// <c>supported_samplerates</c>, <c>supported_framerates</c>, <c>ch_layouts</c>) that were deprecated in 7.1;
/// <c>avcodec_get_supported_config</c> replaces all of them. The context argument is optional, so this works
/// on a bare <see cref="AVCodec"/> before any context is opened.
/// </remarks>
internal static unsafe class FfmpegCodecConfig
{
    /// <summary>
    /// Reads one configuration list for <paramref name="codec"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the codec places no restriction on <paramref name="config"/> (libav reports
    /// a null list, meaning every value is accepted), in which case <paramref name="values"/> is empty.
    /// Otherwise <see langword="true"/> and <paramref name="values"/> holds the supported values, without the
    /// list terminator.
    /// </returns>
    internal static bool TryGetSupportedConfig<T>(AVCodec* codec, AVCodecConfig config, out ReadOnlySpan<T> values)
        where T : unmanaged
    {
        void* configs = null;
        var count = 0;
        var ret = avcodec_get_supported_config(null, codec, config, 0, &configs, &count);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_get_supported_config));

        if (configs is null)
        {
            values = default;
            return false;
        }

        values = new ReadOnlySpan<T>(configs, count);
        return true;
    }
}
