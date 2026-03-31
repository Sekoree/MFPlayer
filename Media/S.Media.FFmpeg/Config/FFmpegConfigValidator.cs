using S.Media.Core.Errors;
using S.Media.FFmpeg.Audio;

namespace S.Media.FFmpeg.Config;

public static class FFmpegConfigValidator
{
    public static int Validate(FFmpegOpenOptions openOptions, FFmpegAudioSourceOptions? audioOptions = null)
    {
        return Validate(openOptions, decodeOptions: null, audioOptions);
    }

    public static int Validate(
        FFmpegOpenOptions openOptions,
        FFmpegDecodeOptions? decodeOptions,
        FFmpegAudioSourceOptions? audioOptions = null)
    {
        ArgumentNullException.ThrowIfNull(openOptions);

        var normalizedDecodeOptions = (decodeOptions ?? new FFmpegDecodeOptions()).Normalize();
        if (normalizedDecodeOptions.Validate() != MediaResult.Success)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (!openOptions.OpenAudio && !openOptions.OpenVideo)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        // UseSharedDecodeContext = false is not yet implemented: sources would be constructed
        // with a null session and silently return stub data. Reject until a non-shared decode
        // path exists.
        if (!openOptions.UseSharedDecodeContext)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (!openOptions.OpenAudio && openOptions.AudioStreamIndex.HasValue)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (openOptions.AudioStreamIndex is < 0)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (!openOptions.OpenVideo && openOptions.VideoStreamIndex.HasValue)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (openOptions.VideoStreamIndex is < 0)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        var hasUri = !string.IsNullOrWhiteSpace(openOptions.InputUri);
        var hasStream = openOptions.InputStream is not null;

        if (hasUri == hasStream)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (hasStream && openOptions.InputStream is not { CanRead: true })
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (hasUri && !openOptions.LeaveInputStreamOpen)
        {
            return (int)MediaErrorCode.FFmpegInvalidConfig;
        }

        if (audioOptions is not null &&
            audioOptions.MappingPolicy == FFmpegAudioChannelMappingPolicy.ApplyExplicitRouteMap &&
            audioOptions.ExplicitChannelMap is null)
        {
            return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
        }

        if (audioOptions?.ExplicitChannelMap is { } channelMap)
        {
            var code = channelMap.Validate(out _);
            if (code != MediaResult.Success)
            {
                return code;
            }
        }

        return MediaResult.Success;
    }
}
