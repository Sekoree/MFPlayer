using S.Media.Core.Errors;

namespace S.Media.NDI.Config;

public sealed record NDIOutputOptions
{
    public bool EnableVideo { get; init; } = true;

    public bool EnableAudio { get; init; }

    public bool ValidateCapabilitiesOnStart { get; init; } = true;

    public bool RequireAudioPathOnStart { get; init; }

    public NDIVideoSendFormat? SendFormatOverride { get; init; }

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
