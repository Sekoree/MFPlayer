using System.Runtime.InteropServices;
using PALib.Types.Core;

namespace PALib.CoreAudio;

internal static class PaMacCoreConstants
{
    public const uint paMacCoreChangeDeviceParameters = 0x01;
    public const uint paMacCoreFailIfConversionRequired = 0x02;
    public const uint paMacCoreConversionQualityMin = 0x0100;
    public const uint paMacCoreConversionQualityMedium = 0x0200;
    public const uint paMacCoreConversionQualityLow = 0x0300;
    public const uint paMacCoreConversionQualityHigh = 0x0400;
    public const uint paMacCoreConversionQualityMax = 0x0000;
    public const uint paMacCorePlayNice = 0x00;
    public const uint paMacCorePro = 0x01;
    public const uint paMacCoreMinimizeCPUButPlayNice = 0x0100;
    public const uint paMacCoreMinimizeCPU = 0x0101;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaMacCoreStreamInfo
{
    public nuint size;
    public PaHostApiTypeId hostApiType;
    public nuint version;
    public nuint flags;
    public nint channelMap;
    public nuint channelMapSize;
}
