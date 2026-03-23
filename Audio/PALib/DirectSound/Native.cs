using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.DirectSound;

public static class Native
{
    private const string Category = "PALib.DirectSound";
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public static PaError TraceStreamInfo(in PaWinDirectSoundStreamInfo info)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        var logger = PALibLogging.GetLogger(Category);
        PALibLogging.TraceCall(logger, nameof(TraceStreamInfo),
            (nameof(info.size), info.size),
            (nameof(info.hostApiType), info.hostApiType),
            (nameof(info.version), info.version),
            (nameof(info.flags), info.flags),
            (nameof(info.framesPerBuffer), info.framesPerBuffer),
            (nameof(info.channelMask), info.channelMask));

        return PaError.paNoError;
    }
}
