using System.Runtime.InteropServices;
using PALib.Types.Core;

namespace PALib.ASIO;

public static class PaAsioConstants
{
    public const uint paAsioUseChannelSelectors = 0x01;
}

[StructLayout(LayoutKind.Sequential)]
public struct PaAsioStreamInfo
{
    public nuint size;
    public PaHostApiTypeId hostApiType;
    public nuint version;
    public nuint flags;
    public nint channelSelectors;
}
