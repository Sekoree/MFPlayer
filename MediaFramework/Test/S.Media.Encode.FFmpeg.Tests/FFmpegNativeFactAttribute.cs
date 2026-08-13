using S.Media.FFmpeg.Common;
using Xunit;

namespace S.Media.Encode.FFmpeg.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <em>skips</em> (rather than fails) when the FFmpeg natives are missing
/// or the wrong ABI major.
/// </summary>
/// <remarks>
/// The same policy <c>S.Media.Decode.FFmpeg.Tests</c> has had all along, arriving here the day it was needed:
/// a runner staged with avcodec-62 against avcodec-63 bindings turned every encoder test in this file red,
/// and twenty identical "FFmpeg native libraries are not loadable" failures say nothing about the encoders
/// they were meant to be testing. An environment that cannot load FFmpeg has no verdict to give on encoding.
///
/// The reason comes from <see cref="FFmpegRuntime.UnavailableReason"/> rather than a probe of its own — that
/// property already names the required and the found sonames, initializes itself, and never throws.
/// </remarks>
public sealed class FFmpegNativeFactAttribute : FactAttribute
{
    public FFmpegNativeFactAttribute()
    {
        if (FFmpegRuntime.UnavailableReason is { } reason)
            Skip = reason;
    }
}

/// <summary><see cref="TheoryAttribute"/> counterpart of <see cref="FFmpegNativeFactAttribute"/>.</summary>
public sealed class FFmpegNativeTheoryAttribute : TheoryAttribute
{
    public FFmpegNativeTheoryAttribute()
    {
        if (FFmpegRuntime.UnavailableReason is { } reason)
            Skip = reason;
    }
}
