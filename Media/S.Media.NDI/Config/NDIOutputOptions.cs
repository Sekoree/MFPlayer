using S.Media.Core.Errors;

namespace S.Media.NDI.Config;

public sealed record NDIOutputOptions
{
    public bool EnableVideo { get; init; } = true;

    public bool EnableAudio { get; init; }


    public bool RequireAudioPathOnStart { get; init; }

    public NDIVideoSendFormat? SendFormatOverride { get; init; }

    /// <summary>
    /// When true, NDIlib will use its own clock to pace video output at exactly the declared frame rate.
    /// Use false (default) when the caller is already managing frame timing.
    /// </summary>
    public bool ClockVideo { get; init; }

    /// <summary>
    /// When true, NDIlib will use its own clock to pace audio output.
    /// Use false (default) when the caller is already managing audio timing.
    /// </summary>
    public bool ClockAudio { get; init; }

    /// <summary>
    /// Numerator for the declared video frame rate sent in the NDI stream (default: 30000 → 29.97 fps with FrameRateD=1001).
    /// </summary>
    public int FrameRateN { get; init; } = 30000;

    /// <summary>
    /// Denominator for the declared video frame rate (default: 1001 → 29.97 fps with FrameRateN=30000).
    /// </summary>
    public int FrameRateD { get; init; } = 1001;

    public int Validate()
    {
        if (!EnableVideo && !EnableAudio)
        {
            return (int)MediaErrorCode.NDIInvalidOutputOptions;
        }

        if (RequireAudioPathOnStart && !EnableAudio)
        {
            return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
        }

        return MediaResult.Success;
    }
}
