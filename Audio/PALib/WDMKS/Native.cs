using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.WDMKS;

public static class Native
{
    private const string Category = "PALib.WDMKS";
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public static PaError TraceInfo(in PaWinWDMKSInfo info)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        var logger = PALibLogging.GetLogger(Category);
        PALibLogging.TraceCall(logger, nameof(TraceInfo),
            (nameof(info.size), info.size),
            (nameof(info.hostApiType), info.hostApiType),
            (nameof(info.version), info.version),
            (nameof(info.flags), info.flags),
            (nameof(info.noOfPackets), info.noOfPackets),
            (nameof(info.channelMask), info.channelMask));

        return PaError.paNoError;
    }
}
