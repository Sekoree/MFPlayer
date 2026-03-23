using System.Runtime.InteropServices;

namespace NdiLib;

public enum NdiFrameType : int
{
    None = 0,
    Video = 1,
    Audio = 2,
    Metadata = 3,
    Error = 4,
    StatusChange = 100,
    SourceChange = 101,
    Max = 0x7fffffff
}

public enum NdiRecvBandwidth : int
{
    MetadataOnly = -10,
    AudioOnly = 10,
    Lowest = 0,
    Highest = 100,
    Max = 0x7fffffff
}

public enum NdiRecvColorFormat : int
{
    BgrxBgra = 0,
    UyvyBgra = 1,
    RgbxRgba = 2,
    UyvyRgba = 3,
    Fastest = 100,
    Best = 101,
    Max = 0x7fffffff
}

public enum NdiFrameFormatType : int
{
    Interleaved = 0,
    Progressive = 1,
    Field0 = 2,
    Field1 = 3,
    Max = 0x7fffffff
}

public enum NdiFourCCVideoType : uint
{
    Uyvy = ((uint)'U') | ((uint)'Y' << 8) | ((uint)'V' << 16) | ((uint)'Y' << 24),
    Uyva = ((uint)'U') | ((uint)'Y' << 8) | ((uint)'V' << 16) | ((uint)'A' << 24),
    P216 = ((uint)'P') | ((uint)'2' << 8) | ((uint)'1' << 16) | ((uint)'6' << 24),
    Pa16 = ((uint)'P') | ((uint)'A' << 8) | ((uint)'1' << 16) | ((uint)'6' << 24),
    Yv12 = ((uint)'Y') | ((uint)'V' << 8) | ((uint)'1' << 16) | ((uint)'2' << 24),
    I420 = ((uint)'I') | ((uint)'4' << 8) | ((uint)'2' << 16) | ((uint)'0' << 24),
    Nv12 = ((uint)'N') | ((uint)'V' << 8) | ((uint)'1' << 16) | ((uint)'2' << 24),
    Bgra = ((uint)'B') | ((uint)'G' << 8) | ((uint)'R' << 16) | ((uint)'A' << 24),
    Bgrx = ((uint)'B') | ((uint)'G' << 8) | ((uint)'R' << 16) | ((uint)'X' << 24),
    Rgba = ((uint)'R') | ((uint)'G' << 8) | ((uint)'B' << 16) | ((uint)'A' << 24),
    Rgbx = ((uint)'R') | ((uint)'G' << 8) | ((uint)'B' << 16) | ((uint)'X' << 24)
}

public enum NdiFourCCAudioType : int
{
    Fltp = ((int)'F') | ((int)'L' << 8) | ((int)'T' << 16) | ((int)'p' << 24),
    Max = 0x7fffffff
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiFindCreate
{
    public byte ShowLocalSources;
    public nint PGroups;
    public nint PExtraIps;

    public static NdiFindCreate CreateDefault()
    {
        return new NdiFindCreate
        {
            ShowLocalSources = 1,
            PGroups = nint.Zero,
            PExtraIps = nint.Zero
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiSource
{
    public nint PNdiName;
    public nint PUrlAddress;

    public readonly string? NdiName => Marshal.PtrToStringUTF8(PNdiName);
    public readonly string? UrlAddress => Marshal.PtrToStringUTF8(PUrlAddress);
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiRecvCreateV3
{
    public NdiSource SourceToConnectTo;
    public NdiRecvColorFormat ColorFormat;
    public NdiRecvBandwidth Bandwidth;
    public byte AllowVideoFields;
    public nint PNdiRecvName;
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiSendCreate
{
    public nint PNdiName;
    public nint PGroups;
    public byte ClockVideo;
    public byte ClockAudio;
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiVideoFrameV2
{
    public int Xres;
    public int Yres;
    public NdiFourCCVideoType FourCC;
    public int FrameRateN;
    public int FrameRateD;
    public float PictureAspectRatio;
    public NdiFrameFormatType FrameFormatType;
    public long Timecode;
    public nint PData;
    public int LineStrideInBytes;
    public nint PMetadata;
    public long Timestamp;

    public readonly string? Metadata => Marshal.PtrToStringUTF8(PMetadata);
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiAudioFrameV3
{
    public int SampleRate;
    public int NoChannels;
    public int NoSamples;
    public long Timecode;
    public NdiFourCCAudioType FourCC;
    public nint PData;
    public int ChannelStrideInBytes;
    public nint PMetadata;
    public long Timestamp;

    public readonly string? Metadata => Marshal.PtrToStringUTF8(PMetadata);
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiMetadataFrame
{
    public int Length;
    public long Timecode;
    public nint PData;

    public readonly string? Data => Marshal.PtrToStringUTF8(PData);
}

public readonly record struct NdiDiscoveredSource(string Name, string? UrlAddress);

