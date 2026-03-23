#pragma warning disable IDE1006

using System.Runtime.InteropServices;
using PALib.Types.Core;

namespace PALib.WDMKS;

[Flags]
public enum PaWinWDMKSFlags : uint
{
    paWinWDMKSOverrideFramesize = 1 << 0,
    paWinWDMKSUseGivenChannelMask = 1 << 1
}

public enum PaWDMKSType
{
    Type_kNotUsed,
    Type_kWaveCyclic,
    Type_kWaveRT,
    Type_kCnt
}

public enum PaWDMKSSubType
{
    SubType_kUnknown,
    SubType_kNotification,
    SubType_kPolled,
    SubType_kCnt
}

[StructLayout(LayoutKind.Sequential)]
public struct PaWinWDMKSInfo
{
    public nuint size;
    public PaHostApiTypeId hostApiType;
    public nuint version;
    public nuint flags;
    public uint noOfPackets;
    public uint channelMask;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PaWinWDMKSDeviceInfo
{
    public const int MaxPath = 260;
    public fixed char filterPath[MaxPath];
    public fixed char topologyPath[MaxPath];
    public PaWDMKSType streamingType;
    public Guid deviceProductGuid;
}

[StructLayout(LayoutKind.Sequential)]
public struct PaWDMKSDirectionSpecificStreamInfo
{
    public int device;
    public uint channels;
    public uint framesPerHostBuffer;
    public int endpointPinId;
    public int muxNodeId;
    public PaWDMKSSubType streamingSubType;
}

[StructLayout(LayoutKind.Sequential)]
public struct PaWDMKSSpecificStreamInfo
{
    public PaWDMKSDirectionSpecificStreamInfo input;
    public PaWDMKSDirectionSpecificStreamInfo output;
}

#pragma warning restore IDE1006
