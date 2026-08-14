using S.Media.FFmpeg.Common;
using Xunit;

namespace S.Media.Stream.Http.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <em>skips</em> (rather than fails) when the FFmpeg natives are missing
/// or the wrong ABI major.
/// </summary>
/// <remarks>
/// Every live-stream test here goes live through an encode session, so each one dies at
/// <c>LiveStreamOptions.Validate</c> on a runner whose FFmpeg does not match the bindings - eleven copies of
/// one environment fault, none of them about HTTP, mounts or the carrier. Mirrors the attribute of the same
/// name in <c>S.Media.Decode.FFmpeg.Tests</c> and <c>S.Media.Encode.FFmpeg.Tests</c>.
/// </remarks>
public sealed class FFmpegNativeFactAttribute : FactAttribute
{
    public FFmpegNativeFactAttribute()
    {
        if (FFmpegRuntime.UnavailableReason is { } reason)
            Skip = reason;
    }
}
